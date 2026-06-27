namespace AnvilAndHammerAI.Formations
{
    /// <summary>
    /// 遇袭自发反应层(reaction override)的内部常量。随实测在代码里调;总开关在 AnvilSettings 暴露。
    /// 与编队级士气系统的 MoraleTuning 同风格:高杠杆项集中此处,便于在游反推。
    /// </summary>
    public static class ReactionTuning
    {
        // 触发:敌"近战骑兵"编队进入此水平距离(m)即视为对本编队的"急性冲锋威胁"。
        // 比 cav-cover 巡逻判定的 60m 更近(更贴脸才反应),避免远处骑兵晃一下就打断计划。
        public const float AcuteCavRange = 30f;
        public const float ArcherFallbackRange = 28f; // 弓兵/骑射:略大,早一点撤(它们扛不住贴身)

        // 显著性门:敌骑兵力值(FormationStrength,tier 加权)≥ 此比例 × 本编队兵力值 才算威胁(去噪)。
        public const float InfantryBraceCavRatio = 0.10f;  // 步兵架枪:门槛低,有点骑兵就该结阵受冲
        public const float ArcherFallbackCavRatio = 0.05f; // 弓兵:几乎任何近战骑兵贴近都该撤
        public const float CavCounterCavRatio = 0.15f;     // 骑兵反冲:门槛略高,免为小股骑兵打乱预备/放锤时序

        // 迟滞:威胁消失后仍保持反应姿态的时长(s)。进反应即时、出反应延迟,防"反应↔常规"每拍抖动。
        public const float HoldSeconds = 2f;

        // 骑兵反冲蓄势(后撤-冲锋迎击):来袭骑兵近于此则先后撤拉开一次冲锋距离再冲(只一次,latch 防抖)。
        public const float CounterWindupDist = 14f;
        public const float CounterRunup = 22f;

        // 弓兵后撤无集结点(我方无主步兵)时,直接背离威胁后撤的距离(m)。
        public const float FallbackAwayDistance = 30f;

        // 反应行为权重:高于一切常规角色权重(常规最高为放锤迂回突击权重 2f),确保被选为 ActiveBehavior。
        public const float ReactionWeight = 3f;
    }
}
