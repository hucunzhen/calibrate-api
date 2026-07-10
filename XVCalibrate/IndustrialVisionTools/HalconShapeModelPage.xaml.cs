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

        /// <summary>矫正后顺时针旋转角度(°)，在 TryApplyCameraCorrections 末尾施加。</summary>
        private double _postCorrectRotateDeg;
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

        /// <summary>旋转矩形 ROI：中心、宽高(像素)、长边方向角(°)。</summary>
        private double _rotRectCenterX;
        private double _rotRectCenterY;
        private double _rotRectWidth;
        private double _rotRectHeight;
        private double _rotRectAngleDeg;
        /// <summary>已拖出大小，等待第二次单击确定角度。</summary>
        private bool _rotRectSettingAngle;

        // 圆形 ROI：圆心 + 半径（图像像素系，X=列 Y=行）
        private Point _circleCenterImage;
        private double _circleRadiusImage;

        // 环形 ROI：外圈 + 内圈；_ringPolygonPhase 0=绘外圈 1=绘内圈 2=完成
        private bool _hasRingRoi;
        private readonly RoiContourPath _ringOuterPath = new RoiContourPath();
        private readonly RoiContourPath _ringInnerPath = new RoiContourPath();
        private int _ringPolygonPhase;

        private bool _hasOpenTrajectoriesRoi;

        /// <summary>轨迹间连接线（不参与模板）。</summary>
        private readonly List<RoiConnectorSegment> _trajectoryConnectors = new();
        private Point? _connectorDraftStart;

        private List<Point>? _cachedRingOuterFlat;
        private List<Point>? _cachedRingInnerFlat;
        private bool _ringFlatDirty = true;
        /// <summary>闭合/吸附起点时在屏幕上的判点半径（px），会随视图缩放换算到图像坐标。</summary>
        private const double DefaultPolygonCloseDistanceViewPx = 14;
        private const int PolygonPreviewMoveIntervalMs = 16;
        private const int PreviewArcSegments = 12;
        private const int ClosedArcSegments = 24;
        private Point? _polygonCursorImage;
        private List<Point>? _cachedPolygonFlat;
        private bool _polygonFlatDirty = true;
        private long _lastPolygonPreviewMoveMs;

        private sealed class RoiArcEditEntry
        {
            public required RoiContourPath Path { get; init; }
            public int EdgeIndex { get; init; }
            public string Label { get; init; } = "";
        }

        private readonly List<RoiArcEditEntry> _roiArcEditEntries = new();
        private bool _suppressRoiArcEditUi;

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

        private bool _isMovingRoi;
        private bool _roiDragDeferThresholdPreview;
        private Point _roiDragStartMouse;
        private RoiDragKind _roiDragKind;
        private RoiDragSnapshot _roiDragSnapshot;

        private enum RoiDragKind { None, Rect, RotatedRect, Circle, Polygon, Ring }

        private sealed class RoiDragSnapshot
        {
            public RoiDragKind Kind;
            public Rect Rect;
            public double RotCenterX;
            public double RotCenterY;
            public double RotWidth;
            public double RotHeight;
            public double RotAngleDeg;
            public Point CircleCenter;
            public double CircleRadius;
            public RoiContourPath? Polygon;
            public RoiContourPath? RingOuter;
            public RoiContourPath? RingInner;
        }

        private const double MinScale = 0.1;
        private const double MaxScale = 10.0;
        private bool _viewHostLayoutFitted;

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
            ViewHost.SizeChanged += ViewHost_SizeChanged;
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
            _postCorrectRotateDeg = 0;
            SyncPostCorrectRotateTextBox();

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
            _postCorrectRotateDeg = 0;
            SyncPostCorrectRotateTextBox();

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
            return 2;
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

        private static double NormalizePostCorrectRotateDeg(double angleDeg)
        {
            double a = angleDeg % 360.0;
            if (a < 0)
                a += 360.0;
            return a;
        }

        private void SyncPostCorrectRotateTextBox()
        {
            if (TxtPostCorrectRotateDeg != null)
                TxtPostCorrectRotateDeg.Text = _postCorrectRotateDeg.ToString("F0", CultureInfo.InvariantCulture);
        }

        private CalibImage ApplyPostCorrectRotation(CalibImage work)
        {
            if (Math.Abs(_postCorrectRotateDeg) < 1e-6)
                return work;

            bool expand = ChkRotateExpandCanvas?.IsChecked == true;
            CalibImage rotated = CalibImageTransform.Rotate(work, _postCorrectRotateDeg, expand);
            if (!ReferenceEquals(rotated, work))
                work.Dispose();
            return rotated;
        }

        private void ChangePostCorrectRotation(double newDegCw, bool logSuccess)
        {
            if (_rawCalibImage == null)
            {
                MessageBox.Show("请先加载图像", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _postCorrectRotateDeg = NormalizePostCorrectRotateDeg(newDegCw);
            SyncPostCorrectRotateTextBox();
            ClearRoiState();
#if HALCON_ENABLED
            InvalidateContourCache();
#endif
            try
            {
                TryApplyCameraCorrections(logSuccess: logSuccess);
                FitImageToView();
                PersistSessionChange();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"旋转失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnRotateCorrectedCw90_Click(object sender, RoutedEventArgs e) =>
            ChangePostCorrectRotation(_postCorrectRotateDeg + 90, logSuccess: true);

        private void BtnRotateCorrectedCcw90_Click(object sender, RoutedEventArgs e) =>
            ChangePostCorrectRotation(_postCorrectRotateDeg + 270, logSuccess: true);

        private void BtnResetCorrectedRotate_Click(object sender, RoutedEventArgs e) =>
            ChangePostCorrectRotation(0, logSuccess: true);

        private void BtnApplyPostCorrectRotate_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(TxtPostCorrectRotateDeg?.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double deg))
            {
                MessageBox.Show("请输入有效旋转角度(°)", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ChangePostCorrectRotation(deg, logSuccess: true);
        }

        /// <summary>从 _rawCalibImage 应用内参/透视矫正、矫正后旋转，并刷新显示与 _currentImage。</summary>
        private bool TryApplyCameraCorrections(bool logSuccess)
        {
            if (_rawCalibImage == null)
                return false;

            bool undistort = ChkEnableUndistort.IsChecked == true;
            bool perspective = ChkEnablePerspective.IsChecked == true;
            if (!undistort && !perspective)
            {
                CalibImage? raw = CalibAPI.DuplicateImage(_rawCalibImage);
                try
                {
                    CalibImage display = ApplyPostCorrectRotation(raw);
                    CommitCalibImageToUi(display, disposeIncoming: true);
                }
                catch
                {
                    raw?.Dispose();
                    throw;
                }

                if (logSuccess)
                {
                    string rotNote = Math.Abs(_postCorrectRotateDeg) < 1e-6
                        ? ""
                        : $"，顺时针旋转 {_postCorrectRotateDeg:F0}°";
                    AppendLog($"相机矫正: 未启用，使用原图{rotNote}");
                }

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
                    int viewIdx = CalibAPI.ParseChessboardViewIndex(TxtCalibViewIndex.Text);
                    int cols = int.TryParse(TxtCalibBoardCols.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? c : ChessboardCalibrationDefaults.InnerCornerCols;
                    int rows = int.TryParse(TxtCalibBoardRows.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : ChessboardCalibrationDefaults.InnerCornerRows;
                    double sq = double.TryParse(TxtCalibSquareSizeMm.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : ChessboardCalibrationDefaults.SquareSizeMm;
                    double pxPerMm = double.TryParse(TxtCalibPxPerMm.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double ppm) ? ppm : ChessboardCalibrationDefaults.PxPerMm;
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
                        string viewNote = viewIdx == -1 ? "view=axis(光轴对称)" : $"view={viewIdx}";
                        AppendLog($"透视矫正: {viewNote} {cols}x{rows} 格, 输出={frame}, {work.Width}x{work.Height}px");
                    }
                }

                CalibImage display = ApplyPostCorrectRotation(work);
                CommitCalibImageToUi(display, disposeIncoming: true);
                owned = null;
                if (logSuccess)
                {
                    string rotNote = Math.Abs(_postCorrectRotateDeg) < 1e-6
                        ? ""
                        : $"，顺时针旋转 {_postCorrectRotateDeg:F0}°";
                    AppendLog($"相机矫正完成 → {_imgWidth}x{_imgHeight}{rotNote}");
                }

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
            if (sn.data == IntPtr.Zero || w <= 0 || h <= 0)
                throw new InvalidOperationException("图像像素数据无效，无法刷新显示");

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

        private CalibImage DuplicateCurrentImageForHalcon()
        {
            if (_currentImage == null)
                throw new InvalidOperationException("请先加载图像");
            return CalibAPI.DuplicateImage(_currentImage);
        }

        /// <summary>将 ViewHost 上的鼠标位置反算为图像像素坐标（与 ImageCanvas 变换一致）。</summary>
        private Point GetImagePointFromMouse(MouseEventArgs e) =>
            HostPointToImage(e.GetPosition(ViewHost));

        private Point HostPointToImage(Point hostPt)
        {
            try
            {
                GeneralTransform? toHost = ImageCanvas.TransformToVisual(ViewHost);
                if (toHost?.Inverse is GeneralTransform toCanvas)
                    return ClampImagePoint(toCanvas.Transform(hostPt));
            }
            catch (InvalidOperationException)
            {
                // 布局未完成时 TransformToVisual 可能失败，走手工逆变换
            }

            double scale = Math.Max(_scale, 1e-9);
            return ClampImagePoint(new Point(
                (hostPt.X - _offsetX) / scale,
                (hostPt.Y - _offsetY) / scale));
        }

        private Point ClampImagePoint(Point pt)
        {
            double x = pt.X;
            double y = pt.Y;
            if (_imgWidth > 0 && _imgHeight > 0)
            {
                x = Math.Max(0, Math.Min(_imgWidth - 1, x));
                y = Math.Max(0, Math.Min(_imgHeight - 1, y));
            }

            return new Point(x, y);
        }

        private bool TryPromptLandingPoint(Point mouseHint, string title, string? hint, out Point confirmed)
        {
            var dlg = new RoiPointLandingDialog
            {
                Owner = Window.GetWindow(this),
                Title = title
            };
            AffineTransform? affine = null;
#if HALCON_ENABLED
            affine = TryGetNinePointAffineOptional();
#endif
            dlg.Initialize(mouseHint.X, mouseHint.Y, affine, _imgWidth, _imgHeight, hint);
            if (dlg.ShowDialog() != true)
            {
                confirmed = default;
                return false;
            }

            confirmed = new Point(dlg.ColumnPx, dlg.RowPx);
            return true;
        }

        private bool IsPrimitiveRoiDrawModeActive() =>
            RbRectMode?.IsChecked == true || IsRotatedRectModeActive() || IsCircleModeActive();

        private RoiPrimitiveKind GetActivePrimitiveRoiKind()
        {
            if (IsCircleModeActive())
                return RoiPrimitiveKind.Circle;
            if (IsRotatedRectModeActive())
                return RoiPrimitiveKind.RotatedRectangle;
            return RoiPrimitiveKind.Rectangle;
        }

        private Point GetImageCenterPoint() =>
            _imgWidth > 0 && _imgHeight > 0
                ? new Point(_imgWidth * 0.5, _imgHeight * 0.5)
                : default;

        private double DefaultPrimitiveExtent() =>
            _imgWidth > 0 && _imgHeight > 0
                ? Math.Max(40, Math.Min(_imgWidth, _imgHeight) * 0.25)
                : 100;

        private Point GetPrimitiveRoiDialogAnchorHint()
        {
            if (_hasRoi && RbRectMode?.IsChecked == true && _roiRectImage.Width > 5 && _roiRectImage.Height > 5)
                return new Point(_roiRectImage.X, _roiRectImage.Y);
            if (HasUsableRotatedRectRoi())
                return new Point(_rotRectCenterX, _rotRectCenterY);
            if (HasUsableCircleRoi())
                return _circleCenterImage;
            return GetImageCenterPoint();
        }

        private bool TryPromptPrimitiveRoiParams(Point anchorHint)
        {
            if (!IsPrimitiveRoiDrawModeActive() || _imgWidth <= 0 || _imgHeight <= 0)
                return false;

            var kind = GetActivePrimitiveRoiKind();
            Point anchor = ClampImagePoint(anchorHint);
            double extent = DefaultPrimitiveExtent();

            double rectLeft = anchor.X;
            double rectTop = anchor.Y;
            double rectWidth = extent;
            double rectHeight = Math.Max(40, extent * 0.75);
            if (kind == RoiPrimitiveKind.Rectangle && _hasRoi && RbRectMode?.IsChecked == true
                && _roiRectImage.Width > 5 && _roiRectImage.Height > 5)
            {
                rectLeft = _roiRectImage.X;
                rectTop = _roiRectImage.Y;
                rectWidth = _roiRectImage.Width;
                rectHeight = _roiRectImage.Height;
            }

            double rotCx = anchor.X;
            double rotCy = anchor.Y;
            double rotW = extent;
            double rotH = Math.Max(40, extent * 0.75);
            double rotAngle = 0;
            if (kind == RoiPrimitiveKind.RotatedRectangle && HasUsableRotatedRectRoi())
            {
                rotCx = _rotRectCenterX;
                rotCy = _rotRectCenterY;
                rotW = _rotRectWidth;
                rotH = _rotRectHeight;
                rotAngle = _rotRectAngleDeg;
            }

            double circleCx = anchor.X;
            double circleCy = anchor.Y;
            double circleR = Math.Max(20, extent * 0.5);
            if (kind == RoiPrimitiveKind.Circle && HasUsableCircleRoi())
            {
                circleCx = _circleCenterImage.X;
                circleCy = _circleCenterImage.Y;
                circleR = _circleRadiusImage;
            }

            var dlg = new RoiPrimitiveParamsDialog { Owner = Window.GetWindow(this) };
            if (!TryGetNinePointAffine(out AffineTransform affine, out string affineErr))
            {
                MessageBox.Show(
                    $"矩形/旋转矩形/圆形 ROI 参数使用 mm，须先加载有效的九点标定 JSON。\n{affineErr}",
                    "九点标定",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }

            dlg.Initialize(
                kind,
                rectLeft, rectTop, rectWidth, rectHeight,
                rotCx, rotCy, rotW, rotH, rotAngle,
                circleCx, circleCy, circleR,
                affine,
                _imgWidth,
                _imgHeight);
            if (dlg.ShowDialog() != true)
                return false;

            return ApplyPrimitiveRoiFromDialog(dlg);
        }

        private bool ApplyPrimitiveRoiFromDialog(RoiPrimitiveParamsDialog dlg)
        {
            bool hasAffine = TryGetNinePointAffine(out AffineTransform affine, out _);

            switch (GetActivePrimitiveRoiKind())
            {
                case RoiPrimitiveKind.Rectangle:
                {
                    double x = dlg.RectLeft;
                    double y = dlg.RectTop;
                    double w = dlg.RectWidth;
                    double h = dlg.RectHeight;
                    ClampRoiToImage(ref x, ref y, ref w, ref h);
                    _roiRectImage = new Rect(x, y, w, h);
                    _hasRoi = true;
                    _rotRectSettingAngle = false;
                    if (hasAffine)
                    {
                        var tlMm = HalconGeometryContourBuilder.ImagePixelToWorldMm(x, y, affine);
                        (double wMm, double hMm) = HalconGeometryContourBuilder.PixelSizeToWorldMm(w, h, x, y, affine);
                        AppendLog($"矩形ROI: ({tlMm.X:F2},{tlMm.Y:F2}) mm {wMm:F2}×{hMm:F2} mm");
                    }
                    else
                        AppendLog($"矩形ROI: ({x:F0},{y:F0}) {w:F0}x{h:F0} px");
                    break;
                }
                case RoiPrimitiveKind.RotatedRectangle:
                {
                    _rotRectCenterX = dlg.RotCenterX;
                    _rotRectCenterY = dlg.RotCenterY;
                    _rotRectWidth = Math.Max(6, dlg.RotWidth);
                    _rotRectHeight = Math.Max(6, dlg.RotHeight);
                    _rotRectAngleDeg = dlg.RotAngleDeg;
                    _rotRectSettingAngle = false;
                    _hasRoi = true;
                    if (hasAffine)
                    {
                        var centerMm = HalconGeometryContourBuilder.ImagePixelToWorldMm(_rotRectCenterX, _rotRectCenterY, affine);
                        double rad = _rotRectAngleDeg * Math.PI / 180.0;
                        double dirX = Math.Cos(rad);
                        double dirY = Math.Sin(rad);
                        double rwMm = RoiSegmentMeasure.PixelLengthToWorldMm(
                            _rotRectWidth, _rotRectCenterX, _rotRectCenterY, dirX, dirY, affine);
                        double rhMm = RoiSegmentMeasure.PixelLengthToWorldMm(
                            _rotRectHeight, _rotRectCenterX, _rotRectCenterY, -dirY, dirX, affine);
                        AppendLog($"旋转矩形 ROI: 中心=({centerMm.X:F2},{centerMm.Y:F2}) mm {rwMm:F2}×{rhMm:F2} mm ∠{_rotRectAngleDeg:F1}°");
                    }
                    else
                        AppendLog($"旋转矩形 ROI: 中心=({_rotRectCenterX:F0},{_rotRectCenterY:F0}) {_rotRectWidth:F0}×{_rotRectHeight:F0} px ∠{_rotRectAngleDeg:F1}°");
                    break;
                }
                default:
                {
                    _circleCenterImage = ClampImagePoint(new Point(dlg.CircleCenterX, dlg.CircleCenterY));
                    _circleRadiusImage = dlg.CircleRadius;
                    ClampCircleRadiusToImage();
                    if (_circleRadiusImage <= 5)
                    {
                        MessageBox.Show(
                            "圆形 ROI 无效：圆心超出图像范围，或半径换算后过小/超出边界。请调整 mm 参数。",
                            "ROI 参数",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return false;
                    }

                    _hasRoi = true;
                    if (hasAffine)
                    {
                        var centerMm = HalconGeometryContourBuilder.ImagePixelToWorldMm(
                            _circleCenterImage.X, _circleCenterImage.Y, affine);
                        double rMm = HalconGeometryContourBuilder.PixelRadiusToWorldMm(
                            _circleRadiusImage, _circleCenterImage.X, _circleCenterImage.Y, affine);
                        AppendLog($"圆形ROI: 圆心=({centerMm.X:F2},{centerMm.Y:F2}) mm R={rMm:F2} mm");
                    }
                    else
                        AppendLog($"圆形ROI: 圆心=({_circleCenterImage.X:F0},{_circleCenterImage.Y:F0}) R={_circleRadiusImage:F0} px");
                    break;
                }
            }

            RefreshRoiVisuals();
            UpdateThresholdPreview();
            PersistSessionChange();
#if HALCON_ENABLED
            TryAutoSyncGeometryFromRoiIfNeeded();
#endif
            return true;
        }

        private void BtnPrimitiveRoiParams_Click(object sender, RoutedEventArgs e)
        {
            if (_imgWidth <= 0 || _imgHeight <= 0)
            {
                MessageBox.Show("请先加载图像", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TryPromptPrimitiveRoiParams(GetPrimitiveRoiDialogAnchorHint());
        }

        private bool TryAddFirstVertexWithDialog(RoiContourPath path, Point mouse, string logPrefix)
        {
            Point hint = mouse;
            string? prompt = null;
            if (IsOpenTrajectoriesModeActive() && path.VertexCount == 0 && TryGetLastConnectorEnd(out Point connEnd))
            {
                hint = connEnd;
                prompt = "默认取最近一条连接线的终点；可修改后作为本条轨迹起点。";
            }

            if (!TryPromptLandingPoint(hint, "落点坐标", prompt ?? "可修改列 X、行 Y（图像像素）。", out Point p))
                return false;

            path.AddFirstVertex(p);
            bool fromConn = IsOpenTrajectoriesModeActive()
                            && TryGetLastConnectorEnd(out Point ce)
                            && Dist(p, ce) < 1.0;
            AppendLog(fromConn
                ? $"{logPrefix}: 第 1 点 ({p.X:F1}, {p.Y:F1})（连接线终点）"
                : $"{logPrefix}: 第 1 点 ({p.X:F1}, {p.Y:F1})");
            return true;
        }

        private bool TrySetArcDraftEndWithDialog(Point mouse, string logPrefix)
        {
            if (!TryPromptLandingPoint(mouse, "圆弧段终点", "已用鼠标选定终点，可修改列 X、行 Y。", out Point p))
                return false;

            _arcDraftEnd = p;
            _tangentArcPreferLeft = null;
            UpdateTangentFlipButtonVisibility();
            AppendLog($"{logPrefix} 圆弧：终点 ({p.X:F1}, {p.Y:F1})，请点击弧上经过的一点");
            return true;
        }

        private double ViewPixelsToImagePixels(double viewPx) =>
            viewPx / Math.Max(_scale, 1e-9);

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

            if (ViewHost.ActualWidth <= 1 || ViewHost.ActualHeight <= 1)
            {
                void OnLayoutUpdated(object? s, EventArgs e)
                {
                    if (ViewHost.ActualWidth <= 1 || ViewHost.ActualHeight <= 1)
                        return;
                    ViewHost.LayoutUpdated -= OnLayoutUpdated;
                    DoFitImageToView();
                }

                ViewHost.LayoutUpdated += OnLayoutUpdated;
                return;
            }

            DoFitImageToView();
        }

        private void DoFitImageToView()
        {
            if (_imgWidth <= 0 || _imgHeight <= 0) return;

            var host = ViewHost;
            double viewW = host.ActualWidth;
            double viewH = host.ActualHeight;
            if (viewW <= 1 || viewH <= 1)
                return;

            double fit = Math.Min(viewW / _imgWidth, viewH / _imgHeight) * 0.95;
            fit = Math.Max(MinScale, Math.Min(MaxScale, fit));
            _scale = fit;
            _offsetX = (viewW - _imgWidth * _scale) / 2;
            _offsetY = (viewH - _imgHeight * _scale) / 2;
            _viewHostLayoutFitted = true;
            ApplyTransform();
            RefreshRoiVisuals();
            UpdateThresholdPreview();
        }

        private void ViewHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_uiReady || _imgWidth <= 0 || _imgHeight <= 0)
                return;
            if (e.NewSize.Width <= 1 || e.NewSize.Height <= 1)
                return;

            // 首次获得有效尺寸，或从 0 布局中恢复：完整适配
            if (!_viewHostLayoutFitted || e.PreviousSize.Width <= 1 || e.PreviousSize.Height <= 1)
            {
                DoFitImageToView();
                return;
            }

            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5
                && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5)
                return;

            // 窗口最大化/全屏等：保持缩放，仅重新居中
            _offsetX = (e.NewSize.Width - _imgWidth * _scale) / 2;
            _offsetY = (e.NewSize.Height - _imgHeight * _scale) / 2;
            ApplyTransform();
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
            else
                RoiRect.Visibility = Visibility.Collapsed;

            UpdateRotatedRectPreview();
            UpdateCirclePreview();
            UpdatePolygonPreview();
            UpdateRingPreview();
        }

        private bool IsCircleModeActive() => RbCircleMode?.IsChecked == true;

        private bool IsRotatedRectModeActive() => RbRotatedRectMode?.IsChecked == true;

        private bool HasUsableRotatedRectRoi() =>
            _hasRoi && IsRotatedRectModeActive() && !_rotRectSettingAngle
            && _rotRectWidth > 5 && _rotRectHeight > 5;

        private List<Point> GetRotatedRectCornerPoints(
            double centerCol,
            double centerRow,
            double widthPx,
            double heightPx,
            double angleDeg)
        {
#if HALCON_ENABLED
            Point2D[] pts = HalconGeometryContourBuilder.BuildRotatedRectangle(
                centerCol, centerRow, widthPx * 0.5, heightPx * 0.5, angleDeg, 1);
            var list = new List<Point>(pts.Length + 1);
            foreach (Point2D p in pts)
                list.Add(new Point(p.X, p.Y));
            if (list.Count > 0)
                list.Add(list[0]);
            return list;
#else
            return new List<Point>();
#endif
        }

        private void UpdateRotatedRectPreview()
        {
            if (RoiRotatedRectLine == null)
                return;

            bool show = (HasUsableRotatedRectRoi() || _rotRectSettingAngle)
                && (IsRotatedRectModeActive() || _rotRectSettingAngle);
            if (!show)
            {
                RoiRotatedRectLine.Visibility = Visibility.Collapsed;
                RoiRotatedRectLine.Points.Clear();
                return;
            }

            var corners = GetRotatedRectCornerPoints(
                _rotRectCenterX, _rotRectCenterY, _rotRectWidth, _rotRectHeight, _rotRectAngleDeg);
            if (corners.Count < 2)
            {
                RoiRotatedRectLine.Visibility = Visibility.Collapsed;
                return;
            }

            RoiRotatedRectLine.Visibility = Visibility.Visible;
            RoiRotatedRectLine.Points = new PointCollection(corners);
        }

        private bool IsPointInRotatedRectRoi(double x, double y)
        {
            if (!HasUsableRotatedRectRoi())
                return false;

            double dx = x - _rotRectCenterX;
            double dy = y - _rotRectCenterY;
            double rad = -_rotRectAngleDeg * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            double lx = dx * cos - dy * sin;
            double ly = dx * sin + dy * cos;
            return Math.Abs(lx) <= _rotRectWidth * 0.5 && Math.Abs(ly) <= _rotRectHeight * 0.5;
        }

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

        private bool IsOpenTrajectoriesModeActive() => RbOpenTrajectoriesMode?.IsChecked == true;

        private bool HasUsableOpenTrajectoriesForTemplate() =>
            IsOpenTrajectoriesModeActive()
            && (CollectOpenTrajectoriesForTemplate().Count > 0);

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

        private void ResetOpenTrajectoriesDrawState()
        {
            _openTrajectoryEntries.Clear();
            _editingOpenTrajectoryIndex = null;
            _hasOpenTrajectoriesRoi = false;
            _trajectoryConnectors.Clear();
            _connectorDraftStart = null;
            if (OpenTrajectoriesLayer != null)
                OpenTrajectoriesLayer.Children.Clear();
            RebuildConnectorsVisual();
            UpdateConnectorRubberVisual();
        }

        private bool TryGetLastConnectorEnd(out Point end)
        {
            if (_trajectoryConnectors.Count == 0)
            {
                end = default;
                return false;
            }

            RoiConnectorSegment last = _trajectoryConnectors[^1];
            end = last.End;
            return true;
        }

        private bool TryGetConnectorAnchorStart(out Point start)
        {
            if (_connectorDraftStart is Point draft)
            {
                start = draft;
                return true;
            }

            if (_roiPath.VertexCount >= 1)
            {
                start = _roiPath.Vertices[^1];
                return true;
            }

            if (_openTrajectoryEntries.Count > 0)
            {
                RoiContourPath last = _openTrajectoryEntries[^1].Path;
                if (last.VertexCount >= 1)
                {
                    start = last.Vertices[^1];
                    return true;
                }
            }

            start = default;
            return false;
        }

        /// <summary>连接线落点后：必要时保存上一条草稿，并在连接线终点自动开始下一条轨迹。</summary>
        private void OnConnectorCommitted(Point connectorEnd)
        {
            if (_roiPath.VertexCount >= 2)
                TryCommitCurrentOpenTrajectoryDraft(showMessage: false);
            else if (_roiPath.VertexCount > 0)
                _roiPath.Clear();

            TryResumeTrajectoryAfterConnector(connectorEnd);
        }

        private void TryResumeTrajectoryAfterConnector(Point connectorEnd)
        {
            if (!IsOpenTrajectoriesModeActive() || _roiPath.VertexCount > 0)
                return;

            _roiPath.AddFirstVertex(connectorEnd);
            if (RbNextSegmentLine != null)
                RbNextSegmentLine.IsChecked = true;
            UpdateNextSegmentToolbar();

            InvalidatePolygonFlatCache();
            UpdatePolygonPreview();
            UpdateConnectorRubberVisual();
            AppendLog($"开放轨迹: 已从连接线终点 ({connectorEnd.X:F1},{connectorEnd.Y:F1}) 继续，移动鼠标后单击绘制下一段");
            PersistSessionChange();
        }

        private void RebuildConnectorsVisual()
        {
            if (ConnectorsLayer == null)
                return;

            ConnectorsLayer.Children.Clear();
            foreach (RoiConnectorSegment seg in _trajectoryConnectors)
            {
                var poly = new Polyline
                {
                    Stroke = Brushes.Gray,
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 6, 4 },
                    Points = new PointCollection { seg.Start, seg.End }
                };
                ConnectorsLayer.Children.Add(poly);
            }
        }

        private void UpdateConnectorRubberVisual()
        {
            if (ConnectorRubberLine == null)
                return;

            if (!IsOpenTrajectoriesModeActive()
                || (!IsNextSegmentConnector() && _connectorDraftStart == null))
            {
                ConnectorRubberLine.Visibility = Visibility.Collapsed;
                ConnectorRubberLine.Points.Clear();
                return;
            }

            if (!TryGetConnectorAnchorStart(out Point start) || _polygonCursorImage is not Point cursor)
            {
                ConnectorRubberLine.Visibility = Visibility.Collapsed;
                ConnectorRubberLine.Points.Clear();
                return;
            }

            ConnectorRubberLine.Visibility = Visibility.Visible;
            ConnectorRubberLine.Points = new PointCollection { start, cursor };
        }

        private bool TryCommitConnectorLineAtMouse(Point anchorStart, Point directionMouse, string logPrefix)
        {
            double dirX = directionMouse.X - anchorStart.X;
            double dirY = directionMouse.Y - anchorStart.Y;
            double initLenPx = Dist(anchorStart, directionMouse);
            if (initLenPx < 1e-6)
                initLenPx = 10;
            else
            {
                dirX /= initLenPx;
                dirY /= initLenPx;
            }

            var dlg = new RoiLineSegmentParamsDialog
            {
                Owner = Window.GetWindow(this),
                Title = "连接线"
            };
            AffineTransform? affineForDialog = null;
#if HALCON_ENABLED
            affineForDialog = TryGetNinePointAffineOptional();
#endif
            dlg.Initialize(anchorStart.X, anchorStart.Y, dirX, dirY, initLenPx, affineForDialog, alongTangent: false);
            if (dlg.ShowDialog() != true)
            {
                AppendLog($"{logPrefix} 已取消连接线");
                return false;
            }

            Point connectorEnd = new Point(dlg.EndColumnPx, dlg.EndRowPx);
            var seg = new RoiConnectorSegment(anchorStart, connectorEnd);
            _trajectoryConnectors.Add(seg);
            _connectorDraftStart = null;
            RebuildConnectorsVisual();
            UpdateConnectorRubberVisual();
            AppendLog($"{logPrefix}: 连接线 ({seg.StartX:F1},{seg.StartY:F1})→({seg.EndX:F1},{seg.EndY:F1})，共 {_trajectoryConnectors.Count} 条（不参与模板）");
            OnConnectorCommitted(connectorEnd);
            return true;
        }

        private void HandleOpenTrajectoryConnectorClick(Point imgPt)
        {
            const string label = "连接线";

            if (!TryGetConnectorAnchorStart(out Point start))
            {
                if (!TryPromptLandingPoint(imgPt, "连接线起点",
                        "指定连接线起点；通常为上一条轨迹的终点。", out Point p))
                    return;

                _connectorDraftStart = p;
                UpdateConnectorRubberVisual();
                AppendLog($"{label}: 起点 ({p.X:F1}, {p.Y:F1})，移动鼠标后单击确定终点");
                return;
            }

            TryCommitConnectorLineAtMouse(start, imgPt, label);
        }

        private void RebuildOpenTrajectoriesVisual()
        {
            if (OpenTrajectoriesLayer == null)
                return;

            OpenTrajectoriesLayer.Children.Clear();
            Brush[] palette =
            {
                Brushes.Lime,
                Brushes.Cyan,
                Brushes.Orange,
                Brushes.Magenta,
                Brushes.DeepSkyBlue
            };

            int i = 0;
            foreach (OpenTrajectoryEntry entry in _openTrajectoryEntries)
            {
                List<Point> pts = entry.Path.BuildFlattenedPolygon(false, arcSegments: ClosedArcSegments);
                if (pts.Count < 2)
                    continue;

                var poly = new Polyline
                {
                    Stroke = palette[i % palette.Length],
                    StrokeThickness = 2,
                    Points = new PointCollection(pts)
                };
                OpenTrajectoriesLayer.Children.Add(poly);
                i++;
            }
        }

        private List<Point2D> GetOpenTrajectoryDensePoints(RoiContourPath path) =>
            path.BuildFlattenedPolygon(false, arcSegments: ClosedArcSegments)
                .Select(p => new Point2D(p.X, p.Y))
                .ToList();

        private List<List<Point2D>> CollectOpenTrajectoriesForTemplate()
        {
            var list = new List<List<Point2D>>();
            foreach (OpenTrajectoryEntry entry in _openTrajectoryEntries)
            {
                List<Point2D> dense = GetOpenTrajectoryDensePoints(entry.Path);
                if (dense.Count >= 2)
                    list.Add(dense);
            }

            if (_roiPath.VertexCount >= 2)
            {
                List<Point2D> draft = GetOpenTrajectoryDensePoints(_roiPath);
                if (draft.Count >= 2)
                    list.Add(draft);
            }

            return list;
        }

        private bool TryCommitCurrentOpenTrajectoryDraft(bool showMessage)
        {
            if (_arcDraftEnd != null)
            {
                if (showMessage)
                    MessageBox.Show("圆弧段尚未完成，请完成或撤销后再保存本条轨迹。", "开放轨迹",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            if (_roiPath.VertexCount < 2)
            {
                if (showMessage)
                    MessageBox.Show("当前轨迹至少需要 2 个点才能保存。", "开放轨迹",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            RoiContourPath saved = HalconGeometryPathEditor.ClonePath(_roiPath);
            saved.IsClosed = false;
            if (_editingOpenTrajectoryIndex is int editIdx
                && editIdx >= 0
                && editIdx < _openTrajectoryEntries.Count)
            {
                string name = _openTrajectoryEntries[editIdx].Name;
                _openTrajectoryEntries[editIdx].Path = saved;
                _editingOpenTrajectoryIndex = null;
                AppendLog($"开放轨迹: 已更新「{name}」");
            }
            else
            {
                _openTrajectoryEntries.Add(new OpenTrajectoryEntry
                {
                    Name = DefaultTrajectoryName(_openTrajectoryEntries.Count),
                    Path = saved
                });
                AppendLog($"开放轨迹: 已保存第 {_openTrajectoryEntries.Count} 条「{_openTrajectoryEntries[^1].Name}」（不闭合）");
            }

            _roiPath.Clear();
            _hasOpenTrajectoriesRoi = true;
            _hasRoi = true;
            CancelArcDraft();
            InvalidatePolygonFlatCache();
            RebuildOpenTrajectoriesVisual();
            RebuildConnectorsVisual();
            UpdatePolygonPreview();
            UpdateConnectorRubberVisual();
            RefreshOpenTrajectoryListUi();
            PersistSessionChange();
            return true;
        }

        private void BtnFinishOpenTrajectory_Click(object sender, RoutedEventArgs e) =>
            TryCommitCurrentOpenTrajectoryDraft(showMessage: true);

        private void BtnNewOpenTrajectory_Click(object sender, RoutedEventArgs e)
        {
            if (_roiPath.VertexCount >= 2)
                TryCommitCurrentOpenTrajectoryDraft(showMessage: false);

            if (_arcDraftEnd != null)
                CancelArcDraft();

            _editingOpenTrajectoryIndex = null;
            _roiPath.Clear();
            _connectorDraftStart = null;
            InvalidatePolygonFlatCache();
            UpdatePolygonPreview();
            UpdateConnectorRubberVisual();
            RefreshOpenTrajectoryListUi();
            AppendLog("开放轨迹: 开始绘制新的一条");
            PersistSessionChange();
        }

        private void HandleOpenTrajectoryClick(Point imgPt)
        {
            const string label = "开放轨迹";

            if (IsNextSegmentConnector())
            {
                HandleOpenTrajectoryConnectorClick(imgPt);
                return;
            }

            if (_arcDraftEnd is Point end)
            {
                TryCommitArcDraftClick(_roiPath, end, imgPt, label, closingToStart: false);
                return;
            }

            if (IsNextSegmentArc())
            {
                if (_roiPath.VertexCount == 0)
                {
                    TryAddFirstVertexWithDialog(_roiPath, imgPt, label);
                    return;
                }

                if (IsTangentJoinEnabled() && _roiPath.VertexCount >= 2)
                {
                    TryCommitTangentArcAtMouse(_roiPath, imgPt, label);
                    return;
                }

                TrySetArcDraftEndWithDialog(imgPt, label);
                return;
            }

            if (_roiPath.VertexCount == 0)
            {
                TryAddFirstVertexWithDialog(_roiPath, imgPt, label);
                return;
            }

            TryCommitLineSegmentAtMouse(_roiPath, imgPt, label);
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

        /// <summary>起点吸附/闭合判定的半径（图像像素，由屏幕容差随缩放换算）。</summary>
        private double GetPolygonCloseDistanceImagePx()
        {
            double viewPx = DefaultPolygonCloseDistanceViewPx;
            if (TxtPolygonCloseDist != null
                && double.TryParse(TxtPolygonCloseDist.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                && d > 0)
                viewPx = d;
            return ViewPixelsToImagePixels(viewPx);
        }

        private bool IsNextSegmentArc() => RbNextSegmentArc?.IsChecked == true;

        private bool IsNextSegmentConnector() => RbNextSegmentConnector?.IsChecked == true;

        private bool IsTangentJoinEnabled() => ChkTangentJoin?.IsChecked == true;

        private void NextSegment_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded)
                return;

            if (!IsNextSegmentConnector())
                _connectorDraftStart = null;

            UpdateNextSegmentToolbar();
            UpdateConnectorRubberVisual();
        }

        private void UpdateNextSegmentToolbar()
        {
            bool open = IsOpenTrajectoriesModeActive();
            if (RbNextSegmentConnector != null)
                RbNextSegmentConnector.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

            bool connector = IsNextSegmentConnector() && open;
            if (ChkTangentJoin != null)
                ChkTangentJoin.Visibility = connector ? Visibility.Collapsed : Visibility.Visible;
            if (connector && ChkTangentJoin?.IsChecked == true)
                ChkTangentJoin.IsChecked = false;

            UpdateTangentFlipButtonVisibility();
        }

        private bool TryResolveArcViaForDraft(RoiContourPath path, Point start, Point end, Point sideHint, out Point via)
        {
            via = default;
            if (!IsTangentJoinEnabled() || path.VertexCount < 2)
                return false;
            return RoiContourPathTangent.TryComputeArcViaTangentAtStartFromPath(
                path, start, end, sideHint, _tangentArcPreferLeft, out via);
        }

        private void UpdateTangentFlipButtonVisibility()
        {
            if (BtnTangentFlipSide == null)
                return;

            bool drawingPath = RbPolygonMode?.IsChecked == true || IsRingModeActive() || IsOpenTrajectoriesModeActive();
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
            if (_arcDraftEnd == null && IsNextSegmentArc())
            {
                if (!RoiContourPathTangent.TryGetIncomingTravelTangent(path, out double tanX, out double tanY))
                    return;
                if (!_tangentArcPreferLeft.HasValue)
                    _tangentArcPreferLeft = RoiContourPathTangent.TangentSideSign(
                        tanX, tanY, start, _polygonCursorImage ?? start) >= 0;
                _tangentArcPreferLeft = !_tangentArcPreferLeft.Value;
                AppendLog(_tangentArcPreferLeft.Value ? "相切弧：切线左侧 (鼓出)" : "相切弧：切线右侧 (鼓出)");
                UpdatePolygonRubberVisual();
                if (IsRingModeActive())
                    UpdateRingRubberVisual();
                return;
            }

            Point end = _arcDraftEnd ?? (_polygonCursorImage ?? start);
            if (Dist(start, end) < 1e-3)
            {
                AppendLog("请先点击圆弧终点");
                return;
            }

            Point mouse = _polygonCursorImage ?? end;
            RoiContourPathTangent.TryGetIncomingLineAnchor(path, out Point lineAnchor);
            if (!RoiContourPathTangent.TryGetIncomingTravelTangent(path, out double tx, out double ty)
                || !RoiContourPathTangent.TryComputeTangentArcBothSides(
                    start, tx, ty, end, mouse, lineAnchor, out _, out _, out bool hasL, out bool hasR))
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

            if (IsTangentJoinEnabled() && _arcDraftEnd == null && path.VertexCount >= 2
                && RoiContourPathTangent.TryBuildTangentArcFromPath(
                    path, cursor, _tangentArcPreferLeft, out Point liveEnd, out Point liveVia))
            {
                foreach (var p in RoiContourPath.SampleArc(start, liveVia, liveEnd, PreviewArcSegments))
                    primary.Add(p);
                return;
            }

            RoiContourPathTangent.TryGetIncomingLineAnchor(path, out Point lineAnchor);
            if (IsTangentJoinEnabled()
                && RoiContourPathTangent.TryGetIncomingTravelTangent(path, out double tx, out double ty)
                && RoiContourPathTangent.TryComputeTangentArcBothSides(
                    start, tx, ty, end, cursor, lineAnchor, out Point viaL, out Point viaR, out bool hasL, out bool hasR)
                && RoiContourPathTangent.TryComputeArcViaTangentAtStart(
                    start, tx, ty, end, cursor, lineAnchor, _tangentArcPreferLeft, out Point viaSel))
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

            return RoiContourPathTangent.TryProjectTangentLineEndAfterArc(path, pick, minStepPx: 2.0, out placed);
        }

        private bool TryCommitTangentArcAtMouse(RoiContourPath path, Point directionMouse, string logPrefix)
        {
            Point junction = path.Vertices[^1];
            if (!RoiContourPathTangent.TryEstimateTangentArcParamsFromMouse(
                    path, directionMouse, _tangentArcPreferLeft, out double initRadiusPx, out double initSweepDeg))
            {
                initRadiusPx = 30;
                initSweepDeg = 45;
            }

            var dlg = new RoiTangentArcParamsDialog
            {
                Owner = Window.GetWindow(this)
            };
            AffineTransform? affineForDialog = null;
#if HALCON_ENABLED
            affineForDialog = TryGetNinePointAffineOptional();
#endif
            dlg.Initialize(junction.X, junction.Y, initRadiusPx, initSweepDeg, affineForDialog);
            if (dlg.ShowDialog() != true)
            {
                AppendLog($"{logPrefix} 已取消圆弧参数输入");
                return false;
            }

            if (!RoiContourPathTangent.TryBuildTangentArcFromRadiusAndSweep(
                    path, directionMouse, dlg.RadiusPx, dlg.SweepDegrees, _tangentArcPreferLeft,
                    out Point end, out Point via))
            {
                AppendLog($"{logPrefix} 圆弧参数无效（半径/张角与相切约束冲突）");
                MessageBox.Show("圆弧参数无效，请调整半径或张角。", "相切圆弧", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            Point arcStart = junction;
            if (IsNearPathStart(path, end)
                && !(IsOpenTrajectoriesModeActive() && ReferenceEquals(path, _roiPath)))
            {
                if (!RoiContourPath.IsValidArcVia(arcStart, via, path.Vertices[0]))
                {
                    AppendLog($"{logPrefix} 闭合弧几乎共线，请调整参数");
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

                CancelArcDraft();
                if (ReferenceEquals(path, _roiPath))
                    FinishPolygonRoiClosed("圆弧相切");
                else
                {
                    AdvanceRingPhaseAfterClose(path);
                    AppendLog($"{logPrefix} 已自动闭合（终点与起点重合）");
                }

                return true;
            }

            if (!RoiContourPath.IsValidArcVia(arcStart, via, end))
            {
                AppendLog($"{logPrefix} 弧几乎共线，请调整鼠标位置");
                return false;
            }

            try
            {
                path.AddArcSegment(end, via);
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            CancelArcDraft();
            AppendLog($"{logPrefix} 圆弧段已添加 R={dlg.RadiusPx:F1}px ∠={dlg.SweepDegrees:F1}°，顶点数 {path.VertexCount}");
            RefreshRoiArcEditUi();
            return ReferenceEquals(path, _roiPath) && _roiPath.IsClosed;
        }

        private bool IsNearPathStart(RoiContourPath path, Point p) =>
            !path.IsClosed && path.VertexCount >= 3
            && Dist(p, path.Vertices[0]) < GetPolygonCloseDistanceImagePx();

        private bool TryClosePathIfCoincidentWithStart(RoiContourPath path, Point end, string logPrefix)
        {
            if (IsOpenTrajectoriesModeActive() && ReferenceEquals(path, _roiPath))
                return false;

            if (!IsNearPathStart(path, end))
                return false;

            if (path.VertexCount < 3)
            {
                MessageBox.Show("多边形至少需要 3 个顶点才能闭合。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            if (ReferenceEquals(path, _roiPath))
                return TryClosePolygonWithLine();

            path.CloseLoop(RoiEdgeKind.Line, null);
            AdvanceRingPhaseAfterClose(path);
            AppendLog($"{logPrefix} 已自动闭合（段终点与起点重合）");
            return true;
        }

        private bool TryGetLineSegmentDirection(
            RoiContourPath path,
            Point directionMouse,
            out double dirX,
            out double dirY,
            out double lengthPx,
            out bool alongTangent)
        {
            dirX = 1;
            dirY = 0;
            lengthPx = 10;
            alongTangent = false;
            if (path.VertexCount == 0)
                return false;

            Point start = path.Vertices[^1];
            if (TryAddTangentLineSegment(path, directionMouse, out Point onRay))
            {
                double dx = onRay.X - start.X;
                double dy = onRay.Y - start.Y;
                double len = Dist(start, onRay);
                if (len >= 1e-6)
                {
                    dirX = dx / len;
                    dirY = dy / len;
                    lengthPx = len;
                    alongTangent = true;
                    return true;
                }
            }

            double vx = directionMouse.X - start.X;
            double vy = directionMouse.Y - start.Y;
            lengthPx = Dist(start, directionMouse);
            if (lengthPx < 1e-6)
                lengthPx = 10;
            else
            {
                dirX = vx / lengthPx;
                dirY = vy / lengthPx;
            }

            return true;
        }

        /// <summary>直线段：鼠标定向后弹窗输入长度；终点落在起点容差内则自动闭合。返回 true 表示路径已闭合。</summary>
        private bool TryCommitLineSegmentAtMouse(RoiContourPath path, Point directionMouse, string logPrefix)
        {
            if (path.VertexCount == 0)
                return false;

            Point start = path.Vertices[^1];
            if (!TryGetLineSegmentDirection(path, directionMouse, out double dirX, out double dirY, out double initLenPx, out bool alongTangent))
                return false;

            var dlg = new RoiLineSegmentParamsDialog
            {
                Owner = Window.GetWindow(this)
            };
            AffineTransform? affineForDialog = null;
#if HALCON_ENABLED
            affineForDialog = TryGetNinePointAffineOptional();
#endif
            dlg.Initialize(start.X, start.Y, dirX, dirY, initLenPx, affineForDialog, alongTangent);
            if (dlg.ShowDialog() != true)
            {
                AppendLog($"{logPrefix} 已取消直线段长度输入");
                return false;
            }

            Point end = new Point(dlg.EndColumnPx, dlg.EndRowPx);

            if (TryClosePathIfCoincidentWithStart(path, end, logPrefix))
                return true;

            path.AddLineSegment(end);
            AppendLog(alongTangent
                ? $"{logPrefix} 直线段(沿弧切线) L={dlg.LengthPx:F1}px ∠={dlg.AngleDegrees:F1}° → ({end.X:F1},{end.Y:F1}) 顶点 {path.VertexCount}"
                : $"{logPrefix} 直线段 L={dlg.LengthPx:F1}px ∠={dlg.AngleDegrees:F1}° → ({end.X:F1},{end.Y:F1}) 顶点 {path.VertexCount}");
            return path.IsClosed;
        }

        private bool TryCommitArcDraftClick(
            RoiContourPath path,
            Point draftEnd,
            Point clickPt,
            string logPrefix,
            bool closingToStart)
        {
            string viaTitle = closingToStart ? "闭合弧经过点" : "弧上经过点";
            if (!TryPromptLandingPoint(clickPt, viaTitle, "可修改弧上经过点的列 X、行 Y。", out Point viaPick))
                return false;

            clickPt = viaPick;
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

        private void CancelArcDraft()
        {
            _arcDraftEnd = null;
            _tangentArcPreferLeft = null;
            HideRubberAltLine();
            UpdateTangentFlipButtonVisibility();
        }

        private void RefreshRoiArcEditUi()
        {
            if (PnlRoiArcEdit == null)
                return;

            _roiArcEditEntries.Clear();
            if (RbPolygonMode?.IsChecked == true && _roiPath.IsClosed && _roiPath.VertexCount >= 3)
                CollectArcEditEntries(_roiPath, "");
            if (IsRingModeActive())
            {
                if (_ringOuterPath.IsClosed && _ringOuterPath.VertexCount >= 3)
                    CollectArcEditEntries(_ringOuterPath, "外圈 ");
                if (_ringInnerPath.IsClosed && _ringInnerPath.VertexCount >= 3)
                    CollectArcEditEntries(_ringInnerPath, "内圈 ");
            }

            bool show = _roiArcEditEntries.Count > 0;
            PnlRoiArcEdit.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show || CmbRoiArcSegment == null)
            {
                if (CmbRoiArcSegment != null)
                {
                    _suppressRoiArcEditUi = true;
                    try
                    {
                        CmbRoiArcSegment.ItemsSource = null;
                        CmbRoiArcSegment.SelectedIndex = -1;
                    }
                    finally
                    {
                        _suppressRoiArcEditUi = false;
                    }
                }

                if (TxtRoiArcRadius != null)
                    TxtRoiArcRadius.Text = "";
                return;
            }

            _suppressRoiArcEditUi = true;
            try
            {
                int keep = CmbRoiArcSegment.SelectedIndex;
                CmbRoiArcSegment.ItemsSource = _roiArcEditEntries.Select(e => e.Label).ToList();
                int pick = keep >= 0 && keep < _roiArcEditEntries.Count ? keep : 0;
                CmbRoiArcSegment.SelectedIndex = pick;
                LoadSelectedRoiArcRadiusToUi();
            }
            finally
            {
                _suppressRoiArcEditUi = false;
            }
        }

        private void CollectArcEditEntries(RoiContourPath path, string prefix)
        {
            if (!TryGetMaxArcEdgeIndex(path, out int maxEdge))
                return;

            for (int i = 0; i <= maxEdge && i < path.EdgeKinds.Count; i++)
            {
                if (path.EdgeKinds[i] != RoiEdgeKind.Arc)
                    continue;
                if (!TryGetArcEdgeGeometry(path, i, out _, out _, out _))
                    continue;

                string label = prefix + $"弧段 #{i + 1}";
#if HALCON_ENABLED
                label = prefix + HalconGeometryPathEditor.FormatSegmentLabel(path, i);
#endif
                _roiArcEditEntries.Add(new RoiArcEditEntry { Path = path, EdgeIndex = i, Label = label });
            }
        }

        /// <summary>闭合：边 0..n-1；开放：边 0..n-2。</summary>
        private static bool TryGetMaxArcEdgeIndex(RoiContourPath path, out int maxEdge)
        {
            maxEdge = -1;
            int n = path.VertexCount;
            if (n < 2)
                return false;
            if (path.IsClosed)
            {
                if (n < 3)
                    return false;
                maxEdge = n - 1;
                return true;
            }

            maxEdge = n - 2;
            return maxEdge >= 0;
        }

        private static bool TryGetArcEdgeGeometry(
            RoiContourPath path,
            int edgeIndex,
            out Point start,
            out Point end,
            out Point? via)
        {
            start = end = default;
            via = null;
            if (edgeIndex < 0 || edgeIndex >= path.EdgeKinds.Count
                || path.EdgeKinds[edgeIndex] != RoiEdgeKind.Arc)
                return false;

            int n = path.VertexCount;
            if (path.IsClosed)
            {
                if (n < 3 || edgeIndex >= n)
                    return false;
                start = path.Vertices[edgeIndex];
                end = path.Vertices[(edgeIndex + 1) % n];
            }
            else
            {
                if (edgeIndex + 1 >= n)
                    return false;
                start = path.Vertices[edgeIndex];
                end = path.Vertices[edgeIndex + 1];
            }

            if (edgeIndex >= path.ArcVia.Count || path.ArcVia[edgeIndex] is not Point v)
                return false;

            via = v;
            return true;
        }

        private RoiArcEditEntry? GetSelectedRoiArcEditEntry()
        {
            if (CmbRoiArcSegment?.SelectedIndex is not int idx || idx < 0 || idx >= _roiArcEditEntries.Count)
                return null;

            var entry = _roiArcEditEntries[idx];
            return TryGetArcEdgeGeometry(entry.Path, entry.EdgeIndex, out _, out _, out _)
                ? entry
                : null;
        }

        private void LoadSelectedRoiArcRadiusToUi()
        {
            if (TxtRoiArcRadius == null)
                return;
            if (GetSelectedRoiArcEditEntry() is not RoiArcEditEntry e
                || !TryGetArcEdgeGeometry(e.Path, e.EdgeIndex, out Point start, out Point end, out Point? via)
                || via is not Point viaPt)
            {
                TxtRoiArcRadius.Text = "";
                return;
            }

            if (RoiContourPath.TryGetArcGeometricRadius(start, viaPt, end, out double r))
                TxtRoiArcRadius.Text = r.ToString("F1", CultureInfo.InvariantCulture);
            else
                TxtRoiArcRadius.Text = "";
        }

        private void RoiArcSegment_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressRoiArcEditUi || !IsLoaded)
                return;

            if (CmbRoiArcSegment?.SelectedIndex is int idx
                && (idx < 0 || idx >= _roiArcEditEntries.Count))
            {
                if (TxtRoiArcRadius != null)
                    TxtRoiArcRadius.Text = "";
                return;
            }

            LoadSelectedRoiArcRadiusToUi();
        }

        private void BtnApplyRoiArcRadius_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedRoiArcEditEntry() is not RoiArcEditEntry entry)
            {
                MessageBox.Show("请先选择要修改的圆弧段。", "圆弧弧度", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!double.TryParse(TxtRoiArcRadius?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double radiusPx)
                || radiusPx < 0.5)
            {
                MessageBox.Show("请输入有效的半径 R（像素）。", "圆弧弧度", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!RoiContourPathTangent.TrySetArcEdgeRadius(entry.Path, entry.EdgeIndex, radiusPx, out string err))
            {
                MessageBox.Show(err, "圆弧弧度", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ReferenceEquals(entry.Path, _roiPath))
            {
                InvalidatePolygonFlatCache();
                UpdatePolygonPreview();
            }
            else
            {
                InvalidateRingFlatCache();
                UpdateRingPreview();
            }

            MirrorGeometryPathFromRoiIfSynced();
            PersistSessionChange();
            LoadSelectedRoiArcRadiusToUi();
            AppendLog($"{entry.Label} 半径已设为 {radiusPx:F1} px");
        }

        private void MirrorGeometryPathFromRoiIfSynced()
        {
#if HALCON_ENABLED
            if (_geometryRoiPath == null || ParseGeometryKind() != HalconMeasuredGeometryKind.RoiPolyline)
                return;
            if (!_roiPath.IsClosed || _roiPath.VertexCount < 3)
                return;

            _geometryRoiPath = HalconGeometryPathEditor.ClonePath(_roiPath);
            RebuildGeometryRoiPolylineFromPath();
            int sel = LstGeomPathSegments?.SelectedIndex ?? -1;
            RefreshGeomPathSegmentList(sel >= 0 ? sel : -1);
#endif
        }

        private void MirrorRoiPolygonFromGeometryPathIfSynced()
        {
#if HALCON_ENABLED
            if (_geometryRoiPath == null || ParseGeometryKind() != HalconMeasuredGeometryKind.RoiPolyline)
                return;
            if (!_roiPath.IsClosed || _roiPath.VertexCount < 3)
                return;

            CopyContourPathContents(_geometryRoiPath, _roiPath);
            InvalidatePolygonFlatCache();
            UpdatePolygonPreview();
#endif
        }

        private static void CopyContourPathContents(RoiContourPath src, RoiContourPath dst)
        {
            dst.IsClosed = src.IsClosed;
            dst.Vertices.Clear();
            foreach (Point p in src.Vertices)
                dst.Vertices.Add(p);
            dst.EdgeKinds.Clear();
            foreach (RoiEdgeKind k in src.EdgeKinds)
                dst.EdgeKinds.Add(k);
            dst.ArcVia.Clear();
            foreach (Point? v in src.ArcVia)
                dst.ArcVia.Add(v);
        }

        private static RoiContourPath CloneContourPath(RoiContourPath src)
        {
            var clone = new RoiContourPath();
            CopyContourPathContents(src, clone);
            return clone;
        }

        private static void TranslateContourPath(RoiContourPath path, double dx, double dy)
        {
            for (int i = 0; i < path.Vertices.Count; i++)
            {
                Point p = path.Vertices[i];
                path.Vertices[i] = new Point(p.X + dx, p.Y + dy);
            }

            for (int i = 0; i < path.ArcVia.Count; i++)
            {
                if (path.ArcVia[i] is Point p)
                    path.ArcVia[i] = new Point(p.X + dx, p.Y + dy);
            }
        }

        private static (double MinX, double MinY, double MaxX, double MaxY) GetPathBounds(RoiContourPath path)
        {
            if (path.VertexCount == 0)
                return (0, 0, 0, 0);

            double minX = path.Vertices[0].X;
            double maxX = minX;
            double minY = path.Vertices[0].Y;
            double maxY = minY;
            foreach (Point p in path.Vertices)
            {
                minX = Math.Min(minX, p.X);
                maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
            }

            return (minX, minY, maxX, maxY);
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
            && Dist(_roiPath.Vertices[0], imgPt) < GetPolygonCloseDistanceImagePx();

        private bool IsNearActiveRingStart(Point imgPt)
        {
            if (_ringPolygonPhase >= 2)
                return false;
            var path = GetActiveRingPath();
            return !path.IsClosed && path.VertexCount >= 3
                && Dist(path.Vertices[0], imgPt) < GetPolygonCloseDistanceImagePx();
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
            RefreshRoiArcEditUi();
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
            {
                if (IsTangentJoinEnabled())
                    return TryClosePolygonWithArc(imgPt);
                return false;
            }

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
                RefreshRoiArcEditUi();
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
            {
                if (IsTangentJoinEnabled())
                    return TryCloseActiveRingWithArc(imgPt);
                return false;
            }
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
                if (IsNextSegmentArc() && !IsTangentJoinEnabled())
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
                    TryAddFirstVertexWithDialog(path, imgPt, $"{label}（圆弧段起点）");
                    return false;
                }

                if (IsTangentJoinEnabled() && path.VertexCount >= 2)
                {
                    if (TryCommitTangentArcAtMouse(path, imgPt, label))
                        return true;
                    return false;
                }

                TrySetArcDraftEndWithDialog(imgPt, label);
                return false;
            }

            if (path.VertexCount == 0)
            {
                TryAddFirstVertexWithDialog(path, imgPt, label);
                return false;
            }

            if (TryCommitLineSegmentAtMouse(path, imgPt, label))
                return true;

            return false;
        }

        private void BtnClosePolygon_Click(object sender, RoutedEventArgs e)
        {
            if (IsOpenTrajectoriesModeActive())
            {
                AppendLog("开放轨迹不闭合；请用「完成本条」保存当前轨迹，「新建本条」开始下一条。");
                return;
            }

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
            _rotRectSettingAngle = false;
            _rotRectWidth = _rotRectHeight = 0;
            _roiPath.Clear();
            CancelArcDraft();
            _polygonCursorImage = null;
            InvalidatePolygonFlatCache();
            ResetCircleDrawState();
            ResetRingDrawState();
            ResetOpenTrajectoriesDrawState();

            try
            {
                if (RoiRect != null) RoiRect.Visibility = Visibility.Collapsed;
                if (RoiRotatedRectLine != null)
                {
                    RoiRotatedRectLine.Visibility = Visibility.Collapsed;
                    RoiRotatedRectLine.Points.Clear();
                }
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
            ClearModelCreateMeta();
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
            UpdateCreatePanelsVisibility();
            if (_imgWidth > 0 && IsPrimitiveRoiDrawModeActive())
                TryPromptPrimitiveRoiParams(GetImageCenterPoint());
        }

        private void UpdateHandDrawnEdgeGradientSubPanels(bool showPanel)
        {
            bool showRingInner = showPanel && IsRingModeActive();
            if (PnlRingInnerGradientSettings != null)
                PnlRingInnerGradientSettings.Visibility = showRingInner ? Visibility.Visible : Visibility.Collapsed;
            if (!showRingInner && PnlRingInnerGradientCustom != null)
                PnlRingInnerGradientCustom.Visibility = Visibility.Collapsed;
            else if (showRingInner)
                RingInnerGradientMode_Changed(CmbRingInnerGradientMode!, null!);
        }

        private void UpdateDrawToolbarForMode()
        {
            bool open = IsOpenTrajectoriesModeActive();
            if (BtnFinishOpenTrajectory != null)
                BtnFinishOpenTrajectory.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (BtnNewOpenTrajectory != null)
                BtnNewOpenTrajectory.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (BtnClosePolygon != null)
                BtnClosePolygon.Visibility = open ? Visibility.Collapsed
                    : RbPolygonMode?.IsChecked == true || IsRingModeActive()
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            if (BtnPrimitiveRoiParams != null)
                BtnPrimitiveRoiParams.Visibility = !open && IsPrimitiveRoiDrawModeActive()
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (!open && IsNextSegmentConnector() && RbNextSegmentLine != null)
                RbNextSegmentLine.IsChecked = true;

            UpdateNextSegmentToolbar();
            UpdateRoiManagePanelVisibility();
        }

        // ───────── 画布鼠标事件 ─────────
        private void DrawToolbar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (ViewHost.IsMouseCaptured)
                ViewHost.ReleaseMouseCapture();
        }

        private bool IsPointInRectRoi(double x, double y) =>
            _hasRoi && RbRectMode?.IsChecked == true
            && _roiRectImage.Width > 5 && _roiRectImage.Height > 5
            && _roiRectImage.Contains(new Point(x, y));

        private bool IsPointInActiveRoi(double x, double y)
        {
            if (IsPointInRectRoi(x, y))
                return true;
            if (HasUsableRotatedRectRoi() && IsPointInRotatedRectRoi(x, y))
                return true;
            if (HasUsableCircleRoi() && IsPointInCircleRoi(x, y))
                return true;
            if (RbPolygonMode?.IsChecked == true && _hasRoi && _roiPath.IsClosed && _roiPath.VertexCount >= 3
                && IsPointInPolygon(x, y, GetPolygonFlattened(forHitTest: true)))
                return true;
            if (HasUsableRingRoi() && IsPointInRingRoi(x, y))
                return true;
            return false;
        }

        private bool CanBeginRoiDragNow()
        {
            if (_isPanning || _isMovingRoi || _isDrawing || _rotRectSettingAngle)
                return false;
            if (IsOpenTrajectoriesModeActive())
                return false;
            if (RbPolygonMode?.IsChecked == true && !_hasRoi && _roiPath.VertexCount > 0)
                return false;
            if (IsRingModeActive() && _ringPolygonPhase < 2)
                return false;
            return true;
        }

        private bool TryBeginRoiDrag(Point imgPt)
        {
            if (!CanBeginRoiDragNow() || !IsPointInActiveRoi(imgPt.X, imgPt.Y))
                return false;

            _roiDragSnapshot = CaptureRoiDragSnapshot(imgPt);
            _roiDragKind = _roiDragSnapshot.Kind;
            if (_roiDragKind == RoiDragKind.None)
                return false;

            _roiDragStartMouse = imgPt;
            _isMovingRoi = true;
            _roiDragDeferThresholdPreview = true;
            CancelFindShapeRun("已取消进行中的匹配（正在移动 ROI）");
            ViewHost.CaptureMouse();
            ViewHost.Cursor = Cursors.SizeAll;
            return true;
        }

        private RoiDragSnapshot CaptureRoiDragSnapshot(Point imgPt)
        {
            var snap = new RoiDragSnapshot();
            if (IsPointInRectRoi(imgPt.X, imgPt.Y))
            {
                snap.Kind = RoiDragKind.Rect;
                snap.Rect = _roiRectImage;
                return snap;
            }

            if (HasUsableRotatedRectRoi() && IsPointInRotatedRectRoi(imgPt.X, imgPt.Y))
            {
                snap.Kind = RoiDragKind.RotatedRect;
                snap.RotCenterX = _rotRectCenterX;
                snap.RotCenterY = _rotRectCenterY;
                snap.RotWidth = _rotRectWidth;
                snap.RotHeight = _rotRectHeight;
                snap.RotAngleDeg = _rotRectAngleDeg;
                return snap;
            }

            if (HasUsableCircleRoi() && IsPointInCircleRoi(imgPt.X, imgPt.Y))
            {
                snap.Kind = RoiDragKind.Circle;
                snap.CircleCenter = _circleCenterImage;
                snap.CircleRadius = _circleRadiusImage;
                return snap;
            }

            if (RbPolygonMode?.IsChecked == true && _hasRoi && _roiPath.IsClosed && _roiPath.VertexCount >= 3
                && IsPointInPolygon(imgPt.X, imgPt.Y, GetPolygonFlattened(forHitTest: true)))
            {
                snap.Kind = RoiDragKind.Polygon;
                snap.Polygon = CloneContourPath(_roiPath);
                return snap;
            }

            if (HasUsableRingRoi() && IsPointInRingRoi(imgPt.X, imgPt.Y))
            {
                snap.Kind = RoiDragKind.Ring;
                snap.RingOuter = CloneContourPath(_ringOuterPath);
                snap.RingInner = CloneContourPath(_ringInnerPath);
            }

            return snap;
        }

        private void ApplyRoiDrag(Vector delta)
        {
            switch (_roiDragKind)
            {
                case RoiDragKind.Rect:
                {
                    double x = _roiDragSnapshot.Rect.X + delta.X;
                    double y = _roiDragSnapshot.Rect.Y + delta.Y;
                    double w = _roiDragSnapshot.Rect.Width;
                    double h = _roiDragSnapshot.Rect.Height;
                    ClampRoiToImage(ref x, ref y, ref w, ref h);
                    _roiRectImage = new Rect(x, y, w, h);
                    _hasRoi = true;
                    break;
                }
                case RoiDragKind.RotatedRect:
                    _rotRectCenterX = _roiDragSnapshot.RotCenterX + delta.X;
                    _rotRectCenterY = _roiDragSnapshot.RotCenterY + delta.Y;
                    _rotRectWidth = _roiDragSnapshot.RotWidth;
                    _rotRectHeight = _roiDragSnapshot.RotHeight;
                    _rotRectAngleDeg = _roiDragSnapshot.RotAngleDeg;
                    _hasRoi = true;
                    break;
                case RoiDragKind.Circle:
                    _circleCenterImage = new Point(
                        _roiDragSnapshot.CircleCenter.X + delta.X,
                        _roiDragSnapshot.CircleCenter.Y + delta.Y);
                    _circleRadiusImage = _roiDragSnapshot.CircleRadius;
                    ClampCircleRadiusToImage(clampCenter: false);
                    _hasRoi = _roiDragSnapshot.CircleRadius > 5;
                    break;
                case RoiDragKind.Polygon:
                    if (_roiDragSnapshot.Polygon != null)
                    {
                        CopyContourPathContents(_roiDragSnapshot.Polygon, _roiPath);
                        TranslateContourPath(_roiPath, delta.X, delta.Y);
                        InvalidatePolygonFlatCache();
                        RebuildCommittedPolygonVisual();
                    }
                    break;
                case RoiDragKind.Ring:
                    if (_roiDragSnapshot.RingOuter != null && _roiDragSnapshot.RingInner != null)
                    {
                        CopyContourPathContents(_roiDragSnapshot.RingOuter, _ringOuterPath);
                        CopyContourPathContents(_roiDragSnapshot.RingInner, _ringInnerPath);
                        TranslateContourPath(_ringOuterPath, delta.X, delta.Y);
                        TranslateContourPath(_ringInnerPath, delta.X, delta.Y);
                        InvalidateRingFlatCache();
                    }
                    break;
            }

            RefreshRoiVisuals();
            if (!_roiDragDeferThresholdPreview)
                UpdateThresholdPreview();
        }

        private Vector ClampRoiDragDelta(Vector delta)
        {
            if (_imgWidth <= 0 || _imgHeight <= 0)
                return delta;

            return _roiDragKind switch
            {
                RoiDragKind.Rect => ClampDeltaForRect(delta, _roiDragSnapshot.Rect),
                RoiDragKind.RotatedRect => ClampDeltaForCenter(delta, _roiDragSnapshot.RotCenterX, _roiDragSnapshot.RotCenterY),
                RoiDragKind.Circle => ClampDeltaForCircle(delta, _roiDragSnapshot),
                RoiDragKind.Polygon when _roiDragSnapshot.Polygon != null
                    => ClampDeltaForPath(delta, _roiDragSnapshot.Polygon),
                RoiDragKind.Ring when _roiDragSnapshot.RingOuter != null
                    => ClampDeltaForPath(delta, _roiDragSnapshot.RingOuter),
                _ => delta
            };
        }

        private Vector ClampDeltaForRect(Vector delta, Rect rect)
        {
            double x = rect.X + delta.X;
            double y = rect.Y + delta.Y;
            x = Math.Max(0, Math.Min(x, _imgWidth - rect.Width));
            y = Math.Max(0, Math.Min(y, _imgHeight - rect.Height));
            return new Vector(x - rect.X, y - rect.Y);
        }

        private Vector ClampDeltaForCenter(Vector delta, double centerX, double centerY)
        {
            double cx = Math.Max(0, Math.Min(centerX + delta.X, _imgWidth - 1));
            double cy = Math.Max(0, Math.Min(centerY + delta.Y, _imgHeight - 1));
            return new Vector(cx - centerX, cy - centerY);
        }

        private Vector ClampDeltaForCircle(Vector delta, RoiDragSnapshot snap)
        {
            double r = snap.CircleRadius;
            double cx = snap.CircleCenter.X + delta.X;
            double cy = snap.CircleCenter.Y + delta.Y;
            cx = Math.Max(r, Math.Min(cx, _imgWidth - 1 - r));
            cy = Math.Max(r, Math.Min(cy, _imgHeight - 1 - r));
            return new Vector(cx - snap.CircleCenter.X, cy - snap.CircleCenter.Y);
        }

        private static Vector ClampDeltaForPath(Vector delta, RoiContourPath path, int imgWidth, int imgHeight)
        {
            (double minX, double minY, double maxX, double maxY) = GetPathBounds(path);
            double dx = delta.X;
            double dy = delta.Y;
            if (minX + dx < 0)
                dx -= minX + dx;
            if (maxX + dx > imgWidth - 1)
                dx -= maxX + dx - (imgWidth - 1);
            if (minY + dy < 0)
                dy -= minY + dy;
            if (maxY + dy > imgHeight - 1)
                dy -= maxY + dy - (imgHeight - 1);
            return new Vector(dx, dy);
        }

        private Vector ClampDeltaForPath(Vector delta, RoiContourPath path) =>
            ClampDeltaForPath(delta, path, _imgWidth, _imgHeight);

        private void FinishRoiDrag()
        {
            if (!_isMovingRoi)
                return;

            _isMovingRoi = false;
            _roiDragKind = RoiDragKind.None;
            _roiDragDeferThresholdPreview = false;
            ViewHost.Cursor = Cursors.Arrow;
            UpdateThresholdPreview();
            PersistSessionChange();
#if HALCON_ENABLED
            try
            {
                TryAutoSyncGeometryFromRoiIfNeeded();
            }
            catch (Exception ex)
            {
                AppendLog($"[ROI] 几何同步失败: {ex.Message}");
            }
#endif
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_imgWidth <= 0 || _imgHeight <= 0) return;

            var imgPt = GetImagePointFromMouse(e);

            if (TryBeginRoiDrag(imgPt))
            {
                e.Handled = true;
                return;
            }

            if (RbRectMode.IsChecked == true)
            {
                TryPromptPrimitiveRoiParams(imgPt);
                return;
            }

            if (IsRotatedRectModeActive())
            {
                if (_rotRectSettingAngle)
                {
                    CommitRotatedRectFromAnglePick(imgPt);
                    ViewHost.ReleaseMouseCapture();
                    return;
                }

                TryPromptPrimitiveRoiParams(imgPt);
                return;
            }

            if (IsCircleModeActive())
            {
                TryPromptPrimitiveRoiParams(imgPt);
                return;
            }
            else if (RbPolygonMode.IsChecked == true)
            {
                if (HandlePolygonClick(imgPt))
                    return;

                RebuildCommittedPolygonVisual();
                UpdatePolygonRubberVisual();
                if (_hasRoi)
                    UpdateThresholdPreview();
            }
            else if (IsOpenTrajectoriesModeActive())
            {
                HandleOpenTrajectoryClick(imgPt);
                RebuildCommittedPolygonVisual();
                RebuildOpenTrajectoriesVisual();
                RebuildConnectorsVisual();
                UpdatePolygonRubberVisual();
                UpdateConnectorRubberVisual();
            }
            else if (IsRingModeActive())
            {
                bool closedStep = HandleRingContourClick(imgPt);
                UpdateRingPreview();
                UpdateRingRubberVisual();
                if (closedStep && _ringPolygonPhase >= 2)
                    UpdateThresholdPreview();
            }
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
                if (IsNextSegmentArc() && !IsTangentJoinEnabled())
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
                    TryAddFirstVertexWithDialog(_roiPath, imgPt, "多边形（圆弧段起点）");
                    return false;
                }

                if (IsTangentJoinEnabled() && _roiPath.VertexCount >= 2)
                {
                    if (TryCommitTangentArcAtMouse(_roiPath, imgPt, "多边形"))
                        return true;
                    return false;
                }

                TrySetArcDraftEndWithDialog(imgPt, "多边形");
                return false;
            }

            if (_roiPath.VertexCount == 0)
            {
                TryAddFirstVertexWithDialog(_roiPath, imgPt, "多边形");
                return false;
            }

            if (TryCommitLineSegmentAtMouse(_roiPath, imgPt, "多边形"))
                return true;

            return false;
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isMovingRoi)
            {
                var imgPt = GetImagePointFromMouse(e);
                Vector delta = imgPt - _roiDragStartMouse;
                delta = ClampRoiDragDelta(delta);
                ApplyRoiDrag(delta);
                return;
            }

            if (_isPanning)
            {
                var current = e.GetPosition(ViewHost);
                _offsetX = _lastPanOffset.X + (current.X - _panStart.X);
                _offsetY = _lastPanOffset.Y + (current.Y - _panStart.Y);
                ApplyTransform();
                return;
            }

            if ((RbPolygonMode.IsChecked == true && !_hasRoi
                 || IsOpenTrajectoriesModeActive())
                && (_roiPath.VertexCount > 0 || _arcDraftEnd != null
                    || IsNextSegmentConnector() || _connectorDraftStart != null))
            {
                long now = Environment.TickCount64;
                if (now - _lastPolygonPreviewMoveMs < PolygonPreviewMoveIntervalMs)
                    return;
                _lastPolygonPreviewMoveMs = now;

                _polygonCursorImage = GetImagePointFromMouse(e);
                UpdatePolygonRubberVisual();
                UpdateConnectorRubberVisual();
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

            if (!_isMovingRoi && !_isPanning && !_isDrawing && CanBeginRoiDragNow())
            {
                var hoverPt = GetImagePointFromMouse(e);
                ViewHost.Cursor = IsPointInActiveRoi(hoverPt.X, hoverPt.Y) ? Cursors.SizeAll : Cursors.Arrow;
            }

            if (_rotRectSettingAngle)
            {
                var curAngle = GetImagePointFromMouse(e);
                _rotRectAngleDeg = Math.Atan2(
                    curAngle.Y - _rotRectCenterY,
                    curAngle.X - _rotRectCenterX) * 180.0 / Math.PI;
                UpdateRotatedRectPreview();
                return;
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

            if (RbRectMode.IsChecked != true && !IsRotatedRectModeActive())
                return;

            double ix = Math.Min(_drawStartImage.X, cur.X);
            double iy = Math.Min(_drawStartImage.Y, cur.Y);
            double iw = Math.Abs(cur.X - _drawStartImage.X);
            double ih = Math.Abs(cur.Y - _drawStartImage.Y);
            ClampRoiToImage(ref ix, ref iy, ref iw, ref ih);

            if (IsRotatedRectModeActive())
            {
                _rotRectCenterX = ix + iw * 0.5;
                _rotRectCenterY = iy + ih * 0.5;
                _rotRectWidth = iw;
                _rotRectHeight = ih;
                _rotRectAngleDeg = Math.Atan2(cur.Y - _rotRectCenterY, cur.X - _rotRectCenterX) * 180.0 / Math.PI;
                UpdateRotatedRectPreview();
                return;
            }

            if (RoiRect != null)
            {
                Canvas.SetLeft(RoiRect, ix);
                Canvas.SetTop(RoiRect, iy);
                RoiRect.Width = iw;
                RoiRect.Height = ih;
            }
        }

        private void CommitRotatedRectFromAnglePick(Point imgPt)
        {
            _rotRectAngleDeg = Math.Atan2(
                imgPt.Y - _rotRectCenterY,
                imgPt.X - _rotRectCenterX) * 180.0 / Math.PI;
            _rotRectSettingAngle = false;
            _hasRoi = true;
            RefreshRoiVisuals();
            UpdateThresholdPreview();
            AppendLog($"旋转矩形 ROI: 中心=({_rotRectCenterX:F0},{_rotRectCenterY:F0}) {_rotRectWidth:F0}×{_rotRectHeight:F0}° {_rotRectAngleDeg:F1}");
            PersistSessionChange();
#if HALCON_ENABLED
            TryAutoSyncGeometryFromRoiIfNeeded();
#endif
        }

        private bool TryBeginRotatedRectAnglePhase(double x, double y, double w, double h)
        {
            if (!IsRotatedRectModeActive() || w <= 5 || h <= 5)
                return false;

            _rotRectCenterX = x + w * 0.5;
            _rotRectCenterY = y + h * 0.5;
            _rotRectWidth = w;
            _rotRectHeight = h;
            _rotRectSettingAngle = true;
            if (RoiRect != null)
                RoiRect.Visibility = Visibility.Collapsed;
            UpdateRotatedRectPreview();
            AppendLog("旋转矩形: 移动鼠标确定角度，单击确认");
            return true;
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isMovingRoi)
            {
                FinishRoiDrag();
                ViewHost.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

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

            if (RbRectMode.IsChecked == true || IsRotatedRectModeActive())
            {
                double x = Math.Min(_drawStartImage.X, cur.X);
                double y = Math.Min(_drawStartImage.Y, cur.Y);
                double w = Math.Abs(cur.X - _drawStartImage.X);
                double h = Math.Abs(cur.Y - _drawStartImage.Y);
                ClampRoiToImage(ref x, ref y, ref w, ref h);

                if (TryBeginRotatedRectAnglePhase(x, y, w, h))
                    return;

                if (w > 5 && h > 5 && RbRectMode.IsChecked == true)
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

        private void ClampCircleRadiusToImage(bool clampCenter = true)
        {
            if (_imgWidth <= 0 || _imgHeight <= 0 || _circleRadiusImage <= 0)
                return;

            if (clampCenter)
                _circleCenterImage = ClampImagePoint(_circleCenterImage);

            double maxR = Math.Min(
                Math.Min(_circleCenterImage.X, _imgWidth - 1 - _circleCenterImage.X),
                Math.Min(_circleCenterImage.Y, _imgHeight - 1 - _circleCenterImage.Y));
            if (maxR < 0)
                maxR = 0;
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

            Point mouseImage = GetImagePointFromMouse(e);
            double factor = e.Delta > 0 ? 1.1 : (1.0 / 1.1);
            double newScale = Math.Max(MinScale, Math.Min(MaxScale, _scale * factor));
            if (Math.Abs(newScale - _scale) < 0.001) return;

            double parentX = newScale * mouseImage.X + _offsetX;
            double parentY = newScale * mouseImage.Y + _offsetY;
            _scale = newScale;
            _offsetX = parentX - newScale * mouseImage.X;
            _offsetY = parentY - newScale * mouseImage.Y;

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
            RefreshRoiArcEditUi();
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

            if (IsOpenTrajectoriesModeActive())
            {
                if (_connectorDraftStart != null)
                {
                    _connectorDraftStart = null;
                    UpdateConnectorRubberVisual();
                    AppendLog("已取消未完成的连接线");
                    return;
                }

                if (_trajectoryConnectors.Count > 0)
                {
                    RoiConnectorSegment removed = _trajectoryConnectors[^1];
                    _trajectoryConnectors.RemoveAt(_trajectoryConnectors.Count - 1);
                    if (_roiPath.VertexCount == 1 && Dist(_roiPath.Vertices[0], removed.End) < 1.0)
                    {
                        _roiPath.Clear();
                        InvalidatePolygonFlatCache();
                        UpdatePolygonPreview();
                    }

                    RebuildConnectorsVisual();
                    UpdateConnectorRubberVisual();
                    AppendLog($"连接线撤销: 剩余 {_trajectoryConnectors.Count} 条");
                    PersistSessionChange();
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
                    UpdatePolygonPreview();
                    AppendLog($"开放轨迹撤销: 当前条剩余 {_roiPath.VertexCount} 个顶点");
                    return;
                }

                if (_openTrajectoryEntries.Count > 0)
                {
                    string removed = _openTrajectoryEntries[^1].Name;
                    _openTrajectoryEntries.RemoveAt(_openTrajectoryEntries.Count - 1);
                    _hasOpenTrajectoriesRoi = _openTrajectoryEntries.Count > 0;
                    _hasRoi = _hasOpenTrajectoriesRoi || _roiPath.VertexCount >= 2;
                    RebuildOpenTrajectoriesVisual();
                    RefreshOpenTrajectoryListUi();
                    AppendLog($"开放轨迹撤销: 已删除「{removed}」，剩余 {_openTrajectoryEntries.Count} 条");
                    PersistSessionChange();
                }

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
            bool useRotated = HasUsableRotatedRectRoi();
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
                    else if (useRotated)
                    {
                        if (!IsPointInRotatedRectRoi(x + 0.5, y + 0.5))
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
                if (useRotated)
                    return IsPointInRotatedRectRoi(x + 0.5, y + 0.5);
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
            AppendLog(useRect || useRotated || useCircle || usePoly || useRing
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
            if (ThresholdOverlayImage == null || _grayPixels == null || _imgWidth <= 0 || _imgHeight <= 0)
            {
                if (ThresholdOverlayImage != null)
                    ThresholdOverlayImage.Visibility = Visibility.Collapsed;
                return;
            }

            int expectedGrayBytes = _imgWidth * _imgHeight;
            if (_grayPixels.Length < expectedGrayBytes)
            {
                ThresholdOverlayImage.Visibility = Visibility.Collapsed;
                return;
            }

            if (!IsGrayThresholdTemplateSource(
                    (CmbTemplateSource?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ThresholdXld"))
            {
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
            if (drawingOpenPoly || drawingOpenRing || _rotRectSettingAngle)
            {
                ThresholdOverlayImage.Visibility = Visibility.Collapsed;
                return;
            }

            int w = _imgWidth;
            int h = _imgHeight;
            int x0 = 0, y0 = 0, x1 = w, y1 = h;
            bool useRect = _hasRoi && RbRectMode?.IsChecked == true;
            bool useRotated = HasUsableRotatedRectRoi();
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
            else if (useRotated)
            {
                var rotCorners = GetRotatedRectCornerPoints(
                    _rotRectCenterX, _rotRectCenterY, _rotRectWidth, _rotRectHeight, _rotRectAngleDeg);
                if (TryGetPolygonBounds(rotCorners, out Rect rotBounds))
                {
                    x0 = Math.Max(0, (int)Math.Floor(rotBounds.X));
                    y0 = Math.Max(0, (int)Math.Floor(rotBounds.Y));
                    x1 = Math.Min(w, (int)Math.Ceiling(rotBounds.Right));
                    y1 = Math.Min(h, (int)Math.Ceiling(rotBounds.Bottom));
                }
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
                    if (useRotated && !IsPointInRotatedRectRoi(x + 0.5, y + 0.5))
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
            UpdateThresholdPreview();
#if HALCON_ENABLED
            if ((CmbTemplateSource?.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "GeometryXld")
                TryAutoSyncGeometryFromRoiIfNeeded();
#endif
        }

        private static bool IsGrayThresholdTemplateSource(string src) =>
            string.Equals(src, "ThresholdXld", StringComparison.Ordinal);

        private static bool UsesContourExtractSettings(string src) =>
            src is "ThresholdXld" or "EdgesXld" or "PolygonXld";

        private static bool UsesGenContourModeSetting(string src) =>
            string.Equals(src, "ThresholdXld", StringComparison.Ordinal);

        private static bool UsesContourTrimWorkflow(string src) =>
            src is "ThresholdXld" or "EdgesXld" or "PolygonXld" or "GeometryXld";

        private bool UsesHandDrawnEdgeGradient(string src) =>
            src == "PolygonXld" && !IsOpenTrajectoriesModeActive();

        private void UpdateCreatePanelsVisibility()
        {
            if (CmbTemplateSource == null) return;

            string src = (CmbTemplateSource.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ThresholdXld";
            string kind = (CmbModelKind?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Shape";

            bool isGeometry = src == "GeometryXld";
            bool isImage = src is "ImageRectangle" or "ImagePolygon";
            bool showGrayThreshold = IsGrayThresholdTemplateSource(src);
            bool showPolygonClose = !isGeometry && src is not "ImageRectangle";

            if (PnlGrayThreshold != null)
                PnlGrayThreshold.Visibility = showGrayThreshold ? Visibility.Visible : Visibility.Collapsed;
            if (PnlPolygonCloseSettings != null)
                PnlPolygonCloseSettings.Visibility = showPolygonClose ? Visibility.Visible : Visibility.Collapsed;
            if (PnlThreshold != null)
                PnlThreshold.Visibility = showGrayThreshold || showPolygonClose
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (TxtThresholdPanelTitle != null)
            {
                TxtThresholdPanelTitle.Text = showGrayThreshold && showPolygonClose
                    ? "阈值与闭合"
                    : showGrayThreshold
                        ? "灰度阈值"
                        : "多边形闭合";
            }

            if (PnlGeometryMeasure != null)
                PnlGeometryMeasure.Visibility = isGeometry ? Visibility.Visible : Visibility.Collapsed;
            bool showContourExtract = UsesContourExtractSettings(src);
            bool showGenContourMode = UsesGenContourModeSetting(src);
            bool showContourTrim = UsesContourTrimWorkflow(src);
            if (PnlContourExtract != null)
                PnlContourExtract.Visibility = showContourExtract ? Visibility.Visible : Visibility.Collapsed;
            if (PnlGenContourMode != null)
                PnlGenContourMode.Visibility = showGenContourMode ? Visibility.Visible : Visibility.Collapsed;
            if (PnlContourTrim != null)
                PnlContourTrim.Visibility = showContourTrim ? Visibility.Visible : Visibility.Collapsed;
            if (PnlEdgeExtract != null)
                PnlEdgeExtract.Visibility = src == "EdgesXld" ? Visibility.Visible : Visibility.Collapsed;
            if (PnlRoiBoundaryDir != null)
            {
                bool showRoiDir = UsesHandDrawnEdgeGradient(src);
                PnlRoiBoundaryDir.Visibility = showRoiDir ? Visibility.Visible : Visibility.Collapsed;
                UpdateHandDrawnEdgeGradientSubPanels(showRoiDir);
            }

            UpdateDrawToolbarForMode();
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
                        ? "选中圆弧段可改半径 R 或弧上点；应用本段后同步 ROI 与 mm 摘要"
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
            RefreshGeomPathSegmentList();
            RefreshRoiArcEditUi();
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

            if (kind == RoiEdgeKind.Arc
                && HalconGeometryPathEditor.TryGetSegmentArcRadius(_geometryRoiPath, edgeIndex, out double radiusPx))
                SetGeomText(TxtGeomPathArcRadius, radiusPx);
            else if (TxtGeomPathArcRadius != null)
                TxtGeomPathArcRadius.Text = "";

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
            bool arc = ParsePathSegKind() == RoiEdgeKind.Arc;
            if (RowGeomPathVia != null)
                RowGeomPathVia.Visibility = arc ? Visibility.Visible : Visibility.Collapsed;
            if (RowGeomPathArcRadius != null)
                RowGeomPathArcRadius.Visibility = arc ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ClearGeomPathSegmentFields()
        {
            foreach (string name in new[]
                     {
                         "TxtGeomPathStartCol", "TxtGeomPathStartRow", "TxtGeomPathEndCol", "TxtGeomPathEndRow",
                         "TxtGeomPathViaCol", "TxtGeomPathViaRow", "TxtGeomPathArcRadius"
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
            if (kind == RoiEdgeKind.Arc
                && double.TryParse(TxtGeomPathArcRadius?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double radiusPx)
                && HalconGeometryPathEditor.TrySetSegmentArcRadius(_geometryRoiPath, edgeIndex, radiusPx, out string radiusErr))
            {
                RebuildGeometryRoiPolylineFromPath();
                MirrorRoiPolygonFromGeometryPathIfSynced();
                RefreshGeomPathSegmentList(edgeIndex);
                RefreshRoiArcEditUi();
                if (TryReadGeometryParamsFromUi(out HalconMeasuredGeometryParams pRadius))
                    RefreshMeasureSummaryText(pRadius);
                ApplyGeometryContourFromUi(logSuccess: false);
                AppendLog($"路径段 #{edgeIndex + 1} 圆弧半径已设为 {radiusPx:F1} px");
                return;
            }

            if (!HalconGeometryPathEditor.TryUpdateSegment(
                    _geometryRoiPath, edgeIndex, kind, sc, sr, ec, er, vc, vr, out string err))
            {
                MessageBox.Show(err, "路径段", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RebuildGeometryRoiPolylineFromPath();
            MirrorRoiPolygonFromGeometryPathIfSynced();
            RefreshGeomPathSegmentList(edgeIndex);
            RefreshRoiArcEditUi();
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

            if (HasUsableRotatedRectRoi())
            {
                WriteGeometryParamsToUi(new HalconMeasuredGeometryParams
                {
                    Kind = HalconMeasuredGeometryKind.RotatedRectangle,
                    CenterCol = _rotRectCenterX,
                    CenterRow = _rotRectCenterY,
                    WidthPx = _rotRectWidth,
                    HeightPx = _rotRectHeight,
                    AngleDeg = _rotRectAngleDeg
                });
                _geometryRoiPath = null;
                _geometryRoiPolylineContour = null;
                message = $"已从旋转矩形 ROI 同步: {_rotRectWidth:F0}×{_rotRectHeight:F0}° {_rotRectAngleDeg:F1}";
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

            message = "请先绘制矩形、旋转矩形、圆、闭合多边形(含弧)或环形 ROI";
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

            if (HasUsableRotatedRectRoi())
            {
                return HalconFlowBridge.GenRegionRectangle2(
                    _rotRectCenterY,
                    _rotRectCenterX,
                    _rotRectAngleDeg,
                    _rotRectWidth * 0.5,
                    _rotRectHeight * 0.5);
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
                if (HasUsableRotatedRectRoi())
                {
                    return HalconFlowBridge.GenRegionRectangle2(
                        _rotRectCenterY,
                        _rotRectCenterX,
                        _rotRectAngleDeg,
                        _rotRectWidth * 0.5,
                        _rotRectHeight * 0.5);
                }

                if (!_hasRoi || RbRectMode.IsChecked != true)
                    throw new InvalidOperationException("矩形灰度模板：请用「矩形」「旋转矩形」「圆形」或「环形」模式绘制 ROI。");
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

            if (HasUsableRotatedRectRoi())
            {
                List<Point> corners = GetRotatedRectCornerPoints(
                    _rotRectCenterX, _rotRectCenterY, _rotRectWidth, _rotRectHeight, _rotRectAngleDeg);
                int n = corners.Count > 0 && Dist(corners[0], corners[^1]) < 1e-3
                    ? corners.Count - 1
                    : corners.Count;
                var pts = new Point2D[n];
                for (int i = 0; i < n; i++)
                    pts[i] = new Point2D(corners[i].X, corners[i].Y);
                return pts;
            }

            if (_roiPath.VertexCount >= 3)
            {
                return GetPolygonFlattened(forHitTest: true)
                    .Select(p => new Point2D(p.X, p.Y))
                    .ToList();
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
            if (PnlRingInnerGradientSettings?.Visibility != Visibility.Visible)
            {
                PnlRingInnerGradientCustom.Visibility = Visibility.Collapsed;
                return;
            }
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
            else if (IsOpenTrajectoriesModeActive())
            {
                if (!HasUsableOpenTrajectoriesForTemplate())
                    throw new InvalidOperationException(
                        "开放轨迹模板：请至少完成一条轨迹（≥2 点），可用「完成本条」保存；当前草稿也会一并计入。");
            }
            else if (RbPolygonMode?.IsChecked == true && !_hasRoi)
            {
                throw new InvalidOperationException("多边形 ROI 请先单击起点闭合。");
            }

            DisposeNativeXldForCreate();

            if (IsOpenTrajectoriesModeActive())
            {
                List<List<Point2D>> trajectories = CollectOpenTrajectoriesForTemplate();
                int iw = _currentImage?.Width > 0 ? _currentImage.Width : _imgWidth;
                int ih = _currentImage?.Height > 0 ? _currentImage.Height : _imgHeight;
                var (bundle, native) = HalconFlowBridge.BuildMultiOpenTrajectoriesXldFromGeometry(
                    iw,
                    ih,
                    trajectories);
                _nativeXldForCreate = native;
                int pts = bundle.Contours?.Sum(c => c?.Length ?? 0) ?? 0;
                AppendLog($"开放轨迹几何模板: {trajectories.Count} 条轨迹，{bundle.ContourCount} 条轮廓、共 {pts} 点（直接由轨迹生成，Metric 自动为 ignore_local_polarity）");
                return bundle;
            }

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
                {
                    int filterMinPts = IsOpenTrajectoriesModeActive()
                        ? Math.Min(minPts, 5)
                        : minPts;
                    bundle = HalconFlowBridge.FilterXldBundle(bundle, filterMinPts, opt.LargestContourOnly);
                }

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
            CalibImage? imageForCreate = null;
            HObject? regionForCreate = null;
            HObject? nativeXldForCreate = null;
            try
            {
                var opt = ReadCreateOptionsFromUi();
                if (!double.TryParse(TxtMinGray.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minGray)) minGray = 0;
                if (!double.TryParse(TxtMaxGray.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double maxGray)) maxGray = 255;

                AppendLog($"创建模式: {opt.SourceKind} / {opt.ModelKind}（epoch={_modelEpoch}）");
                imageForCreate = DuplicateCurrentImageForHalcon();

                bool fromImage = opt.SourceKind is HalconShapeModelSourceKind.ImageRectangle
                    or HalconShapeModelSourceKind.ImagePolygon;

                HalconXldContourBundle? xldBundle = null;

                if (fromImage)
                {
                    using HObject regionBuilt = BuildRequiredRegionForImageMode(opt.SourceKind);
                    regionForCreate = HalconFlowBridge.CloneHObject(regionBuilt)
                        ?? throw new InvalidOperationException("ROI 区域无效，无法创建图像模板");
                    AppendLog("使用 ROI 灰度图创建模板...");
                    _modelId = await HalconComputeRunner.RunAsync(() =>
                        HalconFlowBridge.CreateShapeModel(imageForCreate, null, regionForCreate, opt),
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
                        bool noContours = xldBundle?.Contours == null || xldBundle.Contours.Count == 0;
                        bool noNative = _nativeXldForCreate == null
                                        || !_nativeXldForCreate.IsInitialized()
                                        || _nativeXldForCreate.CountObj() == 0;
                        if (noContours && noNative)
                        {
                            MessageBox.Show(
                                "未能提取到有效轮廓。\n阈值模式请调灰度；手绘/沿边线模式请调 Canny、边带或梯度向内/向外；"
                                + "开放轨迹可尝试将「最小轮廓点数」降到 5 以下。",
                                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        if (noContours && !noNative)
                        {
                            xldBundle = HalconFlowBridge.BundleFromXldOrFallback(
                                _nativeXldForCreate!, _imgWidth, _imgHeight, opt.MinContourPoints);
                        }

                        AppendLog($"轮廓: {xldBundle.Contours.Count} 条");
                        SetWorkingContours(xldBundle);
                        MarkContourCacheReady(opt, minGray, maxGray, includeTrimState: false);
                    }

                    nativeXldForCreate = HalconFlowBridge.CloneHObject(_nativeXldForCreate);
                    _modelId = await HalconComputeRunner.RunAsync(() =>
                        HalconFlowBridge.CreateShapeModel(imageForCreate, xldBundle, null, opt, nativeXldForCreate),
                        HalconThreadPolicy.Geometry);
                    if (opt.SourceKind == HalconShapeModelSourceKind.PolygonXld)
                        AppendLog("匹配指标建议: use_polarity（已含 edge_direction）");
                }

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

                string? resolvedMetric = ResolveMetricUsedForCreate(opt, xldBundle, fromImage);
                _lastModelCreateMeta = CaptureModelCreateMeta(opt, minGray, maxGray, xldBundle, resolvedMetric);

                ResetFindResultUi();
                AppendLog($"模型创建成功 ModelID={_modelId}, epoch={_modelEpoch} ({_modelKind})");
                if (!string.IsNullOrWhiteSpace(resolvedMetric)
                    && !string.Equals(resolvedMetric, opt.Metric, StringComparison.OrdinalIgnoreCase))
                    AppendLog($"Metric: UI={opt.Metric} → 实际={resolvedMetric}");
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
                imageForCreate?.Dispose();
                regionForCreate?.Dispose();
                nativeXldForCreate?.Dispose();
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
                CalibImage? imageForFind = DuplicateCurrentImageForHalcon();

                AppendLog($"查找({modelKind}): ModelID={modelId}, epoch={modelEpoch}, NumMatches={numMatches}, MinScore={minScore}, Greediness={greediness}, NumLevels={findNumLevels}, 角度[{angleStart}°~{angleStart + angleExtent}°]");
                TxtFindResult.Text = "查找中…";

                if (modelKind == HalconFlowModelKind.Deformable)
                {
                    var scaleOpt = ReadCreateOptionsFromUi();
                    try
                    {
                        var findDef = await HalconComputeRunner.RunAsync(ct =>
                        {
                            if (autoRetry)
                            {
                                return HalconFlowBridge.FindDeformableModelWithFallback(
                                    imageForFind, modelId, angleStart, angleExtent, minScore, numMatches, maxOverlap,
                                    findNumLevels, greediness, scaleOpt, ct);
                            }

                            ct.ThrowIfCancellationRequested();
                            return HalconFlowBridge.FindDeformableModel(
                                imageForFind, modelId, angleStart, angleExtent, minScore, numMatches, maxOverlap,
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
                    }
                    finally
                    {
                        imageForFind.Dispose();
                    }

                    return;
                }

                try
                {
                    var findResult = await HalconComputeRunner.RunAsync(ct =>
                    {
                        if (autoRetry)
                        {
                            return HalconFlowBridge.FindShapeModelWithFallback(
                                imageForFind,
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
                            imageForFind,
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
                finally
                {
                    imageForFind.Dispose();
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

                string metaNote = "";
                if (HalconShapeModelFileNameCodec.TryParse(IoPath.GetFileName(dlg.FileName), out HalconShapeModelParsedFileName parsed))
                {
                    ApplyParsedFileName(parsed);
                    metaNote = "\n已从文件名恢复创建参数";
                    AppendLog($"已从文件名解析创建参数: {IoPath.GetFileName(dlg.FileName)}");
                }
                else
                {
                    AppendLog("文件名未使用 xv_ 编码格式，未恢复创建参数");
                }

                TxtModelInfo.Text = $"来源: 文件导入 ({_modelKind})\nEpoch: {_modelEpoch}\n文件: {dlg.FileName}\n{summary}";
                AppendLog($"模型已导入: {dlg.FileName} (ModelID={_modelId}, epoch={_modelEpoch}, {_modelKind})");
                MessageBox.Show($"模型已导入，可进行匹配测试。\nModelID: {_modelId}{metaNote}", "导入成功",
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
                FileName = BuildDefaultExportFileName()
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                if (isDeformable)
                    HalconFlowBridge.WriteDeformableModelToFile(_modelId, dlg.FileName);
                else
                    HalconFlowBridge.WriteShapeModelToFile(_modelId, dlg.FileName);

                AppendLog($"模型已导出: {dlg.FileName}");
                AppendLog($"文件名编码: {HalconShapeModelFileNameCodec.DescribeFormat()}");
                MessageBox.Show(
                    $"HALCON 模型已保存:\n{dlg.FileName}\n\n创建参数已编码在文件名中（xv_ 前缀），便于区分不同模型。",
                    "导出成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
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
