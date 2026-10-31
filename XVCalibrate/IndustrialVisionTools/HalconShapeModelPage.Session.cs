using System;
using System.Globalization;
using System.IO;
using System.Windows;
using CalibOperatorPInvoke;
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
        private bool _sessionRestoreScheduled;
        private int _sessionRestoreEpoch;
        private bool _insideSessionRestore;

        /// <summary>离开页或关程序前写入 %AppData%/IndustrialVisionTools/halcon_shape_model_ui.json。</summary>
        internal void SaveSession() => PersistSessionFromUi();

        /// <summary>进入形状模板页：先让页面显示，再在空闲时恢复图像/ROI。</summary>
        internal void RestoreSessionOnShow() => ScheduleSessionRestoreAfterShow();

        /// <summary>关窗时释放本页原生资源（KeepAlive 切走时不卸载）。</summary>
        internal void ReleaseRuntimeResources()
        {
            _sessionRestoreEpoch++;
            _sessionRestoreScheduled = false;
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

        private void ScheduleSessionRestoreAfterShow()
        {
            if (_sessionRestoreScheduled)
                return;
            _sessionRestoreScheduled = true;
            int epoch = _sessionRestoreEpoch;
            // Render 后再 Background：先完成切页绘制，再读图/矫正。
            Dispatcher.BeginInvoke(() =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _sessionRestoreScheduled = false;
                    if (epoch != _sessionRestoreEpoch)
                        return;
                    RestoreSessionDeferred();
                }, DispatcherPriority.Background);
            }, DispatcherPriority.Render);
        }

        private void RestoreSessionDeferred()
        {
            if (!IsLoaded)
            {
                ScheduleSessionRestoreAfterShow();
                return;
            }

            _suppressSessionPersist = true;
            _insideSessionRestore = true;
            try
            {
                ApplyGlobalCalibrationPathDefaultsIfEmpty();
                bool displayReady = _imgWidth > 0 && _grayPixels != null && DisplayImage?.Source != null;
                ApplySessionFromSettingsIfNeeded(forceReload: !displayReady);
                ApplyRecipeGlobalCalibrationDefaults(forceCorrectionParams: false);
                if (_imgWidth > 0)
                    FitImageToView();

                RefreshParamRecipeCombo(
                    HalconShapeModelUiSettings.Load().LastParamRecipeId,
                    apply: false);
                TryRestoreCachedModelIfMissing();
            }
            finally
            {
                _insideSessionRestore = false;
                _uiReady = true;
                _suppressSessionPersist = false;
                UpdateTangentFlipButtonVisibility();
                UpdateRoiManagePanelVisibility();
                UpdateOptionDependentPanelsVisibility();
            }
        }

        private void HalconShapeModelPage_Loaded(object sender, RoutedEventArgs e)
        {
            _suppressSessionPersist = true;
            UpdateCreatePanelsVisibility();
            UpdateTangentFlipButtonVisibility();
            UpdateRoiManagePanelVisibility();
            UpdateOptionDependentPanelsVisibility();

            if (!_sessionHooksAttached)
            {
                _sessionHooksAttached = true;
                AttachSessionPersistHandlers();
            }

            // 切页先出界面；图像、ROI、配方标定放到空闲时再做。
            ScheduleSessionRestoreAfterShow();
        }

        private void HalconShapeModelPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // Frame 切走仍会 Unloaded。模型/图像留在本页实例里，关窗才 ReleaseRuntimeResources。
            SaveSession();
        }

        private void FlushSessionPersist()
        {
            _sessionPersistTimer?.Stop();
            PersistSessionFromUi();
        }

        /// <summary>
        /// 将本页标定/矫正状态同步为当前流程配方全局配置
        /// （<c>models/camera_correction.json</c> + 约定标定文件）。
        /// </summary>
        private void ApplyRecipeGlobalCalibrationDefaults(bool forceCorrectionParams = false)
        {
            RecipeCameraCorrectionDefaults defaults =
                FlowRecipeCatalog.TryGetSelectedRecipeCameraCorrectionDefaults();

            string? chess = defaults.ChessboardCalibrationJsonPath
                            ?? CalibrationResultFileLoader.TryGuessDefaultChessboardCalibrationPath();
            if (!string.IsNullOrWhiteSpace(chess)
                && (forceCorrectionParams
                    || ShouldReplaceCalibrationPath(_recipeChessboardCalibPath, chess, FlowRecipeCatalog.TryGetSelectedRecipeDirectory())))
                _recipeChessboardCalibPath = chess;

#if HALCON_ENABLED
            string? nine = defaults.NinePointCalibrationJsonPath
                           ?? CalibrationResultFileLoader.TryGuessDefaultPath();
            if (!string.IsNullOrWhiteSpace(nine)
                && (forceCorrectionParams
                    || ShouldReplaceCalibrationPath(_recipeNinePointCalibPath, nine, FlowRecipeCatalog.TryGetSelectedRecipeDirectory())))
            {
                _recipeNinePointCalibPath = nine;
                InvalidateNinePointAffineCache();
            }
#endif

            bool correctionUnset = !_recipeEnableUndistort && !_recipeEnablePerspective;
            if (forceCorrectionParams || correctionUnset)
                ApplyCameraCorrectionDefaultsFromRecipe(defaults);

            RefreshRecipeCalibrationSummaryUi();
        }

        /// <summary>兼容旧调用名：同步配方全局标定。</summary>
        private void ApplyGlobalCalibrationPathDefaultsIfEmpty() =>
            ApplyRecipeGlobalCalibrationDefaults(forceCorrectionParams: false);

        private static bool ShouldReplaceCalibrationPath(string? currentRaw, string recipePath, string? recipeDir)
        {
            string current = currentRaw?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(current))
                return true;

            string recipeFull;
            try
            {
                recipeFull = IoPath.GetFullPath(recipePath);
            }
            catch
            {
                return false;
            }

            string currentFull;
            try
            {
                currentFull = IoPath.GetFullPath(current);
            }
            catch
            {
                return true;
            }

            if (string.Equals(currentFull, recipeFull, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!File.Exists(currentFull))
                return true;
            if (!string.IsNullOrWhiteSpace(recipeDir)
                && !FlowRecipeCatalog.IsPathUnderRecipeDirectory(currentFull, recipeDir))
                return true;

            return false;
        }

        private void ApplyCameraCorrectionDefaultsFromRecipe(RecipeCameraCorrectionDefaults defaults)
        {
            _recipeEnableUndistort = defaults.EnableUndistort;
            if (!string.IsNullOrWhiteSpace(defaults.UndistortAlpha))
                _recipeUndistortAlpha = defaults.UndistortAlpha;
            _recipeEnablePerspective = defaults.EnablePerspective;
            _recipeCalibViewIndex = defaults.ViewIndex;
            _recipeCalibBoardCols = defaults.BoardCols > 0
                ? defaults.BoardCols
                : ChessboardCalibrationDefaults.InnerCornerCols;
            _recipeCalibBoardRows = defaults.BoardRows > 0
                ? defaults.BoardRows
                : ChessboardCalibrationDefaults.InnerCornerRows;
            if (!string.IsNullOrWhiteSpace(defaults.SquareSizeMm))
                _recipeCalibSquareSizeMm = defaults.SquareSizeMm;
            if (!string.IsNullOrWhiteSpace(defaults.PxPerMm))
                _recipeCalibPxPerMm = defaults.PxPerMm;
            if (!string.IsNullOrWhiteSpace(defaults.PerspectiveOutputFrame))
                _recipePerspectiveOutputFrame = defaults.PerspectiveOutputFrame;
            if (!string.IsNullOrWhiteSpace(defaults.ChessboardCalibrationJsonPath))
                _recipeChessboardCalibPath = defaults.ChessboardCalibrationJsonPath;
            if (!string.IsNullOrWhiteSpace(defaults.NinePointCalibrationJsonPath))
                _recipeNinePointCalibPath = defaults.NinePointCalibrationJsonPath;
#if HALCON_ENABLED
            InvalidateNinePointAffineCache();
#endif
            RefreshRecipeCalibrationSummaryUi();
        }

        /// <summary>流程配方切换后：强制用该配方全局标定/主流程矫正参数刷新本页。</summary>
        internal void SyncRecipeGlobalCalibrationFromHost(bool forceCorrectionParams = true)
        {
            if (!IsLoaded)
                return;

            bool prev = _suppressSessionPersist;
            _suppressSessionPersist = true;
            try
            {
                ApplyRecipeGlobalCalibrationDefaults(forceCorrectionParams);
                TryReapplyCameraCorrectionAfterSessionRestore();
            }
            finally
            {
                _suppressSessionPersist = prev;
            }

            if (_uiReady)
                PersistSessionChange();
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

            if (ChkRotateExpandCanvas != null)
            {
                ChkRotateExpandCanvas.Checked += (_, _) => ScheduleSessionPersist();
                ChkRotateExpandCanvas.Unchecked += (_, _) => ScheduleSessionPersist();
            }

            if (TxtPostCorrectRotateDeg != null)
                TxtPostCorrectRotateDeg.LostFocus += (_, _) => ScheduleSessionPersist();
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

            // 启动/初始化阶段或未加载图时，不要用空 ImagePath 覆盖磁盘上已有会话；仍可写入当前参数。
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                HalconShapeModelUiSettings existing = HalconShapeModelUiSettings.Load();
                if (!string.IsNullOrWhiteSpace(existing.ImagePath))
                {
                    existing.Controls = PageUiBinder.Capture(this, HalconShapeModelUiSettings.PersistControlNameList);
                    existing.LastParamRecipeId = (CmbParamRecipe?.SelectedItem as ShapeParamRecipeListItem)?.Recipe.Id
                        ?? _lastParamRecipeId
                        ?? "";
                    existing.LastModelPath = _sessionModelPath ?? "";
                    existing.LastModelDeformable = _sessionModelDeformable;
                    existing.Save();
                    return;
                }
            }

            var s = new HalconShapeModelUiSettings
            {
                ImagePath = imagePath,
                CalibrationJsonPath = _recipeChessboardCalibPath?.Trim() ?? "",
                NinePointCalibrationPath = _recipeNinePointCalibPath?.Trim() ?? "",
            };
            CaptureRoiIntoSettings(s);
            CaptureCameraCorrectionIntoSettings(s);
            s.Controls = PageUiBinder.Capture(this, HalconShapeModelUiSettings.PersistControlNameList);
            s.LastParamRecipeId = (CmbParamRecipe?.SelectedItem as ShapeParamRecipeListItem)?.Recipe.Id
                ?? _lastParamRecipeId
                ?? "";
            s.LastModelPath = _sessionModelPath ?? "";
            s.LastModelDeformable = _sessionModelDeformable;
            s.Save();
        }

        private void CaptureCameraCorrectionIntoSettings(HalconShapeModelUiSettings s)
        {
            s.EnableUndistort = _recipeEnableUndistort;
            s.UndistortAlpha = _recipeUndistortAlpha ?? "-1";
            s.EnablePerspective = _recipeEnablePerspective;
            s.CalibViewIndex = _recipeCalibViewIndex;
            s.CalibBoardCols = _recipeCalibBoardCols;
            s.CalibBoardRows = _recipeCalibBoardRows;
            s.CalibSquareSizeMm = _recipeCalibSquareSizeMm
                ?? ChessboardCalibrationDefaults.SquareSizeMm.ToString(CultureInfo.InvariantCulture);
            s.CalibPxPerMm = _recipeCalibPxPerMm
                ?? ChessboardCalibrationDefaults.PxPerMm.ToString(CultureInfo.InvariantCulture);
            s.PerspectiveOutputFrame = string.IsNullOrWhiteSpace(_recipePerspectiveOutputFrame)
                ? ChessboardCalibrationDefaults.PerspectiveOutputFrame
                : _recipePerspectiveOutputFrame;
            s.PostCorrectRotateDeg = _postCorrectRotateDeg;
            s.RotateExpandCanvas = ChkRotateExpandCanvas?.IsChecked == true;
        }

        private void ApplyCameraCorrectionFromSettings(HalconShapeModelUiSettings s)
        {
            // 矫正参数以配方为准；会话只恢复本页显示用的旋转等
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
            UpdateOptionDependentPanelsVisibility();
        }

        private void TryReapplyCameraCorrectionAfterSessionRestore()
        {
            if (_rawCalibImage == null)
                return;
            if (!_recipeEnableUndistort && !_recipeEnablePerspective)
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
            if (!string.IsNullOrWhiteSpace(s.LastModelPath))
            {
                _sessionModelPath = s.LastModelPath;
                _sessionModelDeformable = s.LastModelDeformable;
            }
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
                ApplyCameraCorrectionFromSettings(s);
                ApplyRecipeGlobalCalibrationDefaults(forceCorrectionParams: true);

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
            if (HasUsableRotatedRectRoi())
                return "rotatedrect";
            return "none";
        }

        private void CaptureRoiIntoSettings(HalconShapeModelUiSettings s)
        {
            s.RoiCoordinateUnit = RoiUsesMillimeters()
                ? RoiSnapshotCoordinateTransform.UnitMillimeter
                : RoiSnapshotCoordinateTransform.UnitPixel;
            s.RoiMode = CaptureCurrentRoiMode();
            s.RoiRectX = _rotRectCenterX - _rotRectWidth * 0.5;
            s.RoiRectY = _rotRectCenterY - _rotRectHeight * 0.5;
            s.RoiRectW = _rotRectWidth;
            s.RoiRectH = _rotRectHeight;
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

            CaptureOpenTrajHistoryIntoSettings(s);
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
            bool sourceMm = string.Equals(
                s.RoiCoordinateUnit, RoiSnapshotCoordinateTransform.UnitMillimeter, StringComparison.OrdinalIgnoreCase);
            if (RoiUsesMillimeters() && !sourceMm
                && TryGetNinePointAffine(out AffineTransform toMm, out _))
            {
                s = RoiSnapshotCoordinateTransform.ConvertPixelSettingsToMillimeters(
                    s, toMm, IsPreserveRoiAngleEnabled());
            }
            else if (!RoiUsesMillimeters() && sourceMm
                && TryGetNinePointAffine(out AffineTransform toPx, out _))
            {
                s = RoiSnapshotCoordinateTransform.ConvertMillimeterSettingsToPixels(
                    s, toPx, IsPreserveRoiAngleEnabled());
            }

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
            ClearOpenTrajHistory();
            ClearPathVertexRedo();
            ResetCircleDrawState();

            switch (s.RoiMode)
            {
                case "rect":
                    if (s.RoiRectW > RoiMinExtent() && s.RoiRectH > RoiMinExtent())
                    {
                        ApplyAxisAlignedRectAsRotatedRoi(s.RoiRectX, s.RoiRectY, s.RoiRectW, s.RoiRectH);
                        _hasRoi = true;
                    }
                    break;
                case "rotatedrect":
                    if (s.RotRectWidth > RoiMinExtent() && s.RotRectHeight > RoiMinExtent())
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
                    if (s.CircleRadius > RoiMinExtent())
                    {
                        _circleCenterImage = new Point(s.CircleCenterX, s.CircleCenterY);
                        _circleRadiusImage = s.CircleRadius;
                        ClampCircleRadiusToImage();
                        _hasRoi = _circleRadiusImage > RoiMinExtent();
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
                    ApplyOpenTrajHistoryFromSettings(s);
                    break;
            }

            if (!string.Equals(s.RoiMode, "opentrajectories", StringComparison.OrdinalIgnoreCase))
            {
                // 非开放轨迹模式不携带历史
                ClearOpenTrajHistory();
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
            RefreshOpenTrajectoryListUi();
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
                case "rect":
                default:
                    RbRotatedRectMode.IsChecked = true;
                    break;
            }
        }

        private void ApplyAxisAlignedRectAsRotatedRoi(double left, double top, double width, double height)
        {
            _rotRectCenterX = left + width * 0.5;
            _rotRectCenterY = top + height * 0.5;
            _rotRectWidth = width;
            _rotRectHeight = height;
            _rotRectAngleDeg = 0;
            _rotRectSettingAngle = false;
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

        private static string SessionCachedModelPath(bool deformable)
        {
            string dir = IoPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppProduct.AppDataFolderName);
            return IoPath.Combine(dir, deformable ? "halcon_shape_model_session.dfm" : "halcon_shape_model_session.shm");
        }

        private void RememberAndCacheCurrentModel()
        {
#if HALCON_ENABLED
            if (_modelId < 0)
                return;
            try
            {
                bool deformable = _modelKind == HalconFlowModelKind.Deformable;
                string path = SessionCachedModelPath(deformable);
                string? dir = IoPath.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                if (deformable)
                    HalconFlowBridge.WriteDeformableModelToFile(_modelId, path);
                else
                    HalconFlowBridge.WriteShapeModelToFile(_modelId, path);
                _sessionModelPath = path;
                _sessionModelDeformable = deformable;
                PersistSessionChange();
            }
            catch (Exception ex)
            {
                AppendLog($"[会话] 未能缓存当前模板: {ex.Message}");
            }
#endif
        }

        private void TryRestoreCachedModelIfMissing()
        {
#if HALCON_ENABLED
            if (BtnTestFind != null)
                BtnTestFind.IsEnabled = _modelId >= 0 && HalconFlowBridge.TryGetRegisteredModelKind(_modelId, out _);

            if (_modelId >= 0 && HalconFlowBridge.TryGetRegisteredModelKind(_modelId, out _))
                return;

            string path = _sessionModelPath;
            bool deformable = _sessionModelDeformable;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                HalconShapeModelUiSettings s = HalconShapeModelUiSettings.Load();
                path = s.LastModelPath ?? "";
                deformable = s.LastModelDeformable;
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            try
            {
                if (_modelId >= 0)
                {
                    try { HalconFlowBridge.ClearModel(_modelId); } catch { /* ignored */ }
                    _modelId = -1;
                }

                string ext = IoPath.GetExtension(path);
                if (deformable || string.Equals(ext, ".dfm", StringComparison.OrdinalIgnoreCase))
                {
                    _modelId = HalconFlowBridge.LoadDeformableModelFromFile(path);
                    _modelKind = HalconFlowModelKind.Deformable;
                    _sessionModelDeformable = true;
                }
                else
                {
                    _modelId = HalconFlowBridge.LoadShapeModelFromFile(path);
                    _modelKind = HalconFlowModelKind.Shape;
                    _sessionModelDeformable = false;
                }

                _sessionModelPath = path;
                if (BtnTestFind != null)
                    BtnTestFind.IsEnabled = _modelId >= 0;
                if (TxtModelInfo != null && string.IsNullOrWhiteSpace(TxtModelInfo.Text))
                    TxtModelInfo.Text = $"已恢复上次模板\n文件: {path}\nModelID: {_modelId}";
                AppendLog($"已恢复上次模板: {IoPath.GetFileName(path)} (ModelID={_modelId})");
            }
            catch (Exception ex)
            {
                AppendLog($"[会话] 恢复模板失败: {ex.Message}");
            }
#endif
        }
    }
}
