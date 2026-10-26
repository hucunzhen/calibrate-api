using System;
using System.Collections.Generic;
using System.Windows;

namespace CalibOperatorCLI_Example
{
    public enum RoiEdgeKind
    {
        Line,
        Arc
    }

    /// <summary>闭合手绘边线处，期望的梯度方向（相对 ROI 内部）。</summary>
    public enum RoiGradientPolarity
    {
        /// <summary>梯度沿边线法向指向 ROI 外侧。</summary>
        Outward,
        /// <summary>梯度沿边线法向指向 ROI 内侧。</summary>
        Inward
    }

    public static class RoiGradientPolarityExtensions
    {
        public static RoiGradientPolarity Opposite(this RoiGradientPolarity p) =>
            p == RoiGradientPolarity.Outward ? RoiGradientPolarity.Inward : RoiGradientPolarity.Outward;
    }

    /// <summary>多边形 ROI：顶点 + 相邻顶点间的直线或圆弧段。</summary>
    public sealed class RoiContourPath
    {
        public List<Point> Vertices { get; } = new List<Point>();
        /// <summary>Edges[i]：从 Vertices[i] 到下一顶点（闭合时到 Vertices[(i+1)%n]）。</summary>
        public List<RoiEdgeKind> EdgeKinds { get; } = new List<RoiEdgeKind>();
        /// <summary>与 EdgeKinds 对齐；直线为 null，圆弧为过弧上的一点（非顶点）。</summary>
        public List<Point?> ArcVia { get; } = new List<Point?>();

        public bool IsClosed { get; set; }

        public int VertexCount => Vertices.Count;

        public void Clear()
        {
            Vertices.Clear();
            EdgeKinds.Clear();
            ArcVia.Clear();
            IsClosed = false;
        }

        public void AddFirstVertex(Point p) => Vertices.Add(p);

        public void AddLineSegment(Point end)
        {
            if (Vertices.Count == 0)
            {
                Vertices.Add(end);
                return;
            }

            EdgeKinds.Add(RoiEdgeKind.Line);
            ArcVia.Add(null);
            Vertices.Add(end);
        }

        public void AddArcSegment(Point end, Point via)
        {
            if (Vertices.Count == 0)
                throw new InvalidOperationException("圆弧段需要已有起点");

            EdgeKinds.Add(RoiEdgeKind.Arc);
            ArcVia.Add(via);
            Vertices.Add(end);
        }

        /// <summary>撤销最后一个顶点及其入边。</summary>
        public void RemoveLastVertex()
        {
            if (Vertices.Count == 0)
                return;

            if (Vertices.Count >= 2)
            {
                EdgeKinds.RemoveAt(EdgeKinds.Count - 1);
                ArcVia.RemoveAt(ArcVia.Count - 1);
            }

            Vertices.RemoveAt(Vertices.Count - 1);
            IsClosed = false;
        }

        public void EnsureClosingEdgeIsLine()
        {
            if (!IsClosed || Vertices.Count < 3)
                return;

            int need = Vertices.Count;
            while (EdgeKinds.Count < need)
            {
                EdgeKinds.Add(RoiEdgeKind.Line);
                ArcVia.Add(null);
            }
        }

        /// <summary>闭合最后一段（不新增顶点）：从 <see cref="Vertices"/>[^1] 到 [0]。</summary>
        public void CloseLoop(RoiEdgeKind closingKind, Point? arcVia)
        {
            if (Vertices.Count < 3)
                return;

            while (EdgeKinds.Count < Vertices.Count - 1)
            {
                EdgeKinds.Add(RoiEdgeKind.Line);
                ArcVia.Add(null);
            }

            if (EdgeKinds.Count == Vertices.Count - 1)
            {
                if (closingKind == RoiEdgeKind.Arc && arcVia is not Point via)
                    throw new ArgumentException("圆弧闭合需要提供弧上经过点", nameof(arcVia));
                if (closingKind == RoiEdgeKind.Arc && !IsValidArcVia(Vertices[^1], via, Vertices[0]))
                    throw new ArgumentException("弧上经过点与两端共线，无法构成圆弧", nameof(arcVia));

                EdgeKinds.Add(closingKind);
                ArcVia.Add(closingKind == RoiEdgeKind.Arc ? arcVia : null);
            }

            IsClosed = true;
        }

