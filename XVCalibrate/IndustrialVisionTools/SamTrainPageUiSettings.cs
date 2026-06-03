using System.Collections.Generic;

namespace CalibOperatorCLI_Example
{
    internal sealed class SamTrainPageUiSettings
    {
        public const string FileName = "sam_train_page_ui.json";

        public Dictionary<string, string> Controls { get; set; } = new();

        public static SamTrainPageUiSettings Load() =>
            AppUiSettingsStore.Load<SamTrainPageUiSettings>(FileName);

        public void Save() => AppUiSettingsStore.Save(FileName, this);
    }
}
