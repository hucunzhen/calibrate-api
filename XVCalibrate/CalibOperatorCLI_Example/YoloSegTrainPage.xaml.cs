using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using IoPath = System.IO.Path;

namespace CalibOperatorCLI_Example
{
    public partial class YoloSegTrainPage : Page
    {
        private static readonly HashSet<string> ImportExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".webp",
        };

        private string? _annotImagePath;
        private int _annotPixelW;
        private int _annotPixelH;
        private System.Windows.Controls.Image? _annotImageElement;
        private readonly List<System.Windows.Point> _annotDraft = new List<System.Windows.Point>();
        private readonly List<(int ClassId, List<System.Windows.Point> Points)> _annotInstances = new List<(int, List<System.Windows.Point>)>();

        private ScrollViewer? _logScrollViewer;
        private bool _logStickToEnd = true;

        public YoloSegTrainPage()
        {
            InitializeComponent();
            Loaded += (_, _) => RefreshAnnotClassCombo();
            AppendLog("提示：默认使用 YOLO11 预训练名（如 yolo11m-seg.pt）；请先 pip install -U -r YoloSeg_Tools/requirements-yolo-seg.txt。");
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

        private string RequireDatasetRoot()
        {
            string p = TxtDatasetRoot.Text.Trim();
            if (string.IsNullOrWhiteSpace(p) || !Directory.Exists(p))
                throw new InvalidOperationException("请先选择有效的数据集根目录。");
            return IoPath.GetFullPath(p);
        }

        private static string ImagesTrainDir(string root) => IoPath.Combine(root, "images", "train");

        private static string LabelsTrainDir(string root) => IoPath.Combine(root, "labels", "train");

        private void BtnBrowseDataset_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择数据集根目录（将使用其中的 images/train、labels/train）",
                UseDescriptionForTitle = true,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            TxtDatasetRoot.Text = dlg.SelectedPath;
        }

