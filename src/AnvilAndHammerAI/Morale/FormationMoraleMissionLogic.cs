using System;
using System.Collections.Generic;
using AnvilAndHammerAI.Detection;
using AnvilAndHammerAI.Logging;
using AnvilAndHammerAI.Settings;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Morale
{
    /// <summary>
    /// 编队级士气编排层(替代旧 MoraleBackbone)。每 tick 跑单向管线:
    ///   传感器(单遍快照) → 压力源(Σ) → 池(累加+衰减+令牌) → 决策(Rout/Rally/None) → 效果(逐兵执行)。
    ///
    /// 这是 vanilla 没有的"编队级协调"层:池越阈 → 整队**协调溃逃**(逐兵压 panic 小正数下限 = 成片溃逃);
    /// 池回落 + 令牌解锁后 → 整队**协调集结**(逐兵 StopRetreating + 抬 floor,触底编队不拉)。
    /// 棘轮(C)也是**编队级**:每次该编队整队溃逃 +1 档,集结目标士气按档数衰减;满档触底 → 整队不再集结。
    /// 状态全在 FormationShockState(池里),无 per-agent 字典、无反射。
    ///
    /// 范围:**对双方恒生效**(编队级士气是本 mod 地基,与自动编队/统一指挥同属结构性,不受『只影响我方军队』限制)。
    /// 纯 MissionBehavior,**零 Harmony 补丁、零反射**(读写士气走公有 AgentComponentExtensions)。
    /// </summary>
    public sealed class FormationMoraleMissionLogic : MissionLogic
    {
        private enum Decision { None, Rout, Rally }

        private const float TickInterval = 0.5f;

        private readonly FormationShockPool _pool;
        private readonly ChargeImpactSensor _chargeSensor;
        private readonly FormationScanner _scanner = new FormationScanner();
        private readonly List<IMoralePressure> _sources;

        private readonly Dictionary<Team, int> _counts = new Dictionary<Team, int>();
        private readonly List<FormationSnapshot> _buffer = new List<FormationSnapshot>(8);
        private TickGate _gate = new TickGate(TickInterval);
        private bool _announced;
        private readonly Action<Agent> _bottomedPanic; // 触底(决定性崩溃)逐兵恐慌四散(缓存委托,稳态零分配)

        public FormationMoraleMissionLogic(FormationShockPool pool, RangedThreatSensor rangedSensor, ChargeImpactSensor chargeSensor)
        {
            _pool = pool;
            _chargeSensor = chargeSensor;
            _bottomedPanic = a =>
            {
                if (a == null || !a.IsHuman) return;
                var cai = a.CommonAIComponent;
                if (cai != null) MoraleEffects.PanicAgent(a, cai);
            };
            // 情势型源(反映"当前态势"):每拍 Sample → 积分进情势池 → 每秒衰减。
            // 伤亡(持久地板)与冲锋(真实冲击震慑)不在此列,由下方主循环分别直接计算/镜像。
            _sources = new List<IMoralePressure>
            {
                new CascadePressure(),
                new EncirclementPressure(),
                new RangedFirePressure(rangedSensor),  // 受远程攻击(箭矢/标枪落点)
            };
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled) return;
            Mission m = Mission.Current;
            if (m == null || !m.IsFieldBattle || m.Mode == MissionMode.Deployment) return;

            if (!_gate.Ready(dt, out float tickDt)) return; // tickDt = 真实经过时间(供差分/积分,非固定 0.5)
            float now = m.CurrentTime;

            if (!_announced) { _announced = true; Log.Info("[formmorale] 编队级士气层已激活(传感器→压力→池→决策→效果)。"); }

            // 让路启发式:各队活跃人数 + 总数(某队占全场比 < 阈 → 大势已去,不集结)。士气系统对双方恒生效,故分母计全部队伍。
            _counts.Clear();
            int total = 0;
            foreach (Team team in m.Teams)
            {
                int c = 0;
                foreach (Agent a in team.ActiveAgents) if (a.IsHuman) c++;
                _counts[team] = c; total += c;
            }
            if (total == 0) return;

            float threshold = s.PoolRoutThreshold;
            float decayPerSec = s.PoolDecayPerSecond;
            bool ratchet = s.RatchetEnabled;
            float rallyFloor = s.RallyMoraleFloor;
            float rallyDelay = s.RallyDelaySeconds;

            foreach (Team team in m.Teams)
            {
                bool letGo = _counts[team] < total * MoraleTuning.LetGoTeamShareThreshold;

                // ── Pass A:扫描本队各非空编队 → 快照缓冲 + 队级聚合 ──
                _buffer.Clear();
                int sumRouting = 0, sumCount = 0;
                foreach (Formation f in team.FormationsIncludingEmpty)
                {
                    if (f.CountOfUnits == 0) continue;
                    FormationSnapshot snap = _scanner.Scan(f);
                    if (snap.Count == 0) continue;
                    _buffer.Add(snap);
                    sumRouting += snap.RoutingCount;
                    sumCount += snap.Count;
                }
                var teamCtx = new TeamMoraleContext(sumRouting, sumCount);

                // ── Pass B:每编队 压力→池→决策→效果 ──
                for (int i = 0; i < _buffer.Count; i++)
                {
                    FormationSnapshot snap = _buffer[i];
                    FormationShockState st = _pool.GetOrCreate(snap.Formation);
                    FormationSnapshot prev = st.HasPrev ? st.Prev : snap;

                    // 援军重整旗鼓:已触底编队的原崩溃逐兵(每拍恐慌四散)逃尽/离队后,若被援军重新填充(出现非逃成员),
                    // 且距触底已过 BottomedRefillResetDelay(跳过触底瞬刻"已恐慌未起逃"的过渡)→ 重置崩溃状态,视作一支新战力,
                    // 否则援军一并入就被下方 _bottomedPanic 当场恐慌。(普通溃逃是整队后撤、成员不脱编,不走此路。)
                    if (st.Bottomed && now - st.LastRoutTime > MoraleTuning.BottomedRefillResetDelay
                        && snap.Count > snap.RoutingCount)
                        st.ResetCollapse();

                    float pressure = 0f;
                    for (int k = 0; k < _sources.Count; k++)
                    {
                        IMoralePressure src = _sources[k];
                        if (!src.IsEnabled) continue;
                        float v = src.Sample(snap, prev, teamCtx, tickDt);
                        if (v == 0f) continue;
                        pressure += v;
                        Telemetry.AddPressure(src.Tag, v);
                    }

                    st.Accumulate(pressure, tickDt);
                    st.Decay(decayPerSec, tickDt);
                    // 情势池封顶(阈×tier抗性×PoolCapFactor):防情势压力(级联/包围/远程)无界增长。
                    // 伤亡地板(持久)与冲锋震慑(慢衰减)各有独立边界,不在此封。
                    float poolCap = MoraleTuning.RoutThreshold(threshold, snap.AvgTier) * MoraleTuning.PoolCapFactor;
                    if (st.Pool > poolCap) st.Pool = poolCap;

                    // ① 伤亡溃逃压力(**可重整**)= ErosionGain × 自上次重整以来损失的"当前兵力"比 =(cf−基准)/(1−基准)。
                    //    tier3 该比达 35% → 31.5 = 阈值 → 溃(参考值 #1)。重整时 Decide 把基准归位到当前伤亡 → 此压力归零 → 可再战;
                    //    之后再损失"当前兵力"的 35% 才再溃(逐命更脆)。"接战过不回满"由士气条单独的存活比上限保证(见 MoraleReadout)。
                    float cf = 1f - snap.CasualtyRatio;
                    if (cf < 0f) cf = 0f;
                    // 溃逃窗口内不让伤亡读数"虚涨":逃散(未死)使 CountOfUnits 暂降、引擎 CasualtyRatio 虚高,
                    // 会把士气条上限(=存活比)瞬时压到空(用户报告的"触底后槽卡空")。仅在非溃逃态、或读数下降(真恢复)时更新;
                    // 真实阵亡导致的下降在集结后(CountOfUnits 恢复)正确体现。封顶语义("接战过不回满")不变。
                    if (!st.RoutLatched || cf < st.CasualtyFractionNow)
                        st.CasualtyFractionNow = cf;
                    float denom = 1f - st.CasualtyBaseline;
                    float sinceRally = denom > 1e-4f ? (cf - st.CasualtyBaseline) / denom : 0f;
                    if (sinceRally < 0f) sinceRally = 0f; else if (sinceRally > 1f) sinceRally = 1f;
                    float floor = MoraleTuning.CasualtyErosionGain * sinceRally;
                    if (floor > st.CasualtyFloor) Telemetry.AddPressure("cas", floor - st.CasualtyFloor);
                    st.CasualtyFloor = floor;

                    // ② 冲锋冲击震慑:镜像 ChargeImpactSensor(真实背/侧冲命中按**强度比**累加、慢衰减)。强度比已使其与编队大小无关(同兵力→Σ=1),无需再除人数。直接计入有效压力(不经情势池积分)。
                    st.ChargeShock = (_chargeSensor != null && s.ChargeShockPressureEnabled)
                        ? MoraleTuning.ChargeImpactGain * _chargeSensor.GetShock(snap.Formation) : 0f;
                    if (st.ChargeShock > Telemetry.PrChg) Telemetry.PrChg = st.ChargeShock;

                    if (st.Effective > Telemetry.PoolPeak) Telemetry.PoolPeak = st.Effective; // 峰值改记有效压力(池+地板+冲锋)

                    Decision d = Decide(st, threshold, snap.AvgTier, now, letGo, ratchet, rallyDelay);

                    // 战场事件:整队溃逃/重整播报(左下角)。对双方均播,标我方/敌军。
                    bool isEnemy = m.PlayerTeam != null && team.IsEnemyOf(m.PlayerTeam);
                    if (d == Decision.Rout)
                    {
                        BattleNarrator.OnRout(snap.Formation, isEnemy);
                        // 溃逃即**释放**冲锋震慑:一次冲锋把编队冲垮后,其震慑已"兑现",不再滞留(慢衰减)逼它反复再溃。
                        // → 保证单次冲锋后能恢复重整;3 次累加(每次未溃)仍有效,因未溃则不释放。新的冲撞命中会重新累加。
                        _chargeSensor?.Discharge(snap.Formation);
                        st.ChargeShock = 0f;
                    }
                    else if (d == Decision.Rally) BattleNarrator.OnRally(snap.Formation, isEnemy);

                    // 溃逃的"效果"**不再逐兵 Panic**(逐兵 Panic 会触发引擎 OnFleeing 把兵踢出编队 → 集结再也关联不回 → 亚秒抖动)。
                    // 改为**编队级**:RoutLatched 由指挥调度器读取 → 整队有序后撤(ThreatReactionBehavior.FallBack,见 CommandScheduler.HoldRout);
                    // 集结(RoutLatched 解除)即调度器恢复战斗行为。编队全程成建制(Pass A 仍扫描它)→ 脱离火力后情势池正常衰减解锁集结。
                    // 故此处对成员零写入;脱编的 vanilla 个体恐慌散兵仍由下方 RallySweep 召回归队。
                    // **唯一例外——决定性崩溃(触底)**:逐兵恐慌四散(IsRunningAway → 吃追击伤害加成)。触底编队不集结
                    // (Decide/RallySweep 均跳过 Bottomed),故 Panic 后无脱编↔召回对冲。调度器此时也不接管该编队(见 Drive)。
                    if (st.Bottomed)
                        snap.Formation.ApplyActionOnEachUnit(_bottomedPanic);

                    st.Prev = snap;
                    st.HasPrev = true;
                }

                // ── Pass C:队级集结清扫(召回脱编逃兵;Pass B 的编队成员遍历碰不到它们) ──
                RallySweep(team, rallyFloor, rallyDelay, now, letGo);
            }
        }

        /// <summary>
        /// 决策(纯函数 + 令牌锁存 + 编队级棘轮)。**tier 抗性缩放阈值**(高 tier 更难崩);有效压力越阈的**上升沿**触发一次整队溃逃,
        /// 并给该编队棘轮升档(满档触底)。**解锁是时间驱动的**:溃逃后维持集结延迟即解锁 → 任何单次溃逃都可恢复重整(无论伤亡/冲锋多重);
        /// 持久伤亡地板/冲锋震慑不再永久锁死,只让重整后更脆、更易反复再溃 → 反复再溃才经棘轮触底(决定性崩溃,保留功能)。
        /// 已触底/大势已去 → 不集结;否则(延迟已过)→ 整队集结。
        /// </summary>
        private static Decision Decide(FormationShockState st, float baseThreshold, float avgTier, float now,
            bool letGo, bool ratchet, float rallyDelay)
        {
            float threshold = baseThreshold * TierResist(avgTier);
            bool over = st.Effective >= threshold; // 有效压力 = 情势池 + 持久伤亡地板 + 冲锋冲击震慑

            if (over && !st.RoutLatched)
            {
                st.RoutLatched = true;
                st.LastRoutTime = now;
                Telemetry.FormRoutEdges++;
                if (ratchet)
                {
                    st.RatchetLevel++;                          // 编队级棘轮:每整队溃逃一次升一档
                    Telemetry.BreakCount++;
                    if (!st.Bottomed && st.RatchetLevel >= MoraleTuning.RatchetBottomLevel)
                    {
                        st.Bottomed = true;                     // 满档触底:整队彻底崩溃,不再集结
                        Telemetry.BottomedCount++;
                    }
                }
                return Decision.Rout;
            }
            if (st.RoutLatched)
            {
                // 解锁(集结)需**双闸**:① 时间——溃逃后至少维持 rallyDelay;② 迟滞——**情势池**(远程/级联/包围)须跌回
                //   阈值×RoutClearFactor 才解锁。缺②会在持续箭雨下"8s 一到就集结、下一拍 Effective≥阈又立即再溃"——
                //   每拍整队 Panic↔Rally 对冲、反复抖动+卡顿(实测 [tele] rally/routAgents 同窗暴涨到上千)。
                //   只看**情势池**(不含伤亡地板/冲锋震慑):伤亡地板集结时归零、不该永久锁死解锁;故持续受击的编队保持逃散,
                //   待火力退去(池衰减到 阈×ClearFactor)才集结;伤亡导致的持久压力仍能在火力停后正常集结(既往不咎)。
                if (now - st.LastRoutTime < rallyDelay) return Decision.None;                   // ① 溃散时间窗:仍在逃
                if (st.Pool >= threshold * MoraleTuning.RoutClearFactor) return Decision.None;  // ② 情势压力未退:保持溃逃,不集结(防抖)
                st.RoutLatched = false;                                                         // 双闸过 → 解锁,落到下方集结
                st.CasualtyBaseline = st.CasualtyFractionNow;                                   // 重整即"既往不咎":伤亡基准归位 → 伤亡溃逃压力归零,可再战
            }
            if (st.Bottomed) return Decision.None;   // 反复溃逃达档 → 彻底崩溃,不再集结(保留功能;单次溃逃不受此限)
            if (letGo) return Decision.None;          // 大势已去:不为其集结
            return Decision.Rally;                    // 重整(逐兵 StopRetreating + 抬士气;RallyAgent 只拉在逃的兵)
        }

        /// <summary>
        /// tier 抗性:溃逃阈值乘子 = clamp(1 + (avgTier − baseline) × perTier, min, max)。
        /// 高 tier 编队阈值更高 → 同样的震慑池更难把它压崩(士气更不易受影响);低 tier 更脆。
        /// </summary>
        private static float TierResist(float avgTier) => MoraleTuning.TierResist(avgTier);

        /// <summary>
        /// 队级集结清扫(Pass C):原版**个体**士气崩溃的兵会脱离编队、跑向地图边缘,且 <see cref="AutoFormationMissionLogic"/>
        /// 跳过 IsRunningAway 兵 → 它们永不归队;Pass B 只遍历编队成员,根本碰不到这些脱编逃兵(诊断里表现为
        /// 各编队 routing=0、但 side 级 fleeingFrac 持续不降,且各编队人数之和 < side 总数)。这里直接扫 team.ActiveAgents
        /// 对它们 StopRetreating(清 IsRunningAway + 恢复士气),下一拍 AutoFormation 即可把它们重新归入 7 编队。
        /// 大势已去(letGo)不强拉;母编队正溃逃/已触底/集结延迟内则尊重编队级决策、不召回。
        /// </summary>
        private void RallySweep(Team team, float rallyFloor, float rallyDelay, float now, bool letGo)
        {
            if (letGo) return;
            foreach (Agent a in team.ActiveAgents)
            {
                if (a == null || !a.IsHuman) continue;
                CommonAIComponent cai = a.CommonAIComponent;
                if (cai == null) continue;
                if (!(a.IsRunningAway || cai.IsRetreating || cai.IsPanicked)) continue;
                Formation home = a.Formation;
                // 脱编逃兵(真逃跑后引擎 OnFleeing 置 a.Formation=null):**不召回**。新模型下脱编逃兵只剩两类——
                // 决定性崩溃(触底)逐兵恐慌 / vanilla 个体恐慌散兵,二者都该任其溃散。普通溃逃是整队后撤(不脱编),本就不经此处。
                // 若仍召回会与触底逐兵恐慌每拍 StopRetreating↔Panic 对冲 → 每 0.5s Panic 风暴卡顿(已确认)。
                if (home == null) continue;
                if (_pool.TryGet(home, out FormationShockState hst))
                {
                    if (hst.RoutLatched || hst.Bottomed) continue;     // 母编队正溃逃/已触底 → 尊重编队级决策,不召回
                    if (now - hst.LastRoutTime < rallyDelay) continue;  // 编队级集结延迟内 → 不召回
                }
                MoraleEffects.RallyAgent(a, cai, rallyFloor);
            }
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();
            _pool.Reset();
        }
    }
}
