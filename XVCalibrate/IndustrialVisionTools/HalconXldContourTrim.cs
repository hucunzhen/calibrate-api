#if HALCON_ENABLED
using System;
using System.Collections.Generic;
using System.Linq;
using CalibOperatorPInvoke;
using HalconDotNet;

namespace CalibOperatorCLI_Example
{
    public enum HalconContourTrimMode
    {
        /// <summary>Douglas–Peucker 简化</summary>
        Simplify,
        /// <summary>HALCON SmoothContoursXld</summary>
        Smooth,
        /// <summary>HALCON SegmentContoursXld 折线/弧段</summary>
        Segment,
        /// <summary>沿轮廓弧长裁掉首尾</summary>
        TrimEnds,
        /// <summary>删除周长过短的轮廓</summary>
        FilterMinLength
    }

    public sealed class HalconContourTrimOptions
    {
        public HalconContourTrimMode Mode { get; set; } = HalconContourTrimMode.Simplify;
        /// <summary>简化容差（像素）</summary>
        public double Epsilon { get; set; } = 2.0;
        public bool ClosedContour { get; set; } = true;
        /// <summary>SmoothContoursXld 窗口</summary>
        public int SmoothWindow { get; set; } = 5;
        public string SegmentMode { get; set; } = "lines_circles";
        public int SegmentSmooth { get; set; } = 5;
        public double SegmentMaxLineDist1 { get; set; } = 5.0;
        public double SegmentMaxLineDist2 { get; set; } = 2.0;
        /// <summary>从首尾各裁掉的弧长（像素）</summary>
        public double TrimEndsPx { get; set; } = 0;
        /// <summary>保留的最小轮廓周长（像素）</summary>
        public double MinContourLength { get; set; } = 20;
    }

    public static class HalconXldContourTrimmer
    {
        public static HalconXldContourBundle Apply(HalconXldContourBundle src, HalconContourTrimOptions opt)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (opt == null) throw new ArgumentNullException(nameof(opt));
            if (src.Contours == null || src.Contours.Count == 0)
                return CloneEmpty(src);

            var contours = src.Contours.Where(c => c != null && c.Length >= 2).ToList();
            if (contours.Count == 0)
                return CloneEmpty(src);

            List<Point2D[]> result = opt.Mode switch
            {
                HalconContourTrimMode.Simplify => contours.Select(c =>
                    opt.ClosedContour
                        ? SimplifyClosed(c, opt.Epsilon)
                        : SimplifyOpen(c, opt.Epsilon)).ToList(),
                HalconContourTrimMode.Smooth => SmoothContours(contours, opt.SmoothWindow),
                HalconContourTrimMode.Segment => SegmentContours(src, opt),
                HalconContourTrimMode.TrimEnds => contours.Select(c => TrimEnds(c, opt.TrimEndsPx, opt.ClosedContour))
                    .Where(c => c.Length >= 2).ToList(),
                HalconContourTrimMode.FilterMinLength => contours
                    .Where(c => PolylineLength(c, opt.ClosedContour) >= opt.MinContourLength).ToList(),
                _ => contours
            };

            result = result.Where(c => c != null && c.Length >= 2).ToList();
            if (opt.ClosedContour && opt.Mode != HalconContourTrimMode.Segment)
            {
                result = result.Select(c => HalconFlowBridge.EnsureClosedContourPoints(c)).ToList();
            }

            return new HalconXldContourBundle
            {
                Width = src.Width,
                Height = src.Height,
                Contours = result
            };
        }

        public static int CountPoints(HalconXldContourBundle? bundle)
        {
            if (bundle?.Contours == null) return 0;
            return bundle.Contours.Sum(c => c?.Length ?? 0);
        }

        private static HalconXldContourBundle CloneEmpty(HalconXldContourBundle src) =>
            new HalconXldContourBundle
            {
                Width = src.Width,
                Height = src.Height,
                Contours = new List<Point2D[]>()
            };

        private static List<Point2D[]> SegmentContours(HalconXldContourBundle src, HalconContourTrimOptions opt)
        {
            var segmented = HalconFlowBridge.SegmentXldBundle(
                src,
                opt.SegmentMode,
                opt.SegmentSmooth,
                opt.SegmentMaxLineDist1,
                opt.SegmentMaxLineDist2);
            return segmented.Contours?.ToList() ?? new List<Point2D[]>();
        }

        private static List<Point2D[]> SmoothContours(List<Point2D[]> contours, int window)
        {
            window = Math.Max(3, window | 1);
            var outList = new List<Point2D[]>();
            foreach (var poly in contours)
            {
                HOperatorSet.GenContourPolygonXld(out HObject xld, Rows(poly), Cols(poly));
                try
                {
                    HOperatorSet.SmoothContoursXld(xld, out HObject smooth, window);
                    try
                    {
                        outList.AddRange(HalconFlowBridge.ContourXldToPointArrays(smooth));
                    }
                    finally
                    {
                        smooth.Dispose();
                    }
                }
                finally
                {
                    xld.Dispose();
                }
            }

            return outList;
        }

