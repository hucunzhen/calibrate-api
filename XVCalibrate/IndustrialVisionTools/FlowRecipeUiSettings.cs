namespace CalibOperatorCLI_Example
{
    internal sealed class FlowRecipeUiSettings
    {
        public const string FileName = "flow_recipe_ui.json";

        /// <summary>flows/ 下子目录名，如 v1、v2。</summary>
        public string SelectedRecipe { get; set; } = "v1";

        public static FlowRecipeUiSettings Load() =>
            AppUiSettingsStore.Load<FlowRecipeUiSettings>(FileName);

        public void Save() => AppUiSettingsStore.Save(FileName, this);
    }
}
