using System.Collections.Generic;
using AnvilAndHammerAI.Detection;
using AnvilAndHammerAI.Morale;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Formations
{
    /// <summary>军团级战场计划(空间脑输出)。</summary>
    public sealed class BattlePlan
    {
        public Formation Target;     // 突破口(schwerpunkt)敌编队
        public Vec2 FlankPoint;      // 开放弧落点(世界 xy);包抄步兵 + 重骑(锤)走它
        public bool HasTarget;
        public bool ReleaseHammer;   // 突破口的震慑池已逼近其崩溃阈值 → 放重骑
        public bool BattleJoined;    // 砧(主步兵)已咬住敌步兵(所有绕后/绕侧的前置门)
        public float TargetPoolFrac; // 突破口"距崩溃"比例(池/崩溃阈,1.0=崩);诊断用
        public Vec2 LeftCavPoint;    // 左轻骑绕击落点:突破口**左翼**接近点(双重包抄:左右轻骑分走对侧,不挤同一处)
        public Vec2 RightCavPoint;   // 右轻骑绕击落点:突破口**右翼**接近点
        public Formation ThreatCav;  // 威胁我方步兵的敌近战骑兵编队(供轻骑掩护拦截);无则 null
        public bool CoverInfantry;   // 砧接战后:敌骑威胁我方步兵 → 轻骑改为掩护(拦截)而非绕击
        public bool Pursue;          // 敌方已崩(绝大多数在溃逃)→ 全线追击残敌(团队级,DriveTeam 提前进入追击分支)
        public bool LightCavPursue;  // 敌军已有相当比例在溃逃(达 LightCavPursueFraction,约一支编队崩了)→ **轻骑随时脱离追残兵**,主力仍照常作战
        public Formation AnvilTarget; // 主步兵(砧)正面对手 = 最近敌步兵(无则最近任意敌编队);用于接敌宽度匹配
    }

    /// <summary>
    /// 空间脑(ADR-0011 第 3 步,完整版:定向编队 + 弧覆盖)。
    /// 把每个敌编队建成"有朝向实体",对其 后/左翼/右翼 三接近点做覆盖判定(CoverRange 内有其它敌编队 = 封闭),
    /// 选"有开放弧 + 最虚弱"的敌编队为突破口,开放弧落点为绕击点(后翼优先;侧翼取离我骑兵近者)。
    ///
    /// **放锤判据 = 直接读突破口的震慑池 vs 它自己的崩溃阈值**:
    ///   池 ≥ <see cref="HammerReleaseFraction"/> × 该编队崩溃阈值(= PoolRoutThreshold × tier抗性,与士气系统同一公式)。
    /// 即"敌军快到崩溃阈值时"重骑下场——冲锋本身是震慑压力源,正好把它推过阈触发整队溃逃 + 追击。
    /// 震慑池来源 = 编队级士气系统所写的共享 <see cref="FormationShockPool"/>(对双方恒生效),故任何显著敌编队经首拍结算后都有池;
    /// <see cref="FormationShockPool.TryGet"/> 仅防开局头一拍竞态(此时不放锤,无碍)。虚弱度(选目标用)= 距崩溃比 + 减员 + 低士气 + 溃逃比。
    ///
    /// 注:**所有绕后/绕侧(包抄步兵 + 轻/重骑放锤)还须先满足"砧(主步兵)已咬住敌步兵"**(见 <see cref="BattlePlan.BattleJoined"/>),
    /// 该门控由中央调度器施加;空间脑只负责"选哪个突破口、绕到何处、是否够崩"。
    /// </summary>
    public static class TacticalBrain
    {
        private const float JoinDistance = 15f;
        private const float CoverRange = 28f;            // 接近点 CoverRange 内有其它敌编队 → 该弧被掩护
        private const float HammerReleaseFraction = 0.75f; // 池达崩溃阈的此比例 = "快到崩溃" → 放锤
        private const float SignificantFraction = 0.15f; // "显著"敌编队 = 兵力值 ≥ 此比例 × 最大敌编队兵力值(相对基准,替代硬编码人数)
        // 轻骑掩护判定:敌近战骑兵威胁我方步兵的口径。一律用统一**兵力值**(FormationStrength,tier 加权)作相对比值,兵力值大才算威胁。
        private const float CavThreatRange = 60f;  // 敌骑在我方步兵此距离内才纳入威胁判定(距离,非兵力)
        private const float CavThreatRatio = 0.2f; // 敌骑兵力值 / 我方受威胁编队兵力值 ≤ 此 = 小→无威胁(绕击);超过 = 大→威胁(掩护)
        private const float PursueRoutingFraction = 0.8f; // 活敌中溃逃比 ≥ 此 → 敌军已崩,转全线追击(团队级)
        private const float LightCavPursueFraction = 0.2f; // 活敌中溃逃比 ≥ 此(约一支编队崩了)→ 轻骑随时脱离追残兵(主力不变)

        public static BattlePlan Plan(Team myTeam, Mission m, FormationShockPool pool, FormationScanner scanner,
            float baseRoutThreshold)
        {
            var plan = new BattlePlan();
            if (myTeam == null || m == null) return plan;

            Formation mainInf = myTeam.GetFormation(FormationClass.Infantry);
            plan.BattleJoined = AnvilEngagedEnemyInfantry(mainInf, myTeam, m, out plan.AnvilTarget);
            ComputeInfantryCavThreat(myTeam, m, out plan.ThreatCav, out plan.CoverInfantry);
            // 敌方溃逃情况(须在下方"无显著敌编队即提前返回"之前算好:敌兵溃逃后脱编 → 敌编队变空 →
            // maxEnemyStrength<=0 提前返回,但那些散兵仍活着 IsRunningAway,正是要追击的对象)。
            // Pursue = 全线追击(团队级);LightCavPursue = 已有约一支编队崩 → 轻骑随时脱离追残兵。
            EnemyRoutInfo(myTeam, m, pool, out plan.Pursue, out float routingFrac);
            plan.LightCavPursue = routingFrac >= LightCavPursueFraction;

            // "显著"敌编队按兵力值的相对比值挑(替代硬编码绝对人数):先求最大敌编队兵力值作基准。
            float maxEnemyStrength = 0f;
            foreach (Formation f in EnemyFormations.Of(myTeam, m))
            {
                float str = FormationStrength.Of(f);
                if (str > maxEnemyStrength) maxEnemyStrength = str;
            }
            if (maxEnemyStrength <= 0f) return plan;
            float significantMin = maxEnemyStrength * SignificantFraction;

            var enemies = new List<Formation>(8);
            foreach (Formation f in EnemyFormations.Of(myTeam, m))
                if (FormationStrength.Of(f) >= significantMin) enemies.Add(f);
            if (enemies.Count == 0) return plan;

            Vec2 cavRef = CavalryReference(myTeam);

            float bestScore = 0f;
            float bestPoolFrac = 0f;
            bool bestRelease = false;
            Formation bestTarget = null;
            Vec2 bestFlank = Vec2.Zero;

            for (int i = 0; i < enemies.Count; i++)
            {
                Formation E = enemies[i];
                Vec2 epos = E.CachedAveragePosition;
                var ap = FormationGeometry.ApproachPointsFor(E, cavRef, FormationGeometry.Standoff);
                Vec2 rear = ap.Rear, lflank = ap.LeftFlank, rflank = ap.RightFlank;

                bool rearOpen = IsOpen(rear, E, enemies);
                bool lOpen = IsOpen(lflank, E, enemies);
                bool rOpen = IsOpen(rflank, E, enemies);

                float arcVal;
                Vec2 arcPt;
                if (rearOpen) { arcVal = 1.0f; arcPt = rear; }
                else if (lOpen || rOpen)
                {
                    arcVal = 0.7f;
                    if (lOpen && rOpen) arcPt = FormationGeometry.Nearer(lflank, rflank, cavRef);
                    else arcPt = lOpen ? lflank : rflank;
                }
                else
                {
                    arcVal = 0.25f;
                    arcPt = FormationGeometry.Nearer(lflank, rflank, cavRef);
                }

                // 该敌编队"距崩溃"比例 = 池 / (基阈 × 它的 tier 抗性)。池由编队级士气系统写入(对双方恒生效),
                // 故任何显著敌编队经首拍士气结算后都有池;TryGet 仅防开局头一拍竞态(此时 release=false,无碍)。
                FormationSnapshot snap = scanner.Scan(E);
                FormationShockState st = null;
                bool poolLive = pool != null && pool.TryGet(E, out st);
                float ePool = poolLive ? st.Effective : 0f; // 有效压力(池+伤亡地板+冲锋震慑):目标被打残/被冲也计入"逼近崩溃"
                float threshold = MoraleTuning.RoutThreshold(baseRoutThreshold, snap.AvgTier);
                float poolFrac = threshold > 0f ? ePool / threshold : 0f;

                float depletion = 1f - snap.CasualtyRatio;
                float weakness = poolFrac + depletion + snap.RoutingFraction;
                float prox = 1f / (1f + cavRef.Distance(epos) * 0.01f);
                float score = arcVal * (1f + weakness) * prox;

                // 放锤就绪 = 突破口震慑池逼近其崩溃阈值(敌人快崩,冲锋把它推过阈触发整队溃逃 + 追击)。
                bool release = poolLive && poolFrac >= HammerReleaseFraction;

                if (score > bestScore)
                {
                    bestScore = score; bestTarget = E; bestFlank = arcPt; bestPoolFrac = poolFrac; bestRelease = release;
                }
            }

            if (bestTarget != null)
            {
                plan.Target = bestTarget;
                plan.FlankPoint = bestFlank;
                plan.HasTarget = true;
                plan.TargetPoolFrac = bestPoolFrac;
                plan.ReleaseHammer = bestRelease;
                // 双重包抄:左/右轻骑分走突破口两侧接近点。**按"我方视角左右"分配**(避免把左翼轻骑派到空间上我方右侧 →
                // 横穿全场撞友军)。我方前向 = 骑兵参考点指向突破口;ourLeft = 前向左垂直;落在 ourLeft 半平面的侧翼点给左轻骑。
                var cavAp = FormationGeometry.ApproachPointsFor(bestTarget, cavRef, FormationGeometry.Standoff);
                Vec2 fwd = bestTarget.CachedAveragePosition - cavRef;
                Vec2 ourLeft = fwd.LengthSquared > 1e-4f ? fwd.Normalized().LeftVec() : new Vec2(-1f, 0f);
                Vec2 ec = bestTarget.CachedAveragePosition;
                bool leftFlankOnOurLeft = Vec2.DotProduct(cavAp.LeftFlank - ec, ourLeft) >= 0f;
                plan.LeftCavPoint = leftFlankOnOurLeft ? cavAp.LeftFlank : cavAp.RightFlank;
                plan.RightCavPoint = leftFlankOnOurLeft ? cavAp.RightFlank : cavAp.LeftFlank;
            }
            return plan;
        }

        /// <summary>
        /// 砧(主步兵)是否已咬住敌步兵 —— 所有绕后/绕侧的前置门。
        /// 主步兵中心到最近**敌步兵编队**的距离 &lt; JoinDistance 即算咬住;
        /// 敌无步兵编队时退回"到最近任意敌编队";我方无主步兵(如纯骑兵军)时不以此门冻结绕击(返 true)。
        /// </summary>
        private static bool AnvilEngagedEnemyInfantry(Formation mainInf, Team myTeam, Mission m, out Formation anvilTarget)
        {
            anvilTarget = null;
            if (mainInf == null || mainInf.CountOfUnits == 0) return true;
            Vec2 anchor = mainInf.CachedAveragePosition;
            float halfMine = mainInf.Depth * 0.5f;
            // 咬住判定按**编队深度感知**:中心距 < JoinDistance + 双方半深 → 两条线已贴近/接战。
            // (旧版纯比中心距 < 15m,需两线几乎重叠才算,实战中心距常 20-30m → 几乎永不触发,导致包抄/绕击/放锤整套门控冻死。)
            bool sawInf = false, infEngaged = false, anyEngaged = false;
            float bestInfD = float.MaxValue, bestAnyD = float.MaxValue;
            Formation nearestInf = null, nearestAny = null;
            foreach (Formation f in EnemyFormations.Of(myTeam, m))
            {
                float d2 = (f.CachedAveragePosition - anchor).LengthSquared;
                float reach = JoinDistance + halfMine + f.Depth * 0.5f;
                bool within = d2 < reach * reach;
                if (within) anyEngaged = true;
                if (d2 < bestAnyD) { bestAnyD = d2; nearestAny = f; }
                var qs = f.QuerySystem;
                if (qs != null && qs.IsInfantryFormation)
                {
                    sawInf = true;
                    if (within) infEngaged = true;
                    if (d2 < bestInfD) { bestInfD = d2; nearestInf = f; }
                }
            }
            anvilTarget = nearestInf ?? nearestAny; // 主步兵正面对手:最近敌步兵,无则最近任意敌编队
            return sawInf ? infEngaged : anyEngaged;
        }

        /// <summary>
        /// 敌方溃逃统计:遍历敌队活跃 human 兵,得"溃逃比"(IsRunningAway 占比)。
        /// broken = 溃逃比 ≥ PursueRoutingFraction(敌军已崩,全线追击);routingFrac 另供"轻骑随时追残"判据(LightCavPursueFraction)。
        /// 敌兵溃逃后脱离编队 → 敌编队人数清零,但散兵仍在 ActiveAgents,正是要追击的对象。
        /// </summary>
        private static void EnemyRoutInfo(Team myTeam, Mission m, FormationShockPool pool, out bool broken, out float routingFrac)
        {
            int alive = 0, routing = 0;
            foreach (Team t in m.Teams)
            {
                if (t == null || t == myTeam || !t.IsEnemyOf(myTeam)) continue;
                foreach (Agent a in t.ActiveAgents)
                {
                    if (a == null || !a.IsHuman) continue;
                    alive++;
                    // 可追击 = 逐兵真逃跑(触底四散 / vanilla 恐慌) **或** 所属编队正整队溃逃后撤(RoutLatched,背敌奔退)。
                    // 后者是新溃逃模型的常态(普通溃逃不脱编、不 IsRunningAway),不计入则轻骑追击触发不了(回归修复)。
                    if (a.IsRunningAway) { routing++; continue; }
                    Formation home = a.Formation;
                    if (home != null && pool != null && pool.TryGet(home, out FormationShockState st) && st.RoutLatched)
                        routing++;
                }
            }
            routingFrac = alive > 0 ? (float)routing / alive : 0f;
            broken = alive > 0 && routingFrac >= PursueRoutingFraction;
        }

        /// <summary>
        /// 敌近战骑兵是否威胁我方"扛骑弱/正面顶线的编队"(主步兵/包抄步兵/弓兵)——决定轻骑是否回援掩护。
        /// 规模一律用统一**兵力值**(<see cref="FormationStrength"/>,tier 加权):对每个受保护编队,在 CavThreatRange 内、
        /// 兵力值 &gt; CavThreatRatio × 该编队兵力值 的敌近战骑兵才算威胁;跨受保护编队取**最近**一支威胁骑兵作拦截目标。
        /// 无任何受保护编队 / 无此类敌骑 → cover=false(轻骑改为绕击或护侧)。
        /// 注:cav-cover 已由"只护主步兵"泛化到三支线列编队(ADR-0011 §5.C screenJobs 的兑现)。
        /// </summary>
        private static void ComputeInfantryCavThreat(Team myTeam, Mission m, out Formation threatCav, out bool cover)
        {
            threatCav = null;
            float bestD = float.MaxValue;
            ConsiderProtectee(myTeam.GetFormation(FormationClass.Infantry), myTeam, m, ref threatCav, ref bestD);
            ConsiderProtectee(myTeam.GetFormation(FormationClass.HeavyInfantry), myTeam, m, ref threatCav, ref bestD);
            ConsiderProtectee(myTeam.GetFormation(FormationClass.Ranged), myTeam, m, ref threatCav, ref bestD);
            cover = threatCav != null;
        }

        /// <summary>对单支受保护编队,把"近于 CavThreatRange、兵力值达威胁门槛、且比已选更近"的敌近战骑兵收为拦截目标。</summary>
        private static void ConsiderProtectee(Formation prot, Team myTeam, Mission m, ref Formation threatCav, ref float bestD)
        {
            if (prot == null || prot.CountOfUnits == 0) return;
            float protStrength = FormationStrength.Of(prot);
            if (protStrength <= 0f) return;
            float threatMin = CavThreatRatio * protStrength; // 兵力值"大到构成威胁"的下限 = 比值 × 受保护编队兵力值
            Vec2 anchor = prot.CachedAveragePosition;
            foreach (Formation f in EnemyFormations.Of(myTeam, m))
            {
                var qs = f.QuerySystem;
                if (qs == null || !qs.IsCavalryFormation) continue; // 仅近战骑兵(排除骑射)
                float d = anchor.Distance(f.CachedAveragePosition);
                if (d > CavThreatRange) continue;
                if (FormationStrength.Of(f) <= threatMin) continue;
                if (d < bestD) { bestD = d; threatCav = f; }
            }
        }

        /// <summary>接近点是否开放:CoverRange 内没有其它敌编队(E 自身不算)。</summary>
        private static bool IsOpen(Vec2 point, Formation E, List<Formation> enemies)
        {
            float r2 = CoverRange * CoverRange;
            for (int i = 0; i < enemies.Count; i++)
            {
                Formation F = enemies[i];
                if (F == E) continue;
                if ((F.CachedAveragePosition - point).LengthSquared < r2) return false;
            }
            return true;
        }

        /// <summary>我方骑兵参考点(优先重骑→轻骑→主步兵中心),用于"突破口离骑兵越近越优先"。</summary>
        private static Vec2 CavalryReference(Team team)
        {
            Formation h = team.GetFormation(FormationClass.HeavyCavalry);
            if (h != null && h.CountOfUnits > 0) return h.CachedAveragePosition;
            Formation l = team.GetFormation(FormationClass.LightCavalry);
            if (l != null && l.CountOfUnits > 0) return l.CachedAveragePosition;
            Formation l2 = team.GetFormation(FormationClass.Cavalry);
            if (l2 != null && l2.CountOfUnits > 0) return l2.CachedAveragePosition;
            Formation inf = team.GetFormation(FormationClass.Infantry);
            if (inf != null && inf.CountOfUnits > 0) return inf.CachedAveragePosition;
            return Vec2.Zero;
        }
    }
}
