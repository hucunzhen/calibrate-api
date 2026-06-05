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

        public double RadiusPx { get; private set; }
        public double SweepDegrees { get; private set; }

        public RoiTangentArcParamsDialog()
        {
            InitializeComponent();
            TxtRadiusPx.TextChanged += (_, _) => OnRadiusPxChanged();
            TxtRadiusMm.TextChanged += (_, _) => OnRadiusMmChanged();
            TxtSweepDeg.TextChanged += (_, _) => UpdateArcLengthHint();
        }

        public void Initialize(double junctionCol, double junctionRow, double radiusPx, double sweepDeg, AffineTransform? affine)
        {
            _junctionCol = junctionCol;
            _junctionRow = junctionRow;
            _affine = affine;
            RadiusPx = Math.Max(0.5, radiusPx);
            SweepDegrees = Math.Clamp(sweepDeg, 0.1, 359.9);

            _syncing = true;
            TxtRadiusPx.Text = RadiusPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtRadiusMm.Text = FormatRadiusMm(RadiusPx);
            TxtSweepDeg.Text = SweepDegrees.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;

            bool hasAffine = affine is AffineTransform;
            TxtRadiusMm.IsReadOnly = !hasAffine;
            TxtMmStatus.Text = hasAffine
                ? "半径/弧长已按九点标定换算 mm。"
                : "未加载九点标定 JSON，仅可编辑像素；mm 显示为 —。";
            UpdateArcLengthHint();
        }

        private void OnRadiusPxChanged()
        {
            if (_syncing)
                return;
            if (!TryParse(TxtRadiusPx, out double px) || px < 0.5)
                return;
            RadiusPx = px;
            _syncing = true;
            TxtRadiusMm.Text = FormatRadiusMm(px);
            _syncing = false;
            UpdateArcLengthHint();
        }

        private void OnRadiusMmChanged()
        {
            if (_syncing || _affine is not AffineTransform aff)
                return;
            if (!TryParse(TxtRadiusMm, out double mm) || mm <= 0)
                return;
            double px = MmRadiusToPixels(mm, _junctionCol, _junctionRow, aff);
            RadiusPx = Math.Max(0.5, px);
            _syncing = true;
            TxtRadiusPx.Text = RadiusPx.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;
            UpdateArcLengthHint();
        }

        private void UpdateArcLengthHint()
        {
            if (!TryParse(TxtSweepDeg, out double deg) || deg < 0.1)
            {
                TxtArcLengthHint.Text = "弧长 —";
                return;
            }

            SweepDegrees = Math.Min(359.9, deg);
            if (!TryParse(TxtRadiusPx, out double radiusPx) || radiusPx < 0.5)
            {
                TxtArcLengthHint.Text = "弧长 —";
                return;
            }

            double arcLenPx = radiusPx * SweepDegrees * Math.PI / 180.0;
            if (_affine is not AffineTransform affLen)
            {
                TxtArcLengthHint.Text = $"弧长 ≈ {arcLenPx:F1} px";
                return;
            }

            double rMm = PixelRadiusToWorldMm(radiusPx, _junctionCol, _junctionRow, affLen);
            double arcLenMm = rMm * SweepDegrees * Math.PI / 180.0;
            TxtArcLengthHint.Text = $"弧长 ≈ {arcLenPx:F1} px / {arcLenMm:F2} mm";
        }

        private string FormatRadiusMm(double radiusPx)
        {
            if (_affine is not AffineTransform aff)
                return "—";
            double mm = PixelRadiusToWorldMm(radiusPx, _junctionCol, _junctionRow, aff);
            return mm.ToString("F3", CultureInfo.InvariantCulture);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParse(TxtRadiusPx, out double px) || px < 0.5)
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

            RadiusPx = px;
            SweepDegrees = deg;
            DialogResult = true;
        }

        private static bool TryParse(System.Windows.Controls.TextBox box, out double v) =>
            double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        private static double PixelRadiusToWorldMm(double radiusPx, double centerCol, double centerRow, in AffineTransform t)
        {
            var c = CalibAPI.ImageToWorld(new Point2D(centerCol, centerRow), t);
            var edge = CalibAPI.ImageToWorld(new Point2D(centerCol + radiusPx, centerRow), t);
            double dx = edge.X - c.X;
            double dy = edge.Y - c.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double MmRadiusToPixels(double radiusMm, double centerCol, double centerRow, in AffineTransform t)
        {
            if (radiusMm <= 0)
                return 0;
            double px = radiusMm;
            for (int i = 0; i < 24; i++)
            {
                double mm = PixelRadiusToWorldMm(px, centerCol, centerRow, t);
                if (mm < 1e-9)
                    break;
                px *= radiusMm / mm;
            }

            return px;
        }
    }
}
