using AnvilAndHammerAI.Settings;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Formations
{
    /// <summary>
    /// 压制 native/RBM 的编队重分/合并(ADR-0011 §5.B,完整版)。直接 prefix **合并原语**
    /// <c>TacticComponent.SplitFormationClassIntoGivenNumber(Func&lt;Formation,bool&gt;, int)</c>(反编译 37965)——
    /// 它把"满足谓词的同类编队"拆/并成给定数量,各战术几乎都传 count=1 → **把同类的多支编队合并成 1 支**。
    /// 于是本 mod 拆出的 主步兵/包抄步兵(都 IsInfantryFormation)、左轻骑/右轻骑/重骑(都 IsCavalryFormation)
    /// 每个 native 战术 tick 都被合并回标准槽 —— 这就是"步兵只 1 队、骑兵只 1~2 队、扩展槽从不出现"的真因。
    ///
    /// **必须 patch 这个原语而非 ManageFormationCounts**:多个战术(反编译 37496/41168/41911)绕过 4 参版
    /// 直接调本原语,只 patch ManageFormationCounts 会漏。本原语是所有合并路径的唯一收口。
    ///
    /// 对**本 mod 接管的队**(ScopeFilter)返回 false:跳过合并,让 AutoFormation 自管的 7 角色编队稳住。
    /// <c>[HarmonyBefore("com.rbmai")] + Priority.First</c>:本 prefix 在 RBM 同方法 prefix(若有)之前跑,返回 false 连带跳过。
    /// 非本 mod 队(只玩家军时的敌方)返回 true,原版/RBM 重分照常;关掉自动编队亦放行。零反射、RBM 缺席也生效。
    /// </summary>
    [HarmonyPatch(typeof(TacticComponent), "SplitFormationClassIntoGivenNumber")]
    [HarmonyBefore("com.rbmai", "com.rbmcombat")]
    public static class FormationResortPatch
    {
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(TacticComponent __instance)
        {
            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled || !s.AutoFormationEnabled) return true;          // 放行原版/RBM 重分
            if (__instance == null || !ScopeFilter.Applies(__instance.Team)) return true; // 只压制本 mod 接管的队
            return false; // 跳过合并 → 本 mod 7 角色编队稳住(本 patch 在前,连带跳过 RBM 同方法 prefix)
        }
    }
}
