using System;

namespace AnvilAndHammerAI.Logging
{
    /// <summary>
    /// 跨子系统打点计数器。诊断每 5 秒 Flush 一次心跳行并清零。
    /// 定位原则:**某计数 = 0 即该子系统这 5 秒没触发** —— 只看日志即可判断哪一层没工作。
    /// (骑兵 AI 已整体移除待重写,本表只剩全兵种士气/伤亡的计数。)
    /// </summary>
    public static class Telemetry
    {
        // B 可拉扯 / C 棘轮
        public static int RallyCount;          // 集结(StopRetreating)次数
        public static int BreakCount;          // 真崩上升沿次数
        public static int BottomedCount;       // 新触底(真崩不再集结)次数

        // D 战斗结束安全
        public static int RoutBlocked;         // CanAgentRout 返 false(挡 fade-out)次数

        // E 伤亡分布
        public static int DmgCalls;            // CalculateDamage postfix 进入次数(>0 即补丁生效)
        public static int DmgPursuit, DmgRear, DmgStand;
        public static int DmgFlank;            // 角色(包抄/轻重骑)用了侧/背袭加成的命中数
        public static int DmgCharge;           // 骑兵冲撞(IsHorseCharge)缩放的命中数
        public static int DmgPlow;             // 重骑冲撞强制击倒(穿阵/减速变小)的次数
        public static float DmgMultSum;        // 已缩放伤害的系数之和(算平均)
        public static int DmgScaled;           // 实际改了系数的次数

        // R 遇袭自发反应(reaction override):各模式本窗口"活跃 tick"次数(=0 即该反应这 5 秒没触发)
        public static int ReactBrace, ReactFallback, ReactCounter;

        // F 编队级士气池
        public static int FormRoutEdges;       // 编队溃逃触发(池越阈上升沿)次数
        public static int FormRoutAgents;      // 被压 panic 下限的 agent 次数
        public static float PoolPeak;          // 本窗口有效压力峰值(池+伤亡地板+冲锋震慑;>0 即有压力)
        public static float PrCas, PrCsc, PrEnc, PrRng, PrChg; // 归因:cas=伤亡地板增量和 csc/enc/rng=情势源贡献和 chg=冲锋震慑峰值
        public static int ChargeHits;          // 本窗口被记入的真实背/侧冲命中次数(反推单次冲锋命中数 K,标定 ChargeImpactGain)

        /// <summary>按源 Tag 累加压力贡献(供 [tele F] 归因)。</summary>
        public static void AddPressure(string tag, float v)
        {
            switch (tag)
            {
                case "cas": PrCas += v; break;
                case "csc": PrCsc += v; break;
                case "enc": PrEnc += v; break;
                case "rng": PrRng += v; break;
            }
        }

        public static void Flush(Action<string> sink)
        {
            sink($"[tele B/C] rally={RallyCount} breaks={BreakCount} newBottomed={BottomedCount}");
            sink($"[tele D] routBlocked={RoutBlocked}");
            float avg = DmgScaled > 0 ? DmgMultSum / DmgScaled : 0f;
            sink($"[tele E] dmgCalls={DmgCalls} pursuit={DmgPursuit} rear={DmgRear} stand={DmgStand} flank={DmgFlank} charge={DmgCharge} plow={DmgPlow} avgMult={avg:0.00}");
            sink($"[tele F] formRout={FormRoutEdges} routAgents={FormRoutAgents} effPeak={PoolPeak:0.0} chargeHits={ChargeHits} " +
                 $"pr[cas(floor)={PrCas:0.0} csc={PrCsc:0.0} enc={PrEnc:0.0} rng={PrRng:0.0} chg(peak)={PrChg:0.0}]");
            sink($"[tele R] react[brace={ReactBrace} fallback={ReactFallback} counter={ReactCounter}]");
            Reset();
        }

        private static void Reset()
        {
            RallyCount = BreakCount = BottomedCount = 0;
            RoutBlocked = 0;
            DmgCalls = DmgPursuit = DmgRear = DmgStand = DmgScaled = DmgFlank = DmgCharge = DmgPlow = 0;
            DmgMultSum = 0f;
            ReactBrace = ReactFallback = ReactCounter = 0;
            FormRoutEdges = FormRoutAgents = 0;
            ChargeHits = 0;
            PoolPeak = PrCas = PrCsc = PrEnc = PrRng = PrChg = 0f;
        }
    }
}
