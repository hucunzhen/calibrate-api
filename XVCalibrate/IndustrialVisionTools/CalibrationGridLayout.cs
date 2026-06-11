using System;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>标定网格：支持 3×3、4×4、5×5、6×6 方阵及自动推断。</summary>
    internal static class CalibrationGridLayout
    {
        public const int MinGridSize = 3;
        public const int MaxGridSize = 6;

        public static int[] SupportedSquareSizes { get; } = { 3, 4, 5, 6 };

        /// <summary>解析 calibrationGrid 参数；无法识别时返回 auto。</summary>
        public static bool TryParsePreset(string? raw, out int rows, out int cols)
        {
            rows = cols = 0;
            string key = (raw ?? "auto").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key) || key is "auto" or "自动" or "0")
                return false;

            key = key
                .Replace("×", "x", StringComparison.Ordinal)
                .Replace("*", "x", StringComparison.Ordinal)
                .Replace(" ", "", StringComparison.Ordinal);

            if (key is "3x3" or "nine" or "nine_3x3" or "3×3" or "九宫格" or "九点")
            {
                rows = cols = 3;
                return true;
            }

            if (key is "4x4" or "grid_4x4" or "4×4" or "十六点" or "16点")
            {
                rows = cols = 4;
                return true;
            }

            if (key is "5x5" or "grid_5x5" or "5×5" or "25点")
            {
                rows = cols = 5;
                return true;
            }

            if (key is "6x6" or "grid_6x6" or "6×6" or "36点")
            {
                rows = cols = 6;
                return true;
            }

            if (key is "scattered" or "散点" or "random" or "jitter_only")
            {
                rows = cols = 0;
                return true;
            }

            if (key is "jitter" or "扰动" or "grid_jitter")
            {
                rows = cols = 0;
                return true;
            }

            if (key.Length == 3 && key[1] == 'x'
                && int.TryParse(key.AsSpan(0, 1), out int r)
                && int.TryParse(key.AsSpan(2, 1), out int c)
                && r == c && r >= MinGridSize && r <= MaxGridSize)
            {
                rows = cols = r;
                return true;
            }

            return false;
        }

        public static bool IsScatteredPreset(string? raw)
        {
            string key = (raw ?? "").Trim().ToLowerInvariant();
            return key is "scattered" or "散点" or "random";
        }

        public static bool IsJitterGridPreset(string? raw)
        {
            string key = (raw ?? "").Trim().ToLowerInvariant();
            return key is "jitter" or "扰动" or "grid_jitter" or "网格+扰动";
        }

        public static (int rows, int cols) Resolve(
            int pointCount,
            Point2D[]? worldPts,
            string? calibrationGridPreset,
            int hintRows = 0,
            int hintCols = 0)
        {
            if (TryParsePreset(calibrationGridPreset, out int pr, out int pc))
            {
                if (IsScatteredPreset(calibrationGridPreset) || IsJitterGridPreset(calibrationGridPreset))
                    return CalibrationPointGrid.InferLayout(pointCount, worldPts);

                if (pointCount != pr * pc)
                    throw new InvalidOperationException(
                        $"标定网格 {FormatLabel(pr, pc)} 需要 {pr * pc} 对世界/像素点，当前 {pointCount} 个");
                return (pr, pc);
            }

            if (hintRows > 0 && hintCols > 0)
            {
                if (hintRows * hintCols != pointCount)
                    throw new InvalidOperationException(
                        $"gridRows×gridCols={hintRows}×{hintCols} 与点数 {pointCount} 不一致");
                return (hintRows, hintCols);
            }

            var (rows, cols) = CalibrationPointGrid.ResolveLayout(pointCount, worldPts);
            if (IsSupportedSquare(rows, cols))
                return (rows, cols);

            if (rows * cols == pointCount && rows == cols)
                throw new InvalidOperationException(
                    $"标定网格 {rows}×{cols} 超出支持范围（当前支持 {MinGridSize}×{MinGridSize}～{MaxGridSize}×{MaxGridSize} 方阵）");

            return (rows, cols);
        }

        public static bool IsSupportedSquare(int rows, int cols) =>
            rows == cols && rows >= MinGridSize && rows <= MaxGridSize;

        public static string FormatLabel(int rows, int cols) =>
            rows == cols ? $"{rows}×{cols}" : $"{rows}×{cols}";

        public static string FormatDialogTitle(int rows, int cols) =>
            rows == cols && rows == 3
                ? "九点标定"
                : $"网格标定 ({FormatLabel(rows, cols)})";

        public static string FormatQualityName(int pointCount, int rows, int cols) =>
            rows == cols && rows == 3 ? "九点标定" : $"网格标定 {FormatLabel(rows, cols)}";
    }
}
