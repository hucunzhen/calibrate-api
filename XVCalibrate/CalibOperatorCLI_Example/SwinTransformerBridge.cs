using System;

using System.Diagnostics;

using System.Globalization;

using System.IO;

using System.Text;

using CalibOperatorPInvoke;



namespace CalibOperatorCLI_Example

{

    /// <summary>

    /// 调用 <c>Swin_Inference/swin_infer.py</c>（timm Swin：可选 ImageNet 分类、特征向量 JSON、特征热力图 BMP）。

    /// </summary>

    public static class SwinTransformerBridge

    {

        public const string DefaultScriptRepoRelative = "Swin_Inference/swin_infer.py";



        public readonly struct Result

        {

            public Result(CalibImage passthrough, string labelsJson, string embeddingJson, CalibImage segmentHeatmap)

            {

                Passthrough = passthrough;

                LabelsJson = labelsJson;

                EmbeddingJson = embeddingJson;

                SegmentHeatmap = segmentHeatmap;

            }



            public CalibImage Passthrough { get; }

            /// <summary>主摘要 JSON（含 top_k、embedding_dim 等）。</summary>

            public string LabelsJson { get; }

            /// <summary>密集向量 <c>{"dim":N,"values":[...]}</c>；关闭时为 <c>{}</c>。</summary>

            public string EmbeddingJson { get; }

            /// <summary>与原图同尺寸的灰度热力图；关闭时为输入图像的拷贝占位。</summary>

            public CalibImage SegmentHeatmap { get; }

        }



        /// <summary>

        /// 写入临时 BMP → Python → 读取输出；返回透传图、摘要 JSON、嵌入 JSON、热力图。

        /// </summary>

        public static Result Run(

            CalibImage input,

            string pythonExecutable,

            string scriptAbsolutePath,

            string modelName,

            int topK,

            bool preferCuda,

            int timeoutMilliseconds,

            bool enableClassification,

            bool enableEmbedding,

            bool enableSegmentHeatmap)

        {

            if (input == null) throw new ArgumentNullException(nameof(input));

            if (string.IsNullOrWhiteSpace(pythonExecutable))

                throw new ArgumentException("Python 可执行文件路径为空", nameof(pythonExecutable));

            if (string.IsNullOrWhiteSpace(scriptAbsolutePath))

                throw new ArgumentException("脚本路径为空", nameof(scriptAbsolutePath));

            if (!File.Exists(scriptAbsolutePath))

                throw new FileNotFoundException("Swin 脚本不存在（请放置 Swin_Inference/swin_infer.py）", scriptAbsolutePath);

            if (!enableClassification && !enableEmbedding && !enableSegmentHeatmap)

                throw new ArgumentException("至少开启分类、特征向量或分割热力图之一。");

            if (enableClassification && (topK < 1 || topK > 1000))

                throw new ArgumentOutOfRangeException(nameof(topK));



            string tmpDir = Path.Combine(Path.GetTempPath(), "calibrate_swin_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tmpDir);

            string inBmp = Path.Combine(tmpDir, "in.bmp");

            string outJson = Path.Combine(tmpDir, "swin_out.json");

            string outEmb = Path.Combine(tmpDir, "swin_emb.json");

            string outSeg = Path.Combine(tmpDir, "swin_seg.bmp");



            try

            {

                if (!CalibAPI.SaveImage(inBmp, input))

                    throw new InvalidOperationException($"Swin: 无法写入临时输入 {inBmp}");



                string dev = preferCuda ? "cuda" : "cpu";



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

                psi.ArgumentList.Add("--output-json");

                psi.ArgumentList.Add(outJson);

                psi.ArgumentList.Add("--model");

                psi.ArgumentList.Add(string.IsNullOrWhiteSpace(modelName) ? "swin_tiny_patch4_window7_224" : modelName.Trim());

                psi.ArgumentList.Add("--topk");

                psi.ArgumentList.Add(topK.ToString(CultureInfo.InvariantCulture));

                psi.ArgumentList.Add("--device");

                psi.ArgumentList.Add(dev);

                psi.ArgumentList.Add("--classify");

                psi.ArgumentList.Add(enableClassification ? "true" : "false");

                psi.ArgumentList.Add("--embedding");

                psi.ArgumentList.Add(enableEmbedding ? "true" : "false");

                if (enableEmbedding)

                {

                    psi.ArgumentList.Add("--output-embedding");

                    psi.ArgumentList.Add(outEmb);

                }



                psi.ArgumentList.Add("--segment");

                psi.ArgumentList.Add(enableSegmentHeatmap ? "true" : "false");

                if (enableSegmentHeatmap)

                {

                    psi.ArgumentList.Add("--output-segment");

                    psi.ArgumentList.Add(outSeg);

                }



                using var proc = Process.Start(psi);

                if (proc == null)

                    throw new InvalidOperationException("Swin: 无法启动 Python 进程");



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



                    throw new TimeoutException($"Swin 推理超时（{timeoutMilliseconds} ms）。首次运行会下载权重，可增大超时。");

                }



                if (proc.ExitCode != 0)

                    throw new InvalidOperationException(

                        $"Swin 脚本退出码 {proc.ExitCode}。" +

                        (string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr.Trim()));



                if (!File.Exists(outJson))

                    throw new InvalidOperationException("Swin: 未生成 JSON。" + (stderr.Length > 0 ? "\n" + stderr.Trim() : ""));



                string json = File.ReadAllText(outJson, Encoding.UTF8);

                var dup = CalibAPI.DuplicateImage(input);



                string embJson = "{}";

                if (enableEmbedding)

                {

                    if (!File.Exists(outEmb))

                        throw new InvalidOperationException("Swin: 未生成特征向量 JSON。" + (stderr.Length > 0 ? "\n" + stderr.Trim() : ""));

                    embJson = File.ReadAllText(outEmb, Encoding.UTF8);

                }



                CalibImage segImg;

                if (enableSegmentHeatmap)

                {

                    if (!File.Exists(outSeg))

                        throw new InvalidOperationException("Swin: 未生成分割热力图 BMP。" + (stderr.Length > 0 ? "\n" + stderr.Trim() : ""));

                    segImg = CalibAPI.LoadImage(outSeg);

                }

                else

                {

                    segImg = CalibAPI.DuplicateImage(dup);

                }



                return new Result(dup, json, embJson, segImg);

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


