#if HALCON_ENABLED
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HalconDotNet;

namespace CalibOperatorCLI_Example
{
    internal sealed class HalconDlSegTrainOptions
    {
        public string ExportRoot { get; set; } = "";
        public string PretrainedHdlPath { get; set; } = "";
        public string OutputHdlPath { get; set; } = "";
        public int Epochs { get; set; } = 5;
        public int BatchSize { get; set; } = 2;
        public double LearningRate { get; set; } = 0.001;
        public bool UseGpu { get; set; } = true;

        /// <summary>
        /// true：将缩放后的训练 image 转为 real [0,1]（接近 preprocess_dl_samples 常见输出）。
        /// false：保持 byte（与本页 <see cref="HalconDlSegBridge.RunSegmentationInference"/> 一致；若干预训练分割模型在 train_dl_model_batch 下可避免 #9001）。
        /// </summary>
        public bool NormalizeTrainingImageToReal01 { get; set; }
    }

    /// <summary>
    /// 使用 HALCON 算子 train_dl_model_batch（HalconDotNet HDlModel.TrainDlModelBatch）在托管代码中做语义分割微调。
    /// 假设导出目录含 flat 的 images/ 与 segmentation/（与 HalconDlDatasetExport 一致）。
    /// 预处理仅为缩放至模型 image_dimensions；若与官方 preprocess_dl_dataset 不一致，请在 HDevelop 中对照参数或改用过程式流程。
    /// </summary>
    internal static class HalconDlSegTrainRunner
    {
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".webp",
        };

        private static void TrySetParam(HDlModel model, string name, HTuple value, Action<string> log)
        {
            try
            {
                model.SetDlModelParam(name, value);
            }
            catch (Exception ex)
            {
                log($"[warn] SetDlModelParam {name} 失败（可忽略若默认值可用）: {ex.Message}");
            }
        }

        private static List<(string Img, string Seg)> CollectPairs(string exportRoot, Action<string> log)
        {
            string imgDir = Path.Combine(exportRoot, "images");
            string segDir = Path.Combine(exportRoot, "segmentation");
            if (!Directory.Exists(imgDir))
                throw new InvalidOperationException($"未找到导出图像目录: {imgDir}");
            if (!Directory.Exists(segDir))
                throw new InvalidOperationException($"未找到导出分割目录: {segDir}");

            var pairs = new List<(string, string)>();
            foreach (string imgPath in Directory.GetFiles(imgDir))
            {
                string ext = Path.GetExtension(imgPath);
                if (!ImageExtensions.Contains(ext))
                    continue;
                string stem = Path.GetFileNameWithoutExtension(imgPath);
                string segPath = Path.Combine(segDir, stem + ".png");
                if (!File.Exists(segPath))
                {
                    log($"[skip] 无对应 segmentation: {stem}");
                    continue;
                }
                pairs.Add((imgPath, segPath));
            }

            if (pairs.Count == 0)
                throw new InvalidOperationException("没有可用的 image + segmentation 配对样本。");
            return pairs;
        }

        private static void DiagDescribeImage(string tag, HObject img, Action<string> log)
        {
            try
            {
                HOperatorSet.CountChannels(img, out HTuple nc);
                HOperatorSet.GetImageType(img, out HTuple typ);
                string ts = typ.Length >= 1 ? typ[0].S : typ.S;
                HOperatorSet.GetImageSize(img, out HTuple iw, out HTuple ih);
                log($"[diag] {tag}: {iw.I}x{ih.I}, channels={nc.I}, pixel_type={ts}");
            }
            catch (Exception ex)
            {
                log($"[diag] {tag}: 读取失败 ({ex.Message})");
            }
        }

        /// <summary>从已构建的 DLSample 读取三张图并打印类型（用于首步核对 batch 内每张样本）。</summary>
        private static void DiagDescribeDlSample(HDict sample, string tag, Action<string> log)
        {
            foreach (string key in new[] { "image", "segmentation_image", "weight_image" })
            {
                HObject? ho = null;
                try
                {
                    ho = sample.GetDictObject(key);
                    DiagDescribeImage($"{tag} · {key}", ho, log);
                }
                catch (Exception ex)
                {
                    log($"[diag] {tag} · {key}: 读取失败 ({ex.Message})");
                }
                finally
                {
                    ho?.Dispose();
                }
            }
        }

