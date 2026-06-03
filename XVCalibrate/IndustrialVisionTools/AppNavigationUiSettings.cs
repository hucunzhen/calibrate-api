namespace CalibOperatorCLI_Example
{
    internal sealed class AppNavigationUiSettings
    {
        public const string FileName = "app_navigation_ui.json";

        /// <summary>Plc | Flow | Controller | Histogram | YoloSeg | HalconDlSeg | SamOnnx | HalconShapeModel</summary>
        public string LastTab { get; set; } = "Flow";

        public static AppNavigationUiSettings Load() => AppUiSettingsStore.Load<AppNavigationUiSettings>(FileName);

        public void Save() => AppUiSettingsStore.Save(FileName, this);
    }
}
