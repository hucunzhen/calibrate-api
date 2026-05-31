using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IoPath = System.IO.Path;

namespace CalibOperatorCLI_Example
{
    public partial class HalconShapeModelPage
    {
        private bool _suppressSessionPersist;
        private bool _suppressDrawModeClear;
        private bool _sessionHooksAttached;
        private bool _uiReady;

        /// <summary>离开页或关程序前写入 %AppData%/IndustrialVisionTools/halcon_shape_model_ui.json。</summary>
        internal void SaveSession() => PersistSessionFromUi();

        /// <summary>进入形状模板页时恢复上次图像与 ROI。</summary>
        internal void RestoreSessionOnShow()
        {
            if (!IsLoaded)
            {
                Dispatcher.BeginInvoke(RestoreSessionOnShow, DispatcherPriority.Loaded);
                return;
            }

            ApplySessionFromSettingsIfNeeded(forceReload: false);
        }

        private void HalconShapeModelPage_Loaded(object sender, RoutedEventArgs e)
        {
            _suppressSessionPersist = true;
            UpdateCreatePanelsVisibility();
            if (string.IsNullOrWhiteSpace(TxtCalibrationJsonPath.Text))
            {
                string guess = IoPath.GetFullPath(IoPath.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "test_images", "chessboard_calibration_from_dir.json"));
                if (File.Exists(guess))
                    TxtCalibrationJsonPath.Text = guess;
            }

            ApplySessionFromSettingsIfNeeded(forceReload: true);

            if (_imgWidth > 0)
                FitImageToView();

            if (!_sessionHooksAttached)
            {
                _sessionHooksAttached = true;
                AttachSessionPersistHandlers();
            }

            _uiReady = true;
            _suppressSessionPersist = false;
        }

        private void HalconShapeModelPage_Unloaded(object sender, RoutedEventArgs e)
        {
            SaveSession();
            DisposeNativeXldForCreate();
            DisposeCurrentModel();
            _rawCalibImage?.Dispose();
            _rawCalibImage = null;
            _currentImage?.Dispose();
            _currentImage = null;
            _grayPixels = null;
            if (DisplayImage != null)
                DisplayImage.Source = null;
        }

        private void FlushSessionPersist()
        {
            _sessionPersistTimer?.Stop();
            PersistSessionFromUi();
        }

        private void AttachSessionPersistHandlers()
        {
            TxtCalibrationJsonPath.LostFocus += (_, _) => ScheduleSessionPersist();
        }

        private void ScheduleSessionPersist()
        {
            if (!_uiReady || _suppressSessionPersist || _sessionPersistTimer == null)
                return;
            _sessionPersistTimer.Stop();
            _sessionPersistTimer.Start();
        }

        private void PersistSessionChange()
        {
            if (!_uiReady)
                return;
            FlushSessionPersist();
        }

        private void PersistSessionFromUi()
        {
            if (_suppressSessionPersist)
                return;

            string imagePath = string.IsNullOrWhiteSpace(_loadedImagePath)
                ? ""
                : IoPath.GetFullPath(_loadedImagePath);

            // 启动/初始化阶段或未加载图时，不要用空 ImagePath 覆盖磁盘上已有会话
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                HalconShapeModelUiSettings existing = HalconShapeModelUiSettings.Load();
                if (!string.IsNullOrWhiteSpace(existing.ImagePath))
                    return;
            }

