using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CalibOperatorCLI_Example;
using CalibOperatorPInvoke;

namespace FlowSmokeTests
{
    /// <summary>
    /// 对比精匹配 first 轮廓 vs 面积最大外轮廓的上下边偏移。
    /// dotnet run --project XVCalibrate/FlowSmokeTests -- --offset-diag
    /// </summary>
    internal static class PolylineOffsetDiagSmokeTest
    {
        public static int Run(string? repoRoot = null)
        {
            repoRoot ??= FlowCalibrationSmokeTest.FindRepoRoot();
            if (repoRoot == null)
            {
                Console.Error.WriteLine("FAIL: 未找到仓库根");
                return 1;
            }

            string v5 = Path.Combine(repoRoot, "flows", "v5");
            string shm = Path.Combine(v5, "models", "contour_scaled_Poly_n0_am180x360_ilp_mc10_s90-110.shm");
            if (!File.Exists(shm))
            {
                Console.Error.WriteLine($"FAIL: 缺模型 {shm}");
                return 1;
            }

            double matchRow = 1079.0;
            double matchCol = 1588.0;
            double matchAngle = -179.2;
            double offsetDist = 3.0;
            double simplifyEps = 0.1;
            double sampleSpacing = 2.0;

            long modelId = HalconFlowBridge.LoadShapeModelFromFile(shm);
            Point2D[][] templates = HalconFlowBridge.GetShapeModelContourPoints(modelId, 1);
            Point2D[] outer = HalconFlowBridge.GetOuterTransformedShapeContour(
                templates, matchRow, matchCol, matchAngle);
            Point2D[] first = templates.Length > 0 && templates[0] != null && templates[0].Length >= 2
                ? HalconFlowBridge.TransformShapeModelContourToImage(templates[0], matchRow, matchCol, matchAngle)
                : Array.Empty<Point2D>();

            var sb = new StringBuilder();
            sb.AppendLine($"shm={Path.GetFileName(shm)}");
            sb.AppendLine($"pose row={matchRow} col={matchCol} angle={matchAngle}");
            sb.AppendLine($"modelContours={templates.Length} firstPts={first.Length} outerPts={outer.Length}");
            for (int ci = 0; ci < templates.Length; ci++)
            {
                var c = templates[ci];
                if (c == null || c.Length < 2) continue;
                var img = HalconFlowBridge.TransformShapeModelContourToImage(c, matchRow, matchCol, matchAngle);
                double minY = img.Min(p => p.Y), maxY = img.Max(p => p.Y);
                double minX = img.Min(p => p.X), maxX = img.Max(p => p.X);
                double dx = img[0].X - img[^1].X, dy = img[0].Y - img[^1].Y;
                bool closedish = dx * dx + dy * dy < 1.0;
                sb.AppendLine(
                    $"  contour[{ci}] n={img.Length} bbox X=[{minX:G5},{maxX:G5}] Y=[{minY:G5},{maxY:G5}] h={maxY - minY:G5} closedish={closedish}");
            }

            AnalyzeChain(sb, "FIRST(contours[0])=精匹配实际", first, sampleSpacing, simplifyEps, offsetDist);
            AnalyzeChain(sb, "OUTER(面积最大)=对照", outer, sampleSpacing, simplifyEps, offsetDist);

            string outPath = Path.Combine(v5, "_offset_diag_smoke.txt");
            File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine(sb.ToString());
            Console.WriteLine($"WROTE {outPath}");
            return 0;
        }

        static void AnalyzeChain(
            StringBuilder sb,
            string name,
            Point2D[] contour,
            double sampleSpacing,
            double simplifyEps,
            double offsetDist)
        {
            if (contour == null || contour.Length < 3)
            {
                sb.AppendLine($"=== {name}: skip ===");
                return;
            }

            var xld = new HalconXldContourBundle
            {
                Width = 2592,
                Height = 1944,
                Contours = new List<Point2D[]> { contour }
            };
            var (sampled, _) = HalconFlowBridge.SamplePointsFromXldBundle(
                xld, sampleSpacing, 0, sortContoursByLengthDescending: false);
            Point2D[] simplified = SimplifyClosedLikeFlow(sampled, simplifyEps);
            Point2D[] offPos = HalconFlowBridge.OffsetPointPolylineHalcon(
                simplified, offsetDist, "regression_normal", closed: true);
            Point2D[] offNeg = HalconFlowBridge.OffsetPointPolylineHalcon(
                simplified, -Math.Abs(offsetDist), "regression_normal", closed: true);
            Point2D[] off2dNeg = PolylineUniformOffset.Offset(sampled, -Math.Abs(offsetDist), closed: true);

            sb.AppendLine($"=== {name} ===");
            sb.AppendLine($"  contour={contour.Length} sampled={sampled.Length} simplified={simplified.Length}");
            AppendTopBottomStats(sb, $"{name} HALCON d=+{offsetDist}", simplified, offPos);
            AppendTopBottomStats(sb, $"{name} HALCON d=-{Math.Abs(offsetDist)}", simplified, offNeg);
            AppendTopBottomStats(sb, $"{name} 2D d=-{Math.Abs(offsetDist)} dense", sampled, off2dNeg);
        }

