using AnvilAndHammerAI.Logging;
using AnvilAndHammerAI.Settings;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.ComponentInterfaces;

namespace AnvilAndHammerAI.Combat
{
    /// <summary>
    /// 统一伤害系统(吸收原 CasualtyDistributionPatch)。伤害管线最外层 AgentApplyDamageModel.CalculateDamage 的**单一** Postfix,
    /// **只乘最终 __result**(装甲折减 / RBM 内层 ComputeBlowDamage 均已算定,引擎在 InflictedDamage 定后才调本方法),零冲突。
    /// 一次命中只乘一个方向因子 + 可选一个冲撞因子,绝不内部叠乘同一信号。
    ///
    /// 口径(全员**编队级**方向;角色门控**替换**;逃兵优先):
    ///   1) 受击者在逃 → PursuitMultiplier(追击致命,与角色/方向无关,保留全兵种语义);
    ///   2) 否则按**受击者所属编队的朝向**判 正面/侧/背(全员编队级;编队缺失或朝向失真则回退逐兵朝向):
    ///        · 攻击者为 包抄步兵/轻骑/重骑 → 背=FlankRear、侧=FlankSide、正面=Stand(这几个角色的侧/背袭加成);
    ///        · 其余攻击者                  → 背=Rear、侧=Side、正面=Stand;
    ///   3) 骑兵冲撞(collisionData.IsHorseCharge;此时攻击者=坐骑→取 RiderAgent 角色)→ 轻骑×LightCavCharge、重骑×HeavyCavCharge,**乘**在方向因子上。
    /// 攻击者角色 = 其编队槽 Formation.FormationIndex(冲撞时取骑手的)。范围受 ScopeFilter(只玩家军时只玩家出手套用)。
    /// RBM 不碰 CalculateDamage,本层为本 mod 独占;保持 [HarmonyAfter("com.rbmcombat")][Priority.Last] 跑在最后只乘最终值。
    /// </summary>
    [HarmonyPatch(typeof(AgentApplyDamageModel), "CalculateDamage")]
    [HarmonyAfter("com.rbmcombat")]
    public static class DamageSystem
    {
        private const float DirDotThreshold = 0.3f; // dot(approach, victimFacing): >阈=背, <-阈=正面, 之间=侧

        [HarmonyPriority(Priority.Last)]
        public static void Postfix(ref float __result, ref AttackInformation attackInformation, ref AttackCollisionData collisionData)
        {
            if (__result <= 0f) return;

            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled || !s.CasualtyEnabled) return;

            var mission = Mission.Current;
            if (mission == null || !mission.IsFieldBattle) return;

            Telemetry.DmgCalls++;

            if (attackInformation.IsFriendlyFire) return;
            if (attackInformation.IsVictimAgentNull || !attackInformation.IsVictimAgentHuman) return;
            if (collisionData.AttackBlockedWithShield) return;

            Agent victim = attackInformation.VictimAgent;
            if (victim == null) return;
            if (!ScopeFilter.Applies(attackInformation.AttackerAgent)) return; // 范围:只玩家军时只玩家出手套用

            // 远程命中(箭矢/标枪)不套用本系统的方向/追击/冲撞缩放——这些是为近战侧/背袭设计的;
            // 套到箭矢上会把"背对射手的目标"误判成背袭(RearMultiplier),致远程伤害异常偏高。远程一律走原版/RBM 伤害。
            if (collisionData.IsMissile) return;

            FormationClass? role = AttackerRole(in attackInformation);
            bool flankRole = role.HasValue && IsFlankRole(role.Value);

            // ① 方向因子(逃兵优先;否则编队级方向,失真回退逐兵)
            float mult;
            bool fleeing = victim.IsRunningAway;
            if (fleeing)
            {
                mult = s.PursuitMultiplier; Telemetry.DmgPursuit++;
            }
            else
            {
                int dir = FormationHitDirection(in attackInformation);                     // 0正/1侧/2背/-1未知
                if (dir < 0) dir = HitDirection(collisionData.WeaponBlowDir, victim.LookDirection); // 回退逐兵
                if (flankRole)
                {
                    mult = dir == 2 ? s.FlankRearMultiplier : dir == 1 ? s.FlankSideMultiplier : s.StandMultiplier;
                    Telemetry.DmgFlank++;
                }
                else
                {
                    mult = dir == 2 ? s.RearMultiplier : dir == 1 ? s.SideMultiplier : s.StandMultiplier;
                }
                if (dir == 2) Telemetry.DmgRear++; else if (dir == 0) Telemetry.DmgStand++;
            }

