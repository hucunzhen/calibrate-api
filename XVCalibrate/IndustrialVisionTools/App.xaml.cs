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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string? lpPathName);

        static App()
        {
            // 海康 MvCameraControl.Net 通过原生 MvCameraControl.dll 等加载；必须从 exe 目录解析（快捷方式/安装器工作目录可能不是 app 根）
            try
            {
                string baseDir = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
                Directory.SetCurrentDirectory(baseDir);
                SetDllDirectory(baseDir);
            }
            catch { /* ignore */ }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var mainWindow = new MainWindow();
            mainWindow.Show();

            string? flowPath = TryParseFlowPathArg(e.Args, out bool preferNativeEngine);
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
                        ok = await mainWindow.RunFlowConfigInBackgroundAsync(flowPath, preferNativeEngine);
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

        private static bool IsPreferNativeEngineSwitch(string arg) =>
            string.Equals(arg, "--prefer-native-engine", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "-prefer-native-engine", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 解析 CLI：flow 路径 + 是否优先使用 C++ NativeFlowEngine（可与路径任意顺序混写）。
        /// </summary>
        private static string? TryParseFlowPathArg(string[] args, out bool preferNativeEngine)
        {
            preferNativeEngine = false;
            if (args == null || args.Length == 0) return null;

            foreach (string a in args)
            {
                if (IsPreferNativeEngineSwitch(a))
                    preferNativeEngine = true;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (IsPreferNativeEngineSwitch(arg)) continue;
                if (string.Equals(arg, "--flow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "-flow", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) return Path.GetFullPath(args[i + 1]);
                    return null;
                }
            }

            // 兼容直接传文件路径（可与开关混用，例如： app.exe my.flow.json --prefer-native-engine）
            foreach (string arg in args)
            {
                if (IsPreferNativeEngineSwitch(arg)) continue;
                if (arg.Length > 0 && arg[0] == '-') continue;
                if (arg.EndsWith(".flow.json", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(arg);
            }

            return null;
        }
    }
}