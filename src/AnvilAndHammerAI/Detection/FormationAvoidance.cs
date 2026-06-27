using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Detection
{
    /// <summary>
    /// 编队级避让(统一原语)。把"抗重叠(站位别叠)"与"冲锋/绕击避开非目标单位(路上别撞穿)"统一成**基于路径前瞻的提前侧避**:
    ///   把每个非目标编队投影到"我→目标"的路线上 —— 只要它落在**前方 lookAhead 窗内**且**贴近路线**,就提前往侧边让开;
    ///   越远开始让得越弱、越近越强,且**纯横向**(保留朝目标的前进分量,不倒退、不被正前方障碍顶停)。
    ///
    /// "非目标" = 除自己与交战目标外、双方所有有兵编队(友军 + 非目标敌军)。另有一道**重叠安全网**:真贴上时任意方向硬推开,防穿插。
    /// 前瞻量(= 提前多远开始让)**按编队当前速度自适应**:lookAhead = 基础 + 当前速度 × 前瞻秒数(封顶)。
    /// 故疾驰的冲锋自动绕大弧、缓行/静止的站位自动贴身微调 —— 由实际速度决定,不看"是不是在冲锋"。
    ///
    /// 由各移动行为每拍调用(连续修正);冲锋最后一段 ChargeToTarget 不经此(要的就是撞上目标)。纯读公有 API、零反射、零分配。
    /// </summary>
    public static class FormationAvoidance
    {
        private const float BaseLookAhead = 12f;       // 前瞻基础(m):静止时也留这点提前量
        private const float AnticipationSeconds = 4f;  // 前瞻 = 基础 + 当前速度 × 此秒数(≈ 提前 4 秒航程起避)
        private const float MaxLookAhead = 90f;        // 前瞻封顶(m),防极端速度算出过大提前量

        private const float Margin = 8f;        // 路线侧向安全间距(m):障碍离路线 < 双方半径和 + 此值 → 视为挡路
        private const float StepAhead = 26f;    // 转向后下一步移动点的前视距离(m)
        private const float RepelGain = 1.8f;   // 侧向转向强度(相对朝目标方向)
        private const float SoonBias = 0.6f;    // "越快到越强"的权重(0=只看侧距,1=只看快慢)

        /// <summary>编队足迹半径:宽/深较大者的一半。</summary>
        public static float Radius(Formation f) => System.Math.Max(f.Width, f.Depth) * 0.5f;

        /// <summary>以编队当前有效 WorldPosition 为基挪到 xy(保留 navmesh 上下文),包装成 MovementOrderMove。
        /// 各移动行为下达"去某点"的统一构造(原 "var wp = CachedMedianPosition; wp.SetVec2(..)" 习语散落 5 处)。</summary>
        public static MovementOrder MoveTo(Formation f, Vec2 xy)
        {
            var wp = f.CachedMedianPosition;
            wp.SetVec2(xy);
            return MovementOrder.MovementOrderMove(wp);
        }

        /// <summary>
        /// 朝 goal 前进、提前避开除 self/target 外的所有有兵编队,返回本拍应下达 MovementOrderMove 的点。
        /// target = 交战目标(要接近/环绕的那支),不纳入避让;无目标传 null。前瞻量按 self 当前速度自适应(快→大、慢→小)。
        /// </summary>
        public static Vec2 Steer(Formation self, Vec2 goal, Formation target, Mission m)
        {
            if (self == null || m == null) return goal;
            Vec2 me = self.CachedAveragePosition;
            Vec2 toGoal = goal - me;
            float goalDist = toGoal.Length;
            Vec2 desired = goalDist > 1e-3f ? toGoal / goalDist : Vec2.Zero;
            float rSelf = Radius(self);
            // 前瞻量按当前速度自适应:疾驰→大(绕大弧),缓行/静止→小(贴身微调)。
            float speed = self.CachedCurrentVelocity.Length;
            float lookAhead = System.Math.Min(BaseLookAhead + speed * AnticipationSeconds, MaxLookAhead);
            Vec2 perp = desired.LengthSquared > 1e-6f ? desired.LeftVec() : new Vec2(0f, 1f);

            Vec2 lateral = Vec2.Zero;        // 提前侧避(垂直于路线)
            Vec2 radialOverlap = Vec2.Zero;  // 重叠安全网(任意方向硬推开)

            foreach (Team t in m.Teams)
            {
                if (t == null) continue;
                foreach (Formation o in t.FormationsIncludingEmpty)
                {
                    if (o == null || o == self || o == target || o.CountOfUnits == 0) continue;
                    Vec2 oc = o.CachedAveragePosition;
                    Vec2 rel = oc - me;
                    float sumR = rSelf + Radius(o);
                    float d = rel.Length;

                    if (d < sumR) // 已贴上 → 任意方向硬推开
                    {
                        Vec2 away = d > 1e-3f ? (me - oc) / d : new Vec2(1f, 0f);
                        radialOverlap += away * ((sumR - d) / sumR);
                        continue;
                    }
                    if (desired.LengthSquared < 1e-6f) continue; // 已到点、无前进方向 → 只留重叠安全网

                    float along = Vec2.DotProduct(rel, desired);   // 沿路线在我前方多远
                    if (along <= 0f || along > lookAhead) continue; // 身后 / 超出前瞻窗 → 不理
                    float lat = Vec2.DotProduct(rel, perp);        // 带符号侧距(+左 / −右)
                    float absLat = lat >= 0f ? lat : -lat;
                    float influence = sumR + Margin;
                    if (absLat >= influence) continue;             // 不贴近路线

                    float latStrength = (influence - absLat) / influence; // 侧向越贴近路线越强
                    float soon = 1f - along / lookAhead;                  // 越快到(越近前方)越强
                    float s = latStrength * (1f - SoonBias + SoonBias * soon);
                    Vec2 side = lat >= 0f ? -perp : perp;                 // 障碍偏左→向右让,反之(正前方取一致侧)
                    lateral += side * (s * RepelGain);
                }
            }

            Vec2 steer = desired + lateral + radialOverlap;
            if (steer.LengthSquared < 1e-6f)
            {
                if (desired.LengthSquared > 1e-6f) steer = desired;
                else return goal; // 已到位且无排斥 → 停在目标
            }
            steer = steer.Normalized();
            float advance = goalDist > 1e-3f ? System.Math.Min(goalDist, StepAhead) : StepAhead * 0.5f;
            return me + steer * advance;
        }
    }
}
