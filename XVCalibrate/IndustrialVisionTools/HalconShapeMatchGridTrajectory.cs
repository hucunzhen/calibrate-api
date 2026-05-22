using System;
using System.Collections.Generic;
using System.Linq;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>落格匹配按格网顺序拼接的轮廓轨迹（图像像素坐标）。</summary>
    public sealed class HalconShapeMatchGridTrajectoryResult
    {
        public Point2D[] Points { get; init; } = Array.Empty<Point2D>();
        /// <summary>每条闭合轮廓段一个条号（0,1,2…），用于显示/简化时禁止跨轮廓连线。</summary>
        public int[] BarIds { get; init; } = Array.Empty<int>();
        /// <summary>按参数 barIdSource 的焊道/分组条号（可与 BarIds 不同）。</summary>
        public int[] GroupBarIds { get; init; } = Array.Empty<int>();
        public CalibPoint3D[] SamplePts { get; init; } = Array.Empty<CalibPoint3D>();
        public int MatchCount { get; init; }
        public int SegmentCount { get; init; }
        public int PointCount { get; init; }
        public string OrderSummary { get; init; } = "";
    }

    /// <summary>将落格后的匹配位姿 + 形状模板轮廓变换到图像，并按格网顺序拼成轨迹点列。</summary>
    public static class HalconShapeMatchGridTrajectory
    {
        public static HalconShapeMatchGridTrajectoryResult Build(
            long modelId,
            double[] rows,
            double[] cols,
            double[]? angles,
            int[]? gridRow,
            int[]? gridCol,
            int contourLevel,
            string contourMode,
            string connectOrder,
            string barIdSource,
            double closeTolPx,
            double defaultZ,
            int latticeGridRows = 0,
            int latticeGridCols = 0)
        {
#if !HALCON_ENABLED
            _ = modelId;
            throw new NotSupportedException("HALCON 落格匹配轮廓→轨迹 需要启用 HALCON（HALCON_ENABLED）");
#else
            if (modelId < 0)
                throw new ArgumentException("ModelId 无效，请连接 halcon_load_shape_model 或与落格匹配相同的模板");

            rows ??= Array.Empty<double>();
            cols ??= Array.Empty<double>();
            int n = Math.Min(rows.Length, cols.Length);
            if (n == 0)
            {
                return new HalconShapeMatchGridTrajectoryResult
                {
                    OrderSummary = "无落格匹配"
                };
            }

            if (closeTolPx <= 0)
                closeTolPx = 0.5;

            Point2D[][] templates = HalconFlowBridge.GetShapeModelContourPoints(modelId, contourLevel);
            if (templates.Length == 0)
                throw new InvalidOperationException($"形状模型 ModelId={modelId} 无轮廓（ContourLevel={contourLevel}）");

            bool hasGrid = gridRow != null && gridCol != null &&
                           gridRow.Length >= n && gridCol.Length >= n;

            var items = new List<(int Idx, int Gr, int Gc, double Row, double Col, double Angle)>(n);
            for (int i = 0; i < n; i++)
            {
                int gr = hasGrid ? gridRow![i] : i;
                int gc = hasGrid ? gridCol![i] : 0;
                double ang = angles != null && angles.Length > i ? angles[i] : 0;
                items.Add((i, gr, gc, rows[i], cols[i], ang));
            }

            var ordered = OrderItems(items, connectOrder);
            bool useAllContours = string.Equals(
                (contourMode ?? "outer").Trim(),
                "all",
                StringComparison.OrdinalIgnoreCase);

            var outPts = new List<Point2D>();
            var outBarIds = new List<int>();
            var outGroupBarIds = new List<int>();
            int nextSegmentBarId = 0;
            int matchSeq = 0;

            int gridColsForCell = latticeGridCols > 0
                ? latticeGridCols
                : (hasGrid ? items.Max(t => t.Gc) + 1 : 1);

            foreach (var it in ordered)
            {
                int groupBarId = ResolveGroupBarId(it, matchSeq, barIdSource, gridColsForCell);
                if (useAllContours)
                    nextSegmentBarId = AppendAllTransformedContours(
                        outPts, outBarIds, outGroupBarIds, templates, it.Row, it.Col, it.Angle,
                        nextSegmentBarId, groupBarId, closeTolPx);
                else
                    nextSegmentBarId = AppendOuterContour(
                        outPts, outBarIds, outGroupBarIds, templates, it.Row, it.Col, it.Angle,
                        nextSegmentBarId, groupBarId, closeTolPx);
                matchSeq++;
            }

            var pts = outPts.ToArray();
            var barIds = outBarIds.ToArray();
            var groupBarIds = outGroupBarIds.ToArray();
            var sample = new CalibPoint3D[pts.Length];
            for (int k = 0; k < pts.Length; k++)
                sample[k] = new CalibPoint3D(pts[k].X, pts[k].Y, defaultZ);

            string orderTag = NormalizeConnectOrder(connectOrder);
            string barTag = NormalizeBarIdSource(barIdSource);
            string contourTag = useAllContours ? "全部子轮廓" : "外形";
            int uniqGroup = groupBarIds.Length > 0 ? groupBarIds.Distinct().Count() : 0;
            string gridHint = hasGrid
                ? $" GridRow/Col 已接"
                : "";
            return new HalconShapeMatchGridTrajectoryResult
            {
                Points = pts,
                BarIds = barIds,
                GroupBarIds = groupBarIds,
                SamplePts = sample,
                MatchCount = ordered.Count,
                SegmentCount = nextSegmentBarId,
                PointCount = pts.Length,
                OrderSummary =
                    $"{ordered.Count} 匹配 · GroupBarId {uniqGroup} 种 · {nextSegmentBarId} 闭合段 · {pts.Length} 点 · {contourTag} · {orderTag} · 分组={barTag}{gridHint}"
            };
#endif
        }

#if HALCON_ENABLED
        private static int AppendOuterContour(
            List<Point2D> outPts,
            List<int> outBarIds,
            List<int> outGroupBarIds,
            Point2D[][] templates,
            double matchRow,
            double matchCol,
            double angleDeg,
            int nextSegmentBarId,
            int groupBarId,
            double closeTolPx)
        {
            Point2D[] outer = HalconFlowBridge.GetOuterTransformedShapeContour(
                templates, matchRow, matchCol, angleDeg);
            return AppendClosedContourSegment(
                outPts, outBarIds, outGroupBarIds, outer, nextSegmentBarId, groupBarId, closeTolPx);
        }

        private static int AppendAllTransformedContours(
            List<Point2D> outPts,
            List<int> outBarIds,
            List<int> outGroupBarIds,
            Point2D[][] templates,
            double matchRow,
            double matchCol,
            double angleDeg,
            int nextSegmentBarId,
            int groupBarId,
            double closeTolPx)
        {
            foreach (Point2D[] contour in templates)
            {
                if (contour == null || contour.Length < 2)
                    continue;
                Point2D[] img = HalconFlowBridge.TransformShapeModelContourToImage(
                    contour, matchRow, matchCol, angleDeg);
                nextSegmentBarId = AppendClosedContourSegment(
                    outPts, outBarIds, outGroupBarIds, img, nextSegmentBarId, groupBarId, closeTolPx);
            }

            return nextSegmentBarId;
        }

        /// <summary>追加一条闭合折线段；BarIds 为段号，避免与下一段首尾相连。</summary>
        private static int AppendClosedContourSegment(
            List<Point2D> outPts,
            List<int> outBarIds,
            List<int> outGroupBarIds,
            Point2D[] contour,
            int nextSegmentBarId,
            int groupBarId,
            double closeTolPx)
        {
            if (contour == null || contour.Length < 2)
                return nextSegmentBarId;

            Point2D[] closed = HalconFlowBridge.EnsureClosedContourPoints(contour, closeTolPx, forceClose: true);
            int segId = nextSegmentBarId;
            foreach (Point2D p in closed)
            {
                outPts.Add(p);
                outBarIds.Add(segId);
                outGroupBarIds.Add(groupBarId);
            }

            return nextSegmentBarId + 1;
        }

#endif

        private static int ResolveGroupBarId(
            (int Idx, int Gr, int Gc, double Row, double Col, double Angle) it,
            int matchSeq,
            string barIdSource,
            int latticeGridCols)
        {
            switch (NormalizeBarIdSource(barIdSource))
            {
                case "grid_cell":
                    int cols = Math.Max(1, latticeGridCols);
                    return it.Gr * cols + it.Gc;
                case "grid_col":
                    return it.Gc;
                case "grid_row":
                    return it.Gr;
                case "single":
                    return 0;
                case "per_match":
                default:
                    return matchSeq;
            }
        }

        private static List<(int Idx, int Gr, int Gc, double Row, double Col, double Angle)> OrderItems(
            List<(int Idx, int Gr, int Gc, double Row, double Col, double Angle)> items,
            string connectOrder)
        {
            switch (NormalizeConnectOrder(connectOrder))
            {
                case "col_major":
                    return items
                        .OrderBy(t => t.Gc)
                        .ThenBy(t => t.Gr)
                        .ThenBy(t => t.Row)
                        .ThenBy(t => t.Col)
                        .ToList();
                case "snake_col":
                    return SnakeByColumn(items);
                case "snake_row":
                    return SnakeByRow(items);
                case "row_major":
                default:
                    return items
                        .OrderBy(t => t.Gr)
                        .ThenBy(t => t.Gc)
                        .ThenBy(t => t.Col)
                        .ThenBy(t => t.Row)
                        .ToList();
            }
        }

        private static List<(int Idx, int Gr, int Gc, double Row, double Col, double Angle)> SnakeByRow(
            List<(int Idx, int Gr, int Gc, double Row, double Col, double Angle)> items)
        {
            var outList = new List<(int Idx, int Gr, int Gc, double Row, double Col, double Angle)>();
            foreach (var grp in items.GroupBy(t => t.Gr).OrderBy(g => g.Key))
            {
                bool rev = (grp.Key & 1) == 1;
                var row = rev
                    ? grp.OrderByDescending(t => t.Gc).ThenByDescending(t => t.Col)
                    : grp.OrderBy(t => t.Gc).ThenBy(t => t.Col);
                outList.AddRange(row);
            }

            return outList;
        }

        private static List<(int Idx, int Gr, int Gc, double Row, double Col, double Angle)> SnakeByColumn(
            List<(int Idx, int Gr, int Gc, double Row, double Col, double Angle)> items)
        {
            var outList = new List<(int Idx, int Gr, int Gc, double Row, double Col, double Angle)>();
            foreach (var grp in items.GroupBy(t => t.Gc).OrderBy(g => g.Key))
            {
                bool rev = (grp.Key & 1) == 1;
                var col = rev
                    ? grp.OrderByDescending(t => t.Gr).ThenByDescending(t => t.Row)
                    : grp.OrderBy(t => t.Gr).ThenBy(t => t.Row);
                outList.AddRange(col);
            }

            return outList;
        }

        private static string NormalizeConnectOrder(string? raw)
        {
            var s = (raw ?? "col_major").Trim().ToLowerInvariant();
            return s switch
            {
                "col_major" or "colmajor" or "column" => "col_major",
                "snake_row" or "snakerow" => "snake_row",
                "snake_col" or "snakecol" or "snake_column" => "snake_col",
                _ => "row_major"
            };
        }

        private static string NormalizeBarIdSource(string? raw)
        {
            var s = (raw ?? "per_match").Trim().ToLowerInvariant();
            return s switch
            {
                "grid_row" or "row" => "grid_row",
                "grid_col" or "col" or "column" => "grid_col",
                "grid_cell" or "cell" or "lattice" => "grid_cell",
                "single" or "one" or "0" => "single",
                "per_match" or "match" or "index" => "per_match",
                _ => "per_match"
            };
        }
    }
}
