using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CalibOperatorCLI_Example
{
    public partial class RecipeCameraCorrectionDialog : Window
    {
        private readonly string _recipeDirectory;
        private readonly string _recipeName;

        public RecipeCameraCorrectionDialog(string recipeDirectory, string recipeName)
        {
            InitializeComponent();
            _recipeDirectory = Path.GetFullPath(recipeDirectory);
            _recipeName = string.IsNullOrWhiteSpace(recipeName)
                ? Path.GetFileName(_recipeDirectory)
                : recipeName.Trim();

            TxtRecipeHint.Text = $"编辑配方「{_recipeName}」的全局标定与矫正配置。";
            TxtFilePath.Text = RecipeCameraCorrectionProfile.GetProfilePath(_recipeDirectory);

            RecipeCameraCorrectionProfile profile =
                RecipeCameraCorrectionProfile.TryLoadFromRecipeDirectory(_recipeDirectory)
                ?? RecipeCameraCorrectionProfile.CreateBuiltInDefaults();
            LoadProfile(profile);
        }

        private void LoadProfile(RecipeCameraCorrectionProfile profile)
        {
            profile.Normalize();
            TxtChessboardFile.Text = profile.ChessboardCalibrationFile;
            TxtNinePointFile.Text = profile.NinePointCalibrationFile;
            ChkEnableUndistort.IsChecked = profile.EnableUndistort;
            TxtUndistortAlpha.Text = profile.UndistortAlpha;
            ChkEnablePerspective.IsChecked = profile.EnablePerspective;
            TxtViewIndex.Text = profile.ViewIndex.ToString(CultureInfo.InvariantCulture);
            TxtBoardCols.Text = profile.BoardCols.ToString(CultureInfo.InvariantCulture);
            TxtBoardRows.Text = profile.BoardRows.ToString(CultureInfo.InvariantCulture);
            TxtSquareSizeMm.Text = profile.SquareSizeMm;
            TxtPxPerMm.Text = profile.PxPerMm;
            SelectPerspectiveOutputFrame(profile.PerspectiveOutputFrame);
        }

        private void SelectPerspectiveOutputFrame(string? tag)
        {
            string want = string.IsNullOrWhiteSpace(tag)
                ? ChessboardCalibrationDefaults.PerspectiveOutputFrame
                : tag.Trim();
            foreach (var item in CmbPerspectiveOutputFrame.Items)
            {
                if (item is ComboBoxItem cbi
                    && string.Equals(cbi.Tag as string, want, StringComparison.OrdinalIgnoreCase))
                {
                    CmbPerspectiveOutputFrame.SelectedItem = cbi;
                    return;
                }
            }

            if (CmbPerspectiveOutputFrame.Items.Count > 0)
                CmbPerspectiveOutputFrame.SelectedIndex = CmbPerspectiveOutputFrame.Items.Count - 1;
        }

        private string GetPerspectiveOutputFrameTag()
        {
            if (CmbPerspectiveOutputFrame.SelectedItem is ComboBoxItem item
                && item.Tag is string tag
                && !string.IsNullOrWhiteSpace(tag))
                return tag.Trim();
            return ChessboardCalibrationDefaults.PerspectiveOutputFrame;
        }

        private bool TryBuildProfile(out RecipeCameraCorrectionProfile profile, out string error)
        {
            profile = RecipeCameraCorrectionProfile.CreateBuiltInDefaults();
            error = "";

            if (!int.TryParse(TxtViewIndex.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int view)
                || view < 0)
            {
                error = "视图号须为 ≥ 0 的整数。";
                return false;
            }

            if (!int.TryParse(TxtBoardCols.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cols)
                || cols <= 0)
            {
                error = "棋盘列数须为正整数。";
                return false;
            }

            if (!int.TryParse(TxtBoardRows.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rows)
                || rows <= 0)
            {
                error = "棋盘行数须为正整数。";
                return false;
            }

            if (!double.TryParse(TxtSquareSizeMm.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sq)
                || sq <= 0)
            {
                error = "方格边长 mm 须为正数。";
                return false;
            }

            if (!double.TryParse(TxtPxPerMm.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ppm)
                || ppm <= 0)
            {
                error = "px/mm 须为正数。";
                return false;
            }

            profile.EnableUndistort = ChkEnableUndistort.IsChecked == true;
            profile.UndistortAlpha = string.IsNullOrWhiteSpace(TxtUndistortAlpha.Text)
                ? "-1"
                : TxtUndistortAlpha.Text.Trim();
            profile.EnablePerspective = ChkEnablePerspective.IsChecked == true;
            profile.ViewIndex = view;
            profile.BoardCols = cols;
            profile.BoardRows = rows;
            profile.SquareSizeMm = sq.ToString(CultureInfo.InvariantCulture);
            profile.PxPerMm = ppm.ToString(CultureInfo.InvariantCulture);
            profile.PerspectiveOutputFrame = GetPerspectiveOutputFrameTag();
            profile.ChessboardCalibrationFile = string.IsNullOrWhiteSpace(TxtChessboardFile.Text)
                ? @"models\calibration_result.json"
                : TxtChessboardFile.Text.Trim().Replace('/', '\\');
            profile.NinePointCalibrationFile = string.IsNullOrWhiteSpace(TxtNinePointFile.Text)
                ? @"models\calibration_nine_point.json"
                : TxtNinePointFile.Text.Trim().Replace('/', '\\');
            profile.Normalize();
            return true;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!TryBuildProfile(out RecipeCameraCorrectionProfile profile, out string error))
            {
                MessageBox.Show(this, error, "配方标定与矫正", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                profile.SaveToRecipeDirectory(_recipeDirectory);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"保存失败：{ex.Message}", "配方标定与矫正", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
