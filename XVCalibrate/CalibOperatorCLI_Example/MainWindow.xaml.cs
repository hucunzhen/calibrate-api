using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using CalibOperatorPInvoke;
using HslCommunication;
using HslCommunication.Profinet.XINJE;

namespace CalibOperatorCLI_Example
{
    public partial class MainWindow : Window
    {
        private CalibImage? _currentImage;
        private Point2D[]? _detectedPoints;
        private CalibrationResult? _calibrationResult;
        private int _currentStep = 0;
        private int _trajStep = 0;  // 轨迹检测当前步骤
        private StreamWriter? _logWriter;
        private TrajectoryStepDetector? _trajDetector;
        private XinJETcpNet? _plc;
        private bool _plcConnected = false;

        private const int IMAGE_WIDTH = 2448;
        private const int IMAGE_HEIGHT = 2048;

        // 缩放相关
        private double _zoomLevel = 1.0;
        private const double ZOOM_STEP = 0.1;
        private const double MIN_ZOOM = 0.1;
        private const double MAX_ZOOM = 5.0;
        private ScaleTransform _imageScaleTransform;

        // 右键拖动相关
        private bool _isDragging = false;
        private System.Windows.Point _lastMousePos;
        private System.Windows.Input.MouseButton _dragButton;

        private readonly Point2D[] _worldPoints = new[]
        {
            new Point2D(100, 100), new Point2D(400, 100), new Point2D(700, 100),
            new Point2D(100, 300), new Point2D(400, 300), new Point2D(700, 300),
            new Point2D(100, 500), new Point2D(400, 500), new Point2D(700, 500)
        };

