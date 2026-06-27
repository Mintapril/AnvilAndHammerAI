using System;
using AnvilAndHammerAI.Logging;
using AnvilAndHammerAI.Morale;
using AnvilAndHammerAI.Settings;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Safety
{
    /// <summary>
    /// D 战斗结束安全闸(阶段1:只温和保护,不含危险的保险栓 latch)。
    /// 主防线 = 本方未触底逃兵不 fade-out 移除(订阅 CanAgentRout 兜底)+ 主动集结(在 MoraleBackbone 内)。
    /// per-side:只护本方(IsFriendOf 玩家队)未触底者;敌方与已触底者一律放行,保证决定性崩溃。
    /// 危险的 BattleEndLogic.ChangeCanCheckForEndCondition(false) 会冻结整个结束判定(含 depleted)=软锁致命,
    /// 推迟到在游 CustomBattle 实测看门狗后再加(见 ADR-0004 ⑤)。
    /// </summary>
    public sealed class BattleEndSafetyMissionLogic : MissionLogic
    {
        private readonly FormationShockPool _pool;
        private Func<Agent, bool> _routCondition;

        public BattleEndSafetyMissionLogic(FormationShockPool pool) { _pool = pool; }

        public override void AfterStart()
        {
            base.AfterStart();
            // 在所有 behavior(含 RBM)初始化后订阅;末位 += 争 multicast Func 返回权。
            // RBM 实测不占用此 Func,这里仅冗余兜底。
            _routCondition = CanAgentRout;
            Mission.CanAgentRout_AdditionalCondition += _routCondition;
            Log.Info("[safety] 已挂 CanAgentRout 兜底谓词。");
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();
            if (_routCondition != null)
            {
                Mission.CanAgentRout_AdditionalCondition -= _routCondition;
                _routCondition = null;
            }
        }

        /// <summary>返回 false = 阻止该 agent fade-out 溃逃移除。只护"本方未触底"者。</summary>
        private bool CanAgentRout(Agent agent)
        {
            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled || !s.SafetyEnabled) return true;
            Mission m = Mission.Current;
            if (m == null || !m.IsFieldBattle) return true; // 只野战(谓词调用时 IsFieldBattle 已可靠)
            if (agent == null || !agent.IsHuman) return true;

            Team team = agent.Team;
            Team playerTeam = Mission.PlayerTeam;
            if (team == null || playerTeam == null) return true;
            if (!team.IsFriendOf(playerTeam)) return true; // 敌方:放行,让其自然溃散

            // 已触底(编队级棘轮满档)的逃兵:放行,让决定性崩溃照常发生。
            Formation f = agent.Formation;
            if (f != null && _pool.TryGet(f, out var st) && st.Bottomed) return true;

            Telemetry.RoutBlocked++;                         // 本方可恢复:阻 fade-out(待集结拉回)
            return false;
        }
    }
}
