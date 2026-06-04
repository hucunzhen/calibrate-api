using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CalibOperatorPInvoke;

#if HALCON_ENABLED
namespace CalibOperatorCLI_Example
{
    public enum HalconMeasuredGeometryKind
    {
        AxisAlignedRectangle,
        RotatedRectangle,
        Circle,
        /// <summary>圆心 + 半径 + 起始角 + 张角(°)，开放弧段轮廓。</summary>
        Arc,
        /// <summary>起点、弧上点、终点确定的圆弧。</summary>
        ArcThreePoint,
        /// <summary>手绘多边形/环形外圈路径（含直线与圆弧段），由 ROI 同步。</summary>
        RoiPolyline
    }

    public struct HalconMeasuredGeometryParams
    {
        public HalconMeasuredGeometryKind Kind;
        public double CenterCol;
        public double CenterRow;
        public double WidthPx;
        public double HeightPx;
        public double AngleDeg;
        public double RadiusPx;
        public double ArcStartAngleDeg;
        public double ArcExtentAngleDeg;
        public double ArcStartCol;
        public double ArcStartRow;
        public double ArcViaCol;
        public double ArcViaRow;
        public double ArcEndCol;
        public double ArcEndRow;
    }

    /// <summary>由测量/ROI 参数生成 XLD 轮廓（列=X，行=Y）。</summary>
    public static class HalconGeometryContourBuilder
    {
        public const int DefaultCircleSegments = 72;
        public const int DefaultRectSegmentsPerEdge = 1;
        public const int DefaultArcSegments = 48;

        public static Point2D[] BuildAxisAlignedRectangle(
            double centerCol,
            double centerRow,
            double widthPx,
            double heightPx) =>
            BuildRotatedRectangle(centerCol, centerRow, widthPx * 0.5, heightPx * 0.5, 0, DefaultRectSegmentsPerEdge);

        public static Point2D[] BuildRotatedRectangle(
            double centerCol,
            double centerRow,
            double halfWidthPx,
            double halfHeightPx,
            double angleDeg,
            int segmentsPerEdge = DefaultRectSegmentsPerEdge)
        {
            halfWidthPx = Math.Max(0.5, halfWidthPx);
            halfHeightPx = Math.Max(0.5, halfHeightPx);
            segmentsPerEdge = Math.Max(1, segmentsPerEdge);

            double phi = angleDeg * Math.PI / 180.0;
            double cos = Math.Cos(phi);
            double sin = Math.Sin(phi);
            double wc = cos * halfWidthPx;
            double wr = sin * halfWidthPx;
            double hc = -sin * halfHeightPx;
            double hr = cos * halfHeightPx;

            var corners = new[]
            {
                new Point2D(centerCol - wc - hc, centerRow - wr - hr),
                new Point2D(centerCol + wc - hc, centerRow + wr - hr),
                new Point2D(centerCol + wc + hc, centerRow + wr + hr),
                new Point2D(centerCol - wc + hc, centerRow - wr + hr)
            };

            var pts = new List<Point2D>(corners.Length * segmentsPerEdge);
            for (int i = 0; i < corners.Length; i++)
            {
                Point2D a = corners[i];
                Point2D b = corners[(i + 1) % corners.Length];
                for (int s = 0; s < segmentsPerEdge; s++)
                {
                    double t = s / (double)segmentsPerEdge;
                    pts.Add(new Point2D(
                        a.X + (b.X - a.X) * t,
                        a.Y + (b.Y - a.Y) * t));
                }
            }

            return pts.ToArray();
        }

        public static Point2D[] BuildCircle(
            double centerCol,
            double centerRow,
            double radiusPx,
            int segments = DefaultCircleSegments)
        {
            radiusPx = Math.Max(0.5, radiusPx);
            segments = Math.Max(12, segments);
            var pts = new Point2D[segments];
            for (int i = 0; i < segments; i++)
            {
                double a = 2.0 * Math.PI * i / segments;
                pts[i] = new Point2D(
                    centerCol + radiusPx * Math.Cos(a),
                    centerRow + radiusPx * Math.Sin(a));
            }

            return pts;
        }

