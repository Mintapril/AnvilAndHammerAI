using System;
using AnvilAndHammerAI.Detection;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Formations
{
    /// <summary>
    /// 骑射风筝(自有行为)。把整队带到**距敌约射程 60% 处**(贴近敌人、非躲大后方),沿敌正面横向缓慢扫掠("绕圈"观感),
    /// 全程 MovementOrderMove + 火力全开、保持编队、接敌宽度匹配敌编队宽度。
    ///
    /// 取代两个 vanilla 行为各自的毛病:
    ///   · BehaviorHorseArcherSkirmish —— 把骑射放到**我方编队中心后方 30m+**(像在大后方放箭,不是绕敌放风筝);
    ///   · BehaviorMountedSkirmish    —— 接战时(RangedCavalryUnitRatio&gt;0.95)会自转 MovementOrderCharge **散队自由冲锋**(违反无混战规则)。
    /// 本行为全程不冲锋:只在敌前方我方这侧的射程边缘环上摆动放箭,绝不绕到敌后被切断。镜像 FlankChargeBehavior 的下令方式。
    /// </summary>
    public sealed class HorseArcherKiteBehavior : BehaviorComponent
    {
        private const float KiteRangeFraction = 0.6f;     // 站在敌前"射程×此比例"处放风筝(≈射程 60%),贴近敌人而非躲我方大后方
        private const float MinStandoff = 30f;            // 兜底最近距离(射程异常小时)
        private const float MaxStandoff = 55f;            // 封顶:射程很大也不退到大后方
        private const float SweepDeg = 35f;               // 横向扫掠幅度(±度):沿敌正面来回扫
        private const float SweepSpeed = 0.22f;           // 扫掠角速度基准(rad/s)
        private const float Deg2Rad = (float)(Math.PI / 180.0);

        public HorseArcherKiteBehavior(Formation formation) : base(formation)
        {
            BehaviorCoherence = 0.5f;
            base.CurrentOrder = MovementOrder.MovementOrderStop;
            CurrentFacingOrder = FacingOrder.FacingOrderLookAtEnemy;
            CalculateCurrentOrder();
        }

        /// <summary>放风筝的对象 = 最近显著敌编队;回退最近任意敌编队。</summary>
        private Formation Target()
        {
            Formation self = base.Formation;
            if (self == null) return null;
            var qs = self.QuerySystem;
            if (qs != null && qs.ClosestSignificantlyLargeEnemyFormation != null)
            {
                Formation t = qs.ClosestSignificantlyLargeEnemyFormation.Formation;
                if (t != null && t.CountOfUnits > 0) return t;
            }
            return EnemyFormations.Nearest(self, Mission.Current);
        }

        /// <summary>我方主体锚点(主步兵优先,回退自身):骑射始终待在"敌→我主体"这条线的敌前方一侧,不绕敌后。</summary>
        private static Vec2 ArmyAnchor(Formation self)
        {
            Team team = self.Team;
            if (team != null)
            {
                Formation inf = team.GetFormation(FormationClass.Infantry);
                if (inf != null && inf.CountOfUnits > 0) return inf.CachedAveragePosition;
            }
            return self.CachedAveragePosition;
        }

        protected override void CalculateCurrentOrder()
        {
            Formation self = base.Formation;
            Formation enemy = Target();
            if (self == null || enemy == null)
            {
                base.CurrentOrder = MovementOrder.MovementOrderStop;
                CurrentFacingOrder = FacingOrder.FacingOrderLookAtEnemy;
                return;
            }
            Vec2 epos = enemy.CachedAveragePosition;
            float range = self.QuerySystem != null ? self.QuerySystem.MissileRangeAdjusted : 0f;
            float standoff = range > 1f ? range * KiteRangeFraction : MinStandoff;   // 贴近敌人(射程 60%)而非退到射程边缘/大后方
            if (standoff < MinStandoff) standoff = MinStandoff;
            else if (standoff > MaxStandoff) standoff = MaxStandoff;

            Vec2 toAnchor = ArmyAnchor(self) - epos;
            if (toAnchor.LengthSquared < 1e-4f) toAnchor = -enemy.Direction; // 敌我主体重合时退回敌背面方向
            toAnchor = toAnchor.Normalized();

            // 横向扫掠:绕"敌→我主体"基准方向左右缓摆(绕圈/游骑观感)。用 Mission 时间生成角度(确定性,不用 Random)。
            float t = Mission.Current != null ? Mission.Current.CurrentTime : 0f;
            float ang = (float)Math.Sin(t * SweepSpeed) * SweepDeg * Deg2Rad;
            Vec2 dir = Rotate(toAnchor, ang);
            Vec2 point = epos + dir * standoff;

            // 连续避让:朝风筝点前进的同时避开非目标编队(被环绕的敌 enemy 不纳入排斥,保持环绕距离)。
            base.CurrentOrder = FormationAvoidance.MoveTo(self, FormationAvoidance.Steer(self, point, enemy, Mission.Current));
            CurrentFacingOrder = FacingOrder.FacingOrderLookAtDirection((epos - point).Normalized());
        }

        private static Vec2 Rotate(Vec2 v, float rad)
        {
            float c = (float)Math.Cos(rad), s = (float)Math.Sin(rad);
            return new Vec2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        public override void TickOccasionally()
        {
            base.TickOccasionally();
            Formation self = base.Formation;
            if (self == null || self.AI.ActiveBehavior != this) return;
            CalculateCurrentOrder();
            self.SetMovementOrder(base.CurrentOrder);
            self.SetFacingOrder(CurrentFacingOrder);
            self.SetFiringOrder(FiringOrder.FiringOrderFireAtWill);
            self.SetArrangementOrder(ArrangementOrder.ArrangementOrderLoose); // 松散间距利于放箭机动,仍成编队(非散队)
            Formation tgt = Target();
            if (tgt != null && tgt.CountOfUnits > 0)
                self.SetFormOrder(FormOrder.FormOrderCustom(tgt.Width)); // 接敌宽度匹配敌编队宽度
        }

        protected override float GetAiWeight() => Target() != null ? 1f : 0f;

        // 重写避免基类用自有行为名查本地化 → "text with id ... doesn't exist"(见 FlankChargeBehavior 同名重写)。
        public override TextObject GetBehaviorString() => new TextObject("{=AnvilHammer_behavior_horse_archer_kite}Cavalry Skirmish");
    }
}
