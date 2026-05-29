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
        /// <summary>加载后未矫正的灰度图副本，用于重复应用矫正参数。</summary>
        private CalibImage? _rawCalibImage;
        private string? _loadedImagePath;
        private byte[]? _grayPixels;
        private WriteableBitmap? _thresholdOverlay;
        private int _imgWidth;
        private int _imgHeight;

        // ROI 状态（坐标均为图像像素系）
        private bool _isDrawing;
        private Point _drawStartImage;
        private readonly RoiContourPath _roiPath = new RoiContourPath();
        /// <summary>圆弧模式：已点终点，等待弧上经过点。</summary>
        private Point? _arcDraftEnd;
        private bool _hasRoi;
        private Rect _roiRectImage;

        // 圆形 ROI：圆心 + 半径（图像像素系，X=列 Y=行）
        private Point _circleCenterImage;
        private double _circleRadiusImage;

        // 环形 ROI：外圈 + 内圈；_ringPolygonPhase 0=绘外圈 1=绘内圈 2=完成
        private bool _hasRingRoi;
        private readonly RoiContourPath _ringOuterPath = new RoiContourPath();
        private readonly RoiContourPath _ringInnerPath = new RoiContourPath();
        private int _ringPolygonPhase;
        private List<Point>? _cachedRingOuterFlat;
        private List<Point>? _cachedRingInnerFlat;
        private bool _ringFlatDirty = true;
        private const double DefaultPolygonCloseDistance = 12;
        private const int PolygonPreviewMoveIntervalMs = 16;
        private const int PreviewArcSegments = 12;
        private const int ClosedArcSegments = 24;
        private Point? _polygonCursorImage;
        private List<Point>? _cachedPolygonFlat;
        private bool _polygonFlatDirty = true;
        private long _lastPolygonPreviewMoveMs;

        // 模型
        private long _modelId = -1;
        private HalconFlowModelKind _modelKind = HalconFlowModelKind.Shape;

        /// <summary>预览/修剪后的轮廓，创建 XLD 模型时优先使用。</summary>
        private HalconXldContourBundle? _workingContours;

#if HALCON_ENABLED
        /// <summary>EdgesSubPix 原始 XLD（含 edge_direction），仅「手绘 ROI 边线」模式使用。</summary>
        private HObject? _nativeXldForCreate;