        public MainWindow()
        {
            InitializeComponent();
            _imageScaleTransform = ImageScaleTransform;  // 初始化缩放变换引用
            InitLogging();
            Log("========================================");
            Log("Calibration GUI Started");
            Log($"Build: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log("========================================");
            UpdateButtonStates();
        }

        private void InitLogging()
        {
            try
            {
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_autotest.log");
                _logWriter = new StreamWriter(logPath, false);
                _logWriter.AutoFlush = true;
            }
            catch (Exception ex)
            {
                Log($"Failed to create log file: {ex.Message}");
            }
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

            _logWriter?.WriteLine(logLine);
            UpdateStatus(message);
        }

        private void UpdateStatus(string message)
        {
            Dispatcher.Invoke(() => StatusText.Text = message);
        }

        private void UpdateButtonStates()
        {
            BtnDetectCircles.IsEnabled = _currentImage != null;
            BtnCalibrate.IsEnabled = _detectedPoints != null && _detectedPoints.Length >= 4;
            BtnSaveResults.IsEnabled = _calibrationResult != null && _calibrationResult.Success;
            BtnTestTransform.IsEnabled = _calibrationResult != null && _calibrationResult.Success;
            BtnAutoTrajTest.IsEnabled = true; // 始终可用，不需要先标定
            UpdateStepButtonStates();
        }

        private void UpdateStepButtonStates()
        {
            // 加载图像按钮始终可用
            BtnLoadTrajImg.IsEnabled = true;

            // 步骤按钮状态：只有前一步完成后才能执行下一步
            bool detectorReady = _trajDetector != null && _currentImage != null;
            BtnStep1.IsEnabled = detectorReady && _trajStep >= 0;
            BtnStep2.IsEnabled = detectorReady && _trajStep >= 1;
            BtnStep3.IsEnabled = detectorReady && _trajStep >= 2;
            BtnStep4.IsEnabled = detectorReady && _trajStep >= 3;
            BtnStep5.IsEnabled = detectorReady && _trajStep >= 4;
            BtnStep6.IsEnabled = detectorReady && _trajStep >= 5;
            BtnStep7.IsEnabled = detectorReady && _trajStep >= 6;
            BtnStep8.IsEnabled = detectorReady && _trajStep >= 7;
            BtnStep9.IsEnabled = detectorReady && _trajStep >= 8;
            BtnStep10.IsEnabled = detectorReady && _trajStep >= 9;

            // 根据当前步骤显示对应参数面板
            UpdateStepParameterVisibility();
        }

        private void UpdateStepParameterVisibility()
        {
            // 隐藏所有参数行
            SpStep2Params.Visibility = Visibility.Collapsed;
            SpStep3Params.Visibility = Visibility.Collapsed;
            SpStep4Params.Visibility = Visibility.Collapsed;
            SpStep5Params.Visibility = Visibility.Collapsed;
            SpStep7Params.Visibility = Visibility.Collapsed;

            // 只显示当前步骤的参数（下一步才能修改的参数）
            switch (_trajStep)
            {
                case 1:  // Step1完成后，显示Step2参数（高斯模糊+morph）
                    SpStep2Params.Visibility = Visibility.Visible;
                    break;
                case 2:  // Step2完成后，显示Step3参数
                    SpStep3Params.Visibility = Visibility.Visible;
                    break;
                case 3:  // Step3完成后，显示Step4参数
                    SpStep4Params.Visibility = Visibility.Visible;
                    break;
                case 4:  // Step4完成后，显示Step5参数(morph+blur)
                    SpStep5Params.Visibility = Visibility.Visible;
                    break;
                case 5:  // Step5完成后，显示Step7参数
                    SpStep7Params.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void DisplayImage(CalibImage img)
        {
            try
            {
                using var bmp = img.ToBitmap();
                if (bmp != null)
                {
                    var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        bmp.GetHbitmap(),
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    Dispatcher.Invoke(() => ImageDisplay.Source = bitmap);
                }
                else
                {
                    Log("[WARN] DisplayImage: ToBitmap returned null");
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Failed to display image: {ex.Message}");
            }
        }

        private void BtnGenerateImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[INFO] Button 1: Generate Image - START");

                _currentImage?.Dispose();
                _currentImage = CalibAPI.CreateCalibrationImage(IMAGE_WIDTH, IMAGE_HEIGHT);

                Log($"[INFO] Image created: {_currentImage.Width}x{_currentImage.Height}, {_currentImage.Channels} channels");

                string savePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "calibration_9points.bmp");
                CalibAPI.SaveImage(savePath, _currentImage);
                Log($"[INFO] Image saved to: {savePath}");

                DisplayImage(_currentImage);

                _currentStep = 1;
                _detectedPoints = null;
                _calibrationResult = null;
                Log($"[INFO] Step updated to: {_currentStep}");
                UpdateButtonStates();
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Failed to generate image: {ex.Message}");
                MessageBox.Show($"Failed to generate image:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnLoadImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[INFO] Button 2: Load Image - START");

                var dlg = new OpenFileDialog
                {
                    Filter = "BMP Files (*.bmp)|*.bmp|All Files (*.*)|*.*",
                    Title = "Select Calibration Image"
                };

                if (dlg.ShowDialog() == true)
                {
                    _currentImage?.Dispose();
                    _currentImage = CalibAPI.LoadImage(dlg.FileName);

                    Log($"[INFO] Image loaded: {_currentImage.Width}x{_currentImage.Height}");
                    Log($"[INFO] File: {dlg.FileName}");

                    DisplayImage(_currentImage);

                    _currentStep = 2;
                    _detectedPoints = null;
                    _calibrationResult = null;
                    Log($"[INFO] Step updated to: {_currentStep}");
                    UpdateButtonStates();
                }
                else
                {
                    Log("[INFO] File dialog cancelled");
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Failed to load image: {ex.Message}");
                MessageBox.Show($"Failed to load image:\n{ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnDetectCircles_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[INFO] Button 3: Detect Circles - START");

                if (_currentImage == null)
                {
                    Log("[ERROR] No image loaded");
                    return;
                }

                _detectedPoints = CalibAPI.DetectCircles(_currentImage);

                if (_detectedPoints != null && _detectedPoints.Length > 0)
                {
                    Log($"[INFO] Detected {_detectedPoints.Length} circles:");
                    for (int i = 0; i < Math.Min(9, _detectedPoints.Length); i++)
                    {
                        Log($"  Point {i + 1}: ({_detectedPoints[i].X:F2}, {_detectedPoints[i].Y:F2})");
                    }

                    CalibAPI.DrawDetectedCircles(_currentImage, _detectedPoints, 255);
                    DisplayImage(_currentImage);

                    _currentStep = 3;
                    Log($"[INFO] Step updated to: {_currentStep}");
                }
                else
                {
                    Log("[WARN] No circles detected");
                    MessageBox.Show("No circles detected in the image.", "Detection Result", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                UpdateButtonStates();
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Circle detection failed: {ex.Message}");
                MessageBox.Show($"Circle detection failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCalibrate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[INFO] Button 4: Calibrate - START");

                if (_detectedPoints == null || _detectedPoints.Length < 4)
                {
                    Log("[ERROR] Not enough detected points for calibration");
                    MessageBox.Show("Please detect at least 4 circles before calibration.", "Calibration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Point2D[] imagePoints;
                Point2D[] worldPts;

                if (_detectedPoints.Length == 9)
                {
                    imagePoints = _detectedPoints;
                    worldPts = _worldPoints;
                }
                else
                {
                    imagePoints = _detectedPoints;
                    worldPts = new Point2D[_detectedPoints.Length];

                    double stepX = 600.0 / 2;
                    double stepY = 400.0 / 2;
                    for (int i = 0; i < worldPts.Length; i++)
                    {
                        int row = i / 3;
                        int col = i % 3;
                        worldPts[i] = new Point2D(100 + col * stepX, 100 + row * stepY);
                    }
                }

                Log("[INFO] Performing 9-point calibration...");
                _calibrationResult = CalibAPI.CalibrateNinePoint(imagePoints, worldPts);

                if (_calibrationResult.Success)
                {
                    Log("[INFO] Calibration SUCCESS!");
                    Log("[INFO] Affine Transform Parameters:");
                    Log($"  X = {_calibrationResult.Transform.A:F6}*x + {_calibrationResult.Transform.B:F6}*y + {_calibrationResult.Transform.C:F6}");
                    Log($"  Y = {_calibrationResult.Transform.D:F6}*x + {_calibrationResult.Transform.E:F6}*y + {_calibrationResult.Transform.F:F6}");
                    Log($"  Average Error: {_calibrationResult.AverageError:F4} pixels");
                    Log($"  Max Error: {_calibrationResult.MaxError:F4} pixels");

                    CalibAPI.SetTransform(_calibrationResult.Transform);

                    _currentStep = 4;
                    Log($"[INFO] Step updated to: {_currentStep}");
                }
                else
                {
                    Log($"[ERROR] Calibration FAILED: {_calibrationResult.ErrorMessage}");
                    MessageBox.Show($"Calibration failed:\n{_calibrationResult.ErrorMessage}", "Calibration Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                UpdateButtonStates();
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Calibration failed: {ex.Message}");
                MessageBox.Show($"Calibration failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSaveResults_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[INFO] Button 5: Save Results - START");

                if (_calibrationResult == null || !_calibrationResult.Success)
                {
                    Log("[ERROR] No valid calibration result to save");
                    return;
                }

                var dlg = new SaveFileDialog
                {
                    Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                    Title = "Save Calibration Results",
                    FileName = "calibration_result.txt"
                };

                if (dlg.ShowDialog() == true)
                {
                    using var writer = new StreamWriter(dlg.FileName);
                    writer.WriteLine("=== 9-Point Calibration Results ===");
                    writer.WriteLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine();
                    writer.WriteLine("Affine Transform Parameters:");
                    writer.WriteLine($"  X = {_calibrationResult.Transform.A:F8}*x + {_calibrationResult.Transform.B:F8}*y + {_calibrationResult.Transform.C:F8}");
                    writer.WriteLine($"  Y = {_calibrationResult.Transform.D:F8}*x + {_calibrationResult.Transform.E:F8}*y + {_calibrationResult.Transform.F:F8}");
                    writer.WriteLine();
                    writer.WriteLine($"Average Error: {_calibrationResult.AverageError:F6} pixels");
                    writer.WriteLine($"Max Error: {_calibrationResult.MaxError:F6} pixels");

                    Log($"[INFO] Results saved to: {dlg.FileName}");
                    MessageBox.Show("Results saved successfully!", "Save Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Failed to save results: {ex.Message}");
                MessageBox.Show($"Failed to save results:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnTestTransform_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[INFO] Button 6: Test Transform - START");

                if (_calibrationResult == null || !_calibrationResult.Success)
                {
                    Log("[ERROR] No valid calibration for transform test");
                    return;
                }

                var testPoints = new[]
                {
                    new Point2D(400, 300),
                    new Point2D(100, 100),
                    new Point2D(700, 500)
                };

                Log("[INFO] Testing coordinate transformation:");
                foreach (var pixel in testPoints)
                {
                    var world = CalibAPI.ImageToWorld(pixel, _calibrationResult.Transform);
                    Log($"  Pixel ({pixel.X:F1}, {pixel.Y:F1}) -> World ({world.X:F4}, {world.Y:F4}) mm");
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Transform test failed: {ex.Message}");
                MessageBox.Show($"Transform test failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAutoTrajTest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[INFO] Button 7: Auto Trajectory Test - START");

                var dlg = new OpenFileDialog
                {
                    Filter = "BMP Files (*.bmp)|*.bmp|All Files (*.*)|*.*",
                    Title = "Select Image for Trajectory Detection"
                };

                if (dlg.ShowDialog() != true)
                {
                    Log("[INFO] File dialog cancelled");
                    return;
                }

                _currentImage?.Dispose();
                _currentImage = CalibAPI.LoadImage(dlg.FileName);
                Log($"[INFO] Image loaded: {_currentImage.Width}x{_currentImage.Height}");
                DisplayImage(_currentImage);

                Log("[INFO] Detecting trajectory...");
                var result = CalibAPI.DetectTrajectoryOpenCV(_currentImage);

                if (result.Success)
                {
                    Log($"[INFO] Trajectory detection SUCCESS! Found {result.Count} points");

                    if (result.Points != null && result.Points.Length > 0)
                    {
                        // 创建空白图像用于绘制轨迹
                        using var blankImage = new CalibImage(_currentImage.Width, _currentImage.Height, 1);
                        CalibAPI.DrawTrajectoryGrayscale(blankImage, result.Points, 255);
                        DisplayImage(blankImage);

                        string savePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trajectory_result.bmp");
                        CalibAPI.SaveImage(savePath, blankImage);
                        Log($"[INFO] Trajectory image saved to: {savePath}");
                    }
                }
                else
                {
                    Log($"[WARN] Trajectory detection: {result.ErrorMessage ?? "No trajectory found"}");
                    MessageBox.Show($"Trajectory detection result:\n{result.ErrorMessage ?? "No trajectory found"}", "Trajectory Detection", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Trajectory detection failed: {ex.Message}");
                MessageBox.Show($"Trajectory detection failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            Log("[INFO] Application closing...");

            // 断开PLC连接
            if (_plc != null)
            {
                if (_plcConnected) _plc.ConnectClose();
                _plc = null;
            }

            _currentImage?.Dispose();
            _trajDetector?.Dispose();
            _logWriter?.Close();
        }

        // ================================================================
        // 分步骤轨迹检测
        // ================================================================

        private void BtnLoadTrajImg_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "BMP Files (*.bmp)|*.bmp|All Files (*.*)|*.*",
                    Title = "Select Image for Trajectory Detection"
                };

                if (dlg.ShowDialog() != true)
                {
                    Log("[INFO] File dialog cancelled");
                    return;
                }

                _currentImage?.Dispose();
                _currentImage = CalibAPI.LoadImage(dlg.FileName);
                Log($"[INFO] Image loaded: {_currentImage.Width}x{_currentImage.Height}");

                // 释放旧的detector，创建新的
                _trajDetector?.Dispose();
                _trajDetector = new TrajectoryStepDetector();
                _trajStep = 0;  // 重置步骤计数器
                ResetZoom();    // 重置缩放

                DisplayImage(_currentImage);
                UpdateStepButtonStates();
                Log("[INFO] Trajectory detector initialized, ready for step-by-step processing");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Failed to load image: {ex.Message}");
                MessageBox.Show($"Failed to load image:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetZoom()
        {
            _zoomLevel = 1.0;
            _imageScaleTransform.ScaleX = 1.0;
            _imageScaleTransform.ScaleY = 1.0;
            ImageScrollViewer.ScrollToHorizontalOffset(0);
            ImageScrollViewer.ScrollToVerticalOffset(0);
        }

        private void BtnStep1_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;

                Log("[STEP 1] Convert to grayscale...");
                _trajDetector.ConvertToGrayscale(_currentImage);
                Log($"[STEP 1] Grayscale converted, size: {_currentImage.Width}x{_currentImage.Height}");

                var stepImg = _trajDetector.GetStepImage(1);
                if (stepImg != null)
                {
                    Log($"[STEP 1] Got step image: {stepImg.Width}x{stepImg.Height}, channels: {stepImg.Channels}");
                    DisplayImage(stepImg);
                    Log("[STEP 1] Image displayed");
                    stepImg.Dispose();
                }
                else
                {
                    Log("[WARN] Step 1: GetStepImage returned null");
                }
                _trajStep = 1;  // Step1完成，启用Step2
                UpdateStepButtonStates();
                Log("[STEP 1] Done");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Step 1 failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.ToString());
            }
        }

        private void BtnStep2_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;

                // 读取参数
                int blurKsize = 7;
                if (!int.TryParse(TxtStep2Blur.Text, out blurKsize))
                {
                    Log("[WARN] Invalid blur kernel, using 7");
                    blurKsize = 7;
                }
                int morphKernelSize = 5;
                if (!int.TryParse(TxtStep2Morph.Text, out morphKernelSize))
                {
                    Log("[WARN] Invalid morph kernel, using 5");
                    morphKernelSize = 5;
                }
                if (blurKsize % 2 == 0) blurKsize++;
                if (morphKernelSize % 2 == 0) morphKernelSize++;

                Log($"[STEP 2] Preprocess (blur={blurKsize}, morph={morphKernelSize})...");
                _trajDetector.PreprocessAndFindContours(blurKsize, morphKernelSize);
                Log("[STEP 2] Contour detection complete");

                // 步骤2显示的是二值化图像，不是mask
                var stepImg = _trajDetector.GetStepImage(2);
                if (stepImg != null)
                {
                    Log($"[STEP 2] Got binary image: {stepImg.Width}x{stepImg.Height}, channels: {stepImg.Channels}");
                    DisplayImage(stepImg);
                    stepImg.Dispose();
                    Log("[STEP 2] Image displayed");
                }
                else
                {
                    Log("[WARN] Step 2: GetStepImage(2) returned null");
                }
                _trajStep = 2;  // Step2完成，启用Step3
                UpdateStepButtonStates();
                Log("[STEP 2] Done");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Step 2 failed: {ex.Message}");
            }
        }

        private void BtnStep3_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;

                // 读取参数
                int contourIdx = 0;
                if (!int.TryParse(TxtStep3ContourIdx.Text, out contourIdx))
                {
                    Log("[WARN] Invalid ContourIdx, using 0 (auto)");
                    contourIdx = 0;
                }

                Log($"[STEP 3] Create workpiece mask (ContourIdx={contourIdx})...");
                _trajDetector.CreateWorkpieceMask(contourIdx);
                Log("[STEP 3] Mask created");

                var stepImg = _trajDetector.GetStepImage(3);
                if (stepImg != null)
                {
                    Log($"[STEP 3] Got mask image: {stepImg.Width}x{stepImg.Height}, channels: {stepImg.Channels}");
                    DisplayImage(stepImg);
                    stepImg.Dispose();
                    Log("[STEP 3] Mask displayed");
                }
                else
                {
                    Log("[WARN] Step 3: GetStepImage(3) returned null - mask may be empty");
                }
                _trajStep = 3;  // Step3完成，启用Step4
                UpdateStepButtonStates();
                Log("[STEP 3] Done");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Step 3 failed: {ex.Message}");
            }
        }

        private void BtnStep4_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;

                // 读取参数
                int threshold = 45;
                if (!int.TryParse(TxtStep4Threshold.Text, out threshold))
                {
                    Log("[WARN] Invalid threshold, using 45");
                    threshold = 45;
                }

                Log($"[STEP 4] Detect dark bars (threshold={threshold})...");
                _trajDetector.DetectDarkBars(threshold);

                var stepImg = _trajDetector.GetStepImage(4);
                if (stepImg != null)
                {
                    DisplayImage(stepImg);
                    stepImg.Dispose();
                }
                _trajStep = 4;  // Step4完成，启用Step5
                UpdateStepButtonStates();
                Log("[STEP 4] Done");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Step 4 failed: {ex.Message}");
            }
        }

        private void BtnStep5_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;

                // 读取参数
                int kernelSize = 3;
                if (!int.TryParse(TxtStep5Kernel.Text, out kernelSize))
                {
                    Log("[WARN] Invalid kernel size, using 3");
                    kernelSize = 3;
                }
                // 确保kernelSize是奇数
                if (kernelSize % 2 == 0) kernelSize++;

                // 读取模糊核大小
                int blurKsize = 5;
                if (!int.TryParse(TxtStep5Blur.Text, out blurKsize))
                {
                    Log("[WARN] Invalid blur size, using 5");
                    blurKsize = 5;
                }
                // 确保blurKsize是奇数
                if (blurKsize % 2 == 0) blurKsize++;

                Log($"[STEP 5] Morphology cleanup (kernel={kernelSize}, blur={blurKsize})...");
                double blurSigma = 1.0;
                if (!double.TryParse(TxtStep5BlurSigma.Text, out blurSigma))
                {
                    Log("[WARN] Invalid blur sigma, using 1.0");
                    blurSigma = 1.0;
                }

                Log($"[STEP 5] Morphology cleanup (kernel={kernelSize}, blur={blurKsize}, sigma={blurSigma:F1})...");
                _trajDetector.MorphologyCleanup(kernelSize, blurKsize, blurSigma);

                // 显示morphed图像（复用step 4的darkBinary结果）
                var stepImg = _trajDetector.GetStepImage(4);
                if (stepImg != null)
                {
                    DisplayImage(stepImg);
                    stepImg.Dispose();
                }
                _trajStep = 5;  // Step5完成，启用Step6
                UpdateStepButtonStates();
                Log("[STEP 5] Done");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Step 5 failed: {ex.Message}");
            }
        }

        private void BtnStep6_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;

                Log("[STEP 6] Find and sort dark contours...");
                int barCount = _trajDetector.FindAndSortDarkContours();
                Log($"[INFO] Dark bars found: {barCount}");

                var stepImg = _trajDetector.GetStepImage(4);
                if (stepImg != null)
                {
                    DisplayImage(stepImg);
                    stepImg.Dispose();
                }
                _trajStep = 6;  // Step6完成，启用Step7
                UpdateStepButtonStates();
                Log("[STEP 6] Done");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Step 6 failed: {ex.Message}");
            }
        }

        private void BtnStep7_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;

                // 读取参数
                int targetBars = 16;
                if (!int.TryParse(TxtStep7Bars.Text, out targetBars))
                {
                    Log("[WARN] Invalid target bars, using 16");
                    targetBars = 16;
                }
                int bandWidth = 5;
                if (!int.TryParse(TxtStep7BandWidth.Text, out bandWidth))
                {
                    Log("[WARN] Invalid band width, using 5");
                    bandWidth = 5;
                }

                Log($"[STEP 7] Sample contours equidistantly (bars={targetBars}, bandWidth={bandWidth})...");
                _trajDetector.SampleContours(targetBars, bandWidth);
                int ptCount = _trajDetector.GetPointCount();
                Log($"[INFO] Points after sampling: {ptCount}");

                // 显示窄带可视化图（暗条=白，窄带=灰，背景=黑）
                if (bandWidth > 0) {
                    var bandImg = _trajDetector.GetStepImage(5);
                    if (bandImg != null) {
                        DisplayImage(bandImg);
                        bandImg.Dispose();
                        Log("[STEP 7] Band visualization displayed (white=bars, gray=band, black=bg)");
                    }
                }

                // 绘制彩色轨迹显示
                if (ptCount > 0) {
                    var coloredTraj = _trajDetector.DrawColoredTrajectory(_currentImage.Width, _currentImage.Height);
                    DisplayImage(coloredTraj);
                    coloredTraj.Dispose();
                }

                _trajStep = 7;  // Step7完成，启用Step8
                UpdateStepButtonStates();
                Log("[STEP 7] Done");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Step 7 failed: {ex.Message}");
            }
        }

