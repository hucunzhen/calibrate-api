using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    public partial class MainWindow : Window
    {
        private CalibImage? _currentImage;
        private Point2D[]? _detectedPoints;
        private CalibrationResult? _calibrationResult;
        private int _currentStep = 0;
        private StreamWriter? _logWriter;

        private const int IMAGE_WIDTH = 2448;
        private const int IMAGE_HEIGHT = 2048;

        private readonly Point2D[] _worldPoints = new[]
        {
            new Point2D(100, 100), new Point2D(400, 100), new Point2D(700, 100),
            new Point2D(100, 300), new Point2D(400, 300), new Point2D(700, 300),
            new Point2D(100, 500), new Point2D(400, 500), new Point2D(700, 500)
        };

        public MainWindow()
        {
            InitializeComponent();
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
            BtnAutoTrajTest.IsEnabled = _calibrationResult != null && _calibrationResult.Success;
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

        private void BtnTestImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[INFO] Button 7: Test Image - START");

                var dlg = new OpenFileDialog
                {
                    Filter = "BMP Files (*.bmp)|*.bmp|All Files (*.*)|*.*",
                    Title = "Select Test Image"
                };

                if (dlg.ShowDialog() == true)
                {
                    _currentImage?.Dispose();
                    _currentImage = CalibAPI.LoadImage(dlg.FileName);

                    Log($"[INFO] Test image loaded: {_currentImage.Width}x{_currentImage.Height}");
                    DisplayImage(_currentImage);
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Failed to load test image: {ex.Message}");
                MessageBox.Show($"Failed to load test image:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAutoTrajTest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[INFO] Button 8: Auto Trajectory Test - START");

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

                if (_calibrationResult == null || !_calibrationResult.Success)
                {
                    Log("[ERROR] Calibration required before trajectory detection");
                    return;
                }

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
            _currentImage?.Dispose();
            _logWriter?.Close();
        }
    }
}