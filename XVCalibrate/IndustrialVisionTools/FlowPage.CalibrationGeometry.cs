// 流程页分文件：标定几何（单应 / 二次多项式）、标定结果落盘 DTO 与拟合辅助方法。

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Numerics;
using Microsoft.Win32;
using CalibOperatorPInvoke;
using HslCommunication.ModBus;
#if HALCON_ENABLED
using HalconDotNet;
#endif

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage : UserControl
    {
        private struct HomographyTransform
        {
            public double H11, H12, H13;
            public double H21, H22, H23;
            public double H31, H32, H33;
        }

        private struct Poly2DTransform
        {
            public double X_x, X_y, X_1, X_x2, X_xy, X_y2;
            public double Y_x, Y_y, Y_1, Y_x2, Y_xy, Y_y2;
        }

        private static readonly JsonSerializerOptions CalibrationResultFileJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private sealed class CalibrationResultFileV1
        {
            public int SchemaVersion { get; set; } = 1;
            public string? CalibrationJson { get; set; }
            public AffineCalibrationSaveV1? Affine { get; set; }
            public HomographyCalibrationSaveV1? Homography { get; set; }
            public Poly2DCalibrationSaveV1? Poly2d { get; set; }
            public IntrinsicsCalibrationSaveV1? Intrinsics { get; set; }
        }

        private sealed class AffineCalibrationSaveV1
        {
            public double A { get; set; }
            public double B { get; set; }
            public double C { get; set; }
            public double D { get; set; }
            public double E { get; set; }
            public double F { get; set; }

            public static AffineCalibrationSaveV1 From(AffineTransform t) =>
                new AffineCalibrationSaveV1 { A = t.A, B = t.B, C = t.C, D = t.D, E = t.E, F = t.F };

            public AffineTransform ToAffine() =>
                new AffineTransform { A = A, B = B, C = C, D = D, E = E, F = F };
        }

        private sealed class HomographyCalibrationSaveV1
        {
            public double H11 { get; set; }
            public double H12 { get; set; }
            public double H13 { get; set; }
            public double H21 { get; set; }
            public double H22 { get; set; }
            public double H23 { get; set; }
            public double H31 { get; set; }
            public double H32 { get; set; }
            public double H33 { get; set; }

            public static HomographyCalibrationSaveV1 From(HomographyTransform h) =>
                new HomographyCalibrationSaveV1
                {
                    H11 = h.H11, H12 = h.H12, H13 = h.H13,
                    H21 = h.H21, H22 = h.H22, H23 = h.H23,
                    H31 = h.H31, H32 = h.H32, H33 = h.H33
                };

            public HomographyTransform ToHomography() =>
                new HomographyTransform
                {
                    H11 = H11, H12 = H12, H13 = H13,
                    H21 = H21, H22 = H22, H23 = H23,
                    H31 = H31, H32 = H32, H33 = H33
                };
        }

        private sealed class Poly2DCalibrationSaveV1
        {
            public double X_x { get; set; }
            public double X_y { get; set; }
            public double X_1 { get; set; }
            public double X_x2 { get; set; }
            public double X_xy { get; set; }
            public double X_y2 { get; set; }
            public double Y_x { get; set; }
            public double Y_y { get; set; }
            public double Y_1 { get; set; }
            public double Y_x2 { get; set; }
            public double Y_xy { get; set; }
            public double Y_y2 { get; set; }

            public static Poly2DCalibrationSaveV1 From(Poly2DTransform p) =>
                new Poly2DCalibrationSaveV1
                {
                    X_x = p.X_x, X_y = p.X_y, X_1 = p.X_1, X_x2 = p.X_x2, X_xy = p.X_xy, X_y2 = p.X_y2,
                    Y_x = p.Y_x, Y_y = p.Y_y, Y_1 = p.Y_1, Y_x2 = p.Y_x2, Y_xy = p.Y_xy, Y_y2 = p.Y_y2
                };

            public Poly2DTransform ToPoly() =>
                new Poly2DTransform
                {
                    X_x = X_x, X_y = X_y, X_1 = X_1, X_x2 = X_x2, X_xy = X_xy, X_y2 = X_y2,
                    Y_x = Y_x, Y_y = Y_y, Y_1 = Y_1, Y_x2 = Y_x2, Y_xy = Y_xy, Y_y2 = Y_y2
                };
        }

        private sealed class IntrinsicsCalibrationSaveV1
        {
            public double Fx { get; set; }
            public double Fy { get; set; }
            public double Cx { get; set; }
            public double Cy { get; set; }
            public double K1 { get; set; }
            public double K2 { get; set; }
            public double P1 { get; set; }
            public double P2 { get; set; }
            public double K3 { get; set; }
            public double Rms { get; set; }

            public static IntrinsicsCalibrationSaveV1 From(CameraIntrinsics i) =>
                new IntrinsicsCalibrationSaveV1
                {
                    Fx = i.Fx, Fy = i.Fy, Cx = i.Cx, Cy = i.Cy,
                    K1 = i.K1, K2 = i.K2, P1 = i.P1, P2 = i.P2, K3 = i.K3,
                    Rms = i.Rms
                };

            public CameraIntrinsics ToIntrinsics() =>
                new CameraIntrinsics
                {
                    Fx = Fx, Fy = Fy, Cx = Cx, Cy = Cy,
                    K1 = K1, K2 = K2, P1 = P1, P2 = P2, K3 = K3,
                    Rms = Rms
                };
        }

        private static bool SolveLinear(double[,] a, double[] b, out double[] x)
        {
            int n = b.Length;
            x = new double[n];
            var aug = new double[n, n + 1];
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++) aug[r, c] = a[r, c];
                aug[r, n] = b[r];
            }

            for (int i = 0; i < n; i++)
            {
                int pivot = i;
                double best = Math.Abs(aug[i, i]);
                for (int r = i + 1; r < n; r++)
                {
                    double v = Math.Abs(aug[r, i]);
                    if (v > best) { best = v; pivot = r; }
                }
                if (best < 1e-12) return false;
                if (pivot != i)
                {
                    for (int c = i; c <= n; c++)
                    {
                        double tmp = aug[i, c];
                        aug[i, c] = aug[pivot, c];
                        aug[pivot, c] = tmp;
                    }
                }

                double diag = aug[i, i];
                for (int c = i; c <= n; c++) aug[i, c] /= diag;
                for (int r = 0; r < n; r++)
                {
                    if (r == i) continue;
                    double f = aug[r, i];
                    if (Math.Abs(f) < 1e-15) continue;
                    for (int c = i; c <= n; c++) aug[r, c] -= f * aug[i, c];
                }
            }
            for (int i = 0; i < n; i++) x[i] = aug[i, n];
            return true;
        }

        private static bool SolveLeastSquares(double[][] rows, double[] y, int cols, out double[] coeffs)
        {
            coeffs = new double[cols];
            var ata = new double[cols, cols];
            var aty = new double[cols];
            for (int r = 0; r < rows.Length; r++)
            {
                var row = rows[r];
                for (int i = 0; i < cols; i++)
                {
                    aty[i] += row[i] * y[r];
                    for (int j = 0; j < cols; j++) ata[i, j] += row[i] * row[j];
                }
            }
            return SolveLinear(ata, aty, out coeffs);
        }

        private static HomographyTransform FitHomography(Point2D[] imgPts, Point2D[] worldPts)
        {
            int n = imgPts.Length;
            var rows = new List<double[]>(n * 2);
            var yy = new List<double>(n * 2);
            for (int i = 0; i < n; i++)
            {
                double x = imgPts[i].X, y = imgPts[i].Y;
                double X = worldPts[i].X, Y = worldPts[i].Y;
                rows.Add(new[] { x, y, 1.0, 0, 0, 0, -x * X, -y * X });
                yy.Add(X);
                rows.Add(new[] { 0, 0, 0, x, y, 1.0, -x * Y, -y * Y });
                yy.Add(Y);
            }
            if (!SolveLeastSquares(rows.ToArray(), yy.ToArray(), 8, out var c))
                throw new InvalidOperationException("透视标定失败: 方程不可解");
            return new HomographyTransform
            {
                H11 = c[0], H12 = c[1], H13 = c[2],
                H21 = c[3], H22 = c[4], H23 = c[5],
                H31 = c[6], H32 = c[7], H33 = 1.0
            };
        }

        private static Point2D ApplyHomography(Point2D p, HomographyTransform h)
        {
            double den = h.H31 * p.X + h.H32 * p.Y + h.H33;
            if (Math.Abs(den) < 1e-12) den = 1e-12;
            return new Point2D(
                (h.H11 * p.X + h.H12 * p.Y + h.H13) / den,
                (h.H21 * p.X + h.H22 * p.Y + h.H23) / den);
        }

        private static Poly2DTransform FitPoly2D(Point2D[] imgPts, Point2D[] worldPts)
        {
            int n = imgPts.Length;
            var rows = new double[n][];
            var yx = new double[n];
            var yy = new double[n];
            for (int i = 0; i < n; i++)
            {
                double x = imgPts[i].X, y = imgPts[i].Y;
                rows[i] = new[] { x, y, 1.0, x * x, x * y, y * y };
                yx[i] = worldPts[i].X;
                yy[i] = worldPts[i].Y;
            }
            if (!SolveLeastSquares(rows, yx, 6, out var cx) || !SolveLeastSquares(rows, yy, 6, out var cy))
                throw new InvalidOperationException("Poly2D标定失败: 方程不可解");
            return new Poly2DTransform
            {
                X_x = cx[0], X_y = cx[1], X_1 = cx[2], X_x2 = cx[3], X_xy = cx[4], X_y2 = cx[5],
                Y_x = cy[0], Y_y = cy[1], Y_1 = cy[2], Y_x2 = cy[3], Y_xy = cy[4], Y_y2 = cy[5]
            };
        }

        private static Point2D ApplyPoly2D(Point2D p, Poly2DTransform t)
        {
            double x = p.X, y = p.Y;
            double xx = x * x, xy = x * y, yy = y * y;
            return new Point2D(
                t.X_x * x + t.X_y * y + t.X_1 + t.X_x2 * xx + t.X_xy * xy + t.X_y2 * yy,
                t.Y_x * x + t.Y_y * y + t.Y_1 + t.Y_x2 * xx + t.Y_xy * xy + t.Y_y2 * yy);
        }

        private static string FormatHomography(HomographyTransform h)
        {
            return $"H = [[{h.H11:F6}, {h.H12:F6}, {h.H13:F6}], [{h.H21:F6}, {h.H22:F6}, {h.H23:F6}], [{h.H31:F6}, {h.H32:F6}, {h.H33:F6}]]";
        }

        private static string FormatPoly2D(Poly2DTransform p)
        {
            return $"X = {p.X_x:F6}*x + {p.X_y:F6}*y + {p.X_1:F6} + {p.X_x2:F6}*x^2 + {p.X_xy:F6}*x*y + {p.X_y2:F6}*y^2\n" +
                   $"Y = {p.Y_x:F6}*x + {p.Y_y:F6}*y + {p.Y_1:F6} + {p.Y_x2:F6}*x^2 + {p.Y_xy:F6}*x*y + {p.Y_y2:F6}*y^2";
        }
    }
}
