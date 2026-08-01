using System;

using System.Globalization;

using System.Windows;

using CalibOperatorPInvoke;



namespace CalibOperatorCLI_Example

{

    public partial class RoiLineSegmentParamsDialog : Window

    {

        private bool _syncing;

        private AffineTransform? _affine;

        private double _anchorCol;

        private double _anchorRow;

        private double _dirX;

        private double _dirY;



        public double LengthPx { get; private set; }

        public double AngleDegrees { get; private set; }

        public double DirectionX => _dirX;

        public double DirectionY => _dirY;

        public double EndColumnPx { get; private set; }

        public double EndRowPx { get; private set; }



        public RoiLineSegmentParamsDialog()

        {

            InitializeComponent();

            TxtLengthPx.TextChanged += (_, _) => OnLengthPxChanged();

            TxtLengthMm.TextChanged += (_, _) => OnLengthMmChanged();

            TxtAngleDeg.TextChanged += (_, _) => OnAngleDegChanged();

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

            bool alongTangent)

        {

            _anchorCol = anchorCol;

            _anchorRow = anchorRow;

            SetDirection(dirX, dirY);

            _affine = affine;

            LengthPx = Math.Max(0.5, lengthPx);

            UpdateEndFromPolar();



            _syncing = true;

            TxtLengthPx.Text = LengthPx.ToString("F2", CultureInfo.InvariantCulture);

            TxtLengthMm.Text = FormatLengthMm(LengthPx);

            TxtAngleDeg.Text = AngleDegrees.ToString("F2", CultureInfo.InvariantCulture);

            TxtEndColPx.Text = EndColumnPx.ToString("F2", CultureInfo.InvariantCulture);

            TxtEndRowPx.Text = EndRowPx.ToString("F2", CultureInfo.InvariantCulture);

            _syncing = false;



            TxtDirectionHint.Text = alongTangent

                ? "初始方向沿上一段圆弧切线；可修改角度、段长或终点坐标。"

                : "初始方向来自鼠标；可修改角度、段长或终点坐标。";



            TxtAnchorHint.Text = affine is AffineTransform t

                ? $"起点 {HalconGeometryContourBuilder.FormatWorldPointMm(_anchorCol, _anchorRow, t)}"

                : $"起点列 {_anchorCol:F1}，行 {_anchorRow:F1}（像素）";



            bool hasAffine = affine is AffineTransform;

            TxtLengthMm.IsReadOnly = !hasAffine;

            TxtMmStatus.Text = hasAffine

                ? "段长已按九点标定换算 mm；若终点落在起点容差内将自动闭合（闭合多边形/环形）。"

                : "未加载九点标定文件，仅可编辑像素；mm 显示为 —。";

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



        private void PushPolarToUi()

        {

            _syncing = true;

            TxtLengthPx.Text = LengthPx.ToString("F2", CultureInfo.InvariantCulture);

            TxtLengthMm.Text = FormatLengthMm(LengthPx);

            TxtAngleDeg.Text = AngleDegrees.ToString("F2", CultureInfo.InvariantCulture);

            TxtEndColPx.Text = EndColumnPx.ToString("F2", CultureInfo.InvariantCulture);

            TxtEndRowPx.Text = EndRowPx.ToString("F2", CultureInfo.InvariantCulture);

            _syncing = false;

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

            PushPolarToUi();

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

            TxtEndColPx.Text = EndColumnPx.ToString("F2", CultureInfo.InvariantCulture);

            TxtEndRowPx.Text = EndRowPx.ToString("F2", CultureInfo.InvariantCulture);

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

            PushPolarToUi();

        }



        private string FormatLengthMm(double lengthPx)

        {

            if (_affine is not AffineTransform aff)

                return "—";

            double mm = RoiSegmentMeasure.PixelLengthToWorldMm(lengthPx, _anchorCol, _anchorRow, _dirX, _dirY, aff);

            return mm.ToString("F3", CultureInfo.InvariantCulture);

        }



        private void BtnOk_Click(object sender, RoutedEventArgs e)

        {

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



            DialogResult = true;

        }



        private static bool TryParse(System.Windows.Controls.TextBox box, out double v) =>

            double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    }

}

