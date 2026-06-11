using System;
using System.Collections.Generic;
using System.Globalization;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 在世界坐标系下微调九点仿射标定：平移与绕 pivot 缩放。
    /// world' = scale * (world - pivot) + pivot + offset
    /// </summary>
    internal static class AffineTransformAdjust
    {
        public static AffineTransform Apply(
            in AffineTransform source,
            double offsetWorldX,
            double offsetWorldY,
            double scaleX,
            double scaleY,
            double pivotWorldX,
            double pivotWorldY)
        {
            if (scaleX <= 0 || scaleY <= 0)
                throw new ArgumentOutOfRangeException(nameof(scaleX), "scaleX/scaleY 须为正数");

            return new AffineTransform
            {
                A = scaleX * source.A,
                B = scaleX * source.B,
                C = scaleX * source.C + (1.0 - scaleX) * pivotWorldX + offsetWorldX,
                D = scaleY * source.D,
                E = scaleY * source.E,
                F = scaleY * source.F + (1.0 - scaleY) * pivotWorldY + offsetWorldY,
            };
        }

        public static (double dx, double dy) ResolveNudgePreset(string? preset, double stepMm)
        {
            if (stepMm == 0)
                return (0, 0);

            string key = (preset ?? "").Trim().ToLowerInvariant();
            return key switch
            {
                "up" or "上" or "向上" => (0, stepMm),
                "down" or "下" or "向下" => (0, -stepMm),
                "left" or "左" or "向左" => (-stepMm, 0),
                "right" or "右" or "向右" => (stepMm, 0),
                "up_left" or "左上" => (-stepMm, stepMm),
                "up_right" or "右上" => (stepMm, stepMm),
                "down_left" or "左下" => (-stepMm, -stepMm),
                "down_right" or "右下" => (stepMm, -stepMm),
                _ => (0, 0),
            };
        }

        public static double ParseDoubleParam(IReadOnlyDictionary<string, string> parameters, string name, double defaultValue)
        {
            if (!parameters.TryGetValue(name, out string? raw) || string.IsNullOrWhiteSpace(raw))
                return defaultValue;
            return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v
                : defaultValue;
        }

        public static string BuildSummary(
            double offsetWorldX,
            double offsetWorldY,
            double scaleX,
            double scaleY,
            double pivotWorldX,
            double pivotWorldY,
            string? nudgePreset)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(nudgePreset)
                && !string.Equals(nudgePreset.Trim(), "none", StringComparison.OrdinalIgnoreCase)
                && nudgePreset.Trim() is not ("无" or ""))
                parts.Add($"预设={nudgePreset.Trim()}");
            if (Math.Abs(offsetWorldX) > 1e-12 || Math.Abs(offsetWorldY) > 1e-12)
                parts.Add($"Δ=({offsetWorldX:G4},{offsetWorldY:G4}) mm");
            if (Math.Abs(scaleX - 1) > 1e-12 || Math.Abs(scaleY - 1) > 1e-12)
                parts.Add($"scale=({scaleX:G6},{scaleY:G6}) @({pivotWorldX:G4},{pivotWorldY:G4})");
            return parts.Count > 0 ? string.Join(" · ", parts) : "无变化";
        }

        /// <summary>行优先网格的几何中心点（3×3 时为正中间那一点）。</summary>
        public static Point2D? GetGridCenterPoint(Point2D[]? pts)
        {
            if (pts == null || pts.Length == 0)
                return null;

            var (rows, cols) = CalibrationPointGrid.InferLayout(pts.Length, pts);
            if (rows >= 1 && cols >= 1 && rows * cols == pts.Length)
            {
                int centerRow = rows / 2;
                int centerCol = cols / 2;
                return pts[centerRow * cols + centerCol];
            }

            double sx = 0, sy = 0;
            foreach (var p in pts)
            {
                sx += p.X;
                sy += p.Y;
            }

            int n = pts.Length;
            return new Point2D { X = sx / n, Y = sy / n };
        }

        public static bool IsAutoPivotToken(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return true;
            string t = raw.Trim();
            return string.Equals(t, "auto", StringComparison.OrdinalIgnoreCase)
                || t is "center" or "mid" or "middle" or "中心" or "中间" or "网格中心";
        }

        /// <summary>
        /// 解析缩放中心：参数留空/auto/双 0 时优先取 WorldPts 网格中心，否则 ImagePts 经 Transform 转世界坐标。
        /// </summary>
        public static (double X, double Y, bool UsedGridCenter) ResolvePivotWorld(
            IReadOnlyDictionary<string, string> parameters,
            Point2D[]? worldPts,
            Point2D[]? imagePts,
            in AffineTransform transform)
        {
            bool autoX = !TryGetExplicitPivotParam(parameters, "pivotWorldX", out double pivotX);
            bool autoY = !TryGetExplicitPivotParam(parameters, "pivotWorldY", out double pivotY);

            // 旧流程默认写 0,0，语义也是「网格中心」而非世界原点
            if (!autoX && !autoY && Math.Abs(pivotX) < 1e-12 && Math.Abs(pivotY) < 1e-12)
            {
                autoX = true;
                autoY = true;
            }

            if (!autoX && !autoY)
                return (pivotX, pivotY, false);

            Point2D? centerWorld = null;
            if (worldPts != null && worldPts.Length > 0)
                centerWorld = GetGridCenterPoint(worldPts);
            else if (imagePts != null && imagePts.Length > 0)
            {
                var centerPix = GetGridCenterPoint(imagePts);
                if (centerPix != null)
                    centerWorld = CalibAPI.ImageToWorld(centerPix.Value, transform);
            }

            if (centerWorld != null)
            {
                return (
                    autoX ? centerWorld.Value.X : pivotX,
                    autoY ? centerWorld.Value.Y : pivotY,
                    autoX && autoY);
            }

            if (autoX)
                pivotX = 0;
            if (autoY)
                pivotY = 0;
            return (pivotX, pivotY, false);
        }

        private static bool TryGetExplicitPivotParam(
            IReadOnlyDictionary<string, string> parameters,
            string name,
            out double value)
        {
            value = 0;
            if (!parameters.TryGetValue(name, out string? raw) || IsAutoPivotToken(raw))
                return false;
            return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
