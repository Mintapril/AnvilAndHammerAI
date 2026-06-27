using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Detection
{
    /// <summary>
    /// 编队几何的唯一来源:由朝向(无效则回退为"指向参考点")与 宽/深 算出 后/左翼/右翼 三接近点,
    /// 以及"取离参考点更近者"。各处算绕击落点的 facing 校验+回退、perp、halfW/halfD、rear/lflank/rflank 收敛于此,口径一致。
    /// </summary>
    public static class FormationGeometry
    {
        /// <summary>绕击站位余量(m):接近点 = 编队半宽/半深 + 此余量。原散落各处的 Standoff 收敛于此。</summary>
        public const float Standoff = 14f;

        /// <summary>一个敌编队的三接近点(世界 xy):后方、左翼、右翼。</summary>
        public readonly struct ApproachPoints
        {
            public readonly Vec2 Rear, LeftFlank, RightFlank;
            public ApproachPoints(Vec2 rear, Vec2 leftFlank, Vec2 rightFlank)
            { Rear = rear; LeftFlank = leftFlank; RightFlank = rightFlank; }
        }

        /// <summary>
        /// 按编队朝向与宽/深算 后/左翼/右翼 三接近点。朝向无效(零向量)时回退为"从编队中心指向 facingFallbackToward"。
        /// (+perp = 左翼,−perp = 右翼,与各调用点一致。)
        /// </summary>
        public static ApproachPoints ApproachPointsFor(Formation enemy, Vec2 facingFallbackToward, float standoff)
        {
            Vec2 epos = enemy.CachedAveragePosition;
            Vec2 face = enemy.Direction;
            if (!face.IsValid || face.LengthSquared < 1e-4f) face = (facingFallbackToward - epos).Normalized();
            Vec2 perp = face.LeftVec();
            float halfW = enemy.Width * 0.5f + standoff;
            float halfD = enemy.Depth * 0.5f + standoff;
            return new ApproachPoints(epos - face * halfD, epos + perp * halfW, epos - perp * halfW);
        }

        /// <summary>取离参考点更近的一个点(并列时取 a)。</summary>
        public static Vec2 Nearer(Vec2 a, Vec2 b, Vec2 reference)
            => (reference - a).LengthSquared <= (reference - b).LengthSquared ? a : b;
    }
}