#endif

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
                if (string.IsNullOrWhiteSpace(TxtCalibrationJsonPath.Text))
                {
                    string guess = IoPath.GetFullPath(IoPath.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "..", "..", "..", "test_images", "chessboard_calibration_from_dir.json"));
                    if (System.IO.File.Exists(guess))
                        TxtCalibrationJsonPath.Text = guess;
                }

                if (_imgWidth > 0)
                    FitImageToView();
            };
            Unloaded += (_, _) =>
            {
                DisposeNativeXldForCreate();
                DisposeCurrentModel();
                _rawCalibImage?.Dispose();
                _rawCalibImage = null;
            };
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

            _loadedImagePath = path;
            _currentImage?.Dispose();
            _currentImage = new CalibImage(w, h, 1);
            var n = _currentImage.GetNativeStruct();
            System.Runtime.InteropServices.Marshal.Copy(_grayPixels, 0, n.data, w * h);

            _rawCalibImage?.Dispose();
            _rawCalibImage = CalibAPI.DuplicateImage(_currentImage);

            TryApplyCameraCorrections(logSuccess: true);

            ClearRoiState();
            _workingContours = null;
            UpdateContourStats();
            FitImageToView();
        }

        private void BtnBrowseCalibrationJson_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "JSON|*.json|所有文件|*.*",
                Title = "选择棋盘标定 JSON",
                FileName = string.IsNullOrWhiteSpace(TxtCalibrationJsonPath.Text)
                    ? "chessboard_calibration_from_dir.json"
                    : IoPath.GetFileName(TxtCalibrationJsonPath.Text)
            };
            if (!string.IsNullOrWhiteSpace(TxtCalibrationJsonPath.Text))
            {
                try
                {
                    string dir = IoPath.GetDirectoryName(TxtCalibrationJsonPath.Text) ?? "";
                    if (System.IO.Directory.Exists(dir))
                        dlg.InitialDirectory = dir;
                }
                catch { /* ignore */ }
            }

            if (dlg.ShowDialog() != true)
                return;
            TxtCalibrationJsonPath.Text = dlg.FileName;
        }

        private void BtnApplyCameraCorrection_Click(object sender, RoutedEventArgs e)
        {
            if (_rawCalibImage == null)
            {
                MessageBox.Show("请先加载图像", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                if (TryApplyCameraCorrections(logSuccess: true))
                    FitImageToView();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"相机矫正失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static int GetPerspectiveOutputModeFromTag(string? tag)
        {
            if (string.Equals(tag, "plane", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "full_plane", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (string.Equals(tag, "local", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "full", StringComparison.OrdinalIgnoreCase))
                return 1;
            return 0;
        }

        private int GetPerspectiveOutputMode()
        {
            if (CmbPerspectiveOutputFrame.SelectedItem is System.Windows.Controls.ComboBoxItem item)
                return GetPerspectiveOutputModeFromTag(item.Tag as string);
            return 0;
        }

        private string LoadCalibrationJsonText(bool requireExtrinsics)
        {
            string path = TxtCalibrationJsonPath.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("请指定标定 JSON 文件路径");
            return requireExtrinsics
                ? CalibAPI.NormalizeChessboardCalibrationJson(path)
                : CalibAPI.NormalizeIntrinsicsCalibrationJson(path);
        }

        /// <summary>从 _rawCalibImage 应用内参/透视矫正并刷新显示与 _currentImage。</summary>
        private bool TryApplyCameraCorrections(bool logSuccess)
        {
            if (_rawCalibImage == null)
                return false;

            bool undistort = ChkEnableUndistort.IsChecked == true;
            bool perspective = ChkEnablePerspective.IsChecked == true;
            if (!undistort && !perspective)
            {
                CommitCalibImageToUi(_rawCalibImage, disposeIncoming: false);
                if (logSuccess)
                    AppendLog("相机矫正: 未启用，使用原图");
                return true;
            }

            string calJsonIntr = LoadCalibrationJsonText(requireExtrinsics: false);
            string? calJsonPersp = ChkEnablePerspective.IsChecked == true ? LoadCalibrationJsonText(requireExtrinsics: true) : null;
            CalibImage? work = CalibAPI.DuplicateImage(_rawCalibImage);
            CalibImage? owned = work;

            try
            {
                if (undistort)
                {
                    double alpha = double.TryParse(TxtUndistortAlpha.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double av)
                        ? av : -1.0;
                    var next = CalibAPI.UndistortImage(work, calJsonIntr, alpha);
                    if (!ReferenceEquals(next, work))
                    {
                        owned?.Dispose();
                        owned = next;
                        work = next;
                    }
                    if (logSuccess)
                        AppendLog($"内参矫正: α={alpha}");
                }

                if (perspective)
                {
                    int viewIdx = int.TryParse(TxtCalibViewIndex.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int vi) ? vi : 0;
                    int cols = int.TryParse(TxtCalibBoardCols.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? c : 9;
                    int rows = int.TryParse(TxtCalibBoardRows.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : 6;
                    double sq = double.TryParse(TxtCalibSquareSizeMm.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : 25.0;
                    double pxPerMm = double.TryParse(TxtCalibPxPerMm.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double ppm) ? ppm : 1.0;
                    int perspMode = GetPerspectiveOutputMode();
                    bool assumeUnd = ChkEnableUndistort.IsChecked == true;
                    var next = CalibAPI.WarpToChessboardPlane(work, calJsonPersp!, viewIdx, cols, rows, sq, pxPerMm, perspMode,
                        assumeUndistortedInput: assumeUnd, outputSizeMode: 1);
                    if (!ReferenceEquals(next, work))
                    {
                        owned?.Dispose();
                        owned = next;
                        work = next;
                    }
                    if (logSuccess)
                    {
                        string frame = perspMode switch { 2 => "plane", 1 => "local", _ => "board" };
                        AppendLog($"透视矫正: view={viewIdx} {cols}x{rows} 格, 输出={frame}, {work.Width}x{work.Height}px");
                    }
                }

                CommitCalibImageToUi(work, disposeIncoming: true);
                owned = null;
                if (logSuccess)
                    AppendLog($"相机矫正完成 → {_imgWidth}x{_imgHeight}");
                return true;
            }
            finally
            {
                owned?.Dispose();
            }
        }

        private void CommitCalibImageToUi(CalibImage source, bool disposeIncoming)
        {
            int w = source.Width;
            int h = source.Height;
            if (w <= 0 || h <= 0)
                throw new InvalidOperationException("无效图像尺寸");

            var sn = source.GetNativeStruct();
            _grayPixels = new byte[w * h];
            System.Runtime.InteropServices.Marshal.Copy(sn.data, _grayPixels, 0, w * h);
            _imgWidth = w;
            _imgHeight = h;

            _currentImage?.Dispose();
            if (disposeIncoming)
                _currentImage = source;
            else
                _currentImage = CalibAPI.DuplicateImage(source);

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, _grayPixels, w);
            bmp.Freeze();
            DisplayImage.Source = bmp;
            DisplayImage.Width = w;
            DisplayImage.Height = h;
            ImageCanvas.Width = w;
            ImageCanvas.Height = h;
            ClearFindResultOverlay();
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
            else if (!_isDrawing || RbCircleMode?.IsChecked == true)
                RoiRect.Visibility = Visibility.Collapsed;

            UpdateCirclePreview();
            UpdatePolygonPreview();
            UpdateRingPreview();
        }

        private bool IsCircleModeActive() => RbCircleMode?.IsChecked == true;

        private bool HasUsableCircleRoi() =>
            _hasRoi && IsCircleModeActive() && _circleRadiusImage > 5;

        private bool IsPointInCircleRoi(double x, double y)
        {
            if (!HasUsableCircleRoi())
                return false;
            double dx = x - _circleCenterImage.X;
            double dy = y - _circleCenterImage.Y;
            return dx * dx + dy * dy <= _circleRadiusImage * _circleRadiusImage;
        }

        private static List<Point> CircleToPolygonPoints(Point center, double radius, int segments = 64)
        {
            var list = new List<Point>(segments + 1);
            for (int i = 0; i <= segments; i++)
            {
                double t = 2 * Math.PI * i / segments;
                list.Add(new Point(
                    center.X + radius * Math.Cos(t),
                    center.Y + radius * Math.Sin(t)));
            }

            return list;
        }

        private void ResetCircleDrawState()
        {
            _circleCenterImage = default;
            _circleRadiusImage = 0;
            if (RoiCircle != null)
                RoiCircle.Visibility = Visibility.Collapsed;
        }

        private void UpdateCirclePreview()
        {
            if (RoiCircle == null)
                return;

            if (!IsCircleModeActive())
            {
                RoiCircle.Visibility = Visibility.Collapsed;
                return;
            }

            if (!HasUsableCircleRoi() && !(_isDrawing && _circleRadiusImage > 0))
            {
                RoiCircle.Visibility = Visibility.Collapsed;
                return;
            }

            double r = Math.Max(1, _circleRadiusImage);
            double d = r * 2;
            Canvas.SetLeft(RoiCircle, _circleCenterImage.X - r);
            Canvas.SetTop(RoiCircle, _circleCenterImage.Y - r);
            RoiCircle.Width = d;
            RoiCircle.Height = d;
            RoiCircle.Visibility = Visibility.Visible;
        }

        private bool IsRingModeActive() => RbRingMode?.IsChecked == true;

        private bool HasUsableRingRoi() =>
            _hasRingRoi && IsRingModeActive() &&
            _ringOuterPath.IsClosed && _ringInnerPath.IsClosed &&
            _ringOuterPath.VertexCount >= 3 && _ringInnerPath.VertexCount >= 3;

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
            return IsPointInPolygon(x, y, GetRingOuterFlattened()) &&
                   !IsPointInPolygon(x, y, GetRingInnerFlattened());
        }

        private void InvalidateRingFlatCache()
        {
            _ringFlatDirty = true;
            _cachedRingOuterFlat = null;
            _cachedRingInnerFlat = null;
        }

        private void RebuildRingFlatCacheIfNeeded()
        {
            if (!_ringFlatDirty && _cachedRingOuterFlat != null && _cachedRingInnerFlat != null)
                return;
            _cachedRingOuterFlat = _ringOuterPath.BuildFlattenedPolygon(
                _ringOuterPath.IsClosed, arcSegments: ClosedArcSegments);
            _cachedRingInnerFlat = _ringInnerPath.BuildFlattenedPolygon(
                _ringInnerPath.IsClosed, arcSegments: ClosedArcSegments);
            _ringFlatDirty = false;
        }

        private List<Point> GetRingOuterFlattened()
        {
            RebuildRingFlatCacheIfNeeded();
            return _cachedRingOuterFlat ?? new List<Point>();
        }

        private List<Point> GetRingInnerFlattened()
        {
            RebuildRingFlatCacheIfNeeded();
            return _cachedRingInnerFlat ?? new List<Point>();
        }

        private RoiContourPath GetActiveRingPath() =>
            _ringPolygonPhase == 0 ? _ringOuterPath : _ringInnerPath;

        private string ActiveRingLabel() => _ringPolygonPhase == 0 ? "外圈" : "内圈";

        private void ResetRingDrawState()
        {
            _hasRingRoi = false;
            _ringPolygonPhase = 0;
            _ringOuterPath.Clear();
            _ringInnerPath.Clear();
            InvalidateRingFlatCache();
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

        private void RebuildRingPolyline(Polyline? line, RoiContourPath path)
        {
            if (line == null)
                return;

            if (path.VertexCount < 1)
            {
                line.Visibility = Visibility.Collapsed;
                line.Points.Clear();
                return;
            }

            int segs = path.IsClosed ? ClosedArcSegments : PreviewArcSegments;
            var pts = path.BuildFlattenedPolygon(path.IsClosed, arcSegments: segs);
            line.Visibility = Visibility.Visible;
            line.Points = new PointCollection(pts);
            if (path.IsClosed && pts.Count > 0 && Dist(pts[0], pts[^1]) > 1e-6)
                line.Points.Add(pts[0]);
        }

        private void UpdateRingPreview()
        {
            if (!IsRingModeActive())
            {
                if (RingOuterLine != null) RingOuterLine.Visibility = Visibility.Collapsed;
                if (RingInnerLine != null) RingInnerLine.Visibility = Visibility.Collapsed;
                return;
            }

            RebuildRingPolyline(RingOuterLine, _ringOuterPath);
            if (_ringPolygonPhase >= 1 || _ringInnerPath.VertexCount > 0)
                RebuildRingPolyline(RingInnerLine, _ringInnerPath);
            else if (RingInnerLine != null)
            {
                RingInnerLine.Visibility = Visibility.Collapsed;
                RingInnerLine.Points.Clear();
            }
        }

        private void UpdateRingRubberVisual()
        {
            if (PolygonRubberLine == null)
                return;

            if (!IsRingModeActive() || _ringPolygonPhase >= 2)
            {
                PolygonRubberLine.Visibility = Visibility.Collapsed;
                PolygonRubberLine.Points.Clear();
                return;
            }

            var path = GetActiveRingPath();
            if (path.VertexCount < 1 && _arcDraftEnd == null)
            {
                PolygonRubberLine.Visibility = Visibility.Collapsed;
                PolygonRubberLine.Points.Clear();
                return;
            }

            var rubber = new List<Point>();
            Point last = path.VertexCount > 0 ? path.Vertices[^1] : default;

            if (_arcDraftEnd is Point end)
            {
                Point via = _polygonCursorImage ?? end;
                foreach (var p in RoiContourPath.SampleArc(last, via, end, PreviewArcSegments))
                    rubber.Add(p);
            }
            else if (_polygonCursorImage is Point cursor && path.VertexCount > 0)
            {
                rubber.Add(last);
                rubber.Add(cursor);
            }

            if (rubber.Count < 2)
            {
                PolygonRubberLine.Visibility = Visibility.Collapsed;
                PolygonRubberLine.Points.Clear();
                return;
            }

            PolygonRubberLine.Visibility = Visibility.Visible;
            PolygonRubberLine.Points = new PointCollection(rubber);
        }

#if HALCON_ENABLED
        private HObject? TryBuildRingRegion()
        {
            if (!HasUsableRingRoi())
                return null;
            var outer = GetRingOuterFlattened().Select(p => new Point2D(p.X, p.Y)).ToList();
            var inner = GetRingInnerFlattened().Select(p => new Point2D(p.X, p.Y)).ToList();
            if (outer.Count < 3 || inner.Count < 3)
                return null;
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

        private double GetPolygonCloseDistancePx()
        {
            if (TxtPolygonCloseDist != null
                && double.TryParse(TxtPolygonCloseDist.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                && d > 0)
                return d;
            return DefaultPolygonCloseDistance;
        }

        private bool IsNextSegmentArc() => RbNextSegmentArc?.IsChecked == true;

        private void CancelArcDraft()
        {
            _arcDraftEnd = null;
        }

        private void InvalidatePolygonFlatCache()
        {
            _polygonFlatDirty = true;
            _cachedPolygonFlat = null;
        }

        private List<Point> GetPolygonFlattened(bool forHitTest)
        {
            if (!_polygonFlatDirty && _cachedPolygonFlat != null)
                return _cachedPolygonFlat;

            bool closed = _roiPath.IsClosed;
            _cachedPolygonFlat = _roiPath.BuildFlattenedPolygon(closed, arcSegments: ClosedArcSegments);
            _polygonFlatDirty = false;
            return _cachedPolygonFlat;
        }

        private List<Point> BuildCommittedPreviewPoints()
        {
            int segs = _roiPath.IsClosed ? ClosedArcSegments : PreviewArcSegments;
            return _roiPath.BuildFlattenedPolygon(_roiPath.IsClosed, arcSegments: segs);
        }

        private void RebuildCommittedPolygonVisual()
        {
            if (PolygonLine == null)
                return;

            InvalidatePolygonFlatCache();

            if (_roiPath.VertexCount < 1 && _arcDraftEnd == null)
            {
                PolygonLine.Visibility = Visibility.Collapsed;
                PolygonLine.Points.Clear();
                return;
            }

            var pts = BuildCommittedPreviewPoints();
            PolygonLine.Visibility = Visibility.Visible;
            PolygonLine.Points = new PointCollection(pts);

            if (_roiPath.IsClosed && pts.Count > 0 && Dist(pts[0], pts[^1]) > 1e-6)
                PolygonLine.Points.Add(pts[0]);
        }

        private void UpdatePolygonRubberVisual()
        {
            if (PolygonRubberLine == null)
                return;

            if (IsRingModeActive())
                return;

            if (_roiPath.VertexCount < 1)
            {
                PolygonRubberLine.Visibility = Visibility.Collapsed;
                PolygonRubberLine.Points.Clear();
                return;
            }

            var rubber = new List<Point>();
            Point last = _roiPath.Vertices[^1];

            if (_arcDraftEnd is Point end)
            {
                Point via = _polygonCursorImage ?? end;
                foreach (var p in RoiContourPath.SampleArc(last, via, end, PreviewArcSegments))
                    rubber.Add(p);
            }
            else if (_polygonCursorImage is Point cursor)
            {
                rubber.Add(last);
                rubber.Add(cursor);
            }

            if (rubber.Count < 2)
            {
                PolygonRubberLine.Visibility = Visibility.Collapsed;
                PolygonRubberLine.Points.Clear();
                return;
            }

            PolygonRubberLine.Visibility = Visibility.Visible;
            PolygonRubberLine.Points = new PointCollection(rubber);
        }

        private static bool PointsNear(Point a, Point b, double eps = 1e-6) => Dist(a, b) < eps;

        private bool IsArcDraftClosingToStart(Point draftEnd) =>
            IsArcDraftClosingToStart(_roiPath, draftEnd);

        private static bool IsArcDraftClosingToStart(RoiContourPath path, Point draftEnd) =>
            path.VertexCount >= 3 && PointsNear(draftEnd, path.Vertices[0]);

        private bool IsNearPolygonStart(Point imgPt) =>
            !_hasRoi && RbPolygonMode?.IsChecked == true && _roiPath.VertexCount >= 3
            && Dist(_roiPath.Vertices[0], imgPt) < GetPolygonCloseDistancePx();

        private bool IsNearActiveRingStart(Point imgPt)
        {
            if (_ringPolygonPhase >= 2)
                return false;
            var path = GetActiveRingPath();
            return !path.IsClosed && path.VertexCount >= 3
                && Dist(path.Vertices[0], imgPt) < GetPolygonCloseDistancePx();
        }

        private bool TryClosePolygonWithLine()
        {
            if (_roiPath.VertexCount < 3)
            {
                MessageBox.Show("多边形至少需要 3 个顶点才能闭合。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            _roiPath.CloseLoop(RoiEdgeKind.Line, null);
            return FinishPolygonRoiClosed("直线");
        }

        private bool TryClosePolygonWithArc(Point via)
        {
            if (_roiPath.VertexCount < 3)
                return false;

            if (!RoiContourPath.IsValidArcVia(_roiPath.Vertices[^1], via, _roiPath.Vertices[0]))
            {
                MessageBox.Show("弧上经过点与起点、终点几乎共线，请换一点。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            try
            {
                _roiPath.CloseLoop(RoiEdgeKind.Arc, via);
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            return FinishPolygonRoiClosed("圆弧");
        }

        private bool FinishPolygonRoiClosed(string closingKindLabel)
        {
            _hasRoi = true;
            _polygonCursorImage = null;
            CancelArcDraft();
            UpdatePolygonPreview();
            UpdateThresholdPreview();
            AppendLog($"多边形 ROI 已{closingKindLabel}闭合: {_roiPath.VertexCount} 个顶点, {_roiPath.EdgeKinds.Count} 段");
            return true;
        }

        private bool TryFinalizePolygonRoi()
        {
            if (_arcDraftEnd != null)
            {
                MessageBox.Show("圆弧段尚未完成：请再点击弧上经过的一点，或撤销后重绘。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            return TryClosePolygonWithLine();
        }

        /// <summary>单击起点（在闭合容差内）时闭合，不添加新顶点。</summary>
        private bool TryClosePolygonAtPoint(Point imgPt)
        {
            if (!IsNearPolygonStart(imgPt))
                return false;

            if (_arcDraftEnd != null)
                return false;

            if (IsNextSegmentArc())
                return false;

            return TryClosePolygonWithLine();
        }

        private void AdvanceRingPhaseAfterClose(RoiContourPath path)
        {
            InvalidateRingFlatCache();
            CancelArcDraft();
            _polygonCursorImage = null;

            if (_ringPolygonPhase == 0)
            {
                _ringPolygonPhase = 1;
                AppendLog($"环形外圈已闭合（{path.VertexCount} 顶点，{path.EdgeKinds.Count} 段），请绘制内圈");
            }
            else
            {
                _ringPolygonPhase = 2;
                _hasRingRoi = true;
                _hasRoi = true;
                AppendLog($"环形 ROI 完成：外 {_ringOuterPath.VertexCount} 顶点，内 {_ringInnerPath.VertexCount} 顶点");
            }
        }

        private bool TryCloseActiveRingWithLine()
        {
            var path = GetActiveRingPath();
            if (path.VertexCount < 3)
            {
                MessageBox.Show("当前圈至少需要 3 个顶点才能闭合。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            path.CloseLoop(RoiEdgeKind.Line, null);
            AdvanceRingPhaseAfterClose(path);
            return true;
        }

        private bool TryCloseActiveRingWithArc(Point via)
        {
            var path = GetActiveRingPath();
            if (path.VertexCount < 3)
                return false;

            if (!RoiContourPath.IsValidArcVia(path.Vertices[^1], via, path.Vertices[0]))
            {
                MessageBox.Show("弧上经过点与起点、终点几乎共线，请换一点。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            try
            {
                path.CloseLoop(RoiEdgeKind.Arc, via);
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            AdvanceRingPhaseAfterClose(path);
            return true;
        }

        private bool TryCloseActiveRingAtPoint(Point imgPt)
        {
            if (!IsNearActiveRingStart(imgPt))
                return false;
            if (_arcDraftEnd != null)
                return false;
            if (IsNextSegmentArc())
                return false;
            return TryCloseActiveRingWithLine();
        }

        private bool HandleRingContourClick(Point imgPt)
        {
            if (TryCloseActiveRingAtPoint(imgPt))
                return true;

            var path = GetActiveRingPath();
            string label = ActiveRingLabel();

            if (_arcDraftEnd is Point end)
            {
                if (IsArcDraftClosingToStart(path, end))
                {
                    TryCloseActiveRingWithArc(imgPt);
                    return true;
                }

                path.AddArcSegment(end, imgPt);
                CancelArcDraft();
                AppendLog($"{label} 圆弧段已添加，顶点数 {path.VertexCount}");
                return false;
            }

            if (IsNearActiveRingStart(imgPt))
            {
                if (IsNextSegmentArc())
                {
                    _arcDraftEnd = path.Vertices[0];
                    AppendLog($"{label} 圆弧闭合：请点击弧上经过的一点");
                    return false;
                }

                return false;
            }

            if (IsNextSegmentArc())
            {
                if (path.VertexCount == 0)
                {
                    path.AddFirstVertex(imgPt);
                    AppendLog($"{label}: 第 1 点（圆弧段起点）");
                    return false;
                }

                _arcDraftEnd = imgPt;
                AppendLog($"{label} 圆弧：已设终点，请点击弧上经过的一点");
                return false;
            }

            if (path.VertexCount == 0)
            {
                path.AddFirstVertex(imgPt);
                AppendLog($"{label}: 第 1 点");
            }
            else
            {
                path.AddLineSegment(imgPt);
                AppendLog($"{label} 直线段 → 顶点 {path.VertexCount}");
            }

            return false;
        }

        private void BtnClosePolygon_Click(object sender, RoutedEventArgs e)
        {
            if (RbPolygonMode.IsChecked == true)
            {
                AppendLog(_roiPath.VertexCount >= 3 && !_hasRoi
                    ? "请单击第一个顶点（起点）完成闭合"
                    : "多边形至少需要 3 个点后再单击起点闭合");
                return;
            }

            if (IsRingModeActive())
            {
                AppendLog(_ringPolygonPhase switch
                {
                    0 => "请单击外圈起点完成外圈闭合",
                    1 => "请单击内圈起点完成内圈闭合",
                    _ => "环形 ROI 已完成"
                });
            }
        }

        private void ClearRoiState()
        {
            _hasRoi = false;
            _isDrawing = false;
            _roiPath.Clear();
            CancelArcDraft();
            _polygonCursorImage = null;
            InvalidatePolygonFlatCache();
            ResetCircleDrawState();
            ResetRingDrawState();

            try
            {
                if (RoiRect != null) RoiRect.Visibility = Visibility.Collapsed;
                if (PolygonLine != null)
                {
                    PolygonLine.Visibility = Visibility.Collapsed;
                    PolygonLine.Points.Clear();
                }
                if (PolygonRubberLine != null)
                {
                    PolygonRubberLine.Visibility = Visibility.Collapsed;
                    PolygonRubberLine.Points.Clear();
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
            if (trimmed)
                DisposeNativeXldForCreate();
            _workingContours = bundle;
            UpdateContourStats();
            DrawXldPreviewOverlay(bundle, trimmed ? Brushes.Orange : Brushes.DeepSkyBlue);
        }

#if HALCON_ENABLED
        private void DisposeNativeXldForCreate()
        {
            _nativeXldForCreate?.Dispose();
            _nativeXldForCreate = null;
        }
#endif

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

            for (int m = 0; m < rows.Length; m++)
            {
                Brush brush = FindMatchBrushes[m % FindMatchBrushes.Length];
                double matchRow = rows[m];
                double matchCol = cols[m];
                double angleDeg = angles.Length > m ? angles[m] : 0;

                Point2D[][] matchContours = Array.Empty<Point2D[]>();
                if (_modelId >= 0)
                {
                    try
                    {
                        matchContours = _modelKind == HalconFlowModelKind.Deformable
                            ? HalconFlowBridge.GetDeformableModelContourPoints(_modelId, 1)
                            : HalconFlowBridge.GetShapeModelContourPoints(_modelId, 1);
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"[提示] 无法读取模型轮廓，仅显示匹配中心: {ex.Message}");
                    }
                }

                foreach (Point2D[] contour in matchContours)
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

        private void DrawDeformableFindResultOverlay(
            double[] rows,
            double[] cols,
            double[] scores,
            IReadOnlyList<Point2D[]> deformedContoursPerMatch)
        {
            if (FindResultOverlay == null || rows.Length == 0)
                return;

            ClearFindResultOverlay();

            for (int m = 0; m < rows.Length; m++)
            {
                Brush brush = FindMatchBrushes[m % FindMatchBrushes.Length];
                double matchRow = rows[m];
                double matchCol = cols[m];

                if (m < deformedContoursPerMatch.Count)
                {
                    Point2D[] contour = deformedContoursPerMatch[m];
                    if (contour != null && contour.Length >= 2)
                    {
                        var pts = new PointCollection(contour.Length);
                        foreach (Point2D pt in contour)
                            pts.Add(new Point(pt.X, pt.Y));

                        FindResultOverlay.Children.Add(new Polyline
                        {
                            Points = pts,
                            Stroke = brush,
                            StrokeThickness = 2.5,
                            Fill = Brushes.Transparent
                        });
                    }
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

                if (scores.Length > m)
                {
                    var label = new TextBlock
                    {
                        Text = $"{scores[m]:F3}",
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
        }

        private void DisposeCurrentModel()
        {
            ClearFindResultOverlay();
#if HALCON_ENABLED
            if (_modelId >= 0)
            {
                try { HalconFlowBridge.ClearModel(_modelId); }
                catch { /* ignored */ }
                _modelId = -1;
            }
            _modelKind = HalconFlowModelKind.Shape;
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
            else if (IsCircleModeActive())
            {
                _isDrawing = true;
                _circleCenterImage = imgPt;
                _circleRadiusImage = 0;
                UpdateCirclePreview();
            }
            else if (RbPolygonMode.IsChecked == true)
            {
                if (HandlePolygonClick(imgPt))
                {
                    ViewHost.ReleaseMouseCapture();
                    return;
                }

                RebuildCommittedPolygonVisual();
                UpdatePolygonRubberVisual();
                if (_hasRoi)
                    UpdateThresholdPreview();
            }
            else if (IsRingModeActive())
            {
                bool closedStep = HandleRingContourClick(imgPt);
                UpdateRingPreview();
                UpdateRingRubberVisual();
                if (closedStep && _ringPolygonPhase >= 2)
                {
                    UpdateThresholdPreview();
                    ViewHost.ReleaseMouseCapture();
                }
            }

            ViewHost.CaptureMouse();
        }

        private bool HandlePolygonClick(Point imgPt)
        {
            if (TryClosePolygonAtPoint(imgPt))
                return true;

            if (_arcDraftEnd is Point end)
            {
                if (IsArcDraftClosingToStart(end))
                {
                    if (TryClosePolygonWithArc(imgPt))
                        return true;
                    return false;
                }

                _roiPath.AddArcSegment(end, imgPt);
                CancelArcDraft();
                AppendLog($"圆弧段已添加，顶点数 {_roiPath.VertexCount}");
                return false;
            }

            if (IsNearPolygonStart(imgPt))
            {
                if (IsNextSegmentArc())
                {
                    _arcDraftEnd = _roiPath.Vertices[0];
                    AppendLog("圆弧闭合：请点击弧上经过的一点");
                    return false;
                }

                return false;
            }

            if (IsNextSegmentArc())
            {
                if (_roiPath.VertexCount == 0)
                {
                    _roiPath.AddFirstVertex(imgPt);
                    AppendLog("多边形: 第 1 点（圆弧段起点）");
                    return false;
                }

                _arcDraftEnd = imgPt;
                AppendLog("圆弧：已设终点，请点击弧上经过的一点");
                return false;
            }

            if (_roiPath.VertexCount == 0)
            {
                _roiPath.AddFirstVertex(imgPt);
                AppendLog("多边形: 第 1 点");
            }
            else
            {
                _roiPath.AddLineSegment(imgPt);
                AppendLog($"直线段 → 顶点 {_roiPath.VertexCount}");
            }

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

            if (RbPolygonMode.IsChecked == true && !_hasRoi &&
                (_roiPath.VertexCount > 0 || _arcDraftEnd != null))
            {
                long now = Environment.TickCount64;
                if (now - _lastPolygonPreviewMoveMs < PolygonPreviewMoveIntervalMs)
                    return;
                _lastPolygonPreviewMoveMs = now;

                _polygonCursorImage = GetImagePointFromMouse(e);
                UpdatePolygonRubberVisual();
            }
            else if (IsRingModeActive() && _ringPolygonPhase < 2)
            {
                var active = GetActiveRingPath();
                if (active.VertexCount > 0 || _arcDraftEnd != null)
                {
                    long now = Environment.TickCount64;
                    if (now - _lastPolygonPreviewMoveMs < PolygonPreviewMoveIntervalMs)
                        return;
                    _lastPolygonPreviewMoveMs = now;

                    _polygonCursorImage = GetImagePointFromMouse(e);
                    UpdateRingRubberVisual();
                }
            }

            if (!_isDrawing) return;

            var cur = GetImagePointFromMouse(e);

            if (IsCircleModeActive())
            {
                double dx = cur.X - _circleCenterImage.X;
                double dy = cur.Y - _circleCenterImage.Y;
                _circleRadiusImage = Math.Sqrt(dx * dx + dy * dy);
                ClampCircleRadiusToImage();
                UpdateCirclePreview();
                return;
            }

            if (RbRectMode.IsChecked != true) return;

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

            if (!_isDrawing) return;

            _isDrawing = false;
            var cur = GetImagePointFromMouse(e);

            if (IsCircleModeActive())
            {
                double dx = cur.X - _circleCenterImage.X;
                double dy = cur.Y - _circleCenterImage.Y;
                _circleRadiusImage = Math.Sqrt(dx * dx + dy * dy);
                ClampCircleRadiusToImage();

                if (_circleRadiusImage > 5)
                {
                    _hasRoi = true;
                    RefreshRoiVisuals();
                    UpdateThresholdPreview();
                    AppendLog($"圆形ROI: 圆心=({_circleCenterImage.X:F0},{_circleCenterImage.Y:F0}) R={_circleRadiusImage:F0}");
                }
                else
                    ResetCircleDrawState();

                return;
            }

            if (RbRectMode.IsChecked == true)
            {
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

        private void ClampCircleRadiusToImage()
        {
            if (_imgWidth <= 0 || _imgHeight <= 0 || _circleRadiusImage <= 0)
                return;

            double maxR = Math.Min(
                Math.Min(_circleCenterImage.X, _imgWidth - 1 - _circleCenterImage.X),
                Math.Min(_circleCenterImage.Y, _imgHeight - 1 - _circleCenterImage.Y));
            if (maxR < 1)
                maxR = 1;
            _circleRadiusImage = Math.Min(_circleRadiusImage, maxR);
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
            RebuildCommittedPolygonVisual();
            UpdatePolygonRubberVisual();
        }

        // ───────── 撤销多边形点 ─────────
        private void BtnUndoPoint_Click(object sender, RoutedEventArgs e)
        {
            if (IsCircleModeActive() && HasUsableCircleRoi())
            {
                _hasRoi = false;
                ResetCircleDrawState();
                UpdateThresholdPreview();
                AppendLog("圆形 ROI 已清除");
                return;
            }

            if (IsRingModeActive())
            {
                if (_arcDraftEnd != null)
                {
                    CancelArcDraft();
                    UpdateRingPreview();
                    UpdateRingRubberVisual();
                    AppendLog("已取消未完成的圆弧段");
                    return;
                }

                _hasRingRoi = false;
                if (_ringPolygonPhase >= 2)
                    _ringPolygonPhase = 1;

                if (_ringInnerPath.VertexCount > 0)
                {
                    _ringInnerPath.RemoveLastVertex();
                    _hasRoi = false;
                    AppendLog($"内圈撤销: 剩余 {_ringInnerPath.VertexCount} 个顶点");
                }
                else if (_ringOuterPath.VertexCount > 0)
                {
                    _ringOuterPath.RemoveLastVertex();
                    _ringPolygonPhase = 0;
                    _hasRoi = false;
                    AppendLog($"外圈撤销: 剩余 {_ringOuterPath.VertexCount} 个顶点");
                }
                else
                    AppendLog("环形 ROI：已空");

                InvalidateRingFlatCache();
                UpdateRingPreview();
                UpdateRingRubberVisual();
                UpdateThresholdPreview();
                return;
            }

            if (_arcDraftEnd != null)
            {
                CancelArcDraft();
                UpdatePolygonPreview();
                AppendLog("已取消未完成的圆弧段");
                return;
            }

            if (_roiPath.VertexCount > 0)
            {
                _roiPath.RemoveLastVertex();
                _hasRoi = false;
                UpdatePolygonPreview();
                if (_hasRoi)
                    UpdateThresholdPreview();
                AppendLog($"撤销: 剩余 {_roiPath.VertexCount} 个顶点");
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
            bool useCircle = HasUsableCircleRoi();
            bool usePoly = RbPolygonMode?.IsChecked == true && _hasRoi && _roiPath.VertexCount >= 3;
            bool useRing = HasUsableRingRoi();
            List<Point>? polyFlat = usePoly ? GetPolygonFlattened(forHitTest: true) : null;

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
                    else if (useCircle)
                    {
                        if (!IsPointInCircleRoi(x + 0.5, y + 0.5))
                            continue;
                    }
                    else if (usePoly && polyFlat != null)
                    {
                        if (!IsPointInPolygon(x + 0.5, y + 0.5, polyFlat))
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
                if (useCircle)
                    return IsPointInCircleRoi(x + 0.5, y + 0.5);
                if (usePoly)
                {
                    var flat = GetPolygonFlattened(forHitTest: true);
                    return IsPointInPolygon(x + 0.5, y + 0.5, flat);
                }
                if (useRing)
                    return IsPointInRingRoi(x + 0.5, y + 0.5);
                return true;
            }

            var (minG, maxG) = HalconFlowBridge.SuggestGrayRangeInRegion(
                _grayPixels, _imgWidth, _imgHeight, thresh, InsideRoi);
            TxtMinGray.Text = minG.ToString(CultureInfo.InvariantCulture);
            TxtMaxGray.Text = maxG.ToString(CultureInfo.InvariantCulture);
            AppendLog(useRect || useCircle || usePoly || useRing
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

            bool drawingOpenPoly = RbPolygonMode?.IsChecked == true && !_hasRoi;
            bool drawingOpenRing = IsRingModeActive() && !_hasRingRoi;
            if (drawingOpenPoly || drawingOpenRing)
            {
                ThresholdOverlayImage.Visibility = Visibility.Collapsed;
                return;
            }

            int w = _imgWidth;
            int h = _imgHeight;
            int x0 = 0, y0 = 0, x1 = w, y1 = h;
            bool useRect = _hasRoi && RbRectMode?.IsChecked == true;
            bool useCircle = HasUsableCircleRoi();
            bool usePoly = RbPolygonMode?.IsChecked == true && _hasRoi && _roiPath.VertexCount >= 3;
            bool useRing = HasUsableRingRoi();
            List<Point>? polyFlat = usePoly ? GetPolygonFlattened(forHitTest: true) : null;

            if (useRect)
            {
                x0 = Math.Max(0, (int)Math.Floor(_roiRectImage.X));
                y0 = Math.Max(0, (int)Math.Floor(_roiRectImage.Y));
                x1 = Math.Min(w, (int)Math.Ceiling(_roiRectImage.Right));
                y1 = Math.Min(h, (int)Math.Ceiling(_roiRectImage.Bottom));
            }
            else if (useCircle)
            {
                x0 = Math.Max(0, (int)Math.Floor(_circleCenterImage.X - _circleRadiusImage));
                y0 = Math.Max(0, (int)Math.Floor(_circleCenterImage.Y - _circleRadiusImage));
                x1 = Math.Min(w, (int)Math.Ceiling(_circleCenterImage.X + _circleRadiusImage));
                y1 = Math.Min(h, (int)Math.Ceiling(_circleCenterImage.Y + _circleRadiusImage));
            }
            else if (useRing)
            {
                var ringOuter = GetRingOuterFlattened();
                if (ringOuter.Count > 0)
                {
                    x0 = Math.Max(0, (int)Math.Floor(ringOuter.Min(p => p.X)));
                    y0 = Math.Max(0, (int)Math.Floor(ringOuter.Min(p => p.Y)));
                    x1 = Math.Min(w, (int)Math.Ceiling(ringOuter.Max(p => p.X)));
                    y1 = Math.Min(h, (int)Math.Ceiling(ringOuter.Max(p => p.Y)));
                }
            }

            int stride = w * 4;
            var pixels = new byte[h * stride];
            int hitCount = 0;

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    if (useCircle && !IsPointInCircleRoi(x + 0.5, y + 0.5))
                        continue;
                    if (usePoly && polyFlat != null &&
                        !IsPointInPolygon(x + 0.5, y + 0.5, polyFlat))
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
            if (PnlRoiBoundaryDir != null)
                PnlRoiBoundaryDir.Visibility = src == "PolygonXld" ? Visibility.Visible : Visibility.Collapsed;
            if (PnlThreshold != null)
                PnlThreshold.Visibility = Visibility.Visible;
            if (BtnClosePolygon != null)
                BtnClosePolygon.Visibility = RbPolygonMode?.IsChecked == true || IsRingModeActive()
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (PnlScale != null)
                PnlScale.Visibility = kind is "ScaledShape" or "Deformable" or "PlanarDeformable" ? Visibility.Visible : Visibility.Collapsed;
            if (RowContrast != null)
                RowContrast.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        }

#if HALCON_ENABLED
        private HalconShapeModelSourceKind ParseSourceKind()
        {
            string? tag = (CmbTemplateSource?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (tag == "RoiPathEdgesXld")
                return HalconShapeModelSourceKind.PolygonXld;
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
            if (HasUsableCircleRoi())
                return HalconFlowBridge.GenRegionCircle(
                    _circleCenterImage.Y, _circleCenterImage.X, _circleRadiusImage);

            if (RbPolygonMode.IsChecked == true && _roiPath.VertexCount >= 3)
            {
                var flat = GetPolygonFlattened(forHitTest: _hasRoi);
                var pts = flat.Select(p => new Point2D(p.X, p.Y)).ToList();
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
                if (HasUsableCircleRoi())
                    return HalconFlowBridge.GenRegionCircle(
                        _circleCenterImage.Y, _circleCenterImage.X, _circleRadiusImage);
                if (!_hasRoi || RbRectMode.IsChecked != true)
                    throw new InvalidOperationException("矩形灰度模板：请用「矩形」「圆形」或「环形」模式绘制 ROI。");
                return HalconFlowBridge.GenRegionRectangle(
                    _roiRectImage.Y, _roiRectImage.X, _roiRectImage.Bottom, _roiRectImage.Right);
            }

            if (HasUsableCircleRoi())
                return HalconFlowBridge.GenRegionCircle(
                    _circleCenterImage.Y, _circleCenterImage.X, _circleRadiusImage);

            if (_roiPath.VertexCount < 3)
                throw new InvalidOperationException("多边形灰度模板：请用「多边形」「圆形」或「环形」模式绘制 ROI。");
            var flat = GetPolygonFlattened(forHitTest: true);
            var pts = flat.Select(p => new Point2D(p.X, p.Y)).ToList();
            return HalconFlowBridge.GenRegionPolygonFilled(pts);
        }

        private IReadOnlyList<Point2D>? TryGetReferencePathForEdgeSampling()
        {
            if (_roiPath.VertexCount >= 3)
            {
                return GetPolygonFlattened(forHitTest: true)
                    .Select(p => new Point2D(p.X, p.Y))
                    .ToList();
            }

            if (HasUsableCircleRoi())
            {
                return CircleToPolygonPoints(_circleCenterImage, _circleRadiusImage)
                    .Select(p => new Point2D(p.X, p.Y))
                    .ToList();
            }

            if (_hasRoi && RbRectMode?.IsChecked == true)
            {
                return new[]
                {
                    new Point2D(_roiRectImage.X, _roiRectImage.Y),
                    new Point2D(_roiRectImage.Right, _roiRectImage.Y),
                    new Point2D(_roiRectImage.Right, _roiRectImage.Bottom),
                    new Point2D(_roiRectImage.Left, _roiRectImage.Bottom)
                };
            }

#if HALCON_ENABLED
            if (HasUsableRingRoi())
            {
                var outer = GetRingOuterFlattened().Select(p => new Point2D(p.X, p.Y)).ToList();
                if (outer.Count >= 3)
                    return outer;
            }
#endif
            return null;
        }

        private RoiGradientPolarity ReadGradientPolarity() =>
            RbGradientInward?.IsChecked == true ? RoiGradientPolarity.Inward : RoiGradientPolarity.Outward;

        private RoiGradientPolarity ReadRingInnerGradientPolarity(RoiGradientPolarity outerPolarity)
        {
            string mode = (CmbRingInnerGradientMode?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "opposite";
            if (string.Equals(mode, "same", StringComparison.OrdinalIgnoreCase))
                return outerPolarity;
            if (string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase))
                return RbInnerGradientInward?.IsChecked == true ? RoiGradientPolarity.Inward : RoiGradientPolarity.Outward;
            return outerPolarity.Opposite();
        }

        private void RingInnerGradientMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || PnlRingInnerGradientCustom == null || CmbRingInnerGradientMode == null)
                return;
            string mode = (CmbRingInnerGradientMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "opposite";
            PnlRingInnerGradientCustom.Visibility = string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private HalconXldContourBundle ExtractDirectedHandPathBundle(HalconShapeModelCreateOptions opt)
        {
            if (IsRingModeActive())
            {
                if (!HasUsableRingRoi())
                    throw new InvalidOperationException("环形 ROI 请先完成外圈与内圈的闭合。");
            }
            else if (RbPolygonMode?.IsChecked == true && !_hasRoi)
            {
                throw new InvalidOperationException("多边形 ROI 请先单击起点闭合。");
            }

            DisposeNativeXldForCreate();

            if (HasUsableRingRoi())
            {
                var outer = GetRingOuterFlattened().Select(p => new Point2D(p.X, p.Y)).ToList();
                var inner = GetRingInnerFlattened().Select(p => new Point2D(p.X, p.Y)).ToList();
                if (outer.Count < 3 || inner.Count < 3)
                    throw new InvalidOperationException("环形外圈/内圈边线采样点不足，请增加顶点或圆弧段。");

                var outerPol = ReadGradientPolarity();
                var innerPol = ReadRingInnerGradientPolarity(outerPol);
                var (bundle, native) = HalconFlowBridge.BuildRingRoiBoundaryXldWithGradientDirection(
                    _imgWidth,
                    _imgHeight,
                    outer,
                    inner,
                    outerPol,
                    innerPol,
                    opt.MinContourPoints);
                _nativeXldForCreate = native;

                string outerLabel = outerPol == RoiGradientPolarity.Outward ? "向外" : "向内";
                string innerLabel = innerPol == RoiGradientPolarity.Outward ? "向外" : "向内";
                string mode = (CmbRingInnerGradientMode?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "与外圈相反";
                AppendLog($"环形手绘边线: 外圈 {outer.Count} 点(梯度{outerLabel})，内圈 {inner.Count} 点(梯度{innerLabel})，{mode}；轮廓 {bundle.ContourCount} 条");
                return bundle;
            }

            IReadOnlyList<Point2D>? path = TryGetReferencePathForEdgeSampling();
            if (path == null || path.Count < 3)
                throw new InvalidOperationException("请先绘制并闭合 ROI（多边形/圆/矩形/环形）。");

            var (bundleSingle, nativeSingle) = HalconFlowBridge.BuildRoiBoundaryXldWithGradientDirection(
                _imgWidth,
                _imgHeight,
                path,
                ReadGradientPolarity(),
                opt.MinContourPoints);
            _nativeXldForCreate = nativeSingle;
            string pol = ReadGradientPolarity() == RoiGradientPolarity.Outward ? "向外" : "向内";
            AppendLog($"手绘边线模板: {path.Count} 采样点，梯度{pol}（轮廓沿 ROI 边线）");
            return bundleSingle;
        }

        private HalconXldContourBundle? ExtractXldBundleForCreate(HalconShapeModelCreateOptions opt, double minGray, double maxGray)
        {
            if (_currentImage == null) return null;

            if (opt.SourceKind != HalconShapeModelSourceKind.PolygonXld)
                DisposeNativeXldForCreate();

            string genMode = opt.GenContourMode;
            int minPts = opt.MinContourPoints;
            HObject? domain = TryBuildOptionalDomainRegion();

            try
            {
                HalconXldContourBundle? bundle = opt.SourceKind switch
                {
                    HalconShapeModelSourceKind.PolygonXld => ExtractDirectedHandPathBundle(opt),
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
            DisposeNativeXldForCreate();
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
                                "未能提取到有效轮廓。\n阈值模式请调灰度；手绘/沿边线模式请调 Canny、边带或梯度向内/向外。",
                                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        AppendLog($"轮廓: {xldBundle.Contours.Count} 条");
                        SetWorkingContours(xldBundle);
                    }

                    _modelId = HalconFlowBridge.CreateShapeModel(
                        _currentImage, xldBundle, null, opt, _nativeXldForCreate);
                    if (opt.SourceKind == HalconShapeModelSourceKind.PolygonXld)
                        AppendLog("匹配指标建议: use_polarity（已含 edge_direction）");
                }

                region?.Dispose();

                _modelKind = opt.ModelKind is HalconShapeModelKind.Deformable or HalconShapeModelKind.PlanarDeformable
                    ? HalconFlowModelKind.Deformable
                    : HalconFlowModelKind.Shape;

                string levelsText = opt.NumLevels > 0 ? opt.NumLevels.ToString() : "auto";
                TxtModelInfo.Text =
                    $"ModelID: {_modelId}\n" +
                    $"来源: {opt.SourceKind}\n" +
                    $"类型: {opt.ModelKind}\n" +
                    $"图像: {_imgWidth}x{_imgHeight}\n" +
                    $"角度: [{opt.AngleStartDeg}°, 范围 {opt.AngleExtentDeg}°]\n" +
                    $"NumLevels: {levelsText}\n" +
                    (xldBundle != null ? $"轮廓数: {xldBundle.Contours.Count}\n" : "");

                AppendLog($"模型创建成功 ModelID={_modelId} ({_modelKind})");
                string modelLabel = _modelKind == HalconFlowModelKind.Deformable ? "可变形模板" : "形状模型";
                MessageBox.Show($"{modelLabel}创建成功!\nModelID: {_modelId}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
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

                AppendLog($"查找({_modelKind}): MinScore={minScore}, Greediness={greediness}, NumLevels={findNumLevels}, 角度[{angleStart}°~{angleStart + angleExtent}°]");

                if (_modelKind == HalconFlowModelKind.Deformable)
                {
                    var scaleOpt = ReadCreateOptionsFromUi();
                    (double[] rows, double[] cols, double[] scores, List<Point2D[]> deformed) findDef;
                    if (ChkFindAutoRetry.IsChecked == true)
                    {
                        findDef = HalconFlowBridge.FindDeformableModelWithFallback(
                            _currentImage, _modelId, angleStart, angleExtent, minScore, numMatches, maxOverlap,
                            findNumLevels, greediness, scaleOpt);
                    }
                    else
                    {
                        findDef = HalconFlowBridge.FindDeformableModel(
                            _currentImage, _modelId, angleStart, angleExtent, minScore, numMatches, maxOverlap,
                            findNumLevels, greediness, scaleOpt);
                    }

                    if (findDef.rows.Length == 0)
                    {
                        ClearFindResultOverlay();
                        TxtFindResult.Text = "未找到匹配";
                        AppendLog("未找到可变形匹配");
                    }
                    else
                    {
                        DrawDeformableFindResultOverlay(findDef.rows, findDef.cols, findDef.scores, findDef.deformed);
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine($"找到 {findDef.rows.Length} 个可变形匹配:");
                        for (int i = 0; i < findDef.rows.Length; i++)
                            sb.AppendLine($"[{i}] Row={findDef.rows[i]:F1}, Col={findDef.cols[i]:F1}, Score={findDef.scores[i]:F3}");
                        TxtFindResult.Text = sb.ToString();
                        AppendLog($"查找完成: {findDef.rows.Length} 个可变形匹配");
                    }

                    return;
                }

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
                Filter = "HALCON 模型|*.shm;*.dfm|形状模型 (*.shm)|*.shm|可变形模型 (*.dfm)|*.dfm|所有文件 (*.*)|*.*",
                Title = "导入 HALCON 模型"
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
                string ext = IoPath.GetExtension(dlg.FileName).ToLowerInvariant();
                if (ext == ".dfm")
                {
                    _modelId = HalconFlowBridge.LoadDeformableModelFromFile(dlg.FileName);
                    _modelKind = HalconFlowModelKind.Deformable;
                }
                else
                {
                    _modelId = HalconFlowBridge.LoadShapeModelFromFile(dlg.FileName);
                    _modelKind = HalconFlowModelKind.Shape;
                }

                string summary = _modelKind == HalconFlowModelKind.Deformable
                    ? HalconFlowBridge.GetDeformableModelParamsSummary(_modelId)
                    : HalconFlowBridge.GetShapeModelParamsSummary(_modelId);
                TxtModelInfo.Text = $"来源: 文件导入 ({_modelKind})\n文件: {dlg.FileName}\n{summary}";
                AppendLog($"模型已导入: {dlg.FileName} (ModelID={_modelId}, {_modelKind})");
                MessageBox.Show($"模型已导入，可进行匹配测试。\nModelID: {_modelId}", "导入成功",
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

            bool isDeformable = _modelKind == HalconFlowModelKind.Deformable;
            var dlg = new SaveFileDialog
            {
                Filter = isDeformable
                    ? "HALCON 可变形模型 (*.dfm)|*.dfm|所有文件 (*.*)|*.*"
                    : "HALCON 形状模型 (*.shm)|*.shm|所有文件 (*.*)|*.*",
                Title = isDeformable ? "保存可变形模型" : "保存形状模型",
                FileName = isDeformable ? "deformable_model.dfm" : "shape_model.shm"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                if (isDeformable)
                    HalconFlowBridge.WriteDeformableModelToFile(_modelId, dlg.FileName);
                else
                    HalconFlowBridge.WriteShapeModelToFile(_modelId, dlg.FileName);
                AppendLog($"模型已导出: {dlg.FileName}");
                MessageBox.Show($"HALCON 模型已保存:\n{dlg.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
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
