using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 调用本仓库 <c>JIT_Inference/jit_calibrate_launcher.py</c>，在本地克隆的
    /// <c>jkyl/just-image-transformer</c> 中运行 JiT（Just Image Transformers）类条件采样。
    /// </summary>
    public static class JitSampleBridge
    {
        public const string DefaultLauncherRepoRelative = "JIT_Inference/jit_calibrate_launcher.py";

        public const string DefaultConfigYamlRelative = "config/jit_L_32.yaml";

        /// <summary>
        /// 解析存在的目录（相对路径：exe 目录、再向上级查找源码树）。
        /// </summary>
        public static string ResolveExistingDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("JiT 仓库路径为空", nameof(path));
            path = path.Trim().Replace('/', Path.DirectorySeparatorChar);

            if (Directory.Exists(path))
                return Path.GetFullPath(path);

            if (Path.IsPathRooted(path))
                throw new DirectoryNotFoundException($"JiT 仓库目录不存在: {path}");

            static string CombineUnder(string dir, string rel) =>
                Path.GetFullPath(Path.Combine(dir, rel));

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string fromExe = CombineUnder(baseDir, path);
            if (Directory.Exists(fromExe))
                return fromExe;

            try
            {
                for (DirectoryInfo? di = new DirectoryInfo(baseDir); di != null; di = di.Parent)
                {
                    string cand = CombineUnder(di.FullName, path);
                    if (Directory.Exists(cand))
                        return cand;
                }
            }
            catch
            {
                // ignored
            }

            throw new DirectoryNotFoundException(
                $"未找到 JiT 仓库目录（请克隆 https://github.com/jkyl/just-image-transformer 并配置依赖）: {path}");
        }

        /// <summary>
        /// 运行 JiT 采样，输出 PNG 并加载为 <see cref="CalibImage"/>。
        /// </summary>
        public static CalibImage Run(
            string launcherScriptAbsolutePath,
            string jitRepoRoot,
            string pythonExecutable,
            string configYamlPath,
            string checkpointAbsolutePath,
            string outputImageAbsolutePath,
            int seed,
            int label,
            double cfgStrength,
            int numSteps,
            string schedule,
            int timeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(pythonExecutable))
                throw new ArgumentException("Python 可执行文件路径为空", nameof(pythonExecutable));
            if (string.IsNullOrWhiteSpace(launcherScriptAbsolutePath))
                throw new ArgumentException("启动脚本路径为空", nameof(launcherScriptAbsolutePath));
            if (!File.Exists(launcherScriptAbsolutePath))
                throw new FileNotFoundException("JiT 启动脚本不存在", launcherScriptAbsolutePath);
            if (string.IsNullOrWhiteSpace(configYamlPath))
                throw new ArgumentException("JiT config 路径为空", nameof(configYamlPath));
            if (string.IsNullOrWhiteSpace(checkpointAbsolutePath))
                throw new ArgumentException("checkpoint 路径为空", nameof(checkpointAbsolutePath));
            if (!File.Exists(checkpointAbsolutePath))
                throw new FileNotFoundException("JiT checkpoint 不存在（可为 model.npz 或内含 model.npz 的 .zip）", checkpointAbsolutePath);
            if (numSteps < 1)
                throw new ArgumentOutOfRangeException(nameof(numSteps));

            string repo = ResolveExistingDirectory(jitRepoRoot);
            string cfgStr = cfgStrength.ToString(CultureInfo.InvariantCulture);
            schedule = string.IsNullOrWhiteSpace(schedule) ? "linear" : schedule.Trim();

            var psi = new ProcessStartInfo
            {
                FileName = pythonExecutable.Trim(),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(launcherScriptAbsolutePath) ?? Environment.CurrentDirectory,
            };

            psi.ArgumentList.Add(launcherScriptAbsolutePath);
            psi.ArgumentList.Add("--repo");
            psi.ArgumentList.Add(repo);
            psi.ArgumentList.Add(configYamlPath.Trim());
            psi.ArgumentList.Add(checkpointAbsolutePath);
            psi.ArgumentList.Add(outputImageAbsolutePath);
            psi.ArgumentList.Add("--seed");
            psi.ArgumentList.Add(seed.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--label");
            psi.ArgumentList.Add(label.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--cfg_strength");
            psi.ArgumentList.Add(cfgStr);
            psi.ArgumentList.Add("--num_steps");
            psi.ArgumentList.Add(numSteps.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--schedule");
            psi.ArgumentList.Add(schedule);

            using var proc = Process.Start(psi);
            if (proc == null)
                throw new InvalidOperationException("JiT: 无法启动 Python 进程");

            string stderr = proc.StandardError.ReadToEnd();
            proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(timeoutMilliseconds))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignored
                }

                throw new TimeoutException(
                    $"JiT 采样超时（{timeoutMilliseconds} ms）。可减少 num_steps / 增大超时；需 JAX + CUDA（Ampere+）。");
            }

            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"JiT 推理退出码 {proc.ExitCode}。" +
                    (string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr.Trim()));

            if (!File.Exists(outputImageAbsolutePath))
                throw new InvalidOperationException(
                    "JiT 未生成输出图像。" + (stderr.Length > 0 ? "\n" + stderr.Trim() : ""));

            return CalibImage.Load(outputImageAbsolutePath);
        }
    }
}