            var s = new HalconShapeModelUiSettings
            {
                ImagePath = imagePath,
                CalibrationJsonPath = TxtCalibrationJsonPath?.Text?.Trim() ?? "",
            };
            CaptureRoiIntoSettings(s);
            s.Save();
        }

        private void ApplySessionFromSettingsIfNeeded(bool forceReload)
        {
            var s = HalconShapeModelUiSettings.Load();
            if (string.IsNullOrWhiteSpace(s.ImagePath))
            {
                if (!TryRefreshDisplayFromMemory())
                    AppendLog($"[会话] 无上次图像（配置: {HalconShapeModelUiSettings.HintPath}）");
                return;
            }

            string imagePath = IoPath.GetFullPath(s.ImagePath);
            if (!File.Exists(imagePath))
            {
                AppendLog($"[会话] 上次图像不存在: {imagePath}");
                return;
            }

            bool displayReady = _imgWidth > 0 && _grayPixels != null && DisplayImage?.Source != null;
            bool sameImage = !string.IsNullOrWhiteSpace(_loadedImagePath)
                && string.Equals(IoPath.GetFullPath(_loadedImagePath), imagePath, StringComparison.OrdinalIgnoreCase);

            if (!forceReload && displayReady && sameImage)
            {
                ApplyRoiFromSettings(s);
                return;
            }

            TryRestoreSessionFromSettings(s, imagePath);
        }

        private void TryRestoreSessionFromSettings()
        {
            var s = HalconShapeModelUiSettings.Load();
            if (string.IsNullOrWhiteSpace(s.ImagePath))
            {
                AppendLog("[会话] 配置中无上次图像");
                return;
            }

            string imagePath = IoPath.GetFullPath(s.ImagePath);
            if (!File.Exists(imagePath))
            {
                AppendLog($"[会话] 上次图像不存在: {imagePath}");
                return;
            }

            TryRestoreSessionFromSettings(s, imagePath);
        }

        private void TryRestoreSessionFromSettings(HalconShapeModelUiSettings s, string imagePath)
        {
            _suppressSessionPersist = true;
            _suppressDrawModeClear = true;
            try
            {
                if (!string.IsNullOrWhiteSpace(s.CalibrationJsonPath))
                    TxtCalibrationJsonPath.Text = s.CalibrationJsonPath;

                LoadImage(imagePath, clearRoi: false);
                ApplyRoiFromSettings(s);
                AppendLog($"已恢复上次会话: {IoPath.GetFileName(imagePath)}（ROI={s.RoiMode}）");
            }
            catch (Exception ex)
            {
                AppendLog($"[会话恢复] {ex.Message}");
            }
            finally
            {
                _suppressDrawModeClear = false;
                _suppressSessionPersist = false;
            }
        }

        private bool TryRefreshDisplayFromMemory()
        {
            if (_imgWidth <= 0 || _grayPixels == null || DisplayImage == null)
                return false;
            if (DisplayImage.Source != null)
                return true;

            var bmp = BitmapSource.Create(
                _imgWidth, _imgHeight, 96, 96, PixelFormats.Gray8, null, _grayPixels, _imgWidth);
            bmp.Freeze();
            DisplayImage.Source = bmp;
            DisplayImage.Width = _imgWidth;
            DisplayImage.Height = _imgHeight;
            ImageCanvas.Width = _imgWidth;
            ImageCanvas.Height = _imgHeight;
            RefreshRoiVisuals();
            RebuildCommittedPolygonVisual();
            UpdateRingPreview();
            if (_hasRoi || _hasRingRoi)
                UpdateThresholdPreview();
            return true;
        }

        private string CaptureCurrentRoiMode()
        {
            if (_hasRingRoi && IsRingModeActive())
                return "ring";
            if (_hasRoi && IsCircleModeActive())
                return "circle";
            if (_hasRoi && RbPolygonMode?.IsChecked == true)
                return "polygon";
            if (_hasRoi && RbRectMode?.IsChecked == true)
                return "rect";
            return "none";
        }

        private void CaptureRoiIntoSettings(HalconShapeModelUiSettings s)
        {
            s.RoiMode = CaptureCurrentRoiMode();
            s.RoiRectX = _roiRectImage.X;
            s.RoiRectY = _roiRectImage.Y;
            s.RoiRectW = _roiRectImage.Width;
            s.RoiRectH = _roiRectImage.Height;
            s.CircleCenterX = _circleCenterImage.X;
            s.CircleCenterY = _circleCenterImage.Y;
            s.CircleRadius = _circleRadiusImage;
            s.PolygonPath = ExportRoiPath(_roiPath);
            s.RingOuterPath = ExportRoiPath(_ringOuterPath);
            s.RingInnerPath = ExportRoiPath(_ringInnerPath);
        }

        private static HalconShapeModelRoiPathDto? ExportRoiPath(RoiContourPath path)
        {
            if (path.VertexCount == 0)
                return null;

            var dto = new HalconShapeModelRoiPathDto { IsClosed = path.IsClosed };
            foreach (Point p in path.Vertices)
            {
                dto.VertexXs.Add(p.X);
                dto.VertexYs.Add(p.Y);
            }

            foreach (RoiEdgeKind kind in path.EdgeKinds)
                dto.EdgeKinds.Add(kind.ToString());

            foreach (Point? via in path.ArcVia)
            {
                dto.ArcViaXs.Add(via?.X);
                dto.ArcViaYs.Add(via?.Y);
            }

            return dto;
        }

        private void ApplyRoiFromSettings(HalconShapeModelUiSettings s)
        {
            SetRoiModeRadio(s.RoiMode);

            _hasRoi = false;
            _hasRingRoi = false;
            _ringPolygonPhase = 0;
            _roiPath.Clear();
            _ringOuterPath.Clear();
            _ringInnerPath.Clear();
            ResetCircleDrawState();

            switch (s.RoiMode)
            {
                case "rect":
                    if (s.RoiRectW > 5 && s.RoiRectH > 5)
                    {
                        _roiRectImage = new Rect(s.RoiRectX, s.RoiRectY, s.RoiRectW, s.RoiRectH);
                        _hasRoi = true;
                    }
                    break;
                case "circle":
                    if (s.CircleRadius > 5)
                    {
                        _circleCenterImage = new Point(s.CircleCenterX, s.CircleCenterY);
                        _circleRadiusImage = s.CircleRadius;
                        ClampCircleRadiusToImage();
                        _hasRoi = _circleRadiusImage > 5;
                    }
                    break;
                case "polygon":
                    ImportRoiPath(_roiPath, s.PolygonPath);
                    _hasRoi = _roiPath.IsClosed && _roiPath.VertexCount >= 3;
                    break;
                case "ring":
                    ImportRoiPath(_ringOuterPath, s.RingOuterPath);
                    ImportRoiPath(_ringInnerPath, s.RingInnerPath);
                    _hasRingRoi = _ringOuterPath.IsClosed && _ringInnerPath.IsClosed
                                  && _ringOuterPath.VertexCount >= 3 && _ringInnerPath.VertexCount >= 3;
                    _ringPolygonPhase = _hasRingRoi ? 2 : 0;
                    _hasRoi = _hasRingRoi;
                    InvalidateRingFlatCache();
                    break;
            }

            InvalidatePolygonFlatCache();
            RefreshRoiVisuals();
            RebuildCommittedPolygonVisual();
            UpdateRingPreview();
            if (_hasRoi || _hasRingRoi)
                UpdateThresholdPreview();
        }

        private void SetRoiModeRadio(string roiMode)
        {
            switch (roiMode)
            {
                case "circle":
                    RbCircleMode.IsChecked = true;
                    break;
                case "polygon":
                    RbPolygonMode.IsChecked = true;
                    break;
                case "ring":
                    RbRingMode.IsChecked = true;
                    break;
                default:
                    RbRectMode.IsChecked = true;
                    break;
            }
        }

        private static void ImportRoiPath(RoiContourPath path, HalconShapeModelRoiPathDto? dto)
        {
            path.Clear();
            if (dto == null || dto.VertexXs.Count == 0)
                return;

            int n = Math.Min(dto.VertexXs.Count, dto.VertexYs.Count);
            for (int i = 0; i < n; i++)
                path.Vertices.Add(new Point(dto.VertexXs[i], dto.VertexYs[i]));

            int edgeCount = dto.EdgeKinds.Count;
            for (int i = 0; i < edgeCount; i++)
            {
                if (Enum.TryParse(dto.EdgeKinds[i], out RoiEdgeKind kind))
                    path.EdgeKinds.Add(kind);
                else
                    path.EdgeKinds.Add(RoiEdgeKind.Line);
            }

            int viaCount = Math.Max(dto.ArcViaXs.Count, dto.ArcViaYs.Count);
            for (int i = 0; i < viaCount; i++)
            {
                double? x = i < dto.ArcViaXs.Count ? dto.ArcViaXs[i] : null;
                double? y = i < dto.ArcViaYs.Count ? dto.ArcViaYs[i] : null;
                path.ArcVia.Add(x.HasValue && y.HasValue ? new Point(x.Value, y.Value) : null);
            }

            while (path.ArcVia.Count < path.EdgeKinds.Count)
                path.ArcVia.Add(null);

            path.IsClosed = dto.IsClosed;
        }
    }
}
