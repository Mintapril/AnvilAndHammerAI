using AnvilAndHammerAI.Settings;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Morale
{
    /// <summary>
    /// 只读士气读出口(供 UI 取某编队当前士气"剩余"比例 0..1;1=健康,0=贴溃逃阈)。
    /// 复用编队级士气系统已维护的同一 <see cref="FormationShockPool"/>(SubModule 注入),
    /// 零反射、零新增存档、零分配、null 安全。读的是士气层每拍写的普通 float 池值(同主线程,撕裂读至多取到上一拍值,绝不崩)。
    /// 士气对双方恒生效(忽略 ScopeFilter),故敌我编队都能读到 → UI 双方都画条。
    /// </summary>
    public static class MoraleReadout
    {
        private static FormationShockPool _pool;

        /// <summary>SubModule 创建共享池后调一次。</summary>
        public static void Register(FormationShockPool pool) => _pool = pool;

        /// <summary>
        /// 取 f 的当前士气剩余比例。无池/无状态(本拍尚未产生震慑态,如野战外或开局头一拍)→ false(UI 不画条)。
        /// remaining01 = 1 − clamp(Pool / 有效溃逃阈, 0, 1);有效阈 = PoolRoutThreshold × tier 抗性(与决策门同式,故条空之刻正是整队溃逃之刻)。
        /// </summary>
        public static bool TryGetRemaining(Formation f, out float remaining01)
        {
            remaining01 = 1f;
            if (_pool == null || f == null) return false;
            if (!_pool.TryGet(f, out FormationShockState st)) return false;

            AnvilSettings s = AnvilSettings.Instance;
            float baseThreshold = s != null ? s.PoolRoutThreshold : MoraleTuning.PoolRoutThresholdDefault;
            float avgTier = st.HasPrev ? st.Prev.AvgTier : MoraleTuning.TierResistBaseline; // 已缓存的上拍快照 tier;无则中性基准(抗性 ×1.0)
            float eff = MoraleTuning.RoutThreshold(baseThreshold, avgTier);
            if (eff <= 0f) return false;

            float frac = st.Pool / eff;
            if (frac < 0f) frac = 0f; else if (frac > 1f) frac = 1f;
            remaining01 = 1f - frac;
            return true;
        }
    }
}
