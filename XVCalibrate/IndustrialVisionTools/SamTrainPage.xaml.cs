using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Threading;
using IoPath = System.IO.Path;

namespace CalibOperatorCLI_Example
{
    public partial class SamTrainPage : Page
    {
        private ScrollViewer? _logScrollViewer;
        private bool _logStickToEnd = true;

        public SamTrainPage()
        {
            InitializeComponent();
            EnsureSamUiPersist();
            // ComboBox 初始选中会在 InitializeComponent 内触发 SelectionChanged，此时靠后的 TxtQuantPrefix 可能尚未赋值，需在事件内判空。
            Loaded += (_, _) => SyncQuantPrefixFromModelType();
            AppendLog("提示：SAM 导出需 PyTorch、onnx、segment-anything；详见 SAM_Tools/requirements-onnx.txt。");
        }

        private void TxtLog_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(TryAttachLogScrollViewer));
        }

        private void TryAttachLogScrollViewer()
        {
            if (_logScrollViewer != null)
                return;
            _logScrollViewer = FindScrollViewer(TxtLog);
            if (_logScrollViewer != null)
                _logScrollViewer.ScrollChanged += LogScrollViewer_ScrollChanged;
        }

        private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_logScrollViewer == null || e.ExtentHeight <= 0)
                return;
            const double slack = 6.0;
            _logStickToEnd = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - slack;
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer sv)
                    return sv;
                var nested = FindScrollViewer(child);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            string ts = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            TxtLog.AppendText($"[{ts}] {line}\r\n");
            if (_logStickToEnd)
            {
                TxtLog.CaretIndex = TxtLog.Text.Length;
                TxtLog.ScrollToEnd();
            }
        }

        private string GetPythonExecutable()
        {
            string py = TxtPython.Text.Trim();
            if (string.IsNullOrWhiteSpace(py))
                throw new InvalidOperationException("请填写 Python 可执行文件。");
            return py;
        }

        private string GetSelectedModelType()
        {
            if (CmbModelType.SelectedItem is ComboBoxItem item && item.Content != null)
            {
                string s = item.Content as string ?? Convert.ToString(item.Content, CultureInfo.InvariantCulture) ?? "";
                if (!string.IsNullOrEmpty(s))
                    return s;
            }

            return "vit_b";
        }

        private void SyncQuantPrefixFromModelType()
        {
            if (TxtQuantPrefix == null)
                return;
            TxtQuantPrefix.Text = "sam_" + GetSelectedModelType();
        }

        private void CmbModelType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SyncQuantPrefixFromModelType();
        }

        private void BtnBrowsePython_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "可执行文件|python.exe;python3.exe|所有文件|*.*",
                Title = "选择 Python 解释器",
            };
            if (dlg.ShowDialog() == true)
                TxtPython.Text = dlg.FileName;
        }

        private void BtnBrowseCheckpoint_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "SAM checkpoint|*.pth|所有文件|*.*",
                Title = "选择 SAM .pth",
            };
            if (dlg.ShowDialog() == true)
                TxtCheckpoint.Text = dlg.FileName;
        }

        private void BtnBrowseOutDir_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择 ONNX 输出目录",
                UseDescriptionForTitle = true,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            TxtOutDir.Text = dlg.SelectedPath;
        }

        private void BtnBrowseQuantInDir_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择含 encoder/decoder ONNX 的目录",
                UseDescriptionForTitle = true,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            TxtQuantInDir.Text = dlg.SelectedPath;
        }

        private void BtnBrowseMaskImages_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择图像目录（与 YOLO labels 同名配对）",
                UseDescriptionForTitle = true,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            TxtMaskImagesDir.Text = dlg.SelectedPath;
        }

        private void BtnBrowseMaskLabels_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择 YOLO-Seg 标签目录（*.txt）",
                UseDescriptionForTitle = true,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            TxtMaskLabelsDir.Text = dlg.SelectedPath;
        }

        private void BtnBrowseMaskOut_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择掩膜输出目录（将写入 PNG）",
                UseDescriptionForTitle = true,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            TxtMaskOutDir.Text = dlg.SelectedPath;
        }

        private async void BtnRunGenMasks_Click(object sender, RoutedEventArgs e)
        {
            BtnRunGenMasks.IsEnabled = false;
            try
            {
                string py = GetPythonExecutable();
                string imgDir = TxtMaskImagesDir.Text.Trim();
                string lblDir = TxtMaskLabelsDir.Text.Trim();
                string outDir = TxtMaskOutDir.Text.Trim();
                if (string.IsNullOrWhiteSpace(imgDir) || !Directory.Exists(imgDir))
                    throw new InvalidOperationException("请选择存在的图像目录。");
                if (string.IsNullOrWhiteSpace(lblDir) || !Directory.Exists(lblDir))
                    throw new InvalidOperationException("请选择存在的标签目录。");
                if (string.IsNullOrWhiteSpace(outDir))
                    throw new InvalidOperationException("请填写掩膜输出目录。");

                string script = FlowPythonRunner.ResolveScriptOrThrow(TxtMaskGenScript.Text);
                string workDir = IoPath.GetDirectoryName(script) ?? Environment.CurrentDirectory;

                AppendLog($"生成掩膜… images={imgDir}, labels={lblDir}, out={outDir}");

                var args = new List<string>
                {
                    script,
                    "--images-dir",
                    IoPath.GetFullPath(imgDir),
                    "--labels-dir",
                    IoPath.GetFullPath(lblDir),
                    "--out-masks-dir",
                    IoPath.GetFullPath(outDir),
                };
                string cf = TxtMaskClassFilter.Text.Trim();
                if (!string.IsNullOrEmpty(cf))
                    args.AddRange(new[] { "--class-filter", cf });

                int exit = await FlowPythonRunner.RunPythonStreamingAsync(py, workDir, args, Dispatcher, AppendLog).ConfigureAwait(true);

                AppendLog(exit == 0 ? "掩膜生成结束。" : $"掩膜脚本退出码: {exit}");
                if (exit != 0)
                    System.Windows.MessageBox.Show($"Python 退出码 {exit}，详见日志。", "生成掩膜", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    System.Windows.MessageBox.Show(
                        $"已写入:\n{IoPath.GetFullPath(outDir)}\n\nSAM 微调请将数据集整理为:\n<根>/images/ 与 <根>/masks/\n（可把 train 图像复制到 images，或把 masks 指到此处）",
                        "生成掩膜",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendLog("错误: " + ex.Message);
                System.Windows.MessageBox.Show(ex.Message, "生成掩膜", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnRunGenMasks.IsEnabled = true;
            }
        }

        private void BtnBrowseTrainData_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择数据集根目录（含 images、masks；YOLO 常为 dataset 根，图为 images/train）",
                UseDescriptionForTitle = true,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            TxtTrainDataRoot.Text = dlg.SelectedPath;
        }

        private void BtnBrowseTrainOutPth_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PyTorch 权重|*.pth|所有文件|*.*",
                Title = "微调完成后保存为",
                FileName = "sam_finetune_best.pth",
            };
            if (dlg.ShowDialog() == true)
                TxtTrainOutPth.Text = dlg.FileName;
        }

        private string RequireTrainDataRoot(out string imagesSubdir, out string masksSubdir)
        {
            string p = TxtTrainDataRoot.Text.Trim();
            if (string.IsNullOrWhiteSpace(p) || !Directory.Exists(p))
                throw new InvalidOperationException("请选择有效的数据集根目录。");
            imagesSubdir = TxtTrainImagesSubdir.Text.Trim();
            masksSubdir = TxtTrainMasksSubdir.Text.Trim();
            string imgDir = string.IsNullOrEmpty(imagesSubdir)
                ? IoPath.Combine(p, "images")
                : IoPath.Combine(p, "images", imagesSubdir);
            string mskDir = string.IsNullOrEmpty(masksSubdir)
                ? IoPath.Combine(p, "masks")
                : IoPath.Combine(p, "masks", masksSubdir);
            if (!Directory.Exists(imgDir))
                throw new InvalidOperationException(
                    $"图像目录不存在:\n{imgDir}\n\n若为 YOLO 的 images/train，请在「images 子目录」填写 train。");
            if (!Directory.Exists(mskDir))
                throw new InvalidOperationException(
                    $"掩膜目录不存在:\n{mskDir}\n\n请先用「生成掩膜」写入 masks，或填写「masks 子目录」。");
            return IoPath.GetFullPath(p);
        }

        private async void BtnRunTrain_Click(object sender, RoutedEventArgs e)
        {
            BtnRunTrain.IsEnabled = false;
            try
            {
                string py = GetPythonExecutable();
                string dataRoot = RequireTrainDataRoot(out string imgSub, out string mskSub);
                string ckpt = TxtCheckpoint.Text.Trim();
                if (string.IsNullOrWhiteSpace(ckpt) || !File.Exists(ckpt))
                    throw new InvalidOperationException("请在「导出 ONNX」区域选择有效的预训练 checkpoint（.pth），微调由此初始化。");

                int epochs = int.TryParse(TxtTrainEpochs.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ep) ? ep : 10;
                epochs = Math.Max(1, epochs);

                if (!double.TryParse(TxtTrainLr.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lr))
                    lr = 1e-4;
                lr = Math.Max(1e-8, lr);

                string trainScript = FlowPythonRunner.ResolveScriptOrThrow(TxtTrainScript.Text);
                string workDir = IoPath.GetDirectoryName(trainScript) ?? Environment.CurrentDirectory;
                string modelType = GetSelectedModelType();

                string outPth = TxtTrainOutPth.Text.Trim();
                string device = TxtTrainDevice.Text.Trim();

                AppendLog(
                    $"开始 SAM 微调… data-root={dataRoot}, images-subdir={(string.IsNullOrEmpty(imgSub) ? "(根)" : imgSub)}, masks-subdir={(string.IsNullOrEmpty(mskSub) ? "(根)" : mskSub)}, model-type={modelType}, epochs={epochs}, lr={lr:G}");

                var args = new List<string>
                {
                    trainScript,
                    "--data-root",
                    dataRoot,
                    "--checkpoint",
                    IoPath.GetFullPath(ckpt),
                    "--model-type",
                    modelType,
                    "--epochs",
                    epochs.ToString(CultureInfo.InvariantCulture),
                    "--lr",
                    lr.ToString(CultureInfo.InvariantCulture),
                };
                if (!string.IsNullOrEmpty(imgSub))
                    args.AddRange(new[] { "--images-subdir", imgSub });
                if (!string.IsNullOrEmpty(mskSub))
                    args.AddRange(new[] { "--masks-subdir", mskSub });
                if (!string.IsNullOrEmpty(outPth))
                    args.AddRange(new[] { "--out", IoPath.GetFullPath(outPth) });
                if (!string.IsNullOrEmpty(device))
                    args.AddRange(new[] { "--device", device });
                if (ChkTrainImageEncoder.IsChecked == true)
                    args.Add("--train-image-encoder");
                if (ChkTrainAugment.IsChecked != true)
                    args.Add("--no-augment");

                int exit = await FlowPythonRunner.RunPythonStreamingAsync(py, workDir, args, Dispatcher, AppendLog).ConfigureAwait(true);

                AppendLog(exit == 0 ? "微调进程已结束。" : $"微调退出码: {exit}");
                if (exit != 0)
                    System.Windows.MessageBox.Show($"Python 退出码 {exit}，详见日志。", "SAM 微调", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    System.Windows.MessageBox.Show(
                        string.IsNullOrEmpty(outPth)
                            ? $"默认已写入:\n{IoPath.Combine(dataRoot, "sam_finetune_best.pth")}\n\n请在上方「Checkpoint」中改为此文件再导出 ONNX。"
                            : $"已保存:\n{IoPath.GetFullPath(outPth)}\n\n请在「Checkpoint」中改为该路径再导出 ONNX。",
                        "SAM 微调",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendLog("错误: " + ex.Message);
                System.Windows.MessageBox.Show(ex.Message, "SAM 微调", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnRunTrain.IsEnabled = true;
            }
        }

        private async void BtnExportOnnx_Click(object sender, RoutedEventArgs e)
        {
            BtnExportOnnx.IsEnabled = false;
            try
            {
                string py = GetPythonExecutable();
                string ckpt = TxtCheckpoint.Text.Trim();
                if (string.IsNullOrWhiteSpace(ckpt) || !File.Exists(ckpt))
                    throw new InvalidOperationException("请选择存在的 SAM checkpoint（.pth）。");

                string outDirRaw = TxtOutDir.Text.Trim();
                if (string.IsNullOrWhiteSpace(outDirRaw))
                    throw new InvalidOperationException("请填写输出目录。");
                string outDir = IoPath.GetFullPath(SamOnnxSegmentation.ResolveModelPath(outDirRaw));

                bool noEnc = ChkNoEncoder.IsChecked == true;
                bool noDec = ChkNoDecoder.IsChecked == true;
                if (noEnc && noDec)
                    throw new InvalidOperationException("不能同时勾选「仅导出 encoder」与「仅导出 decoder」。");

                int opset = int.TryParse(TxtOpset.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var op) ? op : 17;
                opset = Math.Clamp(opset, 11, 21);

                string exportScript = FlowPythonRunner.ResolveScriptOrThrow(TxtExportScript.Text);
                string workDir = IoPath.GetDirectoryName(exportScript) ?? Environment.CurrentDirectory;
                string modelType = GetSelectedModelType();

                AppendLog($"导出 ONNX… checkpoint={ckpt}, model-type={modelType}, out-dir={outDir}, opset={opset}");

                var args = new List<string>
                {
                    exportScript,
                    "--checkpoint",
                    IoPath.GetFullPath(ckpt),
                    "--model-type",
                    modelType,
                    "--out-dir",
                    outDir,
                    "--opset",
                    opset.ToString(CultureInfo.InvariantCulture),
                };
                if (ChkReturnSingleMask.IsChecked == true)
                    args.Add("--return-single-mask");
                if (ChkGeluApproximate.IsChecked == true)
                    args.Add("--gelu-approximate");
                if (noEnc)
                    args.Add("--no-encoder");
                if (noDec)
                    args.Add("--no-decoder");

                int exit = await FlowPythonRunner.RunPythonStreamingAsync(py, workDir, args, Dispatcher, AppendLog).ConfigureAwait(true);

                AppendLog(exit == 0 ? "导出完成。" : $"导出进程退出码: {exit}");
                if (exit != 0)
                    System.Windows.MessageBox.Show($"Python 退出码 {exit}，详见日志。", "导出 ONNX", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    System.Windows.MessageBox.Show(
                        $"输出目录:\n{outDir}\n\n流程编排算子 encoderPath / decoderPath 可指向:\n{modelType} → sam_{modelType}_encoder.onnx / sam_{modelType}_decoder.onnx",
                        "导出 ONNX",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendLog("错误: " + ex.Message);
                System.Windows.MessageBox.Show(ex.Message, "导出 ONNX", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnExportOnnx.IsEnabled = true;
            }
        }

        private async void BtnQuantize_Click(object sender, RoutedEventArgs e)
        {
            BtnQuantize.IsEnabled = false;
            try
            {
                string py = GetPythonExecutable();
                string inDirRaw = TxtQuantInDir.Text.Trim();
                if (string.IsNullOrWhiteSpace(inDirRaw))
                    throw new InvalidOperationException("请填写量化输入目录（含 FP32 ONNX）。");
                string inDir = IoPath.GetFullPath(SamOnnxSegmentation.ResolveModelPath(inDirRaw));

                string prefix = TxtQuantPrefix.Text.Trim();
                if (string.IsNullOrWhiteSpace(prefix))
                    throw new InvalidOperationException("请填写文件名前缀（如 sam_vit_b）。");

                string suffix = TxtQuantSuffix.Text.Trim();
                if (string.IsNullOrWhiteSpace(suffix))
                    suffix = "_int8";

                string quantScript = FlowPythonRunner.ResolveScriptOrThrow(TxtQuantScript.Text);
                string workDir = IoPath.GetDirectoryName(quantScript) ?? Environment.CurrentDirectory;

                AppendLog($"量化… in-dir={inDir}, prefix={prefix}, suffix={suffix}");

                var args = new List<string>
                {
                    quantScript,
                    "--in-dir",
                    inDir,
                    "--prefix",
                    prefix,
                    "--suffix",
                    suffix,
                };
                if (ChkQuantMatmulOnly.IsChecked == true)
                    args.Add("--matmul-only");
                if (ChkQuantPerChannel.IsChecked == true)
                    args.Add("--per-channel");
                if (ChkQuantReduceRange.IsChecked == true)
                    args.Add("--reduce-range");

                int exit = await FlowPythonRunner.RunPythonStreamingAsync(py, workDir, args, Dispatcher, AppendLog).ConfigureAwait(true);

                AppendLog(exit == 0 ? "量化完成。" : $"量化进程退出码: {exit}");
                if (exit != 0)
                    System.Windows.MessageBox.Show($"Python 退出码 {exit}，详见日志。", "量化", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppendLog("错误: " + ex.Message);
                System.Windows.MessageBox.Show(ex.Message, "量化", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnQuantize.IsEnabled = true;
            }
        }
    }
}
