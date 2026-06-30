using System;
using AnvilAndHammerAI.Morale;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Detection
{
    /// <summary>
    /// 编队级只读快照(每编队每 tick 采样一次)。一次 <see cref="Formation.ApplyActionOnEachUnit"/>
    /// 同时算 数量/平均士气/溃逃数,再读 QuerySystem 几个**已缓存**标量(...ReadOnly)。
    /// 压力源 / 决策 / 诊断共享同一份快照,避免对同一 agent 集合每 tick 重复遍历。
    /// </summary>
    public readonly struct FormationSnapshot
    {
        public readonly Formation Formation;
        public readonly int Count;            // 活跃 human 单位数
        public readonly int RoutingCount;     // IsRunningAway 数
        public readonly float AvgMorale;      // 平均士气
        public readonly float CasualtyRatio;  // QuerySystem **存活/战力比**(满编=1,减员→0;按需重算)
        public readonly int LocalEnemyCount;  // 近距敌单位数(30m 内,按需重算)
        public readonly int OccupiedSectors;  // 被敌占的方向扇区数(几何环绕;≥MinPerSector 敌的扇区计 1)
        public readonly float AvgTier;         // 编队平均兵种 tier(GetBattleTier);供 tier 抗性

        public FormationSnapshot(Formation formation, int count, int routingCount,
            float avgMorale, float casualtyRatio, int localEnemyCount, int occupiedSectors, float avgTier)
        {
            Formation = formation; Count = count; RoutingCount = routingCount;
            AvgMorale = avgMorale; CasualtyRatio = casualtyRatio;
            LocalEnemyCount = localEnemyCount; OccupiedSectors = occupiedSectors; AvgTier = avgTier;
        }

        public float RoutingFraction => Count > 0 ? (float)RoutingCount / Count : 0f;
    }

    /// <summary>
    /// 队级聚合(供级联压力源算"邻编队溃逃")。只存整队溃逃数/总数,
    /// O(1) 派生"排除自身后的邻编队溃逃比例"——避免对邻编队再扫一遍 agent。
    /// </summary>
    public readonly struct TeamMoraleContext
    {
        public readonly int TotalRoutingCount;
        public readonly int TotalCount;

        public TeamMoraleContext(int totalRoutingCount, int totalCount)
        { TotalRoutingCount = totalRoutingCount; TotalCount = totalCount; }

        /// <summary>排除自身后,同队其它编队的溃逃比例(0..1)。</summary>
        public float NeighborRoutingFraction(in FormationSnapshot self)
        {
            int othersCount = TotalCount - self.Count;
            int othersRouting = TotalRoutingCount - self.RoutingCount;
            return othersCount > 0 ? (float)othersRouting / othersCount : 0f;
        }
    }

    /// <summary>
    /// 单遍融合扫描器。**可复用实例 + 缓存委托**:稳态 0 堆分配(避免每 tick 的 lambda 捕获闭包)。
    /// 非线程安全——每个 MissionLogic 各持一个,主线程串行复用。
    /// </summary>
    public sealed class FormationScanner
    {
        private int _count, _routing;
        private float _moraleSum;
        private int _tierSum, _tierCount;
        private readonly Action<Agent> _visit;
        private readonly int[] _sectorCounts = new int[MoraleTuning.EncircleSectorCount]; // 复用扇区桶,零每帧分配

        public FormationScanner() { _visit = Visit; } // 委托只分配一次

        private void Visit(Agent a)
        {
            if (a == null || !a.IsHuman) return;
            _count++;
            _moraleSum += AgentComponentExtensions.GetMorale(a);
            if (a.IsRunningAway) _routing++;
            var ch = a.Character;
            if (ch != null) { _tierSum += ch.GetBattleTier(); _tierCount++; }
        }

        public FormationSnapshot Scan(Formation f)
        {
            _count = 0; _routing = 0; _moraleSum = 0f; _tierSum = 0; _tierCount = 0;
            f.ApplyActionOnEachUnit(_visit);
            float avg = _count > 0 ? _moraleSum / _count : 0f;
            float avgTier = _tierCount > 0 ? (float)_tierSum / _tierCount : MoraleTuning.TierResistBaseline;

            var qs = f.QuerySystem;
            // 读会按需重算的 .Value(**非** ...ReadOnly):被屏蔽的骑兵编队(SetControlledByAI(false)+RBM 压制)不跑
            // team AI,...ReadOnly 缓存永不刷新会卡在初值(0/空表);.Value 过期自动重算。频率已被 0.5s tick 门控。
            float casualty = qs != null ? qs.CasualtyRatio : 1f;   // 存活比:无 qs 时按"满编"算(无伤亡压力)
            var localEnemies = qs?.LocalEnemyUnits;
            int enemies = localEnemies?.Count ?? 0;
            int occupied = ComputeOccupiedSectors(localEnemies, f.CachedAveragePosition); // 几何环绕:被占方向数

            return new FormationSnapshot(f, _count, _routing, avg, casualty, enemies, occupied, avgTier);
        }

        /// <summary>
        /// 几何环绕:把 30m 内敌兵按相对编队中心的方位角分 <see cref="MoraleTuning.EncircleSectorCount"/> 个扇区,
        /// 返回敌数 ≥ MinPerSector 的扇区数(= 有威胁的方向数)。粗扇区 + 每扇区门槛去噪,避脆弱细几何。
        /// </summary>
        private int ComputeOccupiedSectors(MBList<Agent> enemies, Vec2 selfPos)
        {
            int n = MoraleTuning.EncircleSectorCount;
            if (enemies == null || enemies.Count == 0) return 0;

            for (int i = 0; i < n; i++) _sectorCounts[i] = 0;
            for (int i = 0; i < enemies.Count; i++)
            {
                Agent e = enemies[i];
                if (e == null) continue;
                Vec3 ep = e.Position;
                float dx = ep.x - selfPos.x, dy = ep.y - selfPos.y;
                if (dx * dx + dy * dy < 1e-4f) continue;            // 与中心重合,无方向
                double ang = Math.Atan2(dy, dx) + Math.PI;          // [0, 2π)
                int sec = (int)(ang / (2.0 * Math.PI) * n);
                if (sec < 0) sec = 0; else if (sec >= n) sec = n - 1;
                _sectorCounts[sec]++;
            }
            int occupied = 0;
            for (int i = 0; i < n; i++)
                if (_sectorCounts[i] >= MoraleTuning.EncircleMinPerSector) occupied++;
            return occupied;
        }
    }
}
