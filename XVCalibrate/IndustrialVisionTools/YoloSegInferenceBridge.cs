using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 调用 <c>YoloSeg_Tools/predict_seg.py</c>（Ultralytics YOLO-Seg：可视化图 + 检测 JSON）。
    /// </summary>
    public static class YoloSegInferenceBridge
    {
        public const string DefaultScriptRepoRelative = "YoloSeg_Tools/predict_seg.py";

        public readonly struct Result
        {
            public Result(CalibImage passthrough, CalibImage visualization, string detectJson)
            {
                Passthrough = passthrough;
                Visualization = visualization;
                DetectJson = detectJson;
            }

            public CalibImage Passthrough { get; }
            /// <summary>Ultralytics plot：掩码叠加（可选是否绘制检测框）。</summary>
            public CalibImage Visualization { get; }
            public string DetectJson { get; }
        }

        /// <summary>
        /// 并行异步读取 stdout/stderr，避免 stderr 写满导致死锁。
        /// </summary>
        public static Result Run(
            CalibImage input,
            string pythonExecutable,
            string scriptAbsolutePath,
            string weightsAbsolutePath,
            double conf,
            bool preferCuda,
            int timeoutMilliseconds,
            int imgsz,
            bool visNoBoxes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (string.IsNullOrWhiteSpace(pythonExecutable))
                throw new ArgumentException("Python 路径为空", nameof(pythonExecutable));
            if (string.IsNullOrWhiteSpace(scriptAbsolutePath))
                throw new ArgumentException("脚本路径为空", nameof(scriptAbsolutePath));
            if (!File.Exists(scriptAbsolutePath))
                throw new FileNotFoundException("predict_seg.py 未找到", scriptAbsolutePath);
            if (string.IsNullOrWhiteSpace(weightsAbsolutePath))
                throw new ArgumentException("权重路径为空", nameof(weightsAbsolutePath));
            if (!File.Exists(weightsAbsolutePath))
                throw new FileNotFoundException("YOLO 权重不存在", weightsAbsolutePath);

            string tmpDir = Path.Combine(Path.GetTempPath(), "calibrate_yoloseg_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            string inBmp = Path.Combine(tmpDir, "in.bmp");
            string outVis = Path.Combine(tmpDir, "yolo_vis.bmp");
            string outJson = Path.Combine(tmpDir, "yolo_det.json");

            try
            {
                if (!CalibAPI.SaveImage(inBmp, input))
                    throw new InvalidOperationException($"YOLO-Seg: 无法写入临时输入 {inBmp}");

                string dev = preferCuda ? "cuda:0" : "cpu";

                var psi = new ProcessStartInfo
                {
                    FileName = pythonExecutable.Trim(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(scriptAbsolutePath) ?? Environment.CurrentDirectory,
                };
                try
                {
                    psi.StandardOutputEncoding = Encoding.UTF8;
                    psi.StandardErrorEncoding = Encoding.UTF8;
                }
                catch
                {
                    // ignored
                }

                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add(scriptAbsolutePath);
                psi.ArgumentList.Add("--input");
                psi.ArgumentList.Add(inBmp);
                psi.ArgumentList.Add("--weights");
                psi.ArgumentList.Add(weightsAbsolutePath);
                psi.ArgumentList.Add("--output-vis");
                psi.ArgumentList.Add(outVis);
                psi.ArgumentList.Add("--output-json");
                psi.ArgumentList.Add(outJson);
                psi.ArgumentList.Add("--conf");
                psi.ArgumentList.Add(conf.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("--device");
                psi.ArgumentList.Add(dev);
                if (imgsz > 0)
                {
                    psi.ArgumentList.Add("--imgsz");
                    psi.ArgumentList.Add(imgsz.ToString(CultureInfo.InvariantCulture));
                }

                if (visNoBoxes)
                    psi.ArgumentList.Add("--vis-no-boxes");

                using var proc = Process.Start(psi);
                if (proc == null)
                    throw new InvalidOperationException("YOLO-Seg: 无法启动 Python");

                Task<string> readOut = proc.StandardOutput.ReadToEndAsync();
                Task<string> readErr = proc.StandardError.ReadToEndAsync();

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

                    throw new TimeoutException($"YOLO-Seg 推理超时（{timeoutMilliseconds} ms），可增大超时秒数。");
                }

                string so = readOut.GetAwaiter().GetResult();
                string se = readErr.GetAwaiter().GetResult();

                if (proc.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"YOLO-Seg 脚本退出码 {proc.ExitCode}。" +
                        (string.IsNullOrWhiteSpace(se) ? "" : "\n" + se.Trim()) +
                        (string.IsNullOrWhiteSpace(so) ? "" : "\n" + so.Trim()));

                if (!File.Exists(outJson) || !File.Exists(outVis))
                    throw new InvalidOperationException(
                        "YOLO-Seg: 未生成输出文件。" +
                        (string.IsNullOrWhiteSpace(se) ? "" : "\n" + se.Trim()));

                string json = File.ReadAllText(outJson, Encoding.UTF8);
                var dup = CalibAPI.DuplicateImage(input);
                var vis = CalibAPI.LoadImage(outVis);
                return new Result(dup, vis, json);
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
