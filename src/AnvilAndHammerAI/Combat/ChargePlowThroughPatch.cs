using AnvilAndHammerAI.Logging;
using AnvilAndHammerAI.Settings;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Combat
{
    /// <summary>
    /// 重骑冲撞"穿透"(冲撞动量的减速半项)。引擎/RBM 的马匹冲撞**减速本身是原生 C++ 物理、无属性可直接设**;
    /// 但模型层有等效杠杆:被**击倒/击退**的目标会被清出马的前进路径,马不再被站立士兵当作实体障碍挡停 →
    /// 实际"撞击减速幅度变小、穿阵继续"。引擎用 <c>GetHorseChargePenetration()</c> 喂击倒判定
    /// (MissionCombatMechanicsHelper.DecideAgentKnockedDownByBlow,见反编译 76283-76287),但它无参数(全局、不分敌我/角色)。
    ///
    /// 本补丁 Postfix 该静态 helper(所有 stat 模型的 DecideAgentKnockedDownByBlow 都委托到它,反编译 57453 调点):
    /// **仅对 重骑(HeavyCavalry)对敌的冲撞**强制击倒 → 穿透(减速更小)。受 ScopeFilter + 仅对敌 + HeavyCavPlowThrough 开关门控(独立于伤害侧的 ChargeMomentumEnabled)。
    /// RBM 不 patch 此 helper(只 patch DecideMountReared/Dismounted,见 rbm_src/HorseChanges.cs),无冲突;保持 [HarmonyAfter("com.rbmcombat")]。
    /// 注:伤害提升半项(轻×0.8/重×2)在 DamageSystem 按最终伤害缩放;此处只管减速/穿透。
    /// </summary>
    [HarmonyPatch(typeof(MissionCombatMechanicsHelper), "DecideAgentKnockedDownByBlow")]
    [HarmonyAfter("com.rbmcombat")]
    public static class ChargePlowThroughPatch
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(ref bool __result, Agent attackerAgent, Agent victimAgent, ref AttackCollisionData collisionData)
        {
            if (__result) return;                       // 引擎已判击倒 → 无需追加
            if (!collisionData.IsHorseCharge) return;   // 仅马匹冲撞

            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled || !s.HeavyCavPlowThrough) return;
            var mission = Mission.Current;
            if (mission == null || !mission.IsFieldBattle) return;

            if (attackerAgent == null || victimAgent == null) return;
            Agent rider = attackerAgent.IsMount ? attackerAgent.RiderAgent : attackerAgent; // 冲撞时 attacker=坐骑,角色在骑手
            if (rider == null || !ScopeFilter.Applies(rider)) return;                         // 范围:只玩家军时只玩家骑兵
            if (rider.Team == null || victimAgent.Team == null || !rider.Team.IsEnemyOf(victimAgent.Team)) return; // 仅对敌,免撞翻友军

            Formation f = rider.Formation;
            if (f == null || f.FormationIndex != FormationClass.HeavyCavalry) return;          // 仅重骑(锤)穿透

            __result = true;       // 重骑冲撞击倒目标 → 清出路径,马穿阵继续,撞击减速更小
            Telemetry.DmgPlow++;
        }
    }
}