        /// <summary>参数化圆弧：起始角、张角均为度（图像坐标：0°沿+X，逆时针为正）。</summary>
        public static Point2D[] BuildCircularArc(
            double centerCol,
            double centerRow,
            double radiusPx,
            double startAngleDeg,
            double extentAngleDeg,
            int segments = DefaultArcSegments)
        {
            radiusPx = Math.Max(0.5, radiusPx);
            if (Math.Abs(extentAngleDeg) < 0.01)
                extentAngleDeg = extentAngleDeg >= 0 ? 1 : -1;

            double a0 = startAngleDeg * Math.PI / 180.0;
            double sweep = extentAngleDeg * Math.PI / 180.0;
            int n = Math.Max(4, segments);
            var pts = new Point2D[n + 1];
            for (int i = 0; i <= n; i++)
            {
                double a = a0 + sweep * i / n;
                pts[i] = new Point2D(
                    centerCol + radiusPx * Math.Cos(a),
                    centerRow + radiusPx * Math.Sin(a));
            }

            return pts;
        }

        public static Point2D[] BuildArcThreePoint(
            double startCol,
            double startRow,
            double viaCol,
            double viaRow,
            double endCol,
            double endRow,
            int segments = DefaultArcSegments)
        {
            var start = new Point(startCol, startRow);
            var via = new Point(viaCol, viaRow);
            var end = new Point(endCol, endRow);
            if (!RoiContourPath.IsValidArcVia(start, via, end))
                return new[] { new Point2D(startCol, startRow), new Point2D(endCol, endRow) };

            return RoiContourPath.SampleArc(start, via, end, Math.Max(4, segments))
                .Select(p => new Point2D(p.X, p.Y))
                .ToArray();
        }

        public static Point2D[] BuildFromRoiContourPath(RoiContourPath path, int arcSegments = 32)
        {
            if (path == null || path.VertexCount < 2)
                return Array.Empty<Point2D>();

            bool closed = path.IsClosed && path.VertexCount >= 3;
            var flat = path.BuildFlattenedPolygon(closed, arcSegments);
            if (flat.Count < 2)
                return Array.Empty<Point2D>();

            return flat.Select(p => new Point2D(p.X, p.Y)).ToArray();
        }

        public static bool TryFitCircularArcFromThreePoints(
            double startCol,
            double startRow,
            double viaCol,
            double viaRow,
            double endCol,
            double endRow,
            out double centerCol,
            out double centerRow,
            out double radiusPx,
            out double startAngleDeg,
            out double extentAngleDeg)
        {
            centerCol = centerRow = radiusPx = startAngleDeg = extentAngleDeg = 0;
            var start = new Point(startCol, startRow);
            var via = new Point(viaCol, viaRow);
            var end = new Point(endCol, endRow);
            if (!RoiContourPath.IsValidArcVia(start, via, end))
                return false;

            if (!TryCircleCenterRadius(start, via, end, out Point center, out double radius))
                return false;

            centerCol = center.X;
            centerRow = center.Y;
            radiusPx = radius;
            double a0 = Math.Atan2(start.Y - center.Y, start.X - center.X) * 180.0 / Math.PI;
            double av = Math.Atan2(via.Y - center.Y, via.X - center.X) * 180.0 / Math.PI;
            double a1 = Math.Atan2(end.Y - center.Y, end.X - center.X) * 180.0 / Math.PI;
            startAngleDeg = a0;
            double sweepCcw = NormalizeSweepDeg(a0, a1, ccw: true);
            double tv = NormalizeAngleDeg(av - a0);
            extentAngleDeg = tv <= sweepCcw + 1e-6 ? sweepCcw : NormalizeSweepDeg(a0, a1, ccw: false);
            return true;
        }

