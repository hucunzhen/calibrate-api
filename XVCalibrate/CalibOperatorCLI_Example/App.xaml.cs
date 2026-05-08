using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace CalibOperatorCLI_Example
{
    public partial class App : Application
    {
        private const uint AttachParentProcess = 0xFFFFFFFFu;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var mainWindow = new MainWindow();
            mainWindow.Show();

            string? flowPath = TryParseFlowPathArg(e.Args);
            if (!string.IsNullOrWhiteSpace(flowPath))
            {
                // 从终端启动时可挂上父控制台，便于看到简要结果（WinExe 默认无控制台）
                try { AttachConsole(AttachParentProcess); } catch { /* ignore */ }

                // 后台自动执行模式：隐藏窗口，执行完成后以退出码返回结果
                mainWindow.WindowState = WindowState.Minimized;
                mainWindow.ShowInTaskbar = false;
                mainWindow.Hide();
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    bool ok = false;
                    try
                    {
                        ok = await mainWindow.RunFlowConfigInBackgroundAsync(flowPath);
                    }
                    catch (Exception ex)
                    {
                        TryConsoleError($"[FlowRunner] {ex.Message}");
                        if (ex.InnerException != null)
                            TryConsoleError($"[FlowRunner] Inner: {ex.InnerException}");
                    }

                    Environment.ExitCode = ok ? 0 : 1;
                    TryConsoleLine(ok
                        ? "[FlowRunner] 成功 (exit 0)"
                        : "[FlowRunner] 失败 (exit 1)：常见原因 — load_image 路径不存在、中间算子报错；错误详情见 FlowPage 日志（启用 MirrorErrorsToStderr 时已写入 stderr）。");
                    Shutdown();
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        private static void TryConsoleLine(string msg)
        {
            try { Console.WriteLine(msg); }
            catch { /* ignore */ }
        }

        private static void TryConsoleError(string msg)
        {
            try { Console.Error.WriteLine(msg); }
            catch { /* ignore */ }
        }

        private static string? TryParseFlowPathArg(string[] args)
        {
            if (args == null || args.Length == 0) return null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--flow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "-flow", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) return Path.GetFullPath(args[i + 1]);
                    return null;
                }
            }

            // 兼容直接传文件路径
            if (args.Length == 1 && args[0].EndsWith(".flow.json", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(args[0]);

            return null;
        }
    }
}