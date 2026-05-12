using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 调用仓库内 <c>DIP_Inference/dip_denoise.py</c>（PyTorch Deep Image Prior）对图像去噪。
    /// </summary>
    public static class DipDenoiseBridge
    {
        public const string DefaultScriptRepoRelative = "DIP_Inference/dip_denoise.py";

        /// <summary>
        /// 将输入保存为临时 BMP → 运行 Python → 读取输出 BMP。
        /// </summary>
        public static CalibImage Run(
            CalibImage input,
            string pythonExecutable,
            string scriptAbsolutePath,
            int iterations,
            double learningRate,
            double tvWeight,
            int maxSide,
            bool preferCuda,
            int timeoutMilliseconds)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (string.IsNullOrWhiteSpace(pythonExecutable))
                throw new ArgumentException("Python 可执行文件路径为空", nameof(pythonExecutable));
            if (string.IsNullOrWhiteSpace(scriptAbsolutePath))
                throw new ArgumentException("脚本路径为空", nameof(scriptAbsolutePath));
            if (!File.Exists(scriptAbsolutePath))
                throw new FileNotFoundException("DIP 脚本不存在（请放置 DIP_Inference/dip_denoise.py）", scriptAbsolutePath);
            if (iterations < 1)
                throw new ArgumentOutOfRangeException(nameof(iterations));

            string tmpDir = Path.Combine(Path.GetTempPath(), "calibrate_dip_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            string inBmp = Path.Combine(tmpDir, "in.bmp");
            string outBmp = Path.Combine(tmpDir, "out.bmp");

            try
            {
                if (!CalibAPI.SaveImage(inBmp, input))
                    throw new InvalidOperationException($"DIP: 无法写入临时输入 {inBmp}");

                string dev = preferCuda ? "cuda" : "cpu";
                string lr = learningRate.ToString(CultureInfo.InvariantCulture);
                string tv = tvWeight.ToString(CultureInfo.InvariantCulture);

                var psi = new ProcessStartInfo
                {
                    FileName = pythonExecutable.Trim(),
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(scriptAbsolutePath) ?? Environment.CurrentDirectory,
                };

                psi.ArgumentList.Add(scriptAbsolutePath);
                psi.ArgumentList.Add("--input");
                psi.ArgumentList.Add(inBmp);
                psi.ArgumentList.Add("--output");
                psi.ArgumentList.Add(outBmp);
                psi.ArgumentList.Add("--iterations");
                psi.ArgumentList.Add(iterations.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("--lr");
                psi.ArgumentList.Add(lr);
                psi.ArgumentList.Add("--tv-weight");
                psi.ArgumentList.Add(tv);
                psi.ArgumentList.Add("--device");
                psi.ArgumentList.Add(dev);
                if (maxSide > 0)
                {
                    psi.ArgumentList.Add("--max-side");
                    psi.ArgumentList.Add(maxSide.ToString(CultureInfo.InvariantCulture));
                }

                using var proc = Process.Start(psi);
                if (proc == null)
                    throw new InvalidOperationException("DIP: 无法启动 Python 进程");

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

                    throw new TimeoutException($"DIP 去噪超时（{timeoutMilliseconds} ms）。可减小 iterations / maxSide，或增大超时。");
                }

                if (proc.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"DIP 脚本退出码 {proc.ExitCode}。" +
                        (string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr.Trim()));

                if (!File.Exists(outBmp))
                    throw new InvalidOperationException("DIP: 未生成输出图像。" + (stderr.Length > 0 ? "\n" + stderr.Trim() : ""));

                return CalibImage.Load(outBmp);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tmpDir))
                        Directory.Delete(tmpDir, recursive: true);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }
}
