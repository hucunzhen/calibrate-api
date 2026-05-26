using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>点列生成 GVAR 的方式：线段（p0→p1，n点→n-1条）或每点退化为一点（p0=p1，n点→n条）。</summary>
    internal enum PlcGvarSegmentMode
    {
        Line,
        PointDegenerate
    }

    internal static class PlcGvarBuilder
    {
        private const double DefaultCloseTolMm = 0.01;

        private static bool PolylineAlreadyClosed2D(Point2D[] pts, double tolMm)
        {
            if (pts == null || pts.Length < 2) return false;
            var a = pts[0];
            var b = pts[^1];
            return Math.Abs(a.X - b.X) <= tolMm && Math.Abs(a.Y - b.Y) <= tolMm;
        }

        private static bool PolylineAlreadyClosed3D(CalibPoint3D[] pts, double tolMm)
        {
            if (pts == null || pts.Length < 2) return false;
            var a = pts[0];
            var b = pts[^1];
            return Math.Abs(a.X - b.X) <= tolMm && Math.Abs(a.Y - b.Y) <= tolMm && Math.Abs(a.Z - b.Z) <= tolMm;
        }

        private static bool ShouldAddClosingSegment(bool closePolyline, int pointCount, bool alreadyClosed)
            => closePolyline && pointCount >= 3 && !alreadyClosed;

        /// <summary>折线点列 → 线段 GVAR（n 点 → n-1 段；closePolyline 时再补末点→首点闭合段）。</summary>
        public static GVAR[] BuildSegmentGvarsFromPolyline(CalibPoint3D[] pts3, short gvarType, bool closePolyline = true, double closeTolMm = DefaultCloseTolMm)
        {
            if (pts3 == null || pts3.Length == 0)
                return Array.Empty<GVAR>();
            if (pts3.Length == 1)
            {
                float x = (float)pts3[0].X, y = (float)pts3[0].Y, z = (float)pts3[0].Z;
                return new[] { MakeLineGvar(gvarType, x, y, z, x, y, z) };
            }

            bool closed = PolylineAlreadyClosed3D(pts3, closeTolMm);
            bool addClose = ShouldAddClosingSegment(closePolyline, pts3.Length, closed);
            var items = new List<GVAR>(pts3.Length - 1 + (addClose ? 1 : 0));
            for (int i = 0; i < pts3.Length - 1; i++)
            {
                float x0 = (float)pts3[i].X, y0 = (float)pts3[i].Y, z0 = (float)pts3[i].Z;
                float x1 = (float)pts3[i + 1].X, y1 = (float)pts3[i + 1].Y, z1 = (float)pts3[i + 1].Z;
                items.Add(MakeLineGvar(gvarType, x0, y0, z0, x1, y1, z1));
            }

            if (addClose)
            {
                int last = pts3.Length - 1;
                float x0 = (float)pts3[last].X, y0 = (float)pts3[last].Y, z0 = (float)pts3[last].Z;
                float x1 = (float)pts3[0].X, y1 = (float)pts3[0].Y, z1 = (float)pts3[0].Z;
                items.Add(MakeLineGvar(gvarType, x0, y0, z0, x1, y1, z1));
            }

            return items.ToArray();
        }

        public static GVAR[] BuildSegmentGvarsFromPolyline(Point2D[] pts2, short gvarType, float zDefault = 0f, bool closePolyline = true, double closeTolMm = DefaultCloseTolMm)
        {
            if (pts2 == null || pts2.Length == 0)
                return Array.Empty<GVAR>();
            float z = zDefault;
            if (pts2.Length == 1)
            {
                float x = (float)pts2[0].X, y = (float)pts2[0].Y;
                return new[] { MakeLineGvar(gvarType, x, y, z, x, y, z) };
            }

            bool closed = PolylineAlreadyClosed2D(pts2, closeTolMm);
            bool addClose = ShouldAddClosingSegment(closePolyline, pts2.Length, closed);
            var items = new List<GVAR>(pts2.Length - 1 + (addClose ? 1 : 0));
            for (int i = 0; i < pts2.Length - 1; i++)
            {
                float x0 = (float)pts2[i].X, y0 = (float)pts2[i].Y;
                float x1 = (float)pts2[i + 1].X, y1 = (float)pts2[i + 1].Y;
                items.Add(MakeLineGvar(gvarType, x0, y0, z, x1, y1, z));
            }

            if (addClose)
            {
                int last = pts2.Length - 1;
                float x0 = (float)pts2[last].X, y0 = (float)pts2[last].Y;
                float x1 = (float)pts2[0].X, y1 = (float)pts2[0].Y;
                items.Add(MakeLineGvar(gvarType, x0, y0, z, x1, y1, z));
            }

            return items.ToArray();
        }

        private static GVAR MakeLineGvar(short gvarType, float x0, float y0, float z0, float x1, float y1, float z1)
            => new GVAR
            {
                type1 = gvarType,
                spVec3_p0 = new SpVec3(x0, y0, z0),
                spVec3_p1 = new SpVec3(x1, y1, z1),
                cx = x0, cy = y0, r = 0,
                start_deg = 0, end_deg = 0,
                z0 = z0, z1 = z1
            };

        private static GVAR MakePointGvar(short gvarType, float x, float y, float z)
            => MakeLineGvar(gvarType, x, y, z, x, y, z);

        /// <summary>点列每点一个 GVAR，p0 与 p1 相同（退化线段）。n 点 → n 条。</summary>
        public static GVAR[] BuildPointGvarsFromPolyline(CalibPoint3D[] pts3, short gvarType, string? _ = null)
        {
            if (pts3 == null || pts3.Length == 0)
                return Array.Empty<GVAR>();

            var items = new GVAR[pts3.Length];
            for (int i = 0; i < pts3.Length; i++)
            {
                float x = (float)pts3[i].X, y = (float)pts3[i].Y, z = (float)pts3[i].Z;
                items[i] = MakePointGvar(gvarType, x, y, z);
            }

            return items;
        }

        public static GVAR[] BuildPointGvarsFromPolyline(Point2D[] pts2, short gvarType, string? _ = null, float zDefault = 0f)
        {
            if (pts2 == null || pts2.Length == 0)
                return Array.Empty<GVAR>();

            var items = new GVAR[pts2.Length];
            for (int i = 0; i < pts2.Length; i++)
            {
                float x = (float)pts2[i].X, y = (float)pts2[i].Y;
                items[i] = MakePointGvar(gvarType, x, y, zDefault);
            }

            return items;
        }

        /// <summary>同条号内才生成线段；条号变化处不连跨条 GVAR（用于 splitByBar=break_segment）。</summary>
        public static GVAR[] BuildSegmentGvarsRespectingBarIds(
            CalibPoint3D[] pts3,
            int[]? barIds,
            short gvarType,
            bool closePolyline = true,
            double closeTolMm = DefaultCloseTolMm)
        {
            if (pts3 == null || pts3.Length == 0)
                return Array.Empty<GVAR>();
            if (barIds == null || barIds.Length != pts3.Length)
                return BuildSegmentGvarsFromPolyline(pts3, gvarType, closePolyline, closeTolMm);

            if (pts3.Length == 1)
            {
                float x = (float)pts3[0].X, y = (float)pts3[0].Y, z = (float)pts3[0].Z;
                return new[] { MakeLineGvar(gvarType, x, y, z, x, y, z) };
            }

            var items = new List<GVAR>();
            for (int i = 0; i < pts3.Length - 1; i++)
            {
                if (barIds[i] != barIds[i + 1])
                    continue;
                float x0 = (float)pts3[i].X, y0 = (float)pts3[i].Y, z0 = (float)pts3[i].Z;
                float x1 = (float)pts3[i + 1].X, y1 = (float)pts3[i + 1].Y, z1 = (float)pts3[i + 1].Z;
                items.Add(MakeLineGvar(gvarType, x0, y0, z0, x1, y1, z1));
            }

            bool closed = PolylineAlreadyClosed3D(pts3, closeTolMm);
            if (ShouldAddClosingSegment(closePolyline, pts3.Length, closed) && barIds[0] == barIds[^1])
            {
                int last = pts3.Length - 1;
                float x0 = (float)pts3[last].X, y0 = (float)pts3[last].Y, z0 = (float)pts3[last].Z;
                float x1 = (float)pts3[0].X, y1 = (float)pts3[0].Y, z1 = (float)pts3[0].Z;
                items.Add(MakeLineGvar(gvarType, x0, y0, z0, x1, y1, z1));
            }

            return items.ToArray();
        }

        /// <summary>每点一个退化点 GVAR（p0=p1）；与 break_segment 线段模式不同，不按相邻点成段过滤。</summary>
        public static GVAR[] BuildPointGvarsRespectingBarIds(CalibPoint3D[] pts3, int[]? barIds, short gvarType, string? _ = null)
            => BuildPointGvarsFromPolyline(pts3, gvarType);

        private static GVAR[] BuildGvarsFromPolylineRun(
            CalibPoint3D[] run,
            short gvarType,
            PlcGvarSegmentMode mode,
            string pointAt,
            bool closePolyline,
            double closeTolMm)
            => mode == PlcGvarSegmentMode.PointDegenerate
                ? BuildPointGvarsFromPolyline(run, gvarType, pointAt)
                : BuildSegmentGvarsFromPolyline(run, gvarType, closePolyline, closeTolMm);

        /// <summary>
        /// separate_batch：每个不同的逐点 BarId（= GroupBarIds / 焊道条号）写一批 PLC。
        /// 同一条号在点列中若分多段出现，合并为同一批（各段各自生成 GVAR 线段，段与段之间不连线）。
        /// 16 个匹配且 BarId 为 0～15 各一段 → 16 批；与「找形匹配个数」一致的前提是 BarIds 来自轨迹 GroupBarIds。
        /// </summary>
        public static List<(int BarId, GVAR[] Gvars)> BuildSegmentGvarBatchesByBarId(
            CalibPoint3D[] pts3,
            int[] barIds,
            short gvarType,
            bool closePolyline = true,
            double closeTolMm = DefaultCloseTolMm)
            => BuildGvarBatchesByBarId(pts3, barIds, gvarType, PlcGvarSegmentMode.Line, "mid", closePolyline, closeTolMm);

        public static List<(int BarId, GVAR[] Gvars)> BuildPointGvarBatchesByBarId(
            CalibPoint3D[] pts3,
            int[] barIds,
            short gvarType,
            string pointAt = "mid")
            => BuildGvarBatchesByBarId(pts3, barIds, gvarType, PlcGvarSegmentMode.PointDegenerate, pointAt, false, DefaultCloseTolMm);

        private static List<(int BarId, GVAR[] Gvars)> BuildGvarBatchesByBarId(
            CalibPoint3D[] pts3,
            int[] barIds,
            short gvarType,
            PlcGvarSegmentMode mode,
            string pointAt,
            bool closePolyline,
            double closeTolMm)
        {
            var batches = new List<(int BarId, GVAR[] Gvars)>();
            if (pts3 == null || pts3.Length == 0 || barIds == null || barIds.Length != pts3.Length)
                return batches;

            var barOrder = new List<int>();
            for (int i = 0; i < barIds.Length; i++)
            {
                if (!barOrder.Contains(barIds[i]))
                    barOrder.Add(barIds[i]);
            }

            foreach (int barId in barOrder)
            {
                var gvars = new List<GVAR>();
                int start = 0;
                for (int i = 1; i <= pts3.Length; i++)
                {
                    if (i == pts3.Length || barIds[i] != barIds[i - 1])
                    {
                        if (i > start && barIds[start] == barId)
                        {
                            int len = i - start;
                            var run = new CalibPoint3D[len];
                            Array.Copy(pts3, start, run, 0, len);
                            gvars.AddRange(BuildGvarsFromPolylineRun(run, gvarType, mode, pointAt, closePolyline, closeTolMm));
                        }

                        start = i;
                    }
                }

                if (gvars.Count > 0)
                    batches.Add((barId, gvars.ToArray()));
            }

            return batches;
        }

        private static int[]? TryGetBarIdsInput(Dictionary<string, object?> inputs)
        {
            if (!inputs.TryGetValue("BarIds", out var bObj) || bObj == null)
                return null;
            if (bObj is int[] ia)
                return ia;
            if (bObj is IEnumerable<int> en)
                return en.ToArray();
            return null;
        }

        /// <summary>解析 send_plc / send_plc_point 输入：优先 GVAR 列表 / PLC 页草稿，再 Points3D/Points。</summary>
        public static bool TryResolveSendPlcGvar(
            Dictionary<string, object?> inputs,
            string? usePlcPageGvarParam,
            short defaultGvarType,
            string? splitByBar,
            out GVAR[] gvarItems,
            out List<(int BarId, GVAR[] Gvars)>? barBatches,
            out string sourceTag,
            out string? diagnostic,
            PlcGvarSegmentMode segmentMode = PlcGvarSegmentMode.Line,
            string? pointAt = "mid",
            double zDefault = 0.0,
            bool closePolyline = true,
            double closeTolMm = DefaultCloseTolMm)
        {
            float zDef = (float)zDefault;
            bool asPoint = segmentMode == PlcGvarSegmentMode.PointDegenerate;
            string ptAt = pointAt ?? "mid";
            string segLabel = asPoint ? "点GVAR(p0=p1)" : (closePolyline ? "段GVAR+闭合" : "段GVAR");
            barBatches = null;
            gvarItems = Array.Empty<GVAR>();
            sourceTag = "";
            diagnostic = null;

            if (inputs.TryGetValue("GvarList", out var gObj) && gObj is GVAR[] gArr && gArr.Length > 0)
            {
                gvarItems = gArr;
                sourceTag = "GvarList输入";
                return true;
            }

            bool useDraft = (usePlcPageGvarParam ?? "false").Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                || usePlcPageGvarParam == "1";
            if (useDraft && PlcGvarDraft.TryGet(out var draft, out var draftSrc))
            {
                gvarItems = draft;
                sourceTag = $"PLC页草稿({draftSrc})";
                return true;
            }

            CalibPoint3D[]? pts3 = null;
            if (inputs.TryGetValue("Points3D", out var p3o))
                pts3 = CoerceCalibPoint3DArray(p3o);
            if (pts3 == null && inputs.TryGetValue("Points", out var pIn))
                pts3 = CoerceCalibPoint3DArray(pIn);

            var barIds = TryGetBarIdsInput(inputs);
            string split = (splitByBar ?? "none").Trim().ToLowerInvariant();

            if (pts3 != null && pts3.Length > 0)
            {
                if (split == "separate_batch")
                {
                    if (barIds == null || barIds.Length == 0)
                    {
                        diagnostic = "separate_batch 须接 BarIds 且与点列等长。";
                        return false;
                    }

                    if (barIds.Length != pts3.Length)
                    {
                        diagnostic =
                            $"separate_batch 要求 BarIds 与点列等长（当前 BarIds={barIds.Length}，点数={pts3.Length}）。" +
                            " 常见原因：BarIds 为「每条轮廓一个」而非逐点；请使用已修复的「垂直入刀」输出 Out2，或检查连线。";
                        return false;
                    }

                    barBatches = asPoint
                        ? BuildPointGvarBatchesByBarId(pts3, barIds, defaultGvarType, ptAt)
                        : BuildSegmentGvarBatchesByBarId(pts3, barIds, defaultGvarType, closePolyline, closeTolMm);
                    int total = barBatches.Sum(b => b.Gvars.Length);
                    int uniqBar = barIds.Distinct().Count();
                    gvarItems = Array.Empty<GVAR>();
                    sourceTag =
                        $"Points3D×{pts3.Length} 焊道条号(BarId)种类={uniqBar}→{barBatches.Count}批/共{total}{segLabel}";
                    return barBatches.Count > 0;
                }

                if (split == "break_segment" && barIds != null && barIds.Length == pts3.Length)
                    gvarItems = asPoint
                        ? BuildPointGvarsRespectingBarIds(pts3, barIds, defaultGvarType, ptAt)
                        : BuildSegmentGvarsRespectingBarIds(pts3, barIds, defaultGvarType, closePolyline, closeTolMm);
                else
                    gvarItems = asPoint
                        ? BuildPointGvarsFromPolyline(pts3, defaultGvarType, ptAt)
                        : BuildSegmentGvarsFromPolyline(pts3, defaultGvarType, closePolyline, closeTolMm);
                sourceTag = $"Points3D×{pts3.Length}→{gvarItems.Length}{segLabel}";
                return true;
            }

            Point2D[]? pts2 = null;
            if (inputs.TryGetValue("Points", out var p2o))
                pts2 = CoercePoint2DArray(p2o);
            if (pts2 != null && pts2.Length > 0)
            {
                if (split == "separate_batch")
                {
                    if (barIds == null || barIds.Length == 0)
                    {
                        diagnostic = "separate_batch 须接 BarIds 且与点列等长。";
                        return false;
                    }

                    if (barIds.Length != pts2.Length)
                    {
                        diagnostic =
                            $"separate_batch 要求 BarIds 与点列等长（当前 BarIds={barIds.Length}，点数={pts2.Length}）。";
                        return false;
                    }

                    var pts3from2 = pts2.Select(p => new CalibPoint3D(p.X, p.Y, zDef)).ToArray();
                    barBatches = asPoint
                        ? BuildPointGvarBatchesByBarId(pts3from2, barIds, defaultGvarType, ptAt)
                        : BuildSegmentGvarBatchesByBarId(pts3from2, barIds, defaultGvarType, closePolyline, closeTolMm);
                    int total = barBatches.Sum(b => b.Gvars.Length);
                    gvarItems = Array.Empty<GVAR>();
                    sourceTag = FormatPoints2DSourceTag(pts2.Length, barBatches.Count, total, segLabel, zDef);
                    return barBatches.Count > 0;
                }

                if (split == "break_segment" && barIds != null && barIds.Length == pts2.Length)
                {
                    var pts3from2 = pts2.Select(p => new CalibPoint3D(p.X, p.Y, zDef)).ToArray();
                    gvarItems = asPoint
                        ? BuildPointGvarsRespectingBarIds(pts3from2, barIds, defaultGvarType, ptAt)
                        : BuildSegmentGvarsRespectingBarIds(pts3from2, barIds, defaultGvarType, closePolyline, closeTolMm);
                }
                else
                    gvarItems = asPoint
                        ? BuildPointGvarsFromPolyline(pts2, defaultGvarType, ptAt, zDef)
                        : BuildSegmentGvarsFromPolyline(pts2, defaultGvarType, zDef, closePolyline, closeTolMm);
                sourceTag = FormatPoints2DSourceTag(pts2.Length, gvarItems.Length, segLabel, zDef);
                return true;
            }

            diagnostic = DescribeFailedInputs(inputs, useDraft);
            return false;
        }

        private static string FormatPoints2DSourceTag(int pointCount, int gvarOrBatchCount, string segLabel, float zDef)
            => zDef != 0f
                ? $"Points×{pointCount} Z={zDef}→{gvarOrBatchCount}{segLabel}"
                : $"Points×{pointCount}→{gvarOrBatchCount}{segLabel}";

        private static string FormatPoints2DSourceTag(int pointCount, int batchCount, int totalGvars, string segLabel, float zDef)
            => zDef != 0f
                ? $"Points×{pointCount} Z={zDef}→{batchCount}条BarId批/共{totalGvars}{segLabel}"
                : $"Points×{pointCount}→{batchCount}条BarId批/共{totalGvars}{segLabel}";

        private static CalibPoint3D[]? CoerceCalibPoint3DArray(object? obj)
        {
            if (obj is CalibPoint3D[] a)
                return a;
            if (obj is IEnumerable<CalibPoint3D> en)
                return en.ToArray();
            return null;
        }

        private static Point2D[]? CoercePoint2DArray(object? obj)
        {
            if (obj is Point2D[] a)
                return a;
            if (obj is IEnumerable<Point2D> en)
                return en.ToArray();
            return null;
        }

        private static string DescribeFailedInputs(Dictionary<string, object?> inputs, bool useDraftRequested)
        {
            var sb = new StringBuilder();
            sb.Append("未得到可下发的 GVAR。");
            if (useDraftRequested && !PlcGvarDraft.TryGet(out _, out _))
                sb.Append(" 已勾选 usePlcPageGvar 但 PLC 页表格为空（请先在 PLC 页 Read/编辑后 Write All 试通）。");
            if (inputs.Count == 0)
            {
                sb.Append(" 无任何输入：检查「真分支放行」是否 blocked、连线是否接到 Points/Points3D/GvarList。");
                sb.Append(" splitByBar=separate_batch/break_segment 须接与点列等长的 BarIds。");
                return sb.ToString();
            }

            foreach (var kv in inputs)
            {
                object? v = kv.Value;
                string type = v == null ? "null" : v.GetType().Name;
                string detail = v switch
                {
                    Array arr => $"Length={arr.Length}",
                    System.Collections.ICollection col => $"Count={col.Count}",
                    _ => ""
                };
                sb.Append($" [{kv.Key}={type}{detail}]");
            }

            return sb.ToString();
        }
    }
}
