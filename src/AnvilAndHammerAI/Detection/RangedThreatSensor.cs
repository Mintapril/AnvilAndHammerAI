using System;
using System.Collections.Generic;
using AnvilAndHammerAI.Morale;
using AnvilAndHammerAI.Settings;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Detection
{
    /// <summary>
    /// 受远程攻击传感器(喂"受远程攻击"压力源)。监听每一次箭矢/标枪碰撞:
    ///   · 命中盾牌 / 直接命中士兵 → 受击者所属编队累加威胁;
    ///   · 地面未命中(OnMissileHit 对 victim==null 也触发,带落点 CollisionGlobalPosition)→ 落点附近最近的受射编队累加较小威胁。
    /// 仅敌方火力计入。per-formation 威胁强度每帧**指数衰减**成短窗,RangedFirePressure 只读 GetThreat。
    /// OnMissileHit 是主线程回调(碰撞反应里还做实体/网络操作)→ 无锁 Dictionary 安全。
    /// </summary>
    public sealed class RangedThreatSensor : MissionLogic
    {
        private readonly struct FormRef
        {
            public readonly Formation F; public readonly Team T; public readonly Vec2 Pos;
            public FormRef(Formation f, Team t, Vec2 pos) { F = f; T = t; Pos = pos; }
        }

        private readonly Dictionary<Formation, float> _threat = new Dictionary<Formation, float>();
        private readonly List<Formation> _keyScratch = new List<Formation>();
        private readonly List<FormRef> _formCache = new List<FormRef>(); // 每帧重建一次,近失归属走它(免每支箭重走 m.Teams)

        public float GetThreat(Formation f)
            => f != null && _threat.TryGetValue(f, out float v) ? v : 0f;

        public override void OnMissileHit(Agent attacker, Agent victim, bool isCanceled, AttackCollisionData collisionData)
        {
            base.OnMissileHit(attacker, victim, isCanceled, collisionData);

            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled) return;
            Mission m = Mission.Current;
            if (m == null || !m.IsFieldBattle) return;
            if (attacker == null || attacker.Team == null) return;

            if (victim != null && victim.IsHuman)
            {
                // 命中盾牌 / 士兵 → 受击者编队(仅敌方火力)
                if (victim.Team == null || !attacker.Team.IsEnemyOf(victim.Team)) return;
                Formation f = victim.Formation;
                if (f == null) return;
                float w = collisionData.AttackBlockedWithShield
                    ? MoraleTuning.RangedHitShieldWeight
                    : MoraleTuning.RangedHitBodyWeight;
                Add(f, w);
            }
            else if (victim == null)
            {
                // 地面未命中 → 落点附近最近的"敌方(被射方)"编队(走每帧缓存,免每支箭重走 m.Teams)
                Vec2 p = collisionData.CollisionGlobalPosition.AsVec2;
                Formation nearest = NearestShotAtFormation(attacker.Team, p, MoraleTuning.RangedNearMissRadius);
                if (nearest != null) Add(nearest, MoraleTuning.RangedNearMissWeight);
            }
        }

        private void Add(Formation f, float w)
        {
            _threat.TryGetValue(f, out float cur);
            _threat[f] = cur + w;
        }

        private Formation NearestShotAtFormation(Team shooterTeam, Vec2 pos, float radius)
        {
            float best = radius * radius; // 比较平方,免开方
            Formation result = null;
            for (int i = 0; i < _formCache.Count; i++)
            {
                FormRef e = _formCache[i];
                if (!shooterTeam.IsEnemyOf(e.T)) continue;
                float d2 = pos.DistanceSquared(e.Pos);
                if (d2 < best) { best = d2; result = e.F; }
            }
            return result;
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled) return; // 与 OnMissileHit 守门一致:关闭总开关则不重建缓存/不衰减
            Mission m = Mission.Current;
            if (m == null || !m.IsFieldBattle) return;

            // 每帧重建编队缓存(位置/队伍),供近失归属用——把"每支箭一次 m.Teams 嵌套遍历"降为"每帧一次"。
            _formCache.Clear();
            foreach (Team t in m.Teams)
            {
                if (t == null) continue;
                foreach (Formation f in t.FormationsIncludingEmpty)
                {
                    if (f.CountOfUnits == 0) continue;
                    _formCache.Add(new FormRef(f, t, f.CachedAveragePosition));
                }
            }

            if (_threat.Count == 0) return;

            // 真指数衰减:factor=exp(-rate*dt) 恒 ∈(0,1],卡顿长帧也不一步清零(线性步进会)。
            float factor = (float)Math.Exp(-MoraleTuning.RangedThreatDecayPerSecond * dt);

            // 不能在 foreach 字典时改值/删键 → 先复制键再回写/剪枝(_keyScratch 复用,无每帧分配)
            _keyScratch.Clear();
            foreach (Formation k in _threat.Keys) _keyScratch.Add(k);
            for (int i = 0; i < _keyScratch.Count; i++)
            {
                Formation k = _keyScratch[i];
                float v = _threat[k] * factor;
                if (v < 0.01f) _threat.Remove(k);
                else _threat[k] = v;
            }
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();
            _threat.Clear();
            _formCache.Clear();
        }
    }
}
