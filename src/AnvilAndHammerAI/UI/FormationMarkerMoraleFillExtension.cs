using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.Prefabs2;

namespace AnvilAndHammerAI.UI
{
    /// <summary>
    /// 把士气直接整合进原版编队标记的圆形兵种图标:往 <c>TeamTypeWidget</c>(渲染阵营色圆盘的 BrushWidget)的子节点里、
    /// 排在**白色兵种符号之前(渲染于其下)**插入一层"深灰盖"。
    ///
    /// 深灰盖 = 一个裁切容器(高度 = 已失士气段)套一张**与图标同 sprite**(<c>General\compass\target_background</c>)的满尺寸圆盘(深灰色):
    /// 容器只露出圆盘**顶部 (1−剩余士气)** 段 → 盖住"已失士气"部分;底部"有士气"段露出底层原阵营色(你队青绿/盟友绿/敌军红)。
    /// 同一圆 sprite 保证盖与底盘形状严丝合缝(不会露方角);宽度 StretchToParent 随图标距离缩放,且作为标记子孙随其按距离淡入淡出。
    /// 高度(裁切高 = MoraleDepletedHeight、内盘高 = MoraleIconHeight)由 <see cref="FormationMarkerMoraleMixin"/> 每帧按士气+距离算好注入。
    /// </summary>
    [PrefabExtension("FormationMarker", "//BrushWidget[@Id='TeamTypeWidget']/Children")]
    internal sealed class FormationMarkerMoraleFillExtension : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Child; // 作为 TeamTypeWidget/Children 的子节点
        public override int Index => 0;                      // 索引 0 = 排在原有白符号之前 → 渲染在其下,符号始终全白可见

        [PrefabExtensionText]
        public string Text => FillXml;

        // 外层 = 裁切容器(高 = 已失士气段,顶对齐);内层 = 满尺寸深灰圆盘(顶对齐),被裁切只露顶部 → 形成深灰"盖"。
        // 颜色 #33332EF0:深灰、高不透明度以盖住底层阵营色(在游可微调)。
        private const string FillXml =
            "<Widget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"Fixed\" SuggestedHeight=\"@MoraleDepletedHeight\" VerticalAlignment=\"Top\" ClipContents=\"true\" IsVisible=\"@ShowMoraleFill\">" +
              "<Children>" +
                "<Widget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"Fixed\" SuggestedHeight=\"@MoraleIconHeight\" VerticalAlignment=\"Top\" Sprite=\"General\\compass\\target_background\" Color=\"#33332EF0\" />" +
              "</Children>" +
            "</Widget>";
    }
}
