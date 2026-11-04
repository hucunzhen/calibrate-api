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
