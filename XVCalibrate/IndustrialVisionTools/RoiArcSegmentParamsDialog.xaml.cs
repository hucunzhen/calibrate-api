using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>编辑已有圆弧段：改半径时保持圆心角与起点，终点与弧上点绕起点缩放。</summary>
    public partial class RoiArcSegmentParamsDialog : Window
    {
        private bool _syncing;
        private AffineTransform? _affine;
        private double _startCol;
        private double _startRow;
        private double _endCol;
        private double _endRow;
        private double _viaCol;
        private double _viaRow;
        private double _radiusRoi;

        public double ViaColumnPx { get; private set; }
        public double ViaRowPx { get; private set; }
        public double EndColumnPx { get; private set; }
        public double EndRowPx { get; private set; }
        public double RadiusPx { get; private set; }

        public RoiArcSegmentParamsDialog()
        {
            InitializeComponent();
            TxtViaColPx.TextChanged += (_, _) => OnViaChanged();
            TxtViaRowPx.TextChanged += (_, _) => OnViaChanged();
            TxtRadiusPx.TextChanged += (_, _) => OnRadiusPxChanged();
            TxtRadiusMm.TextChanged += (_, _) => OnRadiusMmChanged();
        }

        public void Initialize(
            double startCol,
            double startRow,
            double endCol,
            double endRow,
            double viaCol,
            double viaRow,
            AffineTransform? affine)
        {
            _startCol = startCol;
            _startRow = startRow;
            _endCol = endCol;
            _endRow = endRow;
            _affine = affine;
            _viaCol = viaCol;
            _viaRow = viaRow;
            ViaColumnPx = viaCol;
            ViaRowPx = viaRow;
            EndColumnPx = endCol;
            EndRowPx = endRow;

            var start = new Point(startCol, startRow);
            var end = new Point(endCol, endRow);
            var via = new Point(viaCol, viaRow);
            double minR = affine is AffineTransform ? 1e-6 : 0.5;
            if (!RoiContourPath.TryGetArcGeometricRadius(start, via, end, out double r) || r < minR)
                r = Math.Max(minR, Dist(start, end) * 0.5 + minR);
            _radiusRoi = r;
            SyncRadiusDisplay();

            _syncing = true;
            TxtViaColPx.Text = ViaColumnPx.ToString("F4", CultureInfo.InvariantCulture);
            TxtViaRowPx.Text = ViaRowPx.ToString("F4", CultureInfo.InvariantCulture);
            TxtRadiusPx.Text = RadiusPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtRadiusMm.Text = FormatRadiusMm();
            _syncing = false;

            RefreshChordHint();

            bool hasAffine = affine is AffineTransform;
            TxtRadiusMm.IsReadOnly = !hasAffine;
            TxtRadiusPx.IsReadOnly = hasAffine;
            TxtMmStatus.Text = hasAffine
                ? "坐标与半径都在 mm 里计算；像素框只是叠图显示。"
                : "未加载九点标定文件，仅可编辑像素；mm 显示为 —。改半径时起点与圆心角不变。";
        }

        private void OnViaChanged()
        {
            if (_syncing)
                return;
            if (!TryParse(TxtViaColPx, out double col) || !TryParse(TxtViaRowPx, out double row))
                return;

            var start = new Point(_startCol, _startRow);
            var end = new Point(_endCol, _endRow);
            var via = new Point(col, row);
            if (!RoiContourPath.IsValidArcVia(start, via, end))
                return;
            double minR = _affine is AffineTransform ? 1e-6 : 0.5;
            if (!RoiContourPath.TryGetArcGeometricRadius(start, via, end, out double r) || r < minR)
                return;

            _viaCol = col;
            _viaRow = row;
            ViaColumnPx = col;
            ViaRowPx = row;
            _radiusRoi = r;
            SyncRadiusDisplay();
            _syncing = true;
            TxtRadiusPx.Text = RadiusPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtRadiusMm.Text = FormatRadiusMm();
            _syncing = false;
        }

        private void OnRadiusPxChanged()
        {
            if (_syncing || _affine is AffineTransform)
                return;
            if (!TryParse(TxtRadiusPx, out double px) || px < 0.5)
                return;

            if (!TryApplyRadius(px))
                return;

            _syncing = true;
            TxtRadiusMm.Text = FormatRadiusMm();
            TxtViaColPx.Text = ViaColumnPx.ToString("F4", CultureInfo.InvariantCulture);
            TxtViaRowPx.Text = ViaRowPx.ToString("F4", CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private void OnRadiusMmChanged()
        {
            if (_syncing || _affine is not AffineTransform)
                return;
            if (!TryParse(TxtRadiusMm, out double mm) || mm <= 0)
                return;

            if (!TryApplyRadius(mm))
                return;

            _syncing = true;
            TxtRadiusPx.Text = RadiusPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtViaColPx.Text = ViaColumnPx.ToString("F4", CultureInfo.InvariantCulture);
            TxtViaRowPx.Text = ViaRowPx.ToString("F4", CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private bool TryApplyRadius(double radiusPx)
        {
            var start = new Point(_startCol, _startRow);
            var end = new Point(_endCol, _endRow);
            var via = new Point(_viaCol, _viaRow);
            if (!RoiContourPath.TryResizeArcKeepSweepFromStart(start, via, end, radiusPx, out Point newVia, out Point newEnd))
                return false;

            _radiusRoi = radiusPx;
            SyncRadiusDisplay();
            _viaCol = newVia.X;
            _viaRow = newVia.Y;
            ViaColumnPx = newVia.X;
            ViaRowPx = newVia.Y;
            _endCol = newEnd.X;
            _endRow = newEnd.Y;
            EndColumnPx = newEnd.X;
            EndRowPx = newEnd.Y;
            RefreshChordHint();
            return true;
        }

        private void RefreshChordHint()
        {
            if (_affine is AffineTransform)
            {
                TxtChordHint.Text =
                    $"弦：({_startCol:F4},{_startRow:F4}) → ({_endCol:F4},{_endRow:F4}) mm（改半径时终点会动）";
            }
            else
            {
                TxtChordHint.Text =
                    $"弦：({_startCol:F1},{_startRow:F1}) → ({_endCol:F1},{_endRow:F1}) px（改半径时终点会动）";
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (_affine is AffineTransform
                && TryParse(TxtRadiusMm, out double mm) && mm > 0)
            {
                if (!TryApplyRadius(mm))
                {
                    MessageBox.Show(this, "半径无效，无法保持当前圆心角。", "编辑圆弧段",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            else
            {
                if (!TryParse(TxtViaColPx, out double col) || !TryParse(TxtViaRowPx, out double row))
                {
                    MessageBox.Show(this, "请输入有效的弧上点坐标。", "编辑圆弧段", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var start = new Point(_startCol, _startRow);
                var end = new Point(_endCol, _endRow);
                var via = new Point(col, row);
                if (!RoiContourPath.IsValidArcVia(start, via, end))
                {
                    MessageBox.Show(this, "弧上点与两端共线，无法构成圆弧。", "编辑圆弧段", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double minR = _affine is AffineTransform ? 1e-6 : 0.5;
                if (!RoiContourPath.TryGetArcGeometricRadius(start, via, end, out double r) || r < minR)
                {
                    MessageBox.Show(this, "无法由三点求出有效半径。", "编辑圆弧段", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                ViaColumnPx = col;
                ViaRowPx = row;
                EndColumnPx = _endCol;
                EndRowPx = _endRow;
                _radiusRoi = r;
                SyncRadiusDisplay();
            }

            DialogResult = true;
        }

        private void SyncRadiusDisplay()
        {
            if (_affine is AffineTransform aff)
                RadiusPx = RoiWorldPixelConvert.MmLengthToPixels(_radiusRoi, aff);
            else
                RadiusPx = _radiusRoi;
        }

        private string FormatRadiusMm()
        {
            if (_affine is not AffineTransform)
                return "—";
            return _radiusRoi.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static bool TryParse(TextBox? box, out double value)
        {
            value = 0;
            return box != null
                   && double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static double Dist(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
