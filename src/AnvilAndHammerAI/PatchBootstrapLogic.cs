using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI
{
    /// <summary>
    /// 补丁引导:把 Harmony 补丁的安装时机从"加载期"挪到"任务内 Mission.Current 就绪后"。
    /// 加载期(Mission.Current==null)安装补丁会触发 MovementOrder 静态构造器 NRE 并永久毒化该类型(见 SubModule 注释)。
    /// OnBehaviorInitialize(任务 AfterStart,Mission.Current 已设)与首帧 OnMissionTick 各调一次 EnsurePatched(幂等)。
    /// </summary>
    public sealed class PatchBootstrapLogic : MissionLogic
    {
        private readonly SubModule _sub;

        public PatchBootstrapLogic(SubModule sub) { _sub = sub; }

        public override void OnBehaviorInitialize()
        {
            base.OnBehaviorInitialize();
            _sub?.EnsurePatched();
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            _sub?.EnsurePatched(); // 兜底:若 OnBehaviorInitialize 时 Mission.Current 尚未就绪,首帧必已就绪
        }
    }
}