        private static HTuple Rows(Point2D[] pts) => new HTuple(pts.Select(p => p.Y).ToArray());
        private static HTuple Cols(Point2D[] pts) => new HTuple(pts.Select(p => p.X).ToArray());

        public static double PolylineLength(Point2D[] pts, bool closed)
        {
            if (pts == null || pts.Length < 2) return 0;
            double len = 0;
            for (int i = 1; i < pts.Length; i++)
                len += Dist(pts[i - 1], pts[i]);
            if (closed)
                len += Dist(pts[^1], pts[0]);
            return len;
        }

        public static Point2D[] TrimEnds(Point2D[] pts, double trimPx, bool closed)
        {
            if (pts == null || pts.Length < 2 || trimPx <= 0)
                return pts ?? Array.Empty<Point2D>();

            double total = PolylineLength(pts, closed);
            if (trimPx * 2 >= total * 0.95)
                return new[] { pts[pts.Length / 2] };

            var open = pts.ToList();
            if (closed && open.Count > 2)
            {
                open.Add(open[0]);
                var trimmed = TrimOpenEnds(open, trimPx);
                return HalconFlowBridge.EnsureClosedContourPoints(trimmed.ToArray());
            }

            return TrimOpenEnds(open, trimPx).ToArray();
        }

        private static List<Point2D> TrimOpenEnds(List<Point2D> pts, double trimPx)
        {
            double fromStart = 0;
            int iStart = 0;
            while (iStart < pts.Count - 1 && fromStart < trimPx)
            {
                double seg = Dist(pts[iStart], pts[iStart + 1]);
                if (fromStart + seg >= trimPx)
                {
                    double t = (trimPx - fromStart) / Math.Max(seg, 1e-9);
                    pts[iStart] = Lerp(pts[iStart], pts[iStart + 1], t);
                    break;
                }

                fromStart += seg;
                iStart++;
            }

            pts = pts.Skip(iStart).ToList();
            if (pts.Count < 2) return pts;

            double fromEnd = 0;
            int iEnd = pts.Count - 1;
            while (iEnd > 0 && fromEnd < trimPx)
            {
                double seg = Dist(pts[iEnd - 1], pts[iEnd]);
                if (fromEnd + seg >= trimPx)
                {
                    double t = 1.0 - (trimPx - fromEnd) / Math.Max(seg, 1e-9);
                    pts[iEnd] = Lerp(pts[iEnd - 1], pts[iEnd], t);
                    break;
                }

                fromEnd += seg;
                iEnd--;
            }

            return pts.Take(iEnd + 1).ToList();
        }

        private static Point2D Lerp(Point2D a, Point2D b, double t) =>
            new Point2D(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        private static double Dist(Point2D a, Point2D b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static Point2D[] SimplifyOpen(Point2D[] points, double epsilon)
        {
            if (points == null || points.Length <= 2 || epsilon <= 0)
                return points ?? Array.Empty<Point2D>();
            return SimplifyOpenPolyline(points.ToList(), epsilon).ToArray();
        }

        public static Point2D[] SimplifyClosed(Point2D[] points, double epsilon)
        {
            if (points == null || points.Length < 4 || epsilon <= 0)
                return points ?? Array.Empty<Point2D>();
            var open = points.ToList();
            open.Add(points[0]);
            var simplified = SimplifyOpenPolyline(open, epsilon);
            if (simplified.Count > 1)
                simplified.RemoveAt(simplified.Count - 1);
            return simplified.ToArray();
        }

        private static List<Point2D> SimplifyOpenPolyline(List<Point2D> points, double epsilon)
        {
            if (points.Count <= 2) return new List<Point2D>(points);

            int index = -1;
            double maxDist = -1;
            var start = points[0];
            var end = points[^1];

            for (int i = 1; i < points.Count - 1; i++)
            {
                double dist = PointLineDistance(points[i], start, end);
                if (dist > maxDist)
                {
                    maxDist = dist;
                    index = i;
                }
            }

            if (maxDist <= epsilon || index <= 0)
                return new List<Point2D> { start, end };

            var left = SimplifyOpenPolyline(points.GetRange(0, index + 1), epsilon);
            var right = SimplifyOpenPolyline(points.GetRange(index, points.Count - index), epsilon);
            left.RemoveAt(left.Count - 1);
            left.AddRange(right);
            return left;
        }

        private static double PointLineDistance(Point2D p, Point2D a, Point2D b)
        {
            double vx = b.X - a.X;
            double vy = b.Y - a.Y;
            double wx = p.X - a.X;
            double wy = p.Y - a.Y;
            double c1 = vx * wx + vy * wy;
            if (c1 <= 0) return Math.Sqrt(wx * wx + wy * wy);
            double c2 = vx * vx + vy * vy;
            if (c2 <= c1) return Math.Sqrt((p.X - b.X) * (p.X - b.X) + (p.Y - b.Y) * (p.Y - b.Y));
            double t = c1 / c2;
            double px = a.X + t * vx;
            double py = a.Y + t * vy;
            double dx = p.X - px;
            double dy = p.Y - py;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
#endif
