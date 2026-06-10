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
    }
}
