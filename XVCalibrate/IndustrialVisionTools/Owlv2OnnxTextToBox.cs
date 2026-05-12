using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.DotNet;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// OWLv2 文本→框：纯 C# + ONNX Runtime + HuggingFace tokenizer.json（Tokenizers.DotNet / Rust hf_tokenizers）。
    /// 模型由 optimum-cli 等工具离线导出，运行时不再依赖 Python。
    /// </summary>
    public static class Owlv2OnnxTextToBox
    {
        public const string DefaultOnnxRepoRelative = "models/onnx/owlv2_base_patch16_ensemble.onnx";
        public const string DefaultTokenizerJsonRelative = "models/onnx/owlv2_tokenizer/tokenizer.json";

        /// <summary>单个 patch 候选（经阈值与 NMS 后保留）。</summary>
        public readonly record struct Owlv2Detection(SamOnnxSegmentation.OrigBoxPrompt Box, double Score, int PatchIndex);

        private const int Owlv2InputSize = 960;
        private const int MaxTextSequenceLength = 16;
        private static readonly float[] ClipMeanRgb = { 0.48145466f, 0.4578275f, 0.40821073f };
        private static readonly float[] ClipStdRgb = { 0.26862954f, 0.26130258f, 0.27577711f };
        private const uint PadTokenId = 0;

        private static readonly ConcurrentDictionary<string, Lazy<InferenceSession>> Sessions = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Lazy<Tokenizer>> Tokenizers = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, object> RunLocks = new(StringComparer.OrdinalIgnoreCase);

        private static object LockObj(string k) => RunLocks.GetOrAdd(k, _ => new object());

        /// <summary>
        /// 多个互不重叠（NMS）的检测框：对 ONNX 输出的每个 query/patch 打分，阈值过滤后按分数贪心 NMS，最多保留 <paramref name="maxDetections"/> 个。
        /// Xenova OWLv2 等为 (3600 patch)×框； previously 只取 argmax 会得到「单一目标」。
        /// </summary>
        public static IReadOnlyList<Owlv2Detection> QueryDetectionsOrThrow(
            CalibImage image,
            string textPrompt,
            double threshold,
            string onnxModelPath,
            string tokenizerJsonPath,
            bool rawQuery,
            bool useGpu,
            int maxDetections,
            double nmsIouThreshold)
        {
            if (maxDetections < 1)
                throw new ArgumentOutOfRangeException(nameof(maxDetections));
            if (nmsIouThreshold is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(nmsIouThreshold));

            RunOwlv2Forward(
                image,
                textPrompt,
                onnxModelPath,
                tokenizerJsonPath,
                rawQuery,
                useGpu,
                out float[] logitsFlat,
                out float[] boxesFlat,
                out int[] ld,
                out int[] bd,
                out int origW,
                out int origH);

            var ranked = CollectPatchCandidates(logitsFlat, ld, boxesFlat, bd, threshold, origW, origH);
            ranked.Sort((a, b) => b.score.CompareTo(a.score));

            float nms = (float)nmsIouThreshold;
            var kept = new List<Owlv2Detection>();
            foreach (var c in ranked)
            {
                bool suppressed = false;
                for (int i = 0; i < kept.Count; i++)
                {
                    if (BoxIoU(c.box, kept[i].Box) >= nms)
                    {
                        suppressed = true;
                        break;
                    }
                }

                if (suppressed)
                    continue;
                kept.Add(new Owlv2Detection(c.box, c.score, c.patchIndex));
                if (kept.Count >= maxDetections)
                    break;
            }

            if (kept.Count == 0)
                throw new InvalidOperationException(
                    $"文本 grounding 未得到满足阈值 {threshold} 的框（可调低 textThreshold 或改写描述）。");

            return kept;
        }

        /// <summary>
        /// 得分最高的检测框（原图像素 xyxy）；等价于 <see cref="QueryDetectionsOrThrow"/> 且 maxDetections=1。
        /// </summary>
        public static (SamOnnxSegmentation.OrigBoxPrompt box, double score) QueryBestBoxOrThrow(
            CalibImage image,
            string textPrompt,
            double threshold,
            string onnxModelPath,
            string tokenizerJsonPath,
            bool rawQuery,
            bool useGpu)
        {
            var list = QueryDetectionsOrThrow(
                image,
                textPrompt,
                threshold,
                onnxModelPath,
                tokenizerJsonPath,
                rawQuery,
                useGpu,
                maxDetections: 1,
                nmsIouThreshold: 0.5);
            var d = list[0];
            return (d.Box, d.Score);
        }

        private static void RunOwlv2Forward(
            CalibImage image,
            string textPrompt,
            string onnxModelPath,
            string tokenizerJsonPath,
            bool rawQuery,
            bool useGpu,
            out float[] logitsFlat,
            out float[] boxesFlat,
            out int[] ld,
            out int[] bd,
            out int origW,
            out int origH)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (string.IsNullOrWhiteSpace(textPrompt))
                throw new ArgumentException("文本提示为空", nameof(textPrompt));
            if (string.IsNullOrWhiteSpace(onnxModelPath))
                throw new ArgumentException("OWLv2 ONNX 路径为空", nameof(onnxModelPath));
            if (string.IsNullOrWhiteSpace(tokenizerJsonPath))
                throw new ArgumentException("tokenizer.json 路径为空", nameof(tokenizerJsonPath));
            if (!File.Exists(onnxModelPath))
                throw new FileNotFoundException("OWLv2 ONNX 不存在（请先按 models/onnx/README 导出）", onnxModelPath);
            if (!File.Exists(tokenizerJsonPath))
                throw new FileNotFoundException("tokenizer.json 不存在（可从 openai/clip-vit-base-patch32 复制）", tokenizerJsonPath);

            string query = rawQuery ? textPrompt.Trim() : $"a photo of {textPrompt.Trim()}";

            var lazyTok = Tokenizers.GetOrAdd(tokenizerJsonPath, p => new Lazy<Tokenizer>(() => new Tokenizer(p)));
            Tokenizer tokenizer = lazyTok.Value;

            uint[] encoded;
            lock (LockObj("tok:" + tokenizerJsonPath))
                encoded = tokenizer.Encode(query);

            int seqLen = Math.Min(MaxTextSequenceLength, encoded.Length);
            var ids = new long[MaxTextSequenceLength];
            var mask = new long[MaxTextSequenceLength];
            for (int i = 0; i < MaxTextSequenceLength; i++)
            {
                if (i < seqLen)
                {
                    ids[i] = encoded[i];
                    mask[i] = 1;
                }
                else
                {
                    ids[i] = PadTokenId;
                    mask[i] = 0;
                }
            }

            string sessionKey = onnxModelPath + "|" + (useGpu ? "cuda" : "cpu");
            var lazySes = Sessions.GetOrAdd(sessionKey, _ => new Lazy<InferenceSession>(() =>
            {
                var opt = new SessionOptions();
                if (useGpu)
                {
                    try
                    {
                        opt.AppendExecutionProvider_CUDA(0);
                    }
                    catch
                    {
                        // CPU 回退
                    }
                }

                return new InferenceSession(onnxModelPath, opt);
            }));
            InferenceSession session = lazySes.Value;

            using var pixelBitmap = BuildPreprocessedBitmap(image);
            origW = image.Width;
            origH = image.Height;
            var pixelTensor = BitmapToPixelValuesChw(pixelBitmap);

            string pixelInput = ResolveInputName(session, "pixel_values", "pixel");
            string idsInput = ResolveInputName(session, "input_ids", "input");
            string maskInput = ResolveInputName(session, "attention_mask", "mask");

            var pixelNv = NamedOnnxValue.CreateFromTensor(pixelInput, pixelTensor);
            NamedOnnxValue idsNv = CreateIntTensorNamed(idsInput, session.InputMetadata[idsInput].ElementDataType, ids);
            NamedOnnxValue maskNv = CreateIntTensorNamed(maskInput, session.InputMetadata[maskInput].ElementDataType, mask);

            string logitsOutput = ResolveOutputName(session, "logits", "logit");
            string boxesOutput = ResolveOutputName(session, "pred_boxes", "boxes");

            lock (LockObj(sessionKey))
            {
                using var results = session.Run(new List<NamedOnnxValue> { pixelNv, idsNv, maskNv });
                var logitsNv = results.SingleOrDefault(o =>
                    o.Name.Equals(logitsOutput, StringComparison.OrdinalIgnoreCase));
                var boxesNv = results.SingleOrDefault(o =>
                    o.Name.Equals(boxesOutput, StringComparison.OrdinalIgnoreCase));
                if (logitsNv == null || boxesNv == null)
                    throw new InvalidOperationException(
                        $"OWLv2 ONNX 输出缺少 logits/boxes（当前输出：{string.Join(", ", results.Select(r => r.Name))}）。");

                var logitsDense = logitsNv.AsTensor<float>().ToDenseTensor();
                var boxesDense = boxesNv.AsTensor<float>().ToDenseTensor();
                logitsFlat = logitsDense.ToArray();
                boxesFlat = boxesDense.ToArray();
                ld = logitsDense.Dimensions.ToArray();
                bd = boxesDense.Dimensions.ToArray();
            }
        }

        private static List<(int patchIndex, float score, SamOnnxSegmentation.OrigBoxPrompt box)> CollectPatchCandidates(
            float[] logitsFlat,
            int[] ld,
            float[] boxesFlat,
            int[] bd,
            double threshold,
            int origW,
            int origH)
        {
            if (ld.Length < 3 || bd.Length < 3)
                throw new InvalidOperationException($"OWLv2 logits/boxes 维度异常: logits=[{string.Join(",", ld)}], boxes=[{string.Join(",", bd)}]");

            int qCount = ld[^2];
            int numClasses = ld[^1];
            int boxQueries = bd[^2];
            int boxDim = bd[^1];
            if (boxDim != 4 || qCount != boxQueries)
                throw new InvalidOperationException($"OWLv2 输出形状不匹配: Q={qCount}, boxQ={boxQueries}, boxDim={boxDim}");

            int lcStride = numClasses;
            float thr = (float)threshold;
            var list = new List<(int patchIndex, float score, SamOnnxSegmentation.OrigBoxPrompt box)>(Math.Min(qCount, 4096));
            for (int q = 0; q < qCount; q++)
            {
                float s = float.NegativeInfinity;
                int row = q * lcStride;
                for (int c = 0; c < numClasses; c++)
                    s = Math.Max(s, Sigmoid(logitsFlat[row + c]));
                if (s < thr)
                    continue;

                int b0 = q * 4;
                float cx = boxesFlat[b0];
                float cy = boxesFlat[b0 + 1];
                float w = boxesFlat[b0 + 2];
                float h = boxesFlat[b0 + 3];
                var box = PredCxCywhToOrig(cx, cy, w, h, origW, origH);
                list.Add((q, s, box));
            }

            return list;
        }

        private static SamOnnxSegmentation.OrigBoxPrompt PredCxCywhToOrig(float cx, float cy, float w, float h, int origW, int origH)
        {
            float x1n = cx - w * 0.5f;
            float y1n = cy - h * 0.5f;
            float x2n = cx + w * 0.5f;
            float y2n = cy + h * 0.5f;

            float maxSide = Math.Max(origW, origH);
            double x1 = x1n * maxSide;
            double y1 = y1n * maxSide;
            double x2 = x2n * maxSide;
            double y2 = y2n * maxSide;

            const double eps = 1e-3;
            x1 = Math.Clamp(x1, 0, origW - eps);
            x2 = Math.Clamp(x2, eps, origW);
            y1 = Math.Clamp(y1, 0, origH - eps);
            y2 = Math.Clamp(y2, eps, origH);
            if (x2 <= x1) x2 = Math.Min(origW, x1 + 1.0);
            if (y2 <= y1) y2 = Math.Min(origH, y1 + 1.0);
            return new SamOnnxSegmentation.OrigBoxPrompt(x1, y1, x2, y2);
        }

        private static float BoxIoU(SamOnnxSegmentation.OrigBoxPrompt a, SamOnnxSegmentation.OrigBoxPrompt b)
        {
            double ax1 = Math.Min(a.X1, a.X2), ax2 = Math.Max(a.X1, a.X2);
            double ay1 = Math.Min(a.Y1, a.Y2), ay2 = Math.Max(a.Y1, a.Y2);
            double bx1 = Math.Min(b.X1, b.X2), bx2 = Math.Max(b.X1, b.X2);
            double by1 = Math.Min(b.Y1, b.Y2), by2 = Math.Max(b.Y1, b.Y2);
            double iw = Math.Max(0, Math.Min(ax2, bx2) - Math.Max(ax1, bx1));
            double ih = Math.Max(0, Math.Min(ay2, by2) - Math.Max(ay1, by1));
            double inter = iw * ih;
            double areaA = Math.Max(0, ax2 - ax1) * Math.Max(0, ay2 - ay1);
            double areaB = Math.Max(0, bx2 - bx1) * Math.Max(0, by2 - by1);
            double union = areaA + areaB - inter;
            return union > 1e-9 ? (float)(inter / union) : 0f;
        }

        private static NamedOnnxValue CreateIntTensorNamed(string inputName, TensorElementType elementType, long[] data)
        {
            return elementType switch
            {
                TensorElementType.Int64 => NamedOnnxValue.CreateFromTensor(inputName,
                    new DenseTensor<long>(data, new[] { 1, MaxTextSequenceLength })),
                TensorElementType.Int32 =>
                    NamedOnnxValue.CreateFromTensor(inputName,
                        new DenseTensor<int>(data.Select(x => (int)x).ToArray(), new[] { 1, MaxTextSequenceLength })),
                _ => throw new NotSupportedException($"OWLv2 输入 {inputName} 元素类型 {elementType} 不受支持，请导出为 int64 或 int32。"),
            };
        }

        private static float Sigmoid(float x)
        {
            if (x >= 0)
            {
                float z = MathF.Exp(-x);
                return 1f / (1f + z);
            }
            else
            {
                float z = MathF.Exp(x);
                return z / (1f + z);
            }
        }

        private static Bitmap BuildPreprocessedBitmap(CalibImage image)
        {
            using Bitmap? src = image.ToBitmap()
                ?? throw new InvalidOperationException("OWLv2: 无法将 CalibImage 转为 Bitmap");
            using Bitmap padded = PadToSquare(src);
            return ResizeSquare(padded, Owlv2InputSize);
        }

        private static Bitmap PadToSquare(Bitmap src)
        {
            int w = src.Width;
            int h = src.Height;
            int s = Math.Max(w, h);
            if (w == h && w == s)
                return new Bitmap(src);

            var bmp = new Bitmap(s, s, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(src, 0, 0, w, h);
            }
            return bmp;
        }

        private static Bitmap ResizeSquare(Bitmap src, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, size, size);
            }
            return bmp;
        }

        private static DenseTensor<float> BitmapToPixelValuesChw(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = new float[1 * 3 * h * w];
            BitmapData bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    byte* scan0 = (byte*)bd.Scan0;
                    int stride = bd.Stride;
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = scan0 + y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            byte bb = row[x * 3 + 0];
                            byte gg = row[x * 3 + 1];
                            byte rr = row[x * 3 + 2];
                            float rf = rr / 255f;
                            float gf = gg / 255f;
                            float bf = bb / 255f;
                            int ix = y * w + x;
                            data[0 * (h * w) + ix] = (rf - ClipMeanRgb[0]) / ClipStdRgb[0];
                            data[1 * (h * w) + ix] = (gf - ClipMeanRgb[1]) / ClipStdRgb[1];
                            data[2 * (h * w) + ix] = (bf - ClipMeanRgb[2]) / ClipStdRgb[2];
                        }
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }

            return new DenseTensor<float>(data, new[] { 1, 3, h, w });
        }

        private static string ResolveInputName(InferenceSession session, params string[] preferred)
        {
            foreach (var want in preferred)
            {
                foreach (var name in session.InputMetadata.Keys)
                {
                    if (name.Equals(want, StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }
            foreach (var want in preferred)
            {
                foreach (var name in session.InputMetadata.Keys)
                {
                    if (name.Contains(want, StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }
            throw new InvalidOperationException(
                "无法在 ONNX 模型中找到输入 " + string.Join(" / ", preferred) +
                $"（当前输入：{string.Join(", ", session.InputMetadata.Keys)}）。");
        }

        private static string ResolveOutputName(InferenceSession session, params string[] preferred)
        {
            foreach (var want in preferred)
            {
                foreach (var name in session.OutputMetadata.Keys)
                {
                    if (name.Equals(want, StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }
            foreach (var want in preferred)
            {
                foreach (var name in session.OutputMetadata.Keys)
                {
                    if (name.Contains(want, StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }
            throw new InvalidOperationException(
                "无法在 ONNX 模型中找到输出 " + string.Join(" / ", preferred) +
                $"（当前输出：{string.Join(", ", session.OutputMetadata.Keys)}）。");
        }
    }
}
