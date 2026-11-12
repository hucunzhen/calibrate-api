using System;
using System.Collections.Generic;
using System.Globalization;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 世界坐标(mm)下按条号整条平移焊道。循环每轮只有一条时，用该轮 BarId/Index 查表，不按 Y 分上下。
    /// </summary>
    internal static class WorldTrajectoryOffset
    {
        private const double DefaultCloseTolMm = 0.01;

        internal readonly struct Result
        {
            public Point2D[] Points { get; init; }
            public int[] BarIds { get; init; }
            public int AppliedBarCount { get; init; }
            public string Summary { get; init; }
        }

        public static Result Apply(
            Point2D[] points,
            int[]? barIds,
            IReadOnlyDictionary<int, (double Dx, double Dy)> offsetsByBar,
            int? roundBarId = null)
        {
            if (points == null || points.Length == 0)
            {
                return new Result
                {
                    Points = Array.Empty<Point2D>(),
                    BarIds = Array.Empty<int>(),
                    Summary = "skip: 0 pts"
                };
            }

            int[] ids;
            if (roundBarId.HasValue)
                ids = CreateUniformBarIds(points.Length, roundBarId.Value);
            else if (barIds != null && barIds.Length == points.Length)
                ids = (int[])barIds.Clone();
            else
                ids = CreateUniformBarIds(points.Length, 0);

            var outPts = new Point2D[points.Length];
            Array.Copy(points, outPts, points.Length);

            if (offsetsByBar == null || offsetsByBar.Count == 0)
            {
                return new Result
                {
                    Points = outPts,
                    BarIds = ids,
                    Summary = roundBarId.HasValue
                        ? $"循环条号 {roundBarId.Value}，表空，未偏置"
                        : "偏置表空，未偏置"
                };
            }

            var seen = new Dictionary<int, int>();
            int appliedBars = 0;
            for (int i = 0; i < points.Length; i++)
            {
                int id = ids[i];
                if (!seen.TryGetValue(id, out int state))
                {
                    state = offsetsByBar.TryGetValue(id, out var off)
                            && (Math.Abs(off.Dx) >= 1e-15 || Math.Abs(off.Dy) >= 1e-15)
                        ? 1
                        : 0;
                    seen[id] = state;
                    if (state == 1)
                        appliedBars++;
                }

                if (state == 0 || !offsetsByBar.TryGetValue(id, out var delta))
                    continue;
                outPts[i] = new Point2D(points[i].X + delta.Dx, points[i].Y + delta.Dy);
            }

            string summary;
            if (roundBarId.HasValue)
            {
                offsetsByBar.TryGetValue(roundBarId.Value, out var one);
                summary = appliedBars > 0
                    ? $"循环条号 {roundBarId.Value} Δ=({one.Dx:G4},{one.Dy:G4}) mm"
                    : $"循环条号 {roundBarId.Value} 未在表中，未偏置";
            }
            else
            {
                summary = appliedBars > 0
                    ? $"按条号偏置 {appliedBars}/{seen.Count} 条"
                    : $"表中无本批条号（{seen.Count} 条），未偏置";
            }

            return new Result
            {
                Points = outPts,
                BarIds = ids,
                AppliedBarCount = appliedBars,
                Summary = summary
            };
        }

        internal readonly struct CloseResult
        {
            public Point2D[] Points { get; init; }
            public int[] BarIds { get; init; }
            public double[]? Z { get; init; }
            public int CloseCount { get; init; }
        }

        internal readonly struct OverrunResult
        {
            public Point2D[] Points { get; init; }
            public int[] BarIds { get; init; }
            public double[]? Z { get; init; }
            public int OverrunCount { get; init; }
        }

        internal readonly struct TailDistanceResult
        {
            public Point2D[] Points { get; init; }
            public int[] BarIds { get; init; }
            public double[]? Z { get; init; }
            public int SplitCount { get; init; }
        }

        /// <summary>
        /// 每条焊道未闭合时补回起点（至少 3 点）。已闭合或 <paramref name="closePolyline"/> 为 false 时原样返回。
        /// 应在超程、收尾距离之前调用。
        /// </summary>
        public static CloseResult ApplyClose(
            Point2D[] points,
            int[]? barIds,
            bool closePolyline,
            double[]? z = null,
            double closeTolMm = DefaultCloseTolMm)
        {
            if (points == null || points.Length == 0)
            {
                return new CloseResult
                {
                    Points = Array.Empty<Point2D>(),
                    BarIds = Array.Empty<int>(),
                    Z = z == null ? null : Array.Empty<double>()
                };
            }

            int[] ids = barIds != null && barIds.Length == points.Length
                ? barIds
                : CreateUniformBarIds(points.Length, 0);
            bool useZ = z != null && z.Length == points.Length;
            if (!closePolyline || points.Length < 3)
            {
                return new CloseResult
                {
                    Points = (Point2D[])points.Clone(),
                    BarIds = (int[])ids.Clone(),
                    Z = useZ ? (double[])z!.Clone() : null
                };
            }

            var outPts = new List<Point2D>(points.Length + 8);
            var outIds = new List<int>(points.Length + 8);
            List<double>? outZ = useZ ? new List<double>(points.Length + 8) : null;
            int closeCount = 0;
            int start = 0;
            while (start < points.Length)
            {
                int end = start + 1;
                while (end < points.Length && ids[end] == ids[start])
                    end++;
                closeCount += AppendRunClosing(
                    points, ids, z, start, end, closeTolMm, outPts, outIds, outZ);
                start = end;
            }

            return new CloseResult
            {
                Points = outPts.ToArray(),
                BarIds = outIds.ToArray(),
                Z = outZ?.ToArray(),
                CloseCount = closeCount
            };
        }

        /// <summary>
        /// 每条已闭合焊道：沿闭合方向从起点再延伸 <paramref name="overrunDistMm"/>。
        /// 未闭合或 ≤0 时原样返回。须先 <see cref="ApplyClose"/>。
        /// </summary>
        public static OverrunResult ApplyOverrun(
            Point2D[] points,
            int[]? barIds,
            double overrunDistMm,
            double[]? z = null,
            double closeTolMm = DefaultCloseTolMm)
        {
            if (points == null || points.Length == 0)
            {
                return new OverrunResult
                {
                    Points = Array.Empty<Point2D>(),
                    BarIds = Array.Empty<int>(),
                    Z = z == null ? null : Array.Empty<double>()
                };
            }

            int[] ids = barIds != null && barIds.Length == points.Length
                ? barIds
                : CreateUniformBarIds(points.Length, 0);
            bool useZ = z != null && z.Length == points.Length;
            if (points.Length < 2 || overrunDistMm <= 1e-12)
            {
                return new OverrunResult
                {
                    Points = (Point2D[])points.Clone(),
                    BarIds = (int[])ids.Clone(),
                    Z = useZ ? (double[])z!.Clone() : null
                };
            }

            var outPts = new List<Point2D>(points.Length + 8);
            var outIds = new List<int>(points.Length + 8);
            List<double>? outZ = useZ ? new List<double>(points.Length + 8) : null;
            int overrunCount = 0;
            int start = 0;
            while (start < points.Length)
            {
                int end = start + 1;
                while (end < points.Length && ids[end] == ids[start])
                    end++;
                overrunCount += AppendRunWithOverrun(
                    points, ids, z, start, end, overrunDistMm, closeTolMm, outPts, outIds, outZ);
                start = end;
            }

            return new OverrunResult
            {
                Points = outPts.ToArray(),
                BarIds = outIds.ToArray(),
                Z = outZ?.ToArray(),
                OverrunCount = overrunCount
            };
        }

        /// <summary>先闭合，再超程，再收尾。供世界轨迹偏置算子按此顺序调用。</summary>
        public static TailDistanceResult ApplyCloseOverrunTail(
            Point2D[] points,
            int[]? barIds,
            bool closePolyline,
            double overrunDistMm,
            double tailDistMm,
            double[]? z = null)
        {
            var closed = ApplyClose(points, barIds, closePolyline, z);
            var overrun = ApplyOverrun(closed.Points, closed.BarIds, overrunDistMm, closed.Z);
            return ApplyTailDistance(overrun.Points, overrun.BarIds, tailDistMm, overrun.Z);
        }

        /// <summary>
        /// 从终点沿折线回退 <paramref name="tailDistMm"/> 插入一点，终点不动，使最后一条线段
        /// 长度等于收尾距离。末段（含超程段）不够长时继续往前一段累计。整条短于该值则不拆。≤0 时原样返回。
        /// </summary>
        public static TailDistanceResult ApplyTailDistance(Point2D[] points, int[]? barIds, double tailDistMm, double[]? z = null)
        {
            if (points == null || points.Length == 0)
            {
                return new TailDistanceResult
                {
                    Points = Array.Empty<Point2D>(),
                    BarIds = Array.Empty<int>(),
                    Z = z == null ? null : Array.Empty<double>()
                };
            }

            int[] ids = barIds != null && barIds.Length == points.Length
                ? barIds
                : CreateUniformBarIds(points.Length, 0);
            bool useZ = z != null && z.Length == points.Length;
            if (points.Length < 2 || tailDistMm <= 1e-12)
            {
                return new TailDistanceResult
                {
                    Points = (Point2D[])points.Clone(),
                    BarIds = (int[])ids.Clone(),
                    Z = useZ ? (double[])z!.Clone() : null
                };
            }

            var outPts = new List<Point2D>(points.Length + 8);
            var outIds = new List<int>(points.Length + 8);
            List<double>? outZ = useZ ? new List<double>(points.Length + 8) : null;
            int splitCount = 0;
            int start = 0;
            while (start < points.Length)
            {
                int end = start + 1;
                while (end < points.Length && ids[end] == ids[start])
                    end++;
                splitCount += AppendRunSplittingLastSegment(
                    points, ids, z, start, end, tailDistMm, outPts, outIds, outZ);
                start = end;
            }

            return new TailDistanceResult
            {
                Points = outPts.ToArray(),
                BarIds = outIds.ToArray(),
                Z = outZ?.ToArray(),
                SplitCount = splitCount
            };
        }

        private static int AppendRunSplittingLastSegment(
            Point2D[] points,
            int[] ids,
            double[]? z,
            int start,
            int end,
            double tailDistMm,
            List<Point2D> outPts,
            List<int> outIds,
            List<double>? outZ)
        {
            int last = end - 1;
            if (end - start < 2)
            {
                CopyRun(points, ids, z, start, end, outPts, outIds, outZ);
                return 0;
            }

            // 从终点沿折线回退 tailDist。超程段短于收尾时继续往前累计（例：超程2、收尾3 → 末段仍为3）。
            double acc = 0;
            int splitStart = -1;
            int splitEnd = -1;
            double splitLen = 0;
            for (int i = last; i > start; i--)
            {
                double dx = points[i].X - points[i - 1].X;
                double dy = points[i].Y - points[i - 1].Y;
                double segLen = Math.Sqrt(dx * dx + dy * dy);
                if (segLen < 1e-9)
                    continue;
                if (acc + segLen > tailDistMm + 1e-12)
                {
                    splitStart = i - 1;
                    splitEnd = i;
                    splitLen = segLen;
                    break;
                }

                acc += segLen;
            }

            if (splitStart < start)
            {
                CopyRun(points, ids, z, start, end, outPts, outIds, outZ);
                return 0;
            }

            double needOnSeg = tailDistMm - acc;
            double tSplit = (splitLen - needOnSeg) / splitLen;
            var splitPt = new Point2D(
                points[splitStart].X + (points[splitEnd].X - points[splitStart].X) * tSplit,
                points[splitStart].Y + (points[splitEnd].Y - points[splitStart].Y) * tSplit);
            double splitZ = 0;
            if (outZ != null && z != null)
                splitZ = z[splitStart] + (z[splitEnd] - z[splitStart]) * tSplit;

            CopyRun(points, ids, z, start, splitStart + 1, outPts, outIds, outZ);
            outPts.Add(splitPt);
            outIds.Add(ids[last]);
            outZ?.Add(splitZ);
            outPts.Add(points[last]);
            outIds.Add(ids[last]);
            if (outZ != null && z != null)
                outZ.Add(z[last]);
            return 1;
        }

        private static void CopyRun(
            Point2D[] points,
            int[] ids,
            double[]? z,
            int start,
            int end,
            List<Point2D> outPts,
            List<int> outIds,
            List<double>? outZ)
        {
            for (int i = start; i < end; i++)
            {
                outPts.Add(points[i]);
                outIds.Add(ids[i]);
                if (outZ != null && z != null)
                    outZ.Add(z[i]);
            }
        }

        private static int AppendRunWithOverrun(
            Point2D[] points,
            int[] ids,
            double[]? z,
            int start,
            int end,
            double overrunDistMm,
            double closeTolMm,
            List<Point2D> outPts,
            List<int> outIds,
            List<double>? outZ)
        {
            for (int i = start; i < end; i++)
            {
                outPts.Add(points[i]);
                outIds.Add(ids[i]);
                if (outZ != null && z != null)
                    outZ.Add(z[i]);
            }

            if (end - start < 2)
                return 0;

            Point2D origin = points[start];
            double originZ = outZ != null && z != null ? z[start] : 0;
            bool alreadyClosed = Distance(points[end - 1], origin, z, end - 1, start) <= closeTolMm;
            if (!alreadyClosed)
                return 0;

            int beforeIdx = -1;
            for (int i = end - 2; i >= start; i--)
            {
                if (Distance(points[i], points[end - 1], z, i, end - 1) > closeTolMm)
                {
                    beforeIdx = i;
                    break;
                }
            }

            if (beforeIdx < start)
                return 0;

            double dx = origin.X - points[beforeIdx].X;
            double dy = origin.Y - points[beforeIdx].Y;
            double dz = outZ != null && z != null ? originZ - z[beforeIdx] : 0;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-12)
                return 0;

            double scale = overrunDistMm / len;
            var overrunPt = new Point2D(origin.X + dx * scale, origin.Y + dy * scale);
            double overrunZ = originZ + dz * scale;
            int barId = ids[start];
            outPts.Add(overrunPt);
            outIds.Add(barId);
            outZ?.Add(overrunZ);
            return 1;
        }

        private static int AppendRunClosing(
            Point2D[] points,
            int[] ids,
            double[]? z,
            int start,
            int end,
            double closeTolMm,
            List<Point2D> outPts,
            List<int> outIds,
            List<double>? outZ)
        {
            for (int i = start; i < end; i++)
            {
                outPts.Add(points[i]);
                outIds.Add(ids[i]);
                if (outZ != null && z != null)
                    outZ.Add(z[i]);
            }

            if (end - start < 3)
                return 0;

            Point2D origin = points[start];
            if (Distance(points[end - 1], origin, z, end - 1, start) <= closeTolMm)
                return 0;

            outPts.Add(origin);
            outIds.Add(ids[start]);
            if (outZ != null && z != null)
                outZ.Add(z[start]);
            return 1;
        }

        private static double Distance(Point2D a, Point2D b, double[]? z, int ia, int ib)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            double dz = z != null && ia < z.Length && ib < z.Length ? z[ia] - z[ib] : 0;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// 解析「条号:ΔX,ΔY」。分隔：换行或分号。支持范围 0-9:0,0.3。# 为注释。
        /// </summary>
        public static Dictionary<int, (double Dx, double Dy)> ParseOffsetByBar(string? raw)
        {
            var map = new Dictionary<int, (double Dx, double Dy)>();
            if (string.IsNullOrWhiteSpace(raw))
                return map;

            string[] rows = raw.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            for (int r = 0; r < rows.Length; r++)
            {
                string line = rows[r].Trim();
                int hash = line.IndexOf('#');
                if (hash >= 0)
                    line = line[..hash].Trim();
                if (line.Length == 0)
                    continue;

                string idPart;
                string xyPart;
                int colon = line.IndexOf(':');
                if (colon >= 0)
                {
                    idPart = line[..colon].Trim();
                    xyPart = line[(colon + 1)..].Trim();
                }
                else
                {
                    string[] csv = SplitXy(line);
                    if (csv.Length != 3)
                        throw new InvalidOperationException(
                            $"世界轨迹偏置: 无法解析「{line}」。格式 条号:ΔX,ΔY，例如 0:0,0.3 或 0-9:0,0.3");
                    idPart = csv[0];
                    xyPart = csv[1] + "," + csv[2];
                }

                string[] xy = SplitXy(xyPart);
                if (xy.Length != 2
                    || !TryParseDouble(xy[0], out double dx)
                    || !TryParseDouble(xy[1], out double dy))
                {
                    throw new InvalidOperationException(
                        $"世界轨迹偏置: 「{line}」的 ΔX,ΔY 无效。");
                }

                if (!TryParseIdRange(idPart, out int idFrom, out int idTo))
                {
                    throw new InvalidOperationException(
                        $"世界轨迹偏置: 「{idPart}」不是条号或范围（如 3 或 0-9）。");
                }

                if (idTo < idFrom)
                    (idFrom, idTo) = (idTo, idFrom);
                for (int id = idFrom; id <= idTo; id++)
                    map[id] = (dx, dy);
            }

            return map;
        }

        private static string[] SplitXy(string raw)
        {
            return raw.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool TryParseIdRange(string raw, out int from, out int to)
        {
            from = 0;
            to = 0;
            string s = raw.Trim();
            int dash = s.IndexOf('-', 1);
            if (dash < 0)
            {
                if (!TryParseInt(s, out from))
                    return false;
                to = from;
                return true;
            }

            return TryParseInt(s[..dash], out from) && TryParseInt(s[(dash + 1)..], out to);
        }

        private static bool TryParseInt(string raw, out int value) =>
            int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        private static bool TryParseDouble(string raw, out double value) =>
            double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static int[] CreateUniformBarIds(int count, int id)
        {
            var ids = new int[count];
            for (int i = 0; i < count; i++)
                ids[i] = id;
            return ids;
        }
    }
}
