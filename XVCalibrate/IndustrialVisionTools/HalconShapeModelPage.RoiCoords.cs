using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    public partial class HalconShapeModelPage
    {
        /// <summary>有九点标定时 ROI 内存/存盘均为 mm，仅叠图与 HALCON 换成像素。</summary>
        private bool RoiUsesMillimeters()
        {
#if HALCON_ENABLED
            return TryGetNinePointAffineOptional() != null;
#else
            return false;
#endif
        }

        private bool TryRoiAffine(out AffineTransform t, out bool preserveAngle)
        {
            preserveAngle = IsPreserveRoiAngleEnabled();
            t = default;
#if HALCON_ENABLED
            return TryGetNinePointAffine(out t, out _);
#else
            return false;
#endif
        }

        private Point ImagePxToRoi(Point imagePx)
        {
            Point corrected = MapDisplayPixelToCorrected(imagePx);
            if (!RoiUsesMillimeters() || !TryRoiAffine(out AffineTransform t, out bool preserve))
                return corrected;
#if HALCON_ENABLED
            Point2D w = HalconGeometryContourBuilder.ImagePixelToWorldMm(corrected.X, corrected.Y, t, preserve);
            return new Point(w.X, w.Y);
#else
            return corrected;
#endif
        }

        private Point RoiToImagePx(Point roi)
        {
            Point corrected = roi;
            if (RoiUsesMillimeters() && TryRoiAffine(out AffineTransform t, out bool preserve))
            {
#if HALCON_ENABLED
                Point2D p = HalconGeometryContourBuilder.WorldMmToImagePixel(roi.X, roi.Y, t, preserve);
                corrected = new Point(p.X, p.Y);
#endif
            }

            return MapCorrectedPixelToDisplay(corrected);
        }

        /// <summary>矫正坐标系 → 本页显示旋转后的图像像素（与 CalibImageTransform.Rotate 一致）。</summary>
        private Point MapCorrectedPixelToDisplay(Point corrected)
        {
            if (!TryGetPostCorrectRotateSnap(out int snap, out int sw, out int sh))
                return corrected;

            double x;
            double y;
            int rotW;
            int rotH;
            switch (snap)
            {
                case 90:
                    x = sh - 1 - corrected.Y;
                    y = corrected.X;
                    rotW = sh;
                    rotH = sw;
                    break;
                case 180:
                    x = sw - 1 - corrected.X;
                    y = sh - 1 - corrected.Y;
                    rotW = sw;
                    rotH = sh;
                    break;
                case 270:
                    x = corrected.Y;
                    y = sw - 1 - corrected.X;
                    rotW = sh;
                    rotH = sw;
                    break;
                default:
                    return MapCorrectedPixelToDisplayArbitrary(corrected, sw, sh);
            }

            return MapRotatedPixelToDisplayCanvas(x, y, rotW, rotH);
        }

        /// <summary>显示图像像素 → 矫正坐标系（未做本页旋转）。</summary>
        private Point MapDisplayPixelToCorrected(Point display)
        {
            if (!TryGetPostCorrectRotateSnap(out int snap, out int sw, out int sh))
                return display;

            Point onRot = MapDisplayCanvasToRotatedPixel(display, snap, sw, sh);
            double x = onRot.X;
            double y = onRot.Y;
            switch (snap)
            {
                case 90:
                    return new Point(y, sh - 1 - x);
                case 180:
                    return new Point(sw - 1 - x, sh - 1 - y);
                case 270:
                    return new Point(sw - 1 - y, x);
                default:
                    return MapDisplayPixelToCorrectedArbitrary(display, sw, sh);
            }
        }

        private bool TryGetPostCorrectRotateSnap(out int snap, out int sw, out int sh)
        {
            snap = 0;
            sw = _correctFrameWidth > 0 ? _correctFrameWidth : _imgWidth;
            sh = _correctFrameHeight > 0 ? _correctFrameHeight : _imgHeight;
            if (sw <= 0 || sh <= 0)
                return false;
            double a = NormalizePostCorrectRotateDeg(_postCorrectRotateDeg);
            if (a < 1e-6 || Math.Abs(a - 360) < 1e-6)
                return false;
            snap = ((int)Math.Round(a) % 360 + 360) % 360;
            return true;
        }

        private Point MapRotatedPixelToDisplayCanvas(double x, double y, int rotW, int rotH)
        {
            int dw = _imgWidth > 0 ? _imgWidth : rotW;
            int dh = _imgHeight > 0 ? _imgHeight : rotH;
            if (dw == rotW && dh == rotH)
                return new Point(x, y);

            int copyW = Math.Min(rotW, dw);
            int copyH = Math.Min(rotH, dh);
            int srcX0 = Math.Max(0, (rotW - copyW) / 2);
            int srcY0 = Math.Max(0, (rotH - copyH) / 2);
            int dstX0 = Math.Max(0, (dw - copyW) / 2);
            int dstY0 = Math.Max(0, (dh - copyH) / 2);
            return new Point(x - srcX0 + dstX0, y - srcY0 + dstY0);
        }

        private Point MapDisplayCanvasToRotatedPixel(Point display, int snap, int sw, int sh)
        {
            int rotW = snap is 90 or 270 ? sh : sw;
            int rotH = snap is 90 or 270 ? sw : sh;
            int dw = _imgWidth > 0 ? _imgWidth : rotW;
            int dh = _imgHeight > 0 ? _imgHeight : rotH;
            if (dw == rotW && dh == rotH)
                return display;

            int copyW = Math.Min(rotW, dw);
            int copyH = Math.Min(rotH, dh);
            int srcX0 = Math.Max(0, (rotW - copyW) / 2);
            int srcY0 = Math.Max(0, (rotH - copyH) / 2);
            int dstX0 = Math.Max(0, (dw - copyW) / 2);
            int dstY0 = Math.Max(0, (dh - copyH) / 2);
            return new Point(display.X - dstX0 + srcX0, display.Y - dstY0 + srcY0);
        }

        private Point MapCorrectedPixelToDisplayArbitrary(Point corrected, int sw, int sh)
        {
            bool expand = ChkRotateExpandCanvas?.IsChecked == true;
            double rad = NormalizePostCorrectRotateDeg(_postCorrectRotateDeg) * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            int dw;
            int dh;
            if (expand)
            {
                dw = Math.Max(1, (int)Math.Ceiling(sw * Math.Abs(cos) + sh * Math.Abs(sin)));
                dh = Math.Max(1, (int)Math.Ceiling(sw * Math.Abs(sin) + sh * Math.Abs(cos)));
            }
            else
            {
                dw = sw;
                dh = sh;
            }

            double x = (corrected.X - sw / 2.0) * cos - (corrected.Y - sh / 2.0) * sin + dw / 2.0;
            double y = (corrected.X - sw / 2.0) * sin + (corrected.Y - sh / 2.0) * cos + dh / 2.0;
            return MapRotatedPixelToDisplayCanvas(x, y, dw, dh);
        }

        private Point MapDisplayPixelToCorrectedArbitrary(Point display, int sw, int sh)
        {
            bool expand = ChkRotateExpandCanvas?.IsChecked == true;
            double rad = NormalizePostCorrectRotateDeg(_postCorrectRotateDeg) * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            int dw;
            int dh;
            if (expand)
            {
                dw = Math.Max(1, (int)Math.Ceiling(sw * Math.Abs(cos) + sh * Math.Abs(sin)));
                dh = Math.Max(1, (int)Math.Ceiling(sw * Math.Abs(sin) + sh * Math.Abs(cos)));
            }
            else
            {
                dw = sw;
                dh = sh;
            }

            int copyW = Math.Min(dw, _imgWidth > 0 ? _imgWidth : dw);
            int copyH = Math.Min(dh, _imgHeight > 0 ? _imgHeight : dh);
            int srcX0 = Math.Max(0, (dw - copyW) / 2);
            int srcY0 = Math.Max(0, (dh - copyH) / 2);
            int dstX0 = Math.Max(0, ((_imgWidth > 0 ? _imgWidth : dw) - copyW) / 2);
            int dstY0 = Math.Max(0, ((_imgHeight > 0 ? _imgHeight : dh) - copyH) / 2);
            double rx = display.X - dstX0 + srcX0;
            double ry = display.Y - dstY0 + srcY0;
            double x = rx - dw / 2.0;
            double y = ry - dh / 2.0;
            return new Point(x * cos + y * sin + sw / 2.0, -x * sin + y * cos + sh / 2.0);
        }

        private double ImageLengthToRoi(double lengthPx)
        {
            if (lengthPx <= 0 || !RoiUsesMillimeters() || !TryRoiAffine(out AffineTransform t, out _))
                return lengthPx;
            return RoiWorldPixelConvert.PixelLengthToMm(lengthPx, t);
        }

        private double RoiLengthToImagePx(double lengthRoi)
        {
            if (lengthRoi <= 0 || !RoiUsesMillimeters() || !TryRoiAffine(out AffineTransform t, out _))
                return lengthRoi;
            return RoiWorldPixelConvert.MmLengthToPixels(lengthRoi, t);
        }

        private double RoiMinExtent() => ImageLengthToRoi(5);

        private PointCollection ToCanvasPoints(IEnumerable<Point> roiPts)
        {
            var pc = new PointCollection();
            foreach (Point p in roiPts)
                pc.Add(RoiToImagePx(p));
            return pc;
        }

        private List<Point2D> RoiFlattenedToImagePx(IEnumerable<Point> roiPts)
        {
            var list = new List<Point2D>();
            foreach (Point p in roiPts)
            {
                Point px = RoiToImagePx(p);
                list.Add(new Point2D(px.X, px.Y));
            }

            return list;
        }

        private List<Point2D> RoiPathDenseToImagePx(RoiContourPath path, bool closed)
        {
            List<Point> flat = path.BuildFlattenedPolygon(closed, arcSegments: ClosedArcSegments);
            return RoiFlattenedToImagePx(flat);
        }

        private void GetCircleImagePx(out double col, out double row, out double radiusPx)
        {
            Point c = RoiToImagePx(_circleCenterImage);
            col = c.X;
            row = c.Y;
            radiusPx = RoiLengthToImagePx(_circleRadiusImage);
        }

        private void GetRotatedRectImagePx(
            out double col, out double row, out double widthPx, out double heightPx, out double angleDeg)
        {
            Point c = RoiToImagePx(new Point(_rotRectCenterX, _rotRectCenterY));
            col = c.X;
            row = c.Y;
            widthPx = RoiLengthToImagePx(_rotRectWidth);
            heightPx = RoiLengthToImagePx(_rotRectHeight);
            angleDeg = _rotRectAngleDeg + NormalizePostCorrectRotateDeg(_postCorrectRotateDeg);
        }

        private void RoiSegmentToImageDialog(
            Point roiStart,
            Point roiEnd,
            out double colPx,
            out double rowPx,
            out double dirX,
            out double dirY,
            out double lengthPx)
        {
            Point a = RoiToImagePx(roiStart);
            Point b = RoiToImagePx(roiEnd);
            colPx = a.X;
            rowPx = a.Y;
            dirX = b.X - a.X;
            dirY = b.Y - a.Y;
            lengthPx = Math.Sqrt(dirX * dirX + dirY * dirY);
            if (lengthPx < 0.5)
            {
                lengthPx = 0.5;
                dirX = 1;
                dirY = 0;
            }
            else
            {
                dirX /= lengthPx;
                dirY /= lengthPx;
            }
        }
    }
}
