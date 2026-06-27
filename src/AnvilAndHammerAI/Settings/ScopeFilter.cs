using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Settings
{
    /// <summary>
    /// 范围过滤(MCM "只影响我方军队")。各子系统在其**作用主体(actor)**的队伍处调 Applies 守门:
    /// - PlayerArmyOnly=false(默认):全部阵营生效。
    /// - =true:本 mod 对**队伍**的改动只作用于**玩家队**——自动编队、统一战场指挥与骑兵绕击只编排玩家队
    ///   (敌方交回 vanilla/RBM,含 RBM 编队重分);伤亡分布按出手方判定,仅玩家方出手时套用。
    /// **唯一例外:编队级士气系统(成片溃逃/集结/越打越脆)是地基,对双方恒生效,不走本过滤。**
    /// 注:作用主体=动作发起方。玩家骑兵冲锋自然会冲垮敌人(效果落在敌方),那仍属"玩家军队有效",不视为对敌生效。
    /// 战斗结束安全(D)本就只护玩家侧,不随此开关变。
    /// </summary>
    public static class ScopeFilter
    {
        public static bool Applies(Team team)
        {
            var s = AnvilSettings.Instance;
            if (s == null || !s.PlayerArmyOnly) return true; // 全部生效
            return team != null && team.IsPlayerTeam;        // 只玩家队
        }

        public static bool Applies(Formation f) => Applies(f?.Team);
        public static bool Applies(Agent a) => Applies(a?.Team);
    }
}
