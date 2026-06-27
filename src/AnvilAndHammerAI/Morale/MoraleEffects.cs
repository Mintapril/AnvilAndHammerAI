using AnvilAndHammerAI.Logging;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Morale
{
    /// <summary>
    /// 编队级决策落到 per-agent 的**唯一写入边界**(ADR-0004/0009 契约最敏感处,集中此处便于审计)。
    /// 两条硬约束钉死在这里:
    ///   · 溃逃 = 逐兵把士气压到 panic **小正数下限**(>0),**绝不 SetMorale(0)**、绝不整队原子布尔崩溃;
    ///   · 集结 = 逐兵 <see cref="CommonAIComponent.StopRetreating"/> + 抬士气(抬士气**不能**止溃,只有 StopRetreating 能清锁存)。
    /// 范围(ScopeFilter)与 tick 门控由编排层在调用前完成,这里只管"怎么写一个 agent"。
    /// </summary>
    public static class MoraleEffects
    {
        /// <summary>溃逃:把单兵士气压到 floor(只降不升)。floor &lt; 哨兵阈视为误设 0 → 拒写并 Log.Error。</summary>
        public static void RoutAgent(Agent a, float floor)
        {
            if (floor < MoraleTuning.PanicFloorMin)
            {
                Log.Error($"[formrout] panic 下限 {floor:0.00} < {MoraleTuning.PanicFloorMin:0.00},拒写防误设 SetMorale(0)。");
                return;
            }
            if (AgentComponentExtensions.GetMorale(a) > floor)
                AgentComponentExtensions.SetMorale(a, floor);
            Telemetry.FormRoutAgents++;
        }

        /// <summary>集结:仅对正在逃的单兵 StopRetreating + 抬士气到 floor(对冲 StopRetreating 内部钳位)。</summary>
        public static void RallyAgent(Agent a, CommonAIComponent cai, float floor)
        {
            bool fleeing = cai.IsRetreating || cai.IsPanicked || a.IsRunningAway;
            if (!fleeing) return;
            cai.StopRetreating();
            // 兜底:cai.StopRetreating 仅在 IsRetreating 时才清 agent 级 IsRunningAway,二者可能不同步
            // (个体已跑到地图边缘后 IsRetreating 可能已清、IsRunningAway 仍挂着 → AutoFormation 持续跳过它、永不归队)。
            if (a.IsRunningAway) a.StopRetreating();
            if (AgentComponentExtensions.GetMorale(a) < floor)
                AgentComponentExtensions.SetMorale(a, floor);
            Telemetry.RallyCount++;
        }
    }
}
