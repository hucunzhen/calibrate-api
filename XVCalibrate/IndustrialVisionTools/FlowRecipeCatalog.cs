using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CalibOperatorCLI_Example
{
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

        public const string NinePointCalibFlowFileName = "calibSendContour.flow.json";

        /// <summary>部分配方目录中的历史拼写。</summary>
        public const string NinePointCalibFlowFileNameAlt = "caliSendContour.flow.json";

        public const string ChessboardIntrinsicsFlowFileName = "chessboard_intrinsics_from_dir.flow.json";

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

            foreach (string fileName in new[] { NinePointCalibFlowFileName, NinePointCalibFlowFileNameAlt })
            {
                string path = Path.Combine(recipeDirectory, fileName);
                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }

            return null;
        }

        public static string? TryGetSelectedNinePointCalibFlowPath()
        {
            var settings = FlowRecipeUiSettings.Load();
            return TryGetNinePointCalibFlowPath(settings.SelectedRecipe);
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
