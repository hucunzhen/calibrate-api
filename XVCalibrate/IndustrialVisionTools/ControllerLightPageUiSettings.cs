using System.Collections.Generic;

namespace CalibOperatorCLI_Example
{
    internal sealed class ControllerLightPageUiSettings
    {
        public const string FileName = "controller_light_page_ui.json";

        public Dictionary<string, string> Controls { get; set; } = new();

        public static ControllerLightPageUiSettings Load() =>
            AppUiSettingsStore.Load<ControllerLightPageUiSettings>(FileName);

        public void Save() => AppUiSettingsStore.Save(FileName, this);
    }
}