        static void AppendTopBottomStats(StringBuilder sb, string title, Point2D[] src, Point2D[] dst)
        {
            if (src == null || src.Length < 3 || dst == null || dst.Length < 2)
            {
                sb.AppendLine($"--- TB {title}: skip ---");
                return;
            }

            double srcMinY = src.Min(p => p.Y), srcMaxY = src.Max(p => p.Y);
            double srcMinX = src.Min(p => p.X), srcMaxX = src.Max(p => p.X);
            double dstMinY = dst.Min(p => p.Y), dstMaxY = dst.Max(p => p.Y);
            double dstMinX = dst.Min(p => p.X), dstMaxX = dst.Max(p => p.X);
            double band = Math.Max(2.0, 0.15 * Math.Max(1.0, srcMaxY - srcMinY));
            double bandX = Math.Max(2.0, 0.15 * Math.Max(1.0, srcMaxX - srcMinX));

            double topDist = MeanDistBand(dst, src, srcMinY - 1, srcMinY + band);
            double botDist = MeanDistBand(dst, src, srcMaxY - band, srcMaxY + 1);
            double leftDist = MeanDistBandX(dst, src, srcMinX - 1, srcMinX + bandX);
            double rightDist = MeanDistBandX(dst, src, srcMaxX - bandX, srcMaxX + 1);

            sb.AppendLine($"--- TB {title} ---");
            sb.AppendLine($"  bbox src Y=[{srcMinY:G6},{srcMaxY:G6}] → dst Y=[{dstMinY:G6},{dstMaxY:G6}]");
            sb.AppendLine($"  topΔY={dstMinY - srcMinY:G5} botΔY={dstMaxY - srcMaxY:G5} leftΔX={dstMinX - srcMinX:G5} rightΔX={dstMaxX - srcMaxX:G5}");
            sb.AppendLine($"  band dist→src: top={topDist:G5} bot={botDist:G5} left={leftDist:G5} right={rightDist:G5}");
        }

        static double MeanDistBand(Point2D[] dst, Point2D[] src, double yMin, double yMax)
        {
            double sum = 0;
            int n = 0;
            foreach (var p in dst)
            {
                if (p.Y < yMin || p.Y > yMax) continue;
                sum += DistToPoly(p, src, closed: true);
                n++;
            }
            return n > 0 ? sum / n : double.NaN;
        }

        static double MeanDistBandX(Point2D[] dst, Point2D[] src, double xMin, double xMax)
        {
            double sum = 0;
            int n = 0;
            foreach (var p in dst)
            {
                if (p.X < xMin || p.X > xMax) continue;
                sum += DistToPoly(p, src, closed: true);
                n++;
            }
            return n > 0 ? sum / n : double.NaN;
        }

        static double DistToPoly(Point2D p, Point2D[] poly, bool closed)
        {
            if (poly.Length < 2) return double.PositiveInfinity;
            double best = double.PositiveInfinity;
            int n = closed && poly.Length >= 3 ? poly.Length : poly.Length - 1;
            for (int i = 0; i < n; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Length];
                best = Math.Min(best, PointSegDist(p, a, b));
            }
            return best;
        }

        static double PointSegDist(Point2D p, Point2D a, Point2D b)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double wx = p.X - a.X, wy = p.Y - a.Y;
            double c1 = vx * wx + vy * wy;
            if (c1 <= 0) return Math.Sqrt(wx * wx + wy * wy);
            double c2 = vx * vx + vy * vy;
            if (c2 <= 1e-12) return Math.Sqrt(wx * wx + wy * wy);
            double t = Math.Min(1.0, c1 / c2);
            double dx = p.X - (a.X + t * vx);
            double dy = p.Y - (a.Y + t * vy);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        static Point2D[] SimplifyClosedLikeFlow(Point2D[] pts, double epsilon)
        {
            if (pts == null || pts.Length < 4 || epsilon <= 0) return pts ?? Array.Empty<Point2D>();
            var open = pts.ToList();
            open.Add(pts[0]);
            var simplified = SimplifyOpen(open, epsilon);
            if (simplified.Count > 1)
                simplified.RemoveAt(simplified.Count - 1);
            if (simplified.Count >= 2)
            {
                double dx = simplified[0].X - simplified[^1].X;
                double dy = simplified[0].Y - simplified[^1].Y;
                if (dx * dx + dy * dy > 1e-12)
                    simplified.Add(simplified[0]);
            }
            return simplified.ToArray();
        }

        static List<Point2D> SimplifyOpen(List<Point2D> points, double epsilon)
        {
            if (points.Count <= 2) return new List<Point2D>(points);
            int index = -1;
            double maxDist = -1;
            var start = points[0];
            var end = points[^1];
            for (int i = 1; i < points.Count - 1; i++)
            {
                double dist = PointSegDist(points[i], start, end);
                if (dist > maxDist) { maxDist = dist; index = i; }
            }
            if (maxDist <= epsilon || index <= 0)
                return new List<Point2D> { start, end };
            var left = SimplifyOpen(points.GetRange(0, index + 1), epsilon);
            var right = SimplifyOpen(points.GetRange(index, points.Count - index), epsilon);
            left.RemoveAt(left.Count - 1);
            left.AddRange(right);
            return left;
        }
    }
}
