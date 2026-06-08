using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>九点标定（仿射）质检：重投影误差、坏点与配对建议。</summary>
    internal static class NinePointCalibrationQuality
    {
        private const double AvgExcellentMm = 0.30;
        private const double AvgGoodMm = 1.00;
        private const double AvgAcceptableMm = 2.00;
        private const double MaxPointWarnMm = 1.50;
        private const double MaxPointFailMm = 3.00;
        private const int RecommendedPointCount = 9;
        private const double MaxAvgRatioFail = 4.0;

        public sealed class PointErrorRow
        {
            public required int Index { get; init; }
            public required Point2D ImagePt { get; init; }
            public required Point2D WorldPt { get; init; }
            public required Point2D ReprojWorld { get; init; }
            public required double ErrorMm { get; init; }
        }

        public sealed class QualityReport
        {
            public bool Passed { get; init; }
            public string VerdictLine { get; init; } = "";
            public string Grade { get; init; } = "";
            public double AverageErrorMm { get; init; }
            public double MaxErrorMm { get; init; }
            public IReadOnlyList<PointErrorRow> Points { get; init; } = Array.Empty<PointErrorRow>();
        }

        public static QualityReport Analyze(
            AffineTransform transform,
            Point2D[] imagePts,
            Point2D[] worldPts,
            double averageErrorMm,
            double maxErrorMm)
        {
            var rows = ComputePointErrors(transform, imagePts, worldPts);
            double avg = rows.Count > 0 ? rows.Average(r => r.ErrorMm) : averageErrorMm;
            double max = rows.Count > 0 ? rows.Max(r => r.ErrorMm) : maxErrorMm;
            if (averageErrorMm > 0)
                avg = averageErrorMm;
            if (maxErrorMm > 0)
                max = maxErrorMm;

            string grade = GradeFromAverage(avg);
            int badCount = rows.Count(r => r.ErrorMm >= MaxPointWarnMm);
            bool countOk = imagePts.Length >= RecommendedPointCount;
            bool avgOk = avg < AvgAcceptableMm;
            bool maxOk = max < MaxPointFailMm;
            bool ratioOk = avg <= 1e-9 || max / avg < MaxAvgRatioFail;
            bool passed = avgOk && maxOk && ratioOk && badCount == 0 && imagePts.Length >= 4;

            if (imagePts.Length >= 4 && avgOk && maxOk && badCount <= 1 && ratioOk)
                passed = true;

            string verdict = passed
                ? "✓ 九点标定：合格（像素→世界映射误差在允许范围内）"
                : "✗ 九点标定：不合格（请核对坏点并重标）";

            if (imagePts.Length < 4)
                verdict = "✗ 九点标定：不合格（点数不足 4，无法可靠标定）";
            else if (badCount > 0)
                verdict = $"✗ 九点标定：不合格（{badCount} 个点误差偏大，见下方清单）";
            else if (!avgOk)
                verdict = $"✗ 九点标定：不合格（平均误差 {avg:F3} mm 过大）";
            else if (!maxOk)
                verdict = $"✗ 九点标定：不合格（最大单点误差 {max:F3} mm 过大）";
            else if (!ratioOk)
                verdict = "✗ 九点标定：不合格（个别点误差显著偏大，疑似配对错误或检测偏了）";
            else if (!countOk)
                verdict = $"△ 九点标定：可用（当前 {imagePts.Length} 点，建议补至 {RecommendedPointCount} 点）";

            return new QualityReport
            {
                Passed = passed,
                VerdictLine = verdict,
                Grade = grade,
                AverageErrorMm = avg,
                MaxErrorMm = max,
                Points = rows
            };
        }

        public static string BuildBriefSummary(QualityReport report) =>
            report.Passed
                ? $"[合格·{report.Grade}] avg={report.AverageErrorMm:F3}mm max={report.MaxErrorMm:F3}mm"
                : $"[不合格·{report.Grade}] avg={report.AverageErrorMm:F3}mm max={report.MaxErrorMm:F3}mm";

        public static bool TryBuildPopupReport(
            QualityReport report,
            out string headline,
            out string body)
        {
            headline = report.VerdictLine;
            var sb = new StringBuilder();
            sb.AppendLine($"等级：{report.Grade}    平均误差：{report.AverageErrorMm:F3} mm    最大误差：{report.MaxErrorMm:F3} mm");
            sb.AppendLine($"标定点数：{report.Points.Count}（推荐 {RecommendedPointCount} 点，3×3 行优先顺序）");
            sb.AppendLine();
            sb.AppendLine("──────────────────────────────────────");
            sb.AppendLine("[1 · 标定结果好坏]");
            sb.AppendLine(report.VerdictLine);
            sb.AppendLine("误差参考（世界坐标 mm）：平均 <0.3 优秀 / <1.0 良好 / <2.0 可用；单点建议 <1.5 mm。");
            if (report.Passed)
                sb.AppendLine("结论：可保存 calibration_result.json 并在主流程中使用 img_to_world。");
            else
                sb.AppendLine("结论：请按 [2][3] 修正后重新运行九点标定。");

            AppendBadPoints(sb, report);
            AppendFixTips(sb, report);
            body = sb.ToString().TrimEnd();
            return true;
        }

        private static List<PointErrorRow> ComputePointErrors(
            AffineTransform transform,
            Point2D[] imagePts,
            Point2D[] worldPts)
        {
            int n = Math.Min(imagePts?.Length ?? 0, worldPts?.Length ?? 0);
            var rows = new List<PointErrorRow>(n);
            for (int i = 0; i < n; i++)
            {
                Point2D reproj = CalibAPI.ImageToWorld(imagePts[i], transform);
                double dx = reproj.X - worldPts[i].X;
                double dy = reproj.Y - worldPts[i].Y;
                double err = Math.Sqrt(dx * dx + dy * dy);
                rows.Add(new PointErrorRow
                {
                    Index = i + 1,
                    ImagePt = imagePts[i],
                    WorldPt = worldPts[i],
                    ReprojWorld = reproj,
                    ErrorMm = err
                });
            }

            return rows.OrderByDescending(r => r.ErrorMm).ToList();
        }

        private static void AppendBadPoints(StringBuilder sb, QualityReport report)
        {
            sb.AppendLine();
            sb.AppendLine("[2 · 误差偏大的点（建议重新选点/核对世界坐标）]");

            var bad = report.Points.Where(p => p.ErrorMm >= MaxPointWarnMm).ToList();
            if (bad.Count == 0)
            {
                sb.AppendLine("  · 各点重投影误差均在可接受范围内。");
                sb.AppendLine();
                sb.AppendLine("  各点明细（按误差从大到小）：");
            }
            else
            {
                sb.AppendLine($"  · 共 {bad.Count} 个点需重点检查：");
            }

            foreach (var p in report.Points)
            {
                string flag = p.ErrorMm >= MaxPointFailMm ? "必须修正"
                    : p.ErrorMm >= MaxPointWarnMm ? "建议修正" : "正常";
                sb.AppendLine(
                    $"    #{p.Index} [{flag}] 误差={p.ErrorMm:F3} mm");
                sb.AppendLine(
                    $"        像素 ({p.ImagePt.X:F1}, {p.ImagePt.Y:F1}) → 世界 ({p.WorldPt.X:F3}, {p.WorldPt.Y:F3})");
                sb.AppendLine(
                    $"        反算世界 ({p.ReprojWorld.X:F3}, {p.ReprojWorld.Y:F3})");
            }
        }

        private static void AppendFixTips(StringBuilder sb, QualityReport report)
        {
            sb.AppendLine();
            sb.AppendLine("[3 · 改进建议]");

            if (report.Points.Count > 0)
            {
                var worst = report.Points[0];
                if (worst.ErrorMm >= MaxPointWarnMm)
                {
                    sb.AppendLine($"  · 优先重选像素点 #{worst.Index}（误差最大 {worst.ErrorMm:F3} mm）。");
                    sb.AppendLine("    手选时在弹窗中重新点击该点；匹配检测模式则检查圆心/模板匹配是否偏了。");
                }
            }

            if (report.MaxErrorMm > 0 && report.AverageErrorMm > 0
                && report.MaxErrorMm / report.AverageErrorMm >= MaxAvgRatioFail)
            {
                sb.AppendLine("  · 最大误差远高于平均：检查像素点与世界点是否一一对应（顺序、行优先）。");
                sb.AppendLine("    建议开启「图像确认对应」，或与 caliSendContour 的 worldPoints 逐点核对。");
            }

            if (report.Points.Count < RecommendedPointCount)
                sb.AppendLine($"  · 当前仅 {report.Points.Count} 点，产线建议 {RecommendedPointCount} 点（3×3）覆盖工作区域。");

            sb.AppendLine("  · 世界坐标须与 PLC 九点焊接坐标一致（来自 caliSendContour / world_pos.txt）。");
            sb.AppendLine("  · 标定图须清晰、九点特征完整；检测链 findCircle 等排序须与 worldPoints 顺序一致。");

            if (report.Passed)
                sb.AppendLine("  · 当前结果可保存；主流程 img_to_world 将使用本次仿射矩阵。");
        }

        private static string GradeFromAverage(double avgMm)
        {
            if (avgMm < AvgExcellentMm) return "优秀";
            if (avgMm < AvgGoodMm) return "良好";
            if (avgMm < AvgAcceptableMm) return "可用";
            return "偏差较大";
        }
    }
}
