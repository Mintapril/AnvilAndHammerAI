using AnvilAndHammerAI.Detection;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Formations
{
    /// <summary>
    /// 预备待命行为:把编队带到中央调度器注入的**待命点**(包抄步兵=主步兵侧后,重骑=更后方),到位即立定、面向敌方,
    /// 并用**纵深队形**(FormOrderDeep,而非原版 BehaviorReserve 的宽线 FormOrderWide)。
    /// 解决:包抄步兵与重骑在后方重叠、且都排成滑稽的一字宽线。镜像 FlankChargeBehavior 的下令方式(仅在 ActiveBehavior 时于 Tick 应用)。
    /// </summary>
    public sealed class ReserveStagingBehavior : BehaviorComponent
    {
        private Vec2 _point;
        private bool _has;

        public ReserveStagingBehavior(Formation formation) : base(formation)
        {
            BehaviorCoherence = 0.8f;
            base.CurrentOrder = MovementOrder.MovementOrderStop;
            CurrentFacingOrder = FacingOrder.FacingOrderLookAtEnemy;
            CalculateCurrentOrder();
        }

        /// <summary>中央调度器每拍注入待命点。has=false 则原地立定。</summary>
        public void SetPoint(Vec2 point, bool has) { _point = point; _has = has; }

        protected override void CalculateCurrentOrder()
        {
            Formation self = base.Formation;
            if (self == null || !_has) { base.CurrentOrder = MovementOrder.MovementOrderStop; return; }
            // 连续避让:朝待命点前进的同时被非目标编队推开(staging 无交战目标 → 全避)。站到位后排斥力即抗重叠。
            base.CurrentOrder = FormationAvoidance.MoveTo(self, FormationAvoidance.Steer(self, _point, null, Mission.Current));
            CurrentFacingOrder = FacingOrder.FacingOrderLookAtEnemy;
        }

        public override void TickOccasionally()
        {
            base.TickOccasionally();
            Formation self = base.Formation;
            if (self != null && self.AI.ActiveBehavior == this)
            {
                CalculateCurrentOrder();
                self.SetMovementOrder(base.CurrentOrder);
                self.SetFacingOrder(CurrentFacingOrder);
                self.SetFormOrder(FormOrder.FormOrderDeep); // 纵深块,不排成一字宽线
            }
        }

        protected override float GetAiWeight() => _has ? 1f : 0f;

        // 重写:避免基类用自有行为名查本地化 → "text with id ... doesn't exist"(见 FlankChargeBehavior 同名重写)。
        public override TextObject GetBehaviorString() => new TextObject("{=AnvilHammer_behavior_reserve_staging}Reserve Staging");
    }
}
