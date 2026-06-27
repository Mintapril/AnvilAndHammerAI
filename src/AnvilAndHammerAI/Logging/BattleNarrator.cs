using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AnvilAndHammerAI.Settings;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Logging
{
    /// <summary>
    /// 战场事件播报(左下角游戏信息条)。帮助玩家看懂 AI 在干什么:重骑放锤、轻骑掩护/绕击、包抄步兵绕后、编队溃逃/重整、遇袭反应等。
    /// 按"编队 × 频道 × 模式"去重:**只在某编队某频道的模式切换时播一次**,同模式不重复;并对每个(编队,频道)加冷却节流,防门控抖动刷屏。
    /// message 为空 = 静默记录模式(用于退出某事件态以便下次再播)。总开关 AnvilSettings.BattleEventLog。
    /// </summary>
    public static class BattleNarrator
    {
        private const float Cooldown = 3f; // 同一(编队,频道)两次实播的最小间隔(s)

        private static readonly Color CTactic = new Color(0.55f, 0.82f, 1f); // 淡蓝:我方战术动作
        private static readonly Color CRout = new Color(1f, 0.5f, 0.4f);    // 红:溃逃
        private static readonly Color CRally = new Color(0.5f, 1f, 0.6f);   // 绿:重整

        private sealed class State
        {
            public readonly Dictionary<string, (string mode, float time)> Ch =
                new Dictionary<string, (string, float)>();
        }
        private static readonly ConditionalWeakTable<Formation, State> _state = new ConditionalWeakTable<Formation, State>();

        private static bool Enabled => AnvilSettings.Instance?.BattleEventLog == true;

        /// <summary>编队进入某频道的新模式时播报一次。modeKey 改变才考虑播;message 为空则只静默记录模式(不播)。</summary>
        public static void Mode(Formation f, string channel, string modeKey, string message)
            => Mode(f, channel, modeKey, message, CTactic);

        public static void Mode(Formation f, string channel, string modeKey, string message, Color color)
        {
            if (!Enabled || f == null) return;
            State st = _state.GetValue(f, _ => new State());
            st.Ch.TryGetValue(channel, out var prev);
            if (prev.mode == modeKey) return;                                   // 模式未变 → 不播
            float now = Mission.Current != null ? Mission.Current.CurrentTime : 0f;
            bool show = !string.IsNullOrEmpty(message) && (prev.time == 0f || now - prev.time >= Cooldown);
            st.Ch[channel] = (modeKey, show ? now : prev.time);                 // 仅实播时推进节流时间(静默/节流保留旧时间)
            if (show) Display(message, color);
        }

        /// <summary>整队溃逃(池越阈上升沿)。isEnemy 决定阵营标签(我方/敌军)。</summary>
        public static void OnRout(Formation f, bool isEnemy)
            => Mode(f, "morale", "rout", SideRoleText("{=AnvilHammer_narrator_rout}{SIDE} {ROLE} routed!", isEnemy, f), CRout);

        /// <summary>整队重整(仅在此前播报过溃逃后才播,避免给从未溃逃的编队报"重整")。</summary>
        public static void OnRally(Formation f, bool isEnemy)
        {
            if (!Enabled || f == null) return;
            State st = _state.GetValue(f, _ => new State());
            st.Ch.TryGetValue("morale", out var prev);
            if (prev.mode != "rout") return;
            Mode(f, "morale", "rally", SideRoleText("{=AnvilHammer_narrator_rally}{SIDE} {ROLE} rallied!", isEnemy, f), CRally);
        }

        /// <summary>把"阵营 + 兵种 + 动作"模板填充并解析为本地化串(英文内联回退,简中由语言表覆盖)。</summary>
        private static string SideRoleText(string template, bool isEnemy, Formation f)
        {
            var t = new TextObject(template);
            t.SetTextVariable("SIDE", new TextObject(isEnemy ? "{=AnvilHammer_side_enemy}Enemy" : "{=AnvilHammer_side_friendly}Our"));
            t.SetTextVariable("ROLE", RoleName(f));
            return t.ToString();
        }

        // msg 可含本地化键 {=id}English:OnRout/OnRally 传入的已是解析后的串(此处再包一层为幂等),
        // 战术播报(Say)传入的是原始 {=id}English,在此解析成当前语言。
        private static void Display(string msg, Color color)
        {
            try { InformationManager.DisplayMessage(new InformationMessage(new TextObject(msg).ToString(), color)); }
            catch { /* 无 mission 上下文等,忽略 —— 日志绝不能让游戏崩 */ }
        }

        /// <summary>编队槽 → 本地化兵种名(溃逃/重整播报用;敌我通用)。</summary>
        private static TextObject RoleName(Formation f)
        {
            switch (f.FormationIndex)
            {
                case FormationClass.Infantry: return new TextObject("{=AnvilHammer_role_infantry}infantry");
                case FormationClass.HeavyInfantry: return new TextObject("{=AnvilHammer_role_heavy_infantry}heavy infantry");
                case FormationClass.Ranged: return new TextObject("{=AnvilHammer_role_archers}archers");
                case FormationClass.HorseArcher: return new TextObject("{=AnvilHammer_role_horse_archers}horse archers");
                case FormationClass.LightCavalry: return new TextObject("{=AnvilHammer_role_light_cavalry}light cavalry");
                case FormationClass.Cavalry: return new TextObject("{=AnvilHammer_role_cavalry}cavalry");
                case FormationClass.HeavyCavalry: return new TextObject("{=AnvilHammer_role_heavy_cavalry}heavy cavalry");
                default: return new TextObject("{=AnvilHammer_role_troops}troops");
            }
        }
    }
}
