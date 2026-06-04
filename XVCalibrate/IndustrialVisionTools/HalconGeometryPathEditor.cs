using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using CalibOperatorPInvoke;

#if HALCON_ENABLED
namespace CalibOperatorCLI_Example
{
    /// <summary>测量几何「手绘路径」：保留顶点/弧段结构并支持逐段手调。</summary>
    public static class HalconGeometryPathEditor
    {
        public static RoiContourPath ClonePath(RoiContourPath? src)
        {
            var dst = new RoiContourPath();
            if (src == null)
                return dst;

            dst.IsClosed = src.IsClosed;
            foreach (Point p in src.Vertices)
                dst.Vertices.Add(p);
            foreach (RoiEdgeKind k in src.EdgeKinds)
                dst.EdgeKinds.Add(k);
            foreach (Point? v in src.ArcVia)
                dst.ArcVia.Add(v);
            return dst;
        }

        public static int GetClosedEdgeCount(RoiContourPath path) =>
            path.IsClosed && path.VertexCount >= 3 ? path.VertexCount : 0;

        public static void GetSegmentEndpoints(RoiContourPath path, int edgeIndex, out Point start, out Point end)
        {
            int n = path.VertexCount;
            if (n < 2 || edgeIndex < 0 || edgeIndex >= n)
            {
                start = end = default;
                return;
            }

            start = path.Vertices[edgeIndex];
            end = path.IsClosed ? path.Vertices[(edgeIndex + 1) % n] : path.Vertices[Math.Min(edgeIndex + 1, n - 1)];
        }

        public static RoiEdgeKind GetSegmentKind(RoiContourPath path, int edgeIndex) =>
            edgeIndex >= 0 && edgeIndex < path.EdgeKinds.Count
                ? path.EdgeKinds[edgeIndex]
                : RoiEdgeKind.Line;

        public static Point? GetSegmentArcVia(RoiContourPath path, int edgeIndex) =>
            edgeIndex >= 0 && edgeIndex < path.ArcVia.Count
                ? path.ArcVia[edgeIndex]
                : null;

        public static string FormatSegmentLabel(RoiContourPath path, int edgeIndex)
        {
            GetSegmentEndpoints(path, edgeIndex, out Point start, out Point end);
            var kind = GetSegmentKind(path, edgeIndex);
            string kindText = kind == RoiEdgeKind.Arc ? "圆弧" : "直线";
            if (kind == RoiEdgeKind.Arc && GetSegmentArcVia(path, edgeIndex) is Point via)
            {
                return $"#{edgeIndex + 1} {kindText} ({Fmt(start)})→弧上({Fmt(via)})→({Fmt(end)})";
            }

            return $"#{edgeIndex + 1} {kindText} ({Fmt(start)})→({Fmt(end)})";
        }

        public static bool TryUpdateSegment(
            RoiContourPath path,
            int edgeIndex,
            RoiEdgeKind kind,
            double startCol,
            double startRow,
            double endCol,
            double endRow,
            double? viaCol,
            double? viaRow,
            out string error)
        {
            error = "";
            if (!path.IsClosed || path.VertexCount < 3)
            {
                error = "路径须为闭合多边形（至少 3 个顶点）";
                return false;
            }

            int n = path.VertexCount;
            if (edgeIndex < 0 || edgeIndex >= n)
            {
                error = "段序号无效";
                return false;
            }

            var start = new Point(startCol, startRow);
            var end = new Point(endCol, endRow);
            if (Dist(start, end) < 1e-3)
            {
                error = "起点与终点过近";
                return false;
            }

            EnsureEdgeLists(path, n);

            path.Vertices[edgeIndex] = start;
            path.Vertices[(edgeIndex + 1) % n] = end;
            path.EdgeKinds[edgeIndex] = kind;

            if (kind == RoiEdgeKind.Arc)
            {
                if (viaCol is not double vc || viaRow is not double vr)
                {
                    error = "圆弧段须填写弧上点 X,Y";
                    return false;
                }

                var via = new Point(vc, vr);
                if (!RoiContourPath.IsValidArcVia(start, via, end))
                {
                    error = "弧上点与两端共线，无法构成圆弧";
                    return false;
                }

                path.ArcVia[edgeIndex] = via;
            }
            else
                path.ArcVia[edgeIndex] = null;

            return true;
        }

        public static string FormatPathSummary(RoiContourPath path, AffineTransform? worldTransform)
        {
            int edges = GetClosedEdgeCount(path);
            if (edges == 0)
                return "手绘路径未就绪";

            int arcCount = path.EdgeKinds.Count(k => k == RoiEdgeKind.Arc);
            var sb = new StringBuilder();
            sb.Append($"{path.VertexCount} 顶点，{edges} 段（{arcCount} 圆弧）");
            if (worldTransform is AffineTransform t)
            {
                for (int i = 0; i < edges; i++)
                {
                    if (GetSegmentKind(path, i) != RoiEdgeKind.Arc)
                        continue;
                    GetSegmentEndpoints(path, i, out Point start, out Point end);
                    if (GetSegmentArcVia(path, i) is not Point via)
                        continue;
                    sb.Append(CultureInfo.InvariantCulture);
                    sb.Append($"；弧#{i + 1} 起")
                        .Append(HalconGeometryContourBuilder.FormatWorldPointMm(start.X, start.Y, t))
                        .Append(" 弧上")
                        .Append(HalconGeometryContourBuilder.FormatWorldPointMm(via.X, via.Y, t))
                        .Append(" 终")
                        .Append(HalconGeometryContourBuilder.FormatWorldPointMm(end.X, end.Y, t));
                }
            }

            return sb.ToString();
        }

        private static void EnsureEdgeLists(RoiContourPath path, int vertexCount)
        {
            while (path.EdgeKinds.Count < vertexCount)
            {
                path.EdgeKinds.Add(RoiEdgeKind.Line);
                path.ArcVia.Add(null);
            }

            while (path.ArcVia.Count < path.EdgeKinds.Count)
                path.ArcVia.Add(null);
        }

        private static string Fmt(Point p) =>
            $"{p.X:F0},{p.Y:F0}";

        private static double Dist(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
#endif
