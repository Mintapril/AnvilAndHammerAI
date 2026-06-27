using System;
using AnvilAndHammerAI.Morale;
using AnvilAndHammerAI.Settings;
using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.ViewModels;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.ViewModelCollection.HUD.FormationMarker;

namespace AnvilAndHammerAI.UI
{
    /// <summary>
    /// 给原版"编队标记"的每个目标 VM(<see cref="MissionFormationMarkerTargetVM"/>,长按 Alt 时浮在编队上方的圆形兵种图标)
    /// 注入三个可绑定属性,驱动我们用 UIExtenderEx 注入到图标内的"深灰盖"(见 <see cref="FormationMarkerMoraleFillExtension"/>):
    ///   · <see cref="ShowMoraleFill"/>      —— 是否启用填充(无士气数据/未开启时隐藏深灰盖 → 图标回原版纯阵营色);
    ///   · <see cref="MoraleIconHeight"/>     —— 深灰盖内那张满尺寸圆盘的高度 = 当前图标边长(随距离缩放),保证盘为正圆、与底盘等大;
    ///   · <see cref="MoraleDepletedHeight"/> —— 深灰盖(裁切容器)的高度 = (1−剩余士气) × 图标边长,从顶部盖住"已失士气"那段。
    /// RefreshMethodName="Refresh":原版每帧对每个 target 调 Refresh(),UIExtenderEx 随之触发本 <see cref="OnRefresh"/>,故填充每帧跟随士气刷新。
    /// 士气对双方恒生效,故敌我编队都填充。有士气部分露出底层**原阵营色**(不改色),失去部分为深灰 —— 与 vanilla 美术一致。
    /// </summary>
    [ViewModelMixin("Refresh")]
    internal sealed class FormationMarkerMoraleMixin : BaseViewModelMixin<MissionFormationMarkerTargetVM>
    {
        // 图标默认边长(prefab TeamTypeWidget SuggestedWidth/Height=50);原版按距离缩放,见 ComputeScale。
        private const float IconBaseSize = 50f;

        private bool _show;
        private float _iconHeight = IconBaseSize;
        private float _depletedHeight;

        public FormationMarkerMoraleMixin(MissionFormationMarkerTargetVM vm) : base(vm) { }

        [DataSourceProperty]
        public bool ShowMoraleFill
        {
            get => _show;
            set { if (_show != value) { _show = value; OnPropertyChangedWithValue(value, nameof(ShowMoraleFill)); } }
        }

        [DataSourceProperty]
        public float MoraleIconHeight
        {
            get => _iconHeight;
            set { if (_iconHeight != value) { _iconHeight = value; OnPropertyChangedWithValue(value, nameof(MoraleIconHeight)); } }
        }

        [DataSourceProperty]
        public float MoraleDepletedHeight
        {
            get => _depletedHeight;
            set { if (_depletedHeight != value) { _depletedHeight = value; OnPropertyChangedWithValue(value, nameof(MoraleDepletedHeight)); } }
        }

        public override void OnRefresh()
        {
            AnvilSettings s = AnvilSettings.Instance;
            MissionFormationMarkerTargetVM vm = ViewModel;
            Formation f = vm?.Formation;

            if (s == null || !s.Enabled || !s.ShowMoraleBars || f == null
                || !MoraleReadout.TryGetRemaining(f, out float rem))
            {
                ShowMoraleFill = false;
                return;
            }

            // 跟随原版图标的距离缩放,使深灰盖始终贴合图标边缘。
            float iconH = IconBaseSize * ComputeScale(vm.Distance, vm.ShowDistanceTexts);
            MoraleIconHeight = iconH;
            MoraleDepletedHeight = (1f - rem) * iconH; // 顶部盖住"已失士气"段;rem=1→0(不盖),rem=0→满(全灰)
            ShowMoraleFill = true;
        }

        /// <summary>
        /// 复刻原版 <c>FormationMarkerListPanel.GetDistanceRelatedScale</c>:开"显示距离数字"时图标不缩放(恒 1);
        /// 否则近大远小(0.5~1.4,立方根缓动;近&lt;10m=1.4,远&gt;500m=0.5)。
        /// </summary>
        private static float ComputeScale(float distance, bool showDistanceTexts)
        {
            if (showDistanceTexts) return 1f;
            const float far = 500f, close = 10f, farScale = 0.5f, closeScale = 1.4f;
            if (distance > far) return farScale;
            if (distance >= close)
            {
                float amount = (float)Math.Pow((distance - close) / (far - close), 1.0 / 3.0);
                float scl = closeScale + (farScale - closeScale) * amount;
                if (scl < farScale) scl = farScale; else if (scl > closeScale) scl = closeScale;
                return scl;
            }
            return closeScale;
        }
    }
}