        private void BtnFit_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;

                Log("[STEP 7.5] Fit shape...");
                _trajDetector.FitShape();
                int ptCount = _trajDetector.GetPointCount();
                Log($"[INFO] Points after fitting: {ptCount}");

                // 绘制彩色轨迹显示
                if (ptCount > 0) {
                    var coloredTraj = _trajDetector.DrawColoredTrajectory(_currentImage.Width, _currentImage.Height);
                    DisplayImage(coloredTraj);
                    coloredTraj.Dispose();
                }

                _trajStep = 7;  // 保持Step7完成状态，可选Fit后可继续Step8
                UpdateStepButtonStates();
                Log("[STEP 7.5] Done");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Step 7.5 failed: {ex.Message}");
            }
        }

        private void BtnStep8_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;

                Log("[STEP 8] Verify by mask...");
                _trajDetector.VerifyByMask();
                int ptCount = _trajDetector.GetPointCount();
                Log($"[INFO] Points after mask verification: {ptCount}");

                // 绘制彩色轨迹显示
                if (ptCount > 0) {
                    var coloredTraj = _trajDetector.DrawColoredTrajectory(_currentImage.Width, _currentImage.Height);
                    DisplayImage(coloredTraj);
                    coloredTraj.Dispose();
                }

                _trajStep = 8;  // Step8完成，启用Step9
                UpdateStepButtonStates();
                Log("[STEP 8] Done");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Step 8 failed: {ex.Message}");
            }
        }

        private void BtnStep9_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;

                Log("[STEP 9] Deduplicate and sort...");
                _trajDetector.DeduplicateAndSort();
                int ptCount = _trajDetector.GetPointCount();
                Log($"[INFO] Points after dedup: {ptCount}");

                // 绘制彩色轨迹显示
                if (ptCount > 0) {
                    var coloredTraj = _trajDetector.DrawColoredTrajectory(_currentImage.Width, _currentImage.Height);
                    DisplayImage(coloredTraj);
                    coloredTraj.Dispose();
                }

                _trajStep = 9;  // Step9完成，启用Step10
                UpdateStepButtonStates();
                Log("[STEP 9] Done");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Step 9 failed: {ex.Message}");
            }
        }

        private void BtnStep10_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trajDetector == null || _currentImage == null) return;

                Log("[STEP 10] Convert to output...");
                var result = _trajDetector.ConvertToOutput();

                if (result.Success && result.Count > 0)
                {
                    Log($"[INFO] Step-by-step detection SUCCESS! Found {result.Count} trajectory points");

                    // 绘制彩色轨迹
                    var coloredTraj = _trajDetector.DrawColoredTrajectory(_currentImage.Width, _currentImage.Height);
                    DisplayImage(coloredTraj);

                    string savePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trajectory_step_result.bmp");
                    CalibAPI.SaveImage(savePath, coloredTraj);
                    Log($"[INFO] Step-by-step trajectory image saved to: {savePath}");

                    coloredTraj.Dispose();
                    _trajStep = 10;  // Step10完成
                }
                else
                {
                    Log($"[WARN] Detection failed: {result.ErrorMessage ?? "No trajectory found"}");
                    MessageBox.Show($"Detection result:\n{result.ErrorMessage ?? "No trajectory found"}",
                        "Trajectory Detection", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                UpdateStepButtonStates();
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Step 10 failed: {ex.Message}");
                MessageBox.Show($"Step 10 failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== 鼠标滚轮缩放图像 ====================
        private void ImageScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            // 获取鼠标位置（相对于Image）
            var mousePos = e.GetPosition(ImageDisplay);

            // 计算缩放前鼠标在图像上的相对位置
            double relativeX = mousePos.X / ImageDisplay.ActualWidth;
            double relativeY = mousePos.Y / ImageDisplay.ActualHeight;

            // 根据滚轮方向调整缩放级别
            if (e.Delta > 0)
            {
                _zoomLevel = Math.Min(_zoomLevel + ZOOM_STEP, MAX_ZOOM);
            }
            else
            {
                _zoomLevel = Math.Max(_zoomLevel - ZOOM_STEP, MIN_ZOOM);
            }

            // 应用缩放变换
            _imageScaleTransform.ScaleX = _zoomLevel;
            _imageScaleTransform.ScaleY = _zoomLevel;

            // 调整滚动位置，使鼠标位置保持不变（缩放中心跟随鼠标）
            if (ImageDisplay.ActualWidth > 0 && ImageDisplay.ActualHeight > 0)
            {
                double newWidth = ImageScrollViewer.ExtentWidth * _zoomLevel;
                double newHeight = ImageScrollViewer.ExtentHeight * _zoomLevel;

                ImageScrollViewer.ScrollToHorizontalOffset(relativeX * newWidth - mousePos.X);
                ImageScrollViewer.ScrollToVerticalOffset(relativeY * newHeight - mousePos.Y);
            }

            // 更新状态栏显示
            UpdateStatus($"Zoom: {_zoomLevel:F1}x");

            e.Handled = true;
        }

        // ==================== 右键拖动图像 ====================
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
                var currentPos = e.GetPosition(ImageScrollViewer);
                double deltaX = _lastMousePos.X - currentPos.X;
                double deltaY = _lastMousePos.Y - currentPos.Y;

                ImageScrollViewer.ScrollToHorizontalOffset(ImageScrollViewer.HorizontalOffset + deltaX);
                ImageScrollViewer.ScrollToVerticalOffset(ImageScrollViewer.VerticalOffset + deltaY);

                _lastMousePos = currentPos;
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

        private void ImageScrollViewer_PreviewMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // 右键抬起不处理拖动结束，由PreviewMouseUp处理
        }

        private void SaveImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Filter = "BMP Files (*.bmp)|*.bmp|PNG Files (*.png)|*.png|JPEG Files (*.jpg)|*.jpg",
                    Title = "保存图片",
                    FileName = $"saved_image_{DateTime.Now:yyyyMMdd_HHmmss}.bmp"
                };

                if (dlg.ShowDialog() == true)
                {
                    // 获取当前显示的Bitmap
                    if (ImageDisplay.Source is System.Windows.Media.Imaging.BitmapSource bitmapSource)
                    {
                        BitmapEncoder encoder;
                        string ext = System.IO.Path.GetExtension(dlg.FileName).ToLower();
                        if (ext == ".png")
                            encoder = new PngBitmapEncoder();
                        else if (ext == ".jpg" || ext == ".jpeg")
                            encoder = new JpegBitmapEncoder();
                        else
                            encoder = new BmpBitmapEncoder();

                        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                        using var stream = System.IO.File.Create(dlg.FileName);
                        encoder.Save(stream);
                        Log($"[INFO] 图片已保存: {dlg.FileName}");
                    }
                    else
                    {
                        Log("[WARN] 无法获取当前图片");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] 保存图片失败: {ex.Message}");
                MessageBox.Show($"保存图片失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ================================================================
        // PLC 通信
        // ================================================================

        private void BtnPlcConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string ip = TxtPlcIp.Text.Trim();
                if (!int.TryParse(TxtPlcPort.Text.Trim(), out int port) || port <= 0 || port > 65535)
                {
                    MessageBox.Show("Please enter a valid port (1-65535).", "Invalid Port", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (string.IsNullOrEmpty(ip))
                {
                    MessageBox.Show("Please enter a valid IP address.", "Invalid IP", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Log($"[PLC] Connecting to {ip}:{port}...");

                // 关闭旧连接
                if (_plc != null)
                {
                    if (_plcConnected) _plc.ConnectClose();
                    _plc = null;
                    _plcConnected = false;
                }

                // 创建信捷PLC连接
                _plc = new XinJETcpNet();
                _plc.IpAddress = ip;
                _plc.Port = port;

                var result = _plc.ConnectServer();
                if (result.IsSuccess)
                {
                    _plcConnected = true;
                    Log($"[PLC] Connected successfully to {ip}:{port}");

                    // 更新UI
                    BtnPlcConnect.IsEnabled = false;
                    BtnPlcDisconnect.IsEnabled = true;
                    TxtPlcIp.IsEnabled = false;
                    TxtPlcPort.IsEnabled = false;
                    TxtPlcStatus.Text = "[Connected]";
                    TxtPlcStatus.Foreground = new SolidColorBrush(Colors.Green);
                }
                else
                {
                    _plcConnected = false;
                    _plc = null;
                    Log($"[PLC] Connection failed: {result.Message}");
                    MessageBox.Show($"PLC connection failed!\n\nError: {result.Message}",
                        "PLC Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _plcConnected = false;
                _plc = null;
                Log($"[PLC] Connection error: {ex.Message}");
                MessageBox.Show($"PLC connection error:\n{ex.Message}", "PLC Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnPlcDisconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[PLC] Disconnecting...");

                if (_plc != null && _plcConnected)
                {
                    _plc.ConnectClose();
                    Log("[PLC] Connection closed");
                }

                _plc = null;
                _plcConnected = false;

                // 恢复UI
                BtnPlcConnect.IsEnabled = true;
                BtnPlcDisconnect.IsEnabled = false;
                TxtPlcIp.IsEnabled = true;
                TxtPlcPort.IsEnabled = true;
                TxtPlcStatus.Text = "[Disconnected]";
                TxtPlcStatus.Foreground = new SolidColorBrush(Colors.Gray);

                Log("[PLC] Disconnected");
            }
            catch (Exception ex)
            {
                Log($"[PLC] Disconnect error: {ex.Message}");
                MessageBox.Show($"PLC disconnect error:\n{ex.Message}", "PLC Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}