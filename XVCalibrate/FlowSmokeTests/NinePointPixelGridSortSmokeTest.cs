using System;
using System.Linq;
using CalibOperatorCLI_Example;

namespace FlowSmokeTests
{
    /// <summary>NinePointPixelGridSort：左下原点 Y 向上、底行优先，对小角度旋转鲁棒。</summary>
    internal static class NinePointPixelGridSortSmokeTest
    {
        public static int Run()
        {
            int fails = 0;
            fails += CheckAxisAligned3x3NoShuffle();
            fails += CheckAxisAligned3x3();
            fails += CheckRotated3x3();
            fails += CheckYxSortIsTopFirst();
            return fails;
        }

        private static int CheckAxisAligned3x3NoShuffle()
        {
            FillBottomLeftGrid3x3(out var cols, out var rows);
            var order = NinePointPixelGridSort.SortIndices(9, rows, cols, 3, 3);
            if (!SequenceEqual(order, Enumerable.Range(0, 9)))
            {
                Console.Error.WriteLine($"  FAIL bl_xy no-shuffle: [{string.Join(",", order)}]");
                return 1;
            }

            Console.WriteLine("  OK bl_xy no-shuffle 3x3");
            return 0;
        }

        private static int CheckAxisAligned3x3()
        {
            FillBottomLeftGrid3x3(out var cols, out var rows);
            var expectedCols = (double[])cols.Clone();
            var expectedRows = (double[])rows.Clone();
            ShuffleInPlace(cols, rows, new Random(42));
            if (!CheckSpatialOrder(cols, rows, expectedCols, expectedRows, 3, 3, "axis-aligned"))
                return 1;

            Console.WriteLine("  OK bl_xy axis-aligned 3x3");
            return 0;
        }

        private static int CheckRotated3x3()
        {
            FillBottomLeftGrid3x3(out var cols, out var rows);
            var expectedCols = (double[])cols.Clone();
            var expectedRows = (double[])rows.Clone();
            double cx = cols.Average();
            double cy = rows.Average();
            RotateInPlace(cols, rows, cx, cy, 7.5);
            RotateInPlace(expectedCols, expectedRows, cx, cy, 7.5);
            ShuffleInPlace(cols, rows, new Random(7));
            if (!CheckSpatialOrder(cols, rows, expectedCols, expectedRows, 3, 3, "rotated 7.5°"))
                return 1;

            Console.WriteLine("  OK bl_xy rotated 3x3");
            return 0;
        }

        private static bool CheckSpatialOrder(
            double[] cols,
            double[] rows,
            double[] expectedCols,
            double[] expectedRows,
            int gridRows,
            int gridCols,
            string label)
        {
            int n = cols.Length;
            var order = NinePointPixelGridSort.SortIndices(n, rows, cols, gridRows, gridCols);
            const double tol = 1e-3;
            for (int k = 0; k < n; k++)
            {
                int i = order[k];
                if (Math.Abs(cols[i] - expectedCols[k]) > tol || Math.Abs(rows[i] - expectedRows[k]) > tol)
                {
                    Console.Error.WriteLine(
                        $"  FAIL bl_xy {label} at k={k}: got ({cols[i]:F3},{rows[i]:F3}) expect ({expectedCols[k]:F3},{expectedRows[k]:F3}); order=[{string.Join(",", order)}]");
                    return false;
                }
            }

            return true;
        }

        private static void FillBottomLeftGrid3x3(out double[] cols, out double[] rows)
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

        private static int CheckYxSortIsTopFirst()
        {
            double[] rows = { 100, 100, 100, 200, 200, 200, 300, 300, 300 };
            double[] cols = { 10, 20, 30, 10, 20, 30, 10, 20, 30 };
            var order = Enumerable.Range(0, 9).OrderBy(i => rows[i]).ThenBy(i => cols[i]).ToArray();
            if (order[0] != 0)
            {
                Console.Error.WriteLine("  FAIL yx reference: top row should be first");
                return 1;
            }

            Console.WriteLine("  OK yx top-first reference");
            return 0;
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

        private static void ShuffleInPlace(double[] cols, double[] rows, Random rng)
        {
            for (int i = cols.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (cols[i], cols[j]) = (cols[j], cols[i]);
                (rows[i], rows[j]) = (rows[j], rows[i]);
            }
        }

        private static bool SequenceEqual(int[] a, System.Collections.Generic.IEnumerable<int> b)
        {
            var bb = b.ToArray();
            if (a.Length != bb.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != bb[i])
                    return false;
            }

            return true;
        }
    }
}
