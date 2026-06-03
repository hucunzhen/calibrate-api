using System.Windows;
using System.Windows.Controls;

namespace CalibOperatorCLI_Example
{
    public partial class ControllerLightPage
    {
        private static readonly string[] ControllerLightPersistControlNames =
        {
            "TxtCfgIp", "TxtCfgSm", "TxtCfgGw", "TxtCfgSn", "ChkCfgDhcp",
            "TxtConnectIp", "TxtConnectTimeout", "TxtComPort",
            "TxtChannel", "TxtIntensity", "TxtStrobe",
            "TxtIntCycle", "TxtTriMode", "TxtLightState"
        };

        private UiPersistScheduler? _controllerUiPersist;
        private bool _controllerUiReady;

        internal void SaveSession() => PersistControllerLightUi();

        private void EnsureControllerLightUiPersist()
        {
            if (_controllerUiPersist != null)
                return;
            _controllerUiPersist = new UiPersistScheduler(PersistControllerLightUi);
            Loaded += (_, _) =>
            {
                ApplyControllerLightUi();
                _controllerUiReady = true;
                AttachControllerLightUiPersistHandlers();
            };
            Unloaded += (_, _) => PersistControllerLightUi();
        }

        private void AttachControllerLightUiPersistHandlers()
        {
            foreach (string name in ControllerLightPersistControlNames)
            {
                if (FindName(name) is TextBox tb)
                {
                    tb.LostFocus += (_, _) => ScheduleControllerLightUiPersist();
                    tb.TextChanged += (_, _) => ScheduleControllerLightUiPersist();
                }
                else if (FindName(name) is CheckBox cb)
                {
                    cb.Checked += (_, _) => ScheduleControllerLightUiPersist();
                    cb.Unchecked += (_, _) => ScheduleControllerLightUiPersist();
                }
            }
        }

        private void ScheduleControllerLightUiPersist()
        {
            if (!_controllerUiReady)
                return;
            _controllerUiPersist?.Schedule();
        }

        private void PersistControllerLightUi()
        {
            var s = new ControllerLightPageUiSettings
            {
                Controls = PageUiBinder.Capture(this, ControllerLightPersistControlNames)
            };
            s.Save();
        }

        private void ApplyControllerLightUi()
        {
            var s = ControllerLightPageUiSettings.Load();
            PageUiBinder.Apply(this, s.Controls, ControllerLightPersistControlNames);
        }
    }
}
