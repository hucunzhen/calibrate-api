using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 预加载 Controller_SDK 原生 DLL（CommonToolDll / ControllerDll / sControllerDll），
    /// 避免 DllImport 仅在 exe 目录查找时找不到模块。
    /// </summary>
    internal static class ControllerSdkNativeLoader
    {
        private static readonly string[] NativeNames =
        {
            "CommonToolDll.dll",
            "ControllerDll.dll",
            "sControllerDll.dll"
        };

        private static bool _attempted;
        private static bool _ready;
        private static string? _detail;

        public static bool IsReady
        {
            get
            {
                EnsureLoaded();
                return _ready;
            }
        }

        public static string? LoadDetail
        {
            get
            {
                EnsureLoaded();
                return _detail;
            }
        }

        public static void EnsureLoaded()
        {
            if (_attempted)
                return;
            _attempted = true;

#if !CONTROLLER_SDK_ENABLED
            _detail = "未编译 CONTROLLER_SDK（缺少 ControllerDllCSharp.dll）。";
            return;
#else
            string baseDir = Path.GetFullPath(
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar);

            var searchDirs = new List<string> { baseDir };
            string sub = Path.Combine(baseDir, "ControllerSdk");
            if (Directory.Exists(sub))
                searchDirs.Add(sub);

            var missing = new List<string>();
            foreach (string name in NativeNames)
            {
                bool found = searchDirs.Exists(dir => File.Exists(Path.Combine(dir, name)));
                if (!found)
                    missing.Add(name);
            }

            foreach (string dir in searchDirs)
            {
                if (NativeNames.Any(name => !File.Exists(Path.Combine(dir, name))))
                    continue;

                try
                {
                    foreach (string name in NativeNames)
                    {
                        string path = Path.Combine(dir, name);
                        if (!NativeLibrary.TryLoad(path, out IntPtr handle) || handle == IntPtr.Zero)
                            throw new InvalidOperationException($"NativeLibrary.TryLoad 失败: {path}");
                    }

                    _ready = true;
                    _detail = $"已从 {dir} 加载 Controller_SDK 原生库。";
                    return;
                }
                catch (Exception ex)
                {
                    _ready = false;
                    _detail = $"加载 Controller_SDK 原生库失败（{dir}）: {ex.Message}";
                    return;
                }
            }

            _ready = false;
            var sb = new StringBuilder();
            sb.Append("未在程序目录找到 Controller_SDK 原生 DLL。");
            sb.Append($" 目录: {baseDir}");
            if (missing.Count > 0)
                sb.Append($"；缺少: {string.Join(", ", missing)}");
            sb.Append("。请重新生成项目（会执行 CopyControllerSdkNative）。");
            _detail = sb.ToString();
#endif
        }
    }
}
