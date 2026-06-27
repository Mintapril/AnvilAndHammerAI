namespace AnvilAndHammerAI.Morale
{
    /// <summary>
    /// 不经 MCM 暴露的内部常量(随实测在代码里调;高杠杆项已在 AnvilSettings 暴露)。
    /// 士气标度待 CustomBattle 实测确认 —— RBM 设初始士气 Clamp(...,15,100) 说明大致 0–100。
    /// </summary>
    public static class MoraleTuning
    {
        // C 棘轮(编队级):该编队累计整队溃逃满此档数 → 触底=不再集结(决定性崩溃)。约 3 次成片崩即放弃。
        // 去抖由池的越阈令牌(RoutLatched)负责:池须回落到 阈×ClearFactor 才解锁,故两次整队溃逃天然有间隔。
        public const int RatchetBottomLevel = 3;
        // B/D 让路:某队活跃人数占比低于此 → 视为大势已去,不再为其集结(让其自然崩溃,避免与 RBM 残局收尾拉锯)。
        public const float LetGoTeamShareThreshold = 0.12f;

        // ---------------- F 编队级震慑池(全部为校准占位,待在游 [tele F]/[pool] 日志反推) ----------------
        // 池机制:Σ压力(/秒)按 dt 积分入池,每秒衰减;越 阈×tier抗性 → 整队协调溃逃 + 令牌驻留冷却。
        public const float PoolDecayPerSecondDefault = 5f;   // 池每秒衰减(MCM 可覆盖)。实测 decay=10 把池冲到 poolPeak≈4(涨不起来→永不溃逃);decay=4 能涨能溃。取 5 折中。
        public const float PoolRoutThresholdDefault = 30f;   // 越此触发整队溃逃(MCM 可覆盖)。实测 30 配 decay≈4~5 在持续围殴下能溃;45 过高永远够不着。
        public const float RoutLatchClearFactor = 0.5f;      // 池降到 阈×此 → 解锁令牌(可再溃/可集结)
        // **池上限** = 阈×tier抗性×此。关键结构性约束:平衡掉"平直衰减无平衡点 → 持续压力把池冲到 100+ → 溃后永远跌不回解锁线"。
        // 封顶后,威胁一旦减弱,池能在几秒内衰减到解锁线 → 整队可正常集结恢复(被持续围殴的编队仍贴顶=不恢复,符合预期)。
        public const float PoolCapFactor = 1.25f;

        // tier 抗性:按编队平均兵种 tier(Agent.Character.GetBattleTier)缩放**溃逃阈值**——高 tier 更难崩。
        // tierResist = clamp(1 + (avgTier − baseline) × perTier, min, max);阈值 ×tierResist。
        public const float TierResistBaseline = 2.5f;  // 基准 tier(此处 ×1.0,高于更抗、低于更脆)
        public const float TierResistPerTier = 0.10f;  // 每偏离基准 1 级的阈值乘子增量(原 0.18 太陡:精锐军阈值被顶到 ~阈×1.8,几乎不崩)
        public const float TierResistMin = 0.6f;       // 低 tier 脆性下限
        public const float TierResistMax = 1.3f;       // 高 tier 抗性上限(原 1.8 太硬:配合高阈值令精锐编队永不溃逃)

        /// <summary>tier 抗性乘子(编队级士气系统与指挥层共用的单一来源)。</summary>
        public static float TierResist(float avgTier)
        {
            float r = 1f + (avgTier - TierResistBaseline) * TierResistPerTier;
            if (r < TierResistMin) r = TierResistMin;
            else if (r > TierResistMax) r = TierResistMax;
            return r;
        }

        /// <summary>编队实际崩溃(整队溃逃)阈值 = 基阈 × tier 抗性。指挥层据此判"敌军逼近崩溃"。</summary>
        public static float RoutThreshold(float baseThreshold, float avgTier) => baseThreshold * TierResist(avgTier);

        // 溃逃执行:逐兵压到的"panic 小正数下限"——**绝不 SetMorale(0)**(ADR-0004 决策4③)。
        // 关键(反编译核实):vanilla 个体 panic 触发于 CommonAIComponent `_morale < 0.01f`(标度 0–100),
        // 故下限必须 **< 0.01** 才能真触发 panic/retreat;同时严格 >0 守住"非 SetMorale(0)"契约(0.005>0,ClampFloat 后仍非 0)。
        // 不变量:PanicFloorMin < PanicFloorMorale < 0.01。
        public const float PanicFloorMorale = 0.005f;        // 压到的目标士气(< 0.01 panic 阈,> 0)
        public const float PanicFloorMin = 0.001f;           // 哨兵:只拦 ≤ 此(≈0/负)的误设;合法 panic 下限(<0.01)必须放行

        // 三个通用压力源的增益/阈(骑兵震慑回归前的驱动力)。
        public const float CasualtyGain = 50f;    // 伤亡比上升速率(/秒)→ 压力 的增益(原 120 过激,正面对耗即飙压)
        public const float CascadeGain = 12f;     // 邻队溃逃比例 → 压力 的增益
        public const float CascadeNeutral = 0.15f;// 邻队溃逃低于此不施压(防早期噪声)
        public const float CascadeCap = 8f;       // 级联压力/秒上限(饱和,防正反馈雪崩自激)
        // ③ 被包围:**几何方向**——把 30m 内敌兵按方位角分扇区,数"有威胁的方向数"(occupiedSectors),
        // 需 ≥ MinSectors 个方向才算被夹击;压力 = 增益 × 覆盖度(占用扇区/总扇区) × 局部以多打少(capped)。
        // 用粗扇区 + 每扇区最小敌数去噪,避开 RMS 那种脆弱细几何。
        public const int EncircleSectorCount = 8;     // 扇区数(45°/扇区);改它需同步 FormationScanner 的缓冲大小
        public const int EncircleMinPerSector = 2;    // 一个方向需 ≥ 此敌数才算"该方向有威胁"(去噪)
        public const int EncircleMinSectors = 4;      // 需 ≥ 此个方向被占 才算"被夹击"(原 3 太松:正面+两翼接战就误判被围;4=半数方向有敌才算真被围)
        public const float EncircleDensityCap = 2f;   // 局部以多打少倍率上限(防一圈散兵把大编队顶满)
        public const float EncircleGain = 2.5f;       // 覆盖度×密度 → 压力/秒 的增益(原 4 偏高)

        // ④ 受远程攻击(箭矢/标枪落点;RangedThreatSensor 监听 OnMissileHit 累加威胁强度,指数衰减成短窗)。
        public const float RangedHitBodyWeight = 1.0f;     // 直接命中士兵
        public const float RangedHitShieldWeight = 0.5f;   // 命中盾牌(挡下,威慑较轻)
        public const float RangedNearMissWeight = 0.25f;   // 落在编队附近的未命中箭矢/标枪
        public const float RangedNearMissRadius = 6f;      // 未命中落点归入最近受射编队的半径(m)
        public const float RangedThreatDecayPerSecond = 1.5f; // 威胁强度指数衰减率(/秒;窗口≈1/率)
        public const float RangedGain = 1.5f;              // 威胁强度 → 压力/秒 的增益

        // ⑤ 冲锋震慑(高速逼近、距离较近的近战骑兵编队;逼近速度由快照 now/prev 距离差分得)。
        public const float ChargeShockDistance = 40f;        // 到最近敌近战骑兵编队 < 此 才算"距离较近"
        public const float ChargeShockMinClosingSpeed = 2f;  // 逼近速度 ≥ 此(m/s)才算"高速靠近"(<0=在远离)
        public const float ChargeShockMaxClosingSpeed = 12f; // 逼近速度上限(钳制,防 spawn/瞬移假触发)
        public const float ChargeShockGain = 2f;             // (逼近速度 × 贴近度0..1)→ 压力/秒 的增益(原 3 偏高)
    }
}
