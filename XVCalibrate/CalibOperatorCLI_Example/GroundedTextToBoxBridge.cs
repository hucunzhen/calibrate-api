using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 调用仓库内 SAM_Inference/grounded_text_to_box.py（OWLv2），得到原图像素坐标框供 SAM ONNX box prompt 使用。
    /// </summary>
    public static class GroundedTextToBoxBridge
    {
        /// <summary>相对仓库根的默认脚本路径（与 ResolveModelPath 查找规则一致）。</summary>
        public const string DefaultScriptRepoRelative = "SAM_Inference/grounded_text_to_box.py";

        /// <summary>
        /// 得分最高的检测框；若无检测则抛出，异常信息尽量包含 Python stderr。
        /// </summary>
        public static (SamOnnxSegmentation.OrigBoxPrompt box, double score) QueryBestBoxOrThrow(
            CalibImage image,
            string textPrompt,
            double threshold,
            string pythonExe,
            string scriptAbsolutePath,
            int timeoutMs,
            bool rawQuery)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (string.IsNullOrWhiteSpace(textPrompt))
                throw new ArgumentException("文本提示为空", nameof(textPrompt));
            if (string.IsNullOrWhiteSpace(pythonExe))
                throw new ArgumentException("Python 可执行路径为空", nameof(pythonExe));
            if (string.IsNullOrWhiteSpace(scriptAbsolutePath))
                throw new ArgumentException("脚本路径为空", nameof(scriptAbsolutePath));
            if (!File.Exists(scriptAbsolutePath))
                throw new FileNotFoundException("grounding 脚本不存在", scriptAbsolutePath);

            string workDir = Path.GetDirectoryName(scriptAbsolutePath) ?? Environment.CurrentDirectory;
            string sessionDir = Path.Combine(Path.GetTempPath(), "calibrate-ground-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionDir);
            string tempBmp = Path.Combine(sessionDir, "ground_in.bmp");
            string outJson = Path.Combine(sessionDir, "ground_out.json");

            try
            {
                if (!image.Save(tempBmp))
                    throw new InvalidOperationException("无法写入临时 BMP 供 grounding 使用");

                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe.Trim(),
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                psi.Environment["PYTHONUNBUFFERED"] = "1";
                psi.ArgumentList.Add(scriptAbsolutePath);
                psi.ArgumentList.Add(tempBmp);
                psi.ArgumentList.Add(textPrompt);
                psi.ArgumentList.Add("--threshold");
                psi.ArgumentList.Add(threshold.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("--out-json");
                psi.ArgumentList.Add(outJson);
                if (rawQuery)
                    psi.ArgumentList.Add("--raw-query");

                using var proc = new Process { StartInfo = psi };
                proc.Start();
                // 必须在独立线程上同时读取 stdout/stderr，否则会与 WaitForExit 形成管道死锁（子进程写满缓冲区即卡住）。
                Task<string> stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
                Task<string> stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());

                if (!proc.WaitForExit(timeoutMs))
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // ignored
                    }

                    Task.WaitAll(new[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(30));
                    string errTail = stderrTask.Status == TaskStatus.RanToCompletion ? stderrTask.Result : "";
                    throw new TimeoutException(
                        $"文本 grounding 超时（>{timeoutMs} ms）。若首次下载模型，可增大 groundingTimeoutSec。" +
                        (string.IsNullOrWhiteSpace(errTail) ? "" : Environment.NewLine + errTail));
                }

                Task.WaitAll(new[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(120));
                string stdout = stdoutTask.Status == TaskStatus.RanToCompletion ? stdoutTask.Result : "";
                string stderr = stderrTask.Status == TaskStatus.RanToCompletion ? stderrTask.Result : "";

                if (!File.Exists(outJson))
                    throw new InvalidOperationException(
                        "grounding 未生成 JSON。" +
                        Environment.NewLine + "stdout: " + stdout +
                        Environment.NewLine + "stderr: " + stderr +
                        Environment.NewLine + "exit=" + proc.ExitCode);

                using var doc = JsonDocument.Parse(File.ReadAllText(outJson));
                var root = doc.RootElement;
                if (root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False)
                {
                    string err = root.TryGetProperty("error", out var eEl) ? eEl.GetString() ?? "unknown" : "unknown";
                    throw new InvalidOperationException($"文本 grounding 失败: {err}" +
                        (string.IsNullOrWhiteSpace(stderr) ? "" : Environment.NewLine + stderr));
                }

                if (!root.TryGetProperty("boxes", out var boxesEl) || boxesEl.ValueKind != JsonValueKind.Array || boxesEl.GetArrayLength() == 0)
                    throw new InvalidOperationException(
                        "文本 grounding 未得到任何框（可调低 textThreshold 或改写描述）。" +
                        (string.IsNullOrWhiteSpace(stderr) ? "" : Environment.NewLine + stderr));

                JsonElement best = boxesEl[0];
                double x1 = best.GetProperty("x1").GetDouble();
                double y1 = best.GetProperty("y1").GetDouble();
                double x2 = best.GetProperty("x2").GetDouble();
                double y2 = best.GetProperty("y2").GetDouble();
                double score = best.TryGetProperty("score", out var sEl) ? sEl.GetDouble() : 0;
                var box = new SamOnnxSegmentation.OrigBoxPrompt(x1, y1, x2, y2);
                return (box, score);
            }
            finally
            {
                TryDelete(sessionDir);
            }
        }

        private static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // ignored
            }
        }
    }
}
