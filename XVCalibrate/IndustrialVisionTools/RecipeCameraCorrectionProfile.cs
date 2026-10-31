using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 配方全局「标定与矫正」配置（相对配方目录 <c>models/camera_correction.json</c>）。
    /// 与 <c>calibration_result.json</c> / <c>calibration_nine_point.json</c> 并列，供形状模板页与其它工具共用。
    /// </summary>
    internal sealed class RecipeCameraCorrectionProfile
    {
        public const string RelativePath = @"models\camera_correction.json";

        public bool EnableUndistort { get; set; } = true;

        public string UndistortAlpha { get; set; } = "-1";

        public bool EnablePerspective { get; set; } = true;

        public int ViewIndex { get; set; }

        public int BoardCols { get; set; } = ChessboardCalibrationDefaults.InnerCornerCols;

        public int BoardRows { get; set; } = ChessboardCalibrationDefaults.InnerCornerRows;

        public string SquareSizeMm { get; set; } =
            ChessboardCalibrationDefaults.SquareSizeMm.ToString(CultureInfo.InvariantCulture);

        public string PxPerMm { get; set; } =
            ChessboardCalibrationDefaults.PxPerMm.ToString(CultureInfo.InvariantCulture);

        public string PerspectiveOutputFrame { get; set; } = ChessboardCalibrationDefaults.PerspectiveOutputFrame;

        /// <summary>相对配方目录；空则按约定搜索标定文件。</summary>
        public string ChessboardCalibrationFile { get; set; } = @"models\calibration_result.json";

        /// <summary>相对配方目录；空则按约定搜索九点文件。</summary>
        public string NinePointCalibrationFile { get; set; } = @"models\calibration_nine_point.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string GetProfilePath(string recipeDirectory) =>
            Path.Combine(recipeDirectory, RelativePath);

        public static RecipeCameraCorrectionProfile CreateBuiltInDefaults() => new();

        public static RecipeCameraCorrectionProfile? TryLoadFromRecipeDirectory(string? recipeDirectory)
        {
            if (string.IsNullOrWhiteSpace(recipeDirectory) || !Directory.Exists(recipeDirectory))
                return null;

            string path = GetProfilePath(recipeDirectory);
            if (!File.Exists(path))
                return null;

            try
            {
                var profile = JsonSerializer.Deserialize<RecipeCameraCorrectionProfile>(
                                  File.ReadAllText(path), JsonOptions)
                              ?? CreateBuiltInDefaults();
                profile.Normalize();
                return profile;
            }
            catch
            {
                return null;
            }
        }

        public static RecipeCameraCorrectionProfile LoadOrCreateDefaults(string? recipeDirectory)
        {
            RecipeCameraCorrectionProfile? loaded = TryLoadFromRecipeDirectory(recipeDirectory);
            if (loaded != null)
                return loaded;

            var created = CreateBuiltInDefaults();
            if (!string.IsNullOrWhiteSpace(recipeDirectory) && Directory.Exists(recipeDirectory))
            {
                try
                {
                    created.SaveToRecipeDirectory(recipeDirectory);
                }
                catch
                {
                    // 只读配方目录时仍返回内存默认
                }
            }

            return created;
        }

        public void SaveToRecipeDirectory(string recipeDirectory)
        {
            Normalize();
            string path = GetProfilePath(recipeDirectory);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(UndistortAlpha))
                UndistortAlpha = "-1";
            if (BoardCols <= 0)
                BoardCols = ChessboardCalibrationDefaults.InnerCornerCols;
            if (BoardRows <= 0)
                BoardRows = ChessboardCalibrationDefaults.InnerCornerRows;
            if (string.IsNullOrWhiteSpace(SquareSizeMm))
                SquareSizeMm = ChessboardCalibrationDefaults.SquareSizeMm.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(PxPerMm))
                PxPerMm = ChessboardCalibrationDefaults.PxPerMm.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(PerspectiveOutputFrame))
                PerspectiveOutputFrame = ChessboardCalibrationDefaults.PerspectiveOutputFrame;
            if (string.IsNullOrWhiteSpace(ChessboardCalibrationFile))
                ChessboardCalibrationFile = @"models\calibration_result.json";
            if (string.IsNullOrWhiteSpace(NinePointCalibrationFile))
                NinePointCalibrationFile = @"models\calibration_nine_point.json";
        }

        /// <summary>仅解析本配置中的相对路径；不存在则返回 null（由调用方回退约定搜索）。</summary>
        public string? TryResolveChessboardCalibrationPath(string recipeDirectory) =>
            ResolveRelativeFile(recipeDirectory, ChessboardCalibrationFile, requireExists: true);

        /// <summary>仅解析本配置中的相对路径；不存在则返回 null（由调用方回退约定搜索）。</summary>
        public string? TryResolveNinePointCalibrationPath(string recipeDirectory) =>
            ResolveRelativeFile(recipeDirectory, NinePointCalibrationFile, requireExists: true);

        /// <summary>配置中的棋盘标定绝对路径（文件可不存在，供保存）。</summary>
        public string GetConfiguredChessboardCalibrationPath(string recipeDirectory) =>
            ResolveRelativeFile(recipeDirectory, ChessboardCalibrationFile, requireExists: false)
            ?? Path.GetFullPath(Path.Combine(recipeDirectory, "models", "calibration_result.json"));

        /// <summary>配置中的九点标定绝对路径（文件可不存在，供保存）。</summary>
        public string GetConfiguredNinePointCalibrationPath(string recipeDirectory) =>
            ResolveRelativeFile(recipeDirectory, NinePointCalibrationFile, requireExists: false)
            ?? Path.GetFullPath(Path.Combine(recipeDirectory, "models", "calibration_nine_point.json"));

        private static string? ResolveRelativeFile(string recipeDirectory, string? relative, bool requireExists)
        {
            if (string.IsNullOrWhiteSpace(relative))
                return null;
            string path = Path.GetFullPath(Path.Combine(
                recipeDirectory,
                relative.Trim().Replace('/', Path.DirectorySeparatorChar)));
            if (requireExists && !File.Exists(path))
                return null;
            return path;
        }
    }
}
