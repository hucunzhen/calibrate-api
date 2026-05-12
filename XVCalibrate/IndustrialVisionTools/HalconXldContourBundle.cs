using System.Collections.Generic;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// HALCON XLD 在流程中的托管表示：每条轮廓为像素坐标折线（X=列 col，Y=行 row）。
    /// 与原生 <c>find_contours</c> 的 <c>(flatX,flatY,lengths,...)</c> 分离，单独分支使用。
    /// </summary>
    public sealed class HalconXldContourBundle
    {
        public int Width { get; init; }
        public int Height { get; init; }

        /// <summary>各条 XLD 轮廓的点列（至少 2 点）。</summary>
        public IReadOnlyList<Point2D[]> Contours { get; init; } = new List<Point2D[]>();

        public int ContourCount => Contours?.Count ?? 0;
    }
}