        public static bool IsValidArcVia(Point start, Point via, Point end) =>
            !AreCollinear(start, via, end);

        public List<Point> BuildFlattenedPolygon(
            bool closed,
            int arcSegments = 24,
            Point? rubberEnd = null,
            RoiEdgeKind? rubberKind = null,
            Point? rubberVia = null)
        {
            var dest = new List<Point>();
            int n = Vertices.Count;
            if (n == 0)
                return dest;

            if (closed && n >= 3)
            {
                int closedEdges = Math.Min(n, EdgeKinds.Count);
                for (int i = 0; i < closedEdges; i++)
                    AppendEdge(i, Vertices[i], Vertices[(i + 1) % n], arcSegments, dest);

                if (closedEdges < n)
                    AppendSegmentPoints(dest, Vertices[n - 1], Vertices[0], RoiEdgeKind.Line, null, arcSegments);
                return dest;
            }

            int openEdges = Math.Max(0, n - 1);
            for (int i = 0; i < openEdges; i++)
            {
                Point start = Vertices[i];
                Point end;
                RoiEdgeKind kind;
                Point? via = null;

                if (i < n - 2)
                    end = Vertices[i + 1];
                else if (rubberEnd is Point re)
                {
                    end = re;
                    kind = rubberKind ?? RoiEdgeKind.Line;
                    via = rubberVia;
                    AppendSegmentPoints(dest, start, end, kind, via, arcSegments);
                    continue;
                }
                else
                    end = Vertices[i + 1];

                AppendEdge(i, start, end, arcSegments, dest);
            }

            return dest;
        }

        private void AppendEdge(int edgeIndex, Point start, Point end, int arcSegments, List<Point> dest)
        {
            RoiEdgeKind kind = edgeIndex < EdgeKinds.Count ? EdgeKinds[edgeIndex] : RoiEdgeKind.Line;
            Point? via = null;
            if (kind == RoiEdgeKind.Arc && edgeIndex < ArcVia.Count)
                via = ArcVia[edgeIndex];
            AppendSegmentPoints(dest, start, end, kind, via, arcSegments);
        }

        public static void AppendSegmentPoints(
            List<Point> dest,
            Point start,
            Point end,
            RoiEdgeKind kind,
            Point? via,
            int arcSegments)
        {
            if (dest.Count == 0 || Dist(dest[dest.Count - 1], start) > 1e-6)
                dest.Add(start);

            if (kind == RoiEdgeKind.Arc && via is Point v &&
                !AreCollinear(start, v, end))
            {
                bool first = true;
                foreach (var p in SampleArc(start, v, end, arcSegments))
                {
                    if (first)
                    {
                        first = false;
                        continue;
                    }

                    dest.Add(p);
                }
            }
            else
            {
                dest.Add(end);
            }
        }

        public static IEnumerable<Point> SampleArc(Point start, Point via, Point end, int segments)
        {
            if (!TryCircleFromThreePoints(start, via, end, out Point center, out double radius))
            {
                yield return start;
                yield return end;
                yield break;
            }

            double a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
            double av = Math.Atan2(via.Y - center.Y, via.X - center.X);
            double a1 = Math.Atan2(end.Y - center.Y, end.X - center.X);
            double sweepCcw = NormalizeSweep(a0, a1, ccw: true);
            double tv = NormalizeAngle(av - a0);
            bool viaOnCcw = tv <= sweepCcw + 1e-9;
            double sweep = viaOnCcw ? sweepCcw : NormalizeSweep(a0, a1, ccw: false);
            if (Math.Abs(sweep) < 1e-9)
                sweep = viaOnCcw ? NormalizeSweep(a0, a1, ccw: false) : sweepCcw;

            int segs = Math.Max(4, segments);
            for (int i = 0; i <= segs; i++)
            {
                double ang = a0 + sweep * i / segs;
                yield return new Point(
                    center.X + radius * Math.Cos(ang),
                    center.Y + radius * Math.Sin(ang));
            }
        }

