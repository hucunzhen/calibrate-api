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

#if HALCON_ENABLED
            if (TxtNinePointCalibPath != null && string.IsNullOrWhiteSpace(TxtNinePointCalibPath.Text))
            {
                string? nineGuess = CalibrationResultFileLoader.TryGuessDefaultPath();
                if (!string.IsNullOrWhiteSpace(nineGuess))
                    TxtNinePointCalibPath.Text = nineGuess;
            }
#endif

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
            UpdateTangentFlipButtonVisibility();
            UpdateRoiManagePanelVisibility();
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
            foreach (string name in HalconShapeModelUiSettings.PersistControlNameList)
            {
                if (FindName(name) is System.Windows.Controls.TextBox tb)
                {
                    tb.LostFocus += (_, _) => ScheduleSessionPersist();
                    tb.TextChanged += (_, _) => ScheduleSessionPersist();
                }
                else if (FindName(name) is System.Windows.Controls.CheckBox cb)
                {
                    cb.Checked += (_, _) => ScheduleSessionPersist();
                    cb.Unchecked += (_, _) => ScheduleSessionPersist();
                }
                else if (FindName(name) is System.Windows.Controls.ComboBox cmb)
                {
                    cmb.SelectionChanged += (_, _) => ScheduleSessionPersist();
                }
                else if (FindName(name) is System.Windows.Controls.RadioButton rb)
                {
                    rb.Checked += (_, _) => ScheduleSessionPersist();
                }
            }

            TxtCalibrationJsonPath.LostFocus += (_, _) => ScheduleSessionPersist();
            TxtUndistortAlpha.LostFocus += (_, _) => ScheduleSessionPersist();
            TxtCalibViewIndex.LostFocus += (_, _) => ScheduleSessionPersist();
            TxtCalibBoardCols.LostFocus += (_, _) => ScheduleSessionPersist();
            TxtCalibBoardRows.LostFocus += (_, _) => ScheduleSessionPersist();
            TxtCalibSquareSizeMm.LostFocus += (_, _) => ScheduleSessionPersist();
            TxtCalibPxPerMm.LostFocus += (_, _) => ScheduleSessionPersist();
            if (TxtNinePointCalibPath != null)
            {
                TxtNinePointCalibPath.LostFocus += (_, _) => ScheduleSessionPersist();
                TxtNinePointCalibPath.TextChanged += (_, _) => ScheduleSessionPersist();
            }

            ChkEnableUndistort.Checked += OnCameraCorrectionSettingChanged;
            ChkEnableUndistort.Unchecked += OnCameraCorrectionSettingChanged;
            ChkEnablePerspective.Checked += OnCameraCorrectionSettingChanged;
            ChkEnablePerspective.Unchecked += OnCameraCorrectionSettingChanged;
            CmbPerspectiveOutputFrame.SelectionChanged += (_, _) => ScheduleSessionPersist();
            if (ChkRotateExpandCanvas != null)
            {
                ChkRotateExpandCanvas.Checked += OnCameraCorrectionSettingChanged;
                ChkRotateExpandCanvas.Unchecked += OnCameraCorrectionSettingChanged;
            }

            if (TxtPostCorrectRotateDeg != null)
                TxtPostCorrectRotateDeg.LostFocus += (_, _) => ScheduleSessionPersist();
        }

        private void OnCameraCorrectionSettingChanged(object sender, RoutedEventArgs e) => ScheduleSessionPersist();

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
                NinePointCalibrationPath = TxtNinePointCalibPath?.Text?.Trim() ?? "",
            };
            CaptureRoiIntoSettings(s);
            CaptureCameraCorrectionIntoSettings(s);
            s.Controls = PageUiBinder.Capture(this, HalconShapeModelUiSettings.PersistControlNameList);
            s.Save();
        }

        private void CaptureCameraCorrectionIntoSettings(HalconShapeModelUiSettings s)
        {
            s.EnableUndistort = ChkEnableUndistort?.IsChecked == true;
            s.UndistortAlpha = TxtUndistortAlpha?.Text?.Trim() ?? "-1";
            s.EnablePerspective = ChkEnablePerspective?.IsChecked == true;
            if (int.TryParse(TxtCalibViewIndex?.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int vi))
                s.CalibViewIndex = vi;
            if (int.TryParse(TxtCalibBoardCols?.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cols))
                s.CalibBoardCols = cols;
            if (int.TryParse(TxtCalibBoardRows?.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rows))
                s.CalibBoardRows = rows;
            s.CalibSquareSizeMm = TxtCalibSquareSizeMm?.Text?.Trim() ?? "25";
            s.CalibPxPerMm = TxtCalibPxPerMm?.Text?.Trim() ?? "1";
            s.PerspectiveOutputFrame = GetPerspectiveOutputFrameTag();
            s.PostCorrectRotateDeg = _postCorrectRotateDeg;
            s.RotateExpandCanvas = ChkRotateExpandCanvas?.IsChecked == true;
        }

        private string GetPerspectiveOutputFrameTag()
        {
            if (CmbPerspectiveOutputFrame?.SelectedItem is System.Windows.Controls.ComboBoxItem item
                && item.Tag is string tag
                && !string.IsNullOrWhiteSpace(tag))
                return tag.Trim();
            return "board";
        }

        private void ApplyCameraCorrectionFromSettings(HalconShapeModelUiSettings s)
        {
            if (ChkEnableUndistort != null)
                ChkEnableUndistort.IsChecked = s.EnableUndistort;
            if (!string.IsNullOrWhiteSpace(s.UndistortAlpha))
                TxtUndistortAlpha.Text = s.UndistortAlpha;
            if (ChkEnablePerspective != null)
                ChkEnablePerspective.IsChecked = s.EnablePerspective;
            TxtCalibViewIndex.Text = s.CalibViewIndex.ToString(CultureInfo.InvariantCulture);
            TxtCalibBoardCols.Text = (s.CalibBoardCols > 0 ? s.CalibBoardCols : 9).ToString(CultureInfo.InvariantCulture);
            TxtCalibBoardRows.Text = (s.CalibBoardRows > 0 ? s.CalibBoardRows : 6).ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(s.CalibSquareSizeMm))
                TxtCalibSquareSizeMm.Text = s.CalibSquareSizeMm;
            if (!string.IsNullOrWhiteSpace(s.CalibPxPerMm))
                TxtCalibPxPerMm.Text = s.CalibPxPerMm;
            SelectPerspectiveOutputFrame(s.PerspectiveOutputFrame);
            _postCorrectRotateDeg = NormalizePostCorrectRotateDeg(s.PostCorrectRotateDeg);
            if (ChkRotateExpandCanvas != null)
                ChkRotateExpandCanvas.IsChecked = s.RotateExpandCanvas;
            SyncPostCorrectRotateTextBox();
        }

        private void ApplyExtraControlsFromSettings(HalconShapeModelUiSettings s)
        {
            if (s.Controls == null || s.Controls.Count == 0)
                return;
            PageUiBinder.Apply(this, s.Controls, HalconShapeModelUiSettings.PersistControlNameList);
        }

        private void SelectPerspectiveOutputFrame(string? tag)
        {
            if (CmbPerspectiveOutputFrame == null || string.IsNullOrWhiteSpace(tag))
                return;
            string want = tag.Trim();
            foreach (var item in CmbPerspectiveOutputFrame.Items)
            {
                if (item is System.Windows.Controls.ComboBoxItem cbi
                    && string.Equals(cbi.Tag as string, want, StringComparison.OrdinalIgnoreCase))
                {
                    CmbPerspectiveOutputFrame.SelectedItem = cbi;
                    return;
                }
            }
        }

        private void TryReapplyCameraCorrectionAfterSessionRestore()
        {
            if (_rawCalibImage == null)
                return;
            if (ChkEnableUndistort?.IsChecked != true && ChkEnablePerspective?.IsChecked != true)
                return;
            try
            {
                TryApplyCameraCorrections(logSuccess: false);
            }
            catch (Exception ex)
            {
                AppendLog($"[相机矫正] 会话恢复后应用失败: {ex.Message}");
                CommitCalibImageToUi(_rawCalibImage, disposeIncoming: false);
            }
        }

        private void ApplySessionFromSettingsIfNeeded(bool forceReload)
        {
            var s = HalconShapeModelUiSettings.Load();
            if (string.IsNullOrWhiteSpace(s.ImagePath))
            {
                if (!TryRefreshDisplayFromMemory())
                {
                    AppendLog($"[会话] 无上次图像（配置: {HalconShapeModelUiSettings.HintPath}）");
                    return;
                }

                ApplyCameraCorrectionFromSettings(s);
                ApplyExtraControlsFromSettings(s);
                TryReapplyCameraCorrectionAfterSessionRestore();
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
                ApplyCameraCorrectionFromSettings(s);
                ApplyRoiFromSettings(s);
                ApplyExtraControlsFromSettings(s);
                TryReapplyCameraCorrectionAfterSessionRestore();
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
                if (!string.IsNullOrWhiteSpace(s.NinePointCalibrationPath))
                    TxtNinePointCalibPath.Text = s.NinePointCalibrationPath;
                ApplyCameraCorrectionFromSettings(s);

                LoadImage(imagePath, clearRoi: false);
                ApplyRoiFromSettings(s);
                ApplyExtraControlsFromSettings(s);
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
            if (IsOpenTrajectoriesModeActive()
                && (_hasOpenTrajectoriesRoi || _roiPath.VertexCount >= 2))
                return "opentrajectories";
            if (_hasRoi && IsCircleModeActive())
                return "circle";
            if (_hasRoi && RbPolygonMode?.IsChecked == true)
                return "polygon";
            if (_hasRoi && RbRectMode?.IsChecked == true)
                return "rect";
            if (HasUsableRotatedRectRoi())
                return "rotatedrect";
            return "none";
        }

        private void CaptureRoiIntoSettings(HalconShapeModelUiSettings s)
        {
            s.RoiMode = CaptureCurrentRoiMode();
            s.RoiRectX = _roiRectImage.X;
            s.RoiRectY = _roiRectImage.Y;
            s.RoiRectW = _roiRectImage.Width;
            s.RoiRectH = _roiRectImage.Height;
            s.RotRectCenterX = _rotRectCenterX;
            s.RotRectCenterY = _rotRectCenterY;
            s.RotRectWidth = _rotRectWidth;
            s.RotRectHeight = _rotRectHeight;
            s.RotRectAngleDeg = _rotRectAngleDeg;
            s.CircleCenterX = _circleCenterImage.X;
            s.CircleCenterY = _circleCenterImage.Y;
            s.CircleRadius = _circleRadiusImage;
            s.PolygonPath = ExportRoiPath(_roiPath);
            s.RingOuterPath = ExportRoiPath(_ringOuterPath);
            s.RingInnerPath = ExportRoiPath(_ringInnerPath);
            s.OpenTrajectoryPaths.Clear();
            s.OpenTrajectoryNames.Clear();
            foreach (OpenTrajectoryEntry entry in _openTrajectoryEntries)
            {
                if (ExportRoiPath(entry.Path) is HalconShapeModelRoiPathDto dto)
                {
                    s.OpenTrajectoryPaths.Add(dto);
                    s.OpenTrajectoryNames.Add(entry.Name ?? "");
                }
            }

            s.LastRoiFile = _lastRoiFilePath ?? "";
            s.OpenTrajectoryDraft = ExportRoiPath(_roiPath);
            s.OpenTrajectoryConnectors.Clear();
            foreach (RoiConnectorSegment conn in _trajectoryConnectors)
            {
                s.OpenTrajectoryConnectors.Add(new HalconShapeModelRoiConnectorDto
                {
                    StartX = conn.StartX,
                    StartY = conn.StartY,
                    EndX = conn.EndX,
                    EndY = conn.EndY
                });
            }
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
            _openTrajectoryEntries.Clear();
            _editingOpenTrajectoryIndex = null;
            _hasOpenTrajectoriesRoi = false;
            _trajectoryConnectors.Clear();
            _connectorDraftStart = null;
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
                case "rotatedrect":
                    if (s.RotRectWidth > 5 && s.RotRectHeight > 5)
                    {
                        _rotRectCenterX = s.RotRectCenterX;
                        _rotRectCenterY = s.RotRectCenterY;
                        _rotRectWidth = s.RotRectWidth;
                        _rotRectHeight = s.RotRectHeight;
                        _rotRectAngleDeg = s.RotRectAngleDeg;
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
                case "opentrajectories":
                    _openTrajectoryEntries.Clear();
                    _editingOpenTrajectoryIndex = null;
                    if (s.OpenTrajectoryPaths != null)
                    {
                        int ti = 0;
                        foreach (HalconShapeModelRoiPathDto? dto in s.OpenTrajectoryPaths)
                        {
                            if (dto == null)
                                continue;
                            var path = new RoiContourPath();
                            ImportRoiPath(path, dto);
                            path.IsClosed = false;
                            if (path.VertexCount < 2)
                                continue;

                            string name = ti < s.OpenTrajectoryNames?.Count
                                          && !string.IsNullOrWhiteSpace(s.OpenTrajectoryNames[ti])
                                ? s.OpenTrajectoryNames[ti].Trim()
                                : DefaultTrajectoryName(ti);
                            _openTrajectoryEntries.Add(new OpenTrajectoryEntry { Name = name, Path = path });
                            ti++;
                        }
                    }

                    ImportRoiPath(_roiPath, s.OpenTrajectoryDraft);
                    _roiPath.IsClosed = false;
                    _trajectoryConnectors.Clear();
                    if (s.OpenTrajectoryConnectors != null)
                    {
                        foreach (HalconShapeModelRoiConnectorDto? cdto in s.OpenTrajectoryConnectors)
                        {
                            if (cdto == null)
                                continue;
                            _trajectoryConnectors.Add(new RoiConnectorSegment(
                                cdto.StartX, cdto.StartY, cdto.EndX, cdto.EndY));
                        }
                    }

                    _hasOpenTrajectoriesRoi = _openTrajectoryEntries.Count > 0;
                    _hasRoi = _hasOpenTrajectoriesRoi || _roiPath.VertexCount >= 2;
                    break;
            }

            _lastRoiFilePath = !string.IsNullOrWhiteSpace(s.LastRoiFile)
                ? s.LastRoiFile.Trim()
                : string.IsNullOrWhiteSpace(s.LastOpenTrajectoryFile)
                    ? null
                    : s.LastOpenTrajectoryFile.Trim();

            InvalidatePolygonFlatCache();
            RefreshRoiVisuals();
            RebuildCommittedPolygonVisual();
            RebuildOpenTrajectoriesVisual();
            RebuildConnectorsVisual();
            UpdateConnectorRubberVisual();
            UpdateRingPreview();
            UpdateRoiManagePanelVisibility();
            if (_hasRoi || _hasRingRoi)
                UpdateThresholdPreview();
        }

        private void SetRoiModeRadio(string roiMode)
        {
            switch (roiMode)
            {
                case "rotatedrect":
                    RbRotatedRectMode.IsChecked = true;
                    break;
                case "circle":
                    RbCircleMode.IsChecked = true;
                    break;
                case "polygon":
                    RbPolygonMode.IsChecked = true;
                    break;
                case "ring":
                    RbRingMode.IsChecked = true;
                    break;
                case "opentrajectories":
                    RbOpenTrajectoriesMode.IsChecked = true;
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
