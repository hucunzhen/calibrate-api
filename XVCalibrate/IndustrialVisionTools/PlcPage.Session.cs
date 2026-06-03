using System.Windows;
using System.Windows.Controls;

namespace CalibOperatorCLI_Example
{
    public partial class PlcPage
    {
        private static readonly string[] PlcPersistControlNames =
        {
            "TxtPlcIp", "TxtPlcPort", "TxtReadInterval", "ChkAutoRead",
            "TxtLaserPower",
            "TxtStopSafeX", "TxtStopSafeY", "TxtStopSafeZ",
            "TxtPhotoPosX", "TxtPhotoPosY", "TxtPhotoPosZ",
            "TxtLaserRelX", "TxtLaserRelY", "TxtLaserRelZ",
            "TxtWeldSpeed", "TxtReturnSafeSpeed", "TxtPhotoApproachSpeed", "TxtFastOffsetSpeed",
            "TxtXHomeSpd", "TxtXManualSpd", "TxtXPosLimit", "TxtXNegLimit",
            "TxtYHomeSpd", "TxtYManualSpd", "TxtYPosLimit", "TxtYNegLimit",
            "TxtZHomeSpd", "TxtZManualSpd", "TxtZPosLimit", "TxtZNegLimit"
        };

        private UiPersistScheduler? _plcUiPersist;
        private bool _plcUiReady;

        internal void SaveSession() => PersistPlcUiFromControls();

        private void EnsurePlcUiPersist()
        {
            if (_plcUiPersist != null)
                return;
            _plcUiPersist = new UiPersistScheduler(PersistPlcUiFromControls);
            Loaded += (_, _) =>
            {
                ApplyPlcUiToControls();
                _plcUiReady = true;
                AttachPlcUiPersistHandlers();
            };
            Unloaded += (_, _) => PersistPlcUiFromControls();
        }

        private void AttachPlcUiPersistHandlers()
        {
            foreach (string name in PlcPersistControlNames)
            {
                if (FindName(name) is TextBox tb)
                {
                    tb.LostFocus += (_, _) => SchedulePlcUiPersist();
                    tb.TextChanged += (_, _) => SchedulePlcUiPersist();
                }
                else if (FindName(name) is CheckBox cb)
                {
                    cb.Checked += (_, _) => SchedulePlcUiPersist();
                    cb.Unchecked += (_, _) => SchedulePlcUiPersist();
                }
            }
        }

        private void SchedulePlcUiPersist()
        {
            if (!_plcUiReady)
                return;
            _plcUiPersist?.Schedule();
        }

        private void PersistPlcUiFromControls()
        {
            var s = new PlcPageUiSettings
            {
                Controls = PageUiBinder.Capture(this, PlcPersistControlNames)
            };
            s.Save();
        }

        private void ApplyPlcUiToControls()
        {
            var s = PlcPageUiSettings.Load();
            PageUiBinder.Apply(this, s.Controls, PlcPersistControlNames);
        }
    }
}
