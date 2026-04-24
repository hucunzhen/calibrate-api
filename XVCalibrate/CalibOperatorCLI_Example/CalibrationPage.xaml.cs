using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MvCameraControl;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    public partial class CalibrationPage : Page
    {
        private CalibImage? _currentImage;
        private Point2D[]? _detectedPoints;
        private CalibrationResult? _calibrationResult;
        private int _currentStep = 0;

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

        public CalibrationPage()
        {
            InitializeComponent();
            _imageScaleTransform = ImageScaleTransform;
            BtnGrabImage.IsEnabled = false;
            Log("Calibration Page Loaded");
            UpdateButtonStates();
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

        private void UpdateButtonStates()
        {
            BtnDetectCircles.IsEnabled = _currentImage != null;
            BtnCalibrate.IsEnabled = _detectedPoints != null && _detectedPoints.Length >= 4;
            BtnSaveResults.IsEnabled = _calibrationResult != null && _calibrationResult.Success;
            BtnTestTransform.IsEnabled = _calibrationResult != null && _calibrationResult.Success;
            BtnAutoTrajTest.IsEnabled = true;
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
            catch (Exception ex)
            {
                Log($"[ERROR] DisplayImage: {ex.Message}");
            }
        }

        private void BtnGenerateImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[INFO] Generate Image - START");
                _currentImage?.Dispose();
                _currentImage = CalibAPI.CreateCalibrationImage(IMAGE_WIDTH, IMAGE_HEIGHT);
                Log($"[INFO] Image: {_currentImage.Width}x{_currentImage.Height}");

                string savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "calibration_9points.bmp");
                CalibAPI.SaveImage(savePath, _currentImage);
                Log($"[INFO] Saved: {savePath}");
                DisplayImage(_currentImage);

                _currentStep = 1;
                _detectedPoints = null;
                _calibrationResult = null;
                UpdateButtonStates();
            }
            catch (Exception ex)
            {
                Log($"[ERROR] {ex.Message}");
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnLoadImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "BMP Files (*.bmp)|*.bmp|All Files (*.*)|*.*",
                    Title = "Select Calibration Image"
                };
                if (dlg.ShowDialog() == true)
                {
                    _currentImage?.Dispose();
                    _currentImage = CalibAPI.LoadImage(dlg.FileName);
                    Log($"[INFO] Loaded: {_currentImage.Width}x{_currentImage.Height} - {dlg.FileName}");
                    DisplayImage(_currentImage);
                    _currentStep = 2;
                    _detectedPoints = null;
                    _calibrationResult = null;
                    UpdateButtonStates();
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] {ex.Message}");
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnConnectCamera_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var camera = MainWindow.Camera;
                if (camera == null)
                {
                    MessageBox.Show("相机 SDK 未初始化", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (camera.IsConnected)
                {
                    // 断开
                    camera.Disconnect();
                    BtnConnectCamera.Content = "🔌 Connect";
                    BtnGrabImage.IsEnabled = false;
                    Log("[CAMERA] Disconnected");
                    return;
                }

                // 枚举设备
                var devices = CameraService.EnumDevices();
                if (devices.Count == 0)
                {
                    MessageBox.Show("未发现相机设备", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 显示设备列表让用户选择
                string[] deviceNames = new string[devices.Count];
                for (int i = 0; i < devices.Count; i++)
                {
                    string name = $"{i}: {devices[i].ModelName}";
                    if (devices[i] is IGigEDeviceInfo gigeInfo)
                    {
                        uint ip = gigeInfo.CurrentIp;
                        name += $" ({(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF})";
                    }
                    deviceNames[i] = name;
                }

                // 简单处理：只有一个设备直接连接，多个设备用第一个
                if (devices.Count == 1)
                {
                    Log($"[CAMERA] Found: {deviceNames[0]}");
                }
                else
                {
                    Log($"[CAMERA] Found {devices.Count} devices, connecting to first...");
                    for (int i = 0; i < devices.Count; i++)
                        Log($"  {deviceNames[i]}");
                }

                bool ok = camera.ConnectByIndex(0);
                if (!ok)
                {
                    MessageBox.Show("连接相机失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                BtnConnectCamera.Content = "🔌 Disconnect";
                BtnGrabImage.IsEnabled = true;
                Log("[CAMERA] Connected successfully");
            }
            catch (Exception ex)
            {
                Log($"[CAMERA ERROR] {ex.Message}");
                MessageBox.Show($"连接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnGrabImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var camera = MainWindow.Camera;
                if (camera == null || !camera.IsConnected)
                {
                    MessageBox.Show("请先连接相机", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Log("[CAMERA] Grabbing...");

                var img = camera.GrabOneFrame(IMAGE_WIDTH, IMAGE_HEIGHT);
                if (img == null)
                {
                    string errDetail = camera.LastError ?? "timeout or null frame";
                    Log($"[CAMERA ERROR] Failed to grab frame: {errDetail}");
                    MessageBox.Show($"采图失败: {errDetail}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _currentImage?.Dispose();
                _currentImage = img;
                Log($"[CAMERA] Grabbed: {img.Width}x{img.Height}");
                DisplayImage(_currentImage);

                _currentStep = 2;
                _detectedPoints = null;
                _calibrationResult = null;
                UpdateButtonStates();
            }
            catch (Exception ex)
            {
                Log($"[CAMERA ERROR] {ex.Message}");
                MessageBox.Show($"采图异常: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDetectCircles_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[INFO] Detect Circles - START");
                if (_currentImage == null) return;

                _detectedPoints = CalibAPI.DetectCircles(_currentImage);
                if (_detectedPoints != null && _detectedPoints.Length > 0)
                {
                    Log($"[INFO] Detected {_detectedPoints.Length} circles");
                    for (int i = 0; i < Math.Min(9, _detectedPoints.Length); i++)
                        Log($"  Point {i + 1}: ({_detectedPoints[i].X:F2}, {_detectedPoints[i].Y:F2})");

                    CalibAPI.DrawDetectedCircles(_currentImage, _detectedPoints, 255);
                    DisplayImage(_currentImage);
                    _currentStep = 3;
                }
                else
                {
                    Log("[WARN] No circles detected");
                    MessageBox.Show("No circles detected.", "Result", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                UpdateButtonStates();
            }
            catch (Exception ex)
            {
                Log($"[ERROR] {ex.Message}");
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCalibrate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[INFO] Calibrate - START");
                if (_detectedPoints == null || _detectedPoints.Length < 4)
                {
                    MessageBox.Show("Need at least 4 circles.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Point2D[] imagePoints = _detectedPoints;
                Point2D[] worldPts;

                if (_detectedPoints.Length == 9)
                {
                    worldPts = _worldPoints;
                }
                else
                {
                    worldPts = new Point2D[_detectedPoints.Length];
                    double stepX = 600.0 / 2;
                    double stepY = 400.0 / 2;
                    for (int i = 0; i < worldPts.Length; i++)
                    {
                        int row = i / 3, col = i % 3;
                        worldPts[i] = new Point2D(100 + col * stepX, 100 + row * stepY);
                    }
                }

                _calibrationResult = CalibAPI.CalibrateNinePoint(imagePoints, worldPts);
                if (_calibrationResult.Success)
                {
                    Log("[INFO] Calibration SUCCESS!");
                    Log($"  X = {_calibrationResult.Transform.A:F6}*x + {_calibrationResult.Transform.B:F6}*y + {_calibrationResult.Transform.C:F6}");
                    Log($"  Y = {_calibrationResult.Transform.D:F6}*x + {_calibrationResult.Transform.E:F6}*y + {_calibrationResult.Transform.F:F6}");
                    Log($"  Avg Error: {_calibrationResult.AverageError:F4}, Max Error: {_calibrationResult.MaxError:F4}");
                    CalibAPI.SetTransform(_calibrationResult.Transform);
                    _currentStep = 4;
                }
                else
                {
                    Log($"[ERROR] Calibration FAILED: {_calibrationResult.ErrorMessage}");
                    MessageBox.Show(_calibrationResult.ErrorMessage, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                UpdateButtonStates();
            }
            catch (Exception ex)
            {
                Log($"[ERROR] {ex.Message}");
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSaveResults_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_calibrationResult == null || !_calibrationResult.Success) return;

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
                    writer.WriteLine($"  X = {_calibrationResult.Transform.A:F8}*x + {_calibrationResult.Transform.B:F8}*y + {_calibrationResult.Transform.C:F8}");
                    writer.WriteLine($"  Y = {_calibrationResult.Transform.D:F8}*x + {_calibrationResult.Transform.E:F8}*y + {_calibrationResult.Transform.F:F8}");
                    writer.WriteLine($"Average Error: {_calibrationResult.AverageError:F6}");
                    writer.WriteLine($"Max Error: {_calibrationResult.MaxError:F6}");
                    Log($"[INFO] Saved: {dlg.FileName}");
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] {ex.Message}");
            }
        }

        private void BtnTestTransform_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_calibrationResult == null || !_calibrationResult.Success) return;

                var testPoints = new[] { new Point2D(400, 300), new Point2D(100, 100), new Point2D(700, 500) };
                Log("[INFO] Test Transform:");
                foreach (var pixel in testPoints)
                {
                    var world = CalibAPI.ImageToWorld(pixel, _calibrationResult.Transform);
                    Log($"  Pixel ({pixel.X:F1}, {pixel.Y:F1}) -> World ({world.X:F4}, {world.Y:F4}) mm");
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] {ex.Message}");
            }
        }

        private void BtnAutoTrajTest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[INFO] Auto Trajectory Test - START");
                var dlg = new OpenFileDialog
                {
                    Filter = "BMP Files (*.bmp)|*.bmp|All Files (*.*)|*.*",
                    Title = "Select Image for Trajectory Detection"
                };
                if (dlg.ShowDialog() != true) return;

                _currentImage?.Dispose();
                _currentImage = CalibAPI.LoadImage(dlg.FileName);
                Log($"[INFO] Loaded: {_currentImage.Width}x{_currentImage.Height}");
                DisplayImage(_currentImage);

                Log("[INFO] Detecting trajectory...");
                var result = CalibAPI.DetectTrajectoryOpenCV(_currentImage, true, 0, 0.0);
                if (result.Success)
                {
                    Log($"[INFO] SUCCESS! Found {result.Count} points");
                    if (result.Points != null && result.Points.Length > 0)
                    {
                        using var blankImage = new CalibImage(_currentImage.Width, _currentImage.Height, 1);
                        CalibAPI.DrawTrajectoryGrayscale(blankImage, result.Points, 255);
                        DisplayImage(blankImage);
                        string savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trajectory_result.bmp");
                        CalibAPI.SaveImage(savePath, blankImage);
                        Log($"[INFO] Saved: {savePath}");
                    }
                }
                else
                {
                    Log($"[WARN] {result.ErrorMessage ?? "No trajectory found"}");
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] {ex.Message}");
            }
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
                if (dlg.ShowDialog() == true)
                {
                    if (ImageDisplay.Source is BitmapSource bmpSrc)
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
            }
            catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        }
    }
}
