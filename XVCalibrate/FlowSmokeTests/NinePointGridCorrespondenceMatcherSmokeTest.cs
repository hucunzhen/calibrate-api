using System;
using System.Linq;
using CalibOperatorCLI_Example;
using CalibOperatorPInvoke;

namespace FlowSmokeTests
{
    internal static class NinePointGridCorrespondenceMatcherSmokeTest
    {
        public static int Run()
        {
            int fails = 0;
            fails += CheckWorldFileToBottomLeftGrid();
            fails += CheckWithPixelJitter();
            fails += CheckRotatedImageGrid();
            return fails;
        }

        private static int CheckWorldFileToBottomLeftGrid()
        {
            var world = LoadWorldPos3x3();
            FillBottomLeftImageGrid3x3(out var cols, out var rows);
            var image = ToPoints(cols, rows);
            ShuffleInPlace(image, new Random(11));

            var map = NinePointGridCorrespondenceMatcher.Match(world, image);
            if (!VerifyMapping(world, image, map, cols, rows, "world-file"))
                return 1;

            Console.WriteLine("  OK matcher world-file 3x3");
            return 0;
        }

        private static int CheckWithPixelJitter()
        {
            var world = LoadWorldPos3x3();
            FillBottomLeftImageGrid3x3(out var cols, out var rows);
            var rng = new Random(3);
            for (int i = 0; i < cols.Length; i++)
            {
                cols[i] += rng.NextDouble() * 4 - 2;
                rows[i] += rng.NextDouble() * 4 - 2;
            }

            var image = ToPoints(cols, rows);
            ShuffleInPlace(image, rng);
            var map = NinePointGridCorrespondenceMatcher.Match(world, image);
            if (!VerifyMapping(world, image, map, cols, rows, "jitter"))
                return 1;

            Console.WriteLine("  OK matcher jitter 3x3");
            return 0;
        }

        private static int CheckRotatedImageGrid()
        {
            var world = LoadWorldPos3x3();
            FillBottomLeftImageGrid3x3(out var cols, out var rows);
            double cx = cols.Average();
            double cy = rows.Average();
            RotateInPlace(cols, rows, cx, cy, 6.0);
            var image = ToPoints(cols, rows);
            ShuffleInPlace(image, new Random(5));

            var map = NinePointGridCorrespondenceMatcher.Match(world, image);
            var calImg = new Point2D[9];
            for (int i = 0; i < 9; i++)
                calImg[i] = image[map[i]];
            var cal = CalibAPI.CalibrateNinePoint(calImg, world);
            if (!cal.Success || cal.AverageError > 2.0)
            {
                Console.Error.WriteLine($"  FAIL matcher rotated: success={cal.Success} avg={cal.AverageError:F3}");
                return 1;
            }

            Console.WriteLine("  OK matcher rotated 3x3");
            return 0;
        }

        private static Point2D[] LoadWorldPos3x3()
        {
            double[] xs = { 438.166, 478.166, 518.166, 438.166, 478.166, 518.166, 438.166, 478.166, 518.166 };
            double[] ys = { 62.377, 62.377, 62.377, 87.377, 87.377, 87.377, 112.377, 112.377, 112.377 };
            var pts = new Point2D[9];
            for (int i = 0; i < 9; i++)
                pts[i] = new Point2D { X = xs[i], Y = ys[i] };
            return pts;
        }

        private static bool VerifyMapping(
            Point2D[] world,
            Point2D[] image,
            int[] map,
            double[] expectedCols,
            double[] expectedRows,
            string label)
        {
            const double tol = 2.5;
            for (int i = 0; i < world.Length; i++)
            {
                int j = map[i];
                var p = image[j];
                if (Math.Abs(p.X - expectedCols[i]) > tol || Math.Abs(p.Y - expectedRows[i]) > tol)
                {
                    Console.Error.WriteLine(
                        $"  FAIL matcher {label} world[{i}]→img[{j}]=({p.X:F1},{p.Y:F1}) expect ({expectedCols[i]:F1},{expectedRows[i]:F1})");
                    return false;
                }
            }

            var calImg = new Point2D[world.Length];
            for (int i = 0; i < world.Length; i++)
                calImg[i] = image[map[i]];
            var cal = CalibAPI.CalibrateNinePoint(calImg, world);
            if (!cal.Success || cal.AverageError > 1.5)
            {
                Console.Error.WriteLine($"  FAIL matcher {label} cal avg={cal.AverageError:F3}");
                return false;
            }

            return true;
        }

        private static void FillBottomLeftImageGrid3x3(out double[] cols, out double[] rows)
        {
            cols = new double[9];
            rows = new double[9];
            int idx = 0;
            for (int r = 2; r >= 0; r--)
            {
                for (int c = 0; c < 3; c++)
                {
                    cols[idx] = 100 + c * 50;
                    rows[idx] = 300 + r * 50;
                    idx++;
                }
            }
        }

        private static Point2D[] ToPoints(double[] cols, double[] rows)
        {
            var pts = new Point2D[cols.Length];
            for (int i = 0; i < cols.Length; i++)
                pts[i] = new Point2D { X = cols[i], Y = rows[i] };
            return pts;
        }

        private static void ShuffleInPlace(Point2D[] pts, Random rng)
        {
            for (int i = pts.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (pts[i], pts[j]) = (pts[j], pts[i]);
            }
        }

        private static void RotateInPlace(double[] cols, double[] rows, double cx, double cy, double deg)
        {
            double rad = deg * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            for (int i = 0; i < cols.Length; i++)
            {
                double dx = cols[i] - cx;
                double dy = rows[i] - cy;
                cols[i] = cx + dx * cos - dy * sin;
                rows[i] = cy + dx * sin + dy * cos;
            }
        }
    }
}
