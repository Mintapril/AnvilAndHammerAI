using System;
using System.IO;

namespace AnvilAndHammerAI.Logging
{
    /// <summary>
    /// 极简文件日志(+ 可选屏显)。阶段0 的验证全靠它落证据。
    /// 路径:Documents\Mount and Blade II Bannerlord\AnvilAndHammerAI.log
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        private static string Path
        {
            get
            {
                if (_path == null)
                {
                    string dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "Mount and Blade II Bannerlord");
                    try { Directory.CreateDirectory(dir); } catch { /* 忽略 */ }
                    _path = System.IO.Path.Combine(dir, "AnvilAndHammerAI.log");
                }
                return _path;
            }
        }

        public static void Info(string msg) => Write("INFO", msg);

        /// <summary>仅在 MCM 的"调试日志"开启时写(逐事件细节)。关闭时只剩 Info 心跳/聚合。</summary>
        public static void Debug(string msg)
        {
            if (Settings.AnvilSettings.Instance?.DebugLogging == true) Write("DEBUG", msg);
        }

        public static void Error(string msg, Exception e = null)
            => Write("ERROR", e == null ? msg : msg + " :: " + e);

        private static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(Path,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
                }
            }
            catch { /* 日志绝不能让游戏崩 */ }
        }
    }
}
