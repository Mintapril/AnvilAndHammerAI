using AnvilAndHammerAI.Settings;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Formations
{
    /// <summary>
    /// "让出玩家亲自指挥"的**统一判定**——中央调度器(权重层)与自动编队(成员层)共用,保证两处口径一致。
    /// 玩家任 general 的队里,经指挥盘下达真实命令(移动/朝向/阵型/冲锋等)的编队会被引擎置 IsAIControlled=false
    /// (见 OrderController.BeforeSetOrder;RTS Camera/CommandSystem 等指挥 Mod 也走同一标准路径)。
    /// 此时本 mod 既不该覆盖其行为权重,也不该把它的兵按职能重排走/塞入。MP/观战下 IsAIControlled 不可靠 → 守卫 !IsClientOrReplay。
    /// </summary>
    public static class PlayerControl
    {
        /// <summary>是否启用让行(总开关 RespectPlayerOrders + 非 MP/观战)。也用于"玩家主角不被自动归槽"。</summary>
        public static bool RespectEnabled =>
            AnvilSettings.Instance?.RespectPlayerOrders == true && !GameNetwork.IsClientOrReplay;

        /// <summary>该编队是否被玩家亲自接管(本 mod 应让行,不动其权重/成员)。f 为 null → false。</summary>
        public static bool IsPlayerCommanded(Formation f)
        {
            if (f == null || !RespectEnabled) return false;
            Team t = f.Team;
            return t != null && t.IsPlayerGeneral && !f.IsAIControlled;
        }
    }
}
