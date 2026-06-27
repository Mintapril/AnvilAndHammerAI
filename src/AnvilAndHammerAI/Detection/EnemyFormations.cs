using System.Collections.Generic;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Detection
{
    /// <summary>
    /// 遍历某队所有敌方"活编队"(非空)的唯一入口。各处 `foreach Team → 敌? → foreach Formation → 有人?` 的样板收敛于此,
    /// 口径一致(含 null / 空编队守卫)。0.5s 级调用,迭代器开销可忽略。
    /// 注:不用于 FormationScanner 每编队每 tick 的近敌骑扫描(那里坚持稳态零分配,保留手写循环)。
    /// </summary>
    public static class EnemyFormations
    {
        public static IEnumerable<Formation> Of(Team myTeam, Mission m)
        {
            if (myTeam == null || m == null) yield break;
            foreach (Team t in m.Teams)
            {
                if (t == null || !t.IsEnemyOf(myTeam)) continue;
                foreach (Formation f in t.FormationsIncludingEmpty)
                    if (f != null && f.CountOfUnits > 0) yield return f;
            }
        }

        /// <summary>self 队最近的活敌编队(按中心平方距离);self 为空/无敌 → null。
        /// FlankChargeBehavior 与 HorseArcherKiteBehavior 的"最近敌编队"回退共用此处(原两处逐字重复)。</summary>
        public static Formation Nearest(Formation self, Mission m)
        {
            if (self == null) return null;
            Vec2 pos = self.CachedAveragePosition;
            Formation best = null;
            float bestD = float.MaxValue;
            foreach (Formation f in Of(self.Team, m))
            {
                float d = (f.CachedAveragePosition - pos).LengthSquared;
                if (d < bestD) { bestD = d; best = f; }
            }
            return best;
        }
    }
}
