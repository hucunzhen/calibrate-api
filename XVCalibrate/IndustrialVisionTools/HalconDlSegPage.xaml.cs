using System;
using System.Globalization;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CalibOperatorPInvoke;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using IoPath = System.IO.Path;

namespace CalibOperatorCLI_Example
{
    public partial class HalconDlSegPage : Page
    {
        private readonly DispatcherTimer _uiPersistTimer;
        private bool _suppressUiPersist;
        private bool _uiPersistHooksAttached;

        public HalconDlSegPage()
        {
            InitializeComponent();
            _uiPersistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _uiPersistTimer.Tick += (_, _) =>
            {
                _uiPersistTimer.Stop();
                PersistUiFromControls();
            };

            Loaded += HalconDlSegPage_Loaded;
            Unloaded += (_, _) => PersistUiFromControls();
        }

        private void HalconDlSegPage_Loaded(object sender, RoutedEventArgs e)
        {
            ApplySettingsToUi(HalconDlSegUiSettings.Load());

#if HALCON_ENABLED
            TxtHalconInferHint.Text = "推理使用 HDlModel：按模型 image_dimensions 缩放后填入样本字典键「image」。若与您的预处理不一致，请在 HDevelop 中对照官方示例调整。";
            BtnRunInfer.IsEnabled = true;
            BtnStartTrain.IsEnabled = true;
            TxtTrainHint.Text = "训练读取导出目录下的 flat「images」「segmentation」；类别数须与预训练 .hdl 一致；需要 DL 训练授权。";
#else
            TxtHalconInferHint.Text = "当前 exe 未启用 HALCON（编译时未找到 HalconDotNet.dll）。仍可导出数据集；推理需本机安装 HALCON 并重新生成项目。";
            BtnRunInfer.IsEnabled = false;
            BtnStartTrain.IsEnabled = false;
            TxtTrainHint.Text = "当前 exe 未启用 HALCON：训练不可用，仅可导出数据集。";
#endif

            AppendLog("复用「YOLO分割」标注目录：images/train + labels/train（多边形）。");
            AppendLog($"界面配置：{HalconDlSegUiSettingsHintPath()}");

            if (_uiPersistHooksAttached)
                return;
            _uiPersistHooksAttached = true;
            AttachUiPersistHandlers();
        }

        private static string HalconDlSegUiSettingsHintPath()
        {
            return IoPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppProduct.AppDataFolderName,
                "halcon_dl_seg_ui.json");
        }

        private void ApplySettingsToUi(HalconDlSegUiSettings s)
        {
            _suppressUiPersist = true;
            try
            {
                TxtDatasetRoot.Text = s.DatasetRoot ?? "";
                TxtExportRoot.Text = s.ExportRoot ?? "";
                TxtClassNames.Text = string.IsNullOrWhiteSpace(s.ClassNames) ? "object" : s.ClassNames;
                TxtTrainPretrainedHdl.Text = s.TrainPretrainedHdl ?? "";
                TxtTrainOutputHdl.Text = s.TrainOutputHdl ?? "";
                TxtTrainEpochs.Text = string.IsNullOrWhiteSpace(s.TrainEpochs) ? "5" : s.TrainEpochs;
                TxtTrainBatch.Text = string.IsNullOrWhiteSpace(s.TrainBatch) ? "2" : s.TrainBatch;
                TxtTrainLr.Text = string.IsNullOrWhiteSpace(s.TrainLr) ? "0.001" : s.TrainLr;
                int rt = s.TrainRuntimeIndex;
                if (rt < 0 || rt >= CmbTrainRuntime.Items.Count)
                    rt = 0;
                CmbTrainRuntime.SelectedIndex = rt;
                ChkTrainImageReal01.IsChecked = s.TrainImageNormalizeReal01;
                TxtModelHdl.Text = s.ModelHdl ?? "";
                TxtTestImage.Text = s.TestImage ?? "";
            }
            finally
            {
                _suppressUiPersist = false;
            }
        }

        private void PersistUiFromControls()
        {
            if (_suppressUiPersist)
                return;
            var s = new HalconDlSegUiSettings
            {
                DatasetRoot = TxtDatasetRoot.Text ?? "",
                ExportRoot = TxtExportRoot.Text ?? "",
                ClassNames = string.IsNullOrWhiteSpace(TxtClassNames.Text) ? "object" : TxtClassNames.Text.Trim(),
                TrainPretrainedHdl = TxtTrainPretrainedHdl.Text ?? "",
                TrainOutputHdl = TxtTrainOutputHdl.Text ?? "",
                TrainEpochs = TxtTrainEpochs.Text ?? "",
                TrainBatch = TxtTrainBatch.Text ?? "",
                TrainLr = TxtTrainLr.Text ?? "",
                TrainRuntimeIndex = CmbTrainRuntime.SelectedIndex >= 0 ? CmbTrainRuntime.SelectedIndex : 0,
                TrainImageNormalizeReal01 = ChkTrainImageReal01.IsChecked == true,
                ModelHdl = TxtModelHdl.Text ?? "",
                TestImage = TxtTestImage.Text ?? "",
            };
            s.Save();
        }

