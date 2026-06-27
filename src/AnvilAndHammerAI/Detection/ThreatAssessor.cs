using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Detection
{
    /// <summary>
    /// 遇袭威胁评估(反应覆盖层用)。给一支友军编队,找出在 <paramref name="range"/> 内、兵力值达门槛的最近敌"近战骑兵"编队。
    /// 与 TacticalBrain 的 cav-cover 同口径(IsCavalryFormation + 兵力值比 + 距离),但**泛化到任意编队**、且用更近的"贴脸"距离。
    /// 纯读公有 API、廉价(遍历敌编队 ≤约16),稳态无每帧分配。
    /// </summary>
    public static class ThreatAssessor
    {
        /// <summary>
        /// 本编队当前最近的"急性冲锋"敌近战骑兵编队(无则 null)。range/ratio 由调用方按角色给:
        /// 步兵架枪门槛低、弓兵后撤门槛更低、骑兵反冲门槛略高。
        /// </summary>
        public static Formation NearestChargingCav(Formation self, Team myTeam, Mission m, float range, float ratio)
        {
            if (self == null || self.CountOfUnits == 0 || myTeam == null || m == null) return null;
            int selfStr = FormationStrength.Of(self);
            float threatMin = ratio * selfStr;
            Vec2 anchor = self.CachedAveragePosition;
            float bestD = range;
            Formation best = null;
            foreach (Formation f in EnemyFormations.Of(myTeam, m))
            {
                var qs = f.QuerySystem;
                if (qs == null || !qs.IsCavalryFormation) continue;          // 仅近战骑兵(同 cav-cover 口径,排除骑射)
                float d = anchor.Distance(f.CachedAveragePosition);
                if (d > range) continue;
                if (selfStr > 0 && FormationStrength.Of(f) < threatMin) continue; // 兵力值太小 → 非威胁(去噪)
                if (d < bestD) { bestD = d; best = f; }
            }
            return best;
        }
    }
}
