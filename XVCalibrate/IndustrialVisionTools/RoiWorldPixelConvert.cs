using System;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// ROI 毫米↔像素。preserveAngle：去掉九点仿射的平面旋转与剪切，只用均匀尺度+Y 翻转，
    /// 使毫米里平行的线段在图像里仍平行，且 0°(毫米 X) 对应图像 +X。
    /// </summary>
    internal static class RoiWorldPixelConvert
    {
        public static double UniformScaleMmPerPixel(in AffineTransform t)
        {
            double sx = Math.Sqrt(t.A * t.A + t.D * t.D);
            double sy = Math.Sqrt(t.B * t.B + t.E * t.E);
            double s = 0.5 * (sx + sy);
            if (s < 1e-12)
                s = Math.Max(sx, sy);
            if (s < 1e-12)
                throw new InvalidOperationException("九点仿射尺度无效，无法换算 ROI");
            return s;
        }

        public static Point2D PixelToMmPreserveAngle(double col, double row, in AffineTransform t)
        {
            double s = UniformScaleMmPerPixel(t);
            return new Point2D(s * col + t.C, -s * row + t.F);
        }

        public static Point2D MmToPixelPreserveAngle(double worldX, double worldY, in AffineTransform t)
        {
            double s = UniformScaleMmPerPixel(t);
            return new Point2D((worldX - t.C) / s, (t.F - worldY) / s);
        }

        public static double PixelLengthToMm(double lengthPx, in AffineTransform t) =>
            lengthPx <= 0 ? 0 : lengthPx * UniformScaleMmPerPixel(t);

        public static double MmLengthToPixels(double lengthMm, in AffineTransform t) =>
            lengthMm <= 0 ? 0 : lengthMm / UniformScaleMmPerPixel(t);
    }
}
