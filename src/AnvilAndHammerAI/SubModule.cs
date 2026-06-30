using System;
using AnvilAndHammerAI.Detection;
using AnvilAndHammerAI.Diagnostics;
using AnvilAndHammerAI.Formations;
using AnvilAndHammerAI.Logging;
using AnvilAndHammerAI.Morale;
using AnvilAndHammerAI.Safety;
using Bannerlord.UIExtenderEx;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI
{
    /// <summary>
    /// 模块入口。**关键:Harmony 补丁推迟到进入野战、Mission.Current 就绪后才安装**(见 EnsurePatched)——
    /// 规避加载期触发 MovementOrder 静态构造器 NRE 把该类型永久毒化的坑(历史教训)。
    ///
    /// 当前状态:运行时装:自动编队 + 中央调度器(含遇袭自发反应)+ 全兵种士气骨干(B/C)+ 战斗结束安全(D)
    ///   + 统一伤害系统(E:全员编队级方向 + 角色侧/背袭 + 骑兵冲撞缩放)+ 编队级速度系统 + 只读诊断。
    ///   Harmony 补丁:伤害系统 + 压制编队重分(FormationResortPatch,均 [HarmonyPatch] 经 PatchAll)、编队级速度(反射挂 SandBox/CustomBattle stat 模型)。
    /// </summary>
    public sealed class SubModule : MBSubModuleBase
    {
        public const string HarmonyId = "com.rangt.anvilandhammer";

        private Harmony _harmony;
        private static bool _patched;
        private UIExtender _uiExtender; // 持引用防 GC;编队标记士气条(prefab 注入 + VM mixin)经此注册

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            try
            {
                _harmony = new Harmony(HarmonyId);
                Log.Info($"OnSubModuleLoad: Harmony 实例已建 (id={HarmonyId})。补丁推迟到首次进入任务时安装。");
            }
            catch (Exception e)
            {
                Log.Error("OnSubModuleLoad 失败", e);
            }

            // UIExtenderEx:注册编队标记士气条的 prefab 注入(MoraleBarPrefabExtension)+ VM mixin(FormationMarkerMoraleMixin)。
            // 须在任何 movie 加载前(此处=载入期)Enable;失败则降级(标记 UI 不可用),不阻断 mod 主体。
            try
            {
                _uiExtender = UIExtender.Create("AnvilAndHammerAI");
                _uiExtender.Register(typeof(SubModule).Assembly);
                _uiExtender.Enable();
                Log.Info("OnSubModuleLoad: UIExtenderEx 已注册(编队标记士气条)。");
            }
            catch (Exception e)
            {
                Log.Error("OnSubModuleLoad: UIExtenderEx 注册失败(编队标记士气条将不可用)", e);
            }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);

            // ★不能在此判 IsFieldBattle —— MissionTeamAIType 此刻通常仍是 NoTeamAI,IsFieldBattle 假性 false 会误挡整个 mod。
            // 行为照常加入,各自在 tick / 谓词执行时(那时 IsFieldBattle 可靠)自我守门只野战生效。
            Log.Info($"[mission] OnMissionBehaviorInitialize: teamAIType={mission.MissionTeamAIType} isFieldBattle={mission.IsFieldBattle}(此刻多为未判定,真正门控在 tick)");

            // 补丁引导:首帧 IsFieldBattle 就绪后才安装全部补丁。
            mission.AddMissionBehavior(new PatchBootstrapLogic(this));

            // 全兵种士气系统(各自 tick 守门 IsFieldBattle)。共享 FormationShockPool:
            //   · 士气层写(池/越阈令牌/编队级棘轮档数/触底);诊断只读;
            //   · D 战斗结束安全读"该编队是否触底"决定是否放行 fade-out;
            //   · 中央调度器读作放锤判据(突破口震慑池逼近崩溃阈值)。
            var shockPool = new FormationShockPool();
            var rangedSensor = new RangedThreatSensor();
            var chargeSensor = new ChargeImpactSensor();

            // 把共享池交给只读士气读出口,供编队标记士气条(UI mixin)按编队取剩余士气。
            MoraleReadout.Register(shockPool);

            // 自动编队(ADR-0011 第 1 步):接管编队分配为 7 角色编队。自身按 IsFieldBattle/开关守门。
            mission.AddMissionBehavior(new AutoFormationMissionLogic());

            // 中央指挥调度器(ADR-0011 第 2-4 步):每 0.5s 按角色给 7 编队设权(读空间脑弧覆盖;放锤=突破口震慑池逼近崩溃阈值)。
            mission.AddMissionBehavior(new CommandSchedulerMissionLogic(shockPool));

            // 受远程攻击传感器:监听箭矢/标枪碰撞(命中盾牌/士兵 + 附近落点),喂"受远程攻击"压力源。先挂以便首帧起计。
            mission.AddMissionBehavior(rangedSensor);

            // 冲锋冲击传感器:监听真实骑兵冲撞命中(IsHorseCharge,按角色×方向累加、慢衰减),喂"冲锋冲击"震慑。先挂以便首帧起计。
            mission.AddMissionBehavior(chargeSensor);

            // 编队级士气层(替代旧 MoraleBackbone):传感器→压力→池→决策→效果。先于诊断挂,保证诊断读到本 tick 池值/遥测。
            mission.AddMissionBehavior(new FormationMoraleMissionLogic(shockPool, rangedSensor, chargeSensor));

            // 只读诊断(5 秒写日志:编队士气/池 + per-side 逃兵比例 + 子系统心跳)。
            mission.AddMissionBehavior(new AnvilDiagnosticsMissionLogic(shockPool));

            // D 战斗结束安全(只护本方未触底逃兵不 fade-out;敌方/触底编队放行)。
            mission.AddMissionBehavior(new BattleEndSafetyMissionLogic(shockPool));

            // 箭矢轨迹可视化(纯渲染,每帧画在飞导弹的世界空间彩色线段;受 MCM「显示箭矢轨迹」+ IsFieldBattle 门控)。
            mission.AddMissionBehavior(new Detection.MissileTrailMissionLogic());
        }

        /// <summary>
        /// 首次进入任务、Mission.Current 就绪后安装全部补丁(只装一次)。安装前先在有效上下文主动初始化
        /// MovementOrder 静态构造器(防后续补丁触发其 cctor 在 Mission.Current==null 时 NRE 毒化该类型)。
        /// </summary>
        internal void EnsurePatched()
        {
            if (_patched || _harmony == null) return;
            Mission cur = Mission.Current;
            if (cur == null || !cur.IsFieldBattle) return; // 必须进入野战且 Mission.Current 就绪
            _patched = true; // 先置位:防 re-tick 重入导致 PatchAll 重复应用(补丁不可幂等重装)

            // 在有效上下文里强制成功初始化 MovementOrder cctor(防后续补丁触发其 cctor 在 Mission.Current==null 时 NRE 毒化该类型)。
            try { MovementOrder _ = MovementOrder.MovementOrderStop; }
            catch (Exception e) { Log.Error("EnsurePatched: 预热 MovementOrder cctor 失败", e); }

            // E 伤亡分布([HarmonyPatch] 经 PatchAll 收集)。独立 try:本步失败不阻断后续 RBM 重分禁用的安装。
            try
            {
                _harmony.PatchAll(typeof(SubModule).Assembly);
                int patched = 0;
                foreach (var mi in _harmony.GetPatchedMethods())
                {
                    patched++;
                    Log.Info($"  [patched] {mi.DeclaringType?.FullName}.{mi.Name}");
                }
                Log.Info($"EnsurePatched: PatchAll 完成 (已补丁方法数={patched})。");
            }
            catch (Exception e) { Log.Error("EnsurePatched: PatchAll 失败", e); }

            // 压制 RBM 自己的编队重分(反射 patch RBM 的 ManageFormationCountsPatch prefix;它直接按兵种塞 4 槽,不经 SplitFormationClassIntoGivenNumber)。
            // 与 [HarmonyPatch] 特性类 FormationResortPatch(压制 vanilla 合并原语,经上面 PatchAll 收集)互补,二者缺一不可。独立 try。
            try { Formations.RbmResortPatch.Apply(_harmony); }
            catch (Exception e) { Log.Error("EnsurePatched: 压制 RBM 编队重分失败", e); }

            // 编队级速度系统(反射挂 SandBox/CustomBattle 的 AgentStatCalculateModel)。独立 try:缺席/失败不拖累其它。
            try { Combat.FormationSpeedPatch.Apply(_harmony); }
            catch (Exception e) { Log.Error("EnsurePatched: 挂载编队级速度系统失败", e); }
        }
    }
}