        private void SchedulePersistUi()
        {
            if (_suppressUiPersist)
                return;
            _uiPersistTimer.Stop();
            _uiPersistTimer.Start();
        }

        private void AttachUiPersistHandlers()
        {
            TxtDatasetRoot.TextChanged += (_, _) => SchedulePersistUi();
            TxtExportRoot.TextChanged += (_, _) => SchedulePersistUi();
            TxtClassNames.TextChanged += (_, _) => SchedulePersistUi();
            TxtTrainPretrainedHdl.TextChanged += (_, _) => SchedulePersistUi();
            TxtTrainOutputHdl.TextChanged += (_, _) => SchedulePersistUi();
            TxtTrainEpochs.TextChanged += (_, _) => SchedulePersistUi();
            TxtTrainBatch.TextChanged += (_, _) => SchedulePersistUi();
            TxtTrainLr.TextChanged += (_, _) => SchedulePersistUi();
            CmbTrainRuntime.SelectionChanged += (_, _) => SchedulePersistUi();
            ChkTrainImageReal01.Checked += (_, _) => SchedulePersistUi();
            ChkTrainImageReal01.Unchecked += (_, _) => SchedulePersistUi();
            TxtModelHdl.TextChanged += (_, _) => SchedulePersistUi();
            TxtTestImage.TextChanged += (_, _) => SchedulePersistUi();
        }

