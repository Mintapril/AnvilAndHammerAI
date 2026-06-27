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
        private readonly FormationScanner _scanner = new FormationScanner();
        private readonly List<IMoralePressure> _sources;
        private readonly AgentEffectApplier _applier = new AgentEffectApplier();

        private readonly Dictionary<Team, int> _counts = new Dictionary<Team, int>();
        private readonly List<FormationSnapshot> _buffer = new List<FormationSnapshot>(8);
        private TickGate _gate = new TickGate(TickInterval);
        private bool _announced;

        public FormationMoraleMissionLogic(FormationShockPool pool, RangedThreatSensor rangedSensor)
        {
            _pool = pool;
            _sources = new List<IMoralePressure>
            {
                new CasualtyPressure(),
                new CascadePressure(),
                new EncirclementPressure(),
                new RangedFirePressure(rangedSensor),  // 受远程攻击(箭矢/标枪落点)
                new ChargeShockPressure(),             // 冲锋震慑(高速逼近的近战骑兵)
                // 扩展点:骑兵 AI 重写后 → new CavalryChargePressure(chargeProbe),(读 facing/heavy/size/tier 算 Δ)
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
            float ratchetDecay = s.RatchetDecayPerBreak;
            float routFloor = MoraleTuning.PanicFloorMorale;
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
                    FormationSnapshot snap = _scanner.Scan(f, team);
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
                    // 池封顶(阈×tier抗性×PoolCapFactor):防平直衰减下池无界增长到 100+,使威胁减弱后能在几秒内衰减到
                    // 解锁线(阈×0.5),整队得以正常集结恢复;持续被围殴者仍贴顶=不恢复(符合预期)。
                    float poolCap = MoraleTuning.RoutThreshold(threshold, snap.AvgTier) * MoraleTuning.PoolCapFactor;
                    if (st.Pool > poolCap) st.Pool = poolCap;
                    if (st.Pool > Telemetry.PoolPeak) Telemetry.PoolPeak = st.Pool;

                    Decision d = Decide(st, threshold, snap.AvgTier, now, letGo, ratchet, rallyDelay);

                    // 战场事件:整队溃逃/重整播报(左下角)。对双方均播,标我方/敌军。
                    bool isEnemy = m.PlayerTeam != null && team.IsEnemyOf(m.PlayerTeam);
                    if (d == Decision.Rout) BattleNarrator.OnRout(snap.Formation, isEnemy);
                    else if (d == Decision.Rally) BattleNarrator.OnRally(snap.Formation, isEnemy);

                    // 集结目标士气随编队棘轮档数衰减(越打越脆:同样集结只能恢复到越来越低的士气)。
                    float effRallyFloor = ratchet && st.RatchetLevel > 0
                        ? rallyFloor * (float)Math.Pow(ratchetDecay, st.RatchetLevel)
                        : rallyFloor;

                    // 逐兵单遍:Rout 压低 / Rally 拉回(决策与档数已在编队级算好);零捕获(applier 缓存委托)
                    _applier.Begin(d, routFloor, effRallyFloor);
                    _applier.Run(snap.Formation);

                    st.Prev = snap;
                    st.HasPrev = true;
                }

                // ── Pass C:队级集结清扫(召回脱编逃兵;Pass B 的编队成员遍历碰不到它们) ──
                RallySweep(team, rallyFloor, rallyDelay, now, letGo);
            }
        }

        /// <summary>
        /// 决策(纯函数 + 令牌锁存 + 编队级棘轮)。**tier 抗性缩放阈值**(高 tier 编队更难崩);池越阈的**上升沿**触发一次整队溃逃,
        /// 同时给该编队棘轮升档(满档触底);驻留令牌冷却内既不重复溃逃也不集结;池回落到 阈×ClearFactor 解锁;
        /// 已触底编队永不集结;非冷却、非触底、非大势已去、且距上次整队溃逃满集结延迟 → 整队集结。
        /// </summary>
        private static Decision Decide(FormationShockState st, float baseThreshold, float avgTier, float now,
            bool letGo, bool ratchet, float rallyDelay)
        {
            float threshold = baseThreshold * TierResist(avgTier);
            bool over = st.Pool >= threshold;

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
                if (st.Pool < threshold * MoraleTuning.RoutLatchClearFactor)
                    st.RoutLatched = false;     // 解锁:可再溃 / 可集结
                else
                    return Decision.None;        // 冷却:不重复压已 panic 成员(防高频翻转喂棘轮飞速触底)
            }
            if (st.Bottomed) return Decision.None;                       // 触底:整队不再集结
            if (letGo) return Decision.None;                            // 大势已去:不为其集结
            if (now - st.LastRoutTime < rallyDelay) return Decision.None; // 集结延迟(编队级):刚溃逃不立刻拉回
            return Decision.Rally;
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
                if (home != null && _pool.TryGet(home, out FormationShockState hst))
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

        /// <summary>
        /// 逐兵效果施加器(编排胶水)。**可复用 + 缓存委托**:稳态 0 堆分配。
        /// 决策(Rout/Rally/None)、棘轮档数、集结延迟都已在编队级算好;这里只按决策把整队同一动作落到每个兵。
        /// </summary>
        private sealed class AgentEffectApplier
        {
            private readonly Action<Agent> _visit;

            private Decision _d;
            private float _routFloor, _rallyFloor;

            public AgentEffectApplier() { _visit = Visit; }

            public void Begin(Decision d, float routFloor, float rallyFloor)
            {
                _d = d; _routFloor = routFloor; _rallyFloor = rallyFloor;
            }

            public void Run(Formation f) => f.ApplyActionOnEachUnit(_visit);

            private void Visit(Agent a)
            {
                if (a == null || !a.IsHuman) return;
                var cai = a.CommonAIComponent;
                if (cai == null) return;

                if (_d == Decision.Rout)
                    MoraleEffects.RoutAgent(a, _routFloor);
                else if (_d == Decision.Rally)
                    MoraleEffects.RallyAgent(a, cai, _rallyFloor); // RallyAgent 内部只拉"正在逃"的兵
            }
        }
    }
}
