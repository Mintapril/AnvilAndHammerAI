using System.Collections.Generic;
using AnvilAndHammerAI.Logging;
using AnvilAndHammerAI.Settings;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AnvilAndHammerAI.Detection
{
    /// <summary>
    /// 箭矢轨迹可视化:沿每枚导弹**真实飞行路径**铺一条浅灰色拖尾——新抛下的段明显(高不透明),随时间线性淡出直至完全消失,
    /// 可见部分≈最近 <see cref="TTL"/> 秒走过的路程(≈半程)。穿遮挡常显,RTS 俯视(RTSCamera)远距下仍清晰。纯渲染、只读导弹。
    ///
    /// 做法(emit-and-fade,对齐 RTSCamera 的世界覆盖网格用法):导弹每飞过 <see cref="MinDist"/> 米,就在 上次抛点→当前点
    /// 之间抛下一节短杆段(池化实体);每段抛下时调**原生定时淡出** <c>GameEntity.FadeOut(TTL, false)</c>(满→0,引擎逐帧自动淡,
    /// 我方零逐帧开销),到龄(now≥Expire,alpha 已≈0)即隐藏回收复用。无逐帧 SetAlpha 故可取更细的 <see cref="MinDist"/> → 更平滑。
    /// 这天然处理导弹飞行/命中/索引复用,且形态就是真实弹道。
    /// 网格 <c>rts_arrow_body</c>(箭身=直杆,中性基底可被任意染色;段很短故非"箭"观感)+ **无光照**材质
    /// <c>vertex_color_blend_no_depth_mat</c>(平涂不受光 → 敌我两侧同为浅灰,不会因朝光/背光一黑一灰;且无深度=穿遮挡常显)
    /// + <c>mesh.SetFactor1(灰)</c>。常显另加 <c>EntityFlags|0x400000</c> + <c>EntityVisibilityFlags=4</c>(对齐 RTSCamera 箭体)。
    /// 段数上限 <see cref="MaxSegments"/> 保护性能(超限暂不抛新段,优雅降级)。受 MCM「显示箭矢轨迹」+ <see cref="Mission.IsFieldBattle"/> 门控。
    /// </summary>
    public sealed class MissileTrailMissionLogic : MissionLogic
    {
        // —— 可调外观 ——
        private const float TTL = 1.0f;          // 每段存活/淡出时长(秒);可见拖尾长 ≈ 弹速×TTL ≈ 半程。要更长/更短在此调
        private const float MinDist = 1.5f;      // 每飞过此距离(m)抛一节段;越小越细越平滑(原生淡出无逐帧开销,可取较小)
        private const float Width = 0.125f;      // 段横向宽度缩放(X);**Z 恒为 1** 保留扁薄 → 细扁平条带(觉得太粗→调小,太细看不见→调大)
        private const float LengthToScale = 1.336f; // 段世界米 → Y 缩放(rts_arrow_body 基准非单位长;系数取自 RTSCamera 箭身 :6860)
        private const float Overlap = 1.6f;      // 段拉伸系数(>1 相邻段重叠成连续一条;越大越平滑无缝)
        private const float MaxSegLen = 12f;     // 单段最大长度(m):超过判为不连续(索引复用/瞬移)→ 跳过,不跨场画长线
        private const int MaxSegments = 1500;    // 段实体上限(性能护栏:每段=1 个免裁剪半透明实体,过多拖低渲染帧率;此为主要降帧旋钮)

        private static readonly uint TrailColor = new Color(0.82f, 0.82f, 0.85f, 1f).ToUnsignedInteger();

        private sealed class Seg { public GameEntity Entity; public MetaMesh Mesh; public float Expire; public bool Active; }
        private readonly List<Seg> _pool = new List<Seg>();
        private readonly Stack<int> _free = new Stack<int>();
        private readonly Dictionary<Mission.Missile, Vec3> _lastEmit = new Dictionary<Mission.Missile, Vec3>();
        private readonly HashSet<Mission.Missile> _live = new HashSet<Mission.Missile>();
        private readonly List<Mission.Missile> _stale = new List<Mission.Missile>();
        private Material _mat;
        private bool _failed;

        public override void OnPreDisplayMissionTick(float dt)
        {
            base.OnPreDisplayMissionTick(dt);
            if (_failed) return;
            var s = AnvilSettings.Instance;
            if (s == null || !s.Enabled || !s.ShowMissileTrails) { HideAll(); return; }
            Mission m = Mission.Current;
            if (m == null || !m.IsFieldBattle || m.Scene == null) { HideAll(); return; }

            // 仅 RTS/自由镜头下显示:玩家直接操控角色时隐藏(也省去操控时的渲染开销)。
            // RTSCamera 进自由镜头会把主角 Controller 置 AI → IsPlayerControlled=false;正常操控时为 true。
            Agent main = m.MainAgent;
            if (main != null && main.IsPlayerControlled) { HideAll(); return; }

            float now = m.CurrentTime;

            // 1) 回收到龄段(alpha 由原生 FadeOut 处理,这里只在到龄后隐藏并放回空闲池,无逐帧 SetAlpha)。
            for (int i = 0; i < _pool.Count; i++)
            {
                Seg seg = _pool[i];
                if (!seg.Active || now < seg.Expire) continue;
                seg.Active = false;
                if (seg.Entity != null) seg.Entity.SetVisibilityExcludeParents(false);
                _free.Push(i);
            }

            // 2) 沿各导弹路径抛新段。
            _live.Clear();
            var missiles = m.MissilesList;
            if (missiles != null)
            {
                for (int i = 0; i < missiles.Count; i++)
                {
                    Mission.Missile mi = missiles[i];
                    if (mi == null) continue;
                    _live.Add(mi);
                    Vec3 pos = mi.GetPosition();
                    Vec3 last;
                    if (!_lastEmit.TryGetValue(mi, out last)) { _lastEmit[mi] = pos; continue; }
                    Vec3 d = pos - last;
                    float len = d.Length;
                    if (len < MinDist) continue;
                    _lastEmit[mi] = pos;
                    if (len > MaxSegLen) continue;            // 不连续(索引复用/瞬移)→ 跳过
                    if (!Emit(m.Scene, last, pos, d, len, now) && _failed) return;
                }
            }

            // 3) 清掉已消失导弹的抛点记录(限内存)。
            if (_lastEmit.Count > _live.Count)
            {
                _stale.Clear();
                foreach (var kv in _lastEmit) if (!_live.Contains(kv.Key)) _stale.Add(kv.Key);
                for (int i = 0; i < _stale.Count; i++) _lastEmit.Remove(_stale[i]);
            }
        }

        // 抛一节段(last→pos)。返回 false 且 _failed 时表示渲染资源缺失需停用。
        private bool Emit(Scene scene, Vec3 a, Vec3 b, Vec3 d, float len, float now)
        {
            int slot;
            if (_free.Count > 0) slot = _free.Pop();
            else
            {
                if (_pool.Count >= MaxSegments) return false; // 上限 → 暂不抛(优雅降级)
                Seg created;
                if (!Create(scene, out created)) { _failed = true; Log.Error("[missiletrail] 渲染资源缺失,弹道可视化停用。"); return false; }
                _pool.Add(created);
                slot = _pool.Count - 1;
            }

            Seg seg = _pool[slot];
            Vec3 dir = d * (1f / len);
            Vec3 center = (a + b) * 0.5f;
            Mat3 rot = Mat3.CreateMat3WithForward(dir);
            MatrixFrame frame = new MatrixFrame(rot, center);
            Vec3 scale = new Vec3(Width, len * LengthToScale * Overlap, 1f, -1f); // X=宽 Y=沿路径长 Z=1(保留扁薄→扁平条带)
            frame.Scale(scale);
            seg.Entity.SetFrame(ref frame, true);
            seg.Entity.SetAlpha(1f);                // 复用槽:重置 alpha 到满(清上次淡出残留)
            seg.Entity.SetVisibilityExcludeParents(true);
            seg.Entity.FadeOut(TTL, false);         // 原生定时淡出:满→0 历时 TTL,不移除(留作池化复用)
            seg.Expire = now + TTL;
            seg.Active = true;
            return true;
        }

        private bool Create(Scene scene, out Seg seg)
        {
            seg = null;
            GameEntity e = GameEntity.CreateEmpty(scene, true, true, true);
            if (e == null) return false;
            MetaMesh mesh = MetaMesh.GetCopy("rts_arrow_body", true, false);
            if (mesh == null) mesh = MetaMesh.GetCopy("barrier_sphere", true, false); // 兜底:无 RTSCamera 时(可能偏色)
            if (mesh == null) { e.Remove(0); return false; }
            if (_mat == null)
            {
                Material bm = Material.GetFromResource("vertex_color_blend_no_depth_mat"); // 无光照叠加层:平涂不受光 + 无深度常显
                if (bm == null) { e.Remove(0); return false; }
                _mat = bm.CreateCopy();
            }
            mesh.SetMaterial(_mat);        // 材质须在 SetFactor1 之前
            mesh.SetFactor1(TrailColor);   // 平涂浅灰
            e.AddComponent((GameEntityComponent)(object)mesh);
            e.EntityFlags = e.EntityFlags | (EntityFlags)0x400000; // 免裁剪
            e.EntityVisibilityFlags = (EntityVisibilityFlags)4;    // 穿遮挡常显(对齐 RTSCamera 箭体)
            e.SetVisibilityExcludeParents(false);
            seg = new Seg { Entity = e, Mesh = mesh, Active = false };
            return true;
        }

        // 隐藏全部活动段并清抛点(关开关/非野战时)。
        private void HideAll()
        {
            for (int i = 0; i < _pool.Count; i++)
            {
                Seg seg = _pool[i];
                if (!seg.Active) continue;
                seg.Active = false;
                if (seg.Entity != null) seg.Entity.SetVisibilityExcludeParents(false);
                _free.Push(i);
            }
            _lastEmit.Clear();
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();
            for (int i = 0; i < _pool.Count; i++)
            {
                GameEntity e = _pool[i].Entity;
                if (e != null) e.Remove(0);
            }
            _pool.Clear();
            _free.Clear();
            _lastEmit.Clear();
            _live.Clear();
            _stale.Clear();
            _mat = null;
        }
    }
}
