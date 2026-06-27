using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using AnvilAndHammerAI.Detection;
using AnvilAndHammerAI.Logging;
using AnvilAndHammerAI.Settings;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Formations
{
    /// <summary>
    /// 自动编队管理器(ADR-0011 第 1 步:地基)。完全接管编队分配,取代 RBM/vanilla 的每 tick 重分:
    /// 每 0.5s 把每个活跃兵按 §A 分类规则放进 7 支角色编队之一,左右轻骑/主侧步兵按当前兵力值均衡,
    /// 角色一旦定下就缓存(CWT),re-assert 不再翻动。
    ///
    /// 配套 <see cref="FormationResortPatch"/> 压制 native/RBM 的 ManageFormationCounts 合并,避免它每 tick 把 7 角色编队合并回标准槽。
    /// 本层只管"谁在哪支编队";"哪支干什么"是后续中央调度器(自写 Tactic)的事——
    /// **故第 1 步编队会先发呆**(还没人给它们打开动作权重),这是预期,不是 bug(见 ADR-0011 §6)。
    ///
    /// 范围:受『只影响我方军队』(只玩家军时只编排玩家队;敌方留给 RBM/vanilla)。
    /// </summary>
    public sealed class AutoFormationMissionLogic : MissionLogic
    {
        private const float TickInterval = 0.5f;
        private const float SlotLogInterval = 5f;

        private TickGate _gate = new TickGate(TickInterval);
        private TickGate _logGate = new TickGate(SlotLogInterval);
        private bool _announced;

        private sealed class RoleBox { public TacRole Role; }
        private readonly ConditionalWeakTable<Agent, RoleBox> _roles = new ConditionalWeakTable<Agent, RoleBox>();

        // 角色累计兵力值(每队每拍重算自已缓存角色)。均衡基准用它,**不读实时编队成员计数**——
        // 因为 a.Formation = target 在同一拍内不会立即反映到 Formation.CountOfUnits,直接读会让首拍整批兵
        // 全看到"目标槽=0、原槽=满"的陈旧值而塌缩到同一角色(步兵不分主/包抄、轻骑不分左右)。
        private readonly Dictionary<TacRole, int> _tally = new Dictionary<TacRole, int>();

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled || !s.AutoFormationEnabled) return;
            Mission m = Mission.Current;
            if (m == null || !m.IsFieldBattle || m.Mode == MissionMode.Deployment) return;

            if (!_gate.Ready(dt, out _)) return;

            foreach (Team team in m.Teams)
            {
                if (!ScopeFilter.Applies(team)) continue; // 只玩家军时:只编排玩家队;敌方留给 RBM/vanilla

                BuildTally(team);                          // 从已缓存角色重算累计,作为本拍均衡基准

                foreach (Agent a in team.ActiveAgents)
                {
                    if (a == null || !a.IsHuman || a.IsRunningAway) continue;
                    if (PlayerControl.RespectEnabled && a == Agent.Main) continue; // 玩家主角不被自动归槽(由玩家/相机Mod说了算)

                    RoleBox box;
                    if (!_roles.TryGetValue(a, out box))
                    {
                        TacRole r = Resolve(a);            // 用累计均衡(非实时编队计数)
                        if (r == TacRole.Skip) continue;
                        box = new RoleBox { Role = r };
                        _roles.Add(a, box);
                        Add(r, AgentStrength(a));          // 计入累计,使同拍后续兵正确均衡
                    }

                    // §A 锁定:骑射弹尽 → 改并入兵力值较小一翼的轻骑(用累计均衡);一旦转入不再回头。
                    if (box.Role == TacRole.HorseArcher && TroopClassifier.MountedOutOfAmmo(a))
                    {
                        TacRole side = Tally(TacRole.LightCavLeft) <= Tally(TacRole.LightCavRight)
                            ? TacRole.LightCavLeft : TacRole.LightCavRight;
                        box.Role = side;
                        Add(side, AgentStrength(a));
                    }

                    Formation target = team.GetFormation(TroopClassifier.SlotFor(box.Role));
                    if (target == null || a.Formation == target) continue;
                    // 注:**编队组织是地基,不因玩家接管而跳过**——否则你用 RTS Camera/指挥盘控编队后,整支军队就永远不会被分成 7 队。
                    // "让出玩家命令"只作用于调度器的行为权重层(不覆盖你的战术指令);成员归槽(分队)始终进行。
                    // 玩家亲自接管的编队 IsAIControlled=false,RBM 重分本就不动它,故此处重排不会与 RBM 角力。
                    a.Formation = target;   // RBM 自验证过:每 tick 重写 agent.Formation 安全
                }
            }

            if (!_announced)
            {
                _announced = true;
                Log.Info("[autoform] 自动编队已激活(7 编队:主步/包抄步/弓/骑射/左轻骑/右轻骑/重骑;RBM 重分已禁用)。" +
                         "第 1 步编队先发呆,等中央调度器接管。");
            }

            if (_logGate.Ready(TickInterval, out _)) LogSlots(m);
        }

        /// <summary>粗分类 → 具体角色(左右轻骑/主侧步兵按**已分配累计**均衡,把新兵投向当前较小的一侧)。</summary>
        private TacRole Resolve(Agent a)
        {
            switch (TroopClassifier.Categorize(a))
            {
                case TacCategory.Archer: return TacRole.Archer;
                case TacCategory.HorseArcher: return TacRole.HorseArcher;
                case TacCategory.HeavyCav: return TacRole.HeavyCav;
                case TacCategory.LightCav:
                    return Tally(TacRole.LightCavLeft) <= Tally(TacRole.LightCavRight)
                        ? TacRole.LightCavLeft : TacRole.LightCavRight;
                case TacCategory.Infantry:
                    // 主步兵 : 包抄步兵 = 50 : 31(包抄略少)。按目标占比贪心填:谁的"已分配兵力值 / 目标权重"更低就投谁。
                    // flank 欠额 ⟺ flankTally/31 < mainTally/50 ⟺ flankTally*50 < mainTally*31。
                    return Tally(TacRole.InfantryFlank) * 50 <= Tally(TacRole.InfantryMain) * 31
                        ? TacRole.InfantryFlank : TacRole.InfantryMain;
                default: return TacRole.Skip;
            }
        }

        /// <summary>每队每拍重建累计:遍历已缓存角色的活兵,按角色累加其兵力值(= battle tier,与 FormationStrength 同口径)。</summary>
        private void BuildTally(Team team)
        {
            _tally.Clear();
            foreach (Agent a in team.ActiveAgents)
            {
                if (a == null || !a.IsHuman) continue;
                if (_roles.TryGetValue(a, out RoleBox box)) Add(box.Role, AgentStrength(a));
            }
        }

        private void Add(TacRole r, int str) { _tally.TryGetValue(r, out int cur); _tally[r] = cur + str; }
        private int Tally(TacRole r) => _tally.TryGetValue(r, out int v) ? v : 0;
        private static int AgentStrength(Agent a) => a.Character != null ? a.Character.GetBattleTier() : 0;

        private static void LogSlots(Mission m)
        {
            foreach (Team team in m.Teams)
            {
                var sb = new StringBuilder();
                foreach (Formation f in team.FormationsIncludingEmpty)
                    if (f.CountOfUnits > 0)
                        sb.Append(f.FormationIndex).Append('=').Append(f.CountOfUnits)
                          .Append(f.IsAIControlled ? "" : "(P)").Append(' '); // (P)=非 AI 控制(被玩家/RTS接管)
                Log.Info($"[autoform] side={team.Side} {sb.ToString().TrimEnd()}");
            }
        }
    }
}
