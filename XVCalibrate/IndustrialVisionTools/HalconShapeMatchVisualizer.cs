#if HALCON_ENABLED
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>将 FindShapeModel 结果绘制到位图（模型轮廓变换 + 十字 + 得分）。</summary>
    internal static class HalconShapeMatchVisualizer
    {
        private static readonly Color[] MatchColors =
        {
            Color.FromArgb(255, 255, 140, 0),
            Color.FromArgb(255, 0, 191, 255),
            Color.FromArgb(255, 50, 205, 50),
            Color.FromArgb(255, 255, 0, 255),
            Color.FromArgb(255, 255, 215, 0),
            Color.FromArgb(255, 0, 255, 255)
        };

        public static void DrawOnBitmap(
            Bitmap bmp,
            long modelId,
            double[] rows,
            double[] cols,
            double[] angles,
            double[] scales,
            double[] scores,
            int contourLevel,
            float crossHalf,
            float strokeWidth,
            bool drawScores)
        {
            if (bmp == null) throw new ArgumentNullException(nameof(bmp));

            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            int n = Math.Min(rows?.Length ?? 0, cols?.Length ?? 0);
            if (n == 0) return;

            Point2D[][] modelContours = Array.Empty<Point2D[]>();
            if (modelId >= 0)
            {
                try
                {
                    modelContours = HalconFlowBridge.GetShapeModelContourPoints(modelId, contourLevel);
                }
                catch
                {
                    modelContours = Array.Empty<Point2D[]>();
                }
            }

            using var scoreFont = new Font(FontFamily.GenericSansSerif, 11f, FontStyle.Bold);
            using var scoreBrush = new SolidBrush(Color.White);
            using var scoreBack = new SolidBrush(Color.FromArgb(160, 0, 0, 0));

            for (int m = 0; m < n; m++)
            {
                Color color = MatchColors[m % MatchColors.Length];
                using var pen = new Pen(color, strokeWidth);
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                double matchRow = rows![m];
                double matchCol = cols![m];
                double angleDeg = angles != null && angles.Length > m ? angles[m] : 0;
                double scale = scales != null && scales.Length > m ? scales[m] : 1.0;

                foreach (Point2D[] contour in modelContours)
                {
                    if (contour == null || contour.Length < 2)
                        continue;

                    var pts = new PointF[contour.Length];
                    for (int i = 0; i < contour.Length; i++)
                    {
                        TransformModelPointToImage(contour[i], matchRow, matchCol, angleDeg, scale, out double colImg, out double rowImg);
                        pts[i] = new PointF((float)colImg, (float)rowImg);
                    }

                    g.DrawLines(pen, pts);
                }

                float cx = (float)matchCol;
                float cy = (float)matchRow;
                g.DrawLine(pen, cx - crossHalf, cy, cx + crossHalf, cy);
                g.DrawLine(pen, cx, cy - crossHalf, cx, cy + crossHalf);

                if (drawScores && scores != null && scores.Length > m)
                {
                    string text = scores[m].ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                    float tx = cx + crossHalf + 4f;
                    float ty = cy - crossHalf;
                    var size = g.MeasureString(text, scoreFont);
                    g.FillRectangle(scoreBack, tx - 2, ty - 1, size.Width + 4, size.Height + 2);
                    g.DrawString(text, scoreFont, scoreBrush, tx, ty);
                }
            }
        }

        private static void TransformModelPointToImage(
            Point2D modelPt,
            double matchRow,
            double matchCol,
            double angleDeg,
            double scale,
            out double colImg,
            out double rowImg)
        {
            double a = angleDeg * Math.PI / 180.0;
            double c = Math.Cos(a);
            double s = Math.Sin(a);
            double z = Math.Abs(scale) > 1e-9 ? scale : 1.0;
            double row = modelPt.Y * z;
            double col = modelPt.X * z;
            rowImg = row * c - col * s + matchRow;
            colImg = row * s + col * c + matchCol;
        }
    }
}
#endif