        private static double NormalizeSweep(double a0, double a1, bool ccw)
        {
            double diff = a1 - a0;
            if (ccw)
            {
                while (diff <= 0) diff += 2 * Math.PI;
                while (diff > 2 * Math.PI) diff -= 2 * Math.PI;
            }
            else
            {
                while (diff >= 0) diff -= 2 * Math.PI;
                while (diff < -2 * Math.PI) diff += 2 * Math.PI;
            }

            return diff;
        }

        private static double NormalizeAngle(double a)
        {
            while (a < 0) a += 2 * Math.PI;
            while (a >= 2 * Math.PI) a -= 2 * Math.PI;
            return a;
        }

        public static bool TryGetArcGeometricRadius(Point start, Point via, Point end, out double radiusPx)
        {
            radiusPx = 0;
            if (!TryCircleFromThreePoints(start, via, end, out _, out radiusPx))
                return false;
            return radiusPx > 1e-6;
        }

        /// <summary>固定起点、终点，按圆半径求弧上点（sideHint 决定鼓出侧）。</summary>
        public static bool TryViaFromChordAndRadius(
            Point start,
            Point end,
            double radiusPx,
            Point sideHint,
            out Point via)
        {
            via = default;
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-6)
                return false;

            double minR = chord * 0.5 + 1e-3;
            if (radiusPx < minR)
                radiusPx = minR;

            double mx = (start.X + end.X) * 0.5;
            double my = (start.Y + end.Y) * 0.5;
            double nx = -dy / chord;
            double ny = dx / chord;
            double h = Math.Sqrt(Math.Max(0, radiusPx * radiusPx - (chord * 0.5) * (chord * 0.5)));
            var viaPlus = new Point(mx + nx * h, my + ny * h);
            var viaMinus = new Point(mx - nx * h, my - ny * h);
            via = Dist(viaPlus, sideHint) <= Dist(viaMinus, sideHint) ? viaPlus : viaMinus;
            return IsValidArcVia(start, via, end);
        }

        internal static bool TryCircleFromThreePoints(Point a, Point b, Point c, out Point center, out double radius)
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

        private static bool AreCollinear(Point a, Point b, Point c)
        {
            double cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            return Math.Abs(cross) < 1e-6;
        }

