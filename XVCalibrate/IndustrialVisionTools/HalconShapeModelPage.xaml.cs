using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CalibOperatorPInvoke;
using Microsoft.Win32;
#if HALCON_ENABLED
using HalconDotNet;
#endif
using IoPath = System.IO.Path;

namespace CalibOperatorCLI_Example
{
    public partial class HalconShapeModelPage : Page
    {
        private CalibImage? _currentImage;
        private byte[]? _grayPixels;
        private WriteableBitmap? _thresholdOverlay;
        private int _imgWidth;
        private int _imgHeight;

        // ROI 状态（坐标均为图像像素系）
        private bool _isDrawing;
        private Point _drawStartImage;
        private readonly List<Point> _polygonPointsImage = new List<Point>();
        private bool _hasRoi;
        private Rect _roiRectImage;

        // 环形 ROI：外圈 + 内圈两条多边形；_ringPolygonPhase 0=绘外圈 1=绘内圈 2=完成
        private bool _hasRingRoi;
        private readonly List<Point> _ringOuterPolygon = new List<Point>();
        private readonly List<Point> _ringInnerPolygon = new List<Point>();
        private int _ringPolygonPhase;
        private const double RingPolygonCloseDistance = 15;

        // 模型
        private long _modelId = -1;

        /// <summary>预览/修剪后的轮廓，创建 XLD 模型时优先使用。</summary>
        private HalconXldContourBundle? _workingContours;

        // 缩放/拖动状态
        private double _scale = 1.0;
        private double _offsetX = 0;
        private double _offsetY = 0;
        private bool _isPanning;
        private Point _panStart;
        private Point _lastPanOffset;
        private const double MinScale = 0.1;
        private const double MaxScale = 10.0;

        private static readonly Brush[] FindMatchBrushes =
        {
            Brushes.Lime,
            Brushes.Cyan,
            Brushes.Orange,
            Brushes.Magenta,
            Brushes.Gold
        };

        private readonly ScaleTransform _viewScale = new ScaleTransform();
        private readonly TranslateTransform _viewTranslate = new TranslateTransform();

        public HalconShapeModelPage()
        {
            InitializeComponent();
            ImageCanvas.RenderTransform = new TransformGroup
            {
                Children = new TransformCollection { _viewScale, _viewTranslate }
            };
            ImageCanvas.RenderTransformOrigin = new Point(0, 0);
            Loaded += (_, _) =>
            {
                UpdateCreatePanelsVisibility();
                if (_imgWidth > 0)
                    FitImageToView();
            };
            Unloaded += (_, _) => DisposeCurrentModel();
        }

        private void AppendLog(string msg)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            TxtLog.AppendText($"[{ts}] {msg}\r\n");
            TxtLog.ScrollToEnd();
        }

        // ───────── 加载图像 ─────────
        private void BtnLoadImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "图像|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|所有文件 (*.*)|*.*",
                Title = "选择图像"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                LoadImage(dlg.FileName);
                AppendLog($"已加载: {IoPath.GetFileName(dlg.FileName)} ({_imgWidth}x{_imgHeight})");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载图像失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadImage(string path)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            int w = bitmap.PixelWidth;
            int h = bitmap.PixelHeight;
            int stride = (w * bitmap.Format.BitsPerPixel + 7) / 8;
            var rawPixels = new byte[stride * h];
            bitmap.CopyPixels(rawPixels, stride, 0);

            // 转灰度
            _grayPixels = new byte[w * h];
            if (bitmap.Format == PixelFormats.Gray8)
            {
                Array.Copy(rawPixels, _grayPixels, w * h);
            }
            else if (bitmap.Format == PixelFormats.Rgb24 || bitmap.Format == PixelFormats.Bgr24)
            {
                bool bgr = bitmap.Format == PixelFormats.Bgr24;
                for (int i = 0; i < w * h; i++)
                {
                    int r, g, b;
                    if (bgr)
                    {
                        b = rawPixels[i * 3];
                        g = rawPixels[i * 3 + 1];
                        r = rawPixels[i * 3 + 2];
                    }
                    else
                    {
                        r = rawPixels[i * 3];
                        g = rawPixels[i * 3 + 1];
                        b = rawPixels[i * 3 + 2];
                    }
                    _grayPixels[i] = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
                }
            }
            else
            {
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
                stride = (w * 8 + 7) / 8;
                var grayRaw = new byte[stride * h];
                converted.CopyPixels(grayRaw, stride, 0);
                Array.Copy(grayRaw, _grayPixels, w * h);
            }

            _imgWidth = w;
            _imgHeight = h;

            // 创建 CalibImage
            _currentImage?.Dispose();
            _currentImage = new CalibImage(w, h, 1);
            var n = _currentImage.GetNativeStruct();
            System.Runtime.InteropServices.Marshal.Copy(_grayPixels, 0, n.data, w * h);

            // 显示
            DisplayImage.Source = bitmap;
            DisplayImage.Width = w;
            DisplayImage.Height = h;
            ImageCanvas.Width = w;
            ImageCanvas.Height = h;

