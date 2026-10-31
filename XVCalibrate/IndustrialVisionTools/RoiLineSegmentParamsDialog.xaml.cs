using System;
using System.Globalization;
using System.Windows;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    public enum RoiLineSegmentDialogOutcome
    {
        Cancel,
        Ok,
        PickStart,
        PickEnd
    }

    public partial class RoiLineSegmentParamsDialog : Window
    {
        private bool _syncing;
        private AffineTransform? _affine;
        private double _anchorCol;
        private double _anchorRow;
        private double _dirX;
        private double _dirY;
        private bool _allowEditStart;

        public double LengthPx { get; private set; }
        public double AngleDegrees { get; private set; }
        public double DirectionX => _dirX;
        public double DirectionY => _dirY;
        public double StartColumnPx { get; private set; }
        public double StartRowPx { get; private set; }
        public double EndColumnPx { get; private set; }
        public double EndRowPx { get; private set; }
        public RoiLineSegmentDialogOutcome Outcome { get; private set; } = RoiLineSegmentDialogOutcome.Cancel;

        public RoiLineSegmentParamsDialog()
        {
            InitializeComponent();
            TxtLengthPx.TextChanged += (_, _) => OnLengthPxChanged();
            TxtLengthMm.TextChanged += (_, _) => OnLengthMmChanged();
            TxtAngleDeg.TextChanged += (_, _) => OnAngleDegChanged();
            TxtStartColPx.TextChanged += (_, _) => OnStartCoordinatesChanged();
            TxtStartRowPx.TextChanged += (_, _) => OnStartCoordinatesChanged();
            TxtEndColPx.TextChanged += (_, _) => OnEndCoordinatesChanged();
            TxtEndRowPx.TextChanged += (_, _) => OnEndCoordinatesChanged();
        }

        public void Initialize(
            double anchorCol,
            double anchorRow,
            double dirX,
            double dirY,
            double lengthPx,
            AffineTransform? affine,
            bool alongTangent,
            bool allowEditStart = false,
            bool endSnappedToLandmark = false)
        {
            _allowEditStart = allowEditStart;
            _anchorCol = anchorCol;
            _anchorRow = anchorRow;
            StartColumnPx = anchorCol;
            StartRowPx = anchorRow;
            SetDirection(dirX, dirY);
            _affine = affine;
            LengthPx = Math.Max(0.5, lengthPx);
            UpdateEndFromPolar();
            Outcome = RoiLineSegmentDialogOutcome.Cancel;

            _syncing = true;
            TxtStartColPx.Text = StartColumnPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtStartRowPx.Text = StartRowPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtLengthPx.Text = LengthPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtLengthMm.Text = FormatLengthMm(LengthPx);
            TxtAngleDeg.Text = AngleDegrees.ToString("F2", CultureInfo.InvariantCulture);
            TxtEndColPx.Text = EndColumnPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtEndRowPx.Text = EndRowPx.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;

            TxtStartColPx.IsReadOnly = !allowEditStart;
            TxtStartRowPx.IsReadOnly = !allowEditStart;
            BtnPickStart.Visibility = allowEditStart ? Visibility.Visible : Visibility.Collapsed;
            BtnPickStart.IsEnabled = allowEditStart;

            if (endSnappedToLandmark)
            {
                TxtDirectionHint.Text = "已吸附附近顶点作为终点；可改长度/角度，或改终点坐标后确定。";
            }
            else
            {
                TxtDirectionHint.Text = alongTangent
                    ? "初始方向沿上一段圆弧切线；可修改角度、段长或起终点坐标。"
                    : allowEditStart
                        ? "可修改起点、角度、段长或终点；也可用「图上选」在画布点选（空白取点，靠近顶点则吸附）。"
                        : "初始方向来自鼠标；靠近已有顶点会吸附为终点；可修改角度、段长或终点坐标。";
            }

            RefreshAnchorHint();

            bool hasAffine = affine is AffineTransform;
            TxtLengthMm.IsReadOnly = !hasAffine;
            TxtMmStatus.Text = hasAffine
                ? "段长已按九点标定换算 mm；若终点落在起点容差内将自动闭合（闭合多边形/环形）。"
                : "未加载九点标定文件，仅可编辑像素；mm 显示为 —。";
        }

        private void RefreshAnchorHint()
        {
            TxtAnchorHint.Text = _affine is AffineTransform t
                ? $"起点 {HalconGeometryContourBuilder.FormatWorldPointMm(_anchorCol, _anchorRow, t)}"
                : $"起点列 {_anchorCol:F1}，行 {_anchorRow:F1}（像素）";
        }

        private void SetDirection(double dirX, double dirY)
        {
            double len = Math.Sqrt(dirX * dirX + dirY * dirY);
            if (len < 1e-9)
            {
                _dirX = 1;
                _dirY = 0;
            }
            else
            {
                _dirX = dirX / len;
                _dirY = dirY / len;
            }

            AngleDegrees = Math.Atan2(_dirY, _dirX) * 180.0 / Math.PI;
        }

        private void SetDirectionFromAngleDegrees(double angleDeg)
        {
            double rad = angleDeg * Math.PI / 180.0;
            _dirX = Math.Cos(rad);
            _dirY = Math.Sin(rad);
            AngleDegrees = angleDeg;
        }

        private void UpdateEndFromPolar()
        {
            EndColumnPx = _anchorCol + _dirX * LengthPx;
            EndRowPx = _anchorRow + _dirY * LengthPx;
        }

        private void UpdatePolarFromEnd()
        {
            double dx = EndColumnPx - _anchorCol;
            double dy = EndRowPx - _anchorRow;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.5)
                len = 0.5;
            LengthPx = len;
            _dirX = dx / len;
            _dirY = dy / len;
            AngleDegrees = Math.Atan2(_dirY, _dirX) * 180.0 / Math.PI;
        }

        private void ApplyStartFromFields(double col, double row)
        {
            _anchorCol = col;
            _anchorRow = row;
            StartColumnPx = col;
            StartRowPx = row;
            UpdatePolarFromEnd();
            RefreshAnchorHint();
        }

        private void OnLengthPxChanged()
        {
            if (_syncing)
                return;
            if (!TryParse(TxtLengthPx, out double px) || px < 0.5)
                return;

            LengthPx = px;
            UpdateEndFromPolar();
            _syncing = true;
            TxtLengthMm.Text = FormatLengthMm(px);
            TxtEndColPx.Text = EndColumnPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtEndRowPx.Text = EndRowPx.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private void OnLengthMmChanged()
        {
            if (_syncing || _affine is not AffineTransform aff)
                return;
            if (!TryParse(TxtLengthMm, out double mm) || mm <= 0)
                return;

            LengthPx = Math.Max(0.5, RoiSegmentMeasure.MmLengthToPixels(mm, _anchorCol, _anchorRow, _dirX, _dirY, aff));
            UpdateEndFromPolar();
            _syncing = true;
            TxtLengthPx.Text = LengthPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtEndColPx.Text = EndColumnPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtEndRowPx.Text = EndRowPx.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private void OnAngleDegChanged()
        {
            if (_syncing)
                return;
            if (!TryParse(TxtAngleDeg, out double deg))
                return;

            SetDirectionFromAngleDegrees(deg);
            UpdateEndFromPolar();
            _syncing = true;
            TxtLengthPx.Text = LengthPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtLengthMm.Text = FormatLengthMm(LengthPx);
            TxtEndColPx.Text = EndColumnPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtEndRowPx.Text = EndRowPx.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private void OnStartCoordinatesChanged()
        {
            if (_syncing || !_allowEditStart)
                return;
            if (!TryParse(TxtStartColPx, out double col) || !TryParse(TxtStartRowPx, out double row))
                return;

            ApplyStartFromFields(col, row);
            _syncing = true;
            TxtLengthPx.Text = LengthPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtLengthMm.Text = FormatLengthMm(LengthPx);
            TxtAngleDeg.Text = AngleDegrees.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private void OnEndCoordinatesChanged()
        {
            if (_syncing)
                return;
            if (!TryParse(TxtEndColPx, out double col) || !TryParse(TxtEndRowPx, out double row))
                return;

            EndColumnPx = col;
            EndRowPx = row;
            UpdatePolarFromEnd();
            _syncing = true;
            TxtLengthPx.Text = LengthPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtLengthMm.Text = FormatLengthMm(LengthPx);
            TxtAngleDeg.Text = AngleDegrees.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private string FormatLengthMm(double lengthPx)
        {
            if (_affine is not AffineTransform aff)
                return "—";

            double mm = RoiSegmentMeasure.PixelLengthToWorldMm(lengthPx, _anchorCol, _anchorRow, _dirX, _dirY, aff);
            return mm.ToString("F3", CultureInfo.InvariantCulture);
        }

        private void BtnPickStart_Click(object sender, RoutedEventArgs e)
        {
            if (!_allowEditStart)
                return;
            if (!TryCommitFieldsForPick())
                return;
            Outcome = RoiLineSegmentDialogOutcome.PickStart;
            DialogResult = true;
        }

        private void BtnPickEnd_Click(object sender, RoutedEventArgs e)
        {
            if (!TryCommitFieldsForPick())
                return;
            Outcome = RoiLineSegmentDialogOutcome.PickEnd;
            DialogResult = true;
        }

        private bool TryCommitFieldsForPick()
        {
            if (_allowEditStart)
            {
                if (!TryParse(TxtStartColPx, out double sc) || !TryParse(TxtStartRowPx, out double sr))
                {
                    MessageBox.Show(this, "请输入有效的起点列、行坐标。", "直线段", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                ApplyStartFromFields(sc, sr);
            }

            if (TryParse(TxtEndColPx, out double ec) && TryParse(TxtEndRowPx, out double er))
            {
                EndColumnPx = ec;
                EndRowPx = er;
                UpdatePolarFromEnd();
            }

            if (TryParse(TxtAngleDeg, out double deg))
                SetDirectionFromAngleDegrees(deg);
            if (TryParse(TxtLengthPx, out double px) && px >= 0.5)
            {
                LengthPx = px;
                UpdateEndFromPolar();
            }

            return true;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (_allowEditStart)
            {
                if (!TryParse(TxtStartColPx, out double startCol) || !TryParse(TxtStartRowPx, out double startRow))
                {
                    MessageBox.Show(this, "请输入有效的起点列、行坐标。", "直线段", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                ApplyStartFromFields(startCol, startRow);
            }

            if (!TryParse(TxtLengthPx, out double px) || px < 0.5)
            {
                MessageBox.Show(this, "请输入有效的段长（像素 ≥ 0.5）。", "直线段", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!TryParse(TxtAngleDeg, out double deg))
            {
                MessageBox.Show(this, "请输入有效的方向角（度）。", "直线段", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!TryParse(TxtEndColPx, out double endCol) || !TryParse(TxtEndRowPx, out double endRow))
            {
                MessageBox.Show(this, "请输入有效的终点列、行坐标。", "直线段", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            LengthPx = px;
            SetDirectionFromAngleDegrees(deg);
            EndColumnPx = endCol;
            EndRowPx = endRow;
            UpdatePolarFromEnd();
            if (LengthPx < 0.5)
            {
                MessageBox.Show(this, "终点与起点过近（段长 < 0.5 px）。", "直线段", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            StartColumnPx = _anchorCol;
            StartRowPx = _anchorRow;
            Outcome = RoiLineSegmentDialogOutcome.Ok;
            DialogResult = true;
        }

        private static bool TryParse(System.Windows.Controls.TextBox box, out double v) =>
            double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
