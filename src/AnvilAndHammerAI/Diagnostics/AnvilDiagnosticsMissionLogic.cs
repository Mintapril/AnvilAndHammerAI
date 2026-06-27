using AnvilAndHammerAI.Detection;
using AnvilAndHammerAI.Logging;
using AnvilAndHammerAI.Morale;
using AnvilAndHammerAI.Settings;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Diagnostics
{
    /// <summary>
    /// 只读诊断:每 5 秒记录各编队 平均士气/溃逃比例/数量 + **震慑池值/令牌态** + 各槽人数
    /// + **per-side 逃兵比例**(观测编队级溃逃是否把本方推过撤退判定闩),并 Flush 子系统心跳。
    /// 用单遍 FormationScanner(零分配);池只读自共享 FormationShockPool。
    /// </summary>
    public sealed class AnvilDiagnosticsMissionLogic : MissionLogic
    {
        private const float IntervalSeconds = 5f;

        private readonly FormationShockPool _pool;
        private readonly FormationScanner _scanner = new FormationScanner();
        private TickGate _gate = new TickGate(IntervalSeconds);
        private bool _announced;

        public AnvilDiagnosticsMissionLogic(FormationShockPool pool) { _pool = pool; }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);

            // 只野战(tick 时 IsFieldBattle 已可靠;OnMissionBehaviorInitialize 时不可靠)
            Mission fb = Mission.Current;
            if (fb == null || !fb.IsFieldBattle) return;
            // 总开关关闭则不写诊断日志(尊重"关闭后本 Mod 不再有任何动作")。
            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled) return;

            if (!_announced)
            {
                _announced = true;
                Log.Info("[diag] AnvilDiagnosticsMissionLogic 已挂载到当前 Mission。");
                Log.Info("[settings] " + (AnvilSettings.Instance?.Snapshot() ?? "<null>"));
            }

            if (!_gate.Ready(dt, out _)) return;

            Mission mission = Mission.Current;
            if (mission == null) return;

            foreach (Team team in mission.Teams)
            {
                // 各 FormationClass 槽人数(含空槽)
                var slots = new System.Text.StringBuilder();
                foreach (Formation f in team.FormationsIncludingEmpty)
                    slots.Append(f.FormationIndex).Append('=').Append(f.CountOfUnits).Append(' ');
                Log.Info($"[slot] side={team.Side} {slots.ToString().TrimEnd()}");

                // per-side 逃兵比例(B2 观测:编队级溃逃下本方同时逃比例)
                int sideRout = 0, sideCount = 0;
                foreach (Agent a in team.ActiveAgents)
                {
                    if (!a.IsHuman) continue;
                    sideCount++;
                    if (a.IsRunningAway) sideRout++;
                }
                if (sideCount > 0)
                    Log.Info($"[side] side={team.Side} n={sideCount} fleeingFrac={(float)sideRout / sideCount:0.00}");

                // 编队士气 + 震慑池
                foreach (Formation f in team.FormationsIncludingEmpty)
                {
                    if (f.CountOfUnits == 0) continue;
                    FormationSnapshot snap = _scanner.Scan(f, team);

                    float pool = 0f; bool latched = false;
                    if (_pool != null && _pool.TryGet(f, out var st)) { pool = st.Pool; latched = st.RoutLatched; }

                    Log.Info($"[diag] side={team.Side} class={f.FormationIndex} n={snap.Count} " +
                             $"avgMorale={snap.AvgMorale:0.0} routing={snap.RoutingFraction:0.00} " +
                             $"tier={snap.AvgTier:0.0} encSec={snap.OccupiedSectors} " +
                             $"pool={pool:0.0} latched={latched}");
                }
            }

            // 各子系统心跳计数(某计数=0 即该层没触发,见 Telemetry 注释)
            Telemetry.Flush(Log.Info);
        }
    }
}
