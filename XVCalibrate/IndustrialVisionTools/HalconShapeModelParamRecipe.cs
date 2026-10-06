using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CalibOperatorCLI_Example
{
    /// <summary>形状模板「一类任务一套参数」：只含创建/匹配控件，不含图像与 ROI。</summary>
    internal sealed class HalconShapeModelParamRecipe
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsBuiltIn { get; set; }
        public bool HasOverride { get; set; }
        public Dictionary<string, string> Controls { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed class ShapeParamRecipeListItem
    {
        public required HalconShapeModelParamRecipe Recipe { get; init; }

        public override string ToString() =>
            Recipe.IsBuiltIn ? $"【内置】{Recipe.Name}" : Recipe.Name;
    }

    internal static class HalconShapeModelParamRecipeStore
    {
        public const string BuiltinPrefix = "builtin:";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static string RecipesDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppProduct.AppDataFolderName,
            "shape_model_recipes");

        public static IReadOnlyList<HalconShapeModelParamRecipe> ListAll()
        {
            var list = new List<HalconShapeModelParamRecipe>();
            foreach (HalconShapeModelParamRecipe builtin in BuiltInRecipes)
                list.Add(LoadShippedBuiltin(builtin));

            try
            {
                Directory.CreateDirectory(RecipesDirectory);
                foreach (string path in Directory.EnumerateFiles(RecipesDirectory, "*.json"))
                {
                    string stem = Path.GetFileNameWithoutExtension(path);
                    if (stem.StartsWith("builtin_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    HalconShapeModelParamRecipe? loaded = TryLoadFile(path);
                    if (loaded == null || string.IsNullOrWhiteSpace(loaded.Name))
                        continue;
                    loaded.IsBuiltIn = false;
                    loaded.HasOverride = false;
                    if (string.IsNullOrWhiteSpace(loaded.Id))
                        loaded.Id = stem;
                    list.Add(loaded);
                }
            }
            catch
            {
            }

            return list;
        }

        public static HalconShapeModelParamRecipe? FindById(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;
            return ListAll().FirstOrDefault(r =>
                string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public static bool TrySaveUserRecipe(HalconShapeModelParamRecipe recipe, out string error)
        {
            error = "";
            if (!TryValidateUserName(recipe.Name, out error))
                return false;
            if (recipe.Name.StartsWith("【内置】", StringComparison.Ordinal))
            {
                error = "请不要使用「【内置】」开头的名称。";
                return false;
            }

            try
            {
                Directory.CreateDirectory(RecipesDirectory);
                recipe.Id = recipe.Name.Trim();
                recipe.IsBuiltIn = false;
                recipe.HasOverride = false;
                string path = UserFilePath(recipe.Id);
                File.WriteAllText(path, JsonSerializer.Serialize(recipe, JsonOpts));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TrySaveBuiltinDefault(HalconShapeModelParamRecipe recipe, out string savedPath, out string error)
        {
            savedPath = "";
            error = "";
            if (recipe == null || string.IsNullOrWhiteSpace(recipe.Id)
                || !recipe.Id.StartsWith(BuiltinPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "不是内置配方。";
                return false;
            }

            HalconShapeModelParamRecipe? factory = BuiltInRecipes.FirstOrDefault(b =>
                string.Equals(b.Id, recipe.Id, StringComparison.OrdinalIgnoreCase));
            if (factory == null)
            {
                error = "未找到对应的出厂内置配方。";
                return false;
            }

            string? gitDir = TryFindGitRecipesDirectory();
            if (string.IsNullOrWhiteSpace(gitDir))
            {
                error = "未找到工程目录（IndustrialVisionTools.csproj）。请在源码目录下运行本程序后再覆盖内置配方，才能提交 git。";
                return false;
            }

            try
            {
                var stored = new HalconShapeModelParamRecipe
                {
                    Id = factory.Id,
                    Name = factory.Name,
                    Description = string.IsNullOrWhiteSpace(recipe.Description)
                        ? factory.Description
                        : recipe.Description,
                    IsBuiltIn = true,
                    Controls = recipe.Controls ?? new Dictionary<string, string>(StringComparer.Ordinal)
                };
                savedPath = WriteBuiltinJson(gitDir, stored);
                string shipped = Path.Combine(AppContext.BaseDirectory, "shape_model_recipes");
                try
                {
                    WriteBuiltinJson(shipped, stored);
                }
                catch
                {
                    // 输出目录写失败不影响 git 文件
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryResetBuiltin(string builtinId, out string savedPath, out string error)
        {
            savedPath = "";
            error = "";
            HalconShapeModelParamRecipe? factory = BuiltInRecipes.FirstOrDefault(b =>
                string.Equals(b.Id, builtinId, StringComparison.OrdinalIgnoreCase));
            if (factory == null)
            {
                error = "不是内置配方。";
                return false;
            }

            return TrySaveBuiltinDefault(CloneBuiltin(factory), out savedPath, out error);
        }

        public static bool TryDeleteUserRecipe(string id, out string error)
        {
            error = "";
            if (id.StartsWith(BuiltinPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "内置配方不能删除。";
                return false;
            }

            try
            {
                string path = UserFilePath(id);
                if (!File.Exists(path))
                {
                    error = "未找到该配方文件。";
                    return false;
                }

                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryValidateUserName(string? name, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "名称不能为空。";
                return false;
            }

            string n = name.Trim();
            if (n is "." or "..")
            {
                error = "名称无效。";
                return false;
            }

            if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = "名称包含非法字符。";
                return false;
            }

            if (n.StartsWith(BuiltinPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "请不要使用内置配方前缀。";
                return false;
            }

            return true;
        }

        public static bool UserNameExists(string name) =>
            ListAll().Any(r => !r.IsBuiltIn
                && string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        static string UserFilePath(string id) =>
            Path.Combine(RecipesDirectory, id.Trim() + ".json");

        static string BuiltinFileName(string builtinId) =>
            builtinId.Replace(":", "_", StringComparison.Ordinal) + ".json";

        public static string? TryFindGitRecipesDirectory()
        {
            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrWhiteSpace(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "IndustrialVisionTools.csproj")))
                    return Path.Combine(dir, "shape_model_recipes");
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            return null;
        }

        static HalconShapeModelParamRecipe LoadShippedBuiltin(HalconShapeModelParamRecipe factory)
        {
            HalconShapeModelParamRecipe item = CloneBuiltin(factory);
            foreach (string? dir in new[] { TryFindGitRecipesDirectory(), Path.Combine(AppContext.BaseDirectory, "shape_model_recipes") })
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                HalconShapeModelParamRecipe? loaded = TryLoadFile(Path.Combine(dir, BuiltinFileName(factory.Id)));
                if (loaded?.Controls is not { Count: > 0 })
                    continue;
                item.Controls = new Dictionary<string, string>(loaded.Controls, StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(loaded.Description))
                    item.Description = loaded.Description;
                if (!string.IsNullOrWhiteSpace(loaded.Name))
                    item.Name = loaded.Name;
                break;
            }

            return item;
        }

        static HalconShapeModelParamRecipe CloneBuiltin(HalconShapeModelParamRecipe src) =>
            new()
            {
                Id = src.Id,
                Name = src.Name,
                Description = src.Description,
                IsBuiltIn = true,
                Controls = new Dictionary<string, string>(src.Controls, StringComparer.Ordinal)
            };

        static string WriteBuiltinJson(string directory, HalconShapeModelParamRecipe recipe)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, BuiltinFileName(recipe.Id));
            File.WriteAllText(path, JsonSerializer.Serialize(recipe, JsonOpts));
            return path;
        }

        static HalconShapeModelParamRecipe? TryLoadFile(string path)
        {
            try
            {
                return JsonSerializer.Deserialize<HalconShapeModelParamRecipe>(
                    File.ReadAllText(path), JsonOpts);
            }
            catch
            {
                return null;
            }
        }

        static Dictionary<string, string> C(params (string Key, string Value)[] pairs)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in pairs)
                d[k] = v;
            return d;
        }

        public static IReadOnlyList<HalconShapeModelParamRecipe> BuiltInRecipes { get; } =
        [
            new()
            {
                Id = BuiltinPrefix + "outline",
                Name = "外框模型",
                Description = "方向粗定位：阈值抽外轮廓，刚性 .shm。角度开满 ±180°，覆盖现场任意摆放；结果角度给下游粗匹配收窄搜索。",
                IsBuiltIn = true,
                Controls = C(
                    ("CmbTemplateSource", "ThresholdXld"),
                    ("CmbModelKind", "Shape"),
                    ("CmbGenContourMode", "border"),
                    ("ChkLargestContourOnly", "true"),
                    ("TxtMinContourPoints", "10"),
                    ("TxtAngleStart", "-180"),
                    ("TxtAngleExtent", "360"),
                    ("TxtAngleStep", "0"),
                    ("TxtNumLevels", "0"),
                    ("TxtContrast", "auto"),
                    ("TxtMinContrast", "10"),
                    ("CmbOptimization", "point_reduction_medium"),
                    ("CmbMetric", "ignore_local_polarity"),
                    ("TxtFindNumMatches", "0"),
                    ("TxtFindMinScore", "0.25"),
                    ("TxtFindGreediness", "0.75"),
                    ("TxtFindNumLevels", "0"),
                    ("TxtFindMaxOverlap", "0.5"),
                    ("ChkFindApplyGridFilter", "false"),
                    ("ChkFindAutoRetry", "false"))
            },
            new()
            {
                Id = BuiltinPrefix + "coarse",
                Name = "粗匹配模型",
                Description = "位置粗定位：缩放形状 .shm（约 ±3%），角度约 ±15°。用稳定轮廓找位，Row/Col/Angle 给精匹配当锚点。",
                IsBuiltIn = true,
                Controls = C(
                    ("CmbTemplateSource", "ThresholdXld"),
                    ("CmbModelKind", "ScaledShape"),
                    ("CmbGenContourMode", "border"),
                    ("ChkLargestContourOnly", "true"),
                    ("TxtMinContourPoints", "10"),
                    ("TxtScaleMin", "0.97"),
                    ("TxtScaleMax", "1.03"),
                    ("TxtScaleStep", "0"),
                    ("TxtAngleStart", "-15"),
                    ("TxtAngleExtent", "30"),
                    ("TxtAngleStep", "0"),
                    ("TxtNumLevels", "0"),
                    ("TxtContrast", "auto"),
                    ("TxtMinContrast", "10"),
                    ("CmbOptimization", "auto"),
                    ("CmbMetric", "ignore_local_polarity"),
                    ("TxtFindNumMatches", "0"),
                    ("TxtFindMinScore", "0.2"),
                    ("TxtFindGreediness", "0.7"),
                    ("TxtFindNumLevels", "0"),
                    ("TxtFindMaxOverlap", "0.5"),
                    ("ChkFindApplyGridFilter", "false"),
                    ("ChkFindAutoRetry", "false"))
            },
            new()
            {
                Id = BuiltinPrefix + "fine",
                Name = "精匹配模型",
                Description = "最终轮廓：局部可变形 .dfm，角度再收窄、分数更高。轮廓必须贴边；流程接粗匹配 Coarse* 后在 Mask 域内搜。",
                IsBuiltIn = true,
                Controls = C(
                    ("CmbTemplateSource", "ThresholdXld"),
                    ("CmbModelKind", "Deformable"),
                    ("CmbGenContourMode", "border"),
                    ("ChkLargestContourOnly", "true"),
                    ("TxtMinContourPoints", "10"),
                    ("TxtScaleMin", "0.97"),
                    ("TxtScaleMax", "1.03"),
                    ("TxtScaleStep", "0"),
                    ("TxtAngleStart", "-10"),
                    ("TxtAngleExtent", "20"),
                    ("TxtAngleStep", "0"),
                    ("TxtNumLevels", "0"),
                    ("TxtContrast", "auto"),
                    ("TxtMinContrast", "10"),
                    ("CmbOptimization", "none"),
                    ("CmbMetric", "ignore_local_polarity"),
                    ("TxtFindNumMatches", "0"),
                    ("TxtFindMinScore", "0.45"),
                    ("TxtFindGreediness", "0.65"),
                    ("TxtFindNumLevels", "0"),
                    ("TxtFindMaxOverlap", "0.5"),
                    ("ChkFindApplyGridFilter", "false"),
                    ("ChkFindAutoRetry", "false"))
            }
        ];
    }
}