        /// <summary>
        /// train_dl_model_batch 要求的元组长度为 batch_size × batch_size_multiplier（见官方文档）。
        /// 仅传 batch_size 个 DLSample 而 multiplier&gt;1 时，易出现 #9001 等与类型/维度相关的误报。
        /// </summary>
        private static int TryGetBatchSizeMultiplier(HDlModel model, Action<string> log)
        {
            try
            {
                HTuple t = model.GetDlModelParam("batch_size_multiplier");
                if (t != null && t.Length >= 1)
                {
                    int m = t[0].I;
                    if (m >= 1)
                    {
                        if (m > 1)
                            log($"[diag] batch_size_multiplier={m}：每步须提交 UI batch_size×{m} 个 DLSample。");
                        return m;
                    }
                }
            }
            catch (Exception ex)
            {
                log($"[diag] batch_size_multiplier 读取失败，按 1 处理: {ex.Message}");
            }

            return 1;
        }

        private static void LogDlModelDiagnostics(HDlModel model, Action<string> log)
        {
            foreach (string key in new[] { "type", "image_type", "pix_type", "num_channels", "batch_size_multiplier", "intensity_range", "class_ids" })
            {
                try
                {
                    HTuple t = model.GetDlModelParam(key);
                    log($"[diag] get_dl_model_param('{key}') = {t}");
                }
                catch (Exception ex)
                {
                    log($"[diag] get_dl_model_param('{key}') 不可用: {ex.Message}");
                }
            }
        }

        /// <summary>构建单张 DLSample：image、segmentation_image、weight_image。</summary>
        /// <remarks>
        /// 经测试确认正确的类型组合：
        ///   image:               real [0,1]（compact / enhanced 预训练模型均要求）
        ///   segmentation_image:  uint2（类别索引图，像素值为 0..N_classes-1）
        ///   weight_image:        real 1.0（与官方 gen_dl_segmentation_weights 一致）
        /// CPU 模式下 train_dl_model_batch 对 compact 模型不支持，会报 #9001；
        /// GPU 模式在 image_type 正确后需要 cuBLAS（CUDA Toolkit），缺失时报 #7717。
        /// </remarks>
        private static HDict BuildSample(
            string imgPath,
            string segPath,
            int targetW,
            int targetH,
            int modelChannels,
            Action<string>? diagLog,
            bool normalizeImageToReal01)
        {
            HObject? hoWork = null;
            HObject? hoSeg = null;
            HObject? imgZ = null;
            HObject? segZ = null;
            HObject? wimg = null;

            try
            {
                HOperatorSet.ReadImage(out HObject hoRead, imgPath);
                if (diagLog != null)
                {
                    diagLog($"[diag] 首张样本文件: image={imgPath}");
                    diagLog($"[diag] 首张样本文件: seg={segPath}");
                    diagLog($"[diag] 模型期望输入通道数 modelChannels={modelChannels}, 目标缩放 {targetW}x{targetH}");
                    DiagDescribeImage("① ReadImage 后（原始输入）", hoRead, diagLog);
                }

                hoWork = HalconDlSegBridge.PrepareDlSampleInputImage(hoRead, modelChannels, imgPath);
                if (diagLog != null)
                    DiagDescribeImage("② 对齐通道后（将送入 Zoom）", hoWork, diagLog);

                HOperatorSet.ReadImage(out hoSeg, segPath);
                if (diagLog != null)
                    DiagDescribeImage("③ ReadImage 分割标签（原始）", hoSeg, diagLog);

                hoSeg = HalconDlSegBridge.EnsureDlSegmentationSingleChannel(hoSeg, segPath);
                if (diagLog != null)
                    DiagDescribeImage("④ 分割标签（强制单通道后）", hoSeg, diagLog);

                HOperatorSet.ZoomImageSize(hoWork, out imgZ, targetW, targetH, "constant");
                hoWork.Dispose();
                hoWork = null;
                if (diagLog != null)
                    DiagDescribeImage("⑤ image Zoom 后（byte，将转 real）", imgZ, diagLog);

                HOperatorSet.ZoomImageSize(hoSeg, out segZ, targetW, targetH, "nearest_neighbor");
                hoSeg.Dispose();
                hoSeg = null;
                if (diagLog != null)
                    DiagDescribeImage("⑥ segmentation Zoom 后（byte，将转 uint2）", segZ, diagLog);

                // 分割标签必须转为 uint2（类别索引图）
                segZ = HalconDlSegBridge.ConvertDlSegmentationToUint2(segZ);
                if (diagLog != null)
                    DiagDescribeImage("⑥b segmentation → uint2", segZ, diagLog);

                // image 始终 normalize 为 real [0,1]（compact/enhanced 模型要求）
                // normalizeImageToReal01 参数保留用于兼容，但 compact 模型必须 real
                imgZ = HalconDlSegBridge.ConvertDlTrainingByteImageToReal01(imgZ);
                if (diagLog != null)
                    DiagDescribeImage("⑦ image → real [0,1]", imgZ, diagLog);

                // weight_image 始终 real 1.0（与 gen_dl_segmentation_weights 一致）
                wimg = HalconDlSegBridge.CreateDlTrainingUniformWeightRealAlways(targetW, targetH);
                if (diagLog != null)
                    DiagDescribeImage("⑧ weight_image（real 1.0）", wimg, diagLog);

                imgZ = HalconDlSegBridge.EnsureDlTrainingFullDomain(imgZ);
                segZ = HalconDlSegBridge.EnsureDlTrainingFullDomain(segZ);
                wimg = HalconDlSegBridge.EnsureDlTrainingFullDomain(wimg);
                if (diagLog != null)
                {
                    DiagDescribeImage("⑨ image FullDomain 后（写入字典）", imgZ, diagLog);
                    DiagDescribeImage("⑩ segmentation_image FullDomain 后（写入字典）", segZ, diagLog);
                    DiagDescribeImage("⑪ weight_image FullDomain 后（写入字典）", wimg, diagLog);
                }

                var sample = new HDict();
                sample.CreateDict();
                sample.SetDictObject(imgZ, "image");
                sample.SetDictObject(segZ, "segmentation_image");
                sample.SetDictObject(wimg, "weight_image");
                imgZ = segZ = wimg = null;

                return sample;
            }
            finally
            {
                hoWork?.Dispose();
                hoSeg?.Dispose();
                imgZ?.Dispose();
                segZ?.Dispose();
                wimg?.Dispose();
            }
        }

