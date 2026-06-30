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

            float frac = st.Effective / eff; // 有效压力 = 情势池 + 伤亡溃逃压力 + 冲锋冲击震慑(与决策门同口径)
            if (frac < 0f) frac = 0f; else if (frac > 1f) frac = 1f;
            remaining01 = 1f - frac;

            // "接战过不可能满士气":士气条**上限 = 存活比**(= 1 − 累计伤亡比),与"是否在溃逃边缘"解耦。
            // 重整后伤亡压力虽归零(可再战),但只要有过伤亡,条就填不满;伤亡越多上限越低。
            float survivalCap = 1f - st.CasualtyFractionNow;
            if (survivalCap < 0f) survivalCap = 0f;
            if (remaining01 > survivalCap) remaining01 = survivalCap;
            return true;
        }

        /// <summary>f 是否处于溃逃中(普通溃逃锁存 <see cref="FormationShockState.RoutLatched"/> 或决定性崩溃触底 <see cref="FormationShockState.Bottomed"/>)。供 UI 给图标做溃逃闪烁。</summary>
        public static bool IsRouting(Formation f)
        {
            if (_pool == null || f == null) return false;
            if (!_pool.TryGet(f, out FormationShockState st)) return false;
            return st.RoutLatched || st.Bottomed;
        }
    }
}