        private void BtnBrowseSource_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择原始图像所在文件夹",
                UseDescriptionForTitle = true,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            TxtSourceFolder.Text = dlg.SelectedPath;
        }

        private void BtnImportImages_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string root = RequireDatasetRoot();
                string src = TxtSourceFolder.Text.Trim();
                if (string.IsNullOrWhiteSpace(src) || !Directory.Exists(src))
                    throw new InvalidOperationException("请先选择有效的原始图像文件夹。");

                string imgTrain = ImagesTrainDir(root);
                string lblTrain = LabelsTrainDir(root);
                Directory.CreateDirectory(imgTrain);
                Directory.CreateDirectory(lblTrain);

                int n = 0;
                foreach (string f in Directory.GetFiles(src))
                {
                    string ext = IoPath.GetExtension(f);
                    if (!ImportExtensions.Contains(ext)) continue;
                    string dest = IoPath.Combine(imgTrain, IoPath.GetFileName(f));
                    File.Copy(f, dest, overwrite: true);
                    n++;
                }

                AppendLog($"已导入 {n} 张图像到 {imgTrain}");
                if (n == 0)
                    System.Windows.MessageBox.Show("未复制任何文件，请确认扩展名为 bmp/png/jpg 等。", "导入", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnGenYaml_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string root = RequireDatasetRoot();
                string[] names = ParseClassNames(TxtClassNames.Text);
                if (names.Length == 0)
                    throw new InvalidOperationException("至少填写一个类别名（或用默认 object）。");

                string yamlPath = IoPath.Combine(root, "data.yaml");
                WriteFullDataYaml(root, names);
                AppendLog($"已写入 {yamlPath}（nc={names.Length}，含 augment）");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "生成 data.yaml", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnWriteAugmentToYaml_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string root = RequireDatasetRoot();
                string yamlPath = IoPath.Combine(root, "data.yaml");
                if (!File.Exists(yamlPath))
                    throw new InvalidOperationException("请先「生成 data.yaml」或手动创建 data.yaml。");

                string text = File.ReadAllText(yamlPath, Encoding.UTF8);
                text = StripAugmentSection(text).TrimEnd();
                var sb = new StringBuilder(text);
                if (sb.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal))
                    sb.AppendLine();
                AppendAugmentYaml(sb);
                File.WriteAllText(yamlPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                AppendLog($"已更新 augment 段: {yamlPath}");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "更新 augment", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAugPresetLight_Click(object sender, RoutedEventArgs e)
        {
            TxtAugDegrees.Text = "5";
            TxtAugTranslate.Text = "0.05";
            TxtAugScale.Text = "0.3";
            TxtAugShear.Text = "0";
            TxtAugPerspective.Text = "0";
            TxtAugFliplr.Text = "0.5";
            TxtAugFlipud.Text = "0";
            TxtAugMosaic.Text = "1";
            TxtAugMixup.Text = "0";
            TxtAugCopyPaste.Text = "0";
            TxtAugHsvH.Text = "0.015";
            TxtAugHsvS.Text = "0.7";
            TxtAugHsvV.Text = "0.4";
        }

        private void BtnAugPresetMedium_Click(object sender, RoutedEventArgs e)
        {
            TxtAugDegrees.Text = "12";
            TxtAugTranslate.Text = "0.12";
            TxtAugScale.Text = "0.55";
            TxtAugShear.Text = "2";
            TxtAugPerspective.Text = "0.0003";
            TxtAugFliplr.Text = "0.5";
            TxtAugFlipud.Text = "0";
            TxtAugMosaic.Text = "1";
            TxtAugMixup.Text = "0.08";
            TxtAugCopyPaste.Text = "0.15";
            TxtAugHsvH.Text = "0.015";
            TxtAugHsvS.Text = "0.75";
            TxtAugHsvV.Text = "0.45";
        }

        private void BtnAugPresetAggressive_Click(object sender, RoutedEventArgs e)
        {
            TxtAugDegrees.Text = "20";
            TxtAugTranslate.Text = "0.2";
            TxtAugScale.Text = "0.8";
            TxtAugShear.Text = "5";
            TxtAugPerspective.Text = "0.0008";
            TxtAugFliplr.Text = "0.5";
            TxtAugFlipud.Text = "0";
            TxtAugMosaic.Text = "1";
            TxtAugMixup.Text = "0.15";
            TxtAugCopyPaste.Text = "0.3";
            TxtAugHsvH.Text = "0.02";
            TxtAugHsvS.Text = "0.9";
            TxtAugHsvV.Text = "0.55";
        }

        private void BtnAugPresetFlipHsv_Click(object sender, RoutedEventArgs e)
        {
            TxtAugDegrees.Text = "0";
            TxtAugTranslate.Text = "0";
            TxtAugScale.Text = "0";
            TxtAugShear.Text = "0";
            TxtAugPerspective.Text = "0";
            TxtAugFliplr.Text = "0.5";
            TxtAugFlipud.Text = "0";
            TxtAugMosaic.Text = "1";
            TxtAugMixup.Text = "0";
            TxtAugCopyPaste.Text = "0";
            TxtAugHsvH.Text = "0.015";
            TxtAugHsvS.Text = "0.7";
            TxtAugHsvV.Text = "0.4";
        }

        private static string[] ParseClassNames(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new[] { "object" };
            return text.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
        }

        private void WriteFullDataYaml(string datasetRootAbs, IReadOnlyList<string> classNames)
        {
            string rootFwd = datasetRootAbs.Replace('\\', '/');
            var sb = new StringBuilder();
            sb.AppendLine($"path: {rootFwd}");
            sb.AppendLine("train: images/train");
            sb.AppendLine("val: images/train");
            sb.AppendLine($"nc: {classNames.Count}");
            sb.AppendLine("names:");
            for (int i = 0; i < classNames.Count; i++)
                sb.AppendLine($"  {i}: {classNames[i]}");
            AppendAugmentYaml(sb);

            string yamlPath = IoPath.Combine(datasetRootAbs, "data.yaml");
            File.WriteAllText(yamlPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static string StripAugmentSection(string yaml)
        {
            string normalized = yaml.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');
            var output = new List<string>();
            bool skip = false;
            foreach (string raw in lines)
            {
                if (!skip)
                {
                    if (raw.TrimStart().StartsWith("augment:", StringComparison.OrdinalIgnoreCase))
                    {
                        skip = true;
                        continue;
                    }

                    output.Add(raw);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;
                    char c = raw[0];
                    if (c != ' ' && c != '\t')
                    {
                        skip = false;
                        output.Add(raw);
                    }
                }
            }

            return string.Join(Environment.NewLine, output);
        }

        private void AppendAugmentYaml(StringBuilder sb)
        {
            sb.AppendLine("augment:");
            sb.AppendLine($"  degrees: {YamlScalar(AugParseDouble(TxtAugDegrees, 0))}");
            sb.AppendLine($"  translate: {YamlScalar(AugParseDouble(TxtAugTranslate, 0))}");
            sb.AppendLine($"  scale: {YamlScalar(AugParseDouble(TxtAugScale, 0.5))}");
            sb.AppendLine($"  shear: {YamlScalar(AugParseDouble(TxtAugShear, 0))}");
            sb.AppendLine($"  perspective: {YamlScalar(AugParseDouble(TxtAugPerspective, 0))}");
            sb.AppendLine($"  fliplr: {YamlScalar(AugParseDouble(TxtAugFliplr, 0.5))}");
            sb.AppendLine($"  flipud: {YamlScalar(AugParseDouble(TxtAugFlipud, 0))}");
            sb.AppendLine($"  mosaic: {YamlScalar(AugParseDouble(TxtAugMosaic, 1))}");
            sb.AppendLine($"  mixup: {YamlScalar(AugParseDouble(TxtAugMixup, 0))}");
            sb.AppendLine($"  copy_paste: {YamlScalar(AugParseDouble(TxtAugCopyPaste, 0))}");
            sb.AppendLine($"  hsv_h: {YamlScalar(AugParseDouble(TxtAugHsvH, 0.015))}");
            sb.AppendLine($"  hsv_s: {YamlScalar(AugParseDouble(TxtAugHsvS, 0.7))}");
            sb.AppendLine($"  hsv_v: {YamlScalar(AugParseDouble(TxtAugHsvV, 0.4))}");
        }

        private static double AugParseDouble(System.Windows.Controls.TextBox tb, double fallback)
        {
            string t = tb.Text.Trim();
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        private static string YamlScalar(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                return "0";
            if (Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15)
                return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            return v.ToString("G9", CultureInfo.InvariantCulture);
        }

        private string ResolveScriptOrThrow(string relativeOrAbsolute)
        {
            string t = (relativeOrAbsolute ?? "").Trim();
            if (string.IsNullOrEmpty(t))
                throw new InvalidOperationException("脚本路径为空。");
            return SamOnnxSegmentation.ResolveModelPath(t);
        }

        /// <summary>
        /// 运行 Python：异步事件读取 stdout/stderr，避免「先 Read stdout 堵死 stderr 管道」的经典死锁；
        /// <c>-u</c> 关闭缓冲便于首次下载权重时日志实时刷新。
        /// </summary>
        private async Task<int> RunPythonStreamingAsync(
            string pythonExecutable,
            string workingDirectory,
            IReadOnlyList<string> argsAfterScript)
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Environment.CurrentDirectory
                    : workingDirectory,
            };
            try
            {
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
            }
            catch
            {
                // 个别环境下编码不可用则退回默认
            }

            psi.ArgumentList.Add("-u");
            foreach (string a in argsAfterScript)
                psi.ArgumentList.Add(a);

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!p.Start())
                throw new InvalidOperationException($"无法启动进程: {pythonExecutable}");

            void OnLine(string? line)
            {
                if (string.IsNullOrEmpty(line)) return;
                _ = Dispatcher.InvokeAsync(() => AppendLog(line));
            }

            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            await p.WaitForExitAsync().ConfigureAwait(false);
            // 等待异步行回调排空，避免末尾几行丢失
            await Task.Delay(200).ConfigureAwait(false);
            return p.ExitCode;
        }

        // ───────── 单张画布标注（YOLO-Seg 多边形）─────────

        private void BtnAnnotRefreshClasses_Click(object sender, RoutedEventArgs e) => RefreshAnnotClassCombo();

        private void RefreshAnnotClassCombo()
        {
            string[] names = ParseClassNames(TxtClassNames.Text);
            int sel = CmbAnnotClass.SelectedIndex;
            CmbAnnotClass.Items.Clear();
            for (int i = 0; i < names.Length; i++)
                CmbAnnotClass.Items.Add($"{i} — {names[i]}");
            if (CmbAnnotClass.Items.Count > 0)
                CmbAnnotClass.SelectedIndex = sel >= 0 && sel < CmbAnnotClass.Items.Count ? sel : 0;
        }

        private int GetAnnotSelectedClassId()
        {
            int i = CmbAnnotClass.SelectedIndex;
            return i >= 0 ? i : 0;
        }

        private static double AnnotStrokeThickness(int pixelW) =>
            Math.Max(2.0, Math.Min(8.0, pixelW / 512.0));

        private static SolidColorBrush AnnotSolidBrushForClass(int cls)
        {
            int hue = (40 + cls * 53) % 255;
            int g = (hue + 120) % 255;
            int b = (hue + 80) % 255;
            return new SolidColorBrush(Color.FromRgb((byte)hue, (byte)g, (byte)b));
        }

        private void AnnotZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (AnnotLayoutScale == null || TxtAnnotZoomPct == null) return;
            double v = e.NewValue;
            if (double.IsNaN(v) || v < 0.05) return;
            AnnotLayoutScale.ScaleX = AnnotLayoutScale.ScaleY = v;
            TxtAnnotZoomPct.Text = $"{v * 100:0}%";
        }

        private void AnnotScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control)
                return;
            e.Handled = true;
            double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            double z = Math.Round(AnnotZoomSlider.Value * factor, 3);
            z = Math.Clamp(z, AnnotZoomSlider.Minimum, AnnotZoomSlider.Maximum);
            AnnotZoomSlider.Value = z;
        }

        private void BtnAnnotZoomIn_Click(object sender, RoutedEventArgs e)
        {
            double z = Math.Min(AnnotZoomSlider.Maximum, Math.Round(AnnotZoomSlider.Value * 1.2, 3));
            AnnotZoomSlider.Value = z;
        }

        private void BtnAnnotZoomOut_Click(object sender, RoutedEventArgs e)
        {
            double z = Math.Max(AnnotZoomSlider.Minimum, Math.Round(AnnotZoomSlider.Value / 1.2, 3));
            AnnotZoomSlider.Value = z;
        }

        private void BtnAnnotZoomReset_Click(object sender, RoutedEventArgs e)
        {
            AnnotZoomSlider.Value = 1.0;
            AnnotScrollViewer.ScrollToHorizontalOffset(0);
            AnnotScrollViewer.ScrollToVerticalOffset(0);
        }

        private void BtnAnnotLoadImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图像|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.webp|所有文件|*.*",
                Title = "选择要标注的图像",
            };
            if (dlg.ShowDialog() != true) return;

            string path = dlg.FileName;
            string ext = IoPath.GetExtension(path);
            if (!ImportExtensions.Contains(ext))
            {
                System.Windows.MessageBox.Show("不支持的扩展名。", "打开图像", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                LoadAnnotImage(path);
                AppendLog($"已加载标注图像: {path}");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "打开图像失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadAnnotImage(string path)
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            _annotImagePath = IoPath.GetFullPath(path);
            _annotPixelW = bmp.PixelWidth;
            _annotPixelH = bmp.PixelHeight;

            AnnotDrawCanvas.Children.Clear();
            _annotImageElement = new System.Windows.Controls.Image
            {
                Source = bmp,
                Width = _annotPixelW,
                Height = _annotPixelH,
                IsHitTestVisible = false,
            };
            AnnotDrawCanvas.Children.Add(_annotImageElement);
            AnnotDrawCanvas.Width = _annotPixelW;
            AnnotDrawCanvas.Height = _annotPixelH;

            _annotDraft.Clear();
            _annotInstances.Clear();
            TryLoadExistingLabelForCurrentImage();
            RefreshAnnotOverlay();

            AnnotZoomSlider.Value = 1.0;
            AnnotScrollViewer.ScrollToHorizontalOffset(0);
            AnnotScrollViewer.ScrollToVerticalOffset(0);

            TxtAnnotImageInfo.Text = $"{IoPath.GetFileName(path)}  （{_annotPixelW}×{_annotPixelH}）";
        }

        private void TryLoadExistingLabelForCurrentImage()
        {
            if (string.IsNullOrEmpty(_annotImagePath)) return;
            string root = TxtDatasetRoot.Text.Trim();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

            string stem = IoPath.GetFileNameWithoutExtension(_annotImagePath);
            string lblPath = IoPath.Combine(LabelsTrainDir(root), stem + ".txt");
            if (!File.Exists(lblPath)) return;

            foreach (string raw in File.ReadAllLines(lblPath))
            {
                string line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 7) continue;
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cls))
                    continue;
                var pts = new List<System.Windows.Point>();
                for (int i = 1; i + 1 < parts.Length; i += 2)
                {
                    if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double nx)) break;
                    if (!double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ny)) break;
                    double px = nx * _annotPixelW;
                    double py = ny * _annotPixelH;
                    pts.Add(new System.Windows.Point(px, py));
                }

                if (pts.Count >= 3)
                    _annotInstances.Add((cls, pts));
            }
        }

        private System.Windows.Point ClipToAnnotImage(System.Windows.Point p)
        {
            double x = Math.Max(0, Math.Min(_annotPixelW, p.X));
            double y = Math.Max(0, Math.Min(_annotPixelH, p.Y));
            return new System.Windows.Point(x, y);
        }

        private void RefreshAnnotOverlay()
        {
            if (_annotImageElement == null) return;

            while (AnnotDrawCanvas.Children.Count > 1)
                AnnotDrawCanvas.Children.RemoveAt(AnnotDrawCanvas.Children.Count - 1);

            double stroke = AnnotStrokeThickness(_annotPixelW);

            for (int i = 0; i < _annotInstances.Count; i++)
            {
                var pts = _annotInstances[i].Points;
                int cls = _annotInstances[i].ClassId;
                SolidColorBrush brush = AnnotSolidBrushForClass(cls);
                Color c = brush.Color;
                var poly = new Polygon
                {
                    Stroke = brush,
                    StrokeThickness = stroke,
                    Fill = new SolidColorBrush(Color.FromArgb(55, c.R, c.G, c.B)),
                    Points = new PointCollection(pts),
                };
                AnnotDrawCanvas.Children.Add(poly);
            }

            if (_annotDraft.Count >= 2)
            {
                var pl = new Polyline
                {
                    Stroke = Brushes.Gold,
                    StrokeThickness = stroke,
                    Points = new PointCollection(_annotDraft),
                };
                AnnotDrawCanvas.Children.Add(pl);
            }

            foreach (var p in _annotDraft)
            {
                var ell = new Ellipse
                {
                    Width = stroke * 3,
                    Height = stroke * 3,
                    Fill = Brushes.Gold,
                    Stroke = Brushes.OrangeRed,
                    StrokeThickness = 1,
                };
                Canvas.SetLeft(ell, p.X - ell.Width / 2);
                Canvas.SetTop(ell, p.Y - ell.Height / 2);
                AnnotDrawCanvas.Children.Add(ell);
            }
        }

        private void AnnotDrawCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_annotImageElement == null || _annotPixelW <= 0) return;
            var p = ClipToAnnotImage(e.GetPosition(AnnotDrawCanvas));
            _annotDraft.Add(p);
            RefreshAnnotOverlay();
        }

        private void AnnotDrawCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_annotDraft.Count > 0)
            {
                _annotDraft.RemoveAt(_annotDraft.Count - 1);
                RefreshAnnotOverlay();
                e.Handled = true;
            }
        }

        private void BtnAnnotClosePolygon_Click(object sender, RoutedEventArgs e)
        {
            if (_annotDraft.Count < 3)
            {
                System.Windows.MessageBox.Show("多边形至少需要 3 个顶点。", "闭合", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int cls = GetAnnotSelectedClassId();
            var copy = _annotDraft.Select(pt => new System.Windows.Point(pt.X, pt.Y)).ToList();
            _annotInstances.Add((cls, copy));
            _annotDraft.Clear();
            RefreshAnnotOverlay();
        }

        private void BtnAnnotRemoveLastInstance_Click(object sender, RoutedEventArgs e)
        {
            if (_annotInstances.Count == 0) return;
            _annotInstances.RemoveAt(_annotInstances.Count - 1);
            RefreshAnnotOverlay();
        }

        private void BtnAnnotClearAllInstances_Click(object sender, RoutedEventArgs e)
        {
            if (_annotInstances.Count == 0 && _annotDraft.Count == 0) return;
            if (System.Windows.MessageBox.Show("清空当前图上全部实例与未完成顶点？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _annotInstances.Clear();
            _annotDraft.Clear();
            RefreshAnnotOverlay();
        }

        private void BtnAnnotSaveLabel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_annotImagePath) || !File.Exists(_annotImagePath))
                    throw new InvalidOperationException("请先打开图像。");
                if (_annotDraft.Count > 0)
                    throw new InvalidOperationException("尚有未完成的多边形，请先「闭合当前多边形」或右键撤销顶点。");

                string root = RequireDatasetRoot();
                string lblDir = LabelsTrainDir(root);
                Directory.CreateDirectory(lblDir);
                string stem = IoPath.GetFileNameWithoutExtension(_annotImagePath);
                string lblPath = IoPath.Combine(lblDir, stem + ".txt");

                var sb = new StringBuilder();
                foreach (var inst in _annotInstances)
                {
                    var parts = new List<string> { inst.ClassId.ToString(CultureInfo.InvariantCulture) };
                    foreach (var pt in inst.Points)
                    {
                        double nx = Math.Max(0, Math.Min(1.0, pt.X / _annotPixelW));
                        double ny = Math.Max(0, Math.Min(1.0, pt.Y / _annotPixelH));
                        parts.Add(nx.ToString("F6", CultureInfo.InvariantCulture));
                        parts.Add(ny.ToString("F6", CultureInfo.InvariantCulture));
                    }

                    sb.AppendLine(string.Join(" ", parts));
                }

                File.WriteAllText(lblPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                AppendLog($"已保存标签: {lblPath}（{_annotInstances.Count} 个实例）");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "保存标签", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAnnotCopyImageToTrain_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_annotImagePath) || !File.Exists(_annotImagePath))
                    throw new InvalidOperationException("请先打开图像。");

                string root = RequireDatasetRoot();
                string imgTrain = ImagesTrainDir(root);
                Directory.CreateDirectory(imgTrain);
                string dest = IoPath.Combine(imgTrain, IoPath.GetFileName(_annotImagePath));
                File.Copy(_annotImagePath, dest, overwrite: true);
                AppendLog($"已复制图像到: {dest}");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "复制图像", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnAutoLabel_Click(object sender, RoutedEventArgs e)
        {
            BtnAutoLabel.IsEnabled = false;
            try
            {
                string root = RequireDatasetRoot();
                string imgTrain = ImagesTrainDir(root);
                string lblTrain = LabelsTrainDir(root);
                if (!Directory.Exists(imgTrain))
                    throw new InvalidOperationException($"不存在 {imgTrain}，请先导入图像。");

                string py = TxtPython.Text.Trim();
                if (string.IsNullOrWhiteSpace(py))
                    throw new InvalidOperationException("请填写 Python 可执行文件（如 python）。");

                string scriptAbs = ResolveScriptOrThrow(TxtAutoLabelScript.Text);
                string weights = TxtWeights.Text.Trim();
                if (string.IsNullOrWhiteSpace(weights))
                    weights = "yolo11m-seg.pt";

                if (!double.TryParse(TxtConf.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double conf))
                    conf = 0.25;
                if (!int.TryParse(TxtSingleClass.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int singleClass))
                    singleClass = 0;

                string device = TxtDevice.Text.Trim();
                string workDir = IoPath.GetDirectoryName(scriptAbs) ?? root;

                AppendLog("开始自动标注…（首次下载权重可能较慢，日志会实时输出；若长时间无输出请检查网络与防火墙）");

                var argsList = new List<string>
                {
                    scriptAbs,
                    "--images-dir",
                    imgTrain,
                    "--labels-dir",
                    lblTrain,
                    "--weights",
                    weights,
                    "--conf",
                    conf.ToString(CultureInfo.InvariantCulture),
                    "--single-class",
                    singleClass.ToString(CultureInfo.InvariantCulture),
                };
                if (!string.IsNullOrEmpty(device))
                {
                    argsList.Add("--device");
                    argsList.Add(device);
                }

                int exit = await RunPythonStreamingAsync(py, workDir, argsList).ConfigureAwait(true);

                AppendLog(exit == 0 ? "自动标注完成。" : $"自动标注进程退出码: {exit}");
                if (exit != 0)
                    System.Windows.MessageBox.Show($"Python 退出码 {exit}，详见日志。", "自动标注", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppendLog("错误: " + ex.Message);
                System.Windows.MessageBox.Show(ex.Message, "自动标注", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnAutoLabel.IsEnabled = true;
            }
        }

        private async void BtnTrain_Click(object sender, RoutedEventArgs e)
        {
            BtnTrain.IsEnabled = false;
            try
            {
                string root = RequireDatasetRoot();
                string yaml = IoPath.Combine(root, "data.yaml");
                if (!File.Exists(yaml))
                    throw new InvalidOperationException($"未找到 {yaml}，请先生成 data.yaml。");

                string py = TxtPython.Text.Trim();
                if (string.IsNullOrWhiteSpace(py))
                    throw new InvalidOperationException("请填写 Python 可执行文件。");

                string trainScript = ResolveScriptOrThrow(TxtTrainScript.Text);
                string weights = TxtWeights.Text.Trim();
                if (string.IsNullOrWhiteSpace(weights))
                    weights = "yolo11m-seg.pt";

                int epochs = int.TryParse(TxtEpochs.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ep) ? ep : 100;
                int imgsz = int.TryParse(TxtImgSz.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var iz) ? iz : 640;
                int batch = int.TryParse(TxtBatch.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bt) ? bt : 4;
                int patience = int.TryParse(TxtTrainPatience.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pt)
                    ? pt
                    : 0;
                epochs = Math.Max(1, epochs);
                imgsz = Math.Max(32, imgsz);
                batch = Math.Max(1, batch);
                patience = Math.Max(0, patience);

                string device = TxtDevice.Text.Trim();

                AppendLog($"开始训练… epochs={epochs}, imgsz={imgsz}, batch={batch}, patience={patience}");

                var argsList = new List<string>
                {
                    trainScript,
                    "--data",
                    yaml,
                    "--weights",
                    weights,
                    "--epochs",
                    epochs.ToString(CultureInfo.InvariantCulture),
                    "--imgsz",
                    imgsz.ToString(CultureInfo.InvariantCulture),
                    "--batch",
                    batch.ToString(CultureInfo.InvariantCulture),
                    "--patience",
                    patience.ToString(CultureInfo.InvariantCulture),
                };
                if (!string.IsNullOrEmpty(device))
                {
                    argsList.Add("--device");
                    argsList.Add(device);
                }

                int exit = await RunPythonStreamingAsync(py, root, argsList).ConfigureAwait(true);

                AppendLog(exit == 0 ? "训练进程已结束（详见 Ultralytics 输出与 runs 目录）。" : $"训练进程退出码: {exit}");
                if (exit != 0)
                    System.Windows.MessageBox.Show($"Python 退出码 {exit}，详见日志。", "训练", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppendLog("错误: " + ex.Message);
                System.Windows.MessageBox.Show(ex.Message, "训练", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnTrain.IsEnabled = true;
            }
        }
    }
}
