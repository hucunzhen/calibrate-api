namespace CalibOperatorCLI_Example
{
    internal sealed class FlowRecipeUiSettings
    {
        public const string FileName = "flow_recipe_ui.json";

        /// <summary>flows/ 下子目录名，如 v1、v2。</summary>
        public string SelectedRecipe { get; set; } = "v1";

        /// <summary>配方根目录（其下每个子文件夹为一个配方）；空则自动探测仓库 flows/。</summary>
        public string FlowsRootDirectory { get; set; } = "";

        public static FlowRecipeUiSettings Load() =>
            AppUiSettingsStore.Load<FlowRecipeUiSettings>(FileName);

        public void Save() => AppUiSettingsStore.Save(FileName, this);
    }
}
