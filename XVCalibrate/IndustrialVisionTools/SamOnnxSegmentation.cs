using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// SAM 双 ONNX（encoder + decoder）推理：与仓库内 export_sam_onnx.py / Meta SamOnnxModel 对齐。
    /// </summary>
    public static class SamOnnxSegmentation
    {
        private const int SamInputSize = 1024;

        private static readonly float[] PixelMean = { 123.675f, 116.28f, 103.53f };
        private static readonly float[] PixelStd = { 58.395f, 57.12f, 57.375f };

        private sealed class SessionPair : IDisposable
        {
            public InferenceSession Encoder { get; }
            public InferenceSession Decoder { get; }

            public SessionPair(InferenceSession enc, InferenceSession dec)
            {
                Encoder = enc;
                Decoder = dec;
            }

            public void Dispose()
            {
                Encoder.Dispose();
                Decoder.Dispose();
            }
        }

        private static readonly ConcurrentDictionary<string, Lazy<SessionPair>> Sessions = new ConcurrentDictionary<string, Lazy<SessionPair>>();

        public sealed class Result
        {
            /// <summary>IoU 最高的候选掩码（与 SAM 多掩码导出一致时使用）。</summary>
            public CalibImage Mask { get; set; } = null!;
            /// <summary>按 IoU 排序的第二、三、四个候选（decoder 仅单掩码导出时为 null）。</summary>
            public CalibImage? Mask2 { get; set; }
            public CalibImage? Mask3 { get; set; }
            public CalibImage? Mask4 { get; set; }
            public CalibImage Vis { get; set; } = null!;
            /// <summary>最佳候选的 IoU 预测。</summary>
            public float IouPrediction { get; set; }
            /// <summary>每个 ONNX 掩码槽位的 IoU（未排序）；可能长于实际候选数。</summary>
            public float[]? IouPerMaskRaw { get; set; }
            /// <summary>decoder 输出的掩码个数（通常为 3 或 4；单掩码导出为 1）。</summary>
            public int MaskCandidateCount { get; set; }
            /// <summary>
            /// <see cref="MergeMaskOutputsUnion"/> 优先合并此序列：多框路径为各实例主掩码（长度由 <see cref="RunMultiBox"/> 的 <c>maxInstances</c> 与框数决定）；
            /// 单提示 SAM 多候选时为 Mask～Mask4 按 IoU 排序后的列表（decoder 导出多掩码时通常 ≤4）。
            /// </summary>
            public IReadOnlyList<CalibImage>? MaskUnionSources { get; set; }
        }

        /// <summary>
        /// 仓库内 FP32 ONNX（默认）：动态量化含 Conv 的 encoder 会产生 ConvInteger，标准 ORT CPU 常未实现，故默认用 FP32。
        /// </summary>
        public const string DefaultEncoderRepoRelative = "models/onnx/sam_vit_b_encoder.onnx";

        /// <summary>
        /// 仓库内 FP32 ONNX（默认）。
        /// </summary>
        public const string DefaultDecoderRepoRelative = "models/onnx/sam_vit_b_decoder.onnx";

        /// <summary>
        /// 原图像素坐标下的轴对齐框（例如由 OWLv2 文本 grounding 得到），用于 SAM 的 box prompt（两点：左上 label=2、右下 label=3）。
        /// </summary>
        public readonly record struct OrigBoxPrompt(double X1, double Y1, double X2, double Y2);

        /// <summary>
        /// 解析模型路径：绝对路径规范化；
        /// 相对路径先相对 exe 目录（BaseDirectory），不存在则沿上级目录查找源码树中的同名相对路径（直至找到含文件的目录）。
        /// </summary>
        public static string ResolveModelPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("模型路径为空", nameof(path));
            path = path.Trim().Replace('/', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);

            static string CombineUnder(string dir, string rel) =>
                Path.GetFullPath(Path.Combine(dir, rel));

            static string? FirstExistingAncestor(string startDir, string rel)
            {
                try
                {
                    for (DirectoryInfo? di = new DirectoryInfo(startDir); di != null; di = di.Parent)
                    {
                        string cand = CombineUnder(di.FullName, rel);
                        if (File.Exists(cand))
                            return cand;
                    }
                }
                catch
                {
                    // ignored
                }

                return null;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            string fromExe = CombineUnder(baseDir, path);
            if (File.Exists(fromExe))
                return fromExe;

            string? fromWalk = FirstExistingAncestor(baseDir, path);
            if (fromWalk != null)
                return fromWalk;

            try
            {
                string? asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    string fromAsm = CombineUnder(asmDir, path);
                    if (File.Exists(fromAsm))
                        return fromAsm;
                    fromWalk = FirstExistingAncestor(asmDir, path);
                    if (fromWalk != null)
                        return fromWalk;
                }
            }
            catch
            {
                // ignored
            }

            try
            {
                string cwd = Environment.CurrentDirectory;
                string fromCwd = CombineUnder(cwd, path);
                if (File.Exists(fromCwd))
                    return fromCwd;
                fromWalk = FirstExistingAncestor(cwd, path);
                if (fromWalk != null)
                    return fromWalk;
            }
            catch
            {
                // ignored
            }

            return fromExe;
        }

        /// <summary>
        /// 将紧密排列的灰度行写入 CalibImage（与 CALIB_CreateBlankImage 的 4 字节行对齐一致）。
        /// </summary>
        private static void CopyGrayPackedToNative(CalibImage dst, byte[] packedGray, int width, int height)
        {
            NativeImage ni = dst.GetNativeStruct();
            if (ni.data == IntPtr.Zero || packedGray.Length < width * height)
                throw new InvalidOperationException("SAM mask: 缓冲区与尺寸不匹配");
            int tightRow = width;
            int stride = tightRow % 4 == 0 ? tightRow : ((tightRow / 4) + 1) * 4;
            if (stride == tightRow)
            {
                Marshal.Copy(packedGray, 0, ni.data, width * height);
                return;
            }

            for (int y = 0; y < height; y++)
                Marshal.Copy(packedGray, y * width, IntPtr.Add(ni.data, y * stride), width);
        }

        private static SessionPair GetOrCreateSessions(string encoderPath, string decoderPath, bool useGpu)
        {
            string key = encoderPath + "\n" + decoderPath + "\n" + (useGpu ? "1" : "0");
            var lazy = Sessions.GetOrAdd(key, _ => new Lazy<SessionPair>(() =>
            {
                if (!File.Exists(encoderPath))
                    throw new FileNotFoundException("SAM encoder ONNX 不存在", encoderPath);
                if (!File.Exists(decoderPath))
                    throw new FileNotFoundException("SAM decoder ONNX 不存在", decoderPath);

                var opt = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    InterOpNumThreads = 0,
                    IntraOpNumThreads = 0,
                };
                if (useGpu)
                {
                    try
                    {
                        opt.AppendExecutionProvider_CUDA(0);
                    }
                    catch
                    {
                        // 未安装 CUDA EP 时退回 CPU
                    }
                }

                var enc = new InferenceSession(encoderPath, opt);
                var dec = new InferenceSession(decoderPath, opt);
                return new SessionPair(enc, dec);
            }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));

            return lazy.Value;
        }

        /// <summary>
        /// 单点前前景分割（可选第二点为背景），或 <paramref name="boxOrig"/> 框提示（优先于点）。
        /// 坐标均为原图像素，左上角为原点。
        /// </summary>
        public static Result Run(
            CalibImage image,
            IReadOnlyList<Point2D>? promptPoints,
            double fallbackClickX,
            double fallbackClickY,
            string encoderPath,
            string decoderPath,
            float maskLogitThreshold,
            bool useGpu,
            OrigBoxPrompt? boxOrig = null)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));

            int origH = image.Height;
            int origW = image.Width;
            if (origH <= 0 || origW <= 0)
                throw new InvalidOperationException("SAM: 输入图像尺寸无效");

            GetPreprocessShape(origH, origW, SamInputSize, out int newH, out int newW);

            using Bitmap rgbCanvas = CalibImageToRgbBitmap(image);
            using Bitmap resized = ResizeRgb(rgbCanvas, newW, newH);
            using Bitmap padded = PadTopLeftBlack(resized, SamInputSize, SamInputSize);

            var encoderTensor = RgbBitmapToNormalizedNchw(padded);

            List<Point2f> pts = new List<Point2f>();
            List<float> labels = new List<float>();

            if (boxOrig.HasValue)
                AppendBoxCornersNorm(boxOrig.Value, origW, origH, newW, newH, pts, labels);
            else if (promptPoints != null && promptPoints.Count > 0)
            {
                pts.Add(new Point2f(
                    (float)(promptPoints[0].X * newW / origW),
                    (float)(promptPoints[0].Y * newH / origH)));
                labels.Add(1f);
                if (promptPoints.Count >= 2)
                {
                    pts.Add(new Point2f(
                        (float)(promptPoints[1].X * newW / origW),
                        (float)(promptPoints[1].Y * newH / origH)));
                    labels.Add(0f);
                }
            }
            else
            {
                pts.Add(new Point2f(
                    (float)(fallbackClickX * newW / origW),
                    (float)(fallbackClickY * newH / origH)));
                labels.Add(1f);
            }

            var sessions = GetOrCreateSessions(encoderPath, decoderPath, useGpu);
            var embTensor = RunSamEncoder(sessions, encoderTensor);
            return RunSamDecode(image, sessions, embTensor, origH, origW, newH, newW, pts, labels, maskLogitThreshold);
        }

        /// <summary>
        /// 文本 grounding 等多实例：共享一次 SAM encoder，对前 <c>min(框数, maxInstances)</c> 个框依次 decoder。
        /// Mask～Mask4 仍为第 1～4 个实例的主掩码；更多实例仅出现在 <see cref="Result.MaskUnionSources"/> 供 MaskAll 合并。
        /// </summary>
        /// <param name="maxInstances">最多解码几次（与 Flow 中 maskMergeMax 等对齐）；默认解码全部传入的框。</param>
        public static Result RunMultiBox(
            CalibImage image,
            IReadOnlyList<OrigBoxPrompt> boxPrompts,
            string encoderPath,
            string decoderPath,
            float maskLogitThreshold,
            bool useGpu,
            int maxInstances = int.MaxValue)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (boxPrompts == null || boxPrompts.Count == 0)
                throw new ArgumentException("boxPrompts 不能为空", nameof(boxPrompts));
            if (maxInstances < 1)
                throw new ArgumentOutOfRangeException(nameof(maxInstances));

            int origH = image.Height;
            int origW = image.Width;
            if (origH <= 0 || origW <= 0)
                throw new InvalidOperationException("SAM: 输入图像尺寸无效");

            GetPreprocessShape(origH, origW, SamInputSize, out int newH, out int newW);

            using Bitmap rgbCanvas = CalibImageToRgbBitmap(image);
            using Bitmap resized = ResizeRgb(rgbCanvas, newW, newH);
            using Bitmap padded = PadTopLeftBlack(resized, SamInputSize, SamInputSize);

            var encoderTensor = RgbBitmapToNormalizedNchw(padded);
            var sessions = GetOrCreateSessions(encoderPath, decoderPath, useGpu);
            var embTensor = RunSamEncoder(sessions, encoderTensor);

            int nInst = Math.Min(boxPrompts.Count, maxInstances);
            var partial = new Result[nInst];
            var overlayBins = new List<byte[]>(Math.Min(nInst, 3));
            var unionList = new List<CalibImage>(nInst);
            for (int i = 0; i < nInst; i++)
            {
                var pts = new List<Point2f>();
                var labels = new List<float>();
                AppendBoxCornersNorm(boxPrompts[i], origW, origH, newW, newH, pts, labels);
                partial[i] = RunSamDecode(image, sessions, embTensor, origH, origW, newH, newW, pts, labels, maskLogitThreshold);
                unionList.Add(partial[i].Mask);
                if (overlayBins.Count < 3)
                    overlayBins.Add(ReadGrayMaskTightBytes(partial[i].Mask));
            }

            CalibImage vis = overlayBins.Count > 0
                ? BlendMultiOverlay(image, overlayBins, partial[0].Mask.Width, partial[0].Mask.Height)
                : CalibAPI.DuplicateImage(image);

            CalibImage? m2 = null, m3 = null, m4 = null;
            if (nInst >= 2) m2 = partial[1].Mask;
            if (nInst >= 3) m3 = partial[2].Mask;
            if (nInst >= 4) m4 = partial[3].Mask;

            return new Result
            {
                Mask = partial[0].Mask,
                Mask2 = m2,
                Mask3 = m3,
                Mask4 = m4,
                Vis = vis,
                IouPrediction = partial[0].IouPrediction,
                IouPerMaskRaw = partial[0].IouPerMaskRaw,
                MaskCandidateCount = partial[0].MaskCandidateCount,
                MaskUnionSources = unionList,
            };
        }

        private static DenseTensor<float> RunSamEncoder(SessionPair sessions, DenseTensor<float> encoderTensor)
        {
            var encInput = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("image", encoderTensor)
            };

            using var encOut = sessions.Encoder.Run(encInput);
            var first = encOut.First(x => x.Name == "image_embeddings");
            return first.AsTensor<float>().ToDenseTensor();
        }

        private static Result RunSamDecode(
            CalibImage image,
            SessionPair sessions,
            DenseTensor<float> embTensor,
            int origH,
            int origW,
            int newH,
            int newW,
            List<Point2f> pts,
            List<float> labels,
            float maskLogitThreshold)
        {
            int nPts = pts.Count;
            float[] pcArr = new float[nPts * 2];
            for (int i = 0; i < nPts; i++)
            {
                pcArr[i * 2] = pts[i].X;
                pcArr[i * 2 + 1] = pts[i].Y;
            }

            var pointCoords = new DenseTensor<float>(pcArr, new[] { 1, nPts, 2 });
            var pointLabels = new DenseTensor<float>(labels.ToArray(), new[] { 1, nPts });

            const int maskSide = 256;
            var maskInput = new DenseTensor<float>(new float[1 * 1 * maskSide * maskSide], new[] { 1, 1, maskSide, maskSide });
            var hasMask = new DenseTensor<float>(new[] { 0f }, new[] { 1 });
            var origSize = new DenseTensor<float>(new[] { (float)origH, (float)origW }, new[] { 2 });

            var decInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("image_embeddings", embTensor),
                NamedOnnxValue.CreateFromTensor("point_coords", pointCoords),
                NamedOnnxValue.CreateFromTensor("point_labels", pointLabels),
                NamedOnnxValue.CreateFromTensor("mask_input", maskInput),
                NamedOnnxValue.CreateFromTensor("has_mask_input", hasMask),
                NamedOnnxValue.CreateFromTensor("orig_im_size", origSize),
            };

            using var decOut = sessions.Decoder.Run(decInputs);
            var masksNv = decOut.First(x => x.Name == "masks");
            var iouNv = decOut.First(x => x.Name == "iou_predictions");
            var masksDense = masksNv.AsTensor<float>().ToDenseTensor();
            var iouDense = iouNv.AsTensor<float>().ToDenseTensor();
            return BuildMultiMaskResult(
                image,
                masksDense,
                iouDense,
                maskLogitThreshold);
        }

        private static void AppendBoxCornersNorm(
            OrigBoxPrompt b,
            int origW,
            int origH,
            int newW,
            int newH,
            List<Point2f> pts,
            List<float> labels)
        {
            double x1 = Math.Min(b.X1, b.X2);
            double x2 = Math.Max(b.X1, b.X2);
            double y1 = Math.Min(b.Y1, b.Y2);
            double y2 = Math.Max(b.Y1, b.Y2);
            const double eps = 1e-3;
            x1 = Math.Clamp(x1, 0, origW - eps);
            x2 = Math.Clamp(x2, eps, origW);
            y1 = Math.Clamp(y1, 0, origH - eps);
            y2 = Math.Clamp(y2, eps, origH);
            if (x2 <= x1) x2 = Math.Min(origW, x1 + 1.0);
            if (y2 <= y1) y2 = Math.Min(origH, y1 + 1.0);

            pts.Add(new Point2f((float)(x1 * newW / origW), (float)(y1 * newH / origH)));
            labels.Add(2f);
            pts.Add(new Point2f((float)(x2 * newW / origW), (float)(y2 * newH / origH)));
            labels.Add(3f);
        }

        private static byte[] ReadGrayMaskTightBytes(CalibImage m)
        {
            NativeImage ni = m.GetNativeStruct();
            int w = ni.width;
            int h = ni.height;
            if (ni.data == IntPtr.Zero || w <= 0 || h <= 0 || ni.channels != 1)
                throw new InvalidOperationException("SAM: 掩码图为空或通道数无效");

            int tightRow = w;
            int stride = tightRow % 4 == 0 ? tightRow : ((tightRow / 4) + 1) * 4;
            byte[] packed = new byte[w * h];
            for (int y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(ni.data, y * stride), packed, y * w, w);
            return packed;
        }

        /// <summary>
        /// 将多路掩码叠成一张 <strong>3 通道 BGR</strong> 图：黑底上按掩码顺序用<strong>不同色相</strong>以固定透明度逐层 alpha 混合（重叠处会叠色，模拟半透明）。
        /// 优先使用 <see cref="Result.MaskUnionSources"/>；否则退回 Mask→Mask4。需要单通道二值并集时请对各路掩码自行做逻辑或。
        /// </summary>
        public static CalibImage MergeMaskOutputsUnion(Result seg, int maxMerge)
        {
            if (seg == null) throw new ArgumentNullException(nameof(seg));
            if (maxMerge < 1) throw new ArgumentOutOfRangeException(nameof(maxMerge));

            var masks = new List<CalibImage>();

            if (seg.MaskUnionSources != null && seg.MaskUnionSources.Count > 0)
            {
                foreach (CalibImage m in seg.MaskUnionSources)
                {
                    if (m == null) continue;
                    masks.Add(m);
                    if (masks.Count >= maxMerge) break;
                }
            }
            else
            {
                CalibImage?[] slots = { seg.Mask, seg.Mask2, seg.Mask3, seg.Mask4 };
                foreach (CalibImage? m in slots)
                {
                    if (m == null) continue;
                    masks.Add(m);
                    if (masks.Count >= maxMerge) break;
                }
            }

            if (masks.Count == 0)
                throw new InvalidOperationException("SAM MaskAll: 无可用掩码，无法合并");

            int w = masks[0].Width;
            int h = masks[0].Height;
            foreach (CalibImage m in masks)
            {
                if (m.Width != w || m.Height != h || m.Channels != 1)
                    throw new InvalidOperationException(
                        $"SAM MaskAll: 掩码须为单通道且尺寸一致 ({w}×{h})");
            }

            int wh = w * h;
            var accB = new float[wh];
            var accG = new float[wh];
            var accR = new float[wh];
            const float layerAlpha = 0.42f;
            float invA = 1f - layerAlpha;

            for (int mi = 0; mi < masks.Count; mi++)
            {
                HsvToRgbBytesGolden(mi, out byte cr8, out byte cg8, out byte cb8);
                float cr = cr8, cg = cg8, cb = cb8;
                byte[] packed = ReadGrayMaskTightBytes(masks[mi]);
                for (int i = 0; i < wh; i++)
                {
                    if (packed[i] < 128)
                        continue;
                    accR[i] = accR[i] * invA + cr * layerAlpha;
                    accG[i] = accG[i] * invA + cg * layerAlpha;
                    accB[i] = accB[i] * invA + cb * layerAlpha;
                }
            }

            byte[] buf = new byte[wh * 3];
            for (int i = 0; i < wh; i++)
            {
                buf[i * 3 + 0] = (byte)Math.Clamp((int)MathF.Round(accB[i]), 0, 255);
                buf[i * 3 + 1] = (byte)Math.Clamp((int)MathF.Round(accG[i]), 0, 255);
                buf[i * 3 + 2] = (byte)Math.Clamp((int)MathF.Round(accR[i]), 0, 255);
            }

            var dst = new CalibImage(w, h, 3);
            NativeImage dni = dst.GetNativeStruct();
            if (dni.data == IntPtr.Zero)
                throw new InvalidOperationException("SAM MaskAll: 目标图像缓冲区无效");
            Marshal.Copy(buf, 0, dni.data, buf.Length);
            return dst;
        }

        /// <summary>黄金角分布色相，使相邻索引颜色区分大。</summary>
        private static void HsvToRgbBytesGolden(int index, out byte r, out byte g, out byte b)
        {
            float hue = (index * 0.618033988749895f + 0.112f) % 1f;
            HsvToRgbBytes(hue, 0.84f, 0.96f, out r, out g, out b);
        }

        private static void HsvToRgbBytes(float h, float s, float v, out byte r, out byte g, out byte b)
        {
            h = (h % 1f + 1f) % 1f;
            s = Math.Clamp(s, 0f, 1f);
            v = Math.Clamp(v, 0f, 1f);
            float hh = h * 6f;
            int sector = (int)MathF.Floor(hh);
            float f = hh - sector;
            float p = v * (1f - s);
            float q = v * (1f - f * s);
            float t = v * (1f - (1f - f) * s);
            float rf, gf, bf;
            switch (sector % 6)
            {
                case 0: rf = v; gf = t; bf = p; break;
                case 1: rf = q; gf = v; bf = p; break;
                case 2: rf = p; gf = v; bf = t; break;
                case 3: rf = p; gf = q; bf = v; break;
                case 4: rf = t; gf = p; bf = v; break;
                default: rf = v; gf = p; bf = q; break;
            }

            r = (byte)Math.Clamp((int)MathF.Round(rf * 255f), 0, 255);
            g = (byte)Math.Clamp((int)MathF.Round(gf * 255f), 0, 255);
            b = (byte)Math.Clamp((int)MathF.Round(bf * 255f), 0, 255);
        }

        private static Result BuildMultiMaskResult(
            CalibImage image,
            DenseTensor<float> masksDense,
            DenseTensor<float> iouDense,
            float maskLogitThreshold)
        {
            float[] logits = masksDense.ToArray();
            float[] iouRaw = iouDense.ToArray();
            int[] dims = masksDense.Dimensions.ToArray();

            if (!TryGetMaskGeometry(dims, logits.Length, image.Height, image.Width, out int nMask, out int maskH, out int maskW))
                throw new InvalidOperationException(
                    $"SAM decoder 输出 masks 形状无法解析: dims=[{string.Join(",", dims)}], len={logits.Length}。" +
                    "若需要 SAM 的多个候选掩码，请使用 export_sam_onnx.py 导出 decoder 时不要加 --return-single-mask。");

            int hw = maskH * maskW;
            if (hw <= 0 || nMask * hw != logits.Length)
                throw new InvalidOperationException("SAM masks 数据长度与形状不一致。");

            float[] iousAligned = AlignIouScores(iouRaw, nMask);

            int[] order = new int[nMask];
            for (int i = 0; i < nMask; i++) order[i] = i;
            Array.Sort(order, (a, b) => iousAligned[b].CompareTo(iousAligned[a]));

            CalibImage? m2 = null, m3 = null, m4 = null;
            CalibImage[] ranked = new CalibImage[Math.Min(4, nMask)];
            var overlaySlices = new List<byte[]>();
            int overlayCap = Math.Min(3, nMask);

            for (int rank = 0; rank < ranked.Length; rank++)
            {
                int mi = order[rank];
                var slice = new float[hw];
                Array.Copy(logits, mi * hw, slice, 0, hw);
                byte[] bin = SliceToBinary(slice, maskLogitThreshold);
                var img = new CalibImage(maskW, maskH, 1);
                CopyGrayPackedToNative(img, bin, maskW, maskH);
                ranked[rank] = img;
                if (rank < overlayCap)
                    overlaySlices.Add(bin);
            }

            CalibImage primary = ranked[0];
            if (ranked.Length >= 2) m2 = ranked[1];
            if (ranked.Length >= 3) m3 = ranked[2];
            if (ranked.Length >= 4) m4 = ranked[3];

            CalibImage vis = overlaySlices.Count > 0
                ? BlendMultiOverlay(image, overlaySlices, maskW, maskH)
                : CalibAPI.DuplicateImage(image);

            var unionList = new List<CalibImage>(ranked.Length);
            foreach (CalibImage ri in ranked)
                unionList.Add(ri);

            return new Result
            {
                Mask = primary,
                Mask2 = m2,
                Mask3 = m3,
                Mask4 = m4,
                Vis = vis,
                IouPrediction = iousAligned[order[0]],
                IouPerMaskRaw = iouRaw,
                MaskCandidateCount = nMask,
                MaskUnionSources = unionList,
            };
        }

        private static float[] AlignIouScores(float[] iouRaw, int nMask)
        {
            var row = new float[nMask];
            for (int i = 0; i < nMask; i++)
                row[i] = i < iouRaw.Length ? iouRaw[i] : iouRaw.Length > 0 ? iouRaw[iouRaw.Length - 1] : 0f;
            return row;
        }

        private static byte[] SliceToBinary(float[] slice, float threshold)
        {
            var bin = new byte[slice.Length];
            for (int i = 0; i < slice.Length; i++)
                bin[i] = slice[i] > threshold ? (byte)255 : (byte)0;
            return bin;
        }

        /// <summary>
        /// 解析 ONNX masks 张量：期望 [1,N,H,W]；兼容 [N,H,W]、[1,1,H,W]。
        /// </summary>
        private static bool TryGetMaskGeometry(int[] dims, int flatLen, int origH, int origW, out int nMask, out int maskH, out int maskW)
        {
            nMask = 1;
            maskH = origH;
            maskW = origW;

            if (dims.Length == 4 && dims[2] > 0 && dims[3] > 0)
            {
                nMask = dims[1];
                maskH = dims[2];
                maskW = dims[3];
                return nMask > 0 && nMask * maskH * maskW == flatLen;
            }

            if (dims.Length == 3 && dims[1] > 0 && dims[2] > 0)
            {
                nMask = dims[0];
                maskH = dims[1];
                maskW = dims[2];
                return nMask > 0 && nMask * maskH * maskW == flatLen;
            }

            if (dims.Length >= 2)
            {
                maskH = dims[dims.Length - 2];
                maskW = dims[dims.Length - 1];
                if (maskH > 0 && maskW > 0 && maskH * maskW == flatLen)
                    return true;
            }

            maskH = origH;
            maskW = origW;
            if (flatLen == maskH * maskW)
                return true;
            int side = (int)Math.Round(Math.Sqrt(flatLen));
            if (side > 0 && side * side == flatLen)
            {
                maskH = side;
                maskW = side;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 将最多 3 个候选掩码用不同通道叠色（BGR：候选1偏绿、2偏蓝、3偏红）。
        /// </summary>
        private static CalibImage BlendMultiOverlay(CalibImage srcBgr, IReadOnlyList<byte[]> masksOrdered, int maskW, int maskH)
        {
            CalibImage dup = CalibAPI.DuplicateImage(srcBgr);
            NativeImage dn = dup.GetNativeStruct();
            if (dn.channels != 3 || dn.data == IntPtr.Zero || masksOrdered.Count == 0)
                return dup;

            int w = dn.width;
            int h = dn.height;
            int rowBytes = w * 3;
            byte[] row = new byte[rowBytes];
            (int db, int dg, int dr)[] tint =
            {
                (0, 95, 0),
                (85, 35, 0),
                (0, 40, 110),
            };

            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(IntPtr.Add(dn.data, y * rowBytes), row, 0, rowBytes);
                for (int x = 0; x < w; x++)
                {
                    int mx = x * maskW / Math.Max(1, w);
                    int my = y * maskH / Math.Max(1, h);
                    if (mx >= maskW) mx = maskW - 1;
                    if (my >= maskH) my = maskH - 1;
                    int idx = my * maskW + mx;

                    int bi = x * 3;
                    for (int li = 0; li < masksOrdered.Count && li < tint.Length; li++)
                    {
                        if (masksOrdered[li][idx] < 128)
                            continue;
                        var t = tint[li];
                        row[bi + 0] = (byte)Math.Min(255, row[bi + 0] + t.db);
                        row[bi + 1] = (byte)Math.Min(255, row[bi + 1] + t.dg);
                        row[bi + 2] = (byte)Math.Min(255, row[bi + 2] + t.dr);
                    }
                }

                Marshal.Copy(row, 0, IntPtr.Add(dn.data, y * rowBytes), rowBytes);
            }

            return dup;
        }

        private readonly struct Point2f
        {
            public Point2f(float x, float y)
            {
                X = x;
                Y = y;
            }

            public float X { get; }
            public float Y { get; }
        }

        private static void GetPreprocessShape(int h, int w, int target, out int newH, out int newW)
        {
            float scale = target / (float)Math.Max(h, w);
            newH = Math.Max(1, (int)Math.Round(h * scale));
            newW = Math.Max(1, (int)Math.Round(w * scale));
        }

        private static Bitmap CalibImageToRgbBitmap(CalibImage img)
        {
            int ch = img.Channels;
            if (ch == 3)
            {
                Bitmap b = img.ToBitmap();
                if (b == null)
                    throw new InvalidOperationException("SAM: 无法转换彩色图为 Bitmap");
                return b;
            }

            if (ch != 1)
                throw new InvalidOperationException($"SAM: 仅支持 1 或 3 通道图像，当前 Channels={ch}");

            NativeImage n = img.GetNativeStruct();
            if (n.data == IntPtr.Zero || n.width <= 0 || n.height <= 0)
                throw new InvalidOperationException("SAM: 灰度图数据无效");

            var bmp = new Bitmap(n.width, n.height, PixelFormat.Format24bppRgb);
            Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = bd.Stride;
                unsafe
                {
                    byte* dstBase = (byte*)bd.Scan0;
                    for (int y = 0; y < n.height; y++)
                    {
                        byte* dst = dstBase + y * stride;
                        for (int x = 0; x < n.width; x++)
                        {
                            byte g = System.Runtime.InteropServices.Marshal.ReadByte(n.data, y * n.width + x);
                            dst[x * 3 + 0] = g;
                            dst[x * 3 + 1] = g;
                            dst[x * 3 + 2] = g;
                        }
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }

            return bmp;
        }

        private static Bitmap ResizeRgb(Bitmap src, int newW, int newH)
        {
            var bmp = new Bitmap(newW, newH, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, newW, newH);
            }

            return bmp;
        }

        private static Bitmap PadTopLeftBlack(Bitmap src, int canvasW, int canvasH)
        {
            var bmp = new Bitmap(canvasW, canvasH, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.DrawImage(src, 0, 0);
            }

            return bmp;
        }

        private static DenseTensor<float> RgbBitmapToNormalizedNchw(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            float[] buf = new float[1 * 3 * h * w];
            Rectangle rect = new Rectangle(0, 0, w, h);
            BitmapData bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = bd.Stride;
                unsafe
                {
                    byte* basePtr = (byte*)bd.Scan0;
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = basePtr + y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            byte r = row[x * 3 + 2];
                            byte gch = row[x * 3 + 1];
                            byte b = row[x * 3 + 0];
                            float rf = r, gf = gch, bf = b;
                            int ri = 0, gi = 1, bi = 2;
                            buf[ri * (h * w) + y * w + x] = (rf - PixelMean[0]) / PixelStd[0];
                            buf[gi * (h * w) + y * w + x] = (gf - PixelMean[1]) / PixelStd[1];
                            buf[bi * (h * w) + y * w + x] = (bf - PixelMean[2]) / PixelStd[2];
                        }
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }

            return new DenseTensor<float>(buf, new[] { 1, 3, h, w });
        }

    }

    internal static class SamOnnxTensorExtensions
    {
        public static DenseTensor<float> ToDenseTensor(this Tensor<float> t)
        {
            if (t is DenseTensor<float> d)
                return d;
            int len = (int)t.Length;
            var dims = new int[t.Rank];
            for (int i = 0; i < t.Rank; i++)
                dims[i] = t.Dimensions[i];
            var buf = new float[len];
            for (int flat = 0; flat < len; flat++)
                buf[flat] = t.GetValue(flat);
            return new DenseTensor<float>(buf, dims);
        }
    }
}
