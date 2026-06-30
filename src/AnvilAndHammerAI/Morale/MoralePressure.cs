using AnvilAndHammerAI.Detection;
using AnvilAndHammerAI.Settings;

namespace AnvilAndHammerAI.Morale
{
    /// <summary>
    /// 压力源插件契约(编队级士气池的唯一扩展点)。各源把"伤亡/级联/包围/(未来)骑兵冲锋震慑"
    /// 以同构方式算成**瞬时压力速率(/秒)**,编排层求和后按 dt 积分入池。
    /// <para>新增一个压力源 = 新建一个实现类 + 在 <c>FormationMoraleMissionLogic</c> 的 List 里加一行,核心零改动。</para>
    /// Sample 是**无副作用纯函数**:只读快照(本编队 now/prev + 队级上下文),不持有 per-formation 状态
    /// (时间窗/历史已沉到快照差分),便于单独推理与替换。
    /// </summary>
    public interface IMoralePressure
    {
        string Tag { get; }   // 遥测归因短码(cas/csc/enc/...)
        bool IsEnabled { get; }
        float Sample(in FormationSnapshot now, in FormationSnapshot prev, in TeamMoraleContext team, float dt);
    }

    // 注:伤亡不再是"瞬时压力源"——已改为编排层每拍按 (1−存活比) 计算的**持久地板**(见 FormationMoraleMissionLogic)。
    // 冲锋不再是"逼近代理源"——已改为 ChargeImpactSensor 监听真实背/侧冲命中累加、编排层直接写入的 ChargeShock。
    // 故本文件只剩三个**情势型**源(级联/包围/远程):它们反映"当前态势",每拍 Sample → 积分进情势池 → 每秒衰减。

    /// <summary>
    /// ② 溃逃级联:同队**邻编队**的溃逃比例(饱和 + cap)。仅"邻→本",本编队自身溃逃**不回灌**自己的池,
    /// 防正反馈自激雪崩(本编队溃逃只经逐兵棘轮 + D 安全闸表达)。
    /// </summary>
    public sealed class CascadePressure : IMoralePressure
    {
        public string Tag => "csc";
        public bool IsEnabled => true;

        public float Sample(in FormationSnapshot now, in FormationSnapshot prev, in TeamMoraleContext team, float dt)
        {
            float neighbor = team.NeighborRoutingFraction(now);    // 排除自身后的邻队溃逃比(0..1)
            if (neighbor <= MoraleTuning.CascadeNeutral) return 0f;
            float v = MoraleTuning.CascadeGain * (neighbor - MoraleTuning.CascadeNeutral);
            return v > MoraleTuning.CascadeCap ? MoraleTuning.CascadeCap : v; // 饱和上限
        }
    }

    /// <summary>
    /// ③ 被包围(**几何方向**):30m 内敌兵按方位角分扇区(FormationScanner 算 OccupiedSectors=有威胁的方向数)。
    /// 需被占方向 ≥ MinSectors 才算被夹击;压力 = 增益 × 覆盖度(占用扇区/总扇区) × 局部以多打少(capped)。
    /// 方向是门 + 形状,密度是大小——前后夹击/三面合围比"正面一坨"压力更高。
    /// 用粗扇区 + 每扇区最小敌数去噪,避开 RMS 那种脆弱细象限几何(还守卫写反成死代码)。
    /// </summary>
    public sealed class EncirclementPressure : IMoralePressure
    {
        public string Tag => "enc";
        public bool IsEnabled => true;

        public float Sample(in FormationSnapshot now, in FormationSnapshot prev, in TeamMoraleContext team, float dt)
        {
            if (now.Count <= 0) return 0f;
            if (now.OccupiedSectors < MoraleTuning.EncircleMinSectors) return 0f; // 方向不够多 = 没被夹击

            float coverage = (float)now.OccupiedSectors / MoraleTuning.EncircleSectorCount; // 0..1 越被环绕越大
            float density = (float)now.LocalEnemyCount / now.Count;                          // 局部以多打少
            if (density > MoraleTuning.EncircleDensityCap) density = MoraleTuning.EncircleDensityCap;

            return MoraleTuning.EncircleGain * coverage * density;
        }
    }

    /// <summary>
    /// ④ 受远程攻击:读 <see cref="RangedThreatSensor"/> 累加的 per-formation 威胁强度
    /// (命中盾牌/士兵 + 落在编队附近的未命中箭矢/标枪,指数衰减成短窗)。压力 = 增益 × 威胁强度。
    /// 用专用传感器而非引擎 UnderRangedAttackRatio,因后者只数"被命中"不数"附近落点的未命中"。
    /// </summary>
    public sealed class RangedFirePressure : IMoralePressure
    {
        private readonly RangedThreatSensor _sensor;
        public RangedFirePressure(RangedThreatSensor sensor) { _sensor = sensor; }

        public string Tag => "rng";
        public bool IsEnabled => AnvilSettings.Instance?.RangedPressureEnabled == true;

        public float Sample(in FormationSnapshot now, in FormationSnapshot prev, in TeamMoraleContext team, float dt)
        {
            float threat = _sensor != null ? _sensor.GetThreat(now.Formation) : 0f;
            if (threat <= 0f || now.Count <= 0) return 0f;
            // **按编队人数归一化**为"每兵威胁":匹配参考值 #4"本编队人数×10 箭→溃"(rout 触发的是每兵箭量,不随编队大小变化)。
            // 末项:MCM「挨射士气影响」百分比(默认 100% = 基线;玩家可 0% 关停到数倍放大)。
            float intensity = (AnvilSettings.Instance?.RangedPressureIntensity ?? 100f) * 0.01f;
            return MoraleTuning.RangedGain * threat / now.Count * intensity;
        }
    }
}
