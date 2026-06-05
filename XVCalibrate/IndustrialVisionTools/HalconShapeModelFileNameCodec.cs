#if HALCON_ENABLED
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CalibOperatorCLI_Example
{
    /// <summary>将形状模板创建参数编码进导出文件名，便于按名区分模型。格式：xv_{kind}_{src}_n4_a-30x60_ilp_mc10_….shm</summary>
    internal static class HalconShapeModelFileNameCodec
    {
        public const string Prefix = "xv";

        private static readonly Dictionary<string, HalconShapeModelKind> ModelKindByToken = new(StringComparer.OrdinalIgnoreCase)
        {
            ["shape"] = HalconShapeModelKind.Shape,
            ["scaled"] = HalconShapeModelKind.ScaledShape,
            ["dloc"] = HalconShapeModelKind.Deformable,
            ["dplan"] = HalconShapeModelKind.PlanarDeformable
        };

        private static readonly Dictionary<HalconShapeModelKind, string> ModelKindTokens = new()
        {
            [HalconShapeModelKind.Shape] = "shape",
            [HalconShapeModelKind.ScaledShape] = "scaled",
            [HalconShapeModelKind.Deformable] = "dloc",
            [HalconShapeModelKind.PlanarDeformable] = "dplan"
        };

        private static readonly Dictionary<string, HalconShapeModelSourceKind> SourceByToken = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ThXld"] = HalconShapeModelSourceKind.ThresholdXld,
            ["Edges"] = HalconShapeModelSourceKind.EdgesXld,
            ["Poly"] = HalconShapeModelSourceKind.PolygonXld,
            ["ImgR"] = HalconShapeModelSourceKind.ImageRectangle,
            ["ImgP"] = HalconShapeModelSourceKind.ImagePolygon,
            ["Geom"] = HalconShapeModelSourceKind.GeometryXld
        };

        private static readonly Dictionary<HalconShapeModelSourceKind, string> SourceTokens = new()
        {
            [HalconShapeModelSourceKind.ThresholdXld] = "ThXld",
            [HalconShapeModelSourceKind.EdgesXld] = "Edges",
            [HalconShapeModelSourceKind.PolygonXld] = "Poly",
            [HalconShapeModelSourceKind.ImageRectangle] = "ImgR",
            [HalconShapeModelSourceKind.ImagePolygon] = "ImgP",
            [HalconShapeModelSourceKind.GeometryXld] = "Geom"
        };

        private static readonly Dictionary<string, string> MetricByToken = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ilp"] = "ignore_local_polarity",
            ["igp"] = "ignore_global_polarity",
            ["icp"] = "ignore_color_polarity",
            ["upol"] = "use_polarity"
        };

        private static readonly Dictionary<string, string> MetricTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ignore_local_polarity"] = "ilp",
            ["ignore_global_polarity"] = "igp",
            ["ignore_color_polarity"] = "icp",
            ["use_polarity"] = "upol"
        };

        public static string BuildFileName(
            HalconShapeModelCreateOptions opt,
            HalconFlowModelKind flowKind,
            double minGray,
            double maxGray,
            string? resolvedMetric = null)
        {
            string metric = resolvedMetric ?? opt.Metric;
            var parts = new List<string>
            {
                Prefix,
                EncodeModelKind(opt.ModelKind, flowKind),
                EncodeSource(opt.SourceKind),
                $"n{Math.Max(0, opt.NumLevels)}",
                EncodeAngles(opt.AngleStartDeg, opt.AngleExtentDeg)
            };

            if (opt.AngleStepDeg > 0)
                parts.Add($"ast{FormatTokenNumber(opt.AngleStepDeg)}");

            parts.Add(EncodeMetric(metric));
            parts.Add($"mc{opt.MinContrast}");

            if (NeedsScaleSegment(opt))
                parts.Add(EncodeScale(opt.ScaleMin, opt.ScaleMax));

            if (NeedsGraySegment(opt.SourceKind))
                parts.Add(EncodeGray(minGray, maxGray));

            if (opt.SourceKind == HalconShapeModelSourceKind.EdgesXld)
            {
                parts.Add($"ea{FormatTokenNumber(opt.EdgeAlpha)}");
                parts.Add($"el{FormatTokenNumber(opt.EdgeLow)}");
                parts.Add($"eh{FormatTokenNumber(opt.EdgeHigh)}");
            }

            string ext = flowKind == HalconFlowModelKind.Deformable ? ".dfm" : ".shm";
            string name = string.Join("_", parts) + ext;
            return SanitizeFileName(name);
        }

        public static bool TryParse(string fileNameWithoutExtension, out HalconShapeModelParsedFileName parsed)
        {
            parsed = default!;
            if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
                return false;

            string stem = Path.GetFileNameWithoutExtension(fileNameWithoutExtension.Trim());
            string[] parts = stem.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 7 || !string.Equals(parts[0], Prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!ModelKindByToken.TryGetValue(parts[1], out HalconShapeModelKind modelKind))
                return false;
            if (!SourceByToken.TryGetValue(parts[2], out HalconShapeModelSourceKind sourceKind))
                return false;

            var opt = new HalconShapeModelCreateOptions
            {
                ModelKind = modelKind,
                SourceKind = sourceKind
            };

            double minGray = 0;
            double maxGray = 255;
            int i = 3;

            if (i >= parts.Length || !TryParseNumLevels(parts[i], out int numLevels))
                return false;
            opt.NumLevels = numLevels;
            i++;

            if (i >= parts.Length || !TryParseAngles(parts[i], out double angleStart, out double angleExtent))
                return false;
            opt.AngleStartDeg = angleStart;
            opt.AngleExtentDeg = angleExtent;
            i++;

            while (i < parts.Length)
            {
                string token = parts[i];
                if (token.StartsWith("ast", StringComparison.OrdinalIgnoreCase)
                    && TryParseTokenNumber(token[3..], out double angleStep))
                {
                    opt.AngleStepDeg = angleStep;
                }
                else if (MetricByToken.TryGetValue(token, out string? metric))
                {
                    opt.Metric = metric;
                }
                else if (token.StartsWith("mc", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(token[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mc))
                {
                    opt.MinContrast = mc;
                }
                else if (token.StartsWith('s') && token.Contains('-', StringComparison.Ordinal)
                         && TryParseScale(token[1..], out double sMin, out double sMax))
                {
                    opt.ScaleMin = sMin;
                    opt.ScaleMax = sMax;
                }
                else if (token.StartsWith('g') && token.Contains('-', StringComparison.Ordinal)
                         && TryParseGray(token[1..], out minGray, out maxGray))
                {
                    // captured below
                }
                else if (token.StartsWith("ea", StringComparison.OrdinalIgnoreCase)
                         && TryParseTokenNumber(token[2..], out double alpha))
                {
                    opt.EdgeAlpha = alpha;
                }
                else if (token.StartsWith("el", StringComparison.OrdinalIgnoreCase)
                         && TryParseTokenNumber(token[2..], out double low))
                {
                    opt.EdgeLow = low;
                }
                else if (token.StartsWith("eh", StringComparison.OrdinalIgnoreCase)
                         && TryParseTokenNumber(token[2..], out double high))
                {
                    opt.EdgeHigh = high;
                }

                i++;
            }

            parsed = new HalconShapeModelParsedFileName
            {
                CreateOptions = opt,
                MinGray = minGray,
                MaxGray = maxGray,
                FlowModelKind = modelKind is HalconShapeModelKind.Deformable or HalconShapeModelKind.PlanarDeformable
                    ? HalconFlowModelKind.Deformable
                    : HalconFlowModelKind.Shape
            };
            return true;
        }

        public static string DescribeFormat() =>
            "xv_{shape|scaled|dloc|dplan}_{ThXld|Edges|Poly|ImgR|ImgP|Geom}_n{层数}_a{起始}x{范围}_{ilp|igp|icp|upol}_mc{最小对比度}[_s{缩放下限}-{上限}][_g{灰度下限}-{上限}][_ea{α}_el{低}_eh{高}].shm（负号→m，小数点→p）";

        private static string EncodeModelKind(HalconShapeModelKind kind, HalconFlowModelKind flowKind)
        {
            if (flowKind == HalconFlowModelKind.Deformable && kind is HalconShapeModelKind.Shape or HalconShapeModelKind.ScaledShape)
                return "dloc";
            return ModelKindTokens.TryGetValue(kind, out string? token) ? token : "shape";
        }

        private static string EncodeSource(HalconShapeModelSourceKind kind) =>
            SourceTokens.TryGetValue(kind, out string? token) ? token : "ThXld";

        private static string EncodeAngles(double startDeg, double extentDeg) =>
            $"a{FormatTokenNumber(startDeg)}x{FormatTokenNumber(extentDeg)}";

        private static string EncodeMetric(string metric)
        {
            string key = (metric ?? "").Trim();
            return MetricTokens.TryGetValue(key, out string? token) ? token : "ilp";
        }

        private static bool NeedsScaleSegment(HalconShapeModelCreateOptions opt) =>
            opt.ModelKind is HalconShapeModelKind.ScaledShape or HalconShapeModelKind.Deformable or HalconShapeModelKind.PlanarDeformable
            && (Math.Abs(opt.ScaleMin - 1.0) > 1e-6 || Math.Abs(opt.ScaleMax - 1.0) > 1e-6);

        private static string EncodeScale(double min, double max) =>
            $"s{ScaleToToken(min)}-{ScaleToToken(max)}";

        private static bool NeedsGraySegment(HalconShapeModelSourceKind kind) =>
            kind == HalconShapeModelSourceKind.ThresholdXld;

        private static string EncodeGray(double minGray, double maxGray) =>
            $"g{GrayToToken(minGray)}-{GrayToToken(maxGray)}";

        private static string FormatTokenNumber(double value)
        {
            string s = value.ToString("0.###", CultureInfo.InvariantCulture);
            return s.Replace('-', 'm').Replace('.', 'p');
        }

        private static bool TryParseTokenNumber(string token, out double value)
        {
            string s = token.Replace('m', '-').Replace('p', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static int ScaleToToken(double scale) =>
            (int)Math.Round(scale * 100, MidpointRounding.AwayFromZero);

        private static int GrayToToken(double gray) =>
            (int)Math.Round(gray, MidpointRounding.AwayFromZero);

        private static bool TryParseNumLevels(string token, out int numLevels)
        {
            numLevels = 0;
            return token.Length > 1
                   && token[0] == 'n'
                   && int.TryParse(token[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out numLevels);
        }

        private static bool TryParseAngles(string token, out double startDeg, out double extentDeg)
        {
            startDeg = extentDeg = 0;
            Match m = Regex.Match(token, @"^a(.+)x(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success)
                return false;
            return TryParseTokenNumber(m.Groups[1].Value, out startDeg)
                   && TryParseTokenNumber(m.Groups[2].Value, out extentDeg);
        }

        private static bool TryParseScale(string token, out double min, out double max)
        {
            min = max = 1;
            int dash = token.IndexOf('-');
            if (dash <= 0)
                return false;
            if (!int.TryParse(token[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tMin)
                || !int.TryParse(token[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tMax))
                return false;
            min = tMin / 100.0;
            max = tMax / 100.0;
            return true;
        }

        private static bool TryParseGray(string token, out double minGray, out double maxGray)
        {
            minGray = 0;
            maxGray = 255;
            int dash = token.IndexOf('-');
            if (dash <= 0)
                return false;
            if (!int.TryParse(token[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tMin)
                || !int.TryParse(token[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tMax))
                return false;
            minGray = tMin;
            maxGray = tMax;
            return true;
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char ch in name)
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            return sb.ToString();
        }
    }

    internal sealed class HalconShapeModelParsedFileName
    {
        public HalconShapeModelCreateOptions CreateOptions { get; init; } = new();
        public HalconFlowModelKind FlowModelKind { get; init; } = HalconFlowModelKind.Shape;
        public double MinGray { get; init; }
        public double MaxGray { get; init; } = 255;
    }
}
#endif
