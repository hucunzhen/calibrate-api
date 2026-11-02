using System;
using System.Globalization;
using System.Windows;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    public partial class RoiTangentArcParamsDialog : Window
    {
        private bool _syncing;
        private AffineTransform? _affine;
        private double _junctionCol;
        private double _junctionRow;
        private const bool PreserveAngle = true;

        public double RadiusPx { get; private set; }
        public double RadiusMm { get; private set; }
        public double SweepDegrees { get; private set; }

        public RoiTangentArcParamsDialog()
        {
            InitializeComponent();
            TxtRadiusPx.TextChanged += (_, _) => OnRadiusPxChanged();
            TxtRadiusMm.TextChanged += (_, _) => OnRadiusMmChanged();
            TxtSweepDeg.TextChanged += (_, _) => UpdateArcLengthHint();
        }

        /// <param name="radiusRoi">路径坐标下的半径：有九点时为 mm，否则为像素。不要先换成叠图像素。</param>
        public void Initialize(double junctionCol, double junctionRow, double radiusRoi, double sweepDeg, AffineTransform? affine)
        {
            _junctionCol = junctionCol;
            _junctionRow = junctionRow;
            _affine = affine;
            SweepDegrees = Math.Clamp(sweepDeg, 0.1, 359.9);
            if (affine is AffineTransform aff)
            {
                RadiusMm = Math.Max(1e-6, radiusRoi);
                RadiusPx = Math.Max(0.5, RoiWorldPixelConvert.MmLengthToPixels(RadiusMm, aff));
            }
            else
            {
                RadiusPx = Math.Max(0.5, radiusRoi);
                RadiusMm = 0;
            }

            _syncing = true;
            TxtRadiusPx.Text = RadiusPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtRadiusMm.Text = FormatRadiusMm(RadiusMm);
            TxtSweepDeg.Text = SweepDegrees.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;

            bool hasAffine = affine is AffineTransform;
            TxtRadiusMm.IsReadOnly = !hasAffine;
            TxtRadiusPx.IsReadOnly = hasAffine;
            TxtMmStatus.Text = hasAffine
                ? "半径在 mm 里计算；像素框只是叠图显示，确定时不用像素反算。"
                : "未加载九点标定文件，仅可编辑像素；mm 显示为 —。";
            UpdateArcLengthHint();
        }

        private void OnRadiusPxChanged()
        {
            if (_syncing || _affine is AffineTransform)
                return;
            if (!TryParse(TxtRadiusPx, out double px) || px < 0.5)
                return;
            RadiusPx = px;
            SyncRadiusMmFromPx();
            _syncing = true;
            TxtRadiusMm.Text = FormatRadiusMm(RadiusMm);
            _syncing = false;
            UpdateArcLengthHint();
        }

        private void OnRadiusMmChanged()
        {
            if (_syncing || _affine is not AffineTransform)
                return;
            if (!TryParse(TxtRadiusMm, out double mm) || mm <= 0)
                return;
            ApplyRadiusMm(mm);
            _syncing = true;
            TxtRadiusPx.Text = RadiusPx.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;
            UpdateArcLengthHint();
        }

        private void ApplyRadiusMm(double mm)
        {
            RadiusMm = mm;
            if (_affine is AffineTransform aff)
                RadiusPx = Math.Max(0.5, RoiWorldPixelConvert.MmLengthToPixels(mm, aff));
        }

        private void SyncRadiusMmFromPx()
        {
            if (_affine is not AffineTransform aff)
            {
                RadiusMm = 0;
                return;
            }

            RadiusMm = RoiWorldPixelConvert.PixelLengthToMm(RadiusPx, aff);
        }

        private void UpdateArcLengthHint()
        {
            if (!TryParse(TxtSweepDeg, out double deg) || deg < 0.1)
            {
                TxtArcLengthHint.Text = "弧长 —";
                return;
            }

            SweepDegrees = Math.Min(359.9, deg);
            if (RadiusPx < 0.5)
            {
                TxtArcLengthHint.Text = "弧长 —";
                return;
            }

            double arcLenPx = RadiusPx * SweepDegrees * Math.PI / 180.0;
            if (RadiusMm <= 0)
            {
                TxtArcLengthHint.Text = $"弧长 ≈ {arcLenPx:F1} px";
                return;
            }

            double arcLenMm = RadiusMm * SweepDegrees * Math.PI / 180.0;
            TxtArcLengthHint.Text = $"弧长 ≈ {arcLenPx:F1} px / {arcLenMm:F4} mm";
        }

        private static string FormatRadiusMm(double radiusMm) =>
            radiusMm <= 0 ? "—" : radiusMm.ToString("F4", CultureInfo.InvariantCulture);

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (_affine is AffineTransform && TryParse(TxtRadiusMm, out double mm) && mm > 0)
                ApplyRadiusMm(mm);
            else if (TryParse(TxtRadiusPx, out double px) && px >= 0.5)
            {
                RadiusPx = px;
                SyncRadiusMmFromPx();
            }
            else
            {
                MessageBox.Show(this, "请输入有效的半径（像素 ≥ 0.5）。", "相切圆弧", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!TryParse(TxtSweepDeg, out double deg) || deg < 0.1 || deg > 359.9)
            {
                MessageBox.Show(this, "张角须在 0.1°～359.9° 之间。", "相切圆弧", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            SweepDegrees = deg;
            DialogResult = true;
        }

        private static bool TryParse(System.Windows.Controls.TextBox box, out double v) =>
            double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
