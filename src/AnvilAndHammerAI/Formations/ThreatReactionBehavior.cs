using AnvilAndHammerAI.Detection;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Formations
{
    /// <summary>
    /// 遇袭自发反应行为(反应覆盖层的唯一动作)。当某编队遭遇"非流程预期"的急性威胁(主要是敌近战骑兵突袭),
    /// 中央调度器把本行为抬到最高权重使其成为 ActiveBehavior,据注入的模式下达应激姿态:
    ///   · <see cref="Mode.Brace"/>    —— 结盾墙 + 立定 + 面向威胁(步兵架枪/抗骑;取代"背对敌移动"被穿);
    ///   · <see cref="Mode.FallBack"/> —— 朝集结点(我方主步兵后方)散开后撤(弓兵/骑射无法结阵抗骑时脱离);
    ///   · <see cref="Mode.Counter"/>  —— 骑兵后撤拉开一次冲锋距离 → 冲锋迎击来袭骑兵(蓄势只做一次,latch 防抖)。
    /// 模式/目标由调度器每拍经 <see cref="SetReaction"/> 注入;威胁消失(经迟滞)后调度器改回常规权重,本行为自动让位。
    /// 纯读公有 API、零反射;镜像 <see cref="FlankChargeBehavior"/> 的下令方式(仅在自身为 ActiveBehavior 时于 Tick 应用 order)。
    /// </summary>
    public sealed class ThreatReactionBehavior : BehaviorComponent
    {
        public enum Mode { None, Brace, FallBack, Counter, Rout }

        private Mode _mode;
        private Formation _threat;   // 来袭敌编队(Counter 用作冲锋目标;Brace/FallBack 用其位置定朝向)
        private Vec2 _threatPos;     // 威胁位置快照(threat 可能在迟滞窗内消失,仍用快照保持朝向)
        private bool _hasThreatPos;
        private Vec2 _rally;         // FallBack 集结落点(我方主步兵后方);无则背离威胁自撤
        private bool _hasRally;
        private bool _enveloped;     // Brace 时被多面夹击 → 结 Square(全向)而非单向盾墙
        private bool _windupDone;    // Counter 蓄势 latch:拉开一次冲锋距离后即冲,防冲锋-后撤抖动

        private ArrangementOrder _arrangement = ArrangementOrder.ArrangementOrderShieldWall;

        public ThreatReactionBehavior(Formation formation) : base(formation)
        {
            BehaviorCoherence = 0.6f;
            base.CurrentOrder = MovementOrder.MovementOrderStop;
            CurrentFacingOrder = FacingOrder.FacingOrderLookAtEnemy;
            CalculateCurrentOrder();
        }

        /// <summary>调度器每拍注入反应模式与目标/落点。mode==None = 本拍无反应(让位常规权重)。enveloped 仅 Brace 用(被夹击→Square)。</summary>
        public void SetReaction(Mode mode, Formation threat, Vec2 threatPos, bool hasThreatPos, Vec2 rally, bool hasRally, bool enveloped)
        {
            // 进入/离开 Counter 时复位蓄势 latch;持续处于 Counter 则保留(避免反复后撤蓄势)。
            if (mode != Mode.Counter || _mode != Mode.Counter) _windupDone = false;
            _mode = mode;
            _threat = threat;
            _threatPos = threatPos; _hasThreatPos = hasThreatPos;
            _rally = rally; _hasRally = hasRally;
            _enveloped = enveloped;
        }

        protected override void CalculateCurrentOrder()
        {
            Formation self = base.Formation;
            if (self == null) { base.CurrentOrder = MovementOrder.MovementOrderStop; return; }

            Vec2 me = self.CachedAveragePosition;
            Vec2 tpos = _threat != null && _threat.CountOfUnits > 0
                ? _threat.CachedAveragePosition
                : (_hasThreatPos ? _threatPos : me);

            switch (_mode)
            {
                case Mode.Brace:
                {
                    // 被多面夹击 → Square(全向受冲);否则盾墙(有矛/枪即自发架枪),面向主威胁。
                    _arrangement = _enveloped
                        ? ArrangementOrder.ArrangementOrderSquare
                        : ArrangementOrder.ArrangementOrderShieldWall;
                    base.CurrentOrder = MovementOrder.MovementOrderStop;        // 立定,别背对敌移动被穿
                    Vec2 dir = tpos - me;
                    CurrentFacingOrder = dir.LengthSquared > 1e-4f
                        ? FacingOrder.FacingOrderLookAtDirection(dir.Normalized())
                        : FacingOrder.FacingOrderLookAtEnemy;
                    break;
                }
                case Mode.FallBack:
                {
                    _arrangement = ArrangementOrder.ArrangementOrderLoose;      // 散开,减被冲/被射损失
                    Vec2 target;
                    if (_hasRally) target = _rally;
                    else
                    {
                        Vec2 away = me - tpos;
                        away = away.LengthSquared > 1e-4f ? away.Normalized() : self.Direction;
                        target = me + away * ReactionTuning.FallbackAwayDistance;
                    }
                    base.CurrentOrder = FormationAvoidance.MoveTo(self, target);
                    CurrentFacingOrder = FacingOrder.FacingOrderLookAtEnemy;
                    break;
                }
                case Mode.Counter:
                {
                    _arrangement = ArrangementOrder.ArrangementOrderLine;       // 线列冲击
                    Vec2 toThreat = tpos - me;
                    float dist = toThreat.Length;
                    if (!_windupDone && dist < ReactionTuning.CounterWindupDist)
                    {
                        // 先后撤拉开一次冲锋距离(蓄势),只做一次,latch 防抖。
                        Vec2 away = dist > 1e-4f ? (me - tpos).Normalized() : self.Direction;
                        Vec2 windupPt = me + away * ReactionTuning.CounterRunup;
                        base.CurrentOrder = FormationAvoidance.MoveTo(self, windupPt);
                    }
                    else
                    {
                        _windupDone = true;
                        base.CurrentOrder = _threat != null && _threat.CountOfUnits > 0
                            ? MovementOrder.MovementOrderChargeToTarget(_threat) // 整队冲锋迎击来袭那支骑兵(定向,非混战)
                            : MovementOrder.MovementOrderStop;                   // 目标已没 → 原地,不自由冲锋

                    }
                    CurrentFacingOrder = toThreat.LengthSquared > 1e-4f
                        ? FacingOrder.FacingOrderLookAtDirection(toThreat.Normalized())
                        : FacingOrder.FacingOrderLookAtEnemy;
                    break;
                }
                case Mode.Rout:
                {
                    // 溃逃后撤(决定性崩溃之外):整队背对威胁奔退(散开)。**面朝撤离方向 = 背部朝敌** →
                    // 编队级 CurrentDirection 背敌、逐兵亦背敌 → 敌方命中判为背袭,后撤照吃溃逃伤害加成(见 DamageSystem)。
                    _arrangement = ArrangementOrder.ArrangementOrderLoose;
                    Vec2 away = me - tpos;
                    away = away.LengthSquared > 1e-4f ? away.Normalized() : self.Direction;
                    Vec2 target = me + away * ReactionTuning.FallbackAwayDistance;
                    base.CurrentOrder = FormationAvoidance.MoveTo(self, target);
                    CurrentFacingOrder = FacingOrder.FacingOrderLookAtDirection(away);
                    break;
                }
                default:
                    base.CurrentOrder = MovementOrder.MovementOrderStop;
                    CurrentFacingOrder = FacingOrder.FacingOrderLookAtEnemy;
                    break;
            }
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
                self.SetArrangementOrder(_arrangement);
            }
        }

        protected override float GetAiWeight() => _mode != Mode.None ? 1f : 0f;

        // 重写:避免基类用自有行为名查本地化 → "text with id ... doesn't exist"(见 FlankChargeBehavior 同名重写)。按当前模式给中文名。
        public override TextObject GetBehaviorString()
        {
            switch (_mode)
            {
                case Mode.Brace: return new TextObject("{=AnvilHammer_reaction_brace}Shield Wall");
                case Mode.FallBack: return new TextObject("{=AnvilHammer_reaction_fallback}Fall Back");
                case Mode.Counter: return new TextObject("{=AnvilHammer_reaction_counter}Counter-Charge");
                case Mode.Rout: return new TextObject("{=AnvilHammer_reaction_rout}Routing");
                default: return new TextObject("{=AnvilHammer_reaction_default}Respond");
            }
        }
    }
}
