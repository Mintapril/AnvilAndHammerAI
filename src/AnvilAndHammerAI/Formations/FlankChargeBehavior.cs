using AnvilAndHammerAI.Detection;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Formations
{
    /// <summary>
    /// 自有行为(ADR-0011 §D,唯一真·新动作)。把编队带到**军团级 schwerpunkt 的开放弧落点**,到位后切冲锋。
    /// 落点 + 目标由中央调度器(读空间脑)经 <see cref="SetFlankTarget"/> 注入;若未注入则退回"最近显著敌的侧翼"自算。
    /// 轻骑被派绕击 / 重骑(敌逼近崩溃)决定性冲击 / 包抄步兵 共用。纯读公有 API,零反射。
    /// </summary>
    public sealed class FlankChargeBehavior : BehaviorComponent
    {
        // 冲锋落点额外外推(m):沿"背离突破口"方向把起冲点拉远 → 整队从更外侧绕入,接敌角度更大(明显的侧/背袭观感),
        // 路径也更靠外、少穿我方阵列。骑兵外推更多(兼顾助跑加速到疾驰);步兵也外推(把包抄角度拉大,用户要"更大角度")。
        private const float CavRunUp = 34f;
        private const float FootRunUp = 26f;

        // 调度器注入的军团级目标/落点(空间脑算好)
        private Formation _overrideTarget;
        private Vec2 _overrideFlank;
        private bool _hasOverride;

        // 绕行 latch:已对哪支目标绕过其侧翼线(转到与落点同侧)。绕过即记住 → 此后直插落点不再绕,
        // 免在侧翼线上"绕↔切"每拍抖动;换目标(引用不同)自动重新评估。
        private Formation _roundedFor;

        public FlankChargeBehavior(Formation formation) : base(formation)
        {
            BehaviorCoherence = 0.6f;
            base.CurrentOrder = MovementOrder.MovementOrderStop; // 初值;无目标时不自由冲锋(混战),保持整队
            CurrentFacingOrder = FacingOrder.FacingOrderLookAtEnemy;
            CalculateCurrentOrder();
        }

        /// <summary>中央调度器每 tick 注入军团级突破口与开放弧落点。</summary>
        public void SetFlankTarget(Formation target, Vec2 flankPoint, bool has)
        {
            _overrideTarget = target;
            _overrideFlank = flankPoint;
            _hasOverride = has && target != null && target.CountOfUnits > 0;
        }

        private Formation NearestEnemy() => EnemyFormations.Nearest(base.Formation, Mission.Current);

        /// <summary>敌编队的开放侧翼落点(自算回退用):敌中心 ± 朝向垂直 × (敌宽/2+余量),取离我更近一侧。</summary>
        private static Vec2 SelfFlank(Formation self, Formation enemy)
        {
            Vec2 me = self.CachedAveragePosition;
            var ap = FormationGeometry.ApproachPointsFor(enemy, me, FormationGeometry.Standoff);
            return FormationGeometry.Nearer(ap.LeftFlank, ap.RightFlank, me);
        }

        protected override void CalculateCurrentOrder()
        {
            Formation self = base.Formation;
            if (self == null) { base.CurrentOrder = MovementOrder.MovementOrderStop; return; }

            Formation enemy;
            Vec2 flank;
            if (_hasOverride && _overrideTarget != null && _overrideTarget.CountOfUnits > 0)
            {
                enemy = _overrideTarget;
                flank = _overrideFlank;
            }
            else
            {
                enemy = NearestEnemy();
                if (enemy == null)
                {
                    base.CurrentOrder = MovementOrder.MovementOrderStop; // 无敌可冲 → 原地待命,不自由冲锋(混战)
                    CurrentFacingOrder = FacingOrder.FacingOrderLookAtEnemy;
                    return;
                }
                flank = SelfFlank(self, enemy);
            }

            Vec2 me = self.CachedAveragePosition;
            Vec2 ec = enemy.CachedAveragePosition;
            // 把起冲点沿"背离突破口"方向再外推 → 整队从更外侧绕入,接敌角度更大、路径更靠外。骑兵外推更多(兼顾助跑加速)。
            Vec2 outward = flank - ec;
            if (outward.LengthSquared > 1e-4f)
            {
                float runUp = (self.QuerySystem != null && self.QuerySystem.IsCavalryFormation) ? CavRunUp : FootRunUp;
                flank += outward.Normalized() * runUp;
                outward = flank - ec; // 含助跑后重新指向(敌中心 → 落点)
            }
            float dist = (me - flank).Length;
            if (dist <= self.Width * 1.2f + 6f)
            {
                base.CurrentOrder = MovementOrder.MovementOrderChargeToTarget(enemy); // 到起冲点 → 整队冲该敌(已含骑兵助跑外推)
            }
            else
            {
                // 接近段·绕侧/绕后:**未绕过敌侧翼线之前,把目标 enemy 也纳入避让**(势场把路径推向敌侧后,避免从正面直穿敌阵);
                // 一旦转到与落点同侧(= 已绕过侧翼)→ 解除对目标的避让,直插落点,再于上面切 ChargeToTarget(撞侧/背)。
                // rounded 判据:(我−敌中心) 在 "敌中心→落点" 方向上的投影 ≥ 0,即我已到落点那一侧。latch 防在侧翼线上抖动。
                bool rounded;
                if (outward.LengthSquared < 1e-4f) rounded = true;        // 落点≈敌中心(如骑兵拦截)→ 无侧后可绕,直接切入
                else if (_roundedFor == enemy) rounded = true;            // 已对该目标绕过 → 保持切入
                else
                {
                    rounded = Vec2.DotProduct(me - ec, outward.Normalized()) >= 0f;
                    if (rounded) _roundedFor = enemy;                     // latch:绕过即记住
                }
                Formation avoidExcept = rounded ? enemy : null;           // 未绕过:连目标一起避(绕行);已绕过:排除目标(切入)
                base.CurrentOrder = FormationAvoidance.MoveTo(self, FormationAvoidance.Steer(self, flank, avoidExcept, Mission.Current));
            }
            CurrentFacingOrder = FacingOrder.FacingOrderLookAtDirection((ec - me).Normalized());
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
                // 接敌宽度匹配敌编队宽度(尽量与目标同宽,正面对齐);无目标则退回宽正面。重置待命时的纵深队形,免冲成细长纵队。
                Formation tgt = (_hasOverride && _overrideTarget != null && _overrideTarget.CountOfUnits > 0) ? _overrideTarget : NearestEnemy();
                if (tgt != null && tgt.CountOfUnits > 0) self.SetFormOrder(FormOrder.FormOrderCustom(tgt.Width));
                else self.SetFormOrder(FormOrder.FormOrderWide);
            }
        }

        protected override float GetAiWeight()
        {
            if (_hasOverride && _overrideTarget != null && _overrideTarget.CountOfUnits > 0) return 1f;
            return NearestEnemy() != null ? 1f : 0f;
        }

        // 重写:基类 GetBehaviorString 会用 GetType().Name 去查本地化条目,自有行为名无对应 id → 指挥盘/RTSCamera
        // 显示编队当前行为时报 "text with id ... doesn't exist"。返回纯字面 TextObject(不触发 id 查找)既消错又给玩家看懂。
        public override TextObject GetBehaviorString() => new TextObject("{=AnvilHammer_behavior_flank_charge}Flanking Strike");
    }
}
