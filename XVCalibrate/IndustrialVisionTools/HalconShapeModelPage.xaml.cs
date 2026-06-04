using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
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
        /// <summary>相切圆弧：true=切线左侧鼓出，false=右侧，null=随鼠标位置。</summary>
        private bool? _tangentArcPreferLeft;
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
        private const double DefaultPolygonCloseDistance = 6;
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

#if HALCON_ENABLED
        private CancellationTokenSource? _findShapeCts;
        private int _findShapeRunId;
        private int _modelEpoch;
        private bool _createModelInProgress;
        private string? _contourFingerprint;
#endif

        /// <summary>预览/修剪后的轮廓，创建 XLD 模型时优先使用。</summary>
        private HalconXldContourBundle? _workingContours;

#if HALCON_ENABLED
        /// <summary>几何来源为 RoiPolyline 时，可编辑的闭合路径（顶点+直线/圆弧段）。</summary>
        private RoiContourPath? _geometryRoiPath;

        /// <summary>由 <see cref="_geometryRoiPath"/> 采样得到的轮廓缓存。</summary>
        private Point2D[]? _geometryRoiPolylineContour;

        private bool _suppressGeomPathSegmentUi;

        private string? _ninePointCalibPathCached;
        private AffineTransform? _ninePointAffineCached;
        private string? _ninePointAffineLoadError;
