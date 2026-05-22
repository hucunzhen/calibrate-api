// 轮廓转焊道路径：基座坐标系 3D 折线与回退/闭合/分段合并（与 FlowPage.xaml.cs 中 2D 焊道逻辑平行）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage : UserControl
    {
        private static List<CalibPoint3D[]> ExplodeContourTripleToPolylines3D(
            double[] flatX, double[] flatY, double[] flatZ, int[] contourLengths, int contourCount)
        {
            var list = new List<CalibPoint3D[]>();
            if (flatX == null || flatY == null || flatZ == null || contourLengths == null || contourCount <= 0)
                return list;
            int offset = 0;
            for (int i = 0; i < contourCount && i < contourLengths.Length; i++)
            {
                int len = contourLengths[i];
                if (len <= 0 || offset + len > flatX.Length || offset + len > flatY.Length || offset + len > flatZ.Length)
                {
                    offset += Math.Max(0, len);
                    continue;
                }

                var seg = new CalibPoint3D[len];
                for (int j = 0; j < len; j++)
                    seg[j] = new CalibPoint3D(flatX[offset + j], flatY[offset + j], flatZ[offset + j]);
                offset += len;
                list.Add(seg);
            }

            return list;
        }

        private static CalibPoint3D[] LiftPoint2DToBase3D(Point2D[] src, double z)
        {
            if (src == null || src.Length == 0)
                return Array.Empty<CalibPoint3D>();
            var o = new CalibPoint3D[src.Length];
            for (int i = 0; i < src.Length; i++)
                o[i] = new CalibPoint3D(src[i].X, src[i].Y, z);
            return o;
        }

        private static void SplitSampledPointsToContourPolylinesWithBarIds3D(
            CalibPoint3D[] points,
            int[]? barIds,
            out List<CalibPoint3D[]> segments,
            out List<int> segmentBarIds)
        {
            segments = new List<CalibPoint3D[]>();
            segmentBarIds = new List<int>();
            if (points == null || points.Length == 0)
                return;
            if (barIds == null || barIds.Length != points.Length)
            {
                segments.Add(points);
                segmentBarIds.Add(0);
                return;
            }

            int start = 0;
            for (int i = 1; i <= points.Length; i++)
            {
                if (i == points.Length || barIds[i] != barIds[i - 1])
                {
                    int len = i - start;
                    if (len > 0)
                    {
                        var seg = new CalibPoint3D[len];
                        Array.Copy(points, start, seg, 0, len);
                        segments.Add(seg);
                        segmentBarIds.Add(barIds[start]);
                    }

                    start = i;
                }
            }
        }

        private static (List<CalibPoint3D[]> Segments, List<int> BarIds) ZipRemoveEmptyContourSegments3D(
            List<CalibPoint3D[]> segments,
            List<int> segmentBarIds)
        {
            var s2 = new List<CalibPoint3D[]>();
            var b2 = new List<int>();
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (s == null || s.Length == 0)
                    continue;
                s2.Add(s);
                int bid = i < segmentBarIds.Count ? segmentBarIds[i] : i;
                b2.Add(bid);
            }

            return (s2, b2);
        }

        private static double PointDistance3D(CalibPoint3D a, CalibPoint3D b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double WeldPolylineOpenLength3D(CalibPoint3D[] c)
        {
            if (c == null || c.Length < 2)
                return 0;
            double s = 0;
            for (int i = 0; i < c.Length - 1; i++)
                s += PointDistance3D(c[i], c[i + 1]);
            return s;
        }

        private static bool ShouldAutoCloseContourWeld3D(CalibPoint3D[] c)
        {
            if (c == null || c.Length < 3)
                return false;
            double per = WeldPolylineOpenLength3D(c);
            if (per < 1e-9)
                return false;
            double gap = PointDistance3D(c[0], c[^1]);
            if (gap < 1e-9)
                return false;
            double medE = MedianEdgeLengthForContour3D(c);
            return gap / per <= 0.38 || (medE > 1e-12 && gap <= medE * 8.0);
        }

        private static double MedianEdgeLengthForContour3D(CalibPoint3D[] c)
        {
            if (c == null || c.Length < 2)
                return 0;
            var e = new double[c.Length - 1];
            for (int i = 0; i < c.Length - 1; i++)
                e[i] = PointDistance3D(c[i], c[i + 1]);
            Array.Sort(e);
            return e[e.Length / 2];
        }

        private static void AppendDedupePoint3D(List<CalibPoint3D> path, CalibPoint3D p)
        {
            if (path.Count > 0)
            {
                var last = path[path.Count - 1];
                if (Math.Abs(last.X - p.X) < 1e-9 && Math.Abs(last.Y - p.Y) < 1e-9 && Math.Abs(last.Z - p.Z) < 1e-9)
                    return;
            }

            path.Add(p);
        }

        private static void AppendDedupePoint3DWithBarId(
            List<CalibPoint3D> path,
            List<int> pathBarIds,
            CalibPoint3D p,
            int barId)
        {
            if (path.Count > 0)
            {
                var last = path[path.Count - 1];
                if (Math.Abs(last.X - p.X) < 1e-9 && Math.Abs(last.Y - p.Y) < 1e-9 && Math.Abs(last.Z - p.Z) < 1e-9)
                    return;
            }

            path.Add(p);
            pathBarIds.Add(barId);
        }

        private static void AppendLineTransit3D(List<CalibPoint3D> path, CalibPoint3D a, CalibPoint3D b, double spacing)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-12)
            {
                AppendDedupePoint3D(path, b);
                return;
            }

            if (spacing <= 0)
            {
                AppendDedupePoint3D(path, b);
                return;
            }

            int n = Math.Max(1, (int)Math.Ceiling(len / spacing));
            for (int i = 1; i <= n; i++)
            {
                double t = (double)i / n;
                AppendDedupePoint3D(path, new CalibPoint3D(a.X + t * dx, a.Y + t * dy, a.Z + t * dz));
            }
        }

        private static void AppendLineTransit3DWithBarId(
            List<CalibPoint3D> path,
            List<int> pathBarIds,
            CalibPoint3D a,
            CalibPoint3D b,
            double spacing,
            int barId)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-12)
            {
                AppendDedupePoint3DWithBarId(path, pathBarIds, b, barId);
                return;
            }

            if (spacing <= 0)
            {
                AppendDedupePoint3DWithBarId(path, pathBarIds, b, barId);
                return;
            }

            int n = Math.Max(1, (int)Math.Ceiling(len / spacing));
            for (int i = 1; i <= n; i++)
            {
                double t = (double)i / n;
                AppendDedupePoint3DWithBarId(
                    path,
                    pathBarIds,
                    new CalibPoint3D(a.X + t * dx, a.Y + t * dy, a.Z + t * dz),
                    barId);
            }
        }

        private static void AppendWeldContourClosingIfNeeded3D(
            List<CalibPoint3D> path,
            CalibPoint3D[] c,
            string closeContourMode,
            double transitSpacing)
        {
            if (c == null || c.Length < 3 || path.Count == 0)
                return;
            var mode = (closeContourMode ?? "auto").Trim().ToLowerInvariant();
            if (mode is "false" or "0" or "no" or "off")
                return;
            bool shouldClose = mode is "true" or "1" or "yes" or "on" || ShouldAutoCloseContourWeld3D(c);
            if (!shouldClose)
                return;
            var last = path[path.Count - 1];
            if (PointDistance3D(last, c[0]) < 1e-9)
                return;
            AppendLineTransit3D(path, last, c[0], transitSpacing);
        }

        private static int[] SanitizeBarIdsUnifyAlongShortSteps3D(CalibPoint3D[] pts, int[] barIds, double transitSpacingHint)
        {
            if (pts == null || barIds == null || pts.Length < 2 || barIds.Length != pts.Length)
                return barIds!;

            var steps = new double[pts.Length - 1];
            for (int i = 0; i < pts.Length - 1; i++)
                steps[i] = PointDistance3D(pts[i], pts[i + 1]);

            var sorted = (double[])steps.Clone();
            Array.Sort(sorted);
            double med = sorted[sorted.Length / 2];
            double th = Math.Max(med * 4.0, 1e-9);
            if (transitSpacingHint > 1e-12)
                th = Math.Max(th, transitSpacingHint * 3.0);

            var o = (int[])barIds.Clone();
            for (int i = 1; i < pts.Length; i++)
            {
                if (steps[i - 1] <= th && o[i] != o[i - 1])
                    o[i] = o[i - 1];
            }

            return o;
        }

        private static CalibPoint3D[] BuildWeldPathWithRetreatBetweenContours3D(
            List<CalibPoint3D[]> contours,
            CalibPoint3D retreat,
            bool leadIn,
            bool leadOut,
            double transitSpacing,
            string closeContourMode)
        {
            var path = new List<CalibPoint3D>();
            for (int i = 0; i < contours.Count; i++)
            {
                var c = contours[i];
                if (c == null || c.Length == 0) continue;

                if (i == 0)
                {
                    if (leadIn)
                        AppendLineTransit3D(path, retreat, c[0], transitSpacing);
                    else
                        AppendDedupePoint3D(path, c[0]);
                    for (int k = 1; k < c.Length; k++)
                        AppendDedupePoint3D(path, c[k]);
                    AppendWeldContourClosingIfNeeded3D(path, c, closeContourMode, transitSpacing);
                }
                else
                {
                    var prev = path[path.Count - 1];
                    AppendLineTransit3D(path, prev, retreat, transitSpacing);
                    AppendLineTransit3D(path, retreat, c[0], transitSpacing);
                    for (int k = 1; k < c.Length; k++)
                        AppendDedupePoint3D(path, c[k]);
                    AppendWeldContourClosingIfNeeded3D(path, c, closeContourMode, transitSpacing);
                }
            }

            if (leadOut && path.Count > 0)
            {
                var last = path[path.Count - 1];
                AppendLineTransit3D(path, last, retreat, transitSpacing);
            }

            return path.ToArray();
        }

        private static (List<CalibPoint3D[]> Segments, List<int> BarIds, string? Note) NormalizeWeldContourSegmentsForRetreat3D(
            List<CalibPoint3D[]> segments,
            List<int> segmentBarIds,
            string barSplitMode,
            double transitSpacing,
            double segmentJoinMaxDistOverride)
        {
            string? summaryNote = null;
            if (segments == null || segments.Count == 0)
                return (segments ?? new List<CalibPoint3D[]>(), segmentBarIds ?? new List<int>(), null);

            var mode = (barSplitMode ?? "auto").Trim().ToLowerInvariant();
            if (mode is "single" or "one" or "polyline")
            {
                var one = new List<CalibPoint3D>();
                foreach (var s in segments)
                {
                    if (s == null || s.Length == 0) continue;
                    foreach (var p in s)
                        AppendDedupePoint3D(one, p);
                }

                summaryNote = " · 强制单条轨迹(无条间回退)";
                return (new List<CalibPoint3D[]> { one.ToArray() }, new List<int> { 0 }, summaryNote);
            }

            if (mode is "by_bar" or "bars" or "split")
                return (segments, segmentBarIds, null);

            if (segments.Count <= 1)
                return (segments, segmentBarIds, null);

            int nonemptySegCount = segments.Count(s => s != null && s.Length > 0);
            if (nonemptySegCount < 2)
                return (segments, segmentBarIds, null);

            while (segmentBarIds.Count < segments.Count)
                segmentBarIds.Add(segmentBarIds.Count);
            while (segmentBarIds.Count > segments.Count)
                segmentBarIds.RemoveAt(segmentBarIds.Count - 1);

            double jm = segmentJoinMaxDistOverride > 1e-15
                ? segmentJoinMaxDistOverride
                : InferWeldJoinMaxFromPointSteps3D(segments, transitSpacing);
            if (jm <= 1e-15)
                return (segments, segmentBarIds, null);

            var merged = CoalesceWeldSegmentsByEndGapAndBarId3D(segments, segmentBarIds, jm);
            for (int iter = 0; iter < 65536; iter++)
            {
                var again = CoalesceWeldSegmentsByEndGapAndBarId3D(merged.Segments, merged.BarIds, jm);
                if (again.Segments.Count == merged.Segments.Count)
                    break;
                merged = again;
            }

            if (merged.Segments.Count < segments.Count)
                summaryNote = $" · 同条号端距≤{jm.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)}合并分段 {segments.Count}→{merged.Segments.Count}条";
            return (merged.Segments, merged.BarIds, summaryNote);
        }

        private static double InferWeldJoinMaxFromPointSteps3D(List<CalibPoint3D[]> segments, double transitSpacing)
        {
            var flat = segments.Where(s => s != null && s.Length > 0).SelectMany(s => s!).ToArray();
            if (flat.Length < 2)
                return 0;

            var d = new double[flat.Length - 1];
            for (int i = 0; i < flat.Length - 1; i++)
                d[i] = PointDistance3D(flat[i], flat[i + 1]);

            Array.Sort(d);
            double med = d[d.Length / 2];
            int p90i = Math.Min(d.Length - 1, (int)Math.Floor((d.Length - 1) * 0.9));
            double p90 = d[p90i];
            if (med < 1e-12 && p90 < 1e-12)
                return 0;

            double baseStep = Math.Max(med, p90 * 0.35);
            double th = baseStep * 10.0;
            if (transitSpacing > 1e-12)
                th = Math.Max(th, transitSpacing * 5.0);
            return th;
        }

        private static (List<CalibPoint3D[]> Segments, List<int> BarIds) CoalesceWeldSegmentsByEndGapAndBarId3D(
            List<CalibPoint3D[]> segments,
            IReadOnlyList<int> segmentBarId,
            double joinMaxDist)
        {
            var res = new List<CalibPoint3D[]>();
            var resBid = new List<int>();
            var buf = new List<CalibPoint3D>();
            var bufIds = new List<int>();
            bool hasBuf = false;

            for (int j = 0; j < segments.Count; j++)
            {
                var s = segments[j];
                if (s == null || s.Length == 0)
                    continue;
                int bid = j < segmentBarId.Count ? segmentBarId[j] : j;
                if (!hasBuf)
                {
                    foreach (var p in s)
                        AppendDedupePoint3D(buf, p);
                    bufIds.Add(bid);
                    hasBuf = true;
                    continue;
                }

                var last = buf[buf.Count - 1];
                var head = s[0];
                double dist = PointDistance3D(head, last);
                int bufId = bufIds[0];
                if (dist <= joinMaxDist && bid == bufId)
                {
                    foreach (var p in s)
                        AppendDedupePoint3D(buf, p);
                }
                else
                {
                    res.Add(buf.ToArray());
                    resBid.Add(bufId);
                    buf.Clear();
                    bufIds.Clear();
                    foreach (var p in s)
                        AppendDedupePoint3D(buf, p);
                    bufIds.Add(bid);
                }
            }

            if (hasBuf && buf.Count > 0)
            {
                res.Add(buf.ToArray());
                resBid.Add(bufIds[0]);
            }

            return (res, resBid);
        }
    }
}