            // 重置 ROI，再自适应显示
            ClearRoiState();
            _workingContours = null;
            UpdateContourStats();
            FitImageToView();
        }

        /// <summary>鼠标在画布上的位置即图像像素坐标（画布与图像 1:1，变换只施加在 ImageCanvas 整体）。</summary>
        private Point GetImagePointFromMouse(MouseEventArgs e)
        {
            var p = e.GetPosition(ImageCanvas);
            return new Point(
                Math.Max(0, Math.Min(_imgWidth - 1, p.X)),
                Math.Max(0, Math.Min(_imgHeight - 1, p.Y)));
        }

        private void ClampRoiToImage(ref double x, ref double y, ref double w, ref double h)
        {
            if (_imgWidth <= 0 || _imgHeight <= 0) return;
            x = Math.Max(0, Math.Min(x, _imgWidth - 1));
            y = Math.Max(0, Math.Min(y, _imgHeight - 1));
            w = Math.Max(1, Math.Min(w, _imgWidth - x));
            h = Math.Max(1, Math.Min(h, _imgHeight - y));
        }

        private void ResetView()
        {
            FitImageToView();
        }

        private void FitImageToView()
        {
            if (_imgWidth <= 0 || _imgHeight <= 0) return;

            var host = ViewHost;
            double viewW = host.ActualWidth > 1 ? host.ActualWidth : 800;
            double viewH = host.ActualHeight > 1 ? host.ActualHeight : 600;

            double fit = Math.Min(viewW / _imgWidth, viewH / _imgHeight) * 0.95;
            fit = Math.Max(MinScale, Math.Min(MaxScale, fit));
            _scale = fit;
            _offsetX = (viewW - _imgWidth * _scale) / 2;
            _offsetY = (viewH - _imgHeight * _scale) / 2;
            ApplyTransform();
            RefreshRoiVisuals();
            UpdateThresholdPreview();
        }

        private void ApplyTransform()
        {
            _viewScale.ScaleX = _scale;
            _viewScale.ScaleY = _scale;
            _viewTranslate.X = _offsetX;
            _viewTranslate.Y = _offsetY;
        }

        private void RefreshRoiVisuals()
        {
            if (RoiRect == null || PolygonLine == null) return;

            if (_hasRoi && RbRectMode?.IsChecked == true)
            {
                RoiRect.Visibility = Visibility.Visible;
                Canvas.SetLeft(RoiRect, _roiRectImage.X);
                Canvas.SetTop(RoiRect, _roiRectImage.Y);
                RoiRect.Width = _roiRectImage.Width;
                RoiRect.Height = _roiRectImage.Height;
            }
            else if (!_isDrawing)
                RoiRect.Visibility = Visibility.Collapsed;

            UpdatePolygonPreview();
            UpdateRingPreview();
        }

        private bool IsRingModeActive() => RbRingMode?.IsChecked == true;

        private bool HasUsableRingRoi() =>
            _hasRingRoi && IsRingModeActive() &&
            _ringOuterPolygon.Count >= 3 && _ringInnerPolygon.Count >= 3;

        private static double Dist(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private bool IsPointInRingRoi(double x, double y)
        {
            if (!HasUsableRingRoi())
                return false;
            return IsPointInPolygon(x, y, _ringOuterPolygon) &&
                   !IsPointInPolygon(x, y, _ringInnerPolygon);
        }

        private void ResetRingDrawState()
        {
            _hasRingRoi = false;
            _ringPolygonPhase = 0;
            _ringOuterPolygon.Clear();
            _ringInnerPolygon.Clear();
            if (RingOuterLine != null)
            {
                RingOuterLine.Visibility = Visibility.Collapsed;
                RingOuterLine.Points.Clear();
            }
            if (RingInnerLine != null)
            {
                RingInnerLine.Visibility = Visibility.Collapsed;
                RingInnerLine.Points.Clear();
            }
        }

        private static void UpdateRingPolyline(Polyline? line, IReadOnlyList<Point> points, bool closed)
        {
            if (line == null)
                return;
            if (points.Count < 2)
            {
                line.Visibility = Visibility.Collapsed;
                line.Points.Clear();
                return;
            }

            line.Visibility = Visibility.Visible;
            line.Points.Clear();
            foreach (var pt in points)
                line.Points.Add(pt);
            if (closed && points.Count >= 3)
                line.Points.Add(points[0]);
        }

        private void UpdateRingPreview()
        {
            if (!IsRingModeActive())
            {
                if (RingOuterLine != null) RingOuterLine.Visibility = Visibility.Collapsed;
                if (RingInnerLine != null) RingInnerLine.Visibility = Visibility.Collapsed;
                return;
            }

            bool outerClosed = _ringPolygonPhase >= 1;
            bool innerClosed = _ringPolygonPhase >= 2;
            UpdateRingPolyline(RingOuterLine, _ringOuterPolygon, outerClosed);
            UpdateRingPolyline(RingInnerLine, _ringInnerPolygon, innerClosed);
        }

#if HALCON_ENABLED
        private HObject? TryBuildRingRegion()
        {
            if (!HasUsableRingRoi())
                return null;
            var outer = _ringOuterPolygon.Select(p => new Point2D(p.X, p.Y)).ToList();
            var inner = _ringInnerPolygon.Select(p => new Point2D(p.X, p.Y)).ToList();
            return HalconFlowBridge.GenRegionRingFromPolygons(outer, inner);
        }
#endif

        // ───────── ROI 清除 ─────────
        private void BtnClearRoi_Click(object sender, RoutedEventArgs e)
        {
            ClearRoiState();
        }

        private void BtnResetView_Click(object sender, RoutedEventArgs e)
        {
            ResetView();
            AppendLog("视图已重置");
        }

        private void ClearRoiState()
        {
            _hasRoi = false;
            _isDrawing = false;
            _polygonPointsImage.Clear();
            ResetRingDrawState();

            try
            {
                if (RoiRect != null) RoiRect.Visibility = Visibility.Collapsed;
                if (PolygonLine != null)
                {
                    PolygonLine.Visibility = Visibility.Collapsed;
                    PolygonLine.Points.Clear();
                }
                if (ThresholdOverlayImage != null)
                    ThresholdOverlayImage.Visibility = Visibility.Collapsed;
                _thresholdOverlay = null;
                ClearFindResultOverlay();
                _workingContours = null;
                UpdateContourStats();
            }
            catch { /* 忽略 XAML 控件未初始化错误 */ }
        }

        private void UpdateContourStats()
        {
            if (TxtContourStats == null) return;
            if (_workingContours?.Contours == null || _workingContours.Contours.Count == 0)
            {
                TxtContourStats.Text = "轮廓: 未提取（请先预览）";
                return;
            }

            int n = _workingContours.Contours.Count;
            int pts = _workingContours.Contours.Sum(c => c?.Length ?? 0);
            TxtContourStats.Text = $"轮廓: {n} 条, 共 {pts} 点（将用于创建模型）";
        }

        private void SetWorkingContours(HalconXldContourBundle? bundle, bool trimmed = false)
        {
            _workingContours = bundle;
            UpdateContourStats();
            DrawXldPreviewOverlay(bundle, trimmed ? Brushes.Orange : Brushes.DeepSkyBlue);
        }

        private void ClearFindResultOverlay()
        {
            if (FindResultOverlay == null) return;
            FindResultOverlay.Children.Clear();
        }

#if HALCON_ENABLED
        private static Point TransformModelPointToImage(Point2D modelPt, double matchRow, double matchCol, double angleDeg)
        {
            double a = angleDeg * Math.PI / 180.0;
            double c = Math.Cos(a);
            double s = Math.Sin(a);
            double row = modelPt.Y;
            double col = modelPt.X;
            double rowImg = row * c - col * s + matchRow;
            double colImg = row * s + col * c + matchCol;
            return new Point(colImg, rowImg);
        }

        private void DrawFindResultOverlay(double[] rows, double[] cols, double[] angles, double[] scores, int[]? gridRow = null, int[]? gridCol = null)
        {
            if (FindResultOverlay == null || rows.Length == 0)
                return;

            ClearFindResultOverlay();

            Point2D[][] modelContours = Array.Empty<Point2D[]>();
            if (_modelId >= 0)
            {
                try
                {
                    modelContours = HalconFlowBridge.GetShapeModelContourPoints(_modelId, 1);
                }
                catch (Exception ex)
                {
                    AppendLog($"[提示] 无法读取模型轮廓，仅显示匹配中心: {ex.Message}");
                }
            }

            for (int m = 0; m < rows.Length; m++)
            {
                Brush brush = FindMatchBrushes[m % FindMatchBrushes.Length];
                double matchRow = rows[m];
                double matchCol = cols[m];
                double angleDeg = angles[m];

                foreach (Point2D[] contour in modelContours)
                {
                    if (contour == null || contour.Length < 2)
                        continue;

                    var pts = new PointCollection(contour.Length);
                    foreach (Point2D pt in contour)
                        pts.Add(TransformModelPointToImage(pt, matchRow, matchCol, angleDeg));

                    FindResultOverlay.Children.Add(new Polyline
                    {
                        Points = pts,
                        Stroke = brush,
                        StrokeThickness = 2.5,
                        Fill = Brushes.Transparent
                    });
                }

                const double crossHalf = 14;
                FindResultOverlay.Children.Add(new Line
                {
                    X1 = matchCol - crossHalf,
                    Y1 = matchRow,
                    X2 = matchCol + crossHalf,
                    Y2 = matchRow,
                    Stroke = brush,
                    StrokeThickness = 2
                });
                FindResultOverlay.Children.Add(new Line
                {
                    X1 = matchCol,
                    Y1 = matchRow - crossHalf,
                    X2 = matchCol,
                    Y2 = matchRow + crossHalf,
                    Stroke = brush,
                    StrokeThickness = 2
                });

                var labelParts = new List<string>();
                if (scores.Length > m)
                    labelParts.Add($"{scores[m]:F3}");
                if (gridRow != null && gridCol != null && m < gridRow.Length && m < gridCol.Length
                    && gridRow[m] >= 0 && gridCol[m] >= 0)
                    labelParts.Add($"[{gridRow[m]},{gridCol[m]}]");
                var label = new TextBlock
                {
                    Text = string.Join(" ", labelParts),
                    Foreground = brush,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0))
                };
                Canvas.SetLeft(label, matchCol + crossHalf + 4);
                Canvas.SetTop(label, matchRow - crossHalf);
                FindResultOverlay.Children.Add(label);
            }
        }
