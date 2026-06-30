using AnvilAndHammerAI.Settings;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Formations
{
    /// <summary>
    /// 硬接管编队行为权重:把 vanilla/RBM 战术层对**本 mod 正在驱动的编队**的权重写入全部跳过(return false),
    /// 使 mod 的设权成为这些编队的唯一权重权威 —— 不再是"每 0.5s 清零重盖、靠最后盖章取胜"的软压制,
    /// 而是从源头禁止他者设权,消除 0.5s 夺权窗口(用户要求"所有 vanilla/rbm 设置权重的行为全部跳过")。
    ///
    /// <para>单点拦 <see cref="BehaviorComponent"/> 的 <c>WeightFactor</c> setter:engine 的
    /// <c>FormationAI.ResetBehaviorWeights</c>(逐个 <c>ResetBehavior</c> → <c>WeightFactor = 0</c>)与泛型
    /// <c>SetBehaviorWeight&lt;T&gt;</c>(→ <c>WeightFactor = w</c>)都汇于此 setter,故一处即覆盖两条路径
    /// (泛型方法无法直接 Harmony patch,拦其唯一写入汇点最稳)。</para>
    ///
    /// <para><see cref="WeightOwnership.ModStamping"/> 放行 mod 自身写入;仅拦"近期被 mod 实际设权过"的编队
    /// (<see cref="WeightOwnership.OwnedNow"/>),故 mod 未接管的编队(玩家亲控 / 范围外 / 调度器关闭)的
    /// vanilla/RBM 权重照常生效、绝不呆滞。<see cref="BehaviorComponent"/> 属 TaleWorlds.MountAndBlade(已引用),无需反射。</para>
    /// 经 <c>SubModule.EnsurePatched</c> 的 <c>PatchAll</c> 惰性安装(首场野战),不在载入期 patch、不碰 MovementOrder cctor 雷区。
    /// </summary>
    [HarmonyPatch(typeof(BehaviorComponent), "WeightFactor", MethodType.Setter)]
    public static class BehaviorWeightOwnerPatch
    {
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(BehaviorComponent __instance)
        {
            if (WeightOwnership.ModStamping) return true;            // mod 自己的 Reset/SetBehaviorWeight → 放行
            AnvilSettings s = AnvilSettings.Instance;
            if (s == null || !s.Enabled || !s.CommandSchedulerEnabled) return true; // mod 未接管指挥 → 不干预
            Mission m = Mission.Current;
            if (m == null) return true;
            Formation f = __instance?.Formation;
            if (f == null) return true;
            // 该编队近期被 mod 驱动 → 跳过 vanilla/RBM 的本次设权;否则放行(绝不让未接管编队失去权重)。
            return !WeightOwnership.OwnedNow(f, m.CurrentTime);
        }
    }
}
