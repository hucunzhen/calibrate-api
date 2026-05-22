using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    internal static class PlcGvarBuilder
    {
        /// <summary>折线点列 → 线段 GVAR（n 点 → n-1 段；与 PLC 页手工填 p0→p1 一致）。</summary>
        public static GVAR[] BuildSegmentGvarsFromPolyline(CalibPoint3D[] pts3, short gvarType)
        {
            if (pts3 == null || pts3.Length == 0)
                return Array.Empty<GVAR>();
            if (pts3.Length == 1)
            {
                float x = (float)pts3[0].X, y = (float)pts3[0].Y, z = (float)pts3[0].Z;
                return new[] { MakeLineGvar(gvarType, x, y, z, x, y, z) };
            }

            var items = new GVAR[pts3.Length - 1];
            for (int i = 0; i < pts3.Length - 1; i++)
            {
                float x0 = (float)pts3[i].X, y0 = (float)pts3[i].Y, z0 = (float)pts3[i].Z;
                float x1 = (float)pts3[i + 1].X, y1 = (float)pts3[i + 1].Y, z1 = (float)pts3[i + 1].Z;
                items[i] = MakeLineGvar(gvarType, x0, y0, z0, x1, y1, z1);
            }

            return items;
        }

        public static GVAR[] BuildSegmentGvarsFromPolyline(Point2D[] pts2, short gvarType)
        {
            if (pts2 == null || pts2.Length == 0)
                return Array.Empty<GVAR>();
            if (pts2.Length == 1)
            {
                float x = (float)pts2[0].X, y = (float)pts2[0].Y;
                return new[] { MakeLineGvar(gvarType, x, y, 0, x, y, 0) };
            }

            var items = new GVAR[pts2.Length - 1];
            for (int i = 0; i < pts2.Length - 1; i++)
            {
                float x0 = (float)pts2[i].X, y0 = (float)pts2[i].Y;
                float x1 = (float)pts2[i + 1].X, y1 = (float)pts2[i + 1].Y;
                items[i] = MakeLineGvar(gvarType, x0, y0, 0, x1, y1, 0);
            }

            return items;
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

        /// <summary>同条号内才生成线段；条号变化处不连跨条 GVAR（用于 splitByBar=break_segment）。</summary>
        public static GVAR[] BuildSegmentGvarsRespectingBarIds(CalibPoint3D[] pts3, int[]? barIds, short gvarType)
        {
            if (pts3 == null || pts3.Length == 0)
                return Array.Empty<GVAR>();
            if (barIds == null || barIds.Length != pts3.Length)
                return BuildSegmentGvarsFromPolyline(pts3, gvarType);

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

            return items.ToArray();
        }

        /// <summary>按 BarId 连续区间拆成多批（每批一条「折线」→ 若干 GVAR 线段）。</summary>
        public static List<(int BarId, GVAR[] Gvars)> BuildSegmentGvarBatchesByBarId(
            CalibPoint3D[] pts3,
            int[] barIds,
            short gvarType)
        {
            var batches = new List<(int BarId, GVAR[] Gvars)>();
            if (pts3 == null || pts3.Length == 0 || barIds == null || barIds.Length != pts3.Length)
                return batches;

            int start = 0;
            for (int i = 1; i <= pts3.Length; i++)
            {
                if (i == pts3.Length || barIds[i] != barIds[i - 1])
                {
                    int len = i - start;
                    if (len > 0)
                    {
                        var run = new CalibPoint3D[len];
                        Array.Copy(pts3, start, run, 0, len);
                        var gvars = BuildSegmentGvarsFromPolyline(run, gvarType);
                        if (gvars.Length > 0)
                            batches.Add((barIds[start], gvars));
                    }

                    start = i;
                }
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

        /// <summary>解析 send_plc 输入：优先 GVAR 列表 / PLC 页草稿，再 Points3D/Points。</summary>
        public static bool TryResolveSendPlcGvar(
            Dictionary<string, object?> inputs,
            string? usePlcPageGvarParam,
            short defaultGvarType,
            string? splitByBar,
            out GVAR[] gvarItems,
            out List<(int BarId, GVAR[] Gvars)>? barBatches,
            out string sourceTag,
            out string? diagnostic)
        {
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

                    barBatches = BuildSegmentGvarBatchesByBarId(pts3, barIds, defaultGvarType);
                    int total = barBatches.Sum(b => b.Gvars.Length);
                    gvarItems = Array.Empty<GVAR>();
                    sourceTag = $"Points3D×{pts3.Length}→{barBatches.Count}条BarId批/共{total}段GVAR";
                    return barBatches.Count > 0;
                }

                if (split == "break_segment" && barIds != null && barIds.Length == pts3.Length)
                    gvarItems = BuildSegmentGvarsRespectingBarIds(pts3, barIds, defaultGvarType);
                else
                    gvarItems = BuildSegmentGvarsFromPolyline(pts3, defaultGvarType);
                sourceTag = $"Points3D×{pts3.Length}→{gvarItems.Length}段GVAR";
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

                    var pts3from2 = pts2.Select(p => new CalibPoint3D(p.X, p.Y, 0)).ToArray();
                    barBatches = BuildSegmentGvarBatchesByBarId(pts3from2, barIds, defaultGvarType);
                    int total = barBatches.Sum(b => b.Gvars.Length);
                    gvarItems = Array.Empty<GVAR>();
                    sourceTag = $"Points×{pts2.Length}→{barBatches.Count}条BarId批/共{total}段GVAR";
                    return barBatches.Count > 0;
                }

                if (split == "break_segment" && barIds != null && barIds.Length == pts2.Length)
                {
                    var pts3from2 = pts2.Select(p => new CalibPoint3D(p.X, p.Y, 0)).ToArray();
                    gvarItems = BuildSegmentGvarsRespectingBarIds(pts3from2, barIds, defaultGvarType);
                }
                else
                    gvarItems = BuildSegmentGvarsFromPolyline(pts2, defaultGvarType);
                sourceTag = $"Points×{pts2.Length}→{gvarItems.Length}段GVAR";
                return true;
            }

            diagnostic = DescribeFailedInputs(inputs, useDraft);
            return false;
        }

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
