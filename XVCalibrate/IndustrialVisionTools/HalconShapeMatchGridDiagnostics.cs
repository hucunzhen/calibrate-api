using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 阵列过滤诊断日志（线程安全）。后台跑 flow 时写入文件并可选镜像到 Console。
    /// 启用方式：环境变量 XV_GRID_FILTER_LOG=1 或路径；CLI --grid-filter-log；算子参数 debugLog=true。
    /// </summary>
    internal static class HalconShapeMatchGridDiagnostics
    {
        private static readonly object Gate = new();
        private static bool _enabled;
        private static bool _mirrorConsole;
        private static string? _logPath;

        public static bool IsEnabled
        {
            get { lock (Gate) return _enabled; }
        }

        public static string? LogFilePath
        {
            get { lock (Gate) return _logPath; }
        }

        static HalconShapeMatchGridDiagnostics()
        {
            TryConfigureFromEnvironment();
        }

        public static void TryConfigureFromEnvironment()
        {
            string? raw = Environment.GetEnvironmentVariable("XV_GRID_FILTER_LOG");
            if (string.IsNullOrWhiteSpace(raw))
                return;

            if (raw is "0" or "false" or "off" or "no")
            {
                Disable();
                return;
            }

            string? path = raw is "1" or "true" or "on" or "yes" ? null : raw.Trim();
            Enable(path, mirrorConsole: true, sessionName: "env");
        }

        /// <summary>CLI / 后台跑 flow 时调用：日志写在 flow 同目录。</summary>
        public static void EnableForFlowFile(string flowFilePath, bool mirrorConsole = true)
        {
            string? dir = Path.GetDirectoryName(flowFilePath);
            string baseName = Path.GetFileNameWithoutExtension(flowFilePath);
            if (string.IsNullOrEmpty(baseName))
                baseName = "flow";
            string logPath = Path.Combine(
                string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir,
                $"{baseName}.grid-filter.log");
            Enable(logPath, mirrorConsole, sessionName: Path.GetFileName(flowFilePath));
        }

        public static void Enable(string? logFilePath = null, bool mirrorConsole = false, string? sessionName = null)
        {
            lock (Gate)
            {
                _enabled = true;
                _mirrorConsole = mirrorConsole;
                _logPath = string.IsNullOrWhiteSpace(logFilePath) ? DefaultLogPath() : Path.GetFullPath(logFilePath);
                string? parent = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
            }

            Log($"======== 阵列过滤诊断 ========");
            if (!string.IsNullOrEmpty(sessionName))
                Log($"会话: {sessionName}");
            Log($"日志文件: {LogFilePath}");
        }

        public static void Disable()
        {
            lock (Gate)
            {
                if (_enabled)
                    Log("======== 诊断结束 ========");
                _enabled = false;
                _mirrorConsole = false;
            }
        }

        public static bool ShouldLogForParam(string? debugLogParam, bool flowMirrorStderr)
        {
            if (string.IsNullOrWhiteSpace(debugLogParam))
                return IsEnabled;

            string v = debugLogParam.Trim();
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "on", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(v, "auto", StringComparison.OrdinalIgnoreCase))
                return IsEnabled || flowMirrorStderr;

            return false;
        }

        public static void Log(string message)
        {
            bool enabled;
            string? path;
            bool mirror;
            lock (Gate)
            {
                enabled = _enabled;
                path = _logPath;
                mirror = _mirrorConsole;
                if (!enabled)
                    return;
            }

            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            try
            {
                if (!string.IsNullOrEmpty(path))
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 诊断不应影响主流程
            }

            if (mirror)
            {
                try { Console.WriteLine("[GridFilter] " + message); }
                catch { /* ignore */ }
            }
        }

        public static void LogFindInput(string nodeLabel, int count, double[]? scores)
        {
            if (!IsEnabled) return;
            Log($"{nodeLabel} FindShapeModel 输入: {count} 个候选");
            if (scores == null || scores.Length == 0 || count == 0)
                return;
            int n = Math.Min(count, scores.Length);
            double min = double.MaxValue, max = double.MinValue, sum = 0;
            for (int i = 0; i < n; i++)
            {
                double s = scores[i];
                if (s < min) min = s;
                if (s > max) max = s;
                sum += s;
            }

            Log($"{nodeLabel} Score: min={min.ToString("F3", CultureInfo.InvariantCulture)}, max={max.ToString("F3", CultureInfo.InvariantCulture)}, avg={(sum / n).ToString("F3", CultureInfo.InvariantCulture)}");
        }

        private static string DefaultLogPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppProduct.AppDataFolderName,
                "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"grid-filter-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        }
    }
}
