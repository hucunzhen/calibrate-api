namespace CalibOperatorCLI_Example
{
    internal sealed class AppNavigationUiSettings
    {
        public const string FileName = "app_navigation_ui.json";

        /// <summary>Operator | Plc | Flow | Controller | …</summary>
        public string LastTab { get; set; } = "Operator";

        public static AppNavigationUiSettings Load() => AppUiSettingsStore.Load<AppNavigationUiSettings>(FileName);

        public void Save() => AppUiSettingsStore.Save(FileName, this);
    }
}
