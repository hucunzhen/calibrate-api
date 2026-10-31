using System;

using System.IO;

using System.Windows;
using CalibOperatorPInvoke;
using Microsoft.Win32;

using IoPath = System.IO.Path;



namespace CalibOperatorCLI_Example

{

    public partial class HalconShapeModelPage

    {

        private string? _lastRoiFilePath;

        private static bool IsPreserveRoiAngleEnabled() => true;



        private RoiSnapshotDocument CaptureRoiSnapshotFromUiInMillimeters(out string ninePointPath)

        {

            if (!TryGetNinePointAffine(out AffineTransform affine, out string affineError))

                throw new InvalidOperationException(affineError);



            ninePointPath = _recipeNinePointCalibPath?.Trim() ?? "";

            var pixelSettings = new HalconShapeModelUiSettings();

            CaptureRoiIntoSettings(pixelSettings);

            HalconShapeModelUiSettings mmSettings = RoiUsesMillimeters()
                ? pixelSettings
                : RoiSnapshotCoordinateTransform.ConvertPixelSettingsToMillimeters(
                    pixelSettings, affine, IsPreserveRoiAngleEnabled());



            RoiSnapshotDocument doc = RoiSnapshotFileIo.FromSettings(

                mmSettings,

                _loadedImagePath,

                _imgWidth,

                _imgHeight,

                RoiSnapshotCoordinateTransform.UnitMillimeter,

                ninePointPath);
            doc.PreserveRoiAngle = true;
            return doc;

        }



        private HalconShapeModelUiSettings ResolvePixelSettingsFromSnapshot(RoiSnapshotDocument doc)

        {

            HalconShapeModelUiSettings stored = RoiSnapshotFileIo.ToSettings(doc);

            if (!RoiSnapshotCoordinateTransform.IsMillimeterDocument(doc))
            {
                stored.RoiCoordinateUnit = RoiSnapshotCoordinateTransform.UnitPixel;
                return stored;
            }

            stored.RoiCoordinateUnit = RoiSnapshotCoordinateTransform.UnitMillimeter;

            if (!TryGetNinePointAffine(out AffineTransform affine, out string affineError))

                throw new InvalidOperationException($"ROI 文件为 mm 坐标，需要有效的九点标定文件。\n{affineError}");



            // 内存保持 mm；叠图/HALCON 再换成像素。切勿把 mm 当像素再转一次。
            if (RoiUsesMillimeters())
                return stored;

            stored.RoiCoordinateUnit = RoiSnapshotCoordinateTransform.UnitPixel;
            return RoiSnapshotCoordinateTransform.ConvertMillimeterSettingsToPixels(
                stored, affine, preserveAngle: true);

        }



        private void ApplyRoiSnapshotDocument(RoiSnapshotDocument doc, string filePath)

        {

            HalconShapeModelUiSettings pixelSettings = ResolvePixelSettingsFromSnapshot(doc);



            _suppressDrawModeClear = true;

            try

            {

                ApplyRoiFromSettings(pixelSettings);

            }

            finally

            {

                _suppressDrawModeClear = false;

            }



            _lastRoiFilePath = filePath;

            UpdateDrawToolbarForMode();

            UpdateRoiManagePanelVisibility();

            PersistSessionChange();



            string unitLabel = RoiSnapshotCoordinateTransform.IsMillimeterDocument(doc)
                ? (RoiUsesMillimeters() ? "mm，叠图像素" : "mm→px")
                : "px";

            string modeLabel = DescribeRoiMode(doc.RoiMode);

            AppendLog($"已加载 ROI: {IoPath.GetFileName(filePath)}（{modeLabel}，{unitLabel}）");

        }



        private static string DescribeRoiMode(string? mode) => mode switch

        {

            "rect" => "矩形",

            "rotatedrect" => "矩形",

            "circle" => "圆形",

            "polygon" => "多边形",

            "ring" => "环形",

            "opentrajectories" => "开放轨迹",

            _ => "无 ROI"

        };



        private void TryLoadLinkedImageFromRoiSnapshot(RoiSnapshotDocument doc, string roiFilePath)

        {

            string? imagePath = ResolveRoiLinkedImagePath(doc, roiFilePath);

            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))

            {

                if (!string.IsNullOrWhiteSpace(doc.ImagePath))

                    AppendLog($"关联图像不存在: {doc.ImagePath}");

                return;

            }



            bool sizeMismatch = _imgWidth > 0 && _imgHeight > 0

                                && (doc.ImageWidth > 0 && doc.ImageWidth != _imgWidth

                                    || doc.ImageHeight > 0 && doc.ImageHeight != _imgHeight);



            string msg = sizeMismatch

                ? $"ROI 文件关联图像与当前显示尺寸不一致。\n文件: {doc.ImageWidth}×{doc.ImageHeight}\n当前: {_imgWidth}×{_imgHeight}\n是否仍要加载该图像？"

                : $"是否加载 ROI 文件关联的图像？\n{imagePath}";