        private void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            string ts = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            TxtLog.AppendText($"[{ts}] {line}\r\n");
            TxtLog.CaretIndex = TxtLog.Text.Length;
            TxtLog.ScrollToEnd();
        }

        private static string[]? ParseClassNamesText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
            return parts.Length > 0 ? parts : null;
        }

        private void BtnBrowseDataset_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择 YOLO-Seg 数据集根目录（含 images/train、labels/train）",
                UseDescriptionForTitle = true,
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            TxtDatasetRoot.Text = dlg.SelectedPath;
            if (string.IsNullOrWhiteSpace(TxtExportRoot.Text.Trim()))
                TxtExportRoot.Text = IoPath.Combine(dlg.SelectedPath, "halcon_dl_export");
        }

        private void BtnBrowseExport_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择 HALCON 导出目录（将创建 images、segmentation）",
                UseDescriptionForTitle = true,
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            TxtExportRoot.Text = dlg.SelectedPath;
        }

        private void BtnExportHalcon_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string root = TxtDatasetRoot.Text.Trim();
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    throw new InvalidOperationException("请先选择有效的数据集根目录。");
                string export = TxtExportRoot.Text.Trim();
                if (string.IsNullOrWhiteSpace(export))
                    export = IoPath.Combine(root, "halcon_dl_export");
                export = IoPath.GetFullPath(export);

                string[]? names = ParseClassNamesText(TxtClassNames.Text);
                var yamlNames = HalconDlDatasetExport.TryParseClassNamesFromDataYaml(IoPath.Combine(root, "data.yaml"));
                if (names == null && yamlNames != null)
                    names = yamlNames;

                var rep = HalconDlDatasetExport.ExportFromYoloDataset(root, export, names, AppendLog);
                AppendLog($"导出完成：图像 {rep.ImageCount}，有标签 {rep.LabelWritten}，缺标签填零 {rep.EmptyLabelFallback}，标签中最大类别 id {rep.MaxClassIdSeen}。");
                AppendLog($"导出根路径: {export}");
                MessageBox.Show($"已导出至:\n{export}", "HALCON 数据集", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnBrowseModel_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "HALCON DL 模型 (*.hdl)|*.hdl|所有文件 (*.*)|*.*",
                Title = "选择语义分割模型",
            };
            if (dlg.ShowDialog() == true)
                TxtModelHdl.Text = dlg.FileName;
        }

        private void BtnBrowseTestImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "图像|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|所有文件 (*.*)|*.*",
                Title = "选择测试图像",
            };
            if (dlg.ShowDialog() == true)
                TxtTestImage.Text = dlg.FileName;
        }

        private void BtnBrowseTrainPretrained_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "HALCON DL 模型 (*.hdl)|*.hdl|所有文件 (*.*)|*.*",
                Title = "选择预训练语义分割模型",
            };
            if (dlg.ShowDialog() == true)
                TxtTrainPretrainedHdl.Text = dlg.FileName;
        }

        private void BtnBrowseTrainOutput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "HALCON DL (*.hdl)|*.hdl",
                Title = "保存训练后的模型",
                FileName = "seg_finetuned.hdl",
            };
            if (dlg.ShowDialog() == true)
                TxtTrainOutputHdl.Text = dlg.FileName;
        }

        private async void BtnStartTrain_Click(object sender, RoutedEventArgs e)
        {
#if !HALCON_ENABLED
            MessageBox.Show("当前构建未启用 HALCON，无法训练。", "HALCON", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
#else
            string export = TxtExportRoot.Text.Trim();
            if (string.IsNullOrWhiteSpace(export))
            {
                MessageBox.Show("请先填写导出目录（可先执行导出）。", "训练", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtTrainEpochs.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int epochs) || epochs < 1)
            {
                MessageBox.Show("epochs 请输入 ≥1 的整数。", "训练", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtTrainBatch.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int batch) || batch < 1)
            {
                MessageBox.Show("batch 请输入 ≥1 的整数。", "训练", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(TxtTrainLr.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lr) || lr <= 0)
            {
                MessageBox.Show("lr 请输入正数。", "训练", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var opt = new HalconDlSegTrainOptions
            {
                ExportRoot = IoPath.GetFullPath(export),
                PretrainedHdlPath = TxtTrainPretrainedHdl.Text.Trim(),
                OutputHdlPath = TxtTrainOutputHdl.Text.Trim(),
                Epochs = epochs,
                BatchSize = batch,
                LearningRate = lr,
                UseGpu = CmbTrainRuntime.SelectedIndex == 0,
                NormalizeTrainingImageToReal01 = ChkTrainImageReal01.IsChecked == true,
            };

            BtnStartTrain.IsEnabled = false;
            try
            {
                AppendLog("开始 HALCON TrainDlModelBatch 训练…");
                await Task.Run(() =>
                {
                    HalconDlSegTrainRunner.Run(opt, msg => Dispatcher.Invoke(() => AppendLog(msg)));
                }).ConfigureAwait(true);
                AppendLog("训练流程结束。");
                MessageBox.Show($"已保存:\n{IoPath.GetFullPath(opt.OutputHdlPath.Trim())}", "训练完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendLog("[错误] " + ex.Message);
                MessageBox.Show(ex.Message, "训练失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnStartTrain.IsEnabled = true;
            }
#endif
        }

        private void BtnRunInfer_Click(object sender, RoutedEventArgs e)
        {
#if !HALCON_ENABLED
            MessageBox.Show("当前构建未启用 HALCON，无法推理。", "HALCON", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
#else
            try
            {
                string model = TxtModelHdl.Text.Trim();
                string img = TxtTestImage.Text.Trim();
                if (string.IsNullOrEmpty(model) || !File.Exists(model))
                    throw new InvalidOperationException("请选择有效的 .hdl 模型文件。");
                if (string.IsNullOrEmpty(img) || !File.Exists(img))
                    throw new InvalidOperationException("请选择有效的测试图像。");

                AppendLog("开始 HALCON 推理…");
                CalibImage mask = HalconDlSegBridge.RunSegmentationInference(model, img);

                var sfd = new SaveFileDialog
                {
                    Filter = "PNG (*.png)|*.png|BMP (*.bmp)|*.bmp",
                    FileName = IoPath.GetFileNameWithoutExtension(img) + "_halcon_seg.png",
                    Title = "保存分割结果（灰度：像素值为类别索引）",
                };
                if (sfd.ShowDialog() != true)
                {
                    AppendLog("已取消保存。");
                    return;
                }

                using (Bitmap bmp = mask.ToBitmap() ?? throw new InvalidOperationException("无法将掩膜转为位图。"))
                {
                    string ext = IoPath.GetExtension(sfd.FileName).ToLowerInvariant();
                    if (ext == ".bmp")
                        bmp.Save(sfd.FileName, ImageFormat.Bmp);
                    else
                        bmp.Save(sfd.FileName, ImageFormat.Png);
                }

                AppendLog($"已保存: {sfd.FileName}");
                MessageBox.Show($"已保存:\n{sfd.FileName}", "推理完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendLog("[错误] " + ex.Message);
                MessageBox.Show(ex.Message, "推理失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
#endif
        }
    }
}
