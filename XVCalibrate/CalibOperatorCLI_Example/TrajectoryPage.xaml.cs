using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    public partial class TrajectoryPage : Page
    {
        private CalibImage? _currentImage;
        private TrajectoryStepDetector? _trajDetector;
        private int _trajStep = 0;

        // 缩放相关
        private double _zoomLevel = 1.0;
        private const double ZOOM_STEP = 0.1;
        private const double MIN_ZOOM = 0.1;
        private const double MAX_ZOOM = 5.0;
        private ScaleTransform _imageScaleTransform;

        // 网格缩放相关
        private double _gridZoomLevel = 1.0;
        private ScaleTransform _gridScaleTransform;

        // 右键拖动相关
        private bool _isDragging = false;
        private System.Windows.Point _lastMousePos;
        private System.Windows.Input.MouseButton _dragButton;

        public TrajectoryPage()
        {
            InitializeComponent();
            _imageScaleTransform = ImageScaleTransform;
            _gridScaleTransform = StepGridScaleTransform;
            Log("Trajectory Detection Page Loaded");
            UpdateStepButtonStates();
        }

        private void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string logLine = $"[{timestamp}] {message}";
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(logLine + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            });
            UpdateStatus(message);
        }

        private void UpdateStatus(string message)
        {
            Dispatcher.Invoke(() => StatusText.Text = message);
        }

        private void UpdateStepButtonStates()
        {
            BtnLoadTrajImg.IsEnabled = true;
            bool ready = _trajDetector != null && _currentImage != null;
            BtnStep1.IsEnabled = ready && _trajStep >= 0;
            BtnStep2.IsEnabled = ready && _trajStep >= 1;
            BtnStep3.IsEnabled = ready && _trajStep >= 2;
            BtnStep4.IsEnabled = ready && _trajStep >= 3;
            BtnStep5.IsEnabled = ready && _trajStep >= 4;
            BtnExpandEdge.IsEnabled = ready && _trajStep >= 5;
            BtnStep6.IsEnabled = ready && _trajStep >= 5;
            BtnStep7.IsEnabled = ready && _trajStep >= 6;
            BtnStep8.IsEnabled = ready && _trajStep >= 7;
            BtnStep9.IsEnabled = ready && _trajStep >= 8;
            BtnStep10.IsEnabled = ready && _trajStep >= 9;
            BtnAutoDetect.IsEnabled = true;
            UpdateStepParameterVisibility();
        }

        private void UpdateStepParameterVisibility()
        {
            SpStep2Params.Visibility = Visibility.Collapsed;
            SpStep2bParams.Visibility = Visibility.Collapsed;
            SpStep3Params.Visibility = Visibility.Collapsed;
            SpStep4Params.Visibility = Visibility.Collapsed;
            SpStep5Params.Visibility = Visibility.Collapsed;
            SpStep7Params.Visibility = Visibility.Collapsed;
            SpStep75Params.Visibility = Visibility.Collapsed;

            switch (_trajStep)
            {
                case 1: SpStep2Params.Visibility = Visibility.Visible; SpStep2bParams.Visibility = Visibility.Visible; break;
                case 2: SpStep3Params.Visibility = Visibility.Visible; break;
                case 3: SpStep4Params.Visibility = Visibility.Visible; break;
                case 4: SpStep5Params.Visibility = Visibility.Visible; break;
                case 5: SpStep7Params.Visibility = Visibility.Visible; break;
                case 6: SpStep7Params.Visibility = Visibility.Visible; break;
                case 7: SpStep7Params.Visibility = Visibility.Visible; SpStep75Params.Visibility = Visibility.Visible; break;
            }
        }

        /// <summary>
        /// 点击步骤按钮时立即显示对应参数面板，不等到执行后
        /// </summary>
        private void PrepareStep(int step)
        {
            _trajStep = step;
            UpdateStepParameterVisibility();
        }

        private void DisplayImage(CalibImage img)
        {
            try
            {
                using var bmp = img.ToBitmap();
                if (bmp != null)
                {
                    var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    Dispatcher.Invoke(() => ImageDisplay.Source = bitmap);
                }
            }
            catch (Exception ex) { Log($"[ERROR] DisplayImage: {ex.Message}"); }
        }

        private void ResetZoom()
        {
            _zoomLevel = 1.0;
            _imageScaleTransform.ScaleX = 1.0;
            _imageScaleTransform.ScaleY = 1.0;
            ImageScrollViewer.ScrollToHorizontalOffset(0);
            ImageScrollViewer.ScrollToVerticalOffset(0);
        }

        // ==================== 空洞检测（新方案） ====================
        private void BtnHollowDetect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "BMP Files (*.bmp)|*.bmp|All Files (*.*)|*.*",
                    Title = "Select Image for Hollow Detection"
                };
                if (dlg.ShowDialog() != true) return;

                _currentImage?.Dispose();
                _currentImage = CalibAPI.LoadImage(dlg.FileName);
                Log($"[HOLLOW] Loaded: {_currentImage.Width}x{_currentImage.Height}");
                SwitchToSingleImageMode();
                DisplayImage(_currentImage);
                ResetZoom();

                // 参数解析
                int blurKsize = 7, morphKernelSize = 5;
                int.TryParse(TxtHollowBlur.Text, out blurKsize);
                int.TryParse(TxtHollowMorph.Text, out morphKernelSize);
                if (blurKsize % 2 == 0) blurKsize++;
                if (morphKernelSize % 2 == 0) morphKernelSize++;
                bool useContourMode = ChkContourMode.IsChecked == true;

                Log($"[HOLLOW] blur={blurKsize}, morph={morphKernelSize}, contourMode={useContourMode}");

                var result = CalibAPI.DetectHollowTrajectory(_currentImage,
                    blurKsize, morphKernelSize,
                    targetHollows: 16, bandWidth: 8,
                    useContourMode: useContourMode,
                    outerExpandPixels: 25);

                if (result.Success && result.Count > 0)
                {
                    Log($"[HOLLOW] SUCCESS! Found {result.Count} points");

                    // 先获取原图的 BitmapSource（在画轨迹之前，用于网格中透明叠加显示）
                    BitmapSource originalBmpSrc = null;
                    try
                    {
                        using var origBmp = _currentImage.ToBitmap();
                        if (origBmp != null)
                        {
                            originalBmpSrc = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                origBmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                            originalBmpSrc.Freeze();
                        }
                    }
                    catch { }

                    // 在 _currentImage 上绘制不透明彩色轨迹（用于保存 BMP）
                    CalibAPI.DrawTrajectoryColored(_currentImage, result.Points, result.BarIds);

                    // 显示步骤图网格（包含原图+透明轨迹叠加结果）
                    ShowHollowStepGrid(result, originalBmpSrc, result.Points, result.BarIds);

                    string savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trajectory_hollow.bmp");
                    CalibAPI.SaveImage(savePath, _currentImage);
                    Log($"[HOLLOW] Saved: {savePath}");
                }
                else
                {
                    Log($"[HOLLOW] FAIL: {result.ErrorMessage ?? "No trajectory"}");
                    // 即使失败也显示中间步骤图
                    if (result.StepImages.Count > 0)
                    {
                        ShowHollowStepGrid(result, null, null, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Hollow detect: {ex.Message}");
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 显示空洞检测的步骤图网格
        /// </summary>
        private void ShowHollowStepGrid(TrajectoryResult result, BitmapSource originalImage, Point2D[] trajPoints, int[] barIds)
        {
            // 切换到网格模式
            ImageScrollViewer.Visibility = Visibility.Collapsed;
            StepGridScrollViewer.Visibility = Visibility.Visible;

            // 重置网格缩放
            _gridZoomLevel = 1.0;
            _gridScaleTransform.ScaleX = 1.0;
            _gridScaleTransform.ScaleY = 1.0;

            StepImageGrid.Children.Clear();

            // 步骤图（灰度图）→ WPF BitmapSource 转换辅助
            BitmapSource GrayToBitmapSource(CalibImage calibImg)
            {
                using var bmp = calibImg.ToBitmap();
                if (bmp == null) return null;
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }

            // 1~5: 步骤图
            for (int i = 0; i < result.StepImages.Count && i < result.StepLabels.Count; i++)
            {
                var bmpSrc = GrayToBitmapSource(result.StepImages[i]);
                if (bmpSrc == null) continue;

                var stepImg = new Image
                {
                    Source = bmpSrc,
                    Stretch = Stretch.Uniform,
                    MaxHeight = 350
                };
                RenderOptions.SetBitmapScalingMode(stepImg, BitmapScalingMode.HighQuality);

                var border = new Border
                {
                    BorderBrush = new SolidColorBrush(Colors.Gray),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(2),
                    Background = new SolidColorBrush(Colors.Black),
                    Child = stepImg
                };

                // 标签
                var label = new TextBlock
                {
                    Text = result.StepLabels[i],
                    Foreground = new SolidColorBrush(Colors.White),
                    Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                    Padding = new Thickness(4, 2, 4, 2),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };

                var panel = new Grid();
                panel.Children.Add(border);
                panel.Children.Add(label);

                StepImageGrid.Children.Add(panel);
            }

            // 6: 最终轨迹结果图（原图 + 透明轨迹叠加）
            if (originalImage != null && trajPoints != null && trajPoints.Length > 0)
            {
                int w = originalImage.PixelWidth;
                int h = originalImage.PixelHeight;

                // 创建带 alpha 通道的透明轨迹覆盖层
                var overlay = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
                byte[] overlayPixels = new byte[w * h * 4];
                overlay.WritePixels(new Int32Rect(0, 0, w, h), overlayPixels, w * 4, 0);

                // 获取 BAR_COLORS（BGR → RGB）
                System.Drawing.Color[] barColors = CalibAPI.GetBarColors();

                // 在透明覆盖层上画轨迹点（半透明彩色圆点，radius=2）
                const int radius = 2;
                byte[] drawBuf = new byte[w * h * 4];
                for (int i = 0; i < trajPoints.Length; i++)
                {
                    int barIdx = barIds != null && i < barIds.Length ? barIds[i] % 16 : 0;
                    System.Drawing.Color c = barColors[barIdx];
                    int cx = (int)trajPoints[i].X;
                    int cy = (int)trajPoints[i].Y;

                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        for (int dy = -radius; dy <= radius; dy++)
                        {
                            if (dx * dx + dy * dy > radius * radius) continue;
                            int x = cx + dx;
                            int y = cy + dy;
                            if (x < 0 || x >= w || y < 0 || y >= h) continue;

                            int idx = (y * w + x) * 4;
                            drawBuf[idx]     = c.B;          // B
                            drawBuf[idx + 1] = c.G;          // G
                            drawBuf[idx + 2] = c.R;          // R
                            drawBuf[idx + 3] = 200;          // A (半透明)
                        }
                    }
                }
                overlay.WritePixels(new Int32Rect(0, 0, w, h), drawBuf, w * 4, 0);
                overlay.Freeze();

                // 用 Grid 叠加显示：底层原图 + 上层透明轨迹
                var overlayGrid = new Grid();

                var bgImg = new Image
                {
                    Source = originalImage,
                    Stretch = Stretch.Uniform,
                    MaxHeight = 350
                };
                RenderOptions.SetBitmapScalingMode(bgImg, BitmapScalingMode.HighQuality);

                var fgImg = new Image
                {
                    Source = overlay,
                    Stretch = Stretch.Uniform,
                    MaxHeight = 350
                };
                RenderOptions.SetBitmapScalingMode(fgImg, BitmapScalingMode.HighQuality);

                overlayGrid.Children.Add(bgImg);
                overlayGrid.Children.Add(fgImg);

                var border = new Border
                {
                    BorderBrush = new SolidColorBrush(Colors.LimeGreen),
                    BorderThickness = new Thickness(2),
                    Margin = new Thickness(2),
                    Child = overlayGrid
                };

                var label = new TextBlock
                {
                    Text = "Result(Trajectory)",
                    Foreground = new SolidColorBrush(Colors.LimeGreen),
                    Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                    Padding = new Thickness(4, 2, 4, 2),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };

                var panel = new Grid();
                panel.Children.Add(border);
                panel.Children.Add(label);

                StepImageGrid.Children.Add(panel);
            }

            Log($"[HOLLOW] Step grid: {StepImageGrid.Children.Count} images");
        }

        /// <summary>
        /// 切换回单图显示模式
        /// </summary>
        private void SwitchToSingleImageMode()
        {
            StepGridScrollViewer.Visibility = Visibility.Collapsed;
            ImageScrollViewer.Visibility = Visibility.Visible;
            StepImageGrid.Children.Clear();
            _gridZoomLevel = 1.0;
            _gridScaleTransform.ScaleX = 1.0;
            _gridScaleTransform.ScaleY = 1.0;
        }

        // ==================== 一键自动检测 ====================
        private void BtnAutoDetect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "BMP Files (*.bmp)|*.bmp|All Files (*.*)|*.*",
                    Title = "Select Image for Trajectory Detection"
                };
                if (dlg.ShowDialog() != true) return;

                _currentImage?.Dispose();
                _currentImage = CalibAPI.LoadImage(dlg.FileName);
                Log($"[AUTO] Loaded: {_currentImage.Width}x{_currentImage.Height}");
                SwitchToSingleImageMode();
                DisplayImage(_currentImage);

                _trajDetector?.Dispose();
                _trajDetector = new TrajectoryStepDetector();
                _trajStep = 0;
                ResetZoom();

                bool useContourMode = ChkContourMode.IsChecked == true;
                int fitMode = CmbFitMode.SelectedIndex;

                // CLAHE 参数
                double claheClipLimit = 0;
                int claheTileGridSize = 8;
                if (ChkCLAHE.IsChecked == true)
                {
                    double.TryParse(TxtCLAHEClip.Text, out claheClipLimit);
                    int.TryParse(TxtCLAHEGrid.Text, out claheTileGridSize);
                }

                Log($"[AUTO] ContourMode={useContourMode}, FitMode={fitMode}, CLAHE={claheClipLimit > 0}({claheClipLimit:F1},{claheTileGridSize})");

                var result = _trajDetector.Detect(_currentImage, doFit: true, doVerify: true, doDedup: true,
                    bandWidth: 8, includeOuterBand: true, outerThreshold: 0,
                    useContourMode: useContourMode, outerExpandPixels: 25,
                    claheClipLimit: claheClipLimit, claheTileGridSize: claheTileGridSize);

                if (fitMode >= 0)
                    _trajDetector.FitShape(fitMode, 0.0);

                if (result.Success)
                {
                    Log($"[AUTO] SUCCESS! Found {result.Count} points");
                    if (result.Count > 0)
                    {
                        var coloredTraj = _trajDetector.DrawColoredTrajectory(_currentImage.Width, _currentImage.Height);
                        DisplayImage(coloredTraj);
                        string savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trajectory_auto.bmp");
                        CalibAPI.SaveImage(savePath, coloredTraj);
                        Log($"[AUTO] Saved: {savePath}");
                        coloredTraj.Dispose();
                    }
                    _trajStep = 10;
                }
                else
                {
                    Log($"[AUTO] FAIL: {result.ErrorMessage ?? "No trajectory"}");
                }
                UpdateStepButtonStates();
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Auto detect: {ex.Message}");
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== 分步检测 ====================
        private void BtnLoadTrajImg_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "BMP Files (*.bmp)|*.bmp|All Files (*.*)|*.*",
                    Title = "Select Image"
                };
                if (dlg.ShowDialog() != true) return;

                _currentImage?.Dispose();
                _currentImage = CalibAPI.LoadImage(dlg.FileName);
                Log($"[INFO] Loaded: {_currentImage.Width}x{_currentImage.Height}");

                _trajDetector?.Dispose();
                _trajDetector = new TrajectoryStepDetector();
                _trajStep = 0;
                ResetZoom();
                SwitchToSingleImageMode();
                DisplayImage(_currentImage);
                UpdateStepButtonStates();
            }
            catch (Exception ex)
            {
                Log($"[ERROR] {ex.Message}");
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnStep1_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;
                PrepareStep(1);
                Log("[STEP 1] Grayscale...");
                _trajDetector.ConvertToGrayscale(_currentImage);

                // Step 1b: 可选 CLAHE
                if (ChkCLAHE.IsChecked == true)
                {
                    double clipLimit = 2.0;
                    int tileGridSize = 8;
                    double.TryParse(TxtCLAHEClip.Text, out clipLimit);
                    int.TryParse(TxtCLAHEGrid.Text, out tileGridSize);
                    if (clipLimit > 0)
                    {
                        Log($"[STEP 1b] CLAHE (clipLimit={clipLimit:F1}, tileSize={tileGridSize})...");
                        _trajDetector.ApplyCLAHE(clipLimit, tileGridSize);
                    }
                }

                var stepImg = _trajDetector.GetStepImage(1);
                if (stepImg != null) { DisplayImage(stepImg); stepImg.Dispose(); }
                _trajStep = 1;
                UpdateStepButtonStates();
                Log("[STEP 1] Done");
            }
            catch (Exception ex) { Log($"[ERROR] Step 1: {ex.Message}"); }
        }

        private void BtnStep2_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;
                PrepareStep(1);
                int blurKsize = 7, morphKernelSize = 5;
                int.TryParse(TxtStep2Blur.Text, out blurKsize);
                int.TryParse(TxtStep2Morph.Text, out morphKernelSize);
                if (blurKsize % 2 == 0) blurKsize++;
                if (morphKernelSize % 2 == 0) morphKernelSize++;
                Log($"[STEP 2] Preprocess (blur={blurKsize}, morph={morphKernelSize})...");
                _trajDetector.PreprocessAndFindContours(blurKsize, morphKernelSize);
                var stepImg = _trajDetector.GetStepImage(2);
                if (stepImg != null) { DisplayImage(stepImg); stepImg.Dispose(); }
                _trajStep = 2;
                UpdateStepButtonStates();
                Log("[STEP 2] Done");
            }
            catch (Exception ex) { Log($"[ERROR] Step 2: {ex.Message}"); }
        }

        private void BtnCanny_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;
                PrepareStep(1);
                double lowThresh = 0, highThresh = 0, mult = 1.0;
                int blurKsize = 5, bilateralD = 0, nlmeansH = 0, nlmeansT = 7, nlmeansS = 21;
                double bilateralSC = 75, bilateralSS = 75;
                double.TryParse(TxtCannyMult.Text, out mult);
                double.TryParse(TxtCannyLow.Text, out lowThresh);
                double.TryParse(TxtCannyHigh.Text, out highThresh);
                int.TryParse(TxtCannyBlur.Text, out blurKsize);
                int.TryParse(TxtCannyBilateralD.Text, out bilateralD);
                double.TryParse(TxtCannyBilateralSC.Text, out bilateralSC);
                double.TryParse(TxtCannyBilateralSS.Text, out bilateralSS);
                int.TryParse(TxtCannyNlmeansH.Text, out nlmeansH);
                int.TryParse(TxtCannyNlmeansT.Text, out nlmeansT);
                int.TryParse(TxtCannyNlmeansS.Text, out nlmeansS);

                if (lowThresh > 0)
                    _trajDetector.CannyEdges(lowThresh, highThresh, blurKsize, bilateralD, bilateralSC, bilateralSS, nlmeansH, nlmeansT, nlmeansS);
                else if (mult != 1.0)
                    _trajDetector.CannyEdges(0, mult, blurKsize, bilateralD, bilateralSC, bilateralSS, nlmeansH, nlmeansT, nlmeansS);
                else
                    _trajDetector.CannyEdges(0, 0, blurKsize, bilateralD, bilateralSC, bilateralSS, nlmeansH, nlmeansT, nlmeansS);

                var stepImg = _trajDetector.GetStepImage(6);
                if (stepImg != null) { DisplayImage(stepImg); stepImg.Dispose(); }
                _trajStep = 1;
                UpdateStepButtonStates();
                Log("[STEP 2b] Done");
            }
            catch (Exception ex) { Log($"[ERROR] Step 2b: {ex.Message}"); }
        }

        private void BtnStep3_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;
                PrepareStep(2);
                int contourIdx = 0;
                int.TryParse(TxtStep3ContourIdx.Text, out contourIdx);
                Log($"[STEP 3] Mask (ContourIdx={contourIdx})...");
                _trajDetector.CreateWorkpieceMask(contourIdx);
                var stepImg = _trajDetector.GetStepImage(3);
                if (stepImg != null) { DisplayImage(stepImg); stepImg.Dispose(); }
                _trajStep = 3;
                UpdateStepButtonStates();
                Log("[STEP 3] Done");
            }
            catch (Exception ex) { Log($"[ERROR] Step 3: {ex.Message}"); }
        }

        private void BtnStep4_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;
                PrepareStep(3);
                int threshold = 50;
                int.TryParse(TxtStep4Threshold.Text, out threshold);
                Log($"[STEP 4] Dark bars (threshold={threshold})...");
                _trajDetector.DetectDarkBars(threshold);
                var stepImg = _trajDetector.GetStepImage(4);
                if (stepImg != null) { DisplayImage(stepImg); stepImg.Dispose(); }
                _trajStep = 4;
                UpdateStepButtonStates();
                Log("[STEP 4] Done");
            }
            catch (Exception ex) { Log($"[ERROR] Step 4: {ex.Message}"); }
        }

        private void BtnStep5_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;
                PrepareStep(4);
                int kernelSize = 5, blurKsize = 9;
                double blurSigma = 2.0;
                int.TryParse(TxtStep5Kernel.Text, out kernelSize);
                int.TryParse(TxtStep5Blur.Text, out blurKsize);
                double.TryParse(TxtStep5BlurSigma.Text, out blurSigma);
                if (kernelSize % 2 == 0) kernelSize++;
                if (blurKsize % 2 == 0) blurKsize++;
                Log($"[STEP 5] Morphology (kernel={kernelSize}, blur={blurKsize}, sigma={blurSigma:F1})...");
                _trajDetector.MorphologyCleanup(kernelSize, blurKsize, blurSigma);
                var stepImg = _trajDetector.GetStepImage(4);
                if (stepImg != null) { DisplayImage(stepImg); stepImg.Dispose(); }
                _trajStep = 5;
                UpdateStepButtonStates();
                Log("[STEP 5] Done");
            }
            catch (Exception ex) { Log($"[ERROR] Step 5: {ex.Message}"); }
        }

        private void BtnExpandEdge_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;
                PrepareStep(4);
                int expandPixels = 15;
                int.TryParse(TxtStep5Expand.Text, out expandPixels);
                if (expandPixels < 1) expandPixels = 1;
                Log($"[STEP 5.5] Expand (expand={expandPixels}px)...");
                _trajDetector.ExpandToEdgeBoundary(expandPixels);
                var stepImg = _trajDetector.GetStepImage(4);
                if (stepImg != null) { DisplayImage(stepImg); stepImg.Dispose(); }
                UpdateStepButtonStates();
                Log("[STEP 5.5] Done");
            }
            catch (Exception ex) { Log($"[ERROR] Step 5.5: {ex.Message}"); }
        }

        private void BtnStep6_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;
                PrepareStep(5);
                Log("[STEP 6] Find & sort...");
                int barCount = _trajDetector.FindAndSortDarkContours();
                Log($"[INFO] Found {barCount} bars");
                var stepImg = _trajDetector.GetStepImage(4);
                if (stepImg != null) { DisplayImage(stepImg); stepImg.Dispose(); }
                _trajStep = 6;
                UpdateStepButtonStates();
                Log("[STEP 6] Done");
            }
            catch (Exception ex) { Log($"[ERROR] Step 6: {ex.Message}"); }
        }

        private void BtnStep7_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;
                PrepareStep(6);
                int targetBars = 16, bandWidth = 8;
                int.TryParse(TxtStep7Bars.Text, out targetBars);
                int.TryParse(TxtStep7BandWidth.Text, out bandWidth);
                bool useContourMode = ChkContourMode.IsChecked == true;
                Log($"[STEP 7] Sample (bars={targetBars}, bandWidth={bandWidth}, canvas={useContourMode})...");
                _trajDetector.SampleContours(targetBars, bandWidth, true, useContourMode, 25);
                int ptCount = _trajDetector.GetPointCount();
                Log($"[INFO] Points: {ptCount}");

                if (bandWidth > 0)
                {
                    var bandImg = _trajDetector.GetStepImage(5);
                    if (bandImg != null) { DisplayImage(bandImg); bandImg.Dispose(); }
                }
                if (ptCount > 0)
                {
                    var coloredTraj = _trajDetector.DrawColoredTrajectory(_currentImage.Width, _currentImage.Height);
                    DisplayImage(coloredTraj);
                    coloredTraj.Dispose();
                }
                _trajStep = 7;
                UpdateStepButtonStates();
                Log("[STEP 7] Done");
            }
            catch (Exception ex) { Log($"[ERROR] Step 7: {ex.Message}"); }
        }

        private void BtnFit_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;
                PrepareStep(7);
                int fitMode = CmbFitMode.SelectedIndex;
                Log($"[STEP 7.5] Fit (mode={fitMode})...");
                _trajDetector.FitShape(fitMode, 0.0);
                int ptCount = _trajDetector.GetPointCount();
                Log($"[INFO] Points: {ptCount}");
                if (ptCount > 0)
                {
                    var coloredTraj = _trajDetector.DrawColoredTrajectory(_currentImage.Width, _currentImage.Height);
                    DisplayImage(coloredTraj);
                    coloredTraj.Dispose();
                }
                _trajStep = 7;
                UpdateStepButtonStates();
                Log("[STEP 7.5] Done");
            }
            catch (Exception ex) { Log($"[ERROR] Step 7.5: {ex.Message}"); }
        }

        private void BtnStep8_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;
                PrepareStep(7);
                Log("[STEP 8] Verify...");
                _trajDetector.VerifyByMask();
                int ptCount = _trajDetector.GetPointCount();
                Log($"[INFO] Points: {ptCount}");
                if (ptCount > 0)
                {
                    var coloredTraj = _trajDetector.DrawColoredTrajectory(_currentImage.Width, _currentImage.Height);
                    DisplayImage(coloredTraj);
                    coloredTraj.Dispose();
                }
                _trajStep = 8;
                UpdateStepButtonStates();
                Log("[STEP 8] Done");
            }
            catch (Exception ex) { Log($"[ERROR] Step 8: {ex.Message}"); }
        }

        private void BtnStep9_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;
                PrepareStep(8);
                Log("[STEP 9] Dedup...");
                _trajDetector.DeduplicateAndSort();
                int ptCount = _trajDetector.GetPointCount();
                Log($"[INFO] Points: {ptCount}");
                if (ptCount > 0)
                {
                    var coloredTraj = _trajDetector.DrawColoredTrajectory(_currentImage.Width, _currentImage.Height);
                    DisplayImage(coloredTraj);
                    coloredTraj.Dispose();
                }
                _trajStep = 9;
                UpdateStepButtonStates();
                Log("[STEP 9] Done");
            }
            catch (Exception ex) { Log($"[ERROR] Step 9: {ex.Message}"); }
        }

        private void BtnStep10_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;
                PrepareStep(9);
                Log("[STEP 10] Output...");
                var result = _trajDetector.ConvertToOutput();
                if (result.Success && result.Count > 0)
                {
                    Log($"[INFO] SUCCESS! {result.Count} points");
                    var coloredTraj = _trajDetector.DrawColoredTrajectory(_currentImage.Width, _currentImage.Height);
                    DisplayImage(coloredTraj);
                    string savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trajectory_step_result.bmp");
                    CalibAPI.SaveImage(savePath, coloredTraj);
                    Log($"[INFO] Saved: {savePath}");
                    coloredTraj.Dispose();
                    _trajStep = 10;
                }
                else
                {
                    Log($"[WARN] {result.ErrorMessage ?? "No trajectory"}");
                }
                UpdateStepButtonStates();
            }
            catch (Exception ex) { Log($"[ERROR] Step 10: {ex.Message}"); }
        }

        // ==================== 缩放 & 拖动 ====================
        private void ImageScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            var mousePos = e.GetPosition(ImageDisplay);
            double relativeX = mousePos.X / ImageDisplay.ActualWidth;
            double relativeY = mousePos.Y / ImageDisplay.ActualHeight;
            _zoomLevel = e.Delta > 0
                ? Math.Min(_zoomLevel + ZOOM_STEP, MAX_ZOOM)
                : Math.Max(_zoomLevel - ZOOM_STEP, MIN_ZOOM);
            _imageScaleTransform.ScaleX = _zoomLevel;
            _imageScaleTransform.ScaleY = _zoomLevel;
            if (ImageDisplay.ActualWidth > 0 && ImageDisplay.ActualHeight > 0)
            {
                double newWidth = ImageScrollViewer.ExtentWidth * _zoomLevel;
                double newHeight = ImageScrollViewer.ExtentHeight * _zoomLevel;
                ImageScrollViewer.ScrollToHorizontalOffset(relativeX * newWidth - mousePos.X);
                ImageScrollViewer.ScrollToVerticalOffset(relativeY * newHeight - mousePos.Y);
            }
            UpdateStatus($"Zoom: {_zoomLevel:F1}x");
            e.Handled = true;
        }

        private void StepGridScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            var mousePos = e.GetPosition(StepImageGrid);
            double relativeX = StepImageGrid.ActualWidth > 0 ? mousePos.X / StepImageGrid.ActualWidth : 0;
            double relativeY = StepImageGrid.ActualHeight > 0 ? mousePos.Y / StepImageGrid.ActualHeight : 0;

            _gridZoomLevel = e.Delta > 0
                ? Math.Min(_gridZoomLevel + ZOOM_STEP, MAX_ZOOM)
                : Math.Max(_gridZoomLevel - ZOOM_STEP, MIN_ZOOM);

            _gridScaleTransform.ScaleX = _gridZoomLevel;
            _gridScaleTransform.ScaleY = _gridZoomLevel;

            double newWidth = StepGridScrollViewer.ExtentWidth * _gridZoomLevel;
            double newHeight = StepGridScrollViewer.ExtentHeight * _gridZoomLevel;
            StepGridScrollViewer.ScrollToHorizontalOffset(relativeX * newWidth - mousePos.X);
            StepGridScrollViewer.ScrollToVerticalOffset(relativeY * newHeight - mousePos.Y);

            UpdateStatus($"Grid Zoom: {_gridZoomLevel:F1}x");
            e.Handled = true;
        }

        private void ImageScrollViewer_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.RightButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                _isDragging = true;
                _dragButton = System.Windows.Input.MouseButton.Right;
                _lastMousePos = e.GetPosition(ImageScrollViewer);
                ImageScrollViewer.CaptureMouse();
                ImageScrollViewer.Cursor = System.Windows.Input.Cursors.Hand;
            }
        }

        private void ImageScrollViewer_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isDragging && _dragButton == System.Windows.Input.MouseButton.Right)
            {
                var pos = e.GetPosition(ImageScrollViewer);
                ImageScrollViewer.ScrollToHorizontalOffset(ImageScrollViewer.HorizontalOffset + _lastMousePos.X - pos.X);
                ImageScrollViewer.ScrollToVerticalOffset(ImageScrollViewer.VerticalOffset + _lastMousePos.Y - pos.Y);
                _lastMousePos = pos;
            }
        }

        private void ImageScrollViewer_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isDragging && e.ChangedButton == _dragButton)
            {
                _isDragging = false;
                ImageScrollViewer.ReleaseMouseCapture();
                ImageScrollViewer.Cursor = System.Windows.Input.Cursors.Arrow;
            }
        }

        private void ImageScrollViewer_PreviewMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) { }

        private void SaveImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Filter = "BMP|*.bmp|PNG|*.png|JPEG|*.jpg",
                    Title = "保存图片",
                    FileName = $"saved_{DateTime.Now:yyyyMMdd_HHmmss}.bmp"
                };
                if (dlg.ShowDialog() == true && ImageDisplay.Source is BitmapSource bmpSrc)
                {
                    BitmapEncoder enc = Path.GetExtension(dlg.FileName).ToLower() switch
                    {
                        ".png" => new PngBitmapEncoder(),
                        ".jpg" or ".jpeg" => new JpegBitmapEncoder(),
                        _ => new BmpBitmapEncoder()
                    };
                    enc.Frames.Add(BitmapFrame.Create(bmpSrc));
                    using var fs = File.Create(dlg.FileName);
                    enc.Save(fs);
                    Log($"[INFO] Saved: {dlg.FileName}");
                }
            }
            catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        }
    }
}
