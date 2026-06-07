using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    public partial class RoiPrimitiveParamsDialog : Window
    {
        private RoiPrimitiveKind _kind;
        private AffineTransform _affine;

        private int _imgWidth;
        private int _imgHeight;

        public double RectLeft { get; private set; }
        public double RectTop { get; private set; }
        public double RectWidth { get; private set; }
        public double RectHeight { get; private set; }

        public double RotCenterX { get; private set; }
        public double RotCenterY { get; private set; }
        public double RotWidth { get; private set; }
        public double RotHeight { get; private set; }
        public double RotAngleDeg { get; private set; }

        public double CircleCenterX { get; private set; }
        public double CircleCenterY { get; private set; }
        public double CircleRadius { get; private set; }

        public RoiPrimitiveParamsDialog()
        {
            InitializeComponent();
        }

        public void Initialize(
            RoiPrimitiveKind kind,
            double rectLeftPx,
            double rectTopPx,
            double rectWidthPx,
            double rectHeightPx,
            double rotCenterXPx,
            double rotCenterYPx,
            double rotWidthPx,
            double rotHeightPx,
            double rotAngleDeg,
            double circleCenterXPx,
            double circleCenterYPx,
            double circleRadiusPx,
            AffineTransform affine,
            int imgWidth = 0,
            int imgHeight = 0)
        {
            _kind = kind;
            _affine = affine;
            _imgWidth = imgWidth;
            _imgHeight = imgHeight;

            PnlRect.Visibility = kind == RoiPrimitiveKind.Rectangle ? Visibility.Visible : Visibility.Collapsed;
            PnlRotatedRect.Visibility = kind == RoiPrimitiveKind.RotatedRectangle ? Visibility.Visible : Visibility.Collapsed;
            PnlCircle.Visibility = kind == RoiPrimitiveKind.Circle ? Visibility.Visible : Visibility.Collapsed;

            Title = kind switch
            {
                RoiPrimitiveKind.Rectangle => "矩形 ROI 参数 (mm)",
                RoiPrimitiveKind.RotatedRectangle => "旋转矩形 ROI 参数 (mm)",
                _ => "圆形 ROI 参数 (mm)"
            };

            TxtHint.Text = kind switch
            {
                RoiPrimitiveKind.Rectangle => "左上角世界坐标 X/Y 与宽、高（mm，由九点标定换算）。",
                RoiPrimitiveKind.RotatedRectangle => "中心 X/Y、宽、高（mm）与长边方向角（0°=图像 +X，逆时针为正）。",
                _ => "圆心 X/Y 与半径（mm）。"
            };

            var topLeftMm = HalconGeometryContourBuilder.ImagePixelToWorldMm(rectLeftPx, rectTopPx, _affine);
            (double rectWMm, double rectHMm) = HalconGeometryContourBuilder.PixelSizeToWorldMm(
                rectWidthPx, rectHeightPx, rectLeftPx, rectTopPx, _affine);
            TxtRectLeft.Text = topLeftMm.X.ToString("F3", CultureInfo.InvariantCulture);
            TxtRectTop.Text = topLeftMm.Y.ToString("F3", CultureInfo.InvariantCulture);
            TxtRectWidth.Text = rectWMm.ToString("F3", CultureInfo.InvariantCulture);
            TxtRectHeight.Text = rectHMm.ToString("F3", CultureInfo.InvariantCulture);

            var rotCenterMm = HalconGeometryContourBuilder.ImagePixelToWorldMm(rotCenterXPx, rotCenterYPx, _affine);
            double rad = rotAngleDeg * Math.PI / 180.0;
            double dirX = Math.Cos(rad);
            double dirY = Math.Sin(rad);
            double rotWMm = RoiSegmentMeasure.PixelLengthToWorldMm(
                rotWidthPx, rotCenterXPx, rotCenterYPx, dirX, dirY, _affine);
            double rotHMm = RoiSegmentMeasure.PixelLengthToWorldMm(
                rotHeightPx, rotCenterXPx, rotCenterYPx, -dirY, dirX, _affine);
            TxtRotCenterX.Text = rotCenterMm.X.ToString("F3", CultureInfo.InvariantCulture);
            TxtRotCenterY.Text = rotCenterMm.Y.ToString("F3", CultureInfo.InvariantCulture);
            TxtRotWidth.Text = rotWMm.ToString("F3", CultureInfo.InvariantCulture);
            TxtRotHeight.Text = rotHMm.ToString("F3", CultureInfo.InvariantCulture);
            TxtRotAngleDeg.Text = rotAngleDeg.ToString("F1", CultureInfo.InvariantCulture);

            var circleCenterMm = HalconGeometryContourBuilder.ImagePixelToWorldMm(circleCenterXPx, circleCenterYPx, _affine);
            double circleRMm = HalconGeometryContourBuilder.PixelRadiusToWorldMm(
                circleRadiusPx, circleCenterXPx, circleCenterYPx, _affine);
            TxtCircleCenterX.Text = circleCenterMm.X.ToString("F3", CultureInfo.InvariantCulture);
            TxtCircleCenterY.Text = circleCenterMm.Y.ToString("F3", CultureInfo.InvariantCulture);
            TxtCircleRadius.Text = circleRMm.ToString("F3", CultureInfo.InvariantCulture);

            TxtWorldHint.Text = "坐标与尺寸均为世界坐标 mm；确定后自动换算为图像像素用于显示与模板。";
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            switch (_kind)
            {
                case RoiPrimitiveKind.Rectangle:
                    if (!TryParse(TxtRectLeft, out double leftMm)
                        || !TryParse(TxtRectTop, out double topMm)
                        || !TryParse(TxtRectWidth, out double widthMm)
                        || !TryParse(TxtRectHeight, out double heightMm))
                    {
                        ShowInvalid("请输入有效的左上角坐标与宽高（mm）。");
                        return;
                    }

                    if (widthMm <= 0 || heightMm <= 0)
                    {
                        ShowInvalid("宽、高须大于 0 mm。");
                        return;
                    }

                    var tl = HalconGeometryContourBuilder.WorldMmToImagePixel(leftMm, topMm, _affine);
                    double wPx = HalconGeometryContourBuilder.WorldMmWidthToPixelWidth(widthMm, tl.X, tl.Y, _affine);
                    double hPx = HalconGeometryContourBuilder.WorldMmHeightToPixelHeight(heightMm, tl.X, tl.Y, _affine);
                    if (wPx <= 5 || hPx <= 5)
                    {
                        ShowInvalid("换算后的宽、高过小（< 5 px），请增大 mm 尺寸。");
                        return;
                    }

                    RectLeft = tl.X;
                    RectTop = tl.Y;
                    RectWidth = wPx;
                    RectHeight = hPx;
                    break;

                case RoiPrimitiveKind.RotatedRectangle:
                    if (!TryParse(TxtRotCenterX, out double cxMm)
                        || !TryParse(TxtRotCenterY, out double cyMm)
                        || !TryParse(TxtRotWidth, out double rwMm)
                        || !TryParse(TxtRotHeight, out double rhMm)
                        || !TryParse(TxtRotAngleDeg, out double angle))
                    {
                        ShowInvalid("请输入有效的中心、宽高（mm）与角度。");
                        return;
                    }

                    if (rwMm <= 0 || rhMm <= 0)
                    {
                        ShowInvalid("宽、高须大于 0 mm。");
                        return;
                    }

                    var centerPx = HalconGeometryContourBuilder.WorldMmToImagePixel(cxMm, cyMm, _affine);
                    double angleRad = angle * Math.PI / 180.0;
                    double wDirX = Math.Cos(angleRad);
                    double wDirY = Math.Sin(angleRad);
                    double rwPx = RoiSegmentMeasure.MmLengthToPixels(
                        rwMm, centerPx.X, centerPx.Y, wDirX, wDirY, _affine);
                    double rhPx = RoiSegmentMeasure.MmLengthToPixels(
                        rhMm, centerPx.X, centerPx.Y, -wDirY, wDirX, _affine);
                    if (rwPx <= 5 || rhPx <= 5)
                    {
                        ShowInvalid("换算后的宽、高过小（< 5 px），请增大 mm 尺寸。");
                        return;
                    }

                    RotCenterX = centerPx.X;
                    RotCenterY = centerPx.Y;
                    RotWidth = rwPx;
                    RotHeight = rhPx;
                    RotAngleDeg = angle;
                    break;

                default:
                    if (!TryParse(TxtCircleCenterX, out double ccxMm)
                        || !TryParse(TxtCircleCenterY, out double ccyMm)
                        || !TryParse(TxtCircleRadius, out double radiusMm))
                    {
                        ShowInvalid("请输入有效的圆心与半径（mm）。");
                        return;
                    }

                    if (radiusMm <= 0)
                    {
                        ShowInvalid("半径须大于 0 mm。");
                        return;
                    }

                    var circleCenterPx = HalconGeometryContourBuilder.WorldMmToImagePixel(ccxMm, ccyMm, _affine);
                    double rPx = HalconGeometryContourBuilder.WorldMmRadiusToPixelRadius(
                        radiusMm, circleCenterPx.X, circleCenterPx.Y, _affine);
                    if (!TryValidateCircleInImage(circleCenterPx.X, circleCenterPx.Y, rPx, out string circleErr))
                    {
                        ShowInvalid(circleErr);
                        return;
                    }

                    CircleCenterX = circleCenterPx.X;
                    CircleCenterY = circleCenterPx.Y;
                    CircleRadius = rPx;
                    break;
            }

            DialogResult = true;
        }

        private void ShowInvalid(string message) =>
            MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);

        private bool TryValidateCircleInImage(double centerCol, double centerRow, double radiusPx, out string error)
        {
            error = "";
            if (radiusPx <= 5)
            {
                error = "换算后的半径过小（< 5 px），请增大 mm 半径。";
                return false;
            }

            if (_imgWidth <= 0 || _imgHeight <= 0)
                return true;

            if (centerCol < 0 || centerCol > _imgWidth - 1 || centerRow < 0 || centerRow > _imgHeight - 1)
            {
                error = "圆心换算后超出图像范围，请调整 mm 坐标。";
                return false;
            }

            double maxR = Math.Min(
                Math.Min(centerCol, _imgWidth - 1 - centerCol),
                Math.Min(centerRow, _imgHeight - 1 - centerRow));
            if (Math.Min(radiusPx, maxR) <= 5)
            {
                error = "半径过大（超出图像边界）或过小，请调整 mm 半径或圆心位置。";
                return false;
            }

            return true;
        }

        private static bool TryParse(TextBox box, out double v) =>
            double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
