using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 当前流程配方的全局标定/矫正默认值（来自 <c>models/camera_correction.json</c> + 约定标定文件）。
    /// </summary>
    internal sealed class RecipeCameraCorrectionDefaults
    {
        public string? ChessboardCalibrationJsonPath { get; init; }
        public string? NinePointCalibrationJsonPath { get; init; }
        public bool ProfileFound { get; init; }
        public bool EnableUndistort { get; init; }
        public bool EnablePerspective { get; init; }
        public string UndistortAlpha { get; init; } = "-1";
        public int ViewIndex { get; init; }
        public int BoardCols { get; init; } = ChessboardCalibrationDefaults.InnerCornerCols;
        public int BoardRows { get; init; } = ChessboardCalibrationDefaults.InnerCornerRows;
        public string SquareSizeMm { get; init; } =
            ChessboardCalibrationDefaults.SquareSizeMm.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public string PxPerMm { get; init; } =
            ChessboardCalibrationDefaults.PxPerMm.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public string PerspectiveOutputFrame { get; init; } = ChessboardCalibrationDefaults.PerspectiveOutputFrame;
    }

    public sealed class FlowRecipeInfo
    {
        public required string Name { get; init; }
        public required string DirectoryPath { get; init; }
        public required string MainFlowPath { get; init; }
    }

    /// <summary>
    /// 配方 = <c>flows/</c> 下的一个子目录（如 v1、v2）；流程内相对路径均相对该目录解析。
    /// </summary>
    internal static class FlowRecipeCatalog
    {
        public const string DefaultMainFlowFileName = "main.flow.json";

        public const string NinePointCalibFlowFileName = "caliNinePoint.flow.json";

        public const string WeldFlowFileName = "calibSendContour.flow.json";

        /// <summary>部分配方目录中的历史拼写。</summary>
        public const string WeldFlowFileNameAlt = "caliSendContour.flow.json";

        public const string ChessboardIntrinsicsFlowFileName = "chessboard_intrinsics_from_dir.flow.json";

        public const string AffineAdjustFlowFileName = "adjustAffineCalibration.flow.json";

        /// <summary>形状模板页测试用：无 PLC / 无外层循环 / 文件读图。</summary>
        public const string ShapeTemplateTestFlowFileName = "shape_template_test.flow.json";

        /// <summary>配方全局粗匹配形状模板（相对配方目录）。</summary>
        public const string GlobalCoarseShapeModelRelativePath = @"models\shape_model_coarse.shm";

        /// <summary>配方全局外轮廓粗匹配模板（相对配方目录）。</summary>
        public const string GlobalOutlineShapeModelRelativePath = @"models\shape_model_outline.shm";

        /// <summary>配方全局精匹配缩放形状模板（相对配方目录）。</summary>
        public const string GlobalFineShapeModelRelativePath = @"models\shape_model_fine.shm";

        /// <summary>配方全局精匹配可变形模板（相对配方目录；对应参数配方 builtin:fine）。</summary>
        public const string GlobalFineDeformableModelRelativePath = @"models\shape_model_fine.dfm";

        /// <summary>形状模板页测试优先写入的工作模板（相对配方目录）。</summary>
        public const string WorkingShapeModelRelativePath = @"models\shape_model_working.shm";

        /// <summary>形状模板页测试优先写入的可变形工作模板（相对配方目录）。</summary>
        public const string WorkingDeformableModelRelativePath = @"models\shape_model_working.dfm";

        public const string BuiltinOutlineParamRecipeId = "builtin:outline";
        public const string BuiltinCoarseParamRecipeId = "builtin:coarse";
        public const string BuiltinFineParamRecipeId = "builtin:fine";

        public static string? TryFindFlowsRootDirectory()
        {
            string? configured = TryGetConfiguredFlowsRootDirectory();
            if (configured != null)
                return configured;

            return TryAutoDiscoverFlowsRootDirectory();
        }

        public static string? TryGetConfiguredFlowsRootDirectory()
        {
            var settings = FlowRecipeUiSettings.Load();
            if (string.IsNullOrWhiteSpace(settings.FlowsRootDirectory))
                return null;

            string full = Path.GetFullPath(settings.FlowsRootDirectory.Trim());
            return Directory.Exists(full) ? full : null;
        }

        public static string? TryAutoDiscoverFlowsRootDirectory()
        {
            string? repo = FlowCalibrationSmokeTest.FindRepoRoot();
            if (repo != null)
            {
                string flows = Path.Combine(repo, "flows");
                if (Directory.Exists(flows))
                    return Path.GetFullPath(flows);
            }

            string[] fallbacks =
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "flows")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "flows")),
            };
            foreach (string p in fallbacks)
            {
                if (Directory.Exists(p))
                    return p;
            }

            return null;
        }

        public static bool TrySetFlowsRootDirectory(string? directory, out string error)
        {
            error = "";
            var settings = FlowRecipeUiSettings.Load();

            if (string.IsNullOrWhiteSpace(directory))
            {
                settings.FlowsRootDirectory = "";
                settings.Save();
                FormalFlowOperatorCatalog.InvalidateCache();
                return true;
            }

            string full = Path.GetFullPath(directory.Trim());
            if (!Directory.Exists(full))
            {
                error = "目录不存在。";
                return false;
            }

            settings.FlowsRootDirectory = full;
            settings.Save();
            FormalFlowOperatorCatalog.InvalidateCache();
            return true;
        }

        public static IReadOnlyList<FlowRecipeInfo> ListRecipes()
        {
            string? root = TryFindFlowsRootDirectory();
            if (root == null)
                return Array.Empty<FlowRecipeInfo>();

            var list = new List<FlowRecipeInfo>();
            foreach (string dir in Directory.GetDirectories(root).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
                    continue;

                string? mainPath = TryResolveMainFlowPath(dir);
                if (mainPath == null)
                    continue;

                list.Add(new FlowRecipeInfo
                {
                    Name = name,
                    DirectoryPath = Path.GetFullPath(dir),
                    MainFlowPath = mainPath
                });
            }

            return list;
        }

        public static string? TryResolveMainFlowPath(string recipeDirectory)
        {
            if (string.IsNullOrWhiteSpace(recipeDirectory) || !Directory.Exists(recipeDirectory))
                return null;

            string main = Path.Combine(recipeDirectory, DefaultMainFlowFileName);
            if (File.Exists(main))
                return Path.GetFullPath(main);

            string? first = Directory
                .GetFiles(recipeDirectory, "*.flow.json", SearchOption.TopDirectoryOnly)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return first == null ? null : Path.GetFullPath(first);
        }

        public static string? TryGetRecipeDirectory(string recipeName)
        {
            if (string.IsNullOrWhiteSpace(recipeName))
                return null;

            string? root = TryFindFlowsRootDirectory();
            if (root == null)
                return null;

            string dir = Path.Combine(root, recipeName.Trim());
            if (!Directory.Exists(dir))
                return null;

            return Path.GetFullPath(dir);
        }

        public static string? TryGetMainFlowPath(string recipeName)
        {
            string? dir = TryGetRecipeDirectory(recipeName);
            return dir == null ? null : TryResolveMainFlowPath(dir);
        }

        public static string? TryGetNinePointCalibFlowPath(string recipeName)
        {
            string? dir = TryGetRecipeDirectory(recipeName);
            return dir == null ? null : TryResolveNinePointCalibFlowPath(dir);
        }

        public static string? TryResolveNinePointCalibFlowPath(string recipeDirectory)
        {
            if (string.IsNullOrWhiteSpace(recipeDirectory) || !Directory.Exists(recipeDirectory))
                return null;

            string path = Path.Combine(recipeDirectory, NinePointCalibFlowFileName);
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }

        public static string? TryGetSelectedNinePointCalibFlowPath()
        {
            var settings = FlowRecipeUiSettings.Load();
            return TryGetNinePointCalibFlowPath(settings.SelectedRecipe);
        }

        /// <summary>当前配方全局棋盘标定结果：<c>models/calibration_result.json</c>。</summary>
        public static string? TryGetSelectedChessboardCalibrationResultPath()
        {
            string? dir = TryGetSelectedRecipeDirectory();
            return dir == null ? null : TryResolveChessboardCalibrationResultPath(dir);
        }

        /// <summary>当前配方配置的棋盘标定保存路径（文件可不存在）。</summary>
        public static string? TryGetSelectedConfiguredChessboardCalibrationPath()
        {
            string? dir = TryGetSelectedRecipeDirectory();
            if (dir == null)
                return null;
            RecipeCameraCorrectionProfile profile =
                RecipeCameraCorrectionProfile.LoadOrCreateDefaults(dir);
            return profile.GetConfiguredChessboardCalibrationPath(dir);
        }

        /// <summary>当前配方配置的九点标定保存路径（文件可不存在）。</summary>
        public static string? TryGetSelectedConfiguredNinePointCalibrationPath()
        {
            string? dir = TryGetSelectedRecipeDirectory();
            if (dir == null)
                return null;
            RecipeCameraCorrectionProfile profile =
                RecipeCameraCorrectionProfile.LoadOrCreateDefaults(dir);
            return profile.GetConfiguredNinePointCalibrationPath(dir);
        }

        public static string? TryResolveChessboardCalibrationResultPath(string recipeDirectory)
        {
            if (string.IsNullOrWhiteSpace(recipeDirectory) || !Directory.Exists(recipeDirectory))
                return null;

            RecipeCameraCorrectionProfile? profile =
                RecipeCameraCorrectionProfile.TryLoadFromRecipeDirectory(recipeDirectory);
            string? fromProfile = profile?.TryResolveChessboardCalibrationPath(recipeDirectory);
            if (fromProfile != null)
                return fromProfile;

            foreach (string relative in new[]
                     {
                         Path.Combine("models", "calibration_result.json"),
                         Path.Combine("chessboard", "calibration_result.json"),
                         Path.Combine("chessboard", "calibration_full.json")
                     })
            {
                string path = Path.Combine(recipeDirectory, relative);
                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }

            return null;
        }

        /// <summary>当前配方全局九点标定结果：<c>models/calibration_nine_point.json</c>。</summary>
        public static string? TryGetSelectedNinePointCalibrationResultPath()
        {
            string? dir = TryGetSelectedRecipeDirectory();
            return dir == null ? null : TryResolveNinePointCalibrationResultPath(dir);
        }

        public static string? TryResolveNinePointCalibrationResultPath(string recipeDirectory)
        {
            if (string.IsNullOrWhiteSpace(recipeDirectory) || !Directory.Exists(recipeDirectory))
                return null;

            RecipeCameraCorrectionProfile? profile =
                RecipeCameraCorrectionProfile.TryLoadFromRecipeDirectory(recipeDirectory);
            string? fromProfile = profile?.TryResolveNinePointCalibrationPath(recipeDirectory);
            if (fromProfile != null)
                return fromProfile;

            string path = Path.Combine(recipeDirectory, "models", "calibration_nine_point.json");
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }

        /// <summary>
        /// 读取当前配方全局标定/矫正默认值（<c>models/camera_correction.json</c>；无文件则用内置默认 + 约定路径）。
        /// </summary>
        public static RecipeCameraCorrectionDefaults TryGetSelectedRecipeCameraCorrectionDefaults()
        {
            string? recipeDir = TryGetSelectedRecipeDirectory();
            RecipeCameraCorrectionProfile? profile = recipeDir == null
                ? null
                : RecipeCameraCorrectionProfile.TryLoadFromRecipeDirectory(recipeDir);
            RecipeCameraCorrectionProfile effective = profile ?? RecipeCameraCorrectionProfile.CreateBuiltInDefaults();

            string? chess = recipeDir == null ? null : TryResolveChessboardCalibrationResultPath(recipeDir);
            string? nine = recipeDir == null ? null : TryResolveNinePointCalibrationResultPath(recipeDir);

            return new RecipeCameraCorrectionDefaults
            {
                ChessboardCalibrationJsonPath = chess,
                NinePointCalibrationJsonPath = nine,
                ProfileFound = profile != null,
                EnableUndistort = effective.EnableUndistort,
                EnablePerspective = effective.EnablePerspective,
                UndistortAlpha = effective.UndistortAlpha,
                ViewIndex = effective.ViewIndex,
                BoardCols = effective.BoardCols,
                BoardRows = effective.BoardRows,
                SquareSizeMm = effective.SquareSizeMm,
                PxPerMm = effective.PxPerMm,
                PerspectiveOutputFrame = effective.PerspectiveOutputFrame
            };
        }

        public static string? TryGetWeldFlowPath(string recipeName)
        {
            string? dir = TryGetRecipeDirectory(recipeName);
            return dir == null ? null : TryResolveWeldFlowPath(dir);
        }

        public static string? TryResolveWeldFlowPath(string recipeDirectory)
        {
            if (string.IsNullOrWhiteSpace(recipeDirectory) || !Directory.Exists(recipeDirectory))
                return null;

            foreach (string fileName in new[] { WeldFlowFileName, WeldFlowFileNameAlt })
            {
                string path = Path.Combine(recipeDirectory, fileName);
                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }

            return null;
        }

        public static string? TryGetChessboardIntrinsicsFlowPath(string recipeName)
        {
            string? dir = TryGetRecipeDirectory(recipeName);
            return dir == null ? null : TryResolveChessboardIntrinsicsFlowPath(dir);
        }

        public static string? TryResolveChessboardIntrinsicsFlowPath(string recipeDirectory)
        {
            if (string.IsNullOrWhiteSpace(recipeDirectory) || !Directory.Exists(recipeDirectory))
                return null;

            string path = Path.Combine(recipeDirectory, ChessboardIntrinsicsFlowFileName);
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }

        public static string? TryGetAffineAdjustFlowPath(string recipeName)
        {
            string? dir = TryGetRecipeDirectory(recipeName);
            return dir == null ? null : TryResolveAffineAdjustFlowPath(dir);
        }

        public static string? TryResolveAffineAdjustFlowPath(string recipeDirectory)
        {
            if (string.IsNullOrWhiteSpace(recipeDirectory) || !Directory.Exists(recipeDirectory))
                return null;

            string path = Path.Combine(recipeDirectory, AffineAdjustFlowFileName);
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }

        public static string? TryGetShapeTemplateTestFlowPath(string recipeName)
        {
            string? dir = TryGetRecipeDirectory(recipeName);
            return dir == null ? null : TryResolveShapeTemplateTestFlowPath(dir);
        }

        public static string? TryResolveShapeTemplateTestFlowPath(string recipeDirectory)
        {
            if (string.IsNullOrWhiteSpace(recipeDirectory) || !Directory.Exists(recipeDirectory))
                return null;

            string path = Path.Combine(recipeDirectory, ShapeTemplateTestFlowFileName);
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }

        public static string? TryGetSelectedShapeTemplateTestFlowPath()
        {
            var settings = FlowRecipeUiSettings.Load();
            return TryGetShapeTemplateTestFlowPath(settings.SelectedRecipe);
        }

        public static string? TryGetSelectedModelsDirectory()
        {
            string? dir = TryGetSelectedRecipeDirectory();
            if (dir == null)
                return null;

            string models = Path.Combine(dir, "models");
            Directory.CreateDirectory(models);
            return Path.GetFullPath(models);
        }

        public static string? TryGetSelectedGlobalCoarseShapeModelPath() =>
            TryResolveUnderSelectedRecipe(GlobalCoarseShapeModelRelativePath);

        public static string? TryGetSelectedGlobalOutlineShapeModelPath() =>
            TryResolveUnderSelectedRecipe(GlobalOutlineShapeModelRelativePath);

        public static string? TryGetSelectedGlobalFineShapeModelPath() =>
            TryResolveUnderSelectedRecipe(GlobalFineShapeModelRelativePath);

        public static string? TryGetSelectedGlobalFineDeformableModelPath() =>
            TryResolveUnderSelectedRecipe(GlobalFineDeformableModelRelativePath);

        public static string? TryGetSelectedWorkingShapeModelPath() =>
            TryResolveUnderSelectedRecipe(WorkingShapeModelRelativePath);

        public static string? TryGetSelectedWorkingDeformableModelPath() =>
            TryResolveUnderSelectedRecipe(WorkingDeformableModelRelativePath);

        private static string? TryResolveUnderSelectedRecipe(string relativePath)
        {
            string? dir = TryGetSelectedRecipeDirectory();
            if (dir == null || string.IsNullOrWhiteSpace(relativePath))
                return null;
            return Path.GetFullPath(Path.Combine(dir, relativePath));
        }

        /// <summary>
        /// 若全局约定 .shm 尚不存在，则从历史文件名复制一份，便于旧配方平滑切换。
        /// </summary>
        public static void EnsureGlobalShapeModelDefaults(string? recipeDirectory = null)
        {
            string? dir = recipeDirectory ?? TryGetSelectedRecipeDirectory();
            if (dir == null || !Directory.Exists(dir))
                return;

            string models = Path.Combine(dir, "models");
            Directory.CreateDirectory(models);

            SeedModelIfMissing(
                Path.Combine(models, "shape_model_coarse.shm"),
                Path.Combine(models, "shape_model_fixed_manual.shm"),
                Path.Combine(models, "shape_model_fixed.shm"));
            SeedModelIfMissing(
                Path.Combine(models, "shape_model_outline.shm"),
                Path.Combine(models, "shape_mode_outlinel_manual.shm"),
                Path.Combine(models, "shape_mode_outlinel.shm"));
            SeedModelIfMissing(
                Path.Combine(models, "shape_model_fine.shm"),
                Path.Combine(models, "contour_scaled_Poly_n0_am180x360_ilp_mc10_s90-110.shm"));
        }

        private static void SeedModelIfMissing(string target, params string[] legacySources)
        {
            if (File.Exists(target))
                return;
            foreach (string src in legacySources)
            {
                if (!File.Exists(src))
                    continue;
                File.Copy(src, target, overwrite: false);
                return;
            }
        }

        public static string? TryGetSelectedRecipeDirectory()
        {
            var settings = FlowRecipeUiSettings.Load();
            return TryGetRecipeDirectory(settings.SelectedRecipe);
        }

        public static string? TryGetSelectedMainFlowPath() =>
            TryGetMainFlowPath(FlowRecipeUiSettings.Load().SelectedRecipe);

        public static string? TryDetectRecipeNameFromFlowPath(string? flowFilePath)
        {
            if (string.IsNullOrWhiteSpace(flowFilePath))
                return null;

            string? flowsRoot = TryFindFlowsRootDirectory();
            if (flowsRoot == null)
                return null;

            string full = Path.GetFullPath(flowFilePath.Trim());
            string rootFull = Path.GetFullPath(flowsRoot);
            if (!IsUnderDirectory(full, rootFull))
                return null;

            string relative = Path.GetRelativePath(rootFull, full);
            string first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (string.IsNullOrWhiteSpace(first) || first is "." or "..")
                return null;

            return TryGetRecipeDirectory(first) != null ? first : null;
        }

        public static string ResolveSelectedRecipeName(IReadOnlyList<FlowRecipeInfo> recipes)
        {
            if (recipes.Count == 0)
                return "";

            var settings = FlowRecipeUiSettings.Load();
            string want = settings.SelectedRecipe?.Trim() ?? "";
            if (!string.IsNullOrEmpty(want)
                && recipes.Any(r => string.Equals(r.Name, want, StringComparison.OrdinalIgnoreCase)))
                return recipes.First(r => string.Equals(r.Name, want, StringComparison.OrdinalIgnoreCase)).Name;

            if (recipes.Any(r => string.Equals(r.Name, "v1", StringComparison.OrdinalIgnoreCase)))
                return recipes.First(r => string.Equals(r.Name, "v1", StringComparison.OrdinalIgnoreCase)).Name;

            return recipes[0].Name;
        }

        public static bool RecipeNameExists(string recipeName)
        {
            if (string.IsNullOrWhiteSpace(recipeName))
                return false;

            return ListRecipes().Any(r => string.Equals(r.Name, recipeName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static bool TryValidateRecipeName(string? recipeName, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(recipeName))
            {
                error = "名称不能为空。";
                return false;
            }

            string name = recipeName.Trim();
            if (name is "." or "..")
            {
                error = "名称无效。";
                return false;
            }

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = "名称包含非法字符。";
                return false;
            }

            if (name.Contains('/') || name.Contains('\\'))
            {
                error = "名称不能包含路径分隔符。";
                return false;
            }

            return true;
        }

        public static string SuggestCopyRecipeName(string sourceRecipeName)
        {
            string baseName = $"{sourceRecipeName.Trim()}_copy";
            var existing = ListRecipes()
                .Select(r => r.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!existing.Contains(baseName))
                return baseName;

            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{sourceRecipeName.Trim()}_copy{i}";
                if (!existing.Contains(candidate))
                    return candidate;
            }

            return baseName + "_" + Guid.NewGuid().ToString("N")[..6];
        }

        public static FlowRecipeOperationResult TryCopyRecipe(string sourceRecipeName, string targetRecipeName)
        {
            if (!TryValidateRecipeName(targetRecipeName, out string nameError))
                return FlowRecipeOperationResult.Fail(nameError);

            string source = sourceRecipeName.Trim();
            string target = targetRecipeName.Trim();

            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                return FlowRecipeOperationResult.Fail("源配方与目标配方名称相同。");

            string? sourceDir = TryGetRecipeDirectory(source);
            if (sourceDir == null)
                return FlowRecipeOperationResult.Fail($"未找到源配方「{source}」。");

            string? flowsRoot = TryFindFlowsRootDirectory();
            if (flowsRoot == null)
                return FlowRecipeOperationResult.Fail("未找到配方根目录，请在配方栏点击「目录…」选择。");

            string destDir = Path.GetFullPath(Path.Combine(flowsRoot, target));
            if (Directory.Exists(destDir) || File.Exists(destDir))
                return FlowRecipeOperationResult.Fail($"配方「{target}」已存在。");

            try
            {
                CopyDirectoryRecursive(sourceDir, destDir);
                return FlowRecipeOperationResult.Ok(target);
            }
            catch (Exception ex)
            {
                TryDeleteDirectoryIfEmpty(destDir);
                return FlowRecipeOperationResult.Fail(ex.Message);
            }
        }

        public static FlowRecipeOperationResult TryDeleteRecipe(string recipeName)
        {
            if (!TryValidateRecipeName(recipeName, out string nameError))
                return FlowRecipeOperationResult.Fail(nameError);

            string name = recipeName.Trim();
            string? recipeDir = TryGetRecipeDirectory(name);
            if (recipeDir == null)
                return FlowRecipeOperationResult.Fail($"未找到配方「{name}」。");

            try
            {
                Directory.Delete(recipeDir, recursive: true);
                return FlowRecipeOperationResult.Ok(name);
            }
            catch (Exception ex)
            {
                return FlowRecipeOperationResult.Fail(ex.Message);
            }
        }

        public static FlowRecipeOperationResult TryRenameRecipe(string oldRecipeName, string newRecipeName)
        {
            if (!TryValidateRecipeName(newRecipeName, out string nameError))
                return FlowRecipeOperationResult.Fail(nameError);

            string oldName = oldRecipeName.Trim();
            string newName = newRecipeName.Trim();

            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                return FlowRecipeOperationResult.Ok(newName);

            string? oldDir = TryGetRecipeDirectory(oldName);
            if (oldDir == null)
                return FlowRecipeOperationResult.Fail($"未找到配方「{oldName}」。");

            string? flowsRoot = TryFindFlowsRootDirectory();
            if (flowsRoot == null)
                return FlowRecipeOperationResult.Fail("未找到配方根目录，请在配方栏点击「目录…」选择。");

            string newDir = Path.GetFullPath(Path.Combine(flowsRoot, newName));
            if (Directory.Exists(newDir) || File.Exists(newDir))
                return FlowRecipeOperationResult.Fail($"配方「{newName}」已存在。");

            try
            {
                Directory.Move(oldDir, newDir);
                return FlowRecipeOperationResult.Ok(newName);
            }
            catch (Exception ex)
            {
                return FlowRecipeOperationResult.Fail(ex.Message);
            }
        }

        public static string? RemapPathUnderRenamedRecipe(string? filePath, string oldRecipeDirectory, string newRecipeDirectory)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            if (!IsPathUnderRecipeDirectory(filePath, oldRecipeDirectory))
                return Path.GetFullPath(filePath.Trim());

            string oldPrefix = Path.GetFullPath(oldRecipeDirectory);
            if (!oldPrefix.EndsWith(Path.DirectorySeparatorChar))
                oldPrefix += Path.DirectorySeparatorChar;

            string full = Path.GetFullPath(filePath.Trim());
            string relative = full[oldPrefix.Length..];
            return Path.GetFullPath(Path.Combine(newRecipeDirectory, relative));
        }

        public static bool IsPathUnderRecipeDirectory(string? fileOrDirPath, string recipeDirectory)
        {
            if (string.IsNullOrWhiteSpace(fileOrDirPath))
                return false;

            return IsUnderDirectory(Path.GetFullPath(fileOrDirPath.Trim()), recipeDirectory);
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: false);
            }

            foreach (string subDir in Directory.GetDirectories(sourceDir))
            {
                string destSub = Path.Combine(destDir, Path.GetFileName(subDir));
                CopyDirectoryRecursive(subDir, destSub);
            }
        }

        private static void TryDeleteDirectoryIfEmpty(string directory)
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch
            {
                // 非关键
            }
        }

        private static bool IsUnderDirectory(string fileOrDir, string directory)
        {
            string dir = Path.GetFullPath(directory);
            if (!dir.EndsWith(Path.DirectorySeparatorChar))
                dir += Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(fileOrDir);
            return path.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
    }
}