#endif

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

        private readonly DispatcherTimer _sessionPersistTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };

        public HalconShapeModelPage()
        {
            _suppressSessionPersist = true;
            _sessionPersistTimer.Tick += (_, _) =>
            {
                _sessionPersistTimer.Stop();
                PersistSessionFromUi();
            };
            InitializeComponent();
            ImageCanvas.RenderTransform = new TransformGroup
            {
                Children = new TransformCollection { _viewScale, _viewTranslate }
            };
            ImageCanvas.RenderTransformOrigin = new Point(0, 0);
            Loaded += HalconShapeModelPage_Loaded;
            Unloaded += HalconShapeModelPage_Unloaded;
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

        private void BtnCameraGrab_Click(object sender, RoutedEventArgs e)
        {
            int deviceIndex = 0;
            if (TxtCameraDeviceIndex != null
                && int.TryParse(TxtCameraDeviceIndex.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int di))
                deviceIndex = Math.Max(0, di);

            try
            {
                using var cam = new CameraService();
                if (!cam.ConnectByIndex(deviceIndex))
                {
                    var devs = CameraService.EnumDevices();
                    throw new InvalidOperationException(
                        devs.Count == 0
                            ? "未发现相机设备"
                            : $"相机连接失败（deviceIndex={deviceIndex}，共 {devs.Count} 台）");
                }

                CalibImage? frame = cam.GrabOneFrame(0, 0);
                if (frame == null)
                    throw new InvalidOperationException($"相机取图失败: {cam.LastError ?? "未知错误"}");

                try
                {
                    LoadImageFromCalibImage(frame, clearRoi: true);
                    AppendLog($"摄像头取一帧 OK: dev={deviceIndex} ({_imgWidth}x{_imgHeight})");
                }
                finally
                {
                    frame.Dispose();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"摄像头取图失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                AppendLog($"[相机] {ex.Message}");
            }
        }

        /// <summary>从 CalibImage（文件或相机）建立 _rawCalibImage 并刷新显示。</summary>
        private void LoadImageFromCalibImage(CalibImage frame, bool clearRoi = true)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            frame.RefreshProperties();

            CalibImage gray = frame.Channels == 1
                ? CalibAPI.DuplicateImage(frame)
                : HalconFlowBridge.ToSingleChannelGray(frame);

            _loadedImagePath = null;
            _imgWidth = gray.Width;
            _imgHeight = gray.Height;
            var gn = gray.GetNativeStruct();
            _grayPixels = new byte[_imgWidth * _imgHeight];
            System.Runtime.InteropServices.Marshal.Copy(gn.data, _grayPixels, 0, _grayPixels.Length);

            _currentImage?.Dispose();
            _currentImage = CalibAPI.DuplicateImage(gray);
            gray.Dispose();

            _rawCalibImage?.Dispose();
            _rawCalibImage = CalibAPI.DuplicateImage(_currentImage);

            try
            {
                TryApplyCameraCorrections(logSuccess: true);
            }
            catch (Exception ex)
            {
                AppendLog($"[相机矫正] {ex.Message}，显示原图");
                CommitCalibImageToUi(_rawCalibImage, disposeIncoming: false);
            }

            if (clearRoi)
                ClearRoiState();
            else
            {
                _workingContours = null;
                UpdateContourStats();
            }

            FitImageToView();
            FlushSessionPersist();
        }

        private void LoadImage(string path, bool clearRoi = true)
        {
            path = IoPath.GetFullPath(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
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

            try
            {
                TryApplyCameraCorrections(logSuccess: true);
            }
            catch (Exception ex)
            {
                AppendLog($"[相机矫正] {ex.Message}，显示原图");
                CommitCalibImageToUi(_rawCalibImage, disposeIncoming: false);
            }

            if (clearRoi)
                ClearRoiState();
            else
            {
                _workingContours = null;
                UpdateContourStats();
            }

            FitImageToView();
            FlushSessionPersist();
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
            PersistSessionChange();
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
                {
                    FitImageToView();
                    PersistSessionChange();
                }
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
                FillArcRubberPolylines(path, last, end, _polygonCursorImage ?? end, rubber, out _);
            else if (_polygonCursorImage is Point cursor && path.VertexCount > 0)
            {
                if (IsTangentJoinEnabled() && IsNextSegmentArc() && path.VertexCount >= 2)
                    FillArcRubberPolylines(path, last, cursor, cursor, rubber, out _);
                else
                {
                    HideRubberAltLine();
                    rubber.Add(last);
                    if (TryAddTangentLineSegment(path, cursor, out Point onRay))
                        rubber.Add(onRay);
                    else
                        rubber.Add(cursor);
                }
            }
            else
                HideRubberAltLine();

            if (rubber.Count < 2)
            {
                PolygonRubberLine.Visibility = Visibility.Collapsed;
                PolygonRubberLine.Points.Clear();
                HideRubberAltLine();
                UpdateTangentFlipButtonVisibility();
                return;
            }

            PolygonRubberLine.Visibility = Visibility.Visible;
            PolygonRubberLine.Points = new PointCollection(rubber);
            UpdateTangentFlipButtonVisibility();
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

        private bool IsTangentJoinEnabled() => ChkTangentJoin?.IsChecked == true;

        private bool TryResolveArcViaForDraft(RoiContourPath path, Point start, Point end, Point sideHint, out Point via)
        {
            via = default;
            if (!IsTangentJoinEnabled() || path.VertexCount < 2)
                return false;
            if (!RoiContourPathTangent.TryGetIncomingTravelTangent(path, out double tx, out double ty))
                return false;
            return RoiContourPathTangent.TryComputeArcViaTangentAtStart(
                start, tx, ty, end, sideHint, _tangentArcPreferLeft, out via);
        }

        private void UpdateTangentFlipButtonVisibility()
        {
            if (BtnTangentFlipSide == null)
                return;

            bool drawingPath = RbPolygonMode?.IsChecked == true || IsRingModeActive();
            bool show = IsTangentJoinEnabled() && IsNextSegmentArc() && drawingPath
                && (_arcDraftEnd != null
                    || (drawingPath && (IsRingModeActive()
                        ? GetActiveRingPath().VertexCount
                        : _roiPath.VertexCount) >= 2));
            BtnTangentFlipSide.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TangentJoin_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            UpdateTangentFlipButtonVisibility();
            UpdatePolygonRubberVisual();
            if (IsRingModeActive())
                UpdateRingRubberVisual();
        }

        private void BtnTangentFlipSide_Click(object sender, RoutedEventArgs e) => FlipTangentArcSide();

        private void FlipTangentArcSide()
        {
            RoiContourPath path = IsRingModeActive() ? GetActiveRingPath() : _roiPath;
            if (!IsTangentJoinEnabled() || path.VertexCount < 2)
                return;

            Point start = path.Vertices[^1];
            Point end = _arcDraftEnd ?? (_polygonCursorImage ?? start);
            if (Dist(start, end) < 1e-3)
            {
                AppendLog("请先点击圆弧终点");
                return;
            }

            if (!RoiContourPathTangent.TryGetIncomingTravelTangent(path, out double tx, out double ty)
                || !RoiContourPathTangent.TryComputeTangentArcBothSides(
                    start, tx, ty, end, out _, out _, out bool hasL, out bool hasR))
            {
                AppendLog("当前无法计算相切双弧");
                return;
            }

            if (!hasL || !hasR)
            {
                AppendLog("仅存在一种相切圆弧解");
                return;
            }

            if (!_tangentArcPreferLeft.HasValue)
                _tangentArcPreferLeft = RoiContourPathTangent.TangentSideSign(tx, ty, start, _polygonCursorImage ?? end) >= 0;
            _tangentArcPreferLeft = !_tangentArcPreferLeft.Value;
            AppendLog(_tangentArcPreferLeft.Value ? "相切弧：切线左侧 (鼓出)" : "相切弧：切线右侧 (鼓出)");
            UpdatePolygonRubberVisual();
            if (IsRingModeActive())
                UpdateRingRubberVisual();
        }

        private void FillArcRubberPolylines(
            RoiContourPath path,
            Point start,
            Point end,
            Point cursor,
            List<Point> primary,
            out bool drewAlt)
        {
            drewAlt = false;
            HideRubberAltLine();

            if (IsTangentJoinEnabled()
                && RoiContourPathTangent.TryGetIncomingTravelTangent(path, out double tx, out double ty)
                && RoiContourPathTangent.TryComputeTangentArcBothSides(
                    start, tx, ty, end, out Point viaL, out Point viaR, out bool hasL, out bool hasR)
                && RoiContourPathTangent.TryComputeArcViaTangentAtStart(
                    start, tx, ty, end, cursor, _tangentArcPreferLeft, out Point viaSel))
            {
                foreach (var p in RoiContourPath.SampleArc(start, viaSel, end, PreviewArcSegments))
                    primary.Add(p);

                if (hasL && hasR && PolygonRubberAltLine != null)
                {
                    bool selLeft = Dist(viaSel, viaL) <= Dist(viaSel, viaR);
                    Point viaAlt = selLeft ? viaR : viaL;
                    var alt = new List<Point>();
                    foreach (var p in RoiContourPath.SampleArc(start, viaAlt, end, PreviewArcSegments))
                        alt.Add(p);
                    if (alt.Count >= 2)
                    {
                        PolygonRubberAltLine.Visibility = Visibility.Visible;
                        PolygonRubberAltLine.Points = new PointCollection(alt);
                        drewAlt = true;
                    }
                }

                return;
            }

            Point via = cursor;
            if (TryResolveArcViaForDraft(path, start, end, cursor, out Point viaTan))
                via = viaTan;
            foreach (var p in RoiContourPath.SampleArc(start, via, end, PreviewArcSegments))
                primary.Add(p);
        }

        private void HideRubberAltLine()
        {
            if (PolygonRubberAltLine == null)
                return;
            PolygonRubberAltLine.Visibility = Visibility.Collapsed;
            PolygonRubberAltLine.Points.Clear();
        }

        private bool TryAddTangentLineSegment(RoiContourPath path, Point pick, out Point placed)
        {
            placed = pick;
            if (!IsTangentJoinEnabled() || path.VertexCount < 2
                || path.EdgeKinds.Count == 0
                || path.EdgeKinds[^1] != RoiEdgeKind.Arc)
                return false;

            Point origin = path.Vertices[^1];
            if (!RoiContourPathTangent.TryGetOutgoingTravelTangent(path, out double tx, out double ty))
                return false;

            if (!RoiContourPathTangent.TryProjectPointOntoTangentRay(origin, tx, ty, pick, minStepPx: 2.0, out placed))
                return false;

            return true;
        }

        private bool TryCommitArcDraftClick(
            RoiContourPath path,
            Point draftEnd,
            Point clickPt,
            string logPrefix,
            bool closingToStart)
        {
            Point arcStart = path.Vertices[^1];
            Point arcEnd = closingToStart ? path.Vertices[0] : draftEnd;

            if (TryResolveArcViaForDraft(path, arcStart, arcEnd, clickPt, out Point viaTan))
            {
                if (closingToStart)
                {
                    if (!RoiContourPath.IsValidArcVia(arcStart, viaTan, arcEnd))
                        return false;
                    try
                    {
                        path.CloseLoop(RoiEdgeKind.Arc, viaTan);
                    }
                    catch (ArgumentException ex)
                    {
                        MessageBox.Show(ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        return false;
                    }

                    return true;
                }

                path.AddArcSegment(arcEnd, viaTan);
                CancelArcDraft();
                AppendLog($"{logPrefix} 圆弧段已添加(与上段相切)，顶点数 {path.VertexCount}");
                return true;
            }

            if (closingToStart)
                return false;

            path.AddArcSegment(draftEnd, clickPt);
            CancelArcDraft();
            AppendLog($"{logPrefix} 圆弧段已添加，顶点数 {path.VertexCount}");
            return true;
        }

        private void AddLineSegmentWithOptionalTangent(RoiContourPath path, Point imgPt, string logPrefix)
        {
            if (TryAddTangentLineSegment(path, imgPt, out Point onRay))
            {
                path.AddLineSegment(onRay);
                AppendLog($"{logPrefix} 直线段(沿弧切线) → 顶点 {path.VertexCount}");
            }
            else
            {
                path.AddLineSegment(imgPt);
                AppendLog($"{logPrefix} 直线段 → 顶点 {path.VertexCount}");
            }
        }

        private void CancelArcDraft()
        {
            _arcDraftEnd = null;
            _tangentArcPreferLeft = null;
            HideRubberAltLine();
            UpdateTangentFlipButtonVisibility();
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
                FillArcRubberPolylines(_roiPath, last, end, _polygonCursorImage ?? end, rubber, out _);
            else if (_polygonCursorImage is Point cursor)
            {
                if (IsTangentJoinEnabled() && IsNextSegmentArc() && _roiPath.VertexCount >= 2)
                    FillArcRubberPolylines(_roiPath, last, cursor, cursor, rubber, out _);
                else
                {
                    HideRubberAltLine();
                    rubber.Add(last);
                    if (TryAddTangentLineSegment(_roiPath, cursor, out Point onRay))
                        rubber.Add(onRay);
                    else
                        rubber.Add(cursor);
                }
            }
            else
                HideRubberAltLine();

            if (rubber.Count < 2)
            {
                PolygonRubberLine.Visibility = Visibility.Collapsed;
                PolygonRubberLine.Points.Clear();
                HideRubberAltLine();
                UpdateTangentFlipButtonVisibility();
                return;
            }

            PolygonRubberLine.Visibility = Visibility.Visible;
            PolygonRubberLine.Points = new PointCollection(rubber);
            UpdateTangentFlipButtonVisibility();
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

        private bool TryClosePolygonWithArc(Point viaOrSideHint)
        {
            if (_roiPath.VertexCount < 3)
                return false;

            bool usedTangent = false;
            Point via = viaOrSideHint;
            if (TryResolveArcViaForDraft(
                    _roiPath, _roiPath.Vertices[^1], _roiPath.Vertices[0], viaOrSideHint, out Point viaTan))
            {
                via = viaTan;
                usedTangent = true;
            }

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

            return FinishPolygonRoiClosed(usedTangent ? "圆弧相切" : "圆弧");
        }

        private bool FinishPolygonRoiClosed(string closingKindLabel)
        {
            _hasRoi = true;
            _polygonCursorImage = null;
            CancelArcDraft();
            UpdatePolygonPreview();
            UpdateThresholdPreview();
            AppendLog($"多边形 ROI 已{closingKindLabel}闭合: {_roiPath.VertexCount} 个顶点, {_roiPath.EdgeKinds.Count} 段");
            PersistSessionChange();
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
                PersistSessionChange();
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

        private bool TryCloseActiveRingWithArc(Point viaOrSideHint)
        {
            var path = GetActiveRingPath();
            if (path.VertexCount < 3)
                return false;

            Point via = viaOrSideHint;
            if (TryResolveArcViaForDraft(path, path.Vertices[^1], path.Vertices[0], viaOrSideHint, out Point viaTan))
                via = viaTan;

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
                    if (TryCommitArcDraftClick(path, end, imgPt, label, closingToStart: true))
                    {
                        AdvanceRingPhaseAfterClose(path);
                        return true;
                    }

                    TryCloseActiveRingWithArc(imgPt);
                    return true;
                }

                TryCommitArcDraftClick(path, end, imgPt, label, closingToStart: false);
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
                _tangentArcPreferLeft = null;
                UpdateTangentFlipButtonVisibility();
                AppendLog(IsTangentJoinEnabled()
                    ? $"{label} 圆弧终点已设；移动鼠标选鼓出侧，或点「换向」/F 切换，再点确认"
                    : $"{label} 圆弧：已设终点，请点击弧上经过的一点");
                return false;
            }

            if (path.VertexCount == 0)
            {
                path.AddFirstVertex(imgPt);
                AppendLog($"{label}: 第 1 点");
            }
            else
                AddLineSegmentWithOptionalTangent(path, imgPt, label);

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
#if HALCON_ENABLED
                InvalidateContourCache();
#else
                _workingContours = null;
                UpdateContourStats();
#endif
            }
            catch { /* 忽略 XAML 控件未初始化错误 */ }

            PersistSessionChange();
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

        private void DrawFindResultOverlay(
            long modelId,
            HalconFlowModelKind modelKind,
            double[] rows,
            double[] cols,
            double[] angles,
            double[] scores,
            int[]? gridRow = null,
            int[]? gridCol = null)
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
                if (modelId >= 0)
                {
                    try
                    {
                        matchContours = modelKind == HalconFlowModelKind.Deformable
                            ? HalconFlowBridge.GetDeformableModelContourPoints(modelId, 1)
                            : HalconFlowBridge.GetShapeModelContourPoints(modelId, 1);
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
            _modelEpoch++;
#else
            _modelId = -1;
#endif
        }

#if HALCON_ENABLED
        private void InvalidateContourCache()
        {
            _contourFingerprint = null;
            _workingContours = null;
            DisposeNativeXldForCreate();
            UpdateContourStats();
        }

        private string ComputeContourFingerprint(HalconShapeModelCreateOptions opt, double minGray, double maxGray, bool includeTrimState)
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append(_imgWidth).Append('x').Append(_imgHeight).Append('|');
            sb.Append(opt.SourceKind).Append('|');
            sb.Append(minGray.ToString(CultureInfo.InvariantCulture))
                .Append('-')
                .Append(maxGray.ToString(CultureInfo.InvariantCulture))
                .Append('|');
            sb.Append(_hasRoi).Append('|');
            if (_hasRoi)
            {
                foreach (Point p in _roiPath.Vertices)
                {
                    sb.Append(p.X.ToString("F1", CultureInfo.InvariantCulture))
                        .Append(',')
                        .Append(p.Y.ToString("F1", CultureInfo.InvariantCulture))
                        .Append(';');
                }
            }

            sb.Append(opt.ModelKind).Append('|')
                .Append(opt.NumLevels).Append('|')
                .Append(opt.Metric).Append('|')
                .Append(opt.Optimization).Append('|')
                .Append(opt.GenContourMode).Append('|')
                .Append(opt.MinContourPoints).Append('|')
                .Append(opt.LargestContourOnly).Append('|')
                .Append(opt.EdgeAlpha.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(opt.EdgeLow.ToString(CultureInfo.InvariantCulture)).Append('-')
                .Append(opt.EdgeHigh.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(ReadRoiGradientPolarityForFingerprint());

            if (opt.SourceKind == HalconShapeModelSourceKind.GeometryXld)
                AppendGeometryFingerprint(sb);

            if (includeTrimState && _workingContours?.Contours != null && _workingContours.Contours.Count > 0)
            {
                var trim = ReadTrimOptionsFromUi();
                sb.Append("|trim:")
                    .Append(trim.Mode).Append('|')
                    .Append(trim.Epsilon.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(trim.TrimEndsPx.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(trim.MinContourLength.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(trim.ClosedContour);
            }

            return sb.ToString();
        }

        private void MarkContourCacheReady(HalconShapeModelCreateOptions opt, double minGray, double maxGray, bool includeTrimState)
        {
            _contourFingerprint = ComputeContourFingerprint(opt, minGray, maxGray, includeTrimState);
        }

        private void ResetFindResultUi()
        {
            ClearFindResultOverlay();
            TxtFindResult.Text = "无";
        }

        private void CancelFindShapeRun(string? logMessage = null)
        {
            if (_findShapeCts == null || _findShapeCts.IsCancellationRequested)
                return;
            _findShapeCts.Cancel();
            if (!string.IsNullOrWhiteSpace(logMessage))
                AppendLog(logMessage);
        }

        private void PrepareReplaceShapeModel()
        {
            CancelFindShapeRun("已取消进行中的匹配（即将替换模型）");
            if (_modelId >= 0)
                AppendLog($"释放旧模型 ModelID={_modelId}（epoch→{_modelEpoch + 1}）");
            DisposeCurrentModel();
            ResetFindResultUi();
        }

        private bool IsModelSessionCurrent(long modelId, int modelEpoch, HalconFlowModelKind modelKind)
        {
            if (modelId != _modelId || modelEpoch != _modelEpoch || modelKind != _modelKind)
                return false;
            return HalconFlowBridge.TryGetRegisteredModelKind(modelId, out _);
        }
#endif

        private void DrawMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady || _suppressDrawModeClear)
                return;
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
                    if (TryCommitArcDraftClick(_roiPath, end, imgPt, "多边形", closingToStart: true))
                        return FinishPolygonRoiClosed("圆弧相切");
                    if (TryClosePolygonWithArc(imgPt))
                        return true;
                    return false;
                }

                TryCommitArcDraftClick(_roiPath, end, imgPt, "多边形", closingToStart: false);
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
                _tangentArcPreferLeft = null;
                UpdateTangentFlipButtonVisibility();
                AppendLog(IsTangentJoinEnabled()
                    ? "圆弧终点已设；移动鼠标选鼓出侧，或点「换向」/F 切换，再点确认"
                    : "圆弧：已设终点，请点击弧上经过的一点");
                return false;
            }

            if (_roiPath.VertexCount == 0)
            {
                _roiPath.AddFirstVertex(imgPt);
                AppendLog("多边形: 第 1 点");
            }
            else
                AddLineSegmentWithOptionalTangent(_roiPath, imgPt, "多边形");

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
                    PersistSessionChange();
#if HALCON_ENABLED
                    TryAutoSyncGeometryFromRoiIfNeeded();
#endif
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
                    PersistSessionChange();
#if HALCON_ENABLED
                    TryAutoSyncGeometryFromRoiIfNeeded();
#endif
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
            else if (e.Key == Key.F && IsTangentJoinEnabled() && IsNextSegmentArc())
            {
                FlipTangentArcSide();
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
#if HALCON_ENABLED
            if ((CmbTemplateSource?.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "GeometryXld")
                TryAutoSyncGeometryFromRoiIfNeeded();
#endif
        }

        private void UpdateCreatePanelsVisibility()
        {
            if (CmbTemplateSource == null) return;

            string src = (CmbTemplateSource.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ThresholdXld";
            string kind = (CmbModelKind?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Shape";

            bool isGeometry = src == "GeometryXld";
            bool isXld = isGeometry || src is "ThresholdXld" or "EdgesXld" or "PolygonXld";
            bool isImage = src is "ImageRectangle" or "ImagePolygon";

            if (PnlGeometryMeasure != null)
                PnlGeometryMeasure.Visibility = isGeometry ? Visibility.Visible : Visibility.Collapsed;
            if (PnlContourExtract != null)
                PnlContourExtract.Visibility = isXld && !isGeometry ? Visibility.Visible : Visibility.Collapsed;
            if (PnlContourTrim != null)
                PnlContourTrim.Visibility = isXld ? Visibility.Visible : Visibility.Collapsed;
            if (PnlEdgeExtract != null)
                PnlEdgeExtract.Visibility = src == "EdgesXld" ? Visibility.Visible : Visibility.Collapsed;
            if (PnlRoiBoundaryDir != null)
                PnlRoiBoundaryDir.Visibility = src == "PolygonXld" ? Visibility.Visible : Visibility.Collapsed;
            if (PnlThreshold != null)
                PnlThreshold.Visibility = isGeometry ? Visibility.Collapsed : Visibility.Visible;
            if (BtnClosePolygon != null)
                BtnClosePolygon.Visibility = RbPolygonMode?.IsChecked == true || IsRingModeActive()
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (PnlScale != null)
                PnlScale.Visibility = kind is "ScaledShape" or "Deformable" or "PlanarDeformable" ? Visibility.Visible : Visibility.Collapsed;
            if (RowContrast != null)
                RowContrast.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;

            if (isGeometry)
            {
#if HALCON_ENABLED
                UpdateGeometryPanelRowsVisibility();
#endif
            }
        }

        private void GeometryParam_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
#if HALCON_ENABLED
            UpdateGeometryPanelRowsVisibility();
            RefreshMeasureSummaryFromUi();
#endif
        }

#if HALCON_ENABLED
        private HalconMeasuredGeometryKind ParseGeometryKind()
        {
            string? tag = (CmbGeometryKind?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return Enum.TryParse(tag, out HalconMeasuredGeometryKind k)
                ? k
                : HalconMeasuredGeometryKind.AxisAlignedRectangle;
        }

        private void UpdateGeometryPanelRowsVisibility()
        {
            if (CmbGeometryKind == null) return;
            var kind = ParseGeometryKind();
            bool isCircle = kind == HalconMeasuredGeometryKind.Circle;
            bool isArc = kind == HalconMeasuredGeometryKind.Arc;
            bool isArc3 = kind == HalconMeasuredGeometryKind.ArcThreePoint;
            bool isRoiPath = kind == HalconMeasuredGeometryKind.RoiPolyline;
            bool isRect = kind is HalconMeasuredGeometryKind.AxisAlignedRectangle
                or HalconMeasuredGeometryKind.RotatedRectangle;

            if (RowGeomRectSize != null)
                RowGeomRectSize.Visibility = isRect ? Visibility.Visible : Visibility.Collapsed;
            if (RowGeomRadius != null)
                RowGeomRadius.Visibility = isCircle || isArc ? Visibility.Visible : Visibility.Collapsed;
            if (RowGeomAngle != null)
                RowGeomAngle.Visibility = kind == HalconMeasuredGeometryKind.RotatedRectangle
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (RowGeomArcAngles != null)
                RowGeomArcAngles.Visibility = isArc ? Visibility.Visible : Visibility.Collapsed;
            if (RowGeomArc3Start != null)
                RowGeomArc3Start.Visibility = isArc3 ? Visibility.Visible : Visibility.Collapsed;
            if (RowGeomArc3Via != null)
                RowGeomArc3Via.Visibility = isArc3 ? Visibility.Visible : Visibility.Collapsed;
            if (RowGeomArc3End != null)
                RowGeomArc3End.Visibility = isArc3 ? Visibility.Visible : Visibility.Collapsed;
            if (TxtGeomRoiPathHint != null)
                TxtGeomRoiPathHint.Visibility = isRoiPath ? Visibility.Visible : Visibility.Collapsed;
            if (PnlGeomPathEditor != null)
                PnlGeomPathEditor.Visibility = isRoiPath ? Visibility.Visible : Visibility.Collapsed;

            if (RowGeomCenter != null)
                RowGeomCenter.Visibility = isCircle || isArc || isRect ? Visibility.Visible : Visibility.Collapsed;
        }

        private void InvalidateNinePointAffineCache() => _ninePointCalibPathCached = null;

        private bool TryGetNinePointAffine(out AffineTransform transform, out string error)
        {
            transform = default;
            error = _ninePointAffineLoadError ?? "";
            string path = TxtNinePointCalibPath?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "未指定九点标定 JSON";
                _ninePointAffineCached = null;
                return false;
            }

            if (string.Equals(path, _ninePointCalibPathCached, StringComparison.OrdinalIgnoreCase)
                && _ninePointAffineCached is AffineTransform cached)
            {
                transform = cached;
                error = "";
                return true;
            }

            if (!CalibrationResultFileLoader.TryLoadAffine(path, out transform, out error))
            {
                _ninePointCalibPathCached = path;
                _ninePointAffineCached = null;
                _ninePointAffineLoadError = error;
                return false;
            }

            _ninePointCalibPathCached = path;
            _ninePointAffineCached = transform;
            _ninePointAffineLoadError = null;
            return true;
        }

        private AffineTransform? TryGetNinePointAffineOptional()
        {
            return TryGetNinePointAffine(out AffineTransform t, out _) ? t : null;
        }

        private void BtnBrowseNinePointCalib_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "JSON|*.json|所有文件|*.*",
                Title = "选择九点标定结果 JSON",
                FileName = string.IsNullOrWhiteSpace(TxtNinePointCalibPath?.Text)
                    ? "calibration_result.json"
                    : IoPath.GetFileName(TxtNinePointCalibPath.Text)
            };
            if (!string.IsNullOrWhiteSpace(TxtNinePointCalibPath?.Text))
            {
                try
                {
                    string dir = IoPath.GetDirectoryName(TxtNinePointCalibPath.Text) ?? "";
                    if (Directory.Exists(dir))
                        dlg.InitialDirectory = dir;
                }
                catch { /* ignore */ }
            }

            if (dlg.ShowDialog() != true)
                return;
            TxtNinePointCalibPath.Text = dlg.FileName;
            InvalidateNinePointAffineCache();
            PersistSessionChange();
            RefreshMeasureSummaryFromUi();
        }

        private void NinePointCalibPath_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            InvalidateNinePointAffineCache();
            RefreshMeasureSummaryFromUi();
        }

        private void RefreshMeasureSummaryFromUi()
        {
#if HALCON_ENABLED
            if (TryReadGeometryParamsFromUi(out HalconMeasuredGeometryParams p))
                RefreshMeasureSummaryText(p);
            else if (TxtMeasureSummary != null)
                TxtMeasureSummary.Text = "测量: —";
#endif
        }

        private static void SelectGeometryKindCombo(ComboBox? cmb, HalconMeasuredGeometryKind kind)
        {
            if (cmb == null) return;
            foreach (var item in cmb.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), kind.ToString(), StringComparison.Ordinal))
                {
                    cmb.SelectedItem = item;
                    break;
                }
            }
        }

        private void WriteGeometryParamsToUi(in HalconMeasuredGeometryParams p)
        {
            SelectGeometryKindCombo(CmbGeometryKind, p.Kind);
            SetGeomText(TxtGeomCenterCol, p.CenterCol);
            SetGeomText(TxtGeomCenterRow, p.CenterRow);
            SetGeomText(TxtGeomWidth, p.WidthPx);
            SetGeomText(TxtGeomHeight, p.HeightPx);
            SetGeomText(TxtGeomAngleDeg, p.AngleDeg, "F3");
            SetGeomText(TxtGeomRadius, p.RadiusPx);
            SetGeomText(TxtGeomArcStartDeg, p.ArcStartAngleDeg);
            SetGeomText(TxtGeomArcExtentDeg, p.ArcExtentAngleDeg);
            SetGeomText(TxtGeomArcStartCol, p.ArcStartCol);
            SetGeomText(TxtGeomArcStartRow, p.ArcStartRow);
            SetGeomText(TxtGeomArcViaCol, p.ArcViaCol);
            SetGeomText(TxtGeomArcViaRow, p.ArcViaRow);
            SetGeomText(TxtGeomArcEndCol, p.ArcEndCol);
            SetGeomText(TxtGeomArcEndRow, p.ArcEndRow);
            UpdateGeometryPanelRowsVisibility();
            if (p.Kind == HalconMeasuredGeometryKind.RoiPolyline)
                RefreshGeomPathSegmentList();
            RefreshMeasureSummaryText(p);
        }

        private static void SetGeomText(TextBox? box, double value, string format = "F2")
        {
            if (box == null) return;
            box.Text = value.ToString(format, CultureInfo.InvariantCulture);
        }

        private void RefreshMeasureSummaryText(in HalconMeasuredGeometryParams p)
        {
            if (TxtMeasureSummary == null) return;
            var aff = TryGetNinePointAffineOptional();
            string summary = p.Kind == HalconMeasuredGeometryKind.RoiPolyline && _geometryRoiPath != null
                ? HalconGeometryPathEditor.FormatPathSummary(_geometryRoiPath, aff)
                : HalconGeometryContourBuilder.FormatMeasurementSummary(p, aff);
            TxtMeasureSummary.Text = "测量: " + summary;
            RefreshGeomMmHints(p);
        }

        private void RefreshGeomMmHints(in HalconMeasuredGeometryParams p)
        {
            if (TxtGeomMmHints == null) return;
            if (!TryGetNinePointAffine(out AffineTransform t, out string err))
            {
                TxtGeomMmHints.Text = string.IsNullOrWhiteSpace(err) ? "" : $"mm 标定: {err}";
                ClearGeomFieldMmTooltips();
                return;
            }

            switch (p.Kind)
            {
                case HalconMeasuredGeometryKind.Circle:
                case HalconMeasuredGeometryKind.Arc:
                    double rMm = HalconGeometryContourBuilder.PixelRadiusToWorldMm(
                        p.RadiusPx, p.CenterCol, p.CenterRow, t);
                    SetGeomFieldMmTooltip(TxtGeomCenterCol,
                        $"列 X px；世界 {HalconGeometryContourBuilder.FormatWorldPointMm(p.CenterCol, p.CenterRow, t)}");
                    SetGeomFieldMmTooltip(TxtGeomCenterRow, "行 Y px（与列共同确定圆心世界坐标）");
                    SetGeomFieldMmTooltip(TxtGeomRadius, $"半径 {rMm:F2} mm");
                    TxtGeomMmHints.Text = p.Kind == HalconMeasuredGeometryKind.Arc
                        ? $"mm: 圆心 {HalconGeometryContourBuilder.FormatWorldPointMm(p.CenterCol, p.CenterRow, t)}，R={rMm:F2} mm，弧长≈{Math.Abs(p.ArcExtentAngleDeg) * Math.PI / 180.0 * rMm:F2} mm"
                        : $"mm: 圆心 {HalconGeometryContourBuilder.FormatWorldPointMm(p.CenterCol, p.CenterRow, t)}，R={rMm:F2} mm，Ø={2 * rMm:F2} mm";
                    break;
                case HalconMeasuredGeometryKind.ArcThreePoint:
                    SetGeomFieldMmTooltip(TxtGeomArcStartCol,
                        $"起点 {HalconGeometryContourBuilder.FormatWorldPointMm(p.ArcStartCol, p.ArcStartRow, t)}");
                    SetGeomFieldMmTooltip(TxtGeomArcViaCol,
                        $"弧上 {HalconGeometryContourBuilder.FormatWorldPointMm(p.ArcViaCol, p.ArcViaRow, t)}");
                    SetGeomFieldMmTooltip(TxtGeomArcEndCol,
                        $"终点 {HalconGeometryContourBuilder.FormatWorldPointMm(p.ArcEndCol, p.ArcEndRow, t)}");
                    TxtGeomMmHints.Text =
                        $"mm: 起 {HalconGeometryContourBuilder.FormatWorldPointMm(p.ArcStartCol, p.ArcStartRow, t)}，"
                        + $"弧上 {HalconGeometryContourBuilder.FormatWorldPointMm(p.ArcViaCol, p.ArcViaRow, t)}，"
                        + $"终 {HalconGeometryContourBuilder.FormatWorldPointMm(p.ArcEndCol, p.ArcEndRow, t)}";
                    break;
                case HalconMeasuredGeometryKind.AxisAlignedRectangle:
                case HalconMeasuredGeometryKind.RotatedRectangle:
                {
                    var (wMm, hMm) = HalconGeometryContourBuilder.PixelSizeToWorldMm(
                        p.WidthPx, p.HeightPx, p.CenterCol, p.CenterRow, t);
                    SetGeomFieldMmTooltip(TxtGeomCenterCol,
                        $"中心 {HalconGeometryContourBuilder.FormatWorldPointMm(p.CenterCol, p.CenterRow, t)}");
                    SetGeomFieldMmTooltip(TxtGeomWidth, $"宽 ≈{wMm:F2} mm");
                    SetGeomFieldMmTooltip(TxtGeomHeight, $"高 ≈{hMm:F2} mm");
                    TxtGeomMmHints.Text =
                        $"mm: 中心 {HalconGeometryContourBuilder.FormatWorldPointMm(p.CenterCol, p.CenterRow, t)}，"
                        + $"≈{wMm:F2}×{hMm:F2} mm";
                    break;
                }
                case HalconMeasuredGeometryKind.RoiPolyline:
                    TxtGeomMmHints.Text = _geometryRoiPath != null
                        ? "选中路径段后可改起点/终点/弧上点；应用本段后更新 mm 摘要"
                        : "请先「从 ROI 同步」闭合路径";
                    ClearGeomFieldMmTooltips();
                    break;
                default:
                    TxtGeomMmHints.Text = "";
                    ClearGeomFieldMmTooltips();
                    break;
            }
        }

        private static void SetGeomFieldMmTooltip(TextBox? box, string tooltip)
        {
            if (box != null)
                box.ToolTip = tooltip;
        }

        private void ClearGeomFieldMmTooltips()
        {
            foreach (string name in new[]
                     {
                         "TxtGeomCenterCol", "TxtGeomCenterRow", "TxtGeomWidth", "TxtGeomHeight",
                         "TxtGeomRadius", "TxtGeomArcStartCol", "TxtGeomArcViaCol", "TxtGeomArcEndCol"
                     })
            {
                if (FindName(name) is TextBox tb)
                    tb.ToolTip = null;
            }
        }

        private void AssignGeometryRoiPath(RoiContourPath source, string syncMessage, out string message)
        {
            _geometryRoiPath = HalconGeometryPathEditor.ClonePath(source);
            RebuildGeometryRoiPolylineFromPath();
            WriteGeometryParamsToUi(new HalconMeasuredGeometryParams { Kind = HalconMeasuredGeometryKind.RoiPolyline });
            message = syncMessage;
        }

        private void RebuildGeometryRoiPolylineFromPath()
        {
            _geometryRoiPolylineContour = _geometryRoiPath != null
                ? HalconGeometryContourBuilder.BuildFromRoiContourPath(_geometryRoiPath)
                : null;
        }

        private void RefreshGeomPathSegmentList(int selectEdgeIndex = -1)
        {
            if (LstGeomPathSegments == null || _geometryRoiPath == null)
                return;

            _suppressGeomPathSegmentUi = true;
            try
            {
                int edges = HalconGeometryPathEditor.GetClosedEdgeCount(_geometryRoiPath);
                var items = new List<string>(edges);
                for (int i = 0; i < edges; i++)
                    items.Add(HalconGeometryPathEditor.FormatSegmentLabel(_geometryRoiPath, i));

                LstGeomPathSegments.ItemsSource = items;
                if (edges == 0)
                {
                    ClearGeomPathSegmentFields();
                    return;
                }

                int pick = selectEdgeIndex >= 0 && selectEdgeIndex < edges ? selectEdgeIndex : 0;
                LstGeomPathSegments.SelectedIndex = pick;
                LoadGeomPathSegmentToUi(pick);
            }
            finally
            {
                _suppressGeomPathSegmentUi = false;
            }
        }

        private void LoadGeomPathSegmentToUi(int edgeIndex)
        {
            if (_geometryRoiPath == null)
            {
                ClearGeomPathSegmentFields();
                return;
            }

            HalconGeometryPathEditor.GetSegmentEndpoints(_geometryRoiPath, edgeIndex, out Point start, out Point end);
            var kind = HalconGeometryPathEditor.GetSegmentKind(_geometryRoiPath, edgeIndex);
            SelectPathSegKindCombo(kind);
            UpdateGeomPathViaRowVisibility();

            SetGeomText(TxtGeomPathStartCol, start.X);
            SetGeomText(TxtGeomPathStartRow, start.Y);
            SetGeomText(TxtGeomPathEndCol, end.X);
            SetGeomText(TxtGeomPathEndRow, end.Y);
            if (HalconGeometryPathEditor.GetSegmentArcVia(_geometryRoiPath, edgeIndex) is Point via)
            {
                SetGeomText(TxtGeomPathViaCol, via.X);
                SetGeomText(TxtGeomPathViaRow, via.Y);
            }
            else
            {
                double mx = (start.X + end.X) * 0.5;
                double my = (start.Y + end.Y) * 0.5;
                SetGeomText(TxtGeomPathViaCol, mx);
                SetGeomText(TxtGeomPathViaRow, my);
            }

            ApplyGeomPathSegmentMmTooltips(start, end, kind, edgeIndex);
        }

        private void ApplyGeomPathSegmentMmTooltips(Point start, Point end, RoiEdgeKind kind, int edgeIndex)
        {
            if (!TryGetNinePointAffine(out AffineTransform t, out _))
                return;

            SetGeomFieldMmTooltip(TxtGeomPathStartCol,
                $"起点 {HalconGeometryContourBuilder.FormatWorldPointMm(start.X, start.Y, t)}");
            SetGeomFieldMmTooltip(TxtGeomPathEndCol,
                $"终点 {HalconGeometryContourBuilder.FormatWorldPointMm(end.X, end.Y, t)}");
            if (kind == RoiEdgeKind.Arc
                && _geometryRoiPath != null
                && HalconGeometryPathEditor.GetSegmentArcVia(_geometryRoiPath, edgeIndex) is Point via)
            {
                SetGeomFieldMmTooltip(TxtGeomPathViaCol,
                    $"弧上 {HalconGeometryContourBuilder.FormatWorldPointMm(via.X, via.Y, t)}");
            }
        }

        private void SelectPathSegKindCombo(RoiEdgeKind kind)
        {
            if (CmbGeomPathSegKind == null) return;
            string want = kind == RoiEdgeKind.Arc ? "Arc" : "Line";
            foreach (var item in CmbGeomPathSegKind.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), want, StringComparison.Ordinal))
                {
                    CmbGeomPathSegKind.SelectedItem = item;
                    break;
                }
            }
        }

        private RoiEdgeKind ParsePathSegKind()
        {
            string? tag = (CmbGeomPathSegKind?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return string.Equals(tag, "Arc", StringComparison.OrdinalIgnoreCase)
                ? RoiEdgeKind.Arc
                : RoiEdgeKind.Line;
        }

        private void UpdateGeomPathViaRowVisibility()
        {
            if (RowGeomPathVia != null)
                RowGeomPathVia.Visibility = ParsePathSegKind() == RoiEdgeKind.Arc
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private void ClearGeomPathSegmentFields()
        {
            foreach (string name in new[]
                     {
                         "TxtGeomPathStartCol", "TxtGeomPathStartRow", "TxtGeomPathEndCol", "TxtGeomPathEndRow",
                         "TxtGeomPathViaCol", "TxtGeomPathViaRow"
                     })
            {
                if (FindName(name) is TextBox tb)
                    tb.Text = "0";
            }
        }

        private void GeomPathSegment_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressGeomPathSegmentUi || !IsLoaded) return;
            if (LstGeomPathSegments?.SelectedIndex is int idx && idx >= 0)
                LoadGeomPathSegmentToUi(idx);
        }

        private void GeomPathSegmentParam_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressGeomPathSegmentUi || !IsLoaded) return;
            UpdateGeomPathViaRowVisibility();
        }

        private void BtnApplyGeomPathSegment_Click(object sender, RoutedEventArgs e)
        {
#if !HALCON_ENABLED
            MessageBox.Show("当前构建未启用 HALCON。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
#else
            if (_geometryRoiPath == null || LstGeomPathSegments?.SelectedIndex is not int edgeIndex || edgeIndex < 0)
            {
                MessageBox.Show("请先同步闭合路径并选择要修改的段。", "路径段", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!TryParseGeomPathSegmentFields(
                    out double sc, out double sr, out double ec, out double er, out double? vc, out double? vr))
            {
                MessageBox.Show("段参数无效，请检查数值。", "路径段", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var kind = ParsePathSegKind();
            if (!HalconGeometryPathEditor.TryUpdateSegment(
                    _geometryRoiPath, edgeIndex, kind, sc, sr, ec, er, vc, vr, out string err))
            {
                MessageBox.Show(err, "路径段", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RebuildGeometryRoiPolylineFromPath();
            RefreshGeomPathSegmentList(edgeIndex);
            if (TryReadGeometryParamsFromUi(out HalconMeasuredGeometryParams p))
                RefreshMeasureSummaryText(p);
            ApplyGeometryContourFromUi(logSuccess: false);
            AppendLog($"路径段 #{edgeIndex + 1} 已更新 ({kind})");
#endif
        }

        private bool TryParseGeomPathSegmentFields(
            out double startCol,
            out double startRow,
            out double endCol,
            out double endRow,
            out double? viaCol,
            out double? viaRow)
        {
            startCol = startRow = endCol = endRow = 0;
            viaCol = viaRow = null;
            if (!TryParse(TxtGeomPathStartCol, out startCol)
                || !TryParse(TxtGeomPathStartRow, out startRow)
                || !TryParse(TxtGeomPathEndCol, out endCol)
                || !TryParse(TxtGeomPathEndRow, out endRow))
                return false;

            if (ParsePathSegKind() == RoiEdgeKind.Arc)
            {
                if (!TryParse(TxtGeomPathViaCol, out double vc) || !TryParse(TxtGeomPathViaRow, out double vr))
                    return false;
                viaCol = vc;
                viaRow = vr;
            }

            return true;

            static bool TryParse(TextBox? box, out double v) =>
                double.TryParse(box?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private bool TryReadGeometryParamsFromUi(out HalconMeasuredGeometryParams p)
        {
            p = new HalconMeasuredGeometryParams { Kind = ParseGeometryKind() };
            if (p.Kind == HalconMeasuredGeometryKind.RoiPolyline)
                return _geometryRoiPath != null && _geometryRoiPath.IsClosed && _geometryRoiPath.VertexCount >= 3;

            if (p.Kind == HalconMeasuredGeometryKind.ArcThreePoint)
            {
                if (!TryParse(TxtGeomArcStartCol, out p.ArcStartCol)
                    || !TryParse(TxtGeomArcStartRow, out p.ArcStartRow)
                    || !TryParse(TxtGeomArcViaCol, out p.ArcViaCol)
                    || !TryParse(TxtGeomArcViaRow, out p.ArcViaRow)
                    || !TryParse(TxtGeomArcEndCol, out p.ArcEndCol)
                    || !TryParse(TxtGeomArcEndRow, out p.ArcEndRow))
                    return false;
                return RoiContourPath.IsValidArcVia(
                    new Point(p.ArcStartCol, p.ArcStartRow),
                    new Point(p.ArcViaCol, p.ArcViaRow),
                    new Point(p.ArcEndCol, p.ArcEndRow));
            }

            if (!TryParse(TxtGeomCenterCol, out p.CenterCol) || !TryParse(TxtGeomCenterRow, out p.CenterRow))
                return false;

            if (p.Kind == HalconMeasuredGeometryKind.Circle || p.Kind == HalconMeasuredGeometryKind.Arc)
            {
                if (!TryParse(TxtGeomRadius, out p.RadiusPx) || p.RadiusPx < 0.5)
                    return false;
            }

            if (p.Kind == HalconMeasuredGeometryKind.Arc)
            {
                if (!TryParse(TxtGeomArcStartDeg, out p.ArcStartAngleDeg)
                    || !TryParse(TxtGeomArcExtentDeg, out p.ArcExtentAngleDeg))
                    return false;
                return Math.Abs(p.ArcExtentAngleDeg) > 0.01;
            }

            if (p.Kind == HalconMeasuredGeometryKind.Circle)
                return true;

            if (!TryParse(TxtGeomWidth, out p.WidthPx) || p.WidthPx < 1
                || !TryParse(TxtGeomHeight, out p.HeightPx) || p.HeightPx < 1)
                return false;

            if (p.Kind == HalconMeasuredGeometryKind.AxisAlignedRectangle)
            {
                p.AngleDeg = 0;
                return true;
            }

            return TryParse(TxtGeomAngleDeg, out p.AngleDeg);

            static bool TryParse(TextBox? box, out double v) =>
                double.TryParse(box?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private static bool TryGetPolygonBounds(IReadOnlyList<Point> points, out Rect bounds)
        {
            bounds = Rect.Empty;
            if (points == null || points.Count == 0)
                return false;
            double minX = points[0].X, maxX = points[0].X;
            double minY = points[0].Y, maxY = points[0].Y;
            for (int i = 1; i < points.Count; i++)
            {
                minX = Math.Min(minX, points[i].X);
                maxX = Math.Max(maxX, points[i].X);
                minY = Math.Min(minY, points[i].Y);
                maxY = Math.Max(maxY, points[i].Y);
            }

            bounds = new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
            return true;
        }

        private bool TrySyncGeometryFromRoi(out string message)
        {
            message = "";
            if (_imgWidth <= 0 || _imgHeight <= 0)
            {
                message = "请先加载图像";
                return false;
            }

            if (IsCircleModeActive() && _hasRoi && _circleRadiusImage > 5)
            {
                WriteGeometryParamsToUi(new HalconMeasuredGeometryParams
                {
                    Kind = HalconMeasuredGeometryKind.Circle,
                    CenterCol = _circleCenterImage.X,
                    CenterRow = _circleCenterImage.Y,
                    RadiusPx = _circleRadiusImage
                });
                _geometryRoiPath = null;
                _geometryRoiPolylineContour = null;
                message = $"已从圆形 ROI 同步: R={_circleRadiusImage:F1}";
                return true;
            }

            if (RbRectMode?.IsChecked == true && _hasRoi && _roiRectImage.Width > 5 && _roiRectImage.Height > 5)
            {
                WriteGeometryParamsToUi(new HalconMeasuredGeometryParams
                {
                    Kind = HalconMeasuredGeometryKind.AxisAlignedRectangle,
                    CenterCol = _roiRectImage.X + _roiRectImage.Width * 0.5,
                    CenterRow = _roiRectImage.Y + _roiRectImage.Height * 0.5,
                    WidthPx = _roiRectImage.Width,
                    HeightPx = _roiRectImage.Height
                });
                _geometryRoiPath = null;
                _geometryRoiPolylineContour = null;
                message = $"已从矩形 ROI 同步: {_roiRectImage.Width:F0}×{_roiRectImage.Height:F0}";
                return true;
            }

            if (HasUsableRingRoi())
            {
                var ringPath = new RoiContourPath();
                ringPath.IsClosed = _ringOuterPath.IsClosed;
                foreach (var v in _ringOuterPath.Vertices)
                    ringPath.Vertices.Add(v);
                foreach (var k in _ringOuterPath.EdgeKinds)
                    ringPath.EdgeKinds.Add(k);
                foreach (var a in _ringOuterPath.ArcVia)
                    ringPath.ArcVia.Add(a);

                if (ringPath.VertexCount >= 3 && ringPath.IsClosed)
                {
                    int arcCount = ringPath.EdgeKinds.Count(k => k == RoiEdgeKind.Arc);
                    AssignGeometryRoiPath(
                        ringPath,
                        $"已从环形外圈同步: {ringPath.VertexCount} 顶点，{arcCount} 段圆弧",
                        out message);
                    return true;
                }
            }

            if (RbPolygonMode?.IsChecked == true && _roiPath.VertexCount >= 3 && _roiPath.IsClosed)
            {
                int arcCount = _roiPath.EdgeKinds.Count(k => k == RoiEdgeKind.Arc);
                AssignGeometryRoiPath(
                    _roiPath,
                    $"已从闭合多边形同步: {_roiPath.VertexCount} 顶点，{arcCount} 段圆弧",
                    out message);
                return true;
            }

            if (RbPolygonMode?.IsChecked == true && _roiPath.VertexCount >= 3 && !_roiPath.IsClosed)
            {
                message = "多边形未闭合，请单击起点闭合";
                return false;
            }

            if (RbPolygonMode?.IsChecked == true && _roiPath.VertexCount == 3
                && _roiPath.EdgeKinds.Count >= 1
                && _roiPath.EdgeKinds[0] == RoiEdgeKind.Arc
                && _roiPath.ArcVia[0] is Point via)
            {
                var p0 = _roiPath.Vertices[0];
                var p2 = _roiPath.Vertices[2];
                if (RoiContourPath.IsValidArcVia(
                        new Point(p0.X, p0.Y), via, new Point(p2.X, p2.Y)))
                {
                    WriteGeometryParamsToUi(new HalconMeasuredGeometryParams
                    {
                        Kind = HalconMeasuredGeometryKind.ArcThreePoint,
                        ArcStartCol = p0.X,
                        ArcStartRow = p0.Y,
                        ArcViaCol = via.X,
                        ArcViaRow = via.Y,
                        ArcEndCol = p2.X,
                        ArcEndRow = p2.Y
                    });
                    _geometryRoiPath = null;
                    _geometryRoiPolylineContour = null;
                    message = $"已从单段圆弧 ROI 同步为三点弧";
                    return true;
                }
            }

            message = "请先绘制矩形、圆、闭合多边形(含弧)或环形 ROI";
            return false;
        }

        private void TryAutoSyncGeometryFromRoiIfNeeded()
        {
            if (ParseSourceKind() != HalconShapeModelSourceKind.GeometryXld)
                return;
            if (TrySyncGeometryFromRoi(out string msg))
            {
                AppendLog(msg);
                ApplyGeometryContourFromUi(logSuccess: false);
            }
        }

        private HalconXldContourBundle? BuildGeometryBundleFromUi()
        {
            if (_imgWidth <= 0 || _imgHeight <= 0)
                return null;
            if (!TryReadGeometryParamsFromUi(out HalconMeasuredGeometryParams p))
                return null;

            return HalconGeometryContourBuilder.BuildBundle(
                _imgWidth, _imgHeight, p, _geometryRoiPolylineContour, _geometryRoiPath);
        }

        private bool ApplyGeometryContourFromUi(bool logSuccess = true)
        {
            if (!TryReadGeometryParamsFromUi(out HalconMeasuredGeometryParams p))
            {
                MessageBox.Show(
                    "几何参数无效。圆弧需填写圆心/半径/起始角/张角；三点弧需有效弧上点；手绘路径需先「从 ROI 同步」。",
                    "测量几何",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            var bundle = HalconGeometryContourBuilder.BuildBundle(
                _imgWidth, _imgHeight, p, _geometryRoiPolylineContour, _geometryRoiPath);
            if (bundle.Contours == null || bundle.Contours.Count == 0 || bundle.Contours[0].Length < 2)
            {
                MessageBox.Show("未能生成轮廓，请检查参数。", "测量几何", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            SetWorkingContours(bundle);
            RefreshMeasureSummaryText(p);
            if (logSuccess)
                AppendLog($"几何轮廓已应用: {TxtMeasureSummary?.Text ?? ""}");
            return true;
        }

        private void BtnSyncGeometryFromRoi_Click(object sender, RoutedEventArgs e)
        {
#if !HALCON_ENABLED
            MessageBox.Show("当前构建未启用 HALCON。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
#else
            if (!TrySyncGeometryFromRoi(out string msg))
            {
                MessageBox.Show(msg, "从 ROI 同步", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AppendLog(msg);
            ApplyGeometryContourFromUi();
#endif
        }

        private void BtnApplyGeometryParams_Click(object sender, RoutedEventArgs e)
        {
#if !HALCON_ENABLED
            MessageBox.Show("当前构建未启用 HALCON。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
#else
            if (_imgWidth <= 0)
            {
                MessageBox.Show("请先加载图像", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ApplyGeometryContourFromUi();
#endif
        }

        private void AppendGeometryFingerprint(System.Text.StringBuilder sb)
        {
            if (!TryReadGeometryParamsFromUi(out HalconMeasuredGeometryParams p))
                return;
            sb.Append("|geom:")
                .Append(p.Kind).Append('|')
                .Append(p.CenterCol.ToString("F2", CultureInfo.InvariantCulture)).Append('|')
                .Append(p.CenterRow.ToString("F2", CultureInfo.InvariantCulture)).Append('|')
                .Append(p.WidthPx.ToString("F2", CultureInfo.InvariantCulture)).Append('|')
                .Append(p.HeightPx.ToString("F2", CultureInfo.InvariantCulture)).Append('|')
                .Append(p.AngleDeg.ToString("F3", CultureInfo.InvariantCulture)).Append('|')
                .Append(p.RadiusPx.ToString("F2", CultureInfo.InvariantCulture)).Append('|')
                .Append(p.ArcStartAngleDeg.ToString("F2", CultureInfo.InvariantCulture)).Append('|')
                .Append(p.ArcExtentAngleDeg.ToString("F2", CultureInfo.InvariantCulture));
            if (p.Kind == HalconMeasuredGeometryKind.RoiPolyline && _geometryRoiPath != null)
                sb.Append("|roiV:").Append(_geometryRoiPath.VertexCount)
                    .Append("|roiE:").Append(HalconGeometryPathEditor.GetClosedEdgeCount(_geometryRoiPath));
        }

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

        private string ReadRoiGradientPolarityForFingerprint()
        {
            var outer = ReadGradientPolarity();
            if (HasUsableRingRoi())
                return outer + "|" + ReadRingInnerGradientPolarity(outer);
            return outer.ToString();
        }

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
                    HalconShapeModelSourceKind.GeometryXld => BuildGeometryBundleFromUi(),
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

                if (opt.SourceKind != HalconShapeModelSourceKind.GeometryXld)
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
                var opt = ReadCreateOptionsFromUi();
                if (!double.TryParse(TxtMinGray.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minGray)) minGray = 0;
                if (!double.TryParse(TxtMaxGray.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double maxGray)) maxGray = 255;
                MarkContourCacheReady(opt, minGray, maxGray, includeTrimState: true);
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
            InvalidateContourCache();
            ClearFindResultOverlay();
            AppendLog("已清除轮廓缓存，请重新预览轮廓");
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
                if (opt.SourceKind == HalconShapeModelSourceKind.GeometryXld)
                {
                    if (!ApplyGeometryContourFromUi(logSuccess: false))
                    {
                        MessageBox.Show("请填写有效几何参数或先从 ROI 同步。", "预览", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    if (_workingContours != null)
                    {
                        MarkContourCacheReady(opt, 0, 255, includeTrimState: false);
                        AppendLog($"几何轮廓预览: {_workingContours.Contours?.Count ?? 0} 条");
                    }

                    return;
                }

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
                        MarkContourCacheReady(opt, 0, 255, includeTrimState: false);
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
                MarkContourCacheReady(opt, minGray, maxGray, includeTrimState: false);
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
        private async void BtnCreateModel_Click(object sender, RoutedEventArgs e)
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

            if (_createModelInProgress)
            {
                AppendLog("模型创建进行中，请稍候…");
                return;
            }

            PrepareReplaceShapeModel();

            _createModelInProgress = true;
            BtnCreateModel.IsEnabled = false;
            BtnTestFind.IsEnabled = false;
            try
            {
                var opt = ReadCreateOptionsFromUi();
                if (!double.TryParse(TxtMinGray.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minGray)) minGray = 0;
                if (!double.TryParse(TxtMaxGray.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double maxGray)) maxGray = 255;

                AppendLog($"创建模式: {opt.SourceKind} / {opt.ModelKind}（epoch={_modelEpoch}）");

                bool fromImage = opt.SourceKind is HalconShapeModelSourceKind.ImageRectangle
                    or HalconShapeModelSourceKind.ImagePolygon;

                HalconXldContourBundle? xldBundle = null;
                HObject? region = null;

                if (fromImage)
                {
                    region = BuildRequiredRegionForImageMode(opt.SourceKind);
                    AppendLog("使用 ROI 灰度图创建模板...");
                    var image = _currentImage;
                    _modelId = await HalconComputeRunner.RunAsync(() =>
                        HalconFlowBridge.CreateShapeModel(image, null, region, opt),
                        HalconThreadPolicy.Geometry);
                }
                else
                {
                    string fingerprint = ComputeContourFingerprint(opt, minGray, maxGray, includeTrimState: true);
                    if (_workingContours?.Contours != null && _workingContours.Contours.Count > 0
                        && string.Equals(_contourFingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        xldBundle = _workingContours;
                        AppendLog($"使用与预览一致的轮廓: {xldBundle.Contours.Count} 条");
                    }
                    else
                    {
                        AppendLog("参数/ROI 已变或未预览，重新提取轮廓…");
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
                        MarkContourCacheReady(opt, minGray, maxGray, includeTrimState: false);
                    }

                    var image = _currentImage;
                    var nativeXld = _nativeXldForCreate;
                    _modelId = await HalconComputeRunner.RunAsync(() =>
                        HalconFlowBridge.CreateShapeModel(image, xldBundle, null, opt, nativeXld),
                        HalconThreadPolicy.Geometry);
                    if (opt.SourceKind == HalconShapeModelSourceKind.PolygonXld)
                        AppendLog("匹配指标建议: use_polarity（已含 edge_direction）");
                }

                region?.Dispose();

                if (!HalconFlowBridge.TryGetRegisteredModelKind(_modelId, out _))
                    throw new InvalidOperationException($"模型注册失败 ModelID={_modelId}");

                _modelKind = opt.ModelKind is HalconShapeModelKind.Deformable or HalconShapeModelKind.PlanarDeformable
                    ? HalconFlowModelKind.Deformable
                    : HalconFlowModelKind.Shape;

                string levelsText = opt.NumLevels > 0 ? opt.NumLevels.ToString() : "auto";
                TxtModelInfo.Text =
                    $"ModelID: {_modelId}\n" +
                    $"Epoch: {_modelEpoch}\n" +
                    $"来源: {opt.SourceKind}\n" +
                    $"类型: {opt.ModelKind}\n" +
                    $"图像: {_imgWidth}x{_imgHeight}\n" +
                    $"角度: [{opt.AngleStartDeg}°, 范围 {opt.AngleExtentDeg}°]\n" +
                    $"NumLevels: {levelsText}\n" +
                    (xldBundle != null ? $"轮廓数: {xldBundle.Contours.Count}\n" : "");

                ResetFindResultUi();
                AppendLog($"模型创建成功 ModelID={_modelId}, epoch={_modelEpoch} ({_modelKind})");
                string modelLabel = _modelKind == HalconFlowModelKind.Deformable ? "可变形模板" : "形状模型";
                MessageBox.Show($"{modelLabel}创建成功!\nModelID: {_modelId}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendLog($"[错误] {ex.Message}");
                MessageBox.Show($"创建模型失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _createModelInProgress = false;
                BtnCreateModel.IsEnabled = true;
                BtnTestFind.IsEnabled = _modelId >= 0;
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
#if HALCON_ENABLED
        private void SetFindShapeUiRunning(bool running)
        {
            BtnTestFind.IsEnabled = !running;
            BtnStopFind.IsEnabled = running;
            BtnStopFind.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BeginFindShapeRun(out CancellationToken findToken, out int runId)
        {
            _findShapeCts?.Cancel();
            _findShapeCts?.Dispose();
            runId = ++_findShapeRunId;
            _findShapeCts = new CancellationTokenSource();
            findToken = _findShapeCts.Token;
            SetFindShapeUiRunning(true);
        }

        private bool IsFindShapeRunCancelled(int runId) =>
            runId != _findShapeRunId || (_findShapeCts?.IsCancellationRequested ?? false);

        private void ApplyFindShapeStopped(int runId)
        {
            if (!IsFindShapeRunCancelled(runId))
                return;
            AppendLog("匹配已停止");
            ClearFindResultOverlay();
            TxtFindResult.Text = "已停止";
        }

        private bool TryConsumeFindResult(int runId, long modelId, int modelEpoch, HalconFlowModelKind modelKind)
        {
            if (IsFindShapeRunCancelled(runId))
            {
                ApplyFindShapeStopped(runId);
                return false;
            }

            if (!IsModelSessionCurrent(modelId, modelEpoch, modelKind))
            {
                AppendLog($"丢弃过期匹配结果（测试时 epoch={modelEpoch}/ModelID={modelId}，当前 epoch={_modelEpoch}/ModelID={_modelId}）");
                ResetFindResultUi();
                return false;
            }

            return true;
        }
#endif

        private void BtnStopFind_Click(object sender, RoutedEventArgs e)
        {
#if HALCON_ENABLED
            CancelFindShapeRun("已请求停止匹配…");
            BtnStopFind.IsEnabled = false;
            TxtFindResult.Text = "正在停止…（等待 HALCON 结束当前搜索）";
#endif
        }

        private async void BtnTestFind_Click(object sender, RoutedEventArgs e)
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

            if (_createModelInProgress)
            {
                MessageBox.Show("模型创建中，请稍候再测试匹配。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!HalconFlowBridge.TryGetRegisteredModelKind(_modelId, out _))
            {
                MessageBox.Show("当前模型已失效，请重新创建或导入。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                DisposeCurrentModel();
                TxtModelInfo.Text = "未创建模型";
                return;
            }

            if (_currentImage == null)
            {
                MessageBox.Show("请先加载图像", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BeginFindShapeRun(out CancellationToken findToken, out int runId);
            try
            {
                if (!double.TryParse(TxtAngleStart.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double angleStart)) angleStart = -30;
                if (!double.TryParse(TxtAngleExtent.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double angleExtent)) angleExtent = 60;
                if (!int.TryParse(TxtFindNumMatches.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numMatchesRaw) || numMatchesRaw < 0)
                    numMatchesRaw = 0;
                if (!int.TryParse(TxtFindGridRows.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gridRowsHint) || gridRowsHint < 1)
                    gridRowsHint = 3;
                if (!int.TryParse(TxtFindGridCols.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gridColsHint) || gridColsHint < 1)
                    gridColsHint = 3;
                int numMatches = HalconFlowBridge.ResolveFindNumMatches(numMatchesRaw, gridRowsHint, gridColsHint);
                if (numMatchesRaw <= 0)
                    AppendLog($"匹配个数: {numMatchesRaw} → 实际 {numMatches}（阵列 {gridRowsHint}×{gridColsHint} 或默认上限，避免搜全部匹配占满内存）");
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

                bool autoRetry = ChkFindAutoRetry.IsChecked == true;
                long modelId = _modelId;
                int modelEpoch = _modelEpoch;
                var modelKind = _modelKind;
                var image = _currentImage;

                AppendLog($"查找({modelKind}): ModelID={modelId}, epoch={modelEpoch}, NumMatches={numMatches}, MinScore={minScore}, Greediness={greediness}, NumLevels={findNumLevels}, 角度[{angleStart}°~{angleStart + angleExtent}°]");
                TxtFindResult.Text = "查找中…";

                if (modelKind == HalconFlowModelKind.Deformable)
                {
                    var scaleOpt = ReadCreateOptionsFromUi();
                    var findDef = await HalconComputeRunner.RunAsync(ct =>
                    {
                        if (autoRetry)
                        {
                            return HalconFlowBridge.FindDeformableModelWithFallback(
                                image, modelId, angleStart, angleExtent, minScore, numMatches, maxOverlap,
                                findNumLevels, greediness, scaleOpt, ct);
                        }

                        ct.ThrowIfCancellationRequested();
                        return HalconFlowBridge.FindDeformableModel(
                            image, modelId, angleStart, angleExtent, minScore, numMatches, maxOverlap,
                            findNumLevels, greediness, scaleOpt);
                    }, findToken);

                    if (!TryConsumeFindResult(runId, modelId, modelEpoch, modelKind))
                        return;

                    if (findDef.rows.Length == 0)
                    {
                        ClearFindResultOverlay();
                        TxtFindResult.Text = "未找到匹配";
                        AppendLog("未找到可变形匹配");
                    }
                    else
                    {
                        DrawDeformableFindResultOverlay(findDef.rows, findDef.cols, findDef.scores, findDef.deformedContoursPerMatch);
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine($"找到 {findDef.rows.Length} 个可变形匹配:");
                        for (int i = 0; i < findDef.rows.Length; i++)
                            sb.AppendLine($"[{i}] Row={findDef.rows[i]:F1}, Col={findDef.cols[i]:F1}, Score={findDef.scores[i]:F3}");
                        TxtFindResult.Text = sb.ToString();
                        AppendLog($"查找完成: {findDef.rows.Length} 个可变形匹配");
                    }

                    return;
                }

                var findResult = await HalconComputeRunner.RunAsync(ct =>
                {
                    if (autoRetry)
                    {
                        return HalconFlowBridge.FindShapeModelWithFallback(
                            image,
                            modelId,
                            angleStart,
                            angleExtent,
                            minScore,
                            numMatches,
                            maxOverlap,
                            "least_squares",
                            findNumLevels,
                            greediness,
                            scaleMin: 1.0,
                            scaleMax: 1.0,
                            cancellationToken: ct);
                    }

                    ct.ThrowIfCancellationRequested();
                    return HalconFlowBridge.FindShapeModel(
                        image,
                        modelId,
                        angleStart,
                        angleExtent,
                        minScore,
                        numMatches,
                        maxOverlap,
                        "least_squares",
                        findNumLevels,
                        greediness);
                }, findToken);

                if (!TryConsumeFindResult(runId, modelId, modelEpoch, modelKind))
                    return;

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

                    DrawFindResultOverlay(modelId, modelKind, rows, cols, angles, scores, gridRowOut, gridColOut);
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
                    DrawFindResultOverlay(modelId, modelKind, rows, cols, angles, scores);
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
            catch (OperationCanceledException)
            {
                ApplyFindShapeStopped(runId);
            }
            catch (Exception ex)
            {
                if (!IsFindShapeRunCancelled(runId))
                {
                    AppendLog($"[错误] {ex.Message}");
                    MessageBox.Show($"查找失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    ApplyFindShapeStopped(runId);
                }
            }
            finally
            {
                if (runId == _findShapeRunId)
                    SetFindShapeUiRunning(false);
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
                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            PrepareReplaceShapeModel();
            InvalidateContourCache();

            try
            {
                ResetFindResultUi();
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
                TxtModelInfo.Text = $"来源: 文件导入 ({_modelKind})\nEpoch: {_modelEpoch}\n文件: {dlg.FileName}\n{summary}";
                AppendLog($"模型已导入: {dlg.FileName} (ModelID={_modelId}, epoch={_modelEpoch}, {_modelKind})");
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