        private static void DisposeSamples(HDict[] batch)
        {
            foreach (HDict? d in batch)
            {
                try
                {
                    d?.Dispose();
                }
                catch
                {
                    /* ignore */
                }
            }
        }

        private static void Shuffle<T>(IList<T> list, Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        public static void Run(HalconDlSegTrainOptions opt, Action<string> log, CancellationToken ct = default)
        {
            if (opt == null) throw new ArgumentNullException(nameof(opt));
            if (log == null) throw new ArgumentNullException(nameof(log));
            if (string.IsNullOrWhiteSpace(opt.ExportRoot) || !Directory.Exists(opt.ExportRoot))
                throw new InvalidOperationException("请先导出或选择有效的导出目录。");
            if (string.IsNullOrWhiteSpace(opt.PretrainedHdlPath) || !File.Exists(opt.PretrainedHdlPath))
                throw new InvalidOperationException("请选择有效的预训练 .hdl（如 HALCON 自带的 pretrained_dl_segmentation_compact.hdl）。");
            if (string.IsNullOrWhiteSpace(opt.OutputHdlPath))
                throw new InvalidOperationException("请填写输出 .hdl 路径。");
            if (opt.Epochs < 1) throw new InvalidOperationException("epochs 须 ≥ 1。");
            if (opt.BatchSize < 1) throw new InvalidOperationException("batch_size 须 ≥ 1。");

            string exportRoot = Path.GetFullPath(opt.ExportRoot.Trim());
            List<(string Img, string Seg)> pairs = CollectPairs(exportRoot, log);
            log($"样本数: {pairs.Count}（来自 {exportRoot}）");

            using var model = new HDlModel();
            model.ReadDlModel(opt.PretrainedHdlPath);
            log("[diag] 模型句柄已加载，以下为 get_dl_model_param 探测（用于对照 #3359/#9001）：");
            LogDlModelDiagnostics(model, log);

            HTuple dims = model.GetDlModelParam("image_dimensions");
            if (dims == null || dims.Length < 2)
                throw new InvalidOperationException("无法读取模型 image_dimensions。");
            int targetH = dims[0].I;
            int targetW = dims[1].I;
            if (targetW <= 1 || targetH <= 1)
                throw new InvalidOperationException($"无效的 image_dimensions: {targetW}x{targetH}");
            log($"模型输入尺寸: {targetW} x {targetH}");
            int modelChannels = HalconDlSegBridge.TryGetDlModelInputChannels(model, log);

            // 经测试：compact / enhanced 分割模型训练时 image 必须为 real [0,1]，
            // image_type 参数在 compact 模型上不存在（#1302），不再依赖它自动检测。
            // normalizeToReal01 始终强制 true。
            bool normalizeToReal01 = true;
            log($"[diag] 训练固定使用 real [0,1] image（compact 模型要求；image_type 参数在该模型不可用）。");

            int batchMult = TryGetBatchSizeMultiplier(model, log);
            int samplesPerTrainStep = Math.Max(1, checked(opt.BatchSize * batchMult));
            log($"每步 TrainDlModelBatch 将提交 {samplesPerTrainStep} 个 DLSample（= batch_size {opt.BatchSize} × multiplier {batchMult}）。");
            log($"DLSample 类型: image=real[0,1], segmentation_image=uint2, weight_image=real 1.0, 全部 FullDomain。");
            log($"注意: compact/enhanced 模型不支持 CPU 训练（#9001），GPU 训练需要 CUDA Toolkit（cuBLAS，#7717 表示缺失）。");

            TrySetParam(model, "batch_size", opt.BatchSize, log);
            TrySetParam(model, "learning_rate", new HTuple(opt.LearningRate), log);
            string rt = opt.UseGpu ? "gpu" : "cpu";
            TrySetParam(model, "runtime", new HTuple(rt), log);

            string outPath = Path.GetFullPath(opt.OutputHdlPath.Trim());
            string? outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);

            var rng = new Random();
            int n = pairs.Count;

            for (int epoch = 0; epoch < opt.Epochs; epoch++)
            {
                ct.ThrowIfCancellationRequested();
                log($"--- epoch {epoch + 1}/{opt.Epochs} ---");

                var order = new List<(string Img, string Seg)>(pairs);
                Shuffle(order, rng);
                int pad = (samplesPerTrainStep - (order.Count % samplesPerTrainStep)) % samplesPerTrainStep;
                for (int p = 0; p < pad; p++)
                    order.Add(order[rng.Next(n)]);

                int batchesPerEpoch = order.Count / samplesPerTrainStep;

                for (int b = 0; b < batchesPerEpoch; b++)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = new HDict[samplesPerTrainStep];
                    try
                    {
                        for (int j = 0; j < samplesPerTrainStep; j++)
                        {
                            var pair = order[b * samplesPerTrainStep + j];
                            Action<string>? dlog = epoch == 0 && b == 0 && j == 0 ? log : null;
                            batch[j] = BuildSample(
                                pair.Img,
                                pair.Seg,
                                targetW,
                                targetH,
                                modelChannels,
                                dlog,
                                normalizeToReal01);
                        }

                        if (epoch == 0 && b == 0)
                        {
                            log("[diag] 首步 TrainDlModelBatch 提交前：batch 内各样本字典中的图像类型（含非首张）：");
                            for (int j = 0; j < samplesPerTrainStep; j++)
                                DiagDescribeDlSample(batch[j]!, $"样本[{j}]", log);
                        }

                        HDict result = model.TrainDlModelBatch(batch);
                        try
                        {
                            HTuple loss = result.GetDictTuple("total_loss");
                            log($"  batch {b + 1}/{batchesPerEpoch} total_loss={loss}");
                        }
                        catch
                        {
                            log($"  batch {b + 1}/{batchesPerEpoch} 完成（无 total_loss 字段）");
                        }
                        finally
                        {
                            result.Dispose();
                        }
                    }
                    finally
                    {
                        DisposeSamples(batch);
                    }
                }
            }

            model.WriteDlModel(outPath);
            log($"已写入模型: {outPath}");
        }
    }
}
#endif
