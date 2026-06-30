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
        // 援军重整:已触底编队的原崩溃逐兵逃尽后,若被援军重新填充(有非逃成员),且距触底已过此秒数(跳过触底瞬刻"已恐慌未起逃"过渡)
        // → 重置崩溃状态、视作新战力(免援军一并入即被恐慌)。见 FormationMoraleMissionLogic Pass B。
        public const float BottomedRefillResetDelay = 2f;
        // B/D 让路:某队活跃人数占比低于此 → 视为大势已去,不再为其集结(让其自然崩溃,避免与 RBM 残局收尾拉锯)。
        public const float LetGoTeamShareThreshold = 0.12f;

        // ---------------- F 编队级震慑池(全部为校准占位,待在游 [tele F]/[pool] 日志反推) ----------------
        // 池机制:Σ压力(/秒)按 dt 积分入池,每秒衰减;越 阈×tier抗性 → 整队协调溃逃 + 令牌驻留冷却。
        public const float PoolDecayPerSecondDefault = 3f;   // 瞬时情势池(级联+包围+远程)每秒衰减(MCM 可覆盖)。只作用于"情势池";伤亡地板/冲锋震慑各有自己的持久/慢衰减,不受它影响。
        public const float PoolRoutThresholdDefault = 30f;   // 有效压力(情势池+伤亡地板+冲锋震慑)越此 → 整队溃逃(MCM 可覆盖)。tier3 有效阈 = 30×1.05 = 31.5(伤亡地板 90×35% 恰好够到,参考值 #1)。
        // **池上限** = 阈×tier抗性×此。关键结构性约束:平衡掉"平直衰减无平衡点 → 持续压力把池冲到 100+ → 溃后永远跌不回解锁线"。
        // 封顶后,威胁一旦减弱,池能在几秒内衰减到解锁线 → 整队可正常集结恢复(被持续围殴的编队仍贴顶=不恢复,符合预期)。
        public const float PoolCapFactor = 1.25f;
        // 溃逃解锁迟滞:已溃编队须等**情势池**(远程/级联/包围)跌回 阈值×此因子,才允许集结(与时间闸 RallyDelay 取且)。
        // 防"持续受击下 8s 一到就集结、下一拍又越阈再溃"的每拍抖动。0.5 = 火力/包围明显退去才重整(贴合 ARCHITECTURE 的迟滞设计)。
        public const float RoutClearFactor = 0.5f;

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

        // ① 伤亡溃逃压力(**可重整**)= CasualtyErosionGain × 自上次重整以来损失的"当前兵力"比 =(伤亡比 − 重整基准)/(1 − 重整基准)。
        // 标定:tier3 该比 35% → 90×0.35 = 31.5 = tier3 有效阈值 → 整队溃逃(用户参考值 #1)。
        // 重整时基准归位 → 此压力归零、可再战;之后再损失"当前兵力"的 35% 才再溃(逐命更脆)。
        // "接战过不可能满士气"由士气条单独的**存活比上限**保证(见 MoraleReadout),与溃逃解耦。
        public const float CasualtyErosionGain = 90f;

        // 其余通用压力源的增益/阈。
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
        // 受到的 N×10 发 = 命中 + 盾挡 + **落附近的未命中**(都计入)。命中率"5 发命中一个"(1/5):1/5 命中(造成伤亡,权重1.0)+ 4/5 未命中(盾挡/近失,权重0.25)。
        public const float RangedHitBodyWeight = 1.0f;     // 命中士兵(造成伤亡)= 1.0
        public const float RangedHitShieldWeight = 0.25f;  // 命中盾牌 → 按"未命中"算(挡下没伤,威慑同近失)= 0.25
        public const float RangedNearMissWeight = 0.25f;   // 落在编队附近的未命中(也算"受到攻击",威慑较轻;按 6m 半径归属)
        public const float RangedNearMissRadius = 6f;      // 未命中落点归入最近受射编队的半径(m)
        // 威胁衰减 λ_t = ln2/20 → **半衰期 20s**:远程威胁 = 20 秒窗口的剂量累加器(匹配 #4"20秒内"——剂量而非速率,否则~2s 收 N 发即溃,灵敏 10 倍)。
        public const float RangedThreatDecayPerSecond = 0.034657f;
        // 每兵威胁(已按人数归一化)→ 情势池压力/秒 的增益。原"满额校准"曾反解到 32.75(与伤亡源等量、且唯一无单独上限,
        // 致挨射即可独力把池顶到天花板压垮整队);先削到 8 仍偏强,再砍到 0.25 又偏弱(挨射几乎不影响士气)。
        // 现按用户实测调至 **5.0**(= 0.25 的 20 倍,介于早期 8 与 2 之间):挨射对士气有明显但不压倒性的压制。
        // 玩家可经 MCM「挨射士气影响」百分比(RangedPressureIntensity,默认 100%)在此基线上自调。
        public const float RangedGain = 5.0f;

        // ⑤ 冲锋冲击(**真实背/侧冲命中**;由 ChargeImpactSensor 监听 OnAgentHit 的 IsHorseCharge 累加,慢衰减)。
        // 不再用"逼近距离/速度"代理(那测的是恐吓、非冲击)。每次冲撞命中按 角色×方向 计入"冲击单位",
        // ChargeShock = 累加的冲击单位(慢衰减),直接计入有效压力(不经情势池积分)。
        public const float ChargeHeavyFactor = 3f;      // 重骑(HeavyCavalry)每次冲撞命中的冲击权重(轻骑 = 1)
        public const float ChargeLightFactor = 1f;      // 轻骑(LightCavalry/Cavalry)每次冲撞命中的冲击权重
        public const float ChargeRearDirFactor = 1f;    // 背向冲撞:满额
        public const float ChargeSideDirFactor = 0.5f;  // 侧向冲撞:半额
        public const float ChargeFrontDirFactor = 0.15f;// 正面冲撞:小额(正面接战不是"被冲垮")
        public const float ChargeShockDecayPerSecond = 0.011552f; // 冲击震慑衰减 λ_c = ln2/60 → 半衰期 60s(匹配参考值 #3 的"1 分钟"窗口:间隔 >60s 的冲锋不充分累加)
        // ChargeShock = ChargeImpactGain × Σ(本次冲撞骑手强度 / 被冲编队强度) × 角色 × 方向。无界累加由"溃逃即释放"(Discharge)防止。
        // **精确反解**(强度比消去命中数 K):一次"同兵力"冲锋全连接 → Σ强度比 = 1。
        // #3(绑定,最坏均布 t=0/30/60s):3 次轻骑(角色×1)背冲,衰减留存 0.5+0.707+1 = 2.207 → ChargeImpactGain × 2.207 = 31.5 → 14.27。
        // #2(校验):1 次重骑(角色×3)背冲 = 14.27 × 3 = 42.8 ≥ 31.5 → 溃(重骑必然溢出 = 狠狠冲垮,符合直觉)。
        public const float ChargeImpactGain = 14.27f;
    }
}