        public static Point2D[] BuildContour(in HalconMeasuredGeometryParams p, Point2D[]? roiPolylineContour = null)
        {
            return p.Kind switch
            {
                HalconMeasuredGeometryKind.Circle => BuildCircle(p.CenterCol, p.CenterRow, p.RadiusPx),
                HalconMeasuredGeometryKind.AxisAlignedRectangle => BuildAxisAlignedRectangle(
                    p.CenterCol, p.CenterRow, p.WidthPx, p.HeightPx),
                HalconMeasuredGeometryKind.RotatedRectangle => BuildRotatedRectangle(
                    p.CenterCol, p.CenterRow, p.WidthPx * 0.5, p.HeightPx * 0.5, p.AngleDeg),
                HalconMeasuredGeometryKind.Arc => BuildCircularArc(
                    p.CenterCol, p.CenterRow, p.RadiusPx, p.ArcStartAngleDeg, p.ArcExtentAngleDeg),
                HalconMeasuredGeometryKind.ArcThreePoint => BuildArcThreePoint(
                    p.ArcStartCol, p.ArcStartRow, p.ArcViaCol, p.ArcViaRow, p.ArcEndCol, p.ArcEndRow),
                HalconMeasuredGeometryKind.RoiPolyline when roiPolylineContour != null && roiPolylineContour.Length >= 2
                    => roiPolylineContour,
                _ => Array.Empty<Point2D>()
            };
        }

        public static HalconXldContourBundle BuildBundle(
            int imageWidth,
            int imageHeight,
            in HalconMeasuredGeometryParams p,
            Point2D[]? roiPolylineContour = null,
            RoiContourPath? roiPath = null)
        {
            Point2D[] contour = p.Kind == HalconMeasuredGeometryKind.RoiPolyline && roiPath != null
                ? BuildFromRoiContourPath(roiPath)
                : BuildContour(p, roiPolylineContour);
            return new HalconXldContourBundle
            {
                Width = imageWidth,
                Height = imageHeight,
                Contours = contour.Length >= 2 ? new List<Point2D[]> { contour } : new List<Point2D[]>()
            };
        }

        public static Point2D ImagePixelToWorldMm(double col, double row, in AffineTransform t) =>
            CalibAPI.ImageToWorld(new Point2D(col, row), t);

