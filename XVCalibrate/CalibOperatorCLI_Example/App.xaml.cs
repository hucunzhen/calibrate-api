using System;
using System.IO;
using System.Windows;

namespace CalibOperatorCLI_Example
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var mainWindow = new MainWindow();
            mainWindow.Show();

            string? flowPath = TryParseFlowPathArg(e.Args);
            if (!string.IsNullOrWhiteSpace(flowPath))
            {
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
                        Console.Error.WriteLine($"[FlowRunner] {ex.Message}");
                    }

                    Environment.ExitCode = ok ? 0 : 1;
                    Shutdown();
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
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