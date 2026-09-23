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
    /// 针对「第 N 条轨迹」上下边偏移不对称做量化。
    /// dotnet run --project XVCalibrate/FlowSmokeTests -- --offset-bar18
    /// </summary>
    internal static class OffsetBar18DiagSmokeTest
    {
        public static int Run(string? repoRoot = null)
        {
            repoRoot ??= FlowCalibrationSmokeTest.FindRepoRoot();
            if (repoRoot == null) return 1;

            string v5 = Path.Combine(repoRoot, "flows", "v5");
            string shm = Path.Combine(v5, "models", "contour_scaled_Poly_n0_am180x360_ilp_mc10_s90-110.shm");
            if (!File.Exists(shm))
            {
                Console.Error.WriteLine($"FAIL: {shm}");
                return 1;
            }

            // main.flow.grid-filter 日志里 Find #18（1-based 第18条 ≈ 0-based #17）
            // #17 Score=0.925 Row=924 Col=1072
            // #18 Score=0.922 Row=515 Col=1063
            var poses = new (string Tag, double Row, double Col, double Angle, double Scale)[]
            {
                ("Find#17(0-based16)", 924, 1072, -179.2, 1.0),
                ("Find#18(0-based17)", 515, 1063, -179.2, 1.0),
                ("Find#0 mid", 1079, 1588, -179.2, 1.0),
            };

            long modelId = HalconFlowBridge.LoadShapeModelFromFile(shm);
            Point2D[][] templates = HalconFlowBridge.GetShapeModelContourPoints(modelId, 1);
            var sb = new StringBuilder();
            sb.AppendLine($"shm={Path.GetFileName(shm)} templates={templates.Length}");
            sb.AppendLine("offset: 2D PolylineUniformOffset d=-3 closed (与当前点列默认路径一致)");
            sb.AppendLine();

            foreach (var pose in poses)
            {
                Point2D[] first = templates.Length > 0
                    ? TransformScaled(templates[0], pose.Row, pose.Col, pose.Angle, pose.Scale)
                    : Array.Empty<Point2D>();
                AnalyzeOne(sb, pose.Tag, first, d: -3);
            }

            // 额外：若存在两层轮廓，对比外扩后相对「照片感」——相对内圈的上下间距
            if (templates.Length >= 2)
            {
                var pose = poses[1];
                Point2D[] outer = TransformScaled(templates[0], pose.Row, pose.Col, pose.Angle, pose.Scale);
                Point2D[] inner = TransformScaled(templates[1], pose.Row, pose.Col, pose.Angle, pose.Scale);
                var (sampled, _) = Sample(outer);
                Point2D[] off = PolylineUniformOffset.Offset(sampled, -3, closed: true);
                double outerTop = outer.Min(p => p.Y), outerBot = outer.Max(p => p.Y);
                double innerTop = inner.Min(p => p.Y), innerBot = inner.Max(p => p.Y);
                double offTop = off.Min(p => p.Y), offBot = off.Max(p => p.Y);
                sb.AppendLine($"=== 相对内外圈 (pose {pose.Tag}) — 解释「和实物比上下不一」===");
                sb.AppendLine($"  outer Y=[{outerTop:G6},{outerBot:G6}]");
                sb.AppendLine($"  inner Y=[{innerTop:G6},{innerBot:G6}]");
                sb.AppendLine($"  offset Y=[{offTop:G6},{offBot:G6}]");
                sb.AppendLine($"  外→内 上间隙={innerTop - outerTop:G5} 下间隙={outerBot - innerBot:G5}");
                sb.AppendLine($"  外→偏 上移={offTop - outerTop:G5} 下移={outerBot - offBot:G5}");
                sb.AppendLine($"  偏→内 上剩余={innerTop - offTop:G5} 下剩余={offBot - innerBot:G5}");
                sb.AppendLine("  若上剩余≈0、下剩余仍大：看起来「上边缩到贴内边、下边还空着」");
            }

            string path = Path.Combine(v5, "_offset_bar18_diag.txt");
            File.WriteAllText(path, sb.ToString());
            Console.WriteLine(sb.ToString());
            Console.WriteLine($"WROTE {path}");
            return 0;
        }

        static Point2D[] TransformScaled(Point2D[] model, double row, double col, double angleDeg, double scale)
        {
            // 与 BuildScaledShapeModelContourAtPose 一致
            double z = Math.Abs(scale) > 1e-9 ? scale : 1.0;
            double a = angleDeg * Math.PI / 180.0;
            double c = Math.Cos(a), s = Math.Sin(a);
            var dst = new Point2D[model.Length];
            for (int i = 0; i < model.Length; i++)
            {
                double r = model[i].Y * z;
                double k = model[i].X * z;
                double rowImg = r * c - k * s + row;
                double colImg = r * s + k * c + col;
                dst[i] = new Point2D(colImg, rowImg);
            }
            return dst;
        }

        static (Point2D[] Pts, int[] Bars) Sample(Point2D[] contour)
        {
            var xld = new HalconXldContourBundle
            {
                Width = 2592,
                Height = 1944,
                Contours = new List<Point2D[]> { contour }
            };
            return HalconFlowBridge.SamplePointsFromXldBundle(xld, 2, 0, false);
        }

        static void AnalyzeOne(StringBuilder sb, string tag, Point2D[] contour, double d)
        {
            if (contour.Length < 3)
            {
                sb.AppendLine($"=== {tag}: empty ===");
                return;
            }

            var (sampled, _) = Sample(contour);
            Point2D[] off = PolylineUniformOffset.Offset(sampled, d, closed: true);

            double srcMinY = sampled.Min(p => p.Y), srcMaxY = sampled.Max(p => p.Y);
            double srcMinX = sampled.Min(p => p.X), srcMaxX = sampled.Max(p => p.X);
            double dstMinY = off.Min(p => p.Y), dstMaxY = off.Max(p => p.Y);
            double h = Math.Max(1, srcMaxY - srcMinY);
            double band = Math.Max(2, 0.12 * h);

            // 顶/底带：源边上中点到偏移折线距离；以及偏移带点到源折线距离
            double topSrcToOff = MeanEdgeMidToPoly(sampled, off, srcMinY, srcMinY + band, horizontal: true);
            double botSrcToOff = MeanEdgeMidToPoly(sampled, off, srcMaxY - band, srcMaxY, horizontal: true);
            double topOffToSrc = MeanBandDist(off, sampled, dstMinY - 1, dstMinY + band);
            double botOffToSrc = MeanBandDist(off, sampled, dstMaxY - band, dstMaxY + 1);

            // 顶/底带内点的平均 ΔY（偏移点相对最近源点）
            double topDy = MeanNearestDy(off, sampled, dstMinY - 1, dstMinY + band);
            double botDy = MeanNearestDy(off, sampled, dstMaxY - band, dstMaxY + 1);

            sb.AppendLine($"=== {tag} ===");
            sb.AppendLine($"  n={contour.Length} sampled={sampled.Length} off={off.Length}");
            sb.AppendLine($"  bbox Y=[{srcMinY:G6},{srcMaxY:G6}] h={h:G5} → [{dstMinY:G6},{dstMaxY:G6}]");
            sb.AppendLine($"  topΔY(bbox)={dstMinY - srcMinY:G5}  botΔY(bbox)={dstMaxY - srcMaxY:G5}");
            sb.AppendLine($"  leftΔX={off.Min(p => p.X) - srcMinX:G5} rightΔX={off.Max(p => p.X) - srcMaxX:G5}");
            sb.AppendLine($"  源边中点→偏折线: top={topSrcToOff:G5} bot={botSrcToOff:G5}");
            sb.AppendLine($"  偏带→源折线: top={topOffToSrc:G5} bot={botOffToSrc:G5}");
            sb.AppendLine($"  偏点最近源 ΔY均值: top={topDy:G5} bot={botDy:G5}");
            sb.AppendLine();
        }

        static double MeanEdgeMidToPoly(Point2D[] src, Point2D[] dst, double y0, double y1, bool horizontal)
        {
            double sum = 0;
            int n = 0;
            int nSeg = src.Length;
            for (int i = 0; i < nSeg; i++)
            {
                var a = src[i];
                var b = src[(i + 1) % src.Length];
                var mid = new Point2D((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
                if (mid.Y < y0 || mid.Y > y1) continue;
                sum += DistToPoly(mid, dst);
                n++;
            }
            return n > 0 ? sum / n : double.NaN;
        }

        static double MeanBandDist(Point2D[] pts, Point2D[] poly, double y0, double y1)
        {
            double sum = 0;
            int n = 0;
            foreach (var p in pts)
            {
                if (p.Y < y0 || p.Y > y1) continue;
                sum += DistToPoly(p, poly);
                n++;
            }
            return n > 0 ? sum / n : double.NaN;
        }

        static double MeanNearestDy(Point2D[] pts, Point2D[] poly, double y0, double y1)
        {
            double sum = 0;
            int n = 0;
            foreach (var p in pts)
            {
                if (p.Y < y0 || p.Y > y1) continue;
                int best = 0;
                double bestD = double.PositiveInfinity;
                for (int i = 0; i < poly.Length; i++)
                {
                    double dx = p.X - poly[i].X, dy = p.Y - poly[i].Y;
                    double d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; best = i; }
                }
                sum += p.Y - poly[best].Y;
                n++;
            }
            return n > 0 ? sum / n : double.NaN;
        }

        static double DistToPoly(Point2D p, Point2D[] poly)
        {
            double best = double.PositiveInfinity;
            for (int i = 0; i < poly.Length; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Length];
                best = Math.Min(best, SegDist(p, a, b));
            }
            return best;
        }

        static double SegDist(Point2D p, Point2D a, Point2D b)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double wx = p.X - a.X, wy = p.Y - a.Y;
            double c1 = vx * wx + vy * wy;
            if (c1 <= 0) return Math.Sqrt(wx * wx + wy * wy);
            double c2 = vx * vx + vy * vy;
            if (c2 < 1e-12) return Math.Sqrt(wx * wx + wy * wy);
            double t = Math.Min(1, c1 / c2);
            double dx = p.X - (a.X + t * vx), dy = p.Y - (a.Y + t * vy);
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
