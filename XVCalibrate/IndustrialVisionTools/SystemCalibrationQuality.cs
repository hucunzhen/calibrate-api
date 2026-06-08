using System;
using System.Text;
using System.Text.Json;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>棋盘格内参 + 九点仿射的系统整体误差（世界坐标 mm）。</summary>
    internal static class SystemCalibrationQuality
    {
        private const double SystemAvgAcceptableMm = 2.00;
        private const double SystemMaxAcceptableMm = 3.00;

        public sealed class SystemQualityReport
        {
            public bool HasChessboard { get; init; }
            public bool HasNinePoint { get; init; }
            public bool ChessboardPassed { get; init; }
            public bool NinePointPassed { get; init; }
            public bool Passed { get; init; }
            public string VerdictLine { get; init; } = "";
            public string ChessboardGrade { get; init; } = "";
            public string NinePointGrade { get; init; } = "";
            public double ChessboardRmsPx { get; init; }
            public double NinePointAvgMm { get; init; }
            public double NinePointMaxMm { get; init; }
            public double MmPerPixel { get; init; }
            public double ChessboardContributionAvgMm { get; init; }
            public double SystemAvgMm { get; init; }
            public double SystemMaxMm { get; init; }
        }

        public static SystemQualityReport? TryAnalyze(
            string? calibrationJson,
            NinePointCalibrationQuality.QualityReport nineReport,
            AffineTransform transform)
        {
            if (nineReport == null)
                return null;

            double mmPerPx = EstimateMmPerPixel(transform);
            double chessRmsPx = double.NaN;
            bool hasChess = false;
            bool chessPassed = false;
            string chessGrade = "—";
            double chessContribMm = 0;

            if (!string.IsNullOrWhiteSpace(calibrationJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(calibrationJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("intrinsics", out var intr))
                    {
                        hasChess = true;
                        chessRmsPx = intr.TryGetProperty("rms", out var rv) ? rv.GetDouble() : double.NaN;
                        var chessReport = ChessboardCalibrationQuality.AnalyzeForSystem(root);
                        chessPassed = chessReport.Passed;
                        chessGrade = chessReport.Grade;
                        if (!double.IsNaN(chessRmsPx) && chessRmsPx > 0 && mmPerPx > 0)
                            chessContribMm = chessRmsPx * mmPerPx;
                    }
                }
                catch
                {
                    hasChess = false;
                }
            }

            double nineAvg = nineReport.AverageErrorMm;
            double nineMax = nineReport.MaxErrorMm;
            double systemAvg = hasChess && chessContribMm > 0
                ? Math.Sqrt(nineAvg * nineAvg + chessContribMm * chessContribMm)
                : nineAvg;
            double systemMax = hasChess && chessContribMm > 0
                ? nineMax + chessContribMm
                : nineMax;

            bool systemMetricsOk = systemAvg < SystemAvgAcceptableMm && systemMax < SystemMaxAcceptableMm;
            bool passed = nineReport.Passed && (!hasChess || chessPassed) && systemMetricsOk;

            string verdict = passed
                ? "✓ 系统整体：合格（棋盘格 + 九点误差在允许范围内）"
                : "✗ 系统整体：不合格（见分项指标）";

            if (!nineReport.Passed)
                verdict = "✗ 系统整体：不合格（九点标定未通过）";
            else if (hasChess && !chessPassed)
                verdict = "✗ 系统整体：不合格（棋盘格内参未通过，建议重标）";
            else if (!systemMetricsOk)
                verdict = $"✗ 系统整体：不合格（合成误差 avg={systemAvg:F3} mm / max≈{systemMax:F3} mm 超限）";

            return new SystemQualityReport
            {
                HasChessboard = hasChess,
                HasNinePoint = true,
                ChessboardPassed = !hasChess || chessPassed,
                NinePointPassed = nineReport.Passed,
                Passed = passed,
                VerdictLine = verdict,
                ChessboardGrade = chessGrade,
                NinePointGrade = nineReport.Grade,
                ChessboardRmsPx = chessRmsPx,
                NinePointAvgMm = nineAvg,
                NinePointMaxMm = nineMax,
                MmPerPixel = mmPerPx,
                ChessboardContributionAvgMm = chessContribMm,
                SystemAvgMm = systemAvg,
                SystemMaxMm = systemMax
            };
        }

        public static string BuildBriefSummary(SystemQualityReport report) =>
            report.Passed
                ? $"[系统合格] 合成 avg={report.SystemAvgMm:F3}mm max≈{report.SystemMaxMm:F3}mm"
                : $"[系统不合格] 合成 avg={report.SystemAvgMm:F3}mm max≈{report.SystemMaxMm:F3}mm";

        public static void AppendSystemSection(StringBuilder sb, SystemQualityReport report)
        {
            sb.AppendLine();
            sb.AppendLine("──────────────────────────────────────");
            sb.AppendLine("[系统整体误差 · 棋盘格 + 九点]");
            sb.AppendLine(report.VerdictLine);
            sb.AppendLine();

            if (report.HasChessboard && !double.IsNaN(report.ChessboardRmsPx))
            {
                sb.AppendLine($"  棋盘格内参：RMS={report.ChessboardRmsPx:G4} px（{report.ChessboardGrade}）");
                sb.AppendLine(
                    $"    → 折算到世界坐标约 {report.ChessboardContributionAvgMm:F3} mm" +
                    $"（按仿射尺度 {report.MmPerPixel:G4} mm/px）");
            }
            else
                sb.AppendLine("  棋盘格内参：未接入 CalibrationJson（仅显示九点误差）");

            sb.AppendLine(
                $"  九点标定：avg={report.NinePointAvgMm:F3} mm  max={report.NinePointMaxMm:F3} mm（{report.NinePointGrade}）");
            sb.AppendLine();
            sb.AppendLine("  系统合成（RSS 平均 + 保守最大）：");
            sb.AppendLine($"    平均误差 ≈ {report.SystemAvgMm:F3} mm");
            sb.AppendLine($"    最大误差范围 ≈ {report.SystemMaxMm:F3} mm");
            sb.AppendLine("  说明：棋盘格 px 误差经仿射尺度换算为 mm 后与九点 mm 误差合成；");
            sb.AppendLine("        最大值为九点最大误差 + 棋盘格折算（保守上界）。");
            sb.AppendLine("  参考：系统 avg <2.0 mm、max≈<3.0 mm 为产线可用；越小越好。");

            if (report.Passed)
                sb.AppendLine("  结论：可保存标定结果并在主流程使用 img_to_world。");
            else
                sb.AppendLine("  结论：请先修正不合格分项后重新标定。");
        }

        public sealed class SystemErrorDocument
        {
            public bool Passed { get; set; }
            public string? VerdictLine { get; set; }
            public string? SummaryLine { get; set; }
            public double ChessboardRmsPx { get; set; }
            public string? ChessboardGrade { get; set; }
            public double NinePointAvgMm { get; set; }
            public double NinePointMaxMm { get; set; }
            public string? NinePointGrade { get; set; }
            public double MmPerPixel { get; set; }
            public double ChessboardContributionAvgMm { get; set; }
            public double SystemAvgMm { get; set; }
            public double SystemMaxMm { get; set; }

            public static SystemErrorDocument From(SystemQualityReport r) =>
                new SystemErrorDocument
                {
                    Passed = r.Passed,
                    VerdictLine = r.VerdictLine,
                    SummaryLine = BuildBriefSummary(r),
                    ChessboardRmsPx = r.ChessboardRmsPx,
                    ChessboardGrade = r.ChessboardGrade,
                    NinePointAvgMm = r.NinePointAvgMm,
                    NinePointMaxMm = r.NinePointMaxMm,
                    NinePointGrade = r.NinePointGrade,
                    MmPerPixel = r.MmPerPixel,
                    ChessboardContributionAvgMm = r.ChessboardContributionAvgMm,
                    SystemAvgMm = r.SystemAvgMm,
                    SystemMaxMm = r.SystemMaxMm
                };
        }

        public static string SerializeDocument(SystemQualityReport report) =>
            JsonSerializer.Serialize(SystemErrorDocument.From(report), new JsonSerializerOptions { WriteIndented = true });

        public static SystemErrorDocument? TryParseDocument(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                return JsonSerializer.Deserialize<SystemErrorDocument>(json);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>仿射在图像坐标下的平均 mm/px（列/行尺度均值）。</summary>
        public static double EstimateMmPerPixel(AffineTransform t)
        {
            double sx = Math.Sqrt(t.A * t.A + t.D * t.D);
            double sy = Math.Sqrt(t.B * t.B + t.E * t.E);
            if (sx <= 0 && sy <= 0)
                return 0;
            if (sx <= 0)
                return sy;
            if (sy <= 0)
                return sx;
            return (sx + sy) / 2.0;
        }
    }
}
