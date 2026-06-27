namespace AnvilAndHammerAI.Detection
{
    /// <summary>
    /// 固定间隔节流闸:每帧喂 dt,累计到 interval 才 Ready 一次,并把累计的真实经过时间从 <paramref name="elapsed"/> 交回
    /// (供差分/积分用真实 dt,非固定 interval)。替代散落各 MissionLogic 的
    /// `_accum += dt; if (_accum &lt; interval) return; _accum = 0;` 习语。值类型,零分配。
    /// </summary>
    public struct TickGate
    {
        private float _accum;
        private readonly float _interval;

        public TickGate(float interval) { _interval = interval; _accum = 0f; }

        public bool Ready(float dt, out float elapsed)
        {
            _accum += dt;
            if (_accum < _interval) { elapsed = 0f; return false; }
            elapsed = _accum;
            _accum = 0f;
            return true;
        }
    }
}
