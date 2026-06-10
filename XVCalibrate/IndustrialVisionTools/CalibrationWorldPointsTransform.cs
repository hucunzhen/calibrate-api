using System;
using System.Linq;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>九点标定世界点行序 / Y 向变换。</summary>
    internal static class CalibrationWorldPointsTransform
    {
        /// <summary>
        /// 按算子参数变换世界点。rowOrder：bottomFirst=底行优先（与 bl_xy 检测默认一致）；
        /// topFirst/上下颠倒=行序上下翻转（顶行点列与检测首行配对）。
        /// </summary>
        public static Point2D[] ApplyCalibrateWorldRowOrder(Point2D[] pts, string? rowOrderRaw)
        {
            if (pts == null || pts.Length == 0)
                return pts;

            string mode = (rowOrderRaw ?? "bottomFirst").Trim();
            if (IsBottomFirst(mode))
                return pts;

            if (IsTopFirst(mode))
                return ReverseGridRows(pts);

            throw new InvalidOperationException(
                $"未知 worldRowOrder='{rowOrderRaw}'（bottomFirst/底行优先 或 topFirst/上下颠倒）");
        }

        public static bool IsBottomFirst(string mode)
        {
            string k = mode.ToLowerInvariant();
            return k is "bottomfirst" or "bottom_first" or "bl_xy" or "底行优先" or "none" or "无" or "";
        }

        public static bool IsTopFirst(string mode)
        {
            string k = mode.ToLowerInvariant();
            return k is "topfirst" or "top_first" or "reverse" or "reverse_rows" or "flip" or "flip_vertical"
                or "上下" or "上下颠倒" or "上下反转" or "顶行优先";
        }

        /// <summary>行优先网格：第 0 行与最后一行对调，行内列顺序不变。</summary>
        public static Point2D[] ReverseGridRows(Point2D[] pts)
        {
            int n = pts.Length;
            if (n <= 1)
                return pts;

            var (rows, cols) = CalibrationPointGrid.InferLayout(n, pts);
            if (rows <= 1 || rows * cols != n)
                return pts.Reverse().ToArray();

            var result = new Point2D[n];
            for (int r = 0; r < rows; r++)
            {
                int srcR = rows - 1 - r;
                for (int c = 0; c < cols; c++)
                    result[r * cols + c] = pts[srcR * cols + c];
            }

            return result;
        }
    }
}