#endif

        private void DisposeCurrentModel()
        {
            ClearFindResultOverlay();
#if HALCON_ENABLED
            if (_modelId >= 0)
            {
                try { HalconFlowBridge.ClearShapeModel(_modelId); }
                catch { /* ignored */ }
                _modelId = -1;
            }
#else
            _modelId = -1;
#endif
        }

        private void DrawMode_Changed(object sender, RoutedEventArgs e)
        {
            ClearRoiState();
        }

        // ───────── 画布鼠标事件 ─────────
        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_imgWidth <= 0 || _imgHeight <= 0) return;

            var imgPt = GetImagePointFromMouse(e);

            if (RbRectMode.IsChecked == true)
            {
                _isDrawing = true;
                _drawStartImage = imgPt;
                if (RoiRect != null)
                {
                    RoiRect.Visibility = Visibility.Visible;
                    Canvas.SetLeft(RoiRect, imgPt.X);
                    Canvas.SetTop(RoiRect, imgPt.Y);
                    RoiRect.Width = 0;
                    RoiRect.Height = 0;
                }
            }
            else if (RbPolygonMode.IsChecked == true)
            {
                if (_polygonPointsImage.Count >= 3)
                {
                    var start = _polygonPointsImage[0];
                    double dist = Math.Sqrt((imgPt.X - start.X) * (imgPt.X - start.X) +
                                          (imgPt.Y - start.Y) * (imgPt.Y - start.Y));
                    if (dist < 15)
                    {
                        _hasRoi = true;
                        AppendLog($"多边形ROI闭合: {_polygonPointsImage.Count} 个点");
                        ViewHost.ReleaseMouseCapture();
                        UpdatePolygonPreview();
                        UpdateThresholdPreview();
                        return;
                    }
                }

                _polygonPointsImage.Add(imgPt);
                UpdatePolygonPreview();
                UpdateThresholdPreview();
            }
            else if (IsRingModeActive())
            {
                HandleRingMouseClick(imgPt);
            }

            ViewHost.CaptureMouse();
        }

        private void HandleRingMouseClick(Point imgPt)
        {
            if (_ringPolygonPhase == 0)
            {
                if (TryCloseOrAddPolygonPoint(_ringOuterPolygon, imgPt))
                {
                    _ringPolygonPhase = 1;
                    AppendLog($"环形 ROI：外圈已闭合（{_ringOuterPolygon.Count} 点），请绘制内圈");
                }
                else
                    AppendLog($"外圈：第 {_ringOuterPolygon.Count} 点");
                UpdateRingPreview();
                UpdateThresholdPreview();
                return;
            }

            if (_ringPolygonPhase == 1)
            {
                if (TryCloseOrAddPolygonPoint(_ringInnerPolygon, imgPt))
                {
                    _ringPolygonPhase = 2;
                    _hasRingRoi = true;
                    _hasRoi = true;
                    AppendLog($"环形 ROI 完成：外 {_ringOuterPolygon.Count} 点，内 {_ringInnerPolygon.Count} 点");
                    UpdateRingPreview();
                    UpdateThresholdPreview();
                    ViewHost.ReleaseMouseCapture();
                }
                else
                    AppendLog($"内圈：第 {_ringInnerPolygon.Count} 点");
                UpdateRingPreview();
            }
        }

        /// <summary>多边形加点；若靠近起点则闭合。返回 true 表示已闭合。</summary>
        private bool TryCloseOrAddPolygonPoint(List<Point> polygon, Point imgPt)
        {
            if (polygon.Count >= 3)
            {
                if (Dist(polygon[0], imgPt) < RingPolygonCloseDistance)
                    return true;
            }

            polygon.Add(imgPt);
            return false;
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                var current = e.GetPosition(ViewHost);
                _offsetX = _lastPanOffset.X + (current.X - _panStart.X);
                _offsetY = _lastPanOffset.Y + (current.Y - _panStart.Y);
                ApplyTransform();
                return;
            }

            if (!_isDrawing || RbRectMode.IsChecked != true) return;

            var cur = GetImagePointFromMouse(e);
            double ix = Math.Min(_drawStartImage.X, cur.X);
            double iy = Math.Min(_drawStartImage.Y, cur.Y);
            double iw = Math.Abs(cur.X - _drawStartImage.X);
            double ih = Math.Abs(cur.Y - _drawStartImage.Y);
            ClampRoiToImage(ref ix, ref iy, ref iw, ref ih);

            if (RoiRect != null)
            {
                Canvas.SetLeft(RoiRect, ix);
                Canvas.SetTop(RoiRect, iy);
                RoiRect.Width = iw;
                RoiRect.Height = ih;
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ViewHost.ReleaseMouseCapture();

            if (RbRectMode.IsChecked == true && _isDrawing)
            {
                _isDrawing = false;
                var cur = GetImagePointFromMouse(e);
                double x = Math.Min(_drawStartImage.X, cur.X);
                double y = Math.Min(_drawStartImage.Y, cur.Y);
                double w = Math.Abs(cur.X - _drawStartImage.X);
                double h = Math.Abs(cur.Y - _drawStartImage.Y);
                ClampRoiToImage(ref x, ref y, ref w, ref h);

                if (w > 5 && h > 5)
                {
                    _hasRoi = true;
                    _roiRectImage = new Rect(x, y, w, h);
                    RefreshRoiVisuals();
                    UpdateThresholdPreview();
                    AppendLog($"矩形ROI: ({x:F0},{y:F0}) {w:F0}x{h:F0}");
                }
                else if (RoiRect != null)
                {
                    RoiRect.Visibility = Visibility.Collapsed;
                }
            }
        }

        // ───────── 右键拖动 ─────────
        private void Canvas_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_imgWidth <= 0 || _imgHeight <= 0) return;

            _isPanning = true;
            _panStart = e.GetPosition(ViewHost);
            _lastPanOffset = new Point(_offsetX, _offsetY);
            ViewHost.CaptureMouse();
            e.Handled = true;
        }

        private void Canvas_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                ViewHost.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        // ───────── 鼠标滚轮缩放 ─────────
        private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_imgWidth <= 0 || _imgHeight <= 0) return;

            var mouseCanvas = e.GetPosition(ImageCanvas);
            double factor = e.Delta > 0 ? 1.1 : (1.0 / 1.1);
            double newScale = Math.Max(MinScale, Math.Min(MaxScale, _scale * factor));
            if (Math.Abs(newScale - _scale) < 0.001) return;

            double parentX = _scale * mouseCanvas.X + _offsetX;
            double parentY = _scale * mouseCanvas.Y + _offsetY;
            _scale = newScale;
            _offsetX = parentX - newScale * mouseCanvas.X;
            _offsetY = parentY - newScale * mouseCanvas.Y;

            ApplyTransform();
        }

        // ───────── 快捷键 ─────────
        private void Canvas_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+Z 撤销多边形点
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                BtnUndoPoint_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ResetView();
                AppendLog("视图已重置");
                e.Handled = true;
            }
            else if (e.Key == Key.Space)
            {
                ResetView();
                AppendLog("视图已重置");
                e.Handled = true;
            }
        }

        private void UpdatePolygonPreview()
        {
            if (PolygonLine == null) return;

            if (_polygonPointsImage.Count < 2)
            {
                PolygonLine.Visibility = Visibility.Collapsed;
                return;
            }

            PolygonLine.Visibility = Visibility.Visible;
            PolygonLine.Points.Clear();
            foreach (var pt in _polygonPointsImage)
                PolygonLine.Points.Add(pt);

            if (_polygonPointsImage.Count >= 3)
                PolygonLine.Points.Add(_polygonPointsImage[0]);
        }

        // ───────── 撤销多边形点 ─────────
        private void BtnUndoPoint_Click(object sender, RoutedEventArgs e)
        {
            if (IsRingModeActive())
            {
                _hasRingRoi = false;
                _hasRoi = false;
                if (_ringInnerPolygon.Count > 0)
                {
                    _ringInnerPolygon.RemoveAt(_ringInnerPolygon.Count - 1);
                    if (_ringPolygonPhase >= 2)
                        _ringPolygonPhase = 1;
                    AppendLog($"内圈撤销: 剩余 {_ringInnerPolygon.Count} 点");
                }
                else if (_ringOuterPolygon.Count > 0)
                {
                    _ringOuterPolygon.RemoveAt(_ringOuterPolygon.Count - 1);
                    _ringPolygonPhase = 0;
                    AppendLog($"外圈撤销: 剩余 {_ringOuterPolygon.Count} 点");
                }
                else
                    AppendLog("环形 ROI：已空");

                UpdateRingPreview();
                UpdateThresholdPreview();
                return;
            }

            if (_polygonPointsImage.Count > 0)
            {
                _polygonPointsImage.RemoveAt(_polygonPointsImage.Count - 1);
                _hasRoi = false;
                UpdatePolygonPreview();
                AppendLog($"撤销: 剩余 {_polygonPointsImage.Count} 个点");
            }
        }

        // ───────── 自动阈值 ─────────
        private void BtnAutoThreshold_Click(object sender, RoutedEventArgs e)
        {
            if (_grayPixels == null || _imgWidth <= 0)
            {
                MessageBox.Show("请先加载图像", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 大津法：仅在 ROI 内统计（无 ROI 则用全图）
            int[] hist = new int[256];
            int roiHits = 0;
            bool useRect = _hasRoi && RbRectMode?.IsChecked == true;
            bool usePoly = _hasRoi && RbPolygonMode?.IsChecked == true && _polygonPointsImage.Count >= 3;
            bool useRing = HasUsableRingRoi();

            for (int y = 0; y < _imgHeight; y++)
            {
                for (int x = 0; x < _imgWidth; x++)
                {
                    if (useRect)
                    {
                        if (x + 0.5 < _roiRectImage.X || x + 0.5 >= _roiRectImage.Right ||
                            y + 0.5 < _roiRectImage.Y || y + 0.5 >= _roiRectImage.Bottom)
                            continue;
                    }
                    else if (usePoly)
                    {
                        if (!IsPointInPolygon(x + 0.5, y + 0.5, _polygonPointsImage))
                            continue;
                    }
                    else if (useRing)
                    {
                        if (!IsPointInRingRoi(x + 0.5, y + 0.5))
                            continue;
                    }

                    int g = _grayPixels[y * _imgWidth + x];
                    hist[g]++;
                    roiHits++;
                }
            }

            if (roiHits == 0)
            {
                foreach (var g in _grayPixels)
                {
                    hist[g]++;
                    roiHits++;
                }
            }

            int total = roiHits;
            double sum = 0;
            for (int i = 0; i < 256; i++)
                sum += i * hist[i];

            double sumB = 0;
            int wB = 0;
            double maxVar = 0;
            int thresh = 128;

            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                int wF = total - wB;
                if (wF == 0) break;

                sumB += t * (double)hist[t];
                double mB = sumB / wB;
                double mF = (sum - sumB) / wF;
                double var = (double)wB * wF * (mB - mF) * (mB - mF);

                if (var > maxVar)
                {
                    maxVar = var;
                    thresh = t;
                }
            }

            bool InsideRoi(int x, int y)
            {
                if (useRect)
                    return x + 0.5 >= _roiRectImage.X && x + 0.5 < _roiRectImage.Right &&
                           y + 0.5 >= _roiRectImage.Y && y + 0.5 < _roiRectImage.Bottom;
                if (usePoly)
                    return IsPointInPolygon(x + 0.5, y + 0.5, _polygonPointsImage);
                if (useRing)
                    return IsPointInRingRoi(x + 0.5, y + 0.5);
                return true;
            }

            var (minG, maxG) = HalconFlowBridge.SuggestGrayRangeInRegion(
                _grayPixels, _imgWidth, _imgHeight, thresh, InsideRoi);
            TxtMinGray.Text = minG.ToString(CultureInfo.InvariantCulture);
            TxtMaxGray.Text = maxG.ToString(CultureInfo.InvariantCulture);
            AppendLog(useRect || usePoly || useRing
                ? $"ROI 内自动阈值: [{minG}, {maxG}]（Otsu={thresh}，样本 {roiHits} 像素）"
                : $"全图自动阈值: [{minG}, {maxG}]（Otsu={thresh}）");
            UpdateThresholdPreview();
        }

        // ───────── 阈值预览 ─────────
        private void TxtMinGray_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            UpdateThresholdPreview();
        }

        private void TxtMaxGray_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            UpdateThresholdPreview();
        }

        private static bool IsPointInPolygon(double x, double y, IReadOnlyList<Point> polygon)
        {
            if (polygon == null || polygon.Count < 3)
                return false;

            bool inside = false;
            int n = polygon.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double yi = polygon[i].Y;
                double yj = polygon[j].Y;
                double xi = polygon[i].X;
                double xj = polygon[j].X;
                if ((yi > y) != (yj > y) &&
                    x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi)
                    inside = !inside;
            }

            return inside;
        }

        /// <summary>按阈值逐像素高亮（青色半透明），与底图同尺寸、同坐标。</summary>
        private void UpdateThresholdPreview()
        {
            if (ThresholdOverlayImage == null || _grayPixels == null || _imgWidth <= 0)
            {
                if (ThresholdOverlayImage != null)
                    ThresholdOverlayImage.Visibility = Visibility.Collapsed;
                return;
            }

            if (!double.TryParse(TxtMinGray.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minGray))
                minGray = 0;
            if (!double.TryParse(TxtMaxGray.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double maxGray))
                maxGray = 255;

            if (minGray > maxGray)
            {
                ThresholdOverlayImage.Visibility = Visibility.Collapsed;
                return;
            }

            int w = _imgWidth;
            int h = _imgHeight;
            int x0 = 0, y0 = 0, x1 = w, y1 = h;
            bool useRect = _hasRoi && RbRectMode?.IsChecked == true;
            bool usePoly = _hasRoi && RbPolygonMode?.IsChecked == true && _polygonPointsImage.Count >= 3;
            bool useRing = HasUsableRingRoi();

            if (useRect)
            {
                x0 = Math.Max(0, (int)Math.Floor(_roiRectImage.X));
                y0 = Math.Max(0, (int)Math.Floor(_roiRectImage.Y));
                x1 = Math.Min(w, (int)Math.Ceiling(_roiRectImage.Right));
                y1 = Math.Min(h, (int)Math.Ceiling(_roiRectImage.Bottom));
            }
            else if (useRing)
            {
                x0 = Math.Max(0, (int)Math.Floor(_ringOuterPolygon.Min(p => p.X)));
                y0 = Math.Max(0, (int)Math.Floor(_ringOuterPolygon.Min(p => p.Y)));
                x1 = Math.Min(w, (int)Math.Ceiling(_ringOuterPolygon.Max(p => p.X)));
                y1 = Math.Min(h, (int)Math.Ceiling(_ringOuterPolygon.Max(p => p.Y)));
            }

            int stride = w * 4;
            var pixels = new byte[h * stride];
            int hitCount = 0;

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    if (usePoly && !IsPointInPolygon(x + 0.5, y + 0.5, _polygonPointsImage))
                        continue;
                    if (useRing && !IsPointInRingRoi(x + 0.5, y + 0.5))
                        continue;

                    int gray = _grayPixels[y * w + x];
                    if (gray < minGray || gray > maxGray)
                        continue;

                    int o = y * stride + x * 4;
                    pixels[o] = 255;     // B
                    pixels[o + 1] = 255; // G
                    pixels[o + 2] = 0;     // R
                    pixels[o + 3] = 150;   // A
                    hitCount++;
                }
            }

            if (hitCount == 0)
            {
                ThresholdOverlayImage.Visibility = Visibility.Collapsed;
                return;
            }

            _thresholdOverlay = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            _thresholdOverlay.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
            ThresholdOverlayImage.Source = _thresholdOverlay;
            ThresholdOverlayImage.Width = w;
            ThresholdOverlayImage.Height = h;
            ThresholdOverlayImage.Visibility = Visibility.Visible;
        }

        private void CreateOption_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            UpdateCreatePanelsVisibility();
        }

        private void UpdateCreatePanelsVisibility()
        {
            if (CmbTemplateSource == null) return;

            string src = (CmbTemplateSource.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ThresholdXld";
            string kind = (CmbModelKind?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Shape";

            bool isXld = src is "ThresholdXld" or "EdgesXld" or "PolygonXld";
            bool isImage = src is "ImageRectangle" or "ImagePolygon";

            if (PnlContourExtract != null)
                PnlContourExtract.Visibility = isXld ? Visibility.Visible : Visibility.Collapsed;
            if (PnlContourTrim != null)
                PnlContourTrim.Visibility = isXld ? Visibility.Visible : Visibility.Collapsed;
            if (PnlEdgeExtract != null)
                PnlEdgeExtract.Visibility = src == "EdgesXld" ? Visibility.Visible : Visibility.Collapsed;
            if (PnlThreshold != null)
                PnlThreshold.Visibility = src == "ThresholdXld" ? Visibility.Visible : Visibility.Collapsed;
            if (PnlScale != null)
                PnlScale.Visibility = kind == "ScaledShape" ? Visibility.Visible : Visibility.Collapsed;
            if (RowContrast != null)
                RowContrast.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        }

#if HALCON_ENABLED
        private HalconShapeModelSourceKind ParseSourceKind()
        {
            string? tag = (CmbTemplateSource?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return Enum.TryParse(tag, out HalconShapeModelSourceKind k)
                ? k
                : HalconShapeModelSourceKind.ThresholdXld;
        }

        private HalconShapeModelKind ParseModelKind()
        {
            string? tag = (CmbModelKind?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return Enum.TryParse(tag, out HalconShapeModelKind k)
                ? k
                : HalconShapeModelKind.Shape;
        }

        private static string ComboTag(ComboBox? cmb, string fallback) =>
            (cmb?.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? (cmb?.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? fallback;

        private HalconShapeModelCreateOptions ReadCreateOptionsFromUi()
        {
            if (!int.TryParse(TxtNumLevels.Text, out int numLevels)) numLevels = 0;
            if (!double.TryParse(TxtAngleStart.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double angleStart)) angleStart = -30;
            if (!double.TryParse(TxtAngleExtent.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double angleExtent)) angleExtent = 60;
            if (!double.TryParse(TxtAngleStep.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double angleStep)) angleStep = 0;
            if (!int.TryParse(TxtMinContrast.Text, out int minContrast)) minContrast = 10;
            if (!double.TryParse(TxtScaleMin.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double scaleMin)) scaleMin = 0.9;
            if (!double.TryParse(TxtScaleMax.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double scaleMax)) scaleMax = 1.1;
            if (!double.TryParse(TxtScaleStep.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double scaleStep)) scaleStep = 0;
            if (!int.TryParse(TxtMinContourPoints.Text, out int minContourPts)) minContourPts = 10;
            if (!double.TryParse(TxtEdgeAlpha.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double edgeAlpha)) edgeAlpha = 1;
            if (!double.TryParse(TxtEdgeLow.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double edgeLow)) edgeLow = 20;
            if (!double.TryParse(TxtEdgeHigh.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double edgeHigh)) edgeHigh = 40;

            return new HalconShapeModelCreateOptions
            {
                ModelKind = ParseModelKind(),
                SourceKind = ParseSourceKind(),
                NumLevels = numLevels,
                AngleStartDeg = angleStart,
                AngleExtentDeg = angleExtent,
                AngleStepDeg = angleStep,
                Optimization = ComboTag(CmbOptimization, "auto"),
                Metric = ComboTag(CmbMetric, "ignore_local_polarity"),
                Contrast = string.IsNullOrWhiteSpace(TxtContrast.Text) ? "auto" : TxtContrast.Text.Trim(),
                MinContrast = minContrast,
                ScaleMin = scaleMin,
                ScaleMax = scaleMax,
                ScaleStep = scaleStep,
                GenContourMode = ComboTag(CmbGenContourMode, "border"),
                MinContourPoints = minContourPts,
                LargestContourOnly = ChkLargestContourOnly.IsChecked == true,
                EdgeAlpha = edgeAlpha,
                EdgeLow = edgeLow,
                EdgeHigh = edgeHigh
            };
        }

        private HObject? TryBuildOptionalDomainRegion()
        {
#if HALCON_ENABLED
            if (HasUsableRingRoi())
                return TryBuildRingRegion();
#endif
            if (RbPolygonMode.IsChecked == true && _polygonPointsImage.Count >= 3)
            {
                var pts = _polygonPointsImage.Select(p => new Point2D(p.X, p.Y)).ToList();
                return HalconFlowBridge.GenRegionPolygonFilled(pts);
            }

            if (_hasRoi && RbRectMode.IsChecked == true)
            {
                double r1 = _roiRectImage.Y;
                double c1 = _roiRectImage.X;
                double r2 = _roiRectImage.Bottom;
                double c2 = _roiRectImage.Right;
                return HalconFlowBridge.GenRegionRectangle(r1, c1, r2, c2);
            }

            return null;
        }

        private HObject BuildRequiredRegionForImageMode(HalconShapeModelSourceKind src)
        {
#if HALCON_ENABLED
            if (HasUsableRingRoi())
            {
                var ring = TryBuildRingRegion();
                if (ring == null)
                    throw new InvalidOperationException("环形 ROI 无效");
                return ring;
            }
#endif
            if (src == HalconShapeModelSourceKind.ImageRectangle)
            {
                if (!_hasRoi || RbRectMode.IsChecked != true)
                    throw new InvalidOperationException("矩形灰度模板：请用「矩形」或「环形」模式绘制 ROI。");
                return HalconFlowBridge.GenRegionRectangle(
                    _roiRectImage.Y, _roiRectImage.X, _roiRectImage.Bottom, _roiRectImage.Right);
            }

            if (_polygonPointsImage.Count < 3)
                throw new InvalidOperationException("多边形灰度模板：请用「多边形」或「环形」模式绘制 ROI。");
            var pts = _polygonPointsImage.Select(p => new Point2D(p.X, p.Y)).ToList();
            return HalconFlowBridge.GenRegionPolygonFilled(pts);
        }

        private HalconXldContourBundle? ExtractXldBundleForCreate(HalconShapeModelCreateOptions opt, double minGray, double maxGray)
        {
            if (_currentImage == null) return null;

            string genMode = opt.GenContourMode;
            int minPts = opt.MinContourPoints;
            HObject? domain = TryBuildOptionalDomainRegion();

            try
            {
                HalconXldContourBundle? bundle = opt.SourceKind switch
                {
                    HalconShapeModelSourceKind.PolygonXld => BuildPolygonXldBundle(),
                    HalconShapeModelSourceKind.EdgesXld => domain != null
                        ? HalconFlowBridge.XldContoursFromEdgesSubPix(_currentImage, domain, opt.EdgeAlpha, opt.EdgeLow, opt.EdgeHigh, minPts)
                        : HalconFlowBridge.XldContoursFromEdgesSubPix(_currentImage, null, opt.EdgeAlpha, opt.EdgeLow, opt.EdgeHigh, minPts),
                    HalconShapeModelSourceKind.ThresholdXld => domain != null
                        ? HalconFlowBridge.XldContoursFromBinaryGrayInRegion(_currentImage, domain, minGray, maxGray, genMode, minPts)
                        : ExtractXldFromWholeImage(minGray, maxGray, genMode, minPts),
                    _ => null
                };

                if (bundle == null)
                    return null;

                bundle = HalconFlowBridge.FilterXldBundle(bundle, minPts, opt.LargestContourOnly);

                return bundle;
            }
            finally
            {
                domain?.Dispose();
            }
        }

        private HalconXldContourBundle? BuildPolygonXldBundle()
        {
#if HALCON_ENABLED
            if (HasUsableRingRoi())
            {
                var outer = _ringOuterPolygon.Select(p => new Point2D(p.X, p.Y)).ToList();
                var inner = _ringInnerPolygon.Select(p => new Point2D(p.X, p.Y)).ToList();
                var contours = HalconFlowBridge.PolygonsToClosedContourList(outer, inner);
                if (contours.Count < 2)
                    throw new InvalidOperationException("环形 ROI 未能生成内外轮廓，请确认外圈、内圈均已闭合且至少 3 个点");
                return new HalconXldContourBundle
                {
                    Width = _imgWidth,
                    Height = _imgHeight,
                    Contours = contours
                };
            }
#endif
            if (_polygonPointsImage.Count >= 3)
                return PolygonToXldBundle(_polygonPointsImage);

            if (_hasRoi && RbRectMode.IsChecked == true)
            {
                var corners = new List<Point>
                {
                    _roiRectImage.TopLeft,
                    new Point(_roiRectImage.Right, _roiRectImage.Top),
                    _roiRectImage.BottomRight,
                    new Point(_roiRectImage.Left, _roiRectImage.Bottom),
                    _roiRectImage.TopLeft
                };
                return PolygonToXldBundle(corners);
            }

            throw new InvalidOperationException("多边形 XLD：请绘制多边形/矩形/环形 ROI。");
        }

        private HalconXldContourBundle? ExtractXldFromWholeImage(double minGray, double maxGray, string genMode, int minPts)
        {
            if (_currentImage == null) return null;
            return HalconFlowBridge.XldContoursFromBinaryGray(_currentImage, minGray, maxGray, genMode, minPts);
        }

        private void DrawXldPreviewOverlay(HalconXldContourBundle? bundle, Brush? stroke = null)
        {
            ClearFindResultOverlay();
            if (FindResultOverlay == null || bundle?.Contours == null)
                return;

            Brush lineBrush = stroke ?? Brushes.DeepSkyBlue;
            foreach (var contour in bundle.Contours)
            {
                if (contour == null || contour.Length < 2) continue;
                var pts = new PointCollection(contour.Length);
                foreach (var p in contour)
                    pts.Add(new Point(p.X, p.Y));

                FindResultOverlay.Children.Add(new Polyline
                {
                    Points = pts,
                    Stroke = lineBrush,
                    StrokeThickness = 2,
                    Fill = Brushes.Transparent
                });
            }
        }

        private HalconContourTrimOptions ReadTrimOptionsFromUi()
        {
            string? tag = (CmbTrimMode?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Simplify";
            if (!Enum.TryParse(tag, out HalconContourTrimMode mode))
                mode = HalconContourTrimMode.Simplify;

            if (!double.TryParse(TxtTrimEpsilon.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double eps) || eps <= 0)
                eps = 2;
            if (!double.TryParse(TxtTrimEndsPx.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double trimEnds))
                trimEnds = 0;
            if (!double.TryParse(TxtTrimMinLength.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minLen) || minLen < 0)
                minLen = 20;

            return new HalconContourTrimOptions
            {
                Mode = mode,
                Epsilon = eps,
                TrimEndsPx = trimEnds,
                MinContourLength = minLen,
                ClosedContour = ChkTrimClosed.IsChecked == true
            };
        }

        private void BtnApplyTrim_Click(object sender, RoutedEventArgs e)
        {
#if !HALCON_ENABLED
            MessageBox.Show("当前构建未启用 HALCON。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
#else
            if (_workingContours?.Contours == null || _workingContours.Contours.Count == 0)
            {
                MessageBox.Show("请先点击「预览轮廓」提取轮廓。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                int ptsBefore = HalconXldContourTrimmer.CountPoints(_workingContours);
                int nBefore = _workingContours.Contours.Count;
                var trimOpt = ReadTrimOptionsFromUi();
                var trimmed = HalconXldContourTrimmer.Apply(_workingContours, trimOpt);
                if (trimmed.Contours == null || trimmed.Contours.Count == 0)
                {
                    MessageBox.Show("修剪后没有剩余轮廓，请放宽参数或重新预览。", "修剪", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int ptsAfter = HalconXldContourTrimmer.CountPoints(trimmed);
                SetWorkingContours(trimmed, trimmed: true);
                AppendLog($"轮廓修剪 [{trimOpt.Mode}]: {nBefore}条/{ptsBefore}点 → {trimmed.Contours.Count}条/{ptsAfter}点");
            }
            catch (Exception ex)
            {
                AppendLog($"[修剪错误] {ex.Message}");
                MessageBox.Show(ex.Message, "修剪失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
#endif
        }

        private void BtnResetContours_Click(object sender, RoutedEventArgs e)
        {
            _workingContours = null;
            ClearFindResultOverlay();
            UpdateContourStats();
            AppendLog("已清除修剪结果，请重新预览轮廓");
        }

#endif

        private void BtnPreviewContour_Click(object sender, RoutedEventArgs e)
        {
#if !HALCON_ENABLED
            MessageBox.Show("当前构建未启用 HALCON。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
#else
            if (_grayPixels == null || _imgWidth <= 0)
            {
                MessageBox.Show("请先加载图像", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var opt = ReadCreateOptionsFromUi();
                if (opt.SourceKind is HalconShapeModelSourceKind.ImageRectangle or HalconShapeModelSourceKind.ImagePolygon)
                {
                    HObject region = BuildRequiredRegionForImageMode(opt.SourceKind);
                    try
                    {
                        var boundary = HasUsableRingRoi()
                            ? HalconFlowBridge.RegionRingBoundaryContours(region)
                            : HalconFlowBridge.RegionToBoundaryContours(region);
                        if (boundary.Count == 0)
                            throw new InvalidOperationException("未能从 ROI 区域提取边界轮廓");
                        SetWorkingContours(new HalconXldContourBundle
                        {
                            Width = _imgWidth,
                            Height = _imgHeight,
                            Contours = boundary
                        });
                        AppendLog(HasUsableRingRoi()
                            ? $"已预览环形 ROI 边界（{boundary.Count} 条，含内外圈）"
                            : "已预览图像模板 ROI 边界（蓝线）");
                    }
                    finally
                    {
                        region.Dispose();
                    }
                    return;
                }

                if (!double.TryParse(TxtMinGray.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minGray)) minGray = 0;
                if (!double.TryParse(TxtMaxGray.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double maxGray)) maxGray = 255;

                var bundle = ExtractXldBundleForCreate(opt, minGray, maxGray);
                if (bundle?.Contours == null || bundle.Contours.Count == 0)
                {
                    MessageBox.Show("未提取到轮廓，请调整参数或 ROI。", "预览", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SetWorkingContours(bundle);
                AppendLog($"轮廓预览: {bundle.Contours.Count} 条");
            }
            catch (Exception ex)
            {
                AppendLog($"[预览错误] {ex.Message}");
                MessageBox.Show(ex.Message, "预览失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
#endif
        }

        // ───────── 创建形状模型 ─────────
        private void BtnCreateModel_Click(object sender, RoutedEventArgs e)
        {
#if !HALCON_ENABLED
            MessageBox.Show("当前构建未启用 HALCON，无法创建形状模型。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
#else
            if (_grayPixels == null || _imgWidth <= 0)
            {
                MessageBox.Show("请先加载图像", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_modelId >= 0)
            {
                var result = MessageBox.Show("已存在模型，将被替换。是否继续？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;
                DisposeCurrentModel();
            }

            try
            {
                var opt = ReadCreateOptionsFromUi();
                if (!double.TryParse(TxtMinGray.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minGray)) minGray = 0;
                if (!double.TryParse(TxtMaxGray.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double maxGray)) maxGray = 255;

                AppendLog($"创建模式: {opt.SourceKind} / {opt.ModelKind}");
                ClearFindResultOverlay();

                bool fromImage = opt.SourceKind is HalconShapeModelSourceKind.ImageRectangle
                    or HalconShapeModelSourceKind.ImagePolygon;

                HalconXldContourBundle? xldBundle = null;
                HObject? region = null;

                if (fromImage)
                {
                    region = BuildRequiredRegionForImageMode(opt.SourceKind);
                    AppendLog("使用 ROI 灰度图创建模板...");
                    _modelId = HalconFlowBridge.CreateShapeModel(_currentImage, null, region, opt);
                }
                else
                {
                    if (_workingContours?.Contours != null && _workingContours.Contours.Count > 0)
                    {
                        xldBundle = _workingContours;
                        AppendLog($"使用已预览/修剪轮廓: {xldBundle.Contours.Count} 条");
                    }
                    else
                    {
                        AppendLog("提取 XLD 轮廓...");
                        xldBundle = ExtractXldBundleForCreate(opt, minGray, maxGray);
                        if (xldBundle?.Contours == null || xldBundle.Contours.Count == 0)
                        {
                            MessageBox.Show(
                                "未能提取到有效轮廓。\n请使用「ROI 内灰度阈值」、调整灰度或点「自动阈值」。\n「仅 ROI 边线」不会按灰度提轮廓。",
                                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        AppendLog($"轮廓: {xldBundle.Contours.Count} 条");
                        SetWorkingContours(xldBundle);
                    }

                    _modelId = HalconFlowBridge.CreateShapeModel(_currentImage, xldBundle, null, opt);
                }

                region?.Dispose();

                string levelsText = opt.NumLevels > 0 ? opt.NumLevels.ToString() : "auto";
                TxtModelInfo.Text =
                    $"ModelID: {_modelId}\n" +
                    $"来源: {opt.SourceKind}\n" +
                    $"类型: {opt.ModelKind}\n" +
                    $"图像: {_imgWidth}x{_imgHeight}\n" +
                    $"角度: [{opt.AngleStartDeg}°, 范围 {opt.AngleExtentDeg}°]\n" +
                    $"NumLevels: {levelsText}\n" +
                    (xldBundle != null ? $"轮廓数: {xldBundle.Contours.Count}\n" : "");

                AppendLog($"模型创建成功 ModelID={_modelId}");
                MessageBox.Show($"形状模型创建成功!\nModelID: {_modelId}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendLog($"[错误] {ex.Message}");
                MessageBox.Show($"创建模型失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
#endif
        }

        private HalconXldContourBundle PolygonToXldBundle(IReadOnlyList<Point> polygonImagePts)
        {
            var contours = new List<Point2D[]>();
            var pts = polygonImagePts.Select(p => new Point2D(p.X, p.Y)).ToArray();
            if (pts.Length >= 2)
                contours.Add(HalconFlowBridge.EnsureClosedContourPoints(pts));

            return new HalconXldContourBundle
            {
                Width = _imgWidth,
                Height = _imgHeight,
                Contours = contours
            };
        }

        private HalconXldContourBundle? ExtractXldFromRoi(double minGray, double maxGray)
        {
            int x0 = Math.Max(0, (int)_roiRectImage.X);
            int y0 = Math.Max(0, (int)_roiRectImage.Y);
            int x1 = Math.Min(_imgWidth, (int)(_roiRectImage.X + _roiRectImage.Width));
            int y1 = Math.Min(_imgHeight, (int)(_roiRectImage.Y + _roiRectImage.Height));

            var roiPixels = new byte[(x1 - x0) * (y1 - y0)];
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    roiPixels[(y - y0) * (x1 - x0) + (x - x0)] = _grayPixels![y * _imgWidth + x];
                }
            }

            int roiW = x1 - x0;
            int roiH = y1 - y0;
            using var roiImg = new CalibImage(roiW, roiH, 1);
            var rn = roiImg.GetNativeStruct();
            System.Runtime.InteropServices.Marshal.Copy(roiPixels, 0, rn.data, roiW * roiH);

            var bundle = HalconFlowBridge.XldContoursFromBinaryGray(roiImg, minGray, maxGray, "border", 10);

            // 调试：输出每条轮廓的点数
            if (bundle.Contours != null)
            {
                AppendLog($"原始轮廓详情: 共{bundle.Contours.Count}条");
                for (int i = 0; i < bundle.Contours.Count; i++)
                {
                    var contour = bundle.Contours[i];
                    AppendLog($"  轮廓[{i}]: {contour?.Length ?? -1} 点");
                    if (contour != null && contour.Length > 0)
                    {
                        AppendLog($"    首点: ({contour[0].X:F1}, {contour[0].Y:F1}), 末点: ({contour[contour.Length-1].X:F1}, {contour[contour.Length-1].Y:F1})");
                    }
                }

                foreach (var contour in bundle.Contours)
                {
                    if (contour == null) continue;
                    for (int i = 0; i < contour.Length; i++)
                    {
                        contour[i] = new Point2D(contour[i].X + x0, contour[i].Y + y0);
                    }
                }
            }

            return bundle;
        }

        private HalconXldContourBundle? ExtractXldFromWholeImage(double minGray, double maxGray)
        {
            return ExtractXldFromWholeImage(minGray, maxGray, "border", 10);
        }

        // ───────── 测试 FindShapeModel ─────────
        private void BtnTestFind_Click(object sender, RoutedEventArgs e)
        {
#if !HALCON_ENABLED
            MessageBox.Show("当前构建未启用 HALCON。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
#else
            if (_modelId < 0)
            {
                MessageBox.Show("请先创建或导入形状模型", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_currentImage == null)
            {
                MessageBox.Show("请先加载图像", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                if (!double.TryParse(TxtAngleStart.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double angleStart)) angleStart = -30;
                if (!double.TryParse(TxtAngleExtent.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double angleExtent)) angleExtent = 60;
                if (!int.TryParse(TxtFindNumMatches.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numMatches) || numMatches < 0)
                    numMatches = 0;
                if (!double.TryParse(TxtFindMinScore.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minScore) || minScore < 0 || minScore > 1)
                    minScore = 0.4;
                if (!double.TryParse(TxtFindMaxOverlap.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double maxOverlap) || maxOverlap < 0 || maxOverlap > 1)
                    maxOverlap = 0.5;

                if (!double.TryParse(TxtFindGreediness.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double greediness) || greediness < 0 || greediness > 1)
                    greediness = 0.75;
                if (!int.TryParse(TxtFindNumLevels.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int findNumLevels) || findNumLevels < 0)
                    findNumLevels = 0;
                if (!int.TryParse(TxtNumLevels.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int createNumLevels))
                    createNumLevels = 0;
                if (findNumLevels == 0 && createNumLevels > 0)
                    findNumLevels = createNumLevels;

                AppendLog($"查找: MinScore={minScore}, Greediness={greediness}, NumLevels={findNumLevels}, 角度[{angleStart}°~{angleStart + angleExtent}°]");

                (double[] rows, double[] cols, double[] angles, double[] scores) findResult;
                if (ChkFindAutoRetry.IsChecked == true)
                {
                    findResult = HalconFlowBridge.FindShapeModelWithFallback(
                        _currentImage,
                        _modelId,
                        angleStart,
                        angleExtent,
                        minScore,
                        numMatches,
                        maxOverlap,
                        "least_squares",
                        findNumLevels,
                        greediness);
                }
                else
                {
                    findResult = HalconFlowBridge.FindShapeModel(
                        _currentImage,
                        _modelId,
                        angleStart,
                        angleExtent,
                        minScore,
                        numMatches,
                        maxOverlap,
                        "least_squares",
                        findNumLevels,
                        greediness);
                }

                var (rows, cols, angles, scores) = findResult;
                int rawCount = rows.Length;

                if (ChkFindApplyGridFilter.IsChecked == true && rows.Length > 0)
                {
                    if (!HalconShapeMatchGridDiagnostics.IsEnabled)
                    {
                        HalconShapeMatchGridDiagnostics.Enable(mirrorConsole: false, sessionName: "ShapeModelPage");
                        AppendLog($"[GridFilter] 诊断日志 → {HalconShapeMatchGridDiagnostics.LogFilePath}");
                    }

                    if (!int.TryParse(TxtFindGridRows.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gridRows) || gridRows < 1)
                        gridRows = 3;
                    if (!int.TryParse(TxtFindGridCols.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gridCols) || gridCols < 1)
                        gridCols = 3;
                    if (!double.TryParse(TxtFindMinScoreKeep.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minScoreKeep) || minScoreKeep < 0)
                        minScoreKeep = 0.45;
                    if (!double.TryParse(TxtFindMaxAngleDev.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double maxAngleDev) || maxAngleDev < 0)
                        maxAngleDev = 0;

                    double? gridAngleDeg = null;
                    string angleRaw = (TxtFindGridAngleDeg.Text ?? "auto").Trim();
                    if (!string.Equals(angleRaw, "auto", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(angleRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double fixedAngle))
                    {
                        gridAngleDeg = fixedAngle;
                    }

                    HalconShapeMatchGridDiagnostics.LogFindInput("ShapeModelPage", rows.Length, scores);
                    var filtered = HalconShapeMatchGridFilter.Filter(
                        rows, cols, angles, scores,
                        gridRows, gridCols,
                        pitchRow: 0, pitchCol: 0,
                        gridAngleDeg,
                        snapTolerancePx: 0,
                        pitchToleranceRatio: 0.2,
                        minNeighborVotes: 0,
                        minScoreKeep,
                        maxAngleDev,
                        diagnosticTag: "ShapeModelPage");

                    rows = filtered.Rows;
                    cols = filtered.Cols;
                    angles = filtered.Angles;
                    scores = filtered.Scores;
                    int[] gridRowOut = filtered.GridRow;
                    int[] gridColOut = filtered.GridCol;
                    string swapNote = filtered.AxesSwapped ? ", 行列轴已对调" : "";
                    string angleDevNote = maxAngleDev > 0 ? $", 角度±{maxAngleDev}°" : "";
                    AppendLog($"阵列过滤 {gridRows}×{gridCols}: {rawCount} → {rows.Length}（θ≈{filtered.EstimatedAngleDeg:F1}°{swapNote}{angleDevNote}）");

                    if (rows.Length == 0)
                    {
                        ClearFindResultOverlay();
                        TxtFindResult.Text = "未找到匹配";
                        AppendLog("未找到匹配（可尝试 MinScore≈0.35、Greediness≈0.65、扩大角度范围）");
                        return;
                    }

                    DrawFindResultOverlay(rows, cols, angles, scores, gridRowOut, gridColOut);
                    var filteredResult = new System.Text.StringBuilder();
                    filteredResult.AppendLine($"找到 {rows.Length} 个匹配（阵列 {gridRows}×{gridCols}）:");
                    for (int i = 0; i < rows.Length; i++)
                    {
                        string gridNote = i < gridRowOut.Length && gridRowOut[i] >= 0
                            ? $" 格[{gridRowOut[i]},{gridColOut[i]}]"
                            : "";
                        filteredResult.AppendLine($"[{i}] Row={rows[i]:F1}, Col={cols[i]:F1}, Angle={angles[i]:F2}°, Score={scores[i]:F3}{gridNote}");
                    }
                    TxtFindResult.Text = filteredResult.ToString();
                    AppendLog($"查找完成: {rows.Length} 个匹配（已在图像上高亮）");
                    return;
                }
                else if (rawCount > 0)
                {
                    AppendLog($"HALCON 原始匹配 {rawCount} 个（未做阵列过滤；与流程对比请勾选阵列过滤）");
                }

                if (rows.Length == 0)
                {
                    ClearFindResultOverlay();
                    TxtFindResult.Text = "未找到匹配";
                    AppendLog("未找到匹配（可尝试 MinScore≈0.35、Greediness≈0.65、扩大角度范围）");
                }
                else
                {
                    DrawFindResultOverlay(rows, cols, angles, scores);
                    var result = new System.Text.StringBuilder();
                    result.AppendLine($"找到 {rows.Length} 个匹配（未做阵列格标注）:");
                    for (int i = 0; i < rows.Length; i++)
                    {
                        result.AppendLine($"[{i}] Row={rows[i]:F1}, Col={cols[i]:F1}, Angle={angles[i]:F2}°, Score={scores[i]:F3}");
                    }
                    TxtFindResult.Text = result.ToString();
                    AppendLog($"查找完成: {rows.Length} 个匹配（已在图像上高亮）");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[错误] {ex.Message}");
                MessageBox.Show($"查找失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
#endif
        }

        // ───────── 导入模型 ─────────
        private void BtnImportModel_Click(object sender, RoutedEventArgs e)
        {
#if !HALCON_ENABLED
            MessageBox.Show("当前构建未启用 HALCON，无法导入模型。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
#else
            var dlg = new OpenFileDialog
            {
                Filter = "HALCON 形状模型 (*.shm)|*.shm|所有文件 (*.*)|*.*",
                Title = "导入形状模型"
            };
            if (dlg.ShowDialog() != true) return;

            if (_modelId >= 0)
            {
                var confirm = MessageBox.Show("已存在模型，导入将替换当前模型。是否继续？", "确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
                DisposeCurrentModel();
            }

            try
            {
                ClearFindResultOverlay();
                _modelId = HalconFlowBridge.LoadShapeModelFromFile(dlg.FileName);
                string summary = HalconFlowBridge.GetShapeModelParamsSummary(_modelId);
                TxtModelInfo.Text = $"来源: 文件导入\n文件: {dlg.FileName}\n{summary}";
                AppendLog($"模型已导入: {dlg.FileName} (ModelID={_modelId})");
                MessageBox.Show($"形状模型已导入，可进行形状匹配测试。\nModelID: {_modelId}", "导入成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendLog($"[错误] {ex.Message}");
                MessageBox.Show($"导入失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
#endif
        }

        // ───────── 导出模型 ─────────
        private void BtnExportModel_Click(object sender, RoutedEventArgs e)
        {
#if !HALCON_ENABLED
            MessageBox.Show("当前构建未启用 HALCON，无法导出模型。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
#else
            if (_modelId < 0)
            {
                MessageBox.Show("请先创建形状模型", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "HALCON 形状模型 (*.shm)|*.shm|所有文件 (*.*)|*.*",
                Title = "保存形状模型",
                FileName = "shape_model.shm"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                HalconFlowBridge.WriteShapeModelToFile(_modelId, dlg.FileName);
                AppendLog($"模型已导出: {dlg.FileName}");
                MessageBox.Show($"HALCON 形状模型已保存:\n{dlg.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendLog($"[错误] {ex.Message}");
                MessageBox.Show($"导出失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
#endif
        }
    }
}
