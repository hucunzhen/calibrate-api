using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WpfPath = System.Windows.Shapes.Path;
using IoPath = System.IO.Path;

namespace CalibOperatorCLI_Example
{
    public partial class HistogramPage : Page
    {
        private WriteableBitmap _sourceBitmap;
        private byte[] _grayPixels;
        private int _imgWidth;
        private int _imgHeight;

        // Histogram data
        private int[] _histogram = new int[256];
        private int _totalPixels;

        // Stats
        private double _mean;
        private double _stdDev;
        private int _minVal;
        private int _maxVal;
        private double _median;
        private int _otsuThreshold;

        // Display settings
        private bool _logScale = false;
        private bool _showCumulative = false;
        private bool _showStats = true;


        // ROI
        private bool _roiMode = false;
        private bool _isDraggingRoi = false;
        private Point _roiStart;
        private Rect _roiRect;
        private bool _hasRoi = false;

        // Range selection
        private int _rangeMin = 0;
        private int _rangeMax = 255;

        // Hover tooltip
        private TextBlock _tooltip;

        public HistogramPage()
        {
            InitializeComponent();
            _tooltip = new TextBlock
            {
                Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x33, 0x33, 0x33)),
                Foreground = Brushes.White,
                Padding = new Thickness(6, 3, 6, 3),
                FontSize = 12,
                Visibility = Visibility.Collapsed
            };
            Loaded += (s, e) =>
            {
                if (_grayPixels != null)
                    DrawHistogram();
            };
        }

        // ───────── Load Image ─────────
        private void BtnLoadImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|All Files|*.*",
                Title = "Select Grayscale Image"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                LoadImage(dlg.FileName);
                StatusText.Text = $"Loaded: {IoPath.GetFileName(dlg.FileName)} ({_imgWidth}x{_imgHeight})";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load image:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadImage(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();

            int w = bmp.PixelWidth;
            int h = bmp.PixelHeight;
            int stride = (w * bmp.Format.BitsPerPixel + 7) / 8;

            var rawPixels = new byte[stride * h];
            bmp.CopyPixels(rawPixels, stride, 0);

            // Convert to 8-bit gray
            _imgWidth = w;
            _imgHeight = h;
            _grayPixels = new byte[w * h];

            if (bmp.Format == PixelFormats.Gray8)
            {
                Array.Copy(rawPixels, _grayPixels, w * h);
            }
            else if (bmp.Format == PixelFormats.Rgb24 || bmp.Format == PixelFormats.Bgr24)
            {
                for (int i = 0; i < w * h; i++)
                    _grayPixels[i] = (byte)(0.299 * rawPixels[i * 3 + 2] + 0.587 * rawPixels[i * 3 + 1] + 0.114 * rawPixels[i * 3]);
            }
            else
            {
                var converted = new FormatConvertedBitmap(bmp, PixelFormats.Gray8, null, 0);
                stride = (w * 8 + 7) / 8;
                var grayRaw = new byte[stride * h];
                converted.CopyPixels(grayRaw, stride, 0);
                Array.Copy(grayRaw, _grayPixels, w * h);
            }

            // Display grayscale
            _sourceBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Gray8, null);
            _sourceBitmap.WritePixels(new Int32Rect(0, 0, w, h), _grayPixels, w, 0);
            UpdateImagePreview();

            // Reset ROI
            _hasRoi = false;
            RoiRect.Visibility = Visibility.Collapsed;

            // Compute
            ComputeHistogram();
            DrawHistogram();
        }

        // ───────── Histogram Computation ─────────
        private void ComputeHistogram()
        {
            Array.Clear(_histogram, 0, 256);
            _totalPixels = 0;
            long sum = 0;
            long sumSq = 0;
            _minVal = 255;
            _maxVal = 0;

            int x0 = 0, y0 = 0, x1 = _imgWidth, y1 = _imgHeight;
            if (_hasRoi)
            {
                x0 = Math.Max(0, (int)_roiRect.X);
                y0 = Math.Max(0, (int)_roiRect.Y);
                x1 = Math.Min(_imgWidth, (int)(_roiRect.X + _roiRect.Width));
                y1 = Math.Min(_imgHeight, (int)(_roiRect.Y + _roiRect.Height));
            }

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    byte v = _grayPixels[y * _imgWidth + x];
                    _histogram[v]++;
                    _totalPixels++;
                    sum += v;
                    sumSq += (long)v * v;
                    if (v < _minVal) _minVal = v;
                    if (v > _maxVal) _maxVal = v;
                }
            }

            _mean = _totalPixels > 0 ? (double)sum / _totalPixels : 0;
            _stdDev = _totalPixels > 0 ? Math.Sqrt((double)sumSq / _totalPixels - _mean * _mean) : 0;

            // Median
            int cumulative = 0;
            _median = 0;
            int half = _totalPixels / 2;
            for (int i = 0; i < 256; i++)
            {
                cumulative += _histogram[i];
                if (cumulative >= half)
                {
                    _median = i;
                    break;
                }
            }

            // Otsu
            _otsuThreshold = ComputeOtsu();

            // Update stats UI
            TxtPixelCount.Text = _totalPixels.ToString("N0");
            TxtMean.Text = _mean.ToString("F1");
            TxtStdDev.Text = _stdDev.ToString("F1");
            TxtMin.Text = _minVal.ToString();
            TxtMax.Text = _maxVal.ToString();
            TxtMedian.Text = _median.ToString();
            TxtOtsu.Text = _otsuThreshold.ToString();
        }

        private int ComputeOtsu()
        {
            if (_totalPixels == 0) return 0;

            double sum = 0;
            for (int i = 0; i < 256; i++)
                sum += i * (double)_histogram[i];

            double sumB = 0;
            int wB = 0;
            double maxVariance = 0;
            int threshold = 0;

            for (int t = 0; t < 256; t++)
            {
                wB += _histogram[t];
                if (wB == 0) continue;
                int wF = _totalPixels - wB;
                if (wF == 0) break;

                sumB += t * (double)_histogram[t];
                double mB = sumB / wB;
                double mF = (sum - sumB) / wF;
                double variance = (double)wB * wF * (mB - mF) * (mB - mF);

                if (variance > maxVariance)
                {
                    maxVariance = variance;
                    threshold = t;
                }
            }

            return threshold;
        }

        private bool _isDrawing = false;

        // ───────── Drawing ─────────
        private void DrawHistogram()
        {
            if (_isDrawing) return;
            if (!IsLoaded) return;
            var canvas = HistogramCanvas;
            if (canvas == null) return;

            double cw = canvas.ActualWidth;
            double ch = canvas.ActualHeight;
            if (cw < 10 || ch < 10)
            {
                // Canvas 尚未布局完成，延迟重绘
                Dispatcher.BeginInvoke(new Action(() => DrawHistogram()), System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }
            if (_grayPixels == null) return;

            _isDrawing = true;
            try
            {
                canvas.Children.Clear();

            const double margin = 45;
            const double topMargin = 15;
            const double rightMargin = 15;
            double plotW = cw - margin - rightMargin;
            double plotH = ch - topMargin - 30; // bottom margin for axis labels

            if (plotW <= 0 || plotH <= 0) return;

            // Background
            canvas.Children.Add(new Rectangle
            {
                Width = cw, Height = ch,
                Fill = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA))
            });

            // Grid lines
            for (int i = 0; i <= 4; i++)
            {
                double y = topMargin + plotH * i / 4;
                canvas.Children.Add(new Line
                {
                    X1 = margin, Y1 = y, X2 = margin + plotW, Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                    StrokeThickness = 1
                });
            }
            for (int i = 0; i <= 8; i++)
            {
                double x = margin + plotW * i / 8;
                canvas.Children.Add(new Line
                {
                    X1 = x, Y1 = topMargin, X2 = x, Y2 = topMargin + plotH,
                    Stroke = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                    StrokeThickness = 1
                });
            }

            // Compute Y-axis max
            int maxCount = _histogram.Max();
            if (maxCount == 0) maxCount = 1;

            double effectiveMax = _logScale ? Math.Log10(maxCount + 1) : maxCount;

            // Build path
            var pathGeo = new PathGeometry();
            var figure = new PathFigure();
            figure.StartPoint = new Point(margin, topMargin + plotH);

            for (int i = 0; i < 256; i++)
            {
                double x = margin + plotW * i / 255;
                double val = _logScale ? Math.Log10(_histogram[i] + 1) : _histogram[i];
                double y = topMargin + plotH - (val / effectiveMax) * plotH;
                y = Math.Max(topMargin, Math.Min(topMargin + plotH, y));
                figure.Segments.Add(new LineSegment(new Point(x, y), true));
            }
            figure.Segments.Add(new LineSegment(new Point(margin + plotW, topMargin + plotH), true));
            figure.IsClosed = true;
            pathGeo.Figures.Add(figure);

            canvas.Children.Add(new WpfPath
            {
                Data = pathGeo,
                Fill = new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x7A, 0xCC)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)),
                StrokeThickness = 1.5
            });

            // Cumulative distribution curve
            if (_showCumulative)
            {
                var cumGeo = new PathGeometry();
                var cumFig = new PathFigure();
                cumFig.StartPoint = new Point(margin, topMargin + plotH);

                int cum = 0;
                for (int i = 0; i < 256; i++)
                {
                    cum += _histogram[i];
                    double x = margin + plotW * i / 255;
                    double y = topMargin + plotH - ((double)cum / _totalPixels) * plotH;
                    cumFig.Segments.Add(new LineSegment(new Point(x, y), true));
                }
                cumGeo.Figures.Add(cumFig);

                canvas.Children.Add(new WpfPath
                {
                    Data = cumGeo,
                    Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x00)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 6, 3 }
                });
            }

            // Threshold lines
            if (ChkShowOtsu.IsChecked == true)
                DrawThresholdLine(canvas, _otsuThreshold, margin, topMargin, plotW, plotH, Colors.Red, $"Otsu={_otsuThreshold}");

            if (ChkShowMean.IsChecked == true)
                DrawThresholdLine(canvas, (int)Math.Round(_mean), margin, topMargin, plotW, plotH, Colors.Green, $"Mean={_mean:F1}");

            if (ChkShowCustom.IsChecked == true && int.TryParse(TxtCustomThreshold.Text, out int customVal) && customVal >= 0 && customVal <= 255)
                DrawThresholdLine(canvas, customVal, margin, topMargin, plotW, plotH, Colors.Purple, $"Custom={customVal}");

            // Range highlight on histogram
            if (ChkEnableRange.IsChecked == true)
            {
                double x1 = margin + plotW * _rangeMin / 255;
                double x2 = margin + plotW * _rangeMax / 255;
                canvas.Children.Add(new Rectangle
                {
                    Width = x2 - x1,
                    Height = plotH,
                    Fill = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xCC, 0x00)),
                    Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x99, 0x00)),
                    StrokeThickness = 1.5
                });
            }

            // X-axis labels
            for (int i = 0; i <= 255; i += 32)
            {
                double x = margin + plotW * i / 255;
                canvas.Children.Add(new TextBlock
                {
                    Text = i.ToString(),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                    RenderTransform = new TranslateTransform(x - 8, topMargin + plotH + 5)
                });
            }

            // Y-axis label
            canvas.Children.Add(new TextBlock
            {
                Text = _logScale ? "log10(count)" : "count",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                RenderTransform = new TranslateTransform(2, topMargin - 2)
            });

            // "Gray Value" x-axis title
            canvas.Children.Add(new TextBlock
            {
                Text = "Gray Value",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                RenderTransform = new TranslateTransform(margin + plotW / 2 - 30, topMargin + plotH + 18)
            });

            // Axes
            canvas.Children.Add(new Line
            {
                X1 = margin, Y1 = topMargin, X2 = margin, Y2 = topMargin + plotH,
                Stroke = Brushes.Black, StrokeThickness = 1
            });
            canvas.Children.Add(new Line
            {
                X1 = margin, Y1 = topMargin + plotH, X2 = margin + plotW, Y2 = topMargin + plotH,
                Stroke = Brushes.Black, StrokeThickness = 1
            });

            // Tooltip
            canvas.Children.Add(_tooltip);
            }
            finally
            {
                _isDrawing = false;
            }
        }

        // ───────── Image Preview with Range Highlight ─────────
        private void UpdateImagePreview()
        {
            if (_grayPixels == null || _imgWidth <= 0 || _imgHeight <= 0) return;

            bool highlight = IsLoaded && ChkEnableRange != null && ChkEnableRange.IsChecked == true;
            int w = _imgWidth;
            int h = _imgHeight;

            if (!highlight)
            {
                // Show original grayscale
                _sourceBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Gray8, null);
                _sourceBitmap.WritePixels(new Int32Rect(0, 0, w, h), _grayPixels, w, 0);
                ImagePreview.Source = _sourceBitmap;
                return;
            }

            // Build Bgr32 image: pixels in range → bright cyan overlay, others → dimmed grayscale
            int stride = w * 4;
            var bgrPixels = new byte[stride * h];
            int rangeMin = _rangeMin;
            int rangeMax = _rangeMax;

            for (int i = 0; i < w * h; i++)
            {
                byte g = _grayPixels[i];
                int idx = i * 4;
                if (g >= rangeMin && g <= rangeMax)
                {
                    // In range: bright cyan-ish highlight, keep some original brightness
                    bgrPixels[idx]     = (byte)(g * 0.3);           // B
                    bgrPixels[idx + 1] = (byte)(g * 0.5 + 128);    // G (enhanced)
                    bgrPixels[idx + 2] = (byte)(g * 0.2 + 60);     // R
                    bgrPixels[idx + 3] = 255;
                }
                else
                {
                    // Out of range: dimmed grayscale
                    bgrPixels[idx]     = (byte)(g * 0.4);
                    bgrPixels[idx + 1] = (byte)(g * 0.4);
                    bgrPixels[idx + 2] = (byte)(g * 0.4);
                    bgrPixels[idx + 3] = 255;
                }
            }

            var highlightBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            highlightBitmap.WritePixels(new Int32Rect(0, 0, w, h), bgrPixels, stride, 0);
            ImagePreview.Source = highlightBitmap;
        }

        private void DrawThresholdLine(Canvas canvas, int threshold, double margin, double topMargin, double plotW, double plotH, Color color, string label)
        {
            double x = margin + plotW * threshold / 255;
            canvas.Children.Add(new Line
            {
                X1 = x, Y1 = topMargin, X2 = x, Y2 = topMargin + plotH,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 8, 4 }
            });
            canvas.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color),
                RenderTransform = new TranslateTransform(x + 4, topMargin + 2)
            });
        }

        // ───────── UI Events ─────────
        private void ChkThreshold_Click(object sender, RoutedEventArgs e) { if (IsLoaded && _grayPixels != null) DrawHistogram(); }
        private void TxtCustomThreshold_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded && _grayPixels != null) DrawHistogram(); }
        private void ChkLogScale_Click(object sender, RoutedEventArgs e) { _logScale = ChkLogScale.IsChecked == true; DrawHistogram(); }
        private void ChkShowCumulative_Click(object sender, RoutedEventArgs e) { _showCumulative = ChkShowCumulative.IsChecked == true; DrawHistogram(); }
        private void ChkShowStats_Click(object sender, RoutedEventArgs e)
        {
            _showStats = ChkShowStats.IsChecked == true;
            StatsPanel.Visibility = _showStats ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ModeChanged_Click(object sender, RoutedEventArgs e)
        {
            _roiMode = RbROI.IsChecked == true;
            RoiRect.Visibility = (_roiMode && _hasRoi) ? Visibility.Visible : Visibility.Collapsed;
            if (_grayPixels != null)
            {
                ComputeHistogram();
                DrawHistogram();
            }
        }

        private void RangeChanged_Click(object sender, RoutedEventArgs e)
        {
            // 页面初始化阶段或无图片时不处理
            if (!IsLoaded || _histogram == null || _totalPixels == 0) return;

            if (ChkEnableRange.IsChecked == true)
            {
                if (int.TryParse(TxtRangeMin.Text, out int rmin)) _rangeMin = Math.Max(0, Math.Min(255, rmin));
                if (int.TryParse(TxtRangeMax.Text, out int rmax)) _rangeMax = Math.Max(0, Math.Min(255, rmax));
                if (_rangeMin > _rangeMax)
                {
                    int tmp = _rangeMin; _rangeMin = _rangeMax; _rangeMax = tmp;
                    TxtRangeMin.Text = _rangeMin.ToString();
                    TxtRangeMax.Text = _rangeMax.ToString();
                }
                int rangePixels = 0;
                for (int i = _rangeMin; i <= _rangeMax; i++) rangePixels += _histogram[i];
                double pct = _totalPixels > 0 ? 100.0 * rangePixels / _totalPixels : 0;
                TxtRangeInfo.Text = $"[{_rangeMin},{_rangeMax}] {rangePixels:N0}px ({pct:F1}%)";
            }
            else
            {
                TxtRangeInfo.Text = "";
            }
            DrawHistogram();
            UpdateImagePreview();
        }

        // ───────── ROI Selection ─────────
        private void RoiRect_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_roiMode) return;
            _isDraggingRoi = true;
            _roiStart = e.GetPosition(ImagePreview);
            RoiRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(RoiRect, _roiStart.X);
            Canvas.SetTop(RoiRect, _roiStart.Y);
            RoiRect.Width = 0;
            RoiRect.Height = 0;
            ((UIElement)e.Source).CaptureMouse();
        }

        private void RoiRect_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingRoi) return;
            var pos = e.GetPosition(ImagePreview);
            double x = Math.Min(_roiStart.X, pos.X);
            double y = Math.Min(_roiStart.Y, pos.Y);
            double w = Math.Abs(pos.X - _roiStart.X);
            double h = Math.Abs(pos.Y - _roiStart.Y);
            Canvas.SetLeft(RoiRect, x);
            Canvas.SetTop(RoiRect, y);
            RoiRect.Width = w;
            RoiRect.Height = h;
        }

        private void RoiRect_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingRoi) return;
            _isDraggingRoi = false;
            ((UIElement)e.Source).ReleaseMouseCapture();

            double x = (double)Canvas.GetLeft(RoiRect);
            double y = (double)Canvas.GetTop(RoiRect);
            double w = RoiRect.Width;
            double h = RoiRect.Height;

            // Map from image display coordinates to actual pixel coordinates
            if (ImagePreview.Source != null)
            {
                double displayW = ImagePreview.ActualWidth;
                double displayH = ImagePreview.ActualHeight;
                double scaleX = _imgWidth / displayW;
                double scaleY = _imgHeight / displayH;
                _roiRect = new Rect(x * scaleX, y * scaleY, w * scaleX, h * scaleY);
                _hasRoi = (w > 5 && h > 5);

                if (_hasRoi && _grayPixels != null)
                {
                    ComputeHistogram();
                    DrawHistogram();
                    StatusText.Text = $"ROI: ({(int)_roiRect.X},{(int)_roiRect.Y}) {(int)_roiRect.Width}x{(int)_roiRect.Height} | {_totalPixels:N0} pixels";
                }
            }
        }

        // ───────── Mouse Hover Tooltip ─────────
        private void HistogramCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_grayPixels == null) return;

            var pos = e.GetPosition(HistogramCanvas);
            const double margin = 45;
            const double topMargin = 15;
            const double rightMargin = 15;
            double plotW = HistogramCanvas.ActualWidth - margin - rightMargin;
            double plotH = HistogramCanvas.ActualHeight - topMargin - 30;

            if (pos.X < margin || pos.X > margin + plotW || pos.Y < topMargin || pos.Y > topMargin + plotH)
            {
                _tooltip.Visibility = Visibility.Collapsed;
                return;
            }

            int grayVal = (int)Math.Round((pos.X - margin) / plotW * 255);
            grayVal = Math.Max(0, Math.Min(255, grayVal));

            double pct = _totalPixels > 0 ? 100.0 * _histogram[grayVal] / _totalPixels : 0;
            int cum = 0;
            for (int i = 0; i <= grayVal; i++) cum += _histogram[i];
            double cumPct = _totalPixels > 0 ? 100.0 * cum / _totalPixels : 0;

            _tooltip.Text = $"Gray={grayVal}  Count={_histogram[grayVal]:N0} ({pct:F2}%)  Cum={cumPct:F1}%";
            _tooltip.Visibility = Visibility.Visible;

            double tx = pos.X + 12;
            double ty = pos.Y - 25;
            if (tx + 280 > HistogramCanvas.ActualWidth) tx = pos.X - 280;
            if (ty < 0) ty = pos.Y + 12;
            _tooltip.RenderTransform = new TranslateTransform(tx, ty);
        }

        private void HistogramCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            _tooltip.Visibility = Visibility.Collapsed;
        }

        // Redraw when canvas size changes
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (sizeInfo.WidthChanged || sizeInfo.HeightChanged)
            {
                if (_grayPixels != null && HistogramCanvas.ActualWidth > 10 && HistogramCanvas.ActualHeight > 10)
                    DrawHistogram();
            }
        }
    }
}