            if (MessageBox.Show(msg, "加载图像", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)

                return;



            try

            {

                LoadImage(imagePath);

                AppendLog($"已加载关联图像: {IoPath.GetFileName(imagePath)}");

            }

            catch (Exception ex)

            {

                MessageBox.Show($"加载图像失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);

            }

        }



        private static string? ResolveRoiLinkedImagePath(RoiSnapshotDocument doc, string roiFilePath)

        {

            if (string.IsNullOrWhiteSpace(doc.ImagePath))

                return null;



            if (File.Exists(doc.ImagePath))

                return IoPath.GetFullPath(doc.ImagePath);



            string? roiDir = IoPath.GetDirectoryName(roiFilePath);

            if (!string.IsNullOrEmpty(roiDir))

            {

                string relative = IoPath.Combine(roiDir, doc.ImagePath);

                if (File.Exists(relative))

                    return IoPath.GetFullPath(relative);

            }



            return doc.ImagePath;

        }



        private bool TryPrepareOpenTrajectoryDraftBeforeRoiSave()

        {

            if (!IsOpenTrajectoriesModeActive() || _roiPath.VertexCount < 2 || _editingOpenTrajectoryIndex != null)

                return true;



            if (MessageBox.Show("当前开放轨迹草稿尚未「完成本条」，是否先并入 ROI 再保存？", "保存 ROI",

                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)

                return true;



            return TryCommitCurrentOpenTrajectoryDraft(showMessage: false);

        }



        private void BtnSaveRoi_Click(object sender, RoutedEventArgs e)

        {

            if (!TryPrepareOpenTrajectoryDraftBeforeRoiSave())

                return;



            if (!TryGetNinePointAffine(out _, out string affineError))

            {

                MessageBox.Show(

                    $"保存 ROI 需要有效的九点标定文件，以便将坐标写入 mm。\n{affineError}",

                    "保存 ROI",

                    MessageBoxButton.OK,

                    MessageBoxImage.Information);

                return;

            }



            try

            {

                RoiSnapshotDocument preview = CaptureRoiSnapshotFromUiInMillimeters(out _);

                if (preview.RoiMode == "none")

                {

                    MessageBox.Show("当前没有可保存的 ROI。请先绘制矩形、圆、多边形、环形或开放轨迹。", "保存 ROI",

                        MessageBoxButton.OK, MessageBoxImage.Information);

                    return;

                }

            }

            catch (Exception ex)

            {

                MessageBox.Show($"无法换算 mm 坐标:\n{ex.Message}", "保存 ROI", MessageBoxButton.OK, MessageBoxImage.Error);

                return;

            }



            string defaultName = "roi_snapshot.xvroi.json";
            string? initialDir = null;
            if (SelectedParamRecipe != null
                && TryGetParamRecipeRoiPath(SelectedParamRecipe, out string canonicalPath, out _))
            {
                defaultName = IoPath.GetFileName(canonicalPath);
                string? dir = IoPath.GetDirectoryName(canonicalPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    initialDir = dir;
                }
            }
            else if (!string.IsNullOrWhiteSpace(_lastRoiFilePath))
            {
                defaultName = IoPath.GetFileName(_lastRoiFilePath);
                string? dir = IoPath.GetDirectoryName(_lastRoiFilePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    initialDir = dir;
            }

            var dlg = new SaveFileDialog
            {
                Filter = RoiSnapshotFileIo.FileFilter,
                Title = "保存 ROI（mm 坐标）",
                FileName = defaultName
            };
            if (!string.IsNullOrEmpty(initialDir))
                dlg.InitialDirectory = initialDir;



            if (dlg.ShowDialog() != true)

                return;



            try

            {

                RoiSnapshotDocument doc = CaptureRoiSnapshotFromUiInMillimeters(out _);

                RoiSnapshotFileIo.Save(dlg.FileName, doc);

                _lastRoiFilePath = dlg.FileName;

                PersistSessionChange();

                AppendLog($"ROI 已保存(mm): {dlg.FileName}（{DescribeRoiMode(doc.RoiMode)}）");

            }

            catch (Exception ex)

            {

                MessageBox.Show($"保存失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);

            }

        }



        private void BtnLoadRoi_Click(object sender, RoutedEventArgs e)

        {

            var dlg = new OpenFileDialog

            {

                Filter = RoiSnapshotFileIo.FileFilter,

                Title = "加载 ROI"

            };



            if (!string.IsNullOrWhiteSpace(_lastRoiFilePath))

            {

                string? dir = IoPath.GetDirectoryName(_lastRoiFilePath);

                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))

                    dlg.InitialDirectory = dir;

            }



            if (dlg.ShowDialog() != true)

                return;



            try

            {

                RoiSnapshotDocument doc = RoiSnapshotFileIo.Load(dlg.FileName);

                if (RoiSnapshotCoordinateTransform.IsMillimeterDocument(doc)

                    && !string.IsNullOrWhiteSpace(doc.NinePointCalibrationPath)

                    && string.IsNullOrWhiteSpace(_recipeNinePointCalibPath)

                    && File.Exists(doc.NinePointCalibrationPath))

                {

                    // ROI 文件自带路径时仅作回退；正常以配方 camera_correction.json 为准

                    _recipeNinePointCalibPath = doc.NinePointCalibrationPath;

                    InvalidateNinePointAffineCache();

                    RefreshRecipeCalibrationSummaryUi();

                }



                TryLoadLinkedImageFromRoiSnapshot(doc, dlg.FileName);

                ApplyRoiSnapshotDocument(doc, dlg.FileName);

            }

            catch (Exception ex)

            {

                MessageBox.Show($"加载失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);

            }

        }



        private void UpdateRoiManagePanelVisibility()

        {

            if (PnlRoiManage == null)

                return;



            PnlRoiManage.Visibility = Visibility.Visible;



            bool openMode = IsOpenTrajectoriesModeActive();

            if (PnlOpenTrajectoryListSection != null)

                PnlOpenTrajectoryListSection.Visibility = openMode ? Visibility.Visible : Visibility.Collapsed;



            if (openMode)

                RefreshOpenTrajectoryListUi();

        }

    }

}


