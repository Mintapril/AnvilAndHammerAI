using System;
using System.Collections.Generic;
using AnvilAndHammerAI.Logging;
using AnvilAndHammerAI.Morale;
using AnvilAndHammerAI.Settings;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Detection
{
    /// <summary>
    /// 冲锋冲击传感器(喂"冲锋冲击"震慑)。监听每一次**真实骑兵冲撞命中**(<see cref="AttackCollisionData.IsHorseCharge"/>):
    ///   · 受击者编队累加"冲击单位" = (本次冲撞骑手强度 / 被冲编队强度) × 角色(重骑×3 / 轻骑×1) × 方向(背 1 / 侧 0.5 / 正 0.15);
    ///   · 用**强度比**而非命中计数:一次"同兵力"冲锋全连接 → Σ(骑手强度)= 编队强度 → Σ强度比 = 1,与命中数 K **无关** → 增益可精确反解。
    ///   · 仅敌方冲锋计入。per-formation 冲击强度每帧**慢衰减**(半衰期 60s),编排层每拍镜像写入 <c>FormationShockState.ChargeShock</c>。
    /// 替代旧"逼近距离/速度代理"(那测的是恐吓,非冲击),使"1 次重骑背冲 / 60s 内 3 次轻骑背冲 → 整队溃逃"可被精确表达。
    /// <see cref="OnAgentHit"/> 是主线程回调 → 无锁 Dictionary 安全;零 Harmony、零反射、对双方恒生效(忽略 ScopeFilter,与士气层一致)。
    /// </summary>
    public sealed class ChargeImpactSensor : MissionLogic
    {
        private const float DirDotThreshold = 0.3f; // dot(approach, victimFacing): >阈=背, <-阈=正面, 之间=侧(与 DamageSystem 同口径)

        private readonly Dictionary<Formation, float> _shock = new Dictionary<Formation, float>();
        private readonly List<Formation> _keyScratch = new List<Formation>();

        public float GetShock(Formation f)
            => f != null && _shock.TryGetValue(f, out float v) ? v : 0f;

        /// <summary>编队溃逃时释放其累积冲击震慑(已"兑现",不再滞留逼其反复再溃);此后新的冲撞命中会重新累加。</summary>
        public void Discharge(Formation f) { if (f != null) _shock.Remove(f); }

        public override void OnAgentHit(Agent affectedAgent, Agent affectorAgent, in MissionWeapon affectorWeapon,
            in Blow blow, in AttackCollisionData collisionData)
        {
            base.OnAgentHit(affectedAgent, affectorAgent, in affectorWeapon, in blow, in collisionData);

            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled || !s.ChargeShockPressureEnabled) return;
            Mission m = Mission.Current;
            if (m == null || !m.IsFieldBattle) return;
            if (!collisionData.IsHorseCharge) return;                       // 只计真实骑兵冲撞命中

            Agent victim = affectedAgent;
            if (victim == null || !victim.IsHuman || victim.Team == null) return;
            Formation vf = victim.Formation;
            if (vf == null) return;
            if (affectorAgent == null || affectorAgent.Team == null) return;
            if (!affectorAgent.Team.IsEnemyOf(victim.Team)) return;         // 仅敌方冲锋

            // 冲撞命中的"攻击者"通常是坐骑 → 取骑手(有编队/兵种信息的那个)。
            Agent rider = affectorAgent.Formation != null ? affectorAgent
                        : (affectorAgent.RiderAgent != null ? affectorAgent.RiderAgent : affectorAgent);

            float roleFactor = ChargeRoleFactor(rider);
            if (roleFactor <= 0f) return;                                   // 非近战骑兵角色(如骑射/步兵)不计冲击

            float dirFactor = ChargeDirFactor(victim, affectorAgent, vf);

            // 强度比 = 本次冲撞骑手强度 / 被冲编队强度。一次"同兵力"冲锋全连接时 Σ(骑手强度)= 编队强度 → Σ强度比 = 1(与命中数 K 无关)。
            int targetStr = FormationStrength.Of(vf);
            if (targetStr <= 0) return;
            float riderStr = (rider != null && rider.Character != null)
                ? rider.Character.GetBattleTier() * FormationStrength.CavalryStrengthMultiplier : 0f;
            if (riderStr <= 0f) return;

            Add(vf, (riderStr / targetStr) * roleFactor * dirFactor);
            Telemetry.ChargeHits++;
        }

        /// <summary>冲锋者角色权重:重骑×3 / 轻骑×1 / 其余 0(按骑手编队槽 FormationIndex)。</summary>
        private static float ChargeRoleFactor(Agent rider)
        {
            Formation af = rider != null ? rider.Formation : null;
            if (af == null) return MoraleTuning.ChargeLightFactor;          // 无编队信息:按轻骑计(保守)
            switch (af.FormationIndex)
            {
                case FormationClass.HeavyCavalry: return MoraleTuning.ChargeHeavyFactor;
                case FormationClass.LightCavalry:
                case FormationClass.Cavalry: return MoraleTuning.ChargeLightFactor;
                default: return 0f;                                         // 骑射/步兵等非冲锋角色
            }
        }

        /// <summary>方向权重:背 1 / 侧 0.5 / 正 0.15。用受击者编队朝向判 approach·facing(失真则回退逐兵朝向)。</summary>
        private static float ChargeDirFactor(Agent victim, Agent attacker, Formation vf)
        {
            Vec2 facing = vf.CurrentDirection;
            if (facing.LengthSquared < 1e-4f) facing = vf.Direction;
            if (facing.LengthSquared < 1e-4f)
            {
                Vec3 look = victim.LookDirection;
                facing = new Vec2(look.x, look.y);
            }
            Vec2 approach = victim.Position.AsVec2 - attacker.Position.AsVec2;
            if (facing.LengthSquared < 1e-4f || approach.LengthSquared < 1e-4f) return MoraleTuning.ChargeRearDirFactor; // 判不出方向 → 当背冲(冲撞多为穿插)
            float dot = Vec2.DotProduct(approach.Normalized(), facing.Normalized());
            return dot > DirDotThreshold ? MoraleTuning.ChargeRearDirFactor
                 : dot < -DirDotThreshold ? MoraleTuning.ChargeFrontDirFactor
                 : MoraleTuning.ChargeSideDirFactor;
        }

        private void Add(Formation f, float w)
        {
            _shock.TryGetValue(f, out float cur);
            _shock[f] = cur + w; // 无界:溃逃即 Discharge 清零防累加失控;归一化在编排层按人数做
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled) return;
            Mission m = Mission.Current;
            if (m == null || !m.IsFieldBattle) return;
            if (_shock.Count == 0) return;

            // 真指数慢衰减:factor=exp(-rate*dt) 恒 ∈(0,1];卡顿长帧也不一步清零。
            float factor = (float)Math.Exp(-MoraleTuning.ChargeShockDecayPerSecond * dt);

            _keyScratch.Clear();
            foreach (Formation k in _shock.Keys) _keyScratch.Add(k);
            for (int i = 0; i < _keyScratch.Count; i++)
            {
                Formation k = _keyScratch[i];
                float v = _shock[k] * factor;
                if (v < 0.01f) _shock.Remove(k);
                else _shock[k] = v;
            }
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();
            _shock.Clear();
        }
    }
}
