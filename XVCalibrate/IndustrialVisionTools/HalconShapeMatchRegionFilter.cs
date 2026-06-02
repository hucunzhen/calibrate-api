#if HALCON_ENABLED
using System;
using System.Collections.Generic;
using System.Linq;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 用区域模板 Find 结果过滤待选匹配：HALCON 填充 Region + TestRegionPoint，失败时回退多边形。
    /// </summary>
    internal static class HalconShapeMatchRegionFilter
    {
        public sealed class FilterResult
        {
            public double[] Rows { get; init; } = Array.Empty<double>();
            public double[] Columns { get; init; } = Array.Empty<double>();
            public double[] Angles { get; init; } = Array.Empty<double>();
            public double[] Scores { get; init; } = Array.Empty<double>();
            public int[] KeptIndices { get; init; } = Array.Empty<int>();
            public int InputCount { get; init; }
            public int RegionMatchCount { get; init; }
            public int ValidRegionCount { get; init; }
            public int InsideRegionCount { get; init; }
            public double MinSeparationPx { get; init; }
            public bool TouchFilterEnabled { get; init; }
            public bool UsedHalconRegion { get; init; }
            public string? RegionDiagnostic { get; init; }
        }

        public static FilterResult Filter(
            long regionModelId,
            double[] regionRows,
            double[] regionCols,
            double[]? regionAngles,
            long queryModelId,
            double[] queryRows,
            double[] queryCols,
            double[]? queryAngles,
            double[]? queryScores,
            int contourLevel,
            double insetPx,
            double minSeparationPx)
        {
            if (regionModelId < 0)
                throw new ArgumentException("区域模板 ModelId 无效");
            if (queryModelId < 0)
                throw new ArgumentException("待过滤模板 ModelId 无效");

            int nRegion = Math.Min(regionRows?.Length ?? 0, regionCols?.Length ?? 0);
            int nQuery = Math.Min(queryRows?.Length ?? 0, queryCols?.Length ?? 0);

            if (nRegion == 0)
            {
                return Empty(queryRows, queryCols, queryAngles, queryScores,
                    "区域匹配为空：请检查 RegionRow/Column 是否已连接且上游 Find 有结果");
            }

            bool touchFilter = minSeparationPx > 0;
            double sep = touchFilter ? minSeparationPx : 0;

            HalconFlowBridge.ShapeMatchRegionMask[]? halconMasks = null;
            PolygonRegionInstance[]? polyRegions = null;
            bool usedHalcon = false;
            int validRegion = 0;
            string? diag = null;

            try
            {
                try
                {
                    halconMasks = HalconFlowBridge.BuildShapeMatchFilledRegions(
                        regionModelId, regionRows, regionCols, regionAngles, scales: null, contourLevel, insetPx);
                    validRegion = CountValidHalconMasks(halconMasks);
                    if (validRegion > 0)
                        usedHalcon = true;
                    else
                    {
                        diag = "HALCON Region 生成失败，已回退多边形判定";
                        polyRegions = BuildPolygonRegionInstances(
                            regionModelId, regionRows, regionCols, regionAngles, nRegion, contourLevel);
                        validRegion = CountValidPolyRegions(polyRegions);
                    }
                }
                catch (Exception ex)
                {
                    diag = $"HALCON Region 异常({ex.Message})，已回退多边形";
                    polyRegions = BuildPolygonRegionInstances(
                        regionModelId, regionRows, regionCols, regionAngles, nRegion, contourLevel);
                    validRegion = CountValidPolyRegions(polyRegions);
                }

                if (validRegion == 0)
                {
                    return Empty(queryRows, queryCols, queryAngles, queryScores,
                        $"无法获取有效区域(区域匹配{nRegion}个)：检查 RegionModelId 与轮廓层 contourLevel={contourLevel}",
                        nRegion, validRegion, usedHalcon, diag);
                }

                var insideByRegion = new List<(int queryIdx, int regionIdx)>();
                for (int i = 0; i < nQuery; i++)
                {
                    double col = queryCols![i];
                    double row = queryRows![i];
                    int regionIdx = usedHalcon
                        ? HalconFlowBridge.FindShapeMatchRegionIndex(halconMasks!, row, col)
                        : FindContainingPolygonRegion(col, row, polyRegions!, insetPx);
                    if (regionIdx >= 0)
                        insideByRegion.Add((i, regionIdx));
                }

                var kept = ApplyTouchFilter(insideByRegion, queryRows, queryCols, queryScores, touchFilter, sep);
                kept.Sort();
                return Pack(queryRows, queryCols, queryAngles, queryScores, kept, nQuery, nRegion, validRegion,
                    insideByRegion.Count, sep, touchFilter, usedHalcon, diag);
            }
            finally
            {
                if (halconMasks != null)
                {
                    foreach (var m in halconMasks)
                        m?.Dispose();
                }
            }
        }

        private static List<int> ApplyTouchFilter(
            List<(int queryIdx, int regionIdx)> insideByRegion,
            double[] queryRows,
            double[] queryCols,
            double[]? queryScores,
            bool touchFilter,
            double sep)
        {
            var kept = new List<int>();
            if (!touchFilter)
            {
                foreach ((int queryIdx, _) in insideByRegion)
                    kept.Add(queryIdx);
                return kept;
            }

            var buckets = new Dictionary<int, List<int>>();
            foreach ((int queryIdx, int regionIdx) in insideByRegion)
            {
                if (!buckets.TryGetValue(regionIdx, out var list))
                {
                    list = new List<int>();
                    buckets[regionIdx] = list;
                }

                list.Add(queryIdx);
            }

            foreach (var bucket in buckets.Values)
            {
                bucket.Sort((a, b) =>
                {
                    double sa = queryScores != null && a < queryScores.Length ? queryScores[a] : 0;
                    double sb = queryScores != null && b < queryScores.Length ? queryScores[b] : 0;
                    return sb.CompareTo(sa);
                });

                var localKept = new List<int>();
                foreach (int idx in bucket)
                {
                    if (CanPlace(idx, queryRows, queryCols, localKept, sep))
                        localKept.Add(idx);
                }

                kept.AddRange(localKept);
            }

            return kept;
        }

        private static int CountValidHalconMasks(HalconFlowBridge.ShapeMatchRegionMask[] masks)
        {
            int c = 0;
            foreach (var m in masks)
            {
                if (m?.Region != null && m.Region.IsInitialized())
                    c++;
            }

            return c;
        }

        private sealed class PolygonRegionInstance
        {
            public Point2D[] Outer { get; set; } = Array.Empty<Point2D>();
            public List<Point2D[]> Holes { get; } = new();
        }

        private static int CountValidPolyRegions(PolygonRegionInstance[] regions)
        {
            int c = 0;
            foreach (var r in regions)
            {
                if (r.Outer.Length >= 3)
                    c++;
            }

            return c;
        }

        private static FilterResult Empty(
            double[]? rows,
            double[]? cols,
            double[]? angles,
            double[]? scores,
            string diagnostic,
            int regionMatchCount = 0,
            int validRegionCount = 0,
            bool usedHalcon = false,
            string? extraDiag = null)
        {
            return new FilterResult
            {
                Rows = Array.Empty<double>(),
                Columns = Array.Empty<double>(),
                Angles = Array.Empty<double>(),
                Scores = Array.Empty<double>(),
                KeptIndices = Array.Empty<int>(),
                InputCount = Math.Min(rows?.Length ?? 0, cols?.Length ?? 0),
                RegionMatchCount = regionMatchCount,
                ValidRegionCount = validRegionCount,
                RegionDiagnostic = extraDiag == null ? diagnostic : $"{diagnostic}; {extraDiag}",
                UsedHalconRegion = usedHalcon
            };
        }

        private static FilterResult Pack(
            double[] queryRows,
            double[] queryCols,
            double[]? queryAngles,
            double[]? queryScores,
            List<int> kept,
            int inputCount,
            int regionMatchCount,
            int validRegionCount,
            int insideCount,
            double sep,
            bool touchFilter,
            bool usedHalcon,
            string? diag)
        {
            var outR = new double[kept.Count];
            var outC = new double[kept.Count];
            var outA = new double[kept.Count];
            var outS = new double[kept.Count];
            for (int k = 0; k < kept.Count; k++)
            {
                int i = kept[k];
                outR[k] = queryRows[i];
                outC[k] = queryCols[i];
                outA[k] = queryAngles != null && i < queryAngles.Length ? queryAngles[i] : 0;
                outS[k] = queryScores != null && i < queryScores.Length ? queryScores[i] : 0;
            }

            return new FilterResult
            {
                Rows = outR,
                Columns = outC,
                Angles = outA,
                Scores = outS,
                KeptIndices = kept.ToArray(),
                InputCount = inputCount,
                RegionMatchCount = regionMatchCount,
                ValidRegionCount = validRegionCount,
                InsideRegionCount = insideCount,
                MinSeparationPx = sep,
                TouchFilterEnabled = touchFilter,
                UsedHalconRegion = usedHalcon,
                RegionDiagnostic = diag
            };
        }

        private static PolygonRegionInstance[] BuildPolygonRegionInstances(
            long modelId,
            double[] rows,
            double[] cols,
            double[]? angles,
            int n,
            int contourLevel)
        {
            Point2D[][] templates = HalconFlowBridge.GetShapeModelContourPoints(modelId, contourLevel);
            var instances = new PolygonRegionInstance[n];
            for (int m = 0; m < n; m++)
            {
                double matchRow = rows[m];
                double matchCol = cols[m];
                double angleDeg = angles != null && angles.Length > m ? angles[m] : 0;

                var polys = new List<(double area, Point2D[] pts)>();
                var allPts = new List<Point2D>();
                foreach (Point2D[] contour in templates)
                {
                    if (contour == null || contour.Length < 2)
                        continue;

                    var imgPts = new Point2D[contour.Length];
                    for (int i = 0; i < contour.Length; i++)
                    {
                        TransformModelPointToImage(contour[i], matchRow, matchCol, angleDeg, out double colImg, out double rowImg);
                        imgPts[i] = new Point2D(colImg, rowImg);
                        allPts.Add(imgPts[i]);
                    }

                    double area = Math.Abs(PolygonSignedArea(imgPts));
                    if (area >= 4 && imgPts.Length >= 3)
                        polys.Add((area, imgPts));
                }

                instances[m] = new PolygonRegionInstance();
                if (polys.Count == 0)
                {
                    Point2D[] hull = ConvexHull(allPts);
                    if (hull.Length >= 3)
                        instances[m].Outer = hull;
                    continue;
                }

                polys.Sort((a, b) => b.area.CompareTo(a.area));
                instances[m].Outer = polys[0].pts;
                for (int pi = 1; pi < polys.Count; pi++)
                {
                    if (polys[pi].area >= polys[0].area * 0.02)
                        instances[m].Holes.Add(polys[pi].pts);
                }
            }

            return instances;
        }

        private static int FindContainingPolygonRegion(double col, double row, PolygonRegionInstance[] regions, double insetPx)
        {
            for (int ri = 0; ri < regions.Length; ri++)
            {
                if (IsInsidePolygonRegion(col, row, regions[ri], insetPx))
                    return ri;
            }

            return -1;
        }

        private static bool IsInsidePolygonRegion(double col, double row, PolygonRegionInstance region, double insetPx)
        {
            if (region.Outer.Length < 3)
                return false;
            if (!PointInPolygon(col, row, region.Outer))
                return false;
            foreach (Point2D[] hole in region.Holes)
            {
                if (hole.Length >= 3 && PointInPolygon(col, row, hole))
                    return false;
            }

            if (insetPx > 0 && DistanceToPolygonBoundary(col, row, region.Outer) < insetPx)
                return false;

            return true;
        }

        private static bool CanPlace(int idx, double[] rows, double[] cols, List<int> kept, double minSepPx)
        {
            double col = cols[idx];
            double row = rows[idx];
            foreach (int k in kept)
            {
                double dx = col - cols[k];
                double dy = row - rows[k];
                if (dx * dx + dy * dy < minSepPx * minSepPx)
                    return false;
            }

            return true;
        }

        private static double PolygonSignedArea(IReadOnlyList<Point2D> polygon)
        {
            double a = 0;
            int n = polygon.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
                a += (polygon[j].X - polygon[i].X) * (polygon[j].Y + polygon[i].Y);
            return a * 0.5;
        }

        private static Point2D[] ConvexHull(List<Point2D> points)
        {
            if (points.Count < 3)
                return points.Count == 0 ? Array.Empty<Point2D>() : points.ToArray();

            var pts = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
            var lower = new List<Point2D>();
            foreach (Point2D p in pts)
            {
                while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }

            var upper = new List<Point2D>();
            for (int i = pts.Count - 1; i >= 0; i--)
            {
                Point2D p = pts[i];
                while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower.ToArray();
        }

        private static double Cross(Point2D a, Point2D b, Point2D c) =>
            (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        private static double DistanceToPolygonBoundary(double x, double y, IReadOnlyList<Point2D> polygon)
        {
            double min = double.MaxValue;
            int n = polygon.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double d = DistancePointToSegment(
                    x, y,
                    polygon[j].X, polygon[j].Y,
                    polygon[i].X, polygon[i].Y);
                if (d < min)
                    min = d;
            }

            return min;
        }

        private static double DistancePointToSegment(
            double px, double py,
            double ax, double ay,
            double bx, double by)
        {
            double dx = bx - ax;
            double dy = by - ay;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-12)
                return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));

            double t = ((px - ax) * dx + (py - ay) * dy) / len2;
            t = Math.Clamp(t, 0, 1);
            double qx = ax + t * dx;
            double qy = ay + t * dy;
            double ex = px - qx;
            double ey = py - qy;
            return Math.Sqrt(ex * ex + ey * ey);
        }

        private static bool PointInPolygon(double x, double y, IReadOnlyList<Point2D> polygon)
        {
            if (polygon == null || polygon.Count < 3)
                return false;

            bool inside = false;
            int n = polygon.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double yi = polygon[i].Y;
                double yj = polygon[j].Y;
                double xi = polygon[i].X;
                double xj = polygon[j].X;
                if ((yi > y) != (yj > y) &&
                    x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi)
                    inside = !inside;
            }

            return inside;
        }

        private static void TransformModelPointToImage(
            Point2D modelPt,
            double matchRow,
            double matchCol,
            double angleDeg,
            out double colImg,
            out double rowImg)
        {
            double a = angleDeg * Math.PI / 180.0;
            double c = Math.Cos(a);
            double s = Math.Sin(a);
            double row = modelPt.Y;
            double col = modelPt.X;
            rowImg = row * c - col * s + matchRow;
            colImg = row * s + col * c + matchCol;
        }
    }
}
#endif