        /// <summary>在 (centerCol,centerRow) 处沿 +X 偏移 radiusPx 像素，求世界系半径长度(mm)。</summary>
        public static double PixelRadiusToWorldMm(double radiusPx, double centerCol, double centerRow, in AffineTransform t)
        {
            if (radiusPx <= 0)
                return 0;
            var c = ImagePixelToWorldMm(centerCol, centerRow, t);
            var edge = ImagePixelToWorldMm(centerCol + radiusPx, centerRow, t);
            double dx = edge.X - c.X;
            double dy = edge.Y - c.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>在 (anchorCol,anchorRow) 处沿 +X / +Y 各偏移对应像素长度，求世界系宽高(mm)。</summary>
        public static (double WidthMm, double HeightMm) PixelSizeToWorldMm(
            double widthPx,
            double heightPx,
            double anchorCol,
            double anchorRow,
            in AffineTransform t)
        {
            var o = ImagePixelToWorldMm(anchorCol, anchorRow, t);
            var wx = ImagePixelToWorldMm(anchorCol + widthPx, anchorRow, t);
            var hy = ImagePixelToWorldMm(anchorCol, anchorRow + heightPx, t);
            double wMm = Math.Sqrt((wx.X - o.X) * (wx.X - o.X) + (wx.Y - o.Y) * (wx.Y - o.Y));
            double hMm = Math.Sqrt((hy.X - o.X) * (hy.X - o.X) + (hy.Y - o.Y) * (hy.Y - o.Y));
            return (wMm, hMm);
        }

        public static string FormatWorldPointMm(double col, double row, in AffineTransform t)
        {
            var w = ImagePixelToWorldMm(col, row, t);
            return $"({w.X:F2},{w.Y:F2}) mm";
        }

        public static string FormatMeasurementSummary(in HalconMeasuredGeometryParams p, AffineTransform? worldTransform)
        {
            string pxPart = p.Kind switch
            {
                HalconMeasuredGeometryKind.Circle =>
                    $"R={p.RadiusPx:F1} px，圆心 px=({p.CenterCol:F1},{p.CenterRow:F1})",
                HalconMeasuredGeometryKind.Arc =>
                    $"弧 R={p.RadiusPx:F1} px，圆心 px=({p.CenterCol:F1},{p.CenterRow:F1})，起={p.ArcStartAngleDeg:F1}° 张角={p.ArcExtentAngleDeg:F1}°",
                HalconMeasuredGeometryKind.ArcThreePoint =>
                    $"三点弧 px: ({p.ArcStartCol:F0},{p.ArcStartRow:F0})→({p.ArcViaCol:F0},{p.ArcViaRow:F0})→({p.ArcEndCol:F0},{p.ArcEndRow:F0})",
                HalconMeasuredGeometryKind.RoiPolyline =>
                    "手绘路径(含直线/圆弧段)",
                HalconMeasuredGeometryKind.AxisAlignedRectangle or HalconMeasuredGeometryKind.RotatedRectangle =>
                    $"W×H={p.WidthPx:F1}×{p.HeightPx:F1} px，中心 px=({p.CenterCol:F1},{p.CenterRow:F1})，θ={p.AngleDeg:F2}°",
                _ => "—"
            };

            if (worldTransform is not AffineTransform t)
                return pxPart + "；世界坐标: 未加载九点标定";

            string mmPart = FormatMeasurementMmPart(p, t);
            return string.IsNullOrEmpty(mmPart) ? pxPart : $"{pxPart}；世界: {mmPart}";
        }

        private static string FormatMeasurementMmPart(in HalconMeasuredGeometryParams p, in AffineTransform t)
        {
            switch (p.Kind)
            {
                case HalconMeasuredGeometryKind.Circle:
                {
                    double rMm = PixelRadiusToWorldMm(p.RadiusPx, p.CenterCol, p.CenterRow, t);
                    return $"圆心 {FormatWorldPointMm(p.CenterCol, p.CenterRow, t)}，R={rMm:F2} mm，Ø={2 * rMm:F2} mm";
                }
                case HalconMeasuredGeometryKind.Arc:
                {
                    double rMm = PixelRadiusToWorldMm(p.RadiusPx, p.CenterCol, p.CenterRow, t);
                    double arcLenMm = Math.Abs(p.ArcExtentAngleDeg) * Math.PI / 180.0 * rMm;
                    return $"圆心 {FormatWorldPointMm(p.CenterCol, p.CenterRow, t)}，R={rMm:F2} mm，弧长≈{arcLenMm:F2} mm";
                }
                case HalconMeasuredGeometryKind.ArcThreePoint:
                    return $"起点 {FormatWorldPointMm(p.ArcStartCol, p.ArcStartRow, t)}，弧上 {FormatWorldPointMm(p.ArcViaCol, p.ArcViaRow, t)}，终点 {FormatWorldPointMm(p.ArcEndCol, p.ArcEndRow, t)}";
                case HalconMeasuredGeometryKind.RoiPolyline:
                    return "路径顶点见图像 px（闭合多边形/环形）";
                case HalconMeasuredGeometryKind.AxisAlignedRectangle:
                case HalconMeasuredGeometryKind.RotatedRectangle:
                {
                    var (wMm, hMm) = PixelSizeToWorldMm(p.WidthPx, p.HeightPx, p.CenterCol, p.CenterRow, t);
                    return $"中心 {FormatWorldPointMm(p.CenterCol, p.CenterRow, t)}，≈{wMm:F2}×{hMm:F2} mm";
                }
                default:
                    return "";
            }
        }

        private static bool TryCircleCenterRadius(Point a, Point b, Point c, out Point center, out double radius)
        {
            double ax = a.X, ay = a.Y, bx = b.X, by = b.Y, cx = c.X, cy = c.Y;
            double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Math.Abs(d) < 1e-9)
            {
                center = default;
                radius = 0;
                return false;
            }

            double a2 = ax * ax + ay * ay;
            double b2 = bx * bx + by * by;
            double c2 = cx * cx + cy * cy;
            center = new Point(
                (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d,
                (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d);
            double dx = ax - center.X;
            double dy = ay - center.Y;
            radius = Math.Sqrt(dx * dx + dy * dy);
            return radius > 1e-9;
        }

        private static double NormalizeSweepDeg(double a0, double a1, bool ccw)
        {
            double diff = a1 - a0;
            if (ccw)
            {
                while (diff <= 0) diff += 360;
                while (diff > 360) diff -= 360;
            }
            else
            {
                while (diff >= 0) diff -= 360;
                while (diff < -360) diff += 360;
            }

            return diff;
        }

        private static double NormalizeAngleDeg(double a)
        {
            while (a < 0) a += 360;
            while (a >= 360) a -= 360;
            return a;
        }
    }
}
#endif
