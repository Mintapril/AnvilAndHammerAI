using System.Collections.Generic;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Formations
{
    /// <summary>
    /// 行为权重归属:让本 mod 成为它**实际驱动**的编队的唯一权重写入者。
    /// 调度器每次 <c>Drive</c> 一个编队时用 <see cref="ModStamping"/> 标记"以下为 mod 自身写入"(放行),
    /// 并用 <see cref="MarkStamped"/> 记下该编队的最近设权时刻;<see cref="BehaviorWeightOwnerPatch"/>
    /// 据此把**非 mod**(vanilla/RBM 战术层)对这些编队的 <c>WeightFactor</c> 写入直接跳过,
    /// 从源头杜绝两次设权之间的"夺权窗口"——由"清零重盖+频率取胜"的软压制升级为硬接管。
    ///
    /// <para>安全核心:只拦"近期被 mod 实际设权过"的编队(<see cref="GraceSeconds"/> 宽限窗,大于调度间隔 0.5s)。
    /// mod 未接管的编队(玩家亲控让行 / ScopeFilter 范围外 / 非野战 / 调度器关闭)从不被 MarkStamped,
    /// 故其 vanilla/RBM 权重照常生效,绝不会因无人设权而被写成"无行为可选"的呆滞态。</para>
    /// 非线程安全——仅主线程战斗 tick 串行访问。
    /// </summary>
    public static class WeightOwnership
    {
        /// <summary>重入标记:为 true 时表示当前正是 mod 自己在写权重 → patch 放行。</summary>
        public static bool ModStamping;

        // 设权后多久内仍视为"mod 正在驱动该编队"。须 > 调度间隔(0.5s)留足冗余(漏一两拍也不松手);
        // 又不宜过大,免玩家接管/范围切换后迟迟不还权。1.5s ≈ 3 个调度拍。
        private const float GraceSeconds = 1.5f;

        // 编队 → 最近一次被 mod 设权的任务时刻。键为本场任务的 Formation 实例,OnEndMission 清空(免跨场景持引用)。
        private static readonly Dictionary<Formation, float> _lastStamp = new Dictionary<Formation, float>();

        /// <summary>调度器驱动完一个编队后调用:记录其最近设权时刻。</summary>
        public static void MarkStamped(Formation f, float now)
        {
            if (f != null) _lastStamp[f] = now;
        }

        /// <summary>该编队是否处于"被 mod 驱动"的宽限窗内(= 应拦截 vanilla/RBM 对它的设权)。</summary>
        public static bool OwnedNow(Formation f, float now)
        {
            return f != null && _lastStamp.TryGetValue(f, out float t) && now - t < GraceSeconds;
        }

        /// <summary>任务结束清空(释放本场 Formation 引用)。</summary>
        public static void Clear() => _lastStamp.Clear();
    }
}
