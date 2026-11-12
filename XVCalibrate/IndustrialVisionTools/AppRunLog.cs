using System;
using System.IO;
using System.Text;
using System.Threading;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 进程级运行日志落盘：UI LogBox 在崩溃时会丢，文件日志用于事后定位最后执行点。
    /// 目录：{exe}/logs/run_yyyyMMdd_HHmmss.log
    /// </summary>
    internal static class AppRunLog
    {
        private static readonly object Gate = new();
        private static StreamWriter? _writer;
        private static string? _logPath;
        private static string? _lastMarkerPath;
        private static int _lineCount;
        private static string _sessionId = "";

        public static string? CurrentLogPath
        {
            get { lock (Gate) return _logPath; }
        }

        public static void BeginSession(string? reason = null)
        {
            lock (Gate)
            {
                try
                {
                    CloseUnlocked();
                    string logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
                    Directory.CreateDirectory(logsDir);
                    _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    _logPath = Path.Combine(logsDir, $"run_{_sessionId}.log");
                    _lastMarkerPath = Path.Combine(logsDir, "last_run.txt");
                    _writer = new StreamWriter(
                        new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                    {
                        AutoFlush = true
                    };
                    _lineCount = 0;
                    File.WriteAllText(_lastMarkerPath, _logPath + Environment.NewLine, Encoding.UTF8);
                    WriteUnlocked("INFO", $"session start pid={Environment.ProcessId} reason={reason ?? "startup"}");
                    WriteUnlocked("INFO", $"baseDir={AppContext.BaseDirectory}");
                    WriteUnlocked("INFO", $"os={Environment.OSVersion} clr={Environment.Version} x64={Environment.Is64BitProcess}");
                }
                catch
                {
                    CloseUnlocked();
                }
            }
        }

        public static void Write(string level, string message)
        {
            lock (Gate)
            {
                WriteUnlocked(level, message);
            }
        }

        public static void Info(string message) => Write("INFO", message);

        public static void Error(string message) => Write("ERROR", message);

        public static void Crash(string source, Exception? ex)
        {
            lock (Gate)
            {
                WriteUnlocked("CRASH", $"source={source}");
                if (ex != null)
                {
                    WriteUnlocked("CRASH", ex.GetType().FullName + ": " + ex.Message);
                    try
                    {
                        WriteUnlocked("CRASH", ex.ToString());
                    }
                    catch
                    {
                        // ignored
                    }
                }

                FlushUnlocked();
            }
        }

        public static void Flush()
        {
            lock (Gate)
            {
                FlushUnlocked();
            }
        }

        public static void EndSession(string? reason = null)
        {
            lock (Gate)
            {
                WriteUnlocked("INFO", $"session end reason={reason ?? "shutdown"} lines={_lineCount}");
                FlushUnlocked();
                CloseUnlocked();
            }
        }

        private static void WriteUnlocked(string level, string message)
        {
            if (_writer == null)
                return;
            try
            {
                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                _writer.WriteLine($"[{ts}] [{level}] {message}");
                _lineCount++;
                // 关键节点进出：同步写 marker，崩溃后可读最后一行意图
                if (_lastMarkerPath != null
                    && (level.Equals("CRASH", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("[ENTER]", StringComparison.Ordinal)
                        || message.Contains("==========", StringComparison.Ordinal)))
                {
                    try
                    {
                        File.WriteAllText(
                            _lastMarkerPath,
                            (_logPath ?? "") + Environment.NewLine + $"[{ts}] [{level}] {message}" + Environment.NewLine,
                            Encoding.UTF8);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        private static void FlushUnlocked()
        {
            try { _writer?.Flush(); } catch { /* ignored */ }
        }

        private static void CloseUnlocked()
        {
            try { _writer?.Dispose(); } catch { /* ignored */ }
            _writer = null;
        }
    }
}
