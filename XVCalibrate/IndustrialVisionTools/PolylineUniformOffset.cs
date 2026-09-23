using System;
using System.Collections.Generic;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 折线/轮廓法向等距偏移（平行曲线）。闭合轮廓在图像坐标（Y 向下）下：正距离=外扩、负距离=内缩。
    /// </summary>
    public static class PolylineUniformOffset
    {
        const double MiterLimit = 5.0;
        /// <summary>闭合环顶点过少时先沿周长加密，避免折线偏移在弧边/长边上「切角」导致相对原轮廓宽窄不一。</summary>
        const int ClosedDensifyMinPoints = 48;

        public static Point2D[] Offset(Point2D[] pts, double distance, bool closed)
        {
            if (pts == null || pts.Length < 2 || Math.Abs(distance) < 1e-12)
                return pts ?? Array.Empty<Point2D>();

            if (closed && pts.Length >= 3)
                return OffsetClosed(pts, distance);

            return OffsetOpen(pts, distance);
        }

        public static CalibPoint3D[] Offset3D(CalibPoint3D[] pts, double distance, bool closed)
        {
            if (pts == null || pts.Length < 2 || Math.Abs(distance) < 1e-12)
                return pts ?? Array.Empty<CalibPoint3D>();

            var xy = new Point2D[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                xy[i] = new Point2D(pts[i].X, pts[i].Y);

            var off = Offset(xy, distance, closed);
            if (off.Length != pts.Length)
            {
                // 顶点数变化时 Z 用最近原顶点（闭合/开折线偏移后点数通常不变）
                var zOut = new CalibPoint3D[off.Length];
                for (int i = 0; i < off.Length; i++)
                {
                    int j = Math.Min(i, pts.Length - 1);
                    zOut[i] = new CalibPoint3D(off[i].X, off[i].Y, pts[j].Z);
                }
                return zOut;
            }

            var result = new CalibPoint3D[off.Length];
            for (int i = 0; i < off.Length; i++)
                result[i] = new CalibPoint3D(off[i].X, off[i].Y, pts[i].Z);
            return result;
        }

        static Point2D[] OffsetClosed(Point2D[] pts, double d)
        {
            var ring = PrepareClosedRing(pts);
            if (ring.Length < 3)
                return pts;

            if (ring.Length < ClosedDensifyMinPoints)
            {
                var dense = HalconFlowBridge.DensifyClosedPolyline(ring, ClosedDensifyMinPoints);
                ring = PrepareClosedRing(dense);
                if (ring.Length < 3)
                    return pts;
            }

            int n = ring.Length;
            var result = new Point2D[n];
            for (int i = 0; i < n; i++)
            {
                int im = (i - 1 + n) % n;
                int ip = (i + 1) % n;
                result[i] = OffsetVertex(ring[im], ring[i], ring[ip], d);
            }

            return result;
        }

        /// <summary>去掉末尾与起点重合的闭合点，避免退化边导致某一侧偏移异常。</summary>
        static Point2D[] PrepareClosedRing(Point2D[] pts)
        {
            if (pts == null || pts.Length < 2)
                return pts ?? Array.Empty<Point2D>();

            double dx = pts[0].X - pts[^1].X;
            double dy = pts[0].Y - pts[^1].Y;
            if (dx * dx + dy * dy > 1e-12)
                return pts;

            if (pts.Length <= 3)
                return pts;

            var ring = new Point2D[pts.Length - 1];
            Array.Copy(pts, ring, pts.Length - 1);
            return ring;
        }

        static Point2D[] OffsetOpen(Point2D[] pts, double d)
        {
            int n = pts.Length;
            var result = new Point2D[n];

            if (n == 2)
            {
                var n0 = LeftNormal(pts[0], pts[1]);
                result[0] = Shift(pts[0], n0, d);
                result[1] = Shift(pts[1], n0, d);
                return result;
            }

            var nFirst = LeftNormal(pts[0], pts[1]);
            result[0] = Shift(pts[0], nFirst, d);

            for (int i = 1; i < n - 1; i++)
                result[i] = OffsetVertexOpen(pts[i - 1], pts[i], pts[i + 1], d);

            var nLast = LeftNormal(pts[n - 2], pts[n - 1]);
            result[n - 1] = Shift(pts[n - 1], nLast, d);
            return result;
        }

        /// <summary>
        /// 图像坐标（Y 向下、列/行即 X/Y）：外法向统一为边方向的 -LeftNormal，
        /// 与 HALCON 外形顺逆混用时按边一致，避免上/下或左/右一侧动、一侧几乎不动。
        /// </summary>
        static Point2D OutwardNormal(Point2D a, Point2D b)
        {
            var left = LeftNormal(a, b);
            return new Point2D(-left.X, -left.Y);
        }

        static Point2D OffsetVertex(Point2D prev, Point2D cur, Point2D next, double d)
        {
            var n1 = OutwardNormal(prev, cur);
            var n2 = OutwardNormal(cur, next);
            return OffsetVertexMiter(prev, cur, next, d, n1, n2);
        }

        static Point2D OffsetVertexOpen(Point2D prev, Point2D cur, Point2D next, double d)
        {
            var n1 = LeftNormal(prev, cur);
            var n2 = LeftNormal(cur, next);
            return OffsetVertexMiter(prev, cur, next, d, n1, n2);
        }

        static Point2D OffsetVertexMiter(Point2D prev, Point2D cur, Point2D next, double d, Point2D n1, Point2D n2)
        {
            var a1 = Shift(prev, n1, d);
            var b1 = Shift(cur, n1, d);
            var b2 = Shift(cur, n2, d);
            var c2 = Shift(next, n2, d);

            if (LineLineIntersection(a1, b1, b2, c2, out var hit))
            {
                double miter = Dist(hit, cur);
                if (miter <= Math.Max(MiterLimit * Math.Abs(d), 1e-6))
                    return hit;
            }

            return new Point2D((b1.X + b2.X) * 0.5, (b1.Y + b2.Y) * 0.5);
        }

        static Point2D Shift(Point2D p, Point2D nUnit, double d)
            => new Point2D(p.X + nUnit.X * d, p.Y + nUnit.Y * d);

        static Point2D LeftNormal(Point2D a, Point2D b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-12)
                return new Point2D(0, 0);
            return new Point2D(-dy / len, dx / len);
        }

        static double SignedArea(Point2D[] pts)
        {
            double a = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                int j = (i + 1) % pts.Length;
                a += pts[i].X * pts[j].Y - pts[j].X * pts[i].Y;
            }
            return a * 0.5;
        }

        static bool LineLineIntersection(Point2D a1, Point2D a2, Point2D b1, Point2D b2, out Point2D hit)
        {
            hit = default;
            double dax = a2.X - a1.X;
            double day = a2.Y - a1.Y;
            double dbx = b2.X - b1.X;
            double dby = b2.Y - b1.Y;
            double denom = dax * dby - day * dbx;
            if (Math.Abs(denom) < 1e-12)
                return false;

            double t = ((b1.X - a1.X) * dby - (b1.Y - a1.Y) * dbx) / denom;
            hit = new Point2D(a1.X + t * dax, a1.Y + t * day);
            return true;
        }

        static double Dist(Point2D a, Point2D b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
