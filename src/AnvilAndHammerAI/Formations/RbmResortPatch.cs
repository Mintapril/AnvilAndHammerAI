using System;
using System.Reflection;
using AnvilAndHammerAI.Logging;
using AnvilAndHammerAI.Settings;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Formations
{
    /// <summary>
    /// 压制 **RBM 自己的**编队重分(与 <see cref="FormationResortPatch"/> 压制 vanilla 合并原语**互补,二者缺一不可**)。
    ///
    /// RBM 的 <c>RBMAI.Tactics+ManageFormationCountsPatch.PrefixSetDefaultBehaviorWeights</c>
    /// (prefix on <c>TacticComponent.ManageFormationCounts(int,int,int,int)</c>,核实 tools/dump/_rbmsrc/Tactics.cs:515-562)
    /// **直接遍历 agent 按兵种 `agent.Formation = GetFormation(Infantry/Ranged/Cavalry/HorseArcher)` 塞回 4 标准槽**,
    /// **不经过 SplitFormationClassIntoGivenNumber** —— 故 FormationResortPatch 拦不住它。装 RBM 时这是
    /// "本 mod 7 角色编队每 tick 被合并回标准槽(步兵 1 队、骑兵 1~2 队)"的**主因**。
    ///
    /// 手法:patch RBM 那个 prefix 本身。开启自动编队且该队归本 mod 接管(ScopeFilter)时:跳过其方法体(return false),
    /// 并令其"返回 false"(__result=false)→ 连带跳过 vanilla 原版 ManageFormationCounts。关掉自动编队 / 敌方(只玩家军时)→ 放行 RBM。
    /// RBM 缺席/改版 → 反射返 null,记日志跳过(此时只有 vanilla 合并,FormationResortPatch 兜)。
    /// </summary>
    public static class RbmResortPatch
    {
        private const string PatchTypeName = "RBMAI.Tactics+ManageFormationCountsPatch";
        private const string PatchMethodName = "PrefixSetDefaultBehaviorWeights";

        private static bool _applied;

        public static void Apply(Harmony h)
        {
            if (_applied) return;
            _applied = true;

            Type t = AccessTools.TypeByName(PatchTypeName);
            if (t == null)
            {
                Log.Info($"[autoform] 未找到 RBM 类 {PatchTypeName}(RBM 缺席/改版?);RBM 重分未压制 —— 若实际装了 RBM,7 角色编队会被它合并回标准槽。");
                return;
            }
            MethodInfo m = AccessTools.Method(t, PatchMethodName);
            if (m == null)
            {
                Log.Error($"[autoform] 未找到 RBM 方法 {PatchTypeName}.{PatchMethodName};RBM 重分未压制。");
                return;
            }
            try
            {
                h.Patch(m, prefix: new HarmonyMethod(typeof(RbmResortPatch), nameof(DisablePrefix)));
                Log.Info("[autoform] 已 patch RBM 编队重分(本 mod 接管的队跳过其按兵种塞 4 槽,7 角色编队得以稳住)。");
            }
            catch (Exception e)
            {
                Log.Error("[autoform] patch RBM 重分失败。", e);
            }
        }

        /// <summary>
        /// 自动编队开启且该队归本 mod 接管 → 跳过 RBM 重分体(return false),并令其"返回 false"(__result)连带跳过 vanilla 原版。
        /// 关闭自动编队 / 该队不归我们管(只玩家军时的敌方) → 放行 RBM 原 prefix。
        /// </summary>
        public static bool DisablePrefix(ref bool __result, object[] __args)
        {
            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled || !s.AutoFormationEnabled) return true; // 放行 RBM

            // RBM 那个 prefix 首参是 TacticComponent(见 dump Tactics.cs:515 `ref TacticComponent __instance`),经 __args[0] 取其 Team。
            Team team = (__args != null && __args.Length > 0) ? (__args[0] as TacticComponent)?.Team : null;
            if (!ScopeFilter.Applies(team)) return true; // 只压制本 mod 接管的队

            __result = false; // RBM 的 prefix "返回 false" → 连带跳过 vanilla 原版 ManageFormationCounts
            return false;     // 跳过 RBM 的重分体(其每 tick 按兵种塞 4 槽)
        }
    }
}
