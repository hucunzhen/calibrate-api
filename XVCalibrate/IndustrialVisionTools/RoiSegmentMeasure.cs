using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    internal static class RoiSegmentMeasure
    {
        public static double PixelLengthToWorldMm(
            double lengthPx,
            double fromCol,
            double fromRow,
            double dirX,
            double dirY,
            in AffineTransform t)
        {
            if (lengthPx <= 0)
                return 0;
            var a = CalibAPI.ImageToWorld(new Point2D(fromCol, fromRow), t);
            var b = CalibAPI.ImageToWorld(new Point2D(fromCol + dirX * lengthPx, fromRow + dirY * lengthPx), t);
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            return System.Math.Sqrt(dx * dx + dy * dy);
        }

        public static double MmLengthToPixels(
            double lengthMm,
            double fromCol,
            double fromRow,
            double dirX,
            double dirY,
            in AffineTransform t)
        {
            if (lengthMm <= 0)
                return 0;
            double px = lengthMm;
            for (int i = 0; i < 24; i++)
            {
                double mm = PixelLengthToWorldMm(px, fromCol, fromRow, dirX, dirY, t);
                if (mm < 1e-9)
                    break;
                px *= lengthMm / mm;
            }

            return px;
        }
    }
}
