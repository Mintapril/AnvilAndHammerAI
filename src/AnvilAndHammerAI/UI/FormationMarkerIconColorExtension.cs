using System.Collections.Generic;
using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.Prefabs2;

namespace AnvilAndHammerAI.UI
{
    /// <summary>
    /// 给原版编队标记里那张白色兵种符号(<c>FormationMarker.xml</c> 的 <c>&lt;Widget Id="FormationTypeMarker"&gt;</c>)
    /// 加一条 <c>Color</c> 绑定 → 由 <see cref="FormationMarkerMoraleMixin.FormationIconColor"/> 驱动:
    /// 包抄步兵(HeavyInfantry)/ 重骑(HeavyCavalry)编队染金,其余维持白。
    ///
    /// 安全性:<c>FormationMarkerListPanel.OnLateUpdate</c> 只设该 widget 的 <c>Sprite</c>、**从不写 Color**;
    /// 距离淡入淡出走 <c>SetGlobalAlphaRecursively</c>(只改 alpha,不动 RGB),故此 Color 染色不会被每帧覆盖。
    /// </summary>
    [PrefabExtension("FormationMarker", "//Widget[@Id='FormationTypeMarker']")]
    internal sealed class FormationMarkerIconColorExtension : PrefabExtensionSetAttributePatch
    {
        public override List<Attribute> Attributes { get; } = new List<Attribute>
        {
            new Attribute("Color", "@FormationIconColor"),
        };
    }
}
