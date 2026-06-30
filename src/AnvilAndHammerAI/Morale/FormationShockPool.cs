using System.Runtime.CompilerServices;
using AnvilAndHammerAI.Detection;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Morale
{
    /// <summary>
    /// 单个编队的震慑池可变状态(vanilla 没有的编队级士气层的核心)。
    /// 池 = 各压力源贡献按 dt 的连续积分,每秒衰减;越阈触发整队协调溃逃后**令牌驻留**做去抖冷却。
    /// 纯状态对象,绝不塞进任何不可变缓存(ADR-0009 维护者警告)。
    /// </summary>
    public sealed class FormationShockState
    {
        public float Pool;                // 瞬时情势池(级联+包围+远程,积分+每秒衰减)
        public float CasualtyFloor;       // 伤亡溃逃压力 = ErosionGain × (自上次重整以来损失的"当前兵力"比);可重整(重整时基准归位 → 归零)
        public float ChargeShock;         // 冲锋冲击震慑(慢衰减;由编排层每拍从 ChargeImpactSensor 镜像写入)
        public float CasualtyBaseline;    // 上次重整时的累计伤亡比(再溃以此为基准:再损失"当前兵力"的一截才再溃)
        public float CasualtyFractionNow; // 本拍累计伤亡比(= 1 − 存活比;供 Decide 重置基准 + 士气条"永不满"上限)
        public bool RoutLatched;          // 越阈令牌:已触发整队溃逃,冷却内不重复触发/不集结
        public float LastRoutTime = -999f;

        /// <summary>有效溃逃压力 = 瞬时情势池 + 伤亡溃逃压力 + 冲锋冲击震慑。决策/士气条/放锤判据统一读此。</summary>
        public float Effective => Pool + CasualtyFloor + ChargeShock;

        public int RatchetLevel;          // C 棘轮(编队级):该编队累计整队溃逃次数;越多集结目标越低
        public bool Bottomed;             // 触底:档数到顶 → 整队不再集结(决定性崩溃)

        public bool HasPrev;              // 是否已有上拍快照(供压力源算差分)
        public FormationSnapshot Prev;

        /// <summary>按 dt 把瞬时压力速率(/秒)积分入池。</summary>
        public void Accumulate(float pressureRate, float dt)
        {
            Pool += pressureRate * dt;
            if (Pool < 0f) Pool = 0f;
        }

        /// <summary>每秒衰减(去抖 + 恢复)。</summary>
        public void Decay(float perSecond, float dt)
        {
            Pool -= perSecond * dt;
            if (Pool < 0f) Pool = 0f;
        }

        /// <summary>把崩溃/溃逃状态清回"新战力"(援军重新填充已崩溃编队时用;保留 Prev,Effective 自然归 0)。</summary>
        public void ResetCollapse()
        {
            Pool = 0f; CasualtyFloor = 0f; ChargeShock = 0f;
            CasualtyBaseline = 0f; CasualtyFractionNow = 0f;
            RoutLatched = false; LastRoutTime = -999f;
            RatchetLevel = 0; Bottomed = false;
        }
    }

    /// <summary>
    /// per-formation 震慑池容器。用 <see cref="ConditionalWeakTable{TKey,TValue}"/>(ADR-0009 指定):
    /// key 弱引用,编队被引擎丢弃后状态随 GC 自动回收,无需手写删除事件(编队无可靠删除钩子)。
    /// tick 时编队在手,按键查找即可,不依赖枚举。OnEndMission 调 <see cref="Reset"/> 显式释放。
    /// </summary>
    public sealed class FormationShockPool
    {
        private ConditionalWeakTable<Formation, FormationShockState> _table
            = new ConditionalWeakTable<Formation, FormationShockState>();

        public FormationShockState GetOrCreate(Formation f) => _table.GetOrCreateValue(f);

        public bool TryGet(Formation f, out FormationShockState state) => _table.TryGetValue(f, out state);

        /// <summary>整表丢弃(GC 回收旧表);跨战斗不残留。</summary>
        public void Reset() => _table = new ConditionalWeakTable<Formation, FormationShockState>();
    }
}
