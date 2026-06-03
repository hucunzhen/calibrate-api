using System.Collections.Generic;

namespace CalibOperatorCLI_Example
{
    internal sealed class HistogramPageUiSettings
    {
        public const string FileName = "histogram_page_ui.json";

        public string ImagePath { get; set; } = "";
        public Dictionary<string, string> Controls { get; set; } = new();

        public static HistogramPageUiSettings Load() => AppUiSettingsStore.Load<HistogramPageUiSettings>(FileName);

        public void Save() => AppUiSettingsStore.Save(FileName, this);
    }
}
