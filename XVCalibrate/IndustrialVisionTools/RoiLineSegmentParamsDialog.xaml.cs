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
        private enum LineDialogEditKind
        {
            None,
            LengthPx,
            LengthMm,
            Angle,
            Start,
            End
        }

        private bool _syncing;
        private AffineTransform? _affine;
        private double _anchorCol;
        private double _anchorRow;
        private double _dirX;
        private double _dirY;
        private bool _allowEditStart;
        private LineDialogEditKind _lastEdit = LineDialogEditKind.None;

        /// <summary>与 ROI 内存相同：去掉仿射旋转/剪切，只用均匀尺度。</summary>
        private const bool PreserveAngle = true;

        public double LengthPx { get; private set; }
        public double LengthMm { get; private set; }
        public double AngleDegrees { get; private set; }
        public double DirectionX => _dirX;
        public double DirectionY => _dirY;
        public double StartColumnPx { get; private set; }
        public double StartRowPx { get; private set; }
        public double EndColumnPx { get; private set; }
        public double EndRowPx { get; private set; }
        public RoiLineSegmentDialogOutcome Outcome { get; private set; } = RoiLineSegmentDialogOutcome.Cancel;
        public bool StartEdited => _lastEdit == LineDialogEditKind.Start;

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

        /// <param name="lengthRoi">路径坐标下的段长：有九点时为 mm，否则为像素。不要传入叠图像素长。</param>
        public void Initialize(
            double anchorCol,
            double anchorRow,
            double dirX,
            double dirY,
            double lengthRoi,
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
            if (affine is AffineTransform aff)
            {
                LengthMm = Math.Max(1e-6, lengthRoi);
                LengthPx = Math.Max(0.5, RoiWorldPixelConvert.MmLengthToPixels(LengthMm, aff));
            }
            else
            {
                LengthPx = Math.Max(0.5, lengthRoi);
                LengthMm = 0;
            }

            UpdateEndFromPolar();
            _lastEdit = LineDialogEditKind.None;
            Outcome = RoiLineSegmentDialogOutcome.Cancel;

            _syncing = true;
            TxtStartColPx.Text = StartColumnPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtStartRowPx.Text = StartRowPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtLengthPx.Text = LengthPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtLengthMm.Text = FormatLengthMm(LengthMm);
            TxtAngleDeg.Text = AngleDegrees.ToString("F2", CultureInfo.InvariantCulture);
            TxtEndColPx.Text = EndColumnPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtEndRowPx.Text = EndRowPx.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;

            TxtStartColPx.IsReadOnly = !allowEditStart;
            TxtStartRowPx.IsReadOnly = !allowEditStart;
            TxtAngleDeg.IsReadOnly = alongTangent && !endSnappedToLandmark;
            BtnPickStart.Visibility = allowEditStart ? Visibility.Visible : Visibility.Collapsed;
            BtnPickStart.IsEnabled = allowEditStart;

            if (endSnappedToLandmark)
            {
                TxtDirectionHint.Text = "已吸附附近顶点作为终点；可改长度/角度，或改终点坐标后确定。";
            }
            else
            {
                TxtDirectionHint.Text = alongTangent
                    ? "方向已锁定为上一段切线；可改段长（终点沿切线移动）。"
                    : allowEditStart
                        ? "可修改起点、角度、段长或终点；也可用「图上选」在画布点选（空白取点，靠近顶点则吸附）。"
                        : "初始方向来自鼠标；靠近已有顶点会吸附为终点；可修改角度、段长或终点坐标。";
            }

            RefreshAnchorHint();

            bool hasAffine = affine is AffineTransform;
            TxtLengthMm.IsReadOnly = !hasAffine;
            TxtLengthPx.IsReadOnly = hasAffine;
            TxtMmStatus.Text = hasAffine
                ? "段长在 mm 里计算；像素框只是叠图显示，确定时不用像素反算。"
                : "未加载九点标定文件，仅可编辑像素；mm 显示为 —。";
        }

        private void RefreshAnchorHint()
        {
            if (_affine is AffineTransform t)
            {
                Point2D w = HalconGeometryContourBuilder.ImagePixelToWorldMm(
                    _anchorCol, _anchorRow, t, PreserveAngle);
                TxtAnchorHint.Text = $"起点 ({w.X:F4},{w.Y:F4}) mm";
            }
            else
            {
                TxtAnchorHint.Text = $"起点列 {_anchorCol:F1}，行 {_anchorRow:F1}（像素）";
            }
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
            SyncLengthMmFromPx();
        }

        private void SyncLengthMmFromPx()
        {
            if (_affine is not AffineTransform aff)
            {
                LengthMm = 0;
                return;
            }

            LengthMm = RoiSegmentMeasure.PixelLengthToWorldMm(
                LengthPx, _anchorCol, _anchorRow, _dirX, _dirY, aff, PreserveAngle);
        }

        private void ApplyLengthMm(double mm)
        {
            LengthMm = mm;
            if (_affine is AffineTransform aff)
            {
                LengthPx = Math.Max(
                    0.5,
                    RoiSegmentMeasure.MmLengthToPixels(
                        mm, _anchorCol, _anchorRow, _dirX, _dirY, aff, PreserveAngle));
            }

            UpdateEndFromPolar();
        }

        private void ApplyStartFromFields(double col, double row)
        {
            _anchorCol = col;
            _anchorRow = row;
            StartColumnPx = col;
            StartRowPx = row;
            if (_affine is AffineTransform)
                ApplyLengthMm(LengthMm);
            else
                UpdatePolarFromEnd();
            RefreshAnchorHint();
        }

        private void OnLengthPxChanged()
        {
            if (_syncing || _affine is AffineTransform)
                return;
            if (!TryParse(TxtLengthPx, out double px) || px < 0.5)
                return;

            _lastEdit = LineDialogEditKind.LengthPx;
            LengthPx = px;
            UpdateEndFromPolar();
            SyncLengthMmFromPx();
            _syncing = true;
            TxtLengthMm.Text = FormatLengthMm(LengthMm);
            TxtEndColPx.Text = EndColumnPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtEndRowPx.Text = EndRowPx.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private void OnLengthMmChanged()
        {
            if (_syncing || _affine is not AffineTransform)
                return;
            if (!TryParse(TxtLengthMm, out double mm) || mm <= 0)
                return;

            _lastEdit = LineDialogEditKind.LengthMm;
            ApplyLengthMm(mm);
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

            _lastEdit = LineDialogEditKind.Angle;
            SetDirectionFromAngleDegrees(deg);
            if (_affine is AffineTransform)
                ApplyLengthMm(LengthMm);
            else
            {
                UpdateEndFromPolar();
                SyncLengthMmFromPx();
            }
            _syncing = true;
            TxtLengthPx.Text = LengthPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtLengthMm.Text = FormatLengthMm(LengthMm);
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

            _lastEdit = LineDialogEditKind.Start;
            ApplyStartFromFields(col, row);
            _syncing = true;
            TxtLengthPx.Text = LengthPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtLengthMm.Text = FormatLengthMm(LengthMm);
            TxtAngleDeg.Text = AngleDegrees.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private void OnEndCoordinatesChanged()
        {
            if (_syncing)
                return;
            if (!TryParse(TxtEndColPx, out double col) || !TryParse(TxtEndRowPx, out double row))
                return;

            _lastEdit = LineDialogEditKind.End;
            EndColumnPx = col;
            EndRowPx = row;
            UpdatePolarFromEnd();
            _syncing = true;
            TxtLengthPx.Text = LengthPx.ToString("F2", CultureInfo.InvariantCulture);
            TxtLengthMm.Text = FormatLengthMm(LengthMm);
            TxtAngleDeg.Text = AngleDegrees.ToString("F2", CultureInfo.InvariantCulture);
            _syncing = false;
        }

        private static string FormatLengthMm(double lengthMm) =>
            lengthMm <= 0 ? "—" : lengthMm.ToString("F4", CultureInfo.InvariantCulture);

        private void BtnPickStart_Click(object sender, RoutedEventArgs e)
        {
            if (!_allowEditStart)
                return;
            if (!TryCommitGeometryFromFields())
                return;
            Outcome = RoiLineSegmentDialogOutcome.PickStart;
            DialogResult = true;
        }

        private void BtnPickEnd_Click(object sender, RoutedEventArgs e)
        {
            if (!TryCommitGeometryFromFields())
                return;
            Outcome = RoiLineSegmentDialogOutcome.PickEnd;
            DialogResult = true;
        }

        private bool TryCommitGeometryFromFields()
        {
            if (_allowEditStart && _lastEdit == LineDialogEditKind.Start)
            {
                if (!TryParse(TxtStartColPx, out double startCol) || !TryParse(TxtStartRowPx, out double startRow))
                {
                    MessageBox.Show(this, "请输入有效的起点列、行坐标。", "直线段", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                ApplyStartFromFields(startCol, startRow);
            }

            switch (_lastEdit)
            {
                case LineDialogEditKind.End:
                    if (!TryParse(TxtEndColPx, out double endCol) || !TryParse(TxtEndRowPx, out double endRow))
                    {
                        MessageBox.Show(this, "请输入有效的终点列、行坐标。", "直线段", MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return false;
                    }

                    EndColumnPx = endCol;
                    EndRowPx = endRow;
                    UpdatePolarFromEnd();
                    break;
                case LineDialogEditKind.Start:
                    break;
                case LineDialogEditKind.Angle:
                    if (!TryParse(TxtAngleDeg, out double deg))
                    {
                        MessageBox.Show(this, "请输入有效的方向角（度）。", "直线段", MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return false;
                    }

                    SetDirectionFromAngleDegrees(deg);
                    if (_affine is AffineTransform)
                        ApplyLengthMm(LengthMm);
                    else
                    {
                        UpdateEndFromPolar();
                        SyncLengthMmFromPx();
                    }
                    break;
                case LineDialogEditKind.LengthPx:
                    if (!TryParse(TxtLengthPx, out double px) || px < 0.5)
                    {
                        MessageBox.Show(this, "请输入有效的段长（像素 ≥ 0.5）。", "直线段", MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return false;
                    }

                    LengthPx = px;
                    UpdateEndFromPolar();
                    SyncLengthMmFromPx();
                    break;
                default:
                    // 有标定时以 mm 框为准（含只改了数字、lastEdit 被像素框抢走的情况）
                    if (_affine is AffineTransform && TryParse(TxtLengthMm, out double mm) && mm > 0)
                        ApplyLengthMm(mm);
                    else
                    {
                        UpdateEndFromPolar();
                        SyncLengthMmFromPx();
                    }

                    break;
            }

            if (LengthPx < 0.5)
            {
                MessageBox.Show(this, "终点与起点过近（段长 < 0.5 px）。", "直线段", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            StartColumnPx = _anchorCol;
            StartRowPx = _anchorRow;
            return true;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (!TryCommitGeometryFromFields())
                return;

            Outcome = RoiLineSegmentDialogOutcome.Ok;
            DialogResult = true;
        }

        private static bool TryParse(System.Windows.Controls.TextBox box, out double v) =>
            double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
