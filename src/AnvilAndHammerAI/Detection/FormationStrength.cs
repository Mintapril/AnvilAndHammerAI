using System;
using System.Collections.Generic;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Detection
{
    /// <summary>
    /// 编队级"兵力值"的**唯一**度量 = Σ每名存活士兵的 battle tier,骑乘单位再 ×<see cref="CavalryStrengthMultiplier"/>。
    /// 精锐按更高权重计(同样人数,高 tier 兵力值更高);骑兵按更高权重计(用户定:同 tier 1 骑 = 3 步)。
    /// 全 mod 里所有"按规模/兵力"的比较(敌骑威胁、突破口显著性、左右翼/主侧均衡)统一对比此值,避免各处口径不一。
    /// 主线程串行复用静态累加器,零每次分配(委托只建一次);Visit 不重入,故共享静态安全。
    /// </summary>
    public static class FormationStrength
    {
        /// <summary>同 tier 骑兵兵力值 = 步兵 × 此倍数(用户定:1 骑 = 3 步)。所有骑乘单位(含骑射)按此放大。</summary>
        public const int CavalryStrengthMultiplier = 3;

        private static int _sum;
        private static readonly Action<Agent> _visit = Visit;

        // 本帧记忆:同一引擎帧(CurrentTime 不变)内对同一编队只全兵遍历一次。指挥层每拍对同一批敌(骑)编队反复求兵力值
        // (Plan 选目标两遍 + cav-cover×3 被保护编队 + 反应层×最多7 编队),全经此缓存 → 每编队每帧至多一次遍历。
        // 主线程串行无并发;CurrentTime 一推进即整表失效,跨帧不残留陈旧值(无 mission 时退化为不缓存,照常实算)。
        private static readonly Dictionary<Formation, int> _cache = new Dictionary<Formation, int>();
        private static float _stamp = float.NaN;

        public static int Of(Formation f)
        {
            if (f == null || f.CountOfUnits == 0) return 0;
            float now = Mission.Current != null ? Mission.Current.CurrentTime : float.NaN;
            if (now != _stamp) { _cache.Clear(); _stamp = now; } // 新的一帧 → 整表失效
            if (_cache.TryGetValue(f, out int cached)) return cached;
            _sum = 0;
            f.ApplyActionOnEachUnit(_visit);
            _cache[f] = _sum;
            return _sum;
        }

        private static void Visit(Agent a)
        {
            if (a == null || !a.IsHuman) return;
            var ch = a.Character;
            int t = ch != null ? ch.GetBattleTier() : 0;
            if (a.HasMount) t *= CavalryStrengthMultiplier; // 骑兵兵力值 ×3(同 tier 1 骑 = 3 步)
            _sum += t;
        }
    }
}
