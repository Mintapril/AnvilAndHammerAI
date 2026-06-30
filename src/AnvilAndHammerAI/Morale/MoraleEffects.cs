using AnvilAndHammerAI.Logging;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Morale
{
    /// <summary>
    /// 士气层落到 per-agent 的写入边界。**溃逃不再逐兵 Panic**(那会触发引擎 OnFleeing 把兵踢出编队、令集结无法关联回去 → 亚秒抖动);
    /// 溃逃改为**编队级整体后撤**,由指挥调度器读 RoutLatched 驱动(见 CommandScheduler.HoldRout)。本类只剩**集结**一条:
    /// 把脱编的(主要是 vanilla 个体恐慌的)散兵 <see cref="CommonAIComponent.StopRetreating"/> + 抬士气,好让 AutoFormation 归队。
    /// 不变量(ADR-0004/0009):抬士气**不能**止溃、只有 StopRetreating 能清溃逃;且绝不 SetMorale(0)。
    /// </summary>
    public static class MoraleEffects
    {
        /// <summary>
        /// 决定性崩溃(触底)逐兵恐慌:仅对**尚未恐慌**者压士气到 floor + 引擎 <see cref="CommonAIComponent.Panic"/>(四散溃逃,IsRunningAway → 吃追击伤害加成)。
        /// 仅用于触底编队(不集结 → 无脱编↔召回对冲);floor 严格 &gt;0,绝不 SetMorale(0)。幂等,对已恐慌者零开销。
        /// </summary>
        public static void PanicAgent(Agent a, CommonAIComponent cai)
        {
            if (cai == null || cai.IsPanicked) return;
            float floor = MoraleTuning.PanicFloorMorale;
            if (AgentComponentExtensions.GetMorale(a) > floor)
                AgentComponentExtensions.SetMorale(a, floor);
            cai.Panic();
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
