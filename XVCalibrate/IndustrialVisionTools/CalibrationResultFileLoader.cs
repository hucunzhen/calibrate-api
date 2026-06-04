using System;
using System.IO;
using System.Text;
using System.Text.Json;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>读取流程「保存标定结果」输出的 calibration_result.json（schemaVersion + affine）。</summary>
    internal static class CalibrationResultFileLoader
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private sealed class CalibrationResultFileV1
        {
            public int SchemaVersion { get; set; }
            public AffineCalibrationSaveV1? Affine { get; set; }
        }

        private sealed class AffineCalibrationSaveV1
        {
            public double A { get; set; }
            public double B { get; set; }
            public double C { get; set; }
            public double D { get; set; }
            public double E { get; set; }
            public double F { get; set; }

            public AffineTransform ToAffine() =>
                new AffineTransform { A = A, B = B, C = C, D = D, E = E, F = F };
        }

        public static bool TryLoadAffine(string path, out AffineTransform transform, out string error)
        {
            transform = default;
            error = "";
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "标定结果路径为空";
                return false;
            }

            try
            {
                path = Path.GetFullPath(path.Trim());
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            if (!File.Exists(path))
            {
                error = $"文件不存在: {path}";
                return false;
            }

            try
            {
                string raw = File.ReadAllText(path, Encoding.UTF8);
                if (raw.Length > 0 && raw[0] == '\uFEFF')
                    raw = raw[1..];

                if (raw.Contains("\"extrinsicsPerView\"", StringComparison.Ordinal)
                    && !raw.Contains("\"schemaVersion\"", StringComparison.Ordinal))
                {
                    error = "该文件为棋盘内参 JSON，请使用九点标定保存的 calibration_result.json";
                    return false;
                }

                var dto = JsonSerializer.Deserialize<CalibrationResultFileV1>(raw, JsonOptions);
                if (dto == null || dto.SchemaVersion < 1)
                {
                    error = "无效的 schemaVersion（需要 >= 1）";
                    return false;
                }

                if (dto.Affine == null)
                {
                    error = "JSON 不含 affine（请先运行九点标定并保存结果）";
                    return false;
                }

                transform = dto.Affine.ToAffine();
                return true;
            }
            catch (JsonException ex)
            {
                error = $"JSON 解析失败: {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static string? TryGuessDefaultPath()
        {
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "flows", "halcon", "calibration_result.json")),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "flows", "v1", "models", "calibration_result.json")),
            };

            foreach (string p in candidates)
            {
                if (File.Exists(p))
                    return p;
            }

            return null;
        }
    }
}