            // ② 冲撞动量因子(骑兵冲撞;乘在方向因子上。冲撞减速幅度为原生 C++ 物理,无属性可改,首版不做。)
            if (s.ChargeMomentumEnabled && collisionData.IsHorseCharge && role.HasValue)
            {
                float cm = role.Value == FormationClass.HeavyCavalry ? s.HeavyCavChargeMult
                         : (role.Value == FormationClass.LightCavalry || role.Value == FormationClass.Cavalry) ? s.LightCavChargeMult
                         : 1f;
                if (cm != 1f) { mult *= cm; Telemetry.DmgCharge++; }
            }

            Telemetry.DmgScaled++;
            Telemetry.DmgMultSum += mult;
            __result *= mult;

            if (fleeing && s.PursuitGuaranteedKill)
            {
                float hp = attackInformation.VictimAgentHealth;
                if (__result < hp) __result = hp + 1f;
            }
        }

        private static bool IsFlankRole(FormationClass fc)
            => fc == FormationClass.HeavyInfantry || fc == FormationClass.LightCavalry
            || fc == FormationClass.Cavalry || fc == FormationClass.HeavyCavalry;

        /// <summary>攻击者角色槽:近战取其编队槽;冲撞(坐骑无编队)取 RiderAgent 编队槽。都没有则 null(不享角色加成/冲撞缩放)。</summary>
        private static FormationClass? AttackerRole(in AttackInformation ai)
        {
            Formation af = ai.AttackerFormation;
            if (af != null) return af.FormationIndex;
            Agent attacker = ai.AttackerAgent;
            Agent rider = attacker != null ? attacker.RiderAgent : null;
            if (rider != null && rider.Formation != null) return rider.Formation.FormationIndex;
            return null;
        }

        /// <summary>
        /// 编队级命中方向:approach(攻击者→受击者) · 受击者编队朝向。&gt;阈=背(2)、&lt;-阈=正面(0)、之间=侧(1);
        /// 无受击编队 / 朝向失真(溃散中可能为零向量)/ 位置重合 → -1(调用方回退逐兵朝向)。
        /// </summary>
        private static int FormationHitDirection(in AttackInformation ai)
        {
            Formation vf = ai.VictimFormation;
            if (vf == null) return -1;
            Vec2 facing = vf.CurrentDirection;                         // 平滑朝向(0.8 估计 + 0.2 指令)
            if (facing.LengthSquared < 1e-4f) facing = vf.Direction;
            if (facing.LengthSquared < 1e-4f) return -1;
            Vec2 approach = ai.VictimAgentPosition.AsVec2 - ai.AttackerAgentPosition.AsVec2;
            if (approach.LengthSquared < 1e-4f) return -1;
            float dot = Vec2.DotProduct(approach.Normalized(), facing.Normalized());
            return dot > DirDotThreshold ? 2 : dot < -DirDotThreshold ? 0 : 1;
        }

        /// <summary>逐兵回退:blowDir(攻击来向) · victim 朝向。口径同上(与原 CasualtyDistributionPatch 一致)。</summary>
        private static int HitDirection(Vec3 blowDir, Vec3 victimLook)
        {
            Vec2 b = new Vec2(blowDir.x, blowDir.y);
            Vec2 l = new Vec2(victimLook.x, victimLook.y);
            if (b.LengthSquared < 1e-4f || l.LengthSquared < 1e-4f) return 0;
            float dot = Vec2.DotProduct(b.Normalized(), l.Normalized());
            return dot > DirDotThreshold ? 2 : dot < -DirDotThreshold ? 0 : 1;
        }
    }
}
