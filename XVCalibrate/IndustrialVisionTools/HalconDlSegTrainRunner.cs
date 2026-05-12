#if HALCON_ENABLED
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HalconDotNet;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 使用 HALCON 算子 train_dl_model_batch（HalconDotNet HDlModel.TrainDlModelBatch）在托管代码中做语义分割微调。
    /// 样本构建见 <see cref="HalconDlSegTrainSampleBuilder"/>；配对收集见 <see cref="HalconDlSegTrainDataset"/>。
    /// </summary>
    internal static class HalconDlSegTrainRunner
    {
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
            List<(string Img, string Seg)> pairs = HalconDlSegTrainDataset.CollectPairs(exportRoot, log);
            log($"样本数: {pairs.Count}（来自 {exportRoot}）");

            using var model = new HDlModel();
            model.ReadDlModel(opt.PretrainedHdlPath);
            log("[diag] 模型句柄已加载，以下为 get_dl_model_param 探测（用于对照 #3359/#9001）：");
            HalconDlSegTrainDiagnostics.LogDlModelDiagnostics(model, log);

            HTuple dims = model.GetDlModelParam("image_dimensions");
            if (dims == null || dims.Length < 2)
                throw new InvalidOperationException("无法读取模型 image_dimensions。");
            int targetH = dims[0].I;
            int targetW = dims[1].I;
            if (targetW <= 1 || targetH <= 1)
                throw new InvalidOperationException($"无效的 image_dimensions: {targetW}x{targetH}");
            log($"模型输入尺寸: {targetW} x {targetH}");
            int modelChannels = HalconDlSegBridge.TryGetDlModelInputChannels(model, log);

            log("[diag] 训练固定使用 real [0,1] image（compact 模型要求；image_type 参数在该模型不可用）。");

            int batchMult = HalconDlSegTrainDiagnostics.TryGetBatchSizeMultiplier(model, log);
            int samplesPerTrainStep = Math.Max(1, checked(opt.BatchSize * batchMult));
            log($"每步 TrainDlModelBatch 将提交 {samplesPerTrainStep} 个 DLSample（= batch_size {opt.BatchSize} × multiplier {batchMult}）。");
            log("DLSample 类型: image=real[0,1], segmentation_image=uint2, weight_image=real 1.0, 全部 FullDomain。");
            log("注意: compact/enhanced 模型不支持 CPU 训练（#9001），GPU 训练需要 CUDA Toolkit（cuBLAS，#7717 表示缺失）。");

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
                HalconDlSegTrainDataset.Shuffle(order, rng);
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
                            batch[j] = HalconDlSegTrainSampleBuilder.BuildSample(
                                pair.Img,
                                pair.Seg,
                                targetW,
                                targetH,
                                modelChannels,
                                dlog);
                        }

                        if (epoch == 0 && b == 0)
                        {
                            log("[diag] 首步 TrainDlModelBatch 提交前：batch 内各样本字典中的图像类型（含非首张）：");
                            for (int j = 0; j < samplesPerTrainStep; j++)
                                HalconDlSegTrainDiagnostics.DescribeDlSample(batch[j]!, $"样本[{j}]", log);
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
                        HalconDlSegTrainSampleBuilder.DisposeSamples(batch);
                    }
                }
            }

            model.WriteDlModel(outPath);
            log($"已写入模型: {outPath}");
        }
    }
}
#endif
