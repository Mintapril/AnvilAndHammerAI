using System;
using System.Reflection;
using AnvilAndHammerAI.Logging;
using AnvilAndHammerAI.Settings;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Combat
{
    /// <summary>
    /// 编队级速度系统。Postfix AgentStatCalculateModel.UpdateAgentStats(顶层 dispatcher,在 vanilla 全量计算 +
    /// RBM 的 stamina/MountSpeed 内层改动**全部算完后**才跑),按角色槽**乘**最终速度:
    ///   · 包抄步兵(HeavyInfantry,步行)→ 乘骑手脚程 MaxSpeedMultiplier ×1.1;
    ///   · 轻骑(LightCavalry/Cavalry)→ 坐骑 MountSpeed ×1.1;骑射(HorseArcher)×1.15;重骑(HeavyCavalry)×0.8。
    /// 坐骑本身无角色 → 取 RiderAgent 的编队槽。只 `*=`(幂等于单次重算),从不缓存累乘。范围受 ScopeFilter。
    ///
    /// SandBox 程序集刻意不引用(见 csproj 注释)→ 反射挂载:战役 SandBox.GameComponents.SandboxAgentStatCalculateModel +
    /// 自定义战斗 CustomBattleAgentStatCalculateModel。缺席/改版 → 记日志跳过(与 RbmResortPatch 同风格,可降级)。
    /// 注:坐骑减速/冲撞动量保持为原生 C++ 物理,无对应 AgentDrivenProperty,本系统只改速度,不改冲撞减速。
    /// </summary>
    public static class FormationSpeedPatch
    {
        private static bool _applied;

        public static void Apply(Harmony h)
        {
            if (_applied) return;
            _applied = true;
            PatchModel(h, "SandBox.GameComponents.SandboxAgentStatCalculateModel");      // 战役野战
            PatchModel(h, "TaleWorlds.MountAndBlade.CustomBattleAgentStatCalculateModel"); // 自定义战斗
        }

        private static void PatchModel(Harmony h, string typeName)
        {
            Type t = AccessTools.TypeByName(typeName);
            if (t == null) { Log.Info($"[speed] 未找到 {typeName}(该模式不适用?),跳过。"); return; }
            MethodInfo m = AccessTools.Method(t, "UpdateAgentStats", new[] { typeof(Agent), typeof(AgentDrivenProperties) });
            if (m == null) { Log.Error($"[speed] 未找到 {typeName}.UpdateAgentStats(Agent,AgentDrivenProperties),跳过。"); return; }
            try
            {
                h.Patch(m, postfix: new HarmonyMethod(typeof(FormationSpeedPatch), nameof(Postfix)));
                Log.Info($"[speed] 已 patch {typeName}.UpdateAgentStats(编队级速度)。");
            }
            catch (Exception e) { Log.Error($"[speed] patch {typeName}.UpdateAgentStats 失败。", e); }
        }

        public static void Postfix(Agent agent, AgentDrivenProperties agentDrivenProperties)
        {
            if (agent == null || agentDrivenProperties == null) return;
            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled || !s.FormationSpeedEnabled) return;
            var mission = Mission.Current;
            if (mission == null || !mission.IsFieldBattle) return;

            if (agent.IsHuman)
            {
                // 步行角色脚程:仅包抄步兵(骑兵骑手的疾驰走下面坐骑 MountSpeed 分支,避免对同一移动双乘)。
                if (!ScopeFilter.Applies(agent)) return;
                Formation f = agent.Formation;
                if (f == null) return;
                if (f.FormationIndex == FormationClass.HeavyInfantry)
                    agentDrivenProperties.MaxSpeedMultiplier *= s.FlankInfantrySpeedMult;
            }
            else
            {
                // 坐骑疾驰:按骑手角色乘 MountSpeed。
                Agent rider = agent.RiderAgent;
                if (rider == null || !ScopeFilter.Applies(rider)) return;
                Formation f = rider.Formation;
                if (f == null) return;
                float m = CavMountMult(f.FormationIndex, s);
                if (m != 1f) agentDrivenProperties.MountSpeed *= m;
            }
        }

        private static float CavMountMult(FormationClass fc, AnvilSettings s)
        {
            switch (fc)
            {
                case FormationClass.LightCavalry:
                case FormationClass.Cavalry: return s.LightCavSpeedMult;
                case FormationClass.HorseArcher: return s.HorseArcherSpeedMult;
                case FormationClass.HeavyCavalry: return s.HeavyCavSpeedMult;
                default: return 1f;
            }
        }
    }
}
