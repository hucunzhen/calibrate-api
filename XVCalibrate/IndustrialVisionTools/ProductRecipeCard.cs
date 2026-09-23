using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalibOperatorCLI_Example
{
    /// <summary>工艺卡：绑定配方目录内的主流程与换型验收状态（product_card.json）。</summary>
    public sealed class ProductRecipeCard
    {
        public const string FileName = "product_card.json";

        public string DisplayName { get; set; } = "";

        public string MainFlowFile { get; set; } = FlowRecipeCatalog.DefaultMainFlowFileName;

        public string PublishedVersion { get; set; } = "";

        /// <summary>阶段 0–6 是否完成（键 "0"…"6"）。</summary>
        public Dictionary<string, bool> ChangeoverStages { get; set; } = new();

        public bool ProductionReleased { get; set; }

        [JsonIgnore]
        public bool AllChangeoverStagesComplete =>
            Enumerable.Range(0, 7).All(i => ChangeoverStages.TryGetValue(i.ToString(), out bool ok) && ok);

        [JsonIgnore]
        public bool ReadyForOperatorProduction => ProductionReleased && AllChangeoverStagesComplete;

        public static ProductRecipeCard CreateDefault(string recipeName) => new()
        {
            DisplayName = recipeName,
            MainFlowFile = FlowRecipeCatalog.DefaultMainFlowFileName,
            PublishedVersion = "",
            ProductionReleased = false,
            ChangeoverStages = Enumerable.Range(0, 7).ToDictionary(i => i.ToString(), _ => false),
        };

        public static string GetCardPath(string recipeDirectory) =>
            Path.Combine(recipeDirectory, FileName);

        public static ProductRecipeCard LoadForRecipe(string recipeName)
        {
            string? dir = FlowRecipeCatalog.TryGetRecipeDirectory(recipeName);
            if (dir == null)
                return CreateDefault(recipeName ?? "");

            return LoadFromDirectory(dir, recipeName ?? Path.GetFileName(dir));
        }

        public static ProductRecipeCard LoadFromDirectory(string recipeDirectory, string fallbackDisplayName)
        {
            string path = GetCardPath(recipeDirectory);
            if (!File.Exists(path))
                return CreateDefault(fallbackDisplayName);

            try
            {
                var card = JsonSerializer.Deserialize<ProductRecipeCard>(File.ReadAllText(path), JsonOptions)
                           ?? CreateDefault(fallbackDisplayName);
                card.EnsureStageKeys();
                if (string.IsNullOrWhiteSpace(card.DisplayName))
                    card.DisplayName = fallbackDisplayName;
                return card;
            }
            catch
            {
                return CreateDefault(fallbackDisplayName);
            }
        }

        public void SaveToRecipeDirectory(string recipeDirectory)
        {
            EnsureStageKeys();
            string path = GetCardPath(recipeDirectory);
            Directory.CreateDirectory(recipeDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }

        public void EnsureStageKeys()
        {
            for (int i = 0; i < 7; i++)
            {
                string k = i.ToString();
                if (!ChangeoverStages.ContainsKey(k))
                    ChangeoverStages[k] = false;
            }
        }

        public string ResolveMainFlowPath(string recipeDirectory)
        {
            if (string.IsNullOrWhiteSpace(MainFlowFile))
                return FlowRecipeCatalog.TryResolveMainFlowPath(recipeDirectory) ?? "";

            string custom = Path.Combine(recipeDirectory, MainFlowFile.Trim());
            return File.Exists(custom) ? Path.GetFullPath(custom) : FlowRecipeCatalog.TryResolveMainFlowPath(recipeDirectory) ?? "";
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }
}
