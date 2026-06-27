using AnvilAndHammerAI.Settings;
using HarmonyLib;
using TaleWorlds.MountAndBlade.ViewModelCollection.HUD.FormationMarker;

namespace AnvilAndHammerAI.UI
{
    /// <summary>
    /// 让编队标记"全程显示"(无需长按 Alt)。原版 view 每帧 <c>_dataSource.IsEnabled = 按住ViewOrders键 || 指令菜单开</c>,
    /// 而该 view 级 IsEnabled 的 setter 会把值传播给每个 target(进而驱动各标记 widget 的 IsMarkerEnabled/alpha)。
    /// 故只需在此 setter 前置:开启常显时把传入的 false 翻成 true —— view 的 <c>if(IsEnabled)</c> 即恒真,原版自己每帧
    /// RefreshFormationMarkers + UpdateMarkerPositions,标记常驻。setter 自带相等判断,稳态(已 true)再设 true 为 no-op,无抖动、无额外开销。
    /// 关闭常显时不干预,Alt 行为回归原版。经 EnsurePatched 的 PatchAll 收集(首次野战后安装)。
    /// </summary>
    [HarmonyPatch(typeof(MissionFormationMarkerVM), nameof(MissionFormationMarkerVM.IsEnabled), MethodType.Setter)]
    internal static class AlwaysShowMarkersPatch
    {
        private static void Prefix(ref bool value)
        {
            if (value) return; // 本就要启用 → 无需干预
            AnvilSettings s = AnvilSettings.Instance;
            if (s != null && s.Enabled && s.AlwaysShowFormationMarkers)
                value = true;
        }
    }
}
