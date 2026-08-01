using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CalibOperatorCLI_Example
{
    /// <summary>棋盘格标定结果质检：总体评定、补拍位置、需替换不合格图像。</summary>
    internal static class ChessboardCalibrationQuality
    {
        private const double RmsExcellent = 0.10;
        private const double RmsGood = 0.30;
        private const double RmsAcceptable = 0.50;
        private const double ViewRmsReplace = 0.35;
        private const double ViewRmsWarn = 0.25;
        private const double FrontalTiltMaxDeg = 12.0;
        private const double ObliqueTiltMinDeg = 18.0;
        private const int RecommendedMinViews = 15;
        private const int GridDiv = 3;

        private static readonly string[] GridLabels =
        {
            "画面左上", "画面上方中央", "画面右上",
            "画面左侧中央", "画面中央", "画面右侧中央",
            "画面左下", "画面下方中央", "画面右下"
        };

        public sealed class QualityReport
        {
            public bool Passed { get; init; }
            public string VerdictLine { get; init; } = "";
            public string Grade { get; init; } = "";
            public int ReplaceImageCount { get; init; }
        }

        public static string BuildBriefSummary(string calibrationJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(calibrationJson);
                var report = Analyze(doc.RootElement);
                double rms = TryGetDouble(doc.RootElement.GetProperty("intrinsics"), "rms");
                GetCounts(doc.RootElement, out _, out int success, out int failed);
                string flag = report.Passed ? "合格" : "不合格";
                return $"[{flag}·{report.Grade}] RMS={rms:G4}px · 成功{success}张" +
                       (failed > 0 ? $" · 需换{report.ReplaceImageCount}张" : report.ReplaceImageCount > 0 ? $" · 需换{report.ReplaceImageCount}张" : "");
            }
            catch
            {
                return "标定完成";
            }
        }

        /// <summary>供系统误差合成使用的轻量分析（不构建完整弹窗）。</summary>
        public static QualityReport AnalyzeForSystem(JsonElement root) => Analyze(root);

        /// <summary>构建弹窗报告正文；非棋盘格 JSON 返回 false。</summary>
        public static bool TryBuildPopupReport(string calibrationJson, out string headline, out string body)
        {
            headline = "";
            body = "";
            try
            {
                using var doc = JsonDocument.Parse(calibrationJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("intrinsics", out var intr))
                    return false;

                var report = Analyze(root);
                headline = report.VerdictLine;
                GetCounts(root, out int attempted, out int success, out int failed);
                double rms = TryGetDouble(intr, "rms");

                var sb = new StringBuilder();
                sb.AppendLine($"等级：{report.Grade}    全局 RMS：{rms:G4} px");
                sb.AppendLine($"统计：共 {attempted} 张 · 成功 {success} 张 · 角点失败 {failed} 张");
                AppendQualitySection(sb, root);
                body = sb.ToString().TrimEnd();
                headline = report.VerdictLine;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void AppendQualitySection(StringBuilder sb, JsonElement root)
        {
            if (!root.TryGetProperty("intrinsics", out var intr))
                return;

            var report = Analyze(root);
            double rms = TryGetDouble(intr, "rms");
            GetCounts(root, out int attempted, out int success, out int failed);

            sb.AppendLine();
            sb.AppendLine("──────────────────────────────────────");
            sb.AppendLine("[1 · 标定结果评定]");
            sb.AppendLine(report.VerdictLine);
            sb.AppendLine($"等级：{report.Grade} | 全局 RMS={rms:G4}px（<0.10 优秀 / <0.30 良好 / <0.50 可用）");
            sb.AppendLine($"统计：共 {attempted} 张，成功 {success} 张，角点失败 {failed} 张。");
            if (report.Passed)
                sb.AppendLine("结论：当前标定结果可用于保存并进入九点标定。");
            else
                sb.AppendLine("结论：请按下方 [2][3] 补拍并替换不合格图像后，重新运行本流程。");

            AppendRetakePositions(sb, root, success);
            AppendReplaceImages(sb, root);
            AppendGeneralTips(sb, rms, success, failed, attempted, report.Passed);
        }

        private static QualityReport Analyze(JsonElement root)
        {
            double rms = root.TryGetProperty("intrinsics", out var intr) ? TryGetDouble(intr, "rms") : double.NaN;
            GetCounts(root, out _, out int success, out int failed);
            int replaceCount = CountReplaceImages(root);
            bool coverageOk = IsCoverageAdequate(root, success, out _);
            bool rmsOk = !double.IsNaN(rms) && rms < RmsAcceptable;
            bool countOk = success >= RecommendedMinViews;
            bool minViewsOk = success >= 3;
            string grade = GradeFromRms(rms);

            bool passed = minViewsOk && rmsOk && replaceCount == 0 && coverageOk && countOk;
            if (minViewsOk && rmsOk && replaceCount == 0 && coverageOk && success >= 10)
                passed = true;

            string verdict = passed
                ? "✓ 标定结果：合格（指标与覆盖度满足要求）"
                : "✗ 标定结果：不合格（需补拍 / 替换不合格图像 / 或整体重标）";

            if (!minViewsOk)
                verdict = "✗ 标定结果：不合格（成功张数不足 3，无法标定）";
            else if (replaceCount > 0)
                verdict = $"✗ 标定结果：不合格（有 {replaceCount} 张图必须替换，见下方清单）";
            else if (!rmsOk)
                verdict = $"✗ 标定结果：不合格（RMS={rms:G4}px 偏大，需补拍并剔除不合格图像）";
            else if (!coverageOk)
                verdict = "✗ 标定结果：不合格（画面覆盖或姿态不足，见补拍位置）";
            else if (!countOk)
                verdict = $"✗ 标定结果：临界可用（建议补拍至 {RecommendedMinViews} 张后再保存）";

            return new QualityReport
            {
                Passed = passed,
                VerdictLine = verdict,
                Grade = grade,
                ReplaceImageCount = replaceCount
            };
        }

        private static void AppendRetakePositions(StringBuilder sb, JsonElement root, int successCount)
        {
            sb.AppendLine();
            sb.AppendLine("[2 · 建议补拍位置]");
            if (successCount < 3)
            {
                sb.AppendLine("  · 当前无法评估覆盖度；请先保证至少 3 张角点成功的图像。");
                return;
            }

            if (!TryGetImageSize(root, out int imgW, out int imgH))
            {
                sb.AppendLine("  · 缺少 imageSize，无法分析画面覆盖（请重新编译 CalibOperator 后重跑标定）。");
                AppendRetakeByHeuristics(sb, successCount);
                return;
            }

            var covered = new bool[GridDiv * GridDiv];
            double minTilt = double.MaxValue;
            double maxTilt = 0;
            int tiltCount = 0;

            if (root.TryGetProperty("extrinsicsPerView", out var views) && views.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in views.EnumerateArray())
                {
                    if (el.TryGetProperty("boardCenterPx", out var bc) && bc.ValueKind == JsonValueKind.Array
                        && bc.GetArrayLength() >= 2)
                    {
                        double px = bc[0].GetDouble();
                        double py = bc[1].GetDouble();
                        int col = Math.Clamp((int)(px / imgW * GridDiv), 0, GridDiv - 1);
                        int row = Math.Clamp((int)(py / imgH * GridDiv), 0, GridDiv - 1);
                        covered[row * GridDiv + col] = true;
                    }

                    if (el.TryGetProperty("tiltDeg", out var td))
                    {
                        double tilt = td.GetDouble();
                        minTilt = Math.Min(minTilt, tilt);
                        maxTilt = Math.Max(maxTilt, tilt);
                        tiltCount++;
                    }
                }
            }

            var missing = new List<string>();
            for (int i = 0; i < GridLabels.Length; i++)
            {
                if (!covered[i])
                    missing.Add(GridLabels[i]);
            }

            if (missing.Count == 0)
                sb.AppendLine("  · 画面九宫格覆盖：已覆盖全部区域。");
            else
            {
                sb.AppendLine("  · 画面九宫格覆盖不足，建议在以下位置各补拍 1~2 张（棋盘完整、清晰、带一定倾角）：");
                foreach (string m in missing)
                    sb.AppendLine($"      - {m}");
            }

            if (tiltCount > 0)
            {
                if (minTilt > FrontalTiltMaxDeg)
                    sb.AppendLine($"  · 补拍较正视图：当前最正的一张倾角约 {minTilt:F1}°（建议 ≤{FrontalTiltMaxDeg:F0}°，供透视 viewIndex）。");
                if (maxTilt < ObliqueTiltMinDeg)
                    sb.AppendLine($"  · 补拍大倾角图：当前最大倾角约 {maxTilt:F1}°（建议 ≥{ObliqueTiltMinDeg:F0}°，改善边缘畸变标定）。");
            }

            if (successCount < RecommendedMinViews)
                sb.AppendLine($"  · 补拍数量：当前仅 {successCount} 张成功，建议再补 {RecommendedMinViews - successCount}~{Math.Max(0, 25 - successCount)} 张不同姿态。");

            sb.AppendLine("  · 补拍通用要求：棋盘四边完整入画、对焦清晰、无强反光/过曝、与 cols/rows/squareSizeMm 一致。");
        }

        private static void AppendRetakeByHeuristics(StringBuilder sb, int successCount)
        {
            if (successCount < RecommendedMinViews)
                sb.AppendLine($"  · 建议补拍至 {RecommendedMinViews}~25 张，并覆盖画面四角与中心。");
            sb.AppendLine("  · 须含 1 张较正视图（供透视 viewIndex）及若干大倾角图。");
        }

        private static void AppendReplaceImages(StringBuilder sb, JsonElement root)
        {
            sb.AppendLine();
            sb.AppendLine("[3 · 须替换的不合格图像清单]");

            var entries = CollectReplaceEntries(root);
            if (entries.Count == 0)
            {
                sb.AppendLine("  · 无必须替换的图像（角点失败与高 reprojRms 均未超标）。");
                return;
            }

            sb.AppendLine($"  · 共 {entries.Count} 张，请从标定目录删除或覆盖后重新标定：");
            int n = 0;
            foreach (var e in entries)
            {
                n++;
                sb.AppendLine($"    {n}. [{e.Reason}] {Path.GetFileName(e.Path)}");
                sb.AppendLine($"       {e.Path}");
                if (!string.IsNullOrEmpty(e.Detail))
                    sb.AppendLine($"       {e.Detail}");
            }
        }

        private static void AppendGeneralTips(StringBuilder sb, double rms, int success, int failed, int attempted, bool passed)
        {
            if (passed)
                return;

            sb.AppendLine();
            sb.AppendLine("[操作步骤]");
            sb.AppendLine("  1. 按 [3] 删除/替换不合格图像");
            sb.AppendLine("  2. 按 [2] 补拍缺失区域");
            sb.AppendLine("  3. 重新运行本流程，直至 [1] 显示合格");
            if (failed > 0 && attempted > 0 && (double)failed / attempted > 0.2)
                sb.AppendLine("  · 失败比例高：检查曝光、前缀 Image_、扩展名是否与标定节点一致。");
            if (rms > RmsGood)
                sb.AppendLine("  · RMS 偏高：优先剔除 [3] 中重投影误差大的图，再补拍。");
        }

        private sealed class ReplaceEntry
        {
            public required string Path { get; init; }
            public required string Reason { get; init; }
            public string Detail { get; init; } = "";
        }

        private static List<ReplaceEntry> CollectReplaceEntries(JsonElement root)
        {
            var list = new List<ReplaceEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("failedImagePaths", out var failedArr) && failedArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in failedArr.EnumerateArray())
                {
                    string? p = el.GetString();
                    if (string.IsNullOrWhiteSpace(p) || !seen.Add(p))
                        continue;
                    list.Add(new ReplaceEntry
                    {
                        Path = p,
                        Reason = "角点失败",
                        Detail = "读图失败或未检出完整棋盘角点，无法参与标定"
                    });
                }
            }

            if (root.TryGetProperty("extrinsicsPerView", out var views) && views.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var el in views.EnumerateArray())
                {
                    idx++;
                    if (!el.TryGetProperty("reprojRms", out var rv))
                        continue;
                    double viewRms = rv.GetDouble();
                    if (viewRms < ViewRmsReplace)
                        continue;

                    string path = el.TryGetProperty("imagePath", out var pp) ? pp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    if (!seen.Add(path))
                        continue;

                    list.Add(new ReplaceEntry
                    {
                        Path = path,
                        Reason = "重投影误差过大",
                        Detail = $"reprojRms={viewRms:G4}px（阈值 {ViewRmsReplace:G2}px），建议重拍同姿态或剔除"
                    });
                }
            }

            return list;
        }

        private static int CountReplaceImages(JsonElement root) => CollectReplaceEntries(root).Count;

        private static bool IsCoverageAdequate(JsonElement root, int successCount, out int missingCells)
        {
            missingCells = 0;
            if (successCount < 3)
                return false;
            if (!TryGetImageSize(root, out int imgW, out int imgH))
                return successCount >= 10;

            var covered = new bool[GridDiv * GridDiv];
            if (!root.TryGetProperty("extrinsicsPerView", out var views) || views.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var el in views.EnumerateArray())
            {
                if (!el.TryGetProperty("boardCenterPx", out var bc) || bc.ValueKind != JsonValueKind.Array
                    || bc.GetArrayLength() < 2)
                    continue;

                double px = bc[0].GetDouble();
                double py = bc[1].GetDouble();
                int col = Math.Clamp((int)(px / imgW * GridDiv), 0, GridDiv - 1);
                int row = Math.Clamp((int)(py / imgH * GridDiv), 0, GridDiv - 1);
                covered[row * GridDiv + col] = true;
            }

            for (int i = 0; i < covered.Length; i++)
            {
                if (!covered[i])
                    missingCells++;
            }

            double minTilt = double.MaxValue;
            foreach (var el in views.EnumerateArray())
            {
                if (el.TryGetProperty("tiltDeg", out var td))
                    minTilt = Math.Min(minTilt, td.GetDouble());
            }

            bool gridOk = missingCells <= 3;
            bool frontalOk = double.IsNaN(minTilt) || minTilt <= FrontalTiltMaxDeg + 5;
            return gridOk && frontalOk;
        }

        private static bool TryGetImageSize(JsonElement root, out int width, out int height)
        {
            width = height = 0;
            if (!root.TryGetProperty("imageSize", out var sz))
                return false;
            if (!sz.TryGetProperty("width", out var w) || !sz.TryGetProperty("height", out var h))
                return false;
            width = w.GetInt32();
            height = h.GetInt32();
            return width > 0 && height > 0;
        }

        private static string GradeFromRms(double rms)
        {
            if (double.IsNaN(rms)) return "未知";
            if (rms < RmsExcellent) return "优秀";
            if (rms < RmsGood) return "良好";
            if (rms < RmsAcceptable) return "可用";
            return "偏差较大";
        }

        private static void GetCounts(JsonElement root, out int attempted, out int success, out int failed)
        {
            attempted = success = failed = 0;
            if (root.TryGetProperty("calibrationStats", out var stats))
            {
                attempted = stats.TryGetProperty("attemptedCount", out var a) ? a.GetInt32() : 0;
                success = stats.TryGetProperty("successfulCount", out var s) ? s.GetInt32() : 0;
                failed = stats.TryGetProperty("failedCount", out var f) ? f.GetInt32() : 0;
                return;
            }

            if (root.TryGetProperty("extrinsicsPerView", out var views) && views.ValueKind == JsonValueKind.Array)
                success = views.GetArrayLength();
            if (root.TryGetProperty("failedImagePaths", out var failedArr) && failedArr.ValueKind == JsonValueKind.Array)
                failed = failedArr.GetArrayLength();
            attempted = success + failed;
        }

        private static double TryGetDouble(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) ? v.GetDouble() : double.NaN;
    }
}
