using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 启动 Python 并异步读取 stdout/stderr，避免管道死锁；<c>-u</c> 关闭缓冲便于日志实时刷新。
    /// </summary>
    internal static class FlowPythonRunner
    {
        public static string ResolveScriptOrThrow(string relativeOrAbsolute)
        {
            string t = (relativeOrAbsolute ?? "").Trim();
            if (string.IsNullOrEmpty(t))
                throw new InvalidOperationException("脚本路径为空。");
            return SamOnnxSegmentation.ResolveModelPath(t);
        }

        public static async Task<int> RunPythonStreamingAsync(
            string pythonExecutable,
            string workingDirectory,
            IReadOnlyList<string> argsAfterScript,
            Dispatcher dispatcher,
            Action<string> appendLog)
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Environment.CurrentDirectory
                    : workingDirectory,
            };
            try
            {
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
            }
            catch
            {
                // 个别环境下编码不可用则退回默认
            }

            // Windows 下 Python 常按系统代码页写管道，与上方 UTF-8 解码不一致会导致中文乱码
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            psi.ArgumentList.Add("-u");
            foreach (string a in argsAfterScript)
                psi.ArgumentList.Add(a);

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!p.Start())
                throw new InvalidOperationException($"无法启动进程: {pythonExecutable}");

            void OnLine(string? line)
            {
                if (string.IsNullOrEmpty(line)) return;
                _ = dispatcher.InvokeAsync(() => appendLog(line));
            }

            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            await p.WaitForExitAsync().ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
            return p.ExitCode;
        }
    }
}
