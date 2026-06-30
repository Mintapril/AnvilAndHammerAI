using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AnvilAndHammerAI.Detection;
using AnvilAndHammerAI.Logging;
using AnvilAndHammerAI.Morale;
using AnvilAndHammerAI.Settings;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Formations
{
    /// <summary>
    /// 中央指挥调度器(ADR-0011 第 2 步,**完整版**)。不 subclass TacticComponent:每 0.5s 给 7 角色编队
    /// ResetBehaviorWeights + 按角色设权。**硬接管**:经 <see cref="BehaviorWeightOwnerPatch"/>(配合 <see cref="WeightOwnership"/>),
    /// 凡被本调度器驱动过的编队,vanilla/RBM 战术层对其行为权重的写入一律跳过 → mod 设权为唯一权威,无两次设权间的夺权窗口
    /// (取代旧的"设权频率高于战术 → 软压制取胜")。
    ///
    /// 读**完整空间脑**(<see cref="TacticalBrain"/>:定向编队+弧覆盖→突破口+开放弧落点;放锤=突破口逼近崩溃阈值):
    ///   主步兵(砧)= 推进/钉住;弓兵 = 屏射;骑射 = 风筝;
    ///   包抄步兵 = **砧咬住敌步兵后**绕击突破口,在此之前侧后跟随、避战(压后预备,不正面对耗);
    ///   左/右轻骑 = 砧未咬住敌步兵→护侧;咬住后:**敌骑威胁我方步兵(规模很小/正扑步兵)→ 掩护(拦截那支骑兵)**,否则→**直接绕侧绕后**(异向开放弧,避开包抄步兵);重骑(锤)= 压后预备,砧咬住敌步兵且敌逼近崩溃→决定性绕击冲锋。
    /// 即**所有绕后/绕侧都以"砧(主步兵)咬住敌步兵"为前置门**(plan.BattleJoined)。绕击目标/落点 = 空间脑算的 schwerpunkt 开放弧,经 FlankChargeBehavior.SetFlankTarget 注入。
    ///
    /// 共享 <see cref="FormationShockPool"/>(编队级士气系统所写,对双方恒生效)作放锤判据(突破口震慑池逼近崩溃阈值)。
    /// 范围:受『只影响我方军队』(只玩家军时只驱动玩家队;敌方交回 vanilla/RBM)。所有设权走 GetBehavior 守卫防 MBException。
    ///
    /// **遇袭自发反应覆盖层**(<see cref="TryReact"/> + <see cref="ThreatReactionBehavior"/>):每个角色编队在常规设权前先判急性敌骑威胁
    /// (<see cref="ThreatAssessor"/>),命中则以最高权重覆盖本拍计划,做应激姿态——步兵结盾墙/架枪、弓兵/骑射后撤、预备骑兵后撤蓄势冲锋迎击;
    /// 经迟滞(<see cref="ReactionTuning.HoldSeconds"/>)去抖,威胁清除后自动回归计划。受 AnvilSettings.ThreatReactionsEnabled 总控。
    /// </summary>
    public sealed class CommandSchedulerMissionLogic : MissionLogic
    {
        private const float TickInterval = 0.5f;
        private const float PlanLogInterval = 5f;
        private const float HammerReleaseDelay = 12f; // 砧咬住敌阵持续此秒数 → 强制放锤(即便敌方震慑未到崩溃阈,避免重骑永不下场)
        private const float LightCavCoverWeight = 1.8f; // 轻骑掩护我方步兵的权重(高:更倾向掩护)
        private const float LightCavFlankWeight = 1.4f; // 轻骑绕击侧/背的权重(咬住后仍可绕击,但低于掩护)
        private const float HeavyCavChargeReadyDist = 80f;     // 重骑前置蓄势点:距突破口此远、突破口开放弧那侧(留足冲锋助跑,放锤就近发起、免现寻路)
        private const float HammerPreStageFraction = 0.5f;     // 突破口震慑达放锤阈的此比例(放锤=0.75)→ 重骑由后方预备提前前置蓄势
        private const float HammerPreStageTimeFraction = 0.6f; // 或砧咬住已达强制放锤时限的此比例 → 提前前置蓄势

        private readonly FormationShockPool _pool;
        private readonly FormationScanner _scanner = new FormationScanner();

        private TickGate _gate = new TickGate(TickInterval);
        private TickGate _logGate = new TickGate(PlanLogInterval);
        private bool _announced;
        private int _routHeld; // 本拍因士气溃逃而被"停步保持"(不抢回战斗行为)的编队数,供 [sched] 心跳诊断

        // 遇袭自发反应的迟滞状态(每编队):记最近一次有威胁的时刻 + 当时的反应模式/目标,
        // 用于"进反应即时、出反应延迟"的去抖(见 ReactionTuning.HoldSeconds)。CWT 随编队 GC 自动回收。
        private sealed class ReactState
        {
            public ThreatReactionBehavior.Mode Mode;
            public Formation Threat;
            public Vec2 ThreatPos;
            public float LastThreatTime;
        }
        private readonly ConditionalWeakTable<Formation, ReactState> _react = new ConditionalWeakTable<Formation, ReactState>();

        // 每队"砧(主步兵)首次咬住敌阵"的时刻;砧持续咬住 HammerReleaseDelay 秒后强制放锤(时间触发,补震慑触发之外的兜底)。
        private readonly Dictionary<Team, float> _joinedSince = new Dictionary<Team, float>();

        public CommandSchedulerMissionLogic(FormationShockPool pool) { _pool = pool; }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled || !s.AutoFormationEnabled || !s.CommandSchedulerEnabled) return;
            Mission m = Mission.Current;
            if (m == null || !m.IsFieldBattle || m.Mode == MissionMode.Deployment) return;

            if (!_gate.Ready(dt, out _)) return;
            bool logNow = _logGate.Ready(TickInterval, out _);

            foreach (Team team in m.Teams)
            {
                if (team == null || !ScopeFilter.Applies(team)) continue; // 只玩家军时:只驱动玩家队;敌方交回 vanilla/RBM
                BattlePlan plan = TacticalBrain.Plan(team, m, _pool, _scanner, s.PoolRoutThreshold);
                DriveTeam(team, plan, m);
                if (logNow)
                    Log.Info($"[sched] side={team.Side} joined={plan.BattleJoined} release={plan.ReleaseHammer} " +
                             $"poolFrac={plan.TargetPoolFrac:0.00} target={(plan.HasTarget ? plan.Target.FormationIndex.ToString() : "-")} routHeld={_routHeld}");
            }

            if (!_announced)
            {
                _announced = true;
                Log.Info("[sched] 中央调度器已激活(7 编队按角色设权;空间脑弧覆盖选突破口;放锤=突破口震慑池逼近崩溃阈值)。");
            }
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();
            WeightOwnership.Clear(); // 释放本场 Formation 引用(权重归属表)
        }

        private void DriveTeam(Team team, BattlePlan plan, Mission m)
        {
            _routHeld = 0; // 本队本拍诊断计数:几支编队因士气溃逃被停步保持(见 Drive)
            Formation mainInf = team.GetFormation(FormationClass.Infantry);
            Formation flankInf = team.GetFormation(FormationClass.HeavyInfantry);
            Formation archers = team.GetFormation(FormationClass.Ranged);
            Formation horseArch = team.GetFormation(FormationClass.HorseArcher);
            Formation lcavL = team.GetFormation(FormationClass.LightCavalry);
            Formation lcavR = team.GetFormation(FormationClass.Cavalry);
            Formation hcav = team.GetFormation(FormationClass.HeavyCavalry);

            bool joined = plan.BattleJoined;
            bool release = plan.ReleaseHammer;
            // 时间放锤:砧首次咬住敌阵起计时,持续 HammerReleaseDelay 秒即强制放锤(补"敌方震慑逼近崩溃"触发之外的兜底,
            // 避免敌方一直不崩 → 重骑永不下场)。砧脱离则清零重计。
            if (joined) { if (!_joinedSince.ContainsKey(team)) _joinedSince[team] = m.CurrentTime; }
            else _joinedSince.Remove(team);
            float joinedElapsed = (joined && _joinedSince.TryGetValue(team, out float js)) ? m.CurrentTime - js : 0f;
            bool releaseNow = release || (joined && joinedElapsed >= HammerReleaseDelay);
            // 重骑前置蓄势:**快到放锤阈值**(突破口震慑达放锤阈的 HammerPreStageFraction,或砧咬住已达强制放锤时限的 HammerPreStageTimeFraction)
            // → 重骑由后方深远预备提前移动到便于冲击位;否则维持后方预备(原始行为)。
            bool hammerApproaching = joined && plan.HasTarget &&
                (plan.TargetPoolFrac >= HammerPreStageFraction || joinedElapsed >= HammerReleaseDelay * HammerPreStageTimeFraction);
            // 遇袭自发反应总开关(在游可关:关则各编队严格按原计划,被偷袭时可能原地不动)。
            bool react = AnvilSettings.Instance?.ThreatReactionsEnabled == true;
            // 战场事件提示只针对玩家本队(避免敌方 AI 也刷屏);溃逃/重整在士气层另对双方播。
            bool narrate = m.PlayerTeam != null && team == m.PlayerTeam;

            // 敌军已崩(无成建制敌编队、残敌多在溃逃)→ 全线追击残敌。这是决战收尾、唯一允许的自由冲锋
            //(BehaviorCharge 在无最近敌编队时下 MovementOrderCharge,直冲最近散兵);接战期的混战仍禁止。
            if (plan.Pursue)
            {
                // 追击溃敌 = **轻骑兵的职责**:只放两翼轻骑追杀散兵;步兵/重骑/弓兵/骑射就地收拢保持队形、不追
                //(避免步兵重骑徒劳乱跑、阵列散开;BehaviorStop = 停在原地结阵自由射击)。
                Say(narrate, lcavL, "team", "pursue", "{=AnvilHammer_msg_pursue}Enemy routed - light cavalry, pursue!");
                PursueCharge(lcavL); PursueCharge(lcavR);
                HoldGround(mainInf); HoldGround(flankInf); HoldGround(hcav); HoldGround(archers); HoldGround(horseArch);
                return;
            }

            // 主步兵(砧):推进 + 接战稳住钉住。未咬住敌步兵前若被敌骑贴脸 → 结盾墙架枪正面受冲
            //（咬住后交给 cav-cover + 士气,不让砧背对敌步兵转身)。
            Drive(mainInf, f =>
            {
                Reset(f);
                if (react && !joined && TryReact(f, team, m, ThreatReactionBehavior.Mode.Brace,
                        ReactionTuning.AcuteCavRange, ReactionTuning.InfantryBraceCavRatio, mainInf))
                { Say(narrate, f, "react", "brace", "{=AnvilHammer_msg_main_inf_brace}Main infantry forms up against cavalry"); return; }
                Say(narrate, f, "react", "", null);
                W<BehaviorAdvance>(f, 1f);
                W<BehaviorDefend>(f, joined ? 1.2f : 0f);
                // 不设 BehaviorCharge:砧只推进+结阵防守,沿正面整队交战,绝不放任散兵自由冲锋(混战)。
                MatchWidth(f, plan.AnvilTarget); // 接敌宽度匹配正面敌步兵宽度
                Say(narrate, f, "inf", joined ? "join" : "adv", joined ? "{=AnvilHammer_msg_main_inf_joined}Main infantry (anvil) has the enemy locked, holding the front" : null);
            });

            // 包抄步兵:砧咬住敌步兵后绕击军团突破口;在此之前侧后跟随、避战(压后预备)。
            // 预备态被敌骑冲 → 结盾墙架枪稳住(取代背对敌后撤被穿);已在绕击中则不打断。
            Drive(flankInf, f =>
            {
                Reset(f); EnsureFlank(f); SetFlank(f, plan);
                bool flanking = joined && plan.HasTarget;
                if (react && !flanking && TryReact(f, team, m, ThreatReactionBehavior.Mode.Brace,
                        ReactionTuning.AcuteCavRange, ReactionTuning.InfantryBraceCavRatio, mainInf))
                { Say(narrate, f, "react", "brace", "{=AnvilHammer_msg_flank_inf_brace}Flank infantry forms up against cavalry"); return; }
                Say(narrate, f, "react", "", null);
                if (flanking) { W<FlankChargeBehavior>(f, 1.2f); Say(narrate, f, "flank", "flank", "{=AnvilHammer_msg_flank_inf_flanking}Flank infantry strikes the enemy's side and rear"); }
                else { StageOrReserve(f, mainInf, plan, true, false); Say(narrate, f, "flank", "reserve", null); }
            });

            // 弓兵:屏射。被敌近战骑兵突入 → 朝主步兵后方散开后撤(它扛不住贴身近战)。
            Drive(archers, f =>
            {
                Reset(f);
                if (react && TryReact(f, team, m, ThreatReactionBehavior.Mode.FallBack,
                        ReactionTuning.ArcherFallbackRange, ReactionTuning.ArcherFallbackCavRatio, mainInf))
                { ReleaseRangedFocus(f); Say(narrate, f, "react", "fallback", "{=AnvilHammer_msg_archers_fallback}Archers fall back from the cavalry on them"); return; }
                Say(narrate, f, "react", "", null);
                W<BehaviorScreenedSkirmish>(f, 1f);
                W<BehaviorSkirmishLine>(f, 0.5f);
                ApplyRangedFocus(f, ChooseRangedTarget(plan)); // 整队硬锁定聚焦突破口(编队级索敌)
            });

            // 骑射:风筝。被敌近战骑兵贴上(失去风筝空间)→ 朝主步兵后方撤离。
            Drive(horseArch, f =>
            {
                Reset(f);
                if (react && TryReact(f, team, m, ThreatReactionBehavior.Mode.FallBack,
                        ReactionTuning.ArcherFallbackRange, ReactionTuning.ArcherFallbackCavRatio, mainInf))
                { ReleaseRangedFocus(f); Say(narrate, f, "react", "fallback", "{=AnvilHammer_msg_horse_archers_fallback}Horse archers fall back from the cavalry on them"); return; }
                Say(narrate, f, "react", "", null);
                // 自有风筝:把骑射带到**距敌射程边缘**处、沿敌正面横向扫掠("绕圈"),全程整队放箭、接敌宽度匹配敌编队。
                // 不用 vanilla——BehaviorHorseArcherSkirmish 会退到我方后方 30m+ 放箭(不是绕敌);BehaviorMountedSkirmish 接战时散队自由冲锋(违规)。
                EnsureKite(f);
                W<HorseArcherKiteBehavior>(f, 1f);
                ApplyRangedFocus(f, ChooseRangedTarget(plan)); // 整队硬锁定聚焦突破口(风筝管走位,这管打谁)
            });

            // 左轻骑(优先级):掩护我方步兵(防守,最高)> 追击残兵(敌已溃逃约一支)> 绕击突破口左翼(权重低)> 护侧。
            // 掩护不再以"砧已咬住"为前提(随时护步兵抗骑);非掩护/绕击态被敌骑冲 → 回身蓄势冲锋迎击(自卫)。
            Drive(lcavL, f =>
            {
                Reset(f); EnsureFlank(f);
                bool committed = (plan.CoverInfantry && plan.ThreatCav != null) || (joined && plan.HasTarget);
                if (react && !committed && TryReact(f, team, m, ThreatReactionBehavior.Mode.Counter,
                        ReactionTuning.AcuteCavRange, ReactionTuning.CavCounterCavRatio, mainInf))
                { Say(narrate, f, "react", "counter", "{=AnvilHammer_msg_left_cav_counter}Left light cavalry wheels to meet the charging cavalry"); return; }
                Say(narrate, f, "react", "", null);
                if (plan.CoverInfantry && plan.ThreatCav != null) { SetIntercept(f, plan.ThreatCav); W<FlankChargeBehavior>(f, LightCavCoverWeight); Say(narrate, f, "cav", "cover", "{=AnvilHammer_msg_left_cav_cover}Left light cavalry rides to cover our infantry"); }
                else if (plan.LightCavPursue) { W<BehaviorCharge>(f, 2f); Say(narrate, f, "cav", "pursue", "{=AnvilHammer_msg_pursue}Enemy routed - light cavalry, pursue!"); }
                else if (joined && plan.HasTarget) { SetFlankToPoint(f, plan.Target, plan.LeftCavPoint); W<FlankChargeBehavior>(f, LightCavFlankWeight); Say(narrate, f, "cav", "flank", "{=AnvilHammer_msg_left_cav_flank}Left light cavalry strikes the enemy's left"); }
                else { Screen(f, mainInf, plan, 1f); Say(narrate, f, "cav", "screen", null); }
            });

            // 右轻骑:对称(同左)。
            Drive(lcavR, f =>
            {
                Reset(f); EnsureFlank(f);
                bool committed = (plan.CoverInfantry && plan.ThreatCav != null) || (joined && plan.HasTarget);
                if (react && !committed && TryReact(f, team, m, ThreatReactionBehavior.Mode.Counter,
                        ReactionTuning.AcuteCavRange, ReactionTuning.CavCounterCavRatio, mainInf))
                { Say(narrate, f, "react", "counter", "{=AnvilHammer_msg_right_cav_counter}Right light cavalry wheels to meet the charging cavalry"); return; }
                Say(narrate, f, "react", "", null);
                if (plan.CoverInfantry && plan.ThreatCav != null) { SetIntercept(f, plan.ThreatCav); W<FlankChargeBehavior>(f, LightCavCoverWeight); Say(narrate, f, "cav", "cover", "{=AnvilHammer_msg_right_cav_cover}Right light cavalry rides to cover our infantry"); }
                else if (plan.LightCavPursue) { W<BehaviorCharge>(f, 2f); Say(narrate, f, "cav", "pursue", "{=AnvilHammer_msg_pursue}Enemy routed - light cavalry, pursue!"); }
                else if (joined && plan.HasTarget) { SetFlankToPoint(f, plan.Target, plan.RightCavPoint); W<FlankChargeBehavior>(f, LightCavFlankWeight); Say(narrate, f, "cav", "flank", "{=AnvilHammer_msg_right_cav_flank}Right light cavalry strikes the enemy's right"); }
                else { Screen(f, mainInf, plan, -1f); Say(narrate, f, "cav", "screen", null); }
            });

            // 重骑(锤):压后预备,砧咬住敌步兵且敌逼近崩溃→决定性绕击冲锋。
            // 预备(未放锤)期被敌骑冲 → 后撤蓄势冲锋迎击;放锤已就绪则无视骚扰,坚决投锤(不让骚扰取消决定性冲击)。
            Drive(hcav, f =>
            {
                Reset(f); EnsureFlank(f); SetFlank(f, plan);
                bool hammering = joined && releaseNow && plan.HasTarget;
                if (react && !hammering && TryReact(f, team, m, ThreatReactionBehavior.Mode.Counter,
                        ReactionTuning.AcuteCavRange, ReactionTuning.CavCounterCavRatio, mainInf))
                { Say(narrate, f, "react", "counter", "{=AnvilHammer_msg_heavy_cav_counter}Heavy cavalry wheels to meet the charging cavalry"); return; }
                Say(narrate, f, "react", "", null);
                if (hammering) { W<FlankChargeBehavior>(f, 2f); Say(narrate, f, "hcav", "charge", "{=AnvilHammer_msg_heavy_cav_hammer}Heavy cavalry (hammer) launches the decisive charge!"); }
                else { StageOrReserve(f, mainInf, plan, false, hammerApproaching); Say(narrate, f, "hcav", "reserve", null); }
            });
        }

        /// <summary>战场事件播报小工具(仅本队播报时生效)。</summary>
        private static void Say(bool narrate, Formation f, string channel, string mode, string msg)
        {
            if (narrate) BattleNarrator.Mode(f, channel, mode, msg);
        }

        /// <summary>
        /// 遇袭自发反应:若本编队当前(或迟滞窗内)有急性敌骑威胁,则把 <see cref="ThreatReactionBehavior"/> 抬到最高权重
        /// 并注入对应模式,返回 true(调用方应 return,跳过本拍常规设权)。无威胁返 false。
        /// </summary>
        private bool TryReact(Formation f, Team team, Mission m, ThreatReactionBehavior.Mode kind,
            float range, float ratio, Formation mainInf)
        {
            Formation threat = ThreatAssessor.NearestChargingCav(f, team, m, range, ratio);
            ReactState st = _react.GetValue(f, _ => new ReactState());
            float now = m.CurrentTime;
            if (threat != null)
            {
                st.Mode = kind; st.Threat = threat; st.ThreatPos = threat.CachedAveragePosition; st.LastThreatTime = now;
            }

            bool holding = st.Mode != ThreatReactionBehavior.Mode.None && (now - st.LastThreatTime) <= ReactionTuning.HoldSeconds;
            bool active = threat != null || holding;
            // 反冲目标在迟滞窗内已被打光且当前无新威胁 → 结束反应(没有要迎击的对象了)。
            if (active && st.Mode == ThreatReactionBehavior.Mode.Counter && threat == null
                && (st.Threat == null || st.Threat.CountOfUnits == 0))
                active = false;
            if (!active)
            {
                st.Mode = ThreatReactionBehavior.Mode.None;
                // 反应结束:把行为模式也清零,使下次反冲能重新复位蓄势 latch(否则停留 Counter 会跳过后撤蓄势)。
                f.AI.GetBehavior<ThreatReactionBehavior>()?.SetReaction(
                    ThreatReactionBehavior.Mode.None, null, Vec2.Zero, false, Vec2.Zero, false, false);
                return false;
            }

            EnsureReaction(f);
            var b = f.AI.GetBehavior<ThreatReactionBehavior>();
            if (b == null) return false; // 守卫:挂载失败则退回常规设权

            Vec2 rally = Vec2.Zero; bool hasRally = false;
            bool enveloped = false;
            if (st.Mode == ThreatReactionBehavior.Mode.FallBack && mainInf != null && mainInf.CountOfUnits > 0)
            {
                // 集结到主步兵后方(相对当前威胁的背侧),避免撤进白刃线。
                Vec2 mip = mainInf.CachedAveragePosition;
                Vec2 back = mip - st.ThreatPos;
                back = back.LengthSquared > 1e-4f ? back.Normalized() : mainInf.Direction;
                rally = mip + back * (mainInf.Depth * 0.5f + 8f);
                hasRally = true;
            }
            else if (st.Mode == ThreatReactionBehavior.Mode.Brace)
            {
                // 被多面夹击则结 Square(全向)。仅在确实要架枪时扫一次几何环绕,避免每拍空算。
                enveloped = _scanner.Scan(f).OccupiedSectors >= MoraleTuning.EncircleMinSectors;
            }
            b.SetReaction(st.Mode, st.Threat, st.ThreatPos, true, rally, hasRally, enveloped);
            W<ThreatReactionBehavior>(f, ReactionTuning.ReactionWeight);

            switch (st.Mode)
            {
                case ThreatReactionBehavior.Mode.Brace: Telemetry.ReactBrace++; break;
                case ThreatReactionBehavior.Mode.FallBack: Telemetry.ReactFallback++; break;
                case ThreatReactionBehavior.Mode.Counter: Telemetry.ReactCounter++; break;
            }
            return true;
        }

        // ── 设权小工具(全部 GetBehavior 守卫,防 SetBehaviorWeight 抛 MBException) ──
        private void Drive(Formation f, Action<Formation> act)
        {
            if (f == null || f.CountOfUnits == 0 || f.AI == null) return;
            // 玩家亲自接管的编队让行(仅 RespectPlayerOrders 开时):此时不 Reset/设权,避免抹掉玩家意图。
            if (PlayerControl.IsPlayerCommanded(f)) return;
            // 关键:本 mod 靠**设行为权重**指挥,而权重**只对 AI 控制的编队生效**。若编队被 RTS Camera/指挥盘接管成非 AI
            // (IsAIControlled=false),设了权重也不响应 → 站着发呆。故在驱动前夺回 AI 控制,使权重生效(RespectPlayerOrders 关时本 mod 全程接管指挥)。
            if (!f.IsAIControlled) f.SetControlledByAI(true);
            // 与士气层协调:本编队正处士气溃逃锁存(RoutLatched)中 → 不抢回战斗行为,改为**整队有序后撤**(见 HoldRout)。
            // 编队保持成建制(逐兵不 Panic、不脱编)→ Pass A 仍扫描它、脱离火力后情势池衰减解锁集结;集结后常规设权自动覆盖。
            FormationShockState st = null;
            bool routed = _pool != null && _pool.TryGet(f, out st) && st.RoutLatched;
            // 决定性崩溃(触底):逐兵恐慌四散由士气层处理(触底不集结,无脱编↔召回对冲),调度器不接管、任其溃散。
            if (routed && st.Bottomed) return;
            if (routed) _routHeld++;
            // 硬接管权重:标记"以下为 mod 自身写入"(BehaviorWeightOwnerPatch 放行),写完记录本编队最近设权时刻 →
            // 该 patch 据此把 vanilla/RBM 对本编队的设权全部跳过,消除两次设权之间的夺权窗口。
            WeightOwnership.ModStamping = true;
            try { if (routed) HoldRout(f); else act(f); }
            finally { WeightOwnership.ModStamping = false; }
            WeightOwnership.MarkStamped(f, Mission.Current.CurrentTime);
        }

        private static void Reset(Formation f) { f.AI.ResetBehaviorWeights(); }

        // 接敌宽度:把编队正面宽度设为目标敌编队宽度(尽量同宽对齐)。target 为空则不动。
        private static void MatchWidth(Formation f, Formation target)
        {
            if (target != null && target.CountOfUnits > 0)
                f.SetFormOrder(FormOrder.FormOrderCustom(target.Width));
        }

        // 骑射风筝(自有行为)按需挂载并设权。
        private static void EnsureKite(Formation f)
        {
            if (f.AI.GetBehavior<HorseArcherKiteBehavior>() == null)
                f.AI.AddAiBehavior(new HorseArcherKiteBehavior(f));
        }

        private readonly List<Agent> _focusBuf = new List<Agent>(); // 复用:目标编队活体 agent 列表(避免每拍分配)

        // 远程编队级**硬锁定**索敌:整支远程编队的每个 agent 强制索敌目标编队 target 内的敌兵(在其活体间轮流分摊,
        // 避免全打一个、一死全空),并关掉自动索敌防原生/RBM 改回 → 真正"禁止同编队各打各的"。
        // (SetTargetFormation 只是索敌"偏好",原生仍可能打更近的,实测不够,故升级为此逐兵硬锁。)target 为空 → 释放,见 ReleaseRangedFocus。
        private void ApplyRangedFocus(Formation f, Formation target)
        {
            if (f == null) return;
            if (target == null || target.CountOfUnits <= 0) { ReleaseRangedFocus(f); return; }
            if (f.TargetFormation != target) f.SetTargetFormation(target); // 编队级目标(供其它系统读),与逐兵硬锁互补

            _focusBuf.Clear();
            target.ApplyActionOnEachUnit(a => { if (a != null && a.IsActive()) _focusBuf.Add(a); });
            int n = _focusBuf.Count;
            if (n == 0) { ReleaseRangedFocus(f); return; }

            int next = 0;
            f.ApplyActionOnEachUnit(a =>
            {
                if (a == null || !a.IsActive()) return;
                Agent cur = a.GetTargetAgent();
                if (cur == null || !cur.IsActive() || cur.Formation != target)
                    a.SetTargetAgent(_focusBuf[next++ % n]); // 现目标不在 target 内 → 改派 target 内一个(轮流)
                a.SetAutomaticTargetSelection(false);          // 关自动索敌:锁住不让原生改回别的编队
            });
        }

        // 释放远程硬锁:开回自动索敌(被骑兵贴身/溃逃时能自卫、能正常索敌),清编队级目标。对未锁过的编队也安全(开回自动=默认态)。
        private static void ReleaseRangedFocus(Formation f)
        {
            if (f == null) return;
            if (f.TargetFormation != null) f.SetTargetFormation(null);
            f.ApplyActionOnEachUnit(a => { if (a != null) a.SetAutomaticTargetSelection(true); });
        }

        private static Formation ChooseRangedTarget(BattlePlan plan)
        {
            if (plan != null)
            {
                if (plan.HasTarget && plan.Target != null && plan.Target.CountOfUnits > 0) return plan.Target;
                if (plan.AnvilTarget != null && plan.AnvilTarget.CountOfUnits > 0) return plan.AnvilTarget;
            }
            return null;
        }

        // 追击:整队 BehaviorCharge(无最近敌编队时它会下 MovementOrderCharge,直冲最近散兵 → 追溃逃残敌)。仅敌军已崩时给轻骑调用。
        private void PursueCharge(Formation f) => Drive(f, ff => { Reset(ff); W<BehaviorCharge>(ff, 2f); });

        // 就地收拢:停在原地结阵自由射击,不追击、不乱跑(追击阶段给步兵/重骑/弓兵/骑射用,使追击成为轻骑专责)。
        private void HoldGround(Formation f) => Drive(f, ff => { Reset(ff); W<BehaviorStop>(ff, 1f); });

        // 溃逃后撤:士气溃逃锁存中的编队 → 整队有序后撤(复用 ThreatReactionBehavior 的 FallBack:散开、背离最近敌编队、面朝敌)。
        // 不逐兵 Panic、不脱编 → 编队成建制、Pass A 仍扫描它,脱离火力后情势池衰减、集结(RoutLatched 解除)即恢复战斗;挂载失败退回停步。
        private void HoldRout(Formation f)
        {
            Reset(f);
            ReleaseRangedFocus(f); // 溃逃时释放远程硬锁:开回自动索敌,不让溃兵呆滞死盯远处目标
            EnsureReaction(f);
            var b = f.AI.GetBehavior<ThreatReactionBehavior>();
            if (b == null) { W<BehaviorStop>(f, 1f); return; }
            Mission m = Mission.Current;
            Formation threat = m != null ? EnemyFormations.Nearest(f, m) : null;
            Vec2 tpos = threat != null ? threat.CachedAveragePosition : f.CachedAveragePosition;
            b.SetReaction(ThreatReactionBehavior.Mode.Rout, threat, tpos, threat != null, Vec2.Zero, false, false);
            W<ThreatReactionBehavior>(f, ReactionTuning.ReactionWeight);
        }

        private static void W<T>(Formation f, float w) where T : BehaviorComponent
        {
            if (w <= 0f) return;
            if (f.AI.GetBehavior<T>() != null) f.AI.SetBehaviorWeight<T>(w);
        }

        // 护侧:把轻骑带到主步兵该侧侧翼并立定面敌。**不用 vanilla BehaviorProtectFlank**——它要某编队被标记
        // IsMainFormation 才有非零权重(其 GetAiWeight 在无 main formation 时恒返 0),而本 mod 压制了战术层、无人被标记
        //  → ProtectFlank 有效权重恒为 0、设权再高也选不上 → 两翼轻骑无定位、双双挤在一侧。
        // 改用自有 staging 行为(注入落点后权重恒为 1)把左/右轻骑分别定位到主步兵左/右外侧。sideSign:左=+1,右=-1。
        private static void Screen(Formation f, Formation mainInf, BattlePlan plan, float sideSign)
        {
            if (mainInf != null && mainInf.CountOfUnits > 0)
            {
                EnsureStaging(f);
                if (f.AI.GetBehavior<ReserveStagingBehavior>() is ReserveStagingBehavior b)
                {
                    b.SetPoint(ScreenPoint(mainInf, plan, sideSign), true); // 避让由 ReserveStagingBehavior 每拍连续处理(FormationAvoidance)
                    W<ReserveStagingBehavior>(f, 1f);
                    return;
                }
            }
            EnsureReserve(f); W<BehaviorReserve>(f, 1f); // 无主步兵(纯骑兵军)→ 退回原版预备
        }

        /// <summary>主步兵参照系:中心 m、单位前向 d(朝向无效则回退指向突破口,再无则 +Y)、左垂直 perp。ScreenPoint/StagePoint 共用。</summary>
        private static void MainInfFrame(Formation mainInf, BattlePlan plan, out Vec2 m, out Vec2 d, out Vec2 perp)
        {
            m = mainInf.CachedAveragePosition;
            d = mainInf.Direction;
            if (d.LengthSquared < 1e-4f && plan != null && plan.HasTarget) d = plan.Target.CachedAveragePosition - m;
            d = d.LengthSquared > 1e-4f ? d.Normalized() : new Vec2(0f, 1f);
            perp = d.LeftVec();
        }

        /// <summary>护侧点:主步兵左/右外侧(半宽 + 余量),与主步兵线大致齐平、面向敌方。sideSign:左=+1(d.LeftVec 方向),右=-1。</summary>
        private static Vec2 ScreenPoint(Formation mainInf, BattlePlan plan, float sideSign)
        {
            MainInfFrame(mainInf, plan, out Vec2 m, out _, out Vec2 perp);
            return m + perp * sideSign * (mainInf.Width * 0.5f + 72f); // 大幅外推:两翼轻骑各离主步兵翼侧 ~72m,左右拉得很开
        }

        private static void EnsureFlank(Formation f)
        {
            if (f.AI.GetBehavior<FlankChargeBehavior>() == null)
                f.AI.AddAiBehavior(new FlankChargeBehavior(f));
        }

        // 遇袭自发反应行为按需挂载(任意角色编队都可能用到:步兵架枪 / 弓兵后撤 / 骑兵反冲)。
        private static void EnsureReaction(Formation f)
        {
            if (f.AI.GetBehavior<ThreatReactionBehavior>() == null)
                f.AI.AddAiBehavior(new ThreatReactionBehavior(f));
        }

        // ── 预备待命定位(包抄步兵=主步兵侧后,重骑=更后方;纵深队形,互不重叠、不排成一字宽线) ──
        private static void StageOrReserve(Formation f, Formation mainInf, BattlePlan plan, bool isFlankInf, bool heavyForward)
        {
            if (mainInf != null && mainInf.CountOfUnits > 0)
            {
                EnsureStaging(f);
                if (f.AI.GetBehavior<ReserveStagingBehavior>() is ReserveStagingBehavior b)
                {
                    b.SetPoint(StagePoint(mainInf, plan, isFlankInf, heavyForward), true); // 避让由 ReserveStagingBehavior 每拍连续处理(FormationAvoidance)
                    W<ReserveStagingBehavior>(f, 1f);
                    return;
                }
            }
            EnsureReserve(f); W<BehaviorReserve>(f, 1f); // 无主步兵(纯骑兵军等)→ 退回原版预备
        }

        /// <summary>待命点:包抄步兵=主步兵侧后(朝突破口那一侧),重骑=正后更深。-Direction = 主步兵后方(背离敌)。</summary>
        private static Vec2 StagePoint(Formation mainInf, BattlePlan plan, bool isFlankInf, bool heavyForward)
        {
            MainInfFrame(mainInf, plan, out Vec2 m, out Vec2 d, out Vec2 perp);
            // 偏向突破口那一侧(无目标默认左)。包抄步兵与重骑都压在这一侧侧后,但纵深 + 横向都拉开,互不重叠。
            float side = (plan != null && plan.HasTarget && Vec2.DotProduct(plan.FlankPoint - m, perp) < 0f) ? -1f : 1f;
            if (isFlankInf)
                return m - d * 22f + perp * side * (mainInf.Width * 0.5f + 24f);  // 包抄步兵:侧后(后 22m + 半宽+24m),贴主步兵翼侧待绕击
            // 重骑:**默认仍在侧后深远预备(原始行为)**;仅当快到放锤阈值(heavyForward)时,提前移动到
            // **距突破口 HeavyCavChargeReadyDist(80m)、突破口开放弧那一侧**的便于冲击位,留足冲锋助跑,放锤时就近发起、免现寻路。
            if (heavyForward && plan != null && plan.HasTarget)
            {
                Vec2 ec = (plan.Target != null && plan.Target.CountOfUnits > 0) ? plan.Target.CachedAveragePosition : plan.FlankPoint;
                Vec2 toFlank = plan.FlankPoint - ec;                              // 敌中心 → 开放弧落点(= 冲击进入侧)
                toFlank = toFlank.LengthSquared > 1e-4f ? toFlank.Normalized() : -d; // 退化:用主步兵前向反向(背敌侧)
                return ec + toFlank * HeavyCavChargeReadyDist;                    // 距突破口 80m、开放弧那侧
            }
            return m - d * 60f + perp * side * (mainInf.Width * 0.5f + 48f);       // 默认:侧后最后侧深远预备
        }

        private static void EnsureStaging(Formation f)
        {
            if (f.AI.GetBehavior<ReserveStagingBehavior>() == null)
                f.AI.AddAiBehavior(new ReserveStagingBehavior(f));
        }

        // 步兵编队默认行为集未必含 BehaviorReserve(它常见于骑兵);包抄步兵避战预备前先确保挂上,免 W<> 空转。
        private static void EnsureReserve(Formation f)
        {
            if (f.AI.GetBehavior<BehaviorReserve>() == null)
                f.AI.AddAiBehavior(new BehaviorReserve(f));
        }

        private static void SetFlank(Formation f, BattlePlan plan)
        {
            var b = f.AI.GetBehavior<FlankChargeBehavior>();
            if (b != null) b.SetFlankTarget(plan.Target, plan.FlankPoint, plan.HasTarget);
        }

        // 轻骑专用:把绕击落点注入到指定点(左/右轻骑分走突破口左/右翼,双重包抄,不挤同一处)。
        private static void SetFlankToPoint(Formation f, Formation target, Vec2 point)
        {
            var b = f.AI.GetBehavior<FlankChargeBehavior>();
            if (b != null) b.SetFlankTarget(target, point, target != null && target.CountOfUnits > 0);
        }

        // 轻骑掩护:驰向并咬住威胁我方步兵的敌骑(绕击行为指向它本身 = 主动拦截),区别于被动护侧。
        private static void SetIntercept(Formation f, Formation threat)
        {
            var b = f.AI.GetBehavior<FlankChargeBehavior>();
            if (b != null && threat != null) b.SetFlankTarget(threat, threat.CachedAveragePosition, true);
        }

    }
}
