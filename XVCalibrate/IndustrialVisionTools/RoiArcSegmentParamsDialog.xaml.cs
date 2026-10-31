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
            if (!RoiContourPath.TryGetArcGeometricRadius(start, via, end, out double r) || r < 0.5)
                r = Math.Max(0.5, Dist(start, end) * 0.5 + 1);
            RadiusPx = r;

            _syncing = true;
            TxtViaColPx.Text = ViaColumnPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtViaRowPx.Text = ViaRowPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtRadiusPx.Text = RadiusPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtRadiusMm.Text = FormatRadiusMm(RadiusPx);
            _syncing = false;

            RefreshChordHint();

            bool hasAffine = affine is AffineTransform;
            TxtRadiusMm.IsReadOnly = !hasAffine;
            TxtMmStatus.Text = hasAffine
                ? "半径按九点标定换算 mm；改半径时保持圆心角与起点，终点随半径移动。"
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
            if (!RoiContourPath.TryGetArcGeometricRadius(start, via, end, out double r) || r < 0.5)
                return;

            _viaCol = col;
            _viaRow = row;
            ViaColumnPx = col;
            ViaRowPx = row;
            RadiusPx = r;
            _syncing = true;
            TxtRadiusPx.Text = RadiusPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtRadiusMm.Text = FormatRadiusMm(RadiusPx);
            _syncing = false;
        }

        private void OnRadiusPxChanged()
        {
            if (_syncing)
                return;
            if (!TryParse(TxtRadiusPx, out double px) || px < 0.5)
                return;

            if (!TryApplyRadius(px))
                return;

            _syncing = true;
            TxtRadiusMm.Text = FormatRadiusMm(RadiusPx);
            TxtViaColPx.Text = ViaColumnPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtViaRowPx.Text = ViaRowPx.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private void OnRadiusMmChanged()
        {
            if (_syncing || _affine is not AffineTransform aff)
                return;
            if (!TryParse(TxtRadiusMm, out double mm) || mm <= 0)
                return;

            double midCol = (_startCol + _endCol) * 0.5;
            double midRow = (_startRow + _endRow) * 0.5;
            double px = MmRadiusToPixels(mm, midCol, midRow, aff);
            if (!TryApplyRadius(Math.Max(0.5, px)))
                return;

            _syncing = true;
            TxtRadiusPx.Text = RadiusPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtViaColPx.Text = ViaColumnPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtViaRowPx.Text = ViaRowPx.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private bool TryApplyRadius(double radiusPx)
        {
            var start = new Point(_startCol, _startRow);
            var end = new Point(_endCol, _endRow);
            var via = new Point(_viaCol, _viaRow);
            if (!RoiContourPath.TryResizeArcKeepSweepFromStart(start, via, end, radiusPx, out Point newVia, out Point newEnd))
                return false;

            RadiusPx = radiusPx;
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
            if (_affine is AffineTransform t)
            {
                var ws = CalibAPI.ImageToWorld(new Point2D(_startCol, _startRow), t);
                var we = CalibAPI.ImageToWorld(new Point2D(_endCol, _endRow), t);
                TxtChordHint.Text = $"弦：({ws.X:F2},{ws.Y:F2}) → ({we.X:F2},{we.Y:F2}) mm（改半径时终点会动）";
            }
            else
            {
                TxtChordHint.Text =
                    $"弦：({_startCol:F1},{_startRow:F1}) → ({_endCol:F1},{_endRow:F1}) px（改半径时终点会动）";
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
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

            if (!RoiContourPath.TryGetArcGeometricRadius(start, via, end, out double r) || r < 0.5)
            {
                MessageBox.Show(this, "无法由三点求出有效半径。", "编辑圆弧段", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ViaColumnPx = col;
            ViaRowPx = row;
            EndColumnPx = _endCol;
            EndRowPx = _endRow;
            RadiusPx = r;
            DialogResult = true;
        }

        private string FormatRadiusMm(double radiusPx)
        {
            if (_affine is not AffineTransform aff)
                return "—";
            double midCol = (_startCol + _endCol) * 0.5;
            double midRow = (_startRow + _endRow) * 0.5;
            return PixelRadiusToWorldMm(radiusPx, midCol, midRow, aff)
                .ToString("F3", CultureInfo.InvariantCulture);
        }

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
