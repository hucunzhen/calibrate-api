namespace CalibOperatorCLI_Example
{
    internal sealed class FlowPageToolbarUiSettings
    {
        public const string FileName = "flow_page_toolbar_ui.json";

        public string LatticeGridRows { get; set; } = "8";
        public string LatticeGridCols { get; set; } = "2";
        public string StandaloneDebugImagePath { get; set; } = "";

        public static FlowPageToolbarUiSettings Load() =>
            AppUiSettingsStore.Load<FlowPageToolbarUiSettings>(FileName);

        public void Save() => AppUiSettingsStore.Save(FileName, this);
    }
}
