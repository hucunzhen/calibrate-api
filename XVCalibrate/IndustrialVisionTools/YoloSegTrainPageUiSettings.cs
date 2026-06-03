using System.Collections.Generic;

namespace CalibOperatorCLI_Example
{
    internal sealed class YoloSegTrainPageUiSettings
    {
        public const string FileName = "yolo_seg_train_page_ui.json";

        public Dictionary<string, string> Controls { get; set; } = new();

        public static YoloSegTrainPageUiSettings Load() =>
            AppUiSettingsStore.Load<YoloSegTrainPageUiSettings>(FileName);

        public void Save() => AppUiSettingsStore.Save(FileName, this);
    }
}
