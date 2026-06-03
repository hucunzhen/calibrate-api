using System.Collections.Generic;

namespace CalibOperatorCLI_Example
{
    internal sealed class PlcPageUiSettings
    {
        public const string FileName = "plc_page_ui.json";

        public Dictionary<string, string> Controls { get; set; } = new();

        public static PlcPageUiSettings Load() => AppUiSettingsStore.Load<PlcPageUiSettings>(FileName);

        public void Save() => AppUiSettingsStore.Save(FileName, this);
    }
}