        private static double Dist(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>闭合时边数=顶点数；开放时边数=顶点数-1。</summary>
        public static int GetEdgeCount(RoiContourPath path)
        {
            if (path == null || path.VertexCount < 2)
                return 0;
            if (path.IsClosed && path.VertexCount >= 3)
                return path.VertexCount;
            return path.VertexCount - 1;
        }

        public static void GetEdgeEndpoints(RoiContourPath path, int edgeIndex, out Point start, out Point end)
        {
            int n = path.VertexCount;
            if (n < 2 || edgeIndex < 0 || edgeIndex >= GetEdgeCount(path))
            {
                start = end = default;
                return;
            }

            start = path.Vertices[edgeIndex];
            end = path.IsClosed
                ? path.Vertices[(edgeIndex + 1) % n]
                : path.Vertices[edgeIndex + 1];
        }

        public static RoiEdgeKind GetEdgeKind(RoiContourPath path, int edgeIndex) =>
            edgeIndex >= 0 && edgeIndex < path.EdgeKinds.Count
                ? path.EdgeKinds[edgeIndex]
                : RoiEdgeKind.Line;

        /// <summary>
        /// 命中距点击点最近的边；容差单位为图像像素。
        /// 直线用点到线段距离；圆弧用采样折线距离。
        /// </summary>
        public static bool TryHitEdge(
            RoiContourPath path,
            Point imagePt,
            double tolerancePx,
            out int edgeIndex,
            out double distancePx)
        {
            edgeIndex = -1;
            distancePx = double.MaxValue;
            int edges = GetEdgeCount(path);
            if (edges <= 0 || tolerancePx <= 0)
                return false;

            for (int i = 0; i < edges; i++)
            {
                GetEdgeEndpoints(path, i, out Point start, out Point end);
                RoiEdgeKind kind = GetEdgeKind(path, i);
                double d;
                if (kind == RoiEdgeKind.Arc
                    && i < path.ArcVia.Count
                    && path.ArcVia[i] is Point via
                    && IsValidArcVia(start, via, end))
                {
                    d = DistanceToSampledArc(imagePt, start, via, end);
                }
                else
                {
                    d = DistanceToSegment(imagePt, start, end);
                }

                if (d < distancePx)
                {
                    distancePx = d;
                    edgeIndex = i;
                }
            }

            return edgeIndex >= 0 && distancePx <= tolerancePx;
        }

        /// <summary>固定起点，改写该边终点（开放/闭合均可用）；直线边会清掉弧上点。</summary>
        public static bool TrySetLineEdgeEnd(RoiContourPath path, int edgeIndex, Point newEnd, out string error)
        {
            error = "";
            int edges = GetEdgeCount(path);
            if (edgeIndex < 0 || edgeIndex >= edges)
            {
                error = "段序号无效";
                return false;
            }

            GetEdgeEndpoints(path, edgeIndex, out Point start, out _);
            if (Dist(start, newEnd) < 0.5)
            {
                error = "终点与起点过近";
                return false;
            }

            int n = path.VertexCount;
            int endIdx = path.IsClosed ? (edgeIndex + 1) % n : edgeIndex + 1;
            path.Vertices[endIdx] = newEnd;
            EnsureEdgeLists(path, path.IsClosed ? n : Math.Max(n - 1, 0));
            if (edgeIndex < path.EdgeKinds.Count)
                path.EdgeKinds[edgeIndex] = RoiEdgeKind.Line;
            if (edgeIndex < path.ArcVia.Count)
                path.ArcVia[edgeIndex] = null;
            return true;
        }

        /// <summary>改写圆弧边的弧上点（半径随之变化）。</summary>
        public static bool TrySetArcEdgeVia(RoiContourPath path, int edgeIndex, Point via, out string error)
        {
            error = "";
            int edges = GetEdgeCount(path);
            if (edgeIndex < 0 || edgeIndex >= edges)
            {
                error = "段序号无效";
                return false;
            }

            GetEdgeEndpoints(path, edgeIndex, out Point start, out Point end);
            if (!IsValidArcVia(start, via, end))
            {
                error = "弧上点与两端共线，无法构成圆弧";
                return false;
            }

            EnsureEdgeLists(path, path.IsClosed ? path.VertexCount : Math.Max(path.VertexCount - 1, 0));
            path.EdgeKinds[edgeIndex] = RoiEdgeKind.Arc;
            path.ArcVia[edgeIndex] = via;
            return true;
        }

        /// <summary>采样该边折线点（含起终点），用于高亮绘制。</summary>
        public static List<Point> SampleEdgePoints(RoiContourPath path, int edgeIndex, int arcSegments = 24)
        {
            var dest = new List<Point>();
            int edges = GetEdgeCount(path);
            if (edgeIndex < 0 || edgeIndex >= edges)
                return dest;

            GetEdgeEndpoints(path, edgeIndex, out Point start, out Point end);
            RoiEdgeKind kind = GetEdgeKind(path, edgeIndex);
            Point? via = null;
            if (kind == RoiEdgeKind.Arc && edgeIndex < path.ArcVia.Count)
                via = path.ArcVia[edgeIndex];
            AppendSegmentPoints(dest, start, end, kind, via, arcSegments);
            return dest;
        }

        private static void EnsureEdgeLists(RoiContourPath path, int edgeCount)
        {
            while (path.EdgeKinds.Count < edgeCount)
            {
                path.EdgeKinds.Add(RoiEdgeKind.Line);
                path.ArcVia.Add(null);
            }

            while (path.ArcVia.Count < path.EdgeKinds.Count)
                path.ArcVia.Add(null);
        }

        private static double DistanceToSegment(Point p, Point a, Point b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-12)
                return Dist(p, a);

            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            t = Math.Clamp(t, 0, 1);
            return Dist(p, new Point(a.X + t * dx, a.Y + t * dy));
        }

        private static double DistanceToSampledArc(Point p, Point start, Point via, Point end)
        {
            double best = Dist(p, start);
            Point prev = start;
            bool first = true;
            foreach (Point s in SampleArc(start, via, end, 32))
            {
                if (first)
                {
                    first = false;
                    prev = s;
                    continue;
                }

                best = Math.Min(best, DistanceToSegment(p, prev, s));
                prev = s;
            }

            best = Math.Min(best, Dist(p, end));
            return best;
        }
    }
}
