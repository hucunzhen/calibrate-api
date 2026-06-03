using System.Windows;
using System.Windows.Controls;

namespace CalibOperatorCLI_Example
{
    public partial class SamTrainPage
    {
        private static readonly string[] SamPersistControlNames =
        {
            "TxtPython", "TxtExportScript", "TxtQuantScript", "TxtTrainScript", "TxtMaskGenScript",
            "TxtMaskImagesDir", "TxtMaskLabelsDir", "TxtMaskOutDir", "TxtMaskClassFilter",
            "TxtTrainDataRoot", "TxtTrainImagesSubdir", "TxtTrainMasksSubdir", "TxtTrainOutPth",
            "TxtTrainEpochs", "TxtTrainLr", "TxtTrainDevice", "ChkTrainAugment", "ChkTrainImageEncoder",
            "TxtCheckpoint", "CmbModelType", "TxtOutDir", "TxtOpset",
            "ChkReturnSingleMask", "ChkGeluApproximate", "ChkNoEncoder", "ChkNoDecoder",
            "TxtQuantInDir", "TxtQuantPrefix", "TxtQuantSuffix",
            "ChkQuantMatmulOnly", "ChkQuantPerChannel", "ChkQuantReduceRange"
        };

        private UiPersistScheduler? _samUiPersist;
        private bool _samUiReady;

        internal void SaveSession() => PersistSamUi();

        private void EnsureSamUiPersist()
        {
            if (_samUiPersist != null)
                return;
            _samUiPersist = new UiPersistScheduler(PersistSamUi);
            Loaded += (_, _) =>
            {
                ApplySamUi();
                _samUiReady = true;
                AttachSamUiPersistHandlers();
            };
            Unloaded += (_, _) => PersistSamUi();
        }

        private void AttachSamUiPersistHandlers()
        {
            foreach (string name in SamPersistControlNames)
            {
                if (FindName(name) is TextBox tb)
                {
                    tb.LostFocus += (_, _) => ScheduleSamUiPersist();
                    tb.TextChanged += (_, _) => ScheduleSamUiPersist();
                }
                else if (FindName(name) is CheckBox cb)
                {
                    cb.Checked += (_, _) => ScheduleSamUiPersist();
                    cb.Unchecked += (_, _) => ScheduleSamUiPersist();
                }
                else if (FindName(name) is ComboBox cmb)
                {
                    cmb.SelectionChanged += (_, _) => ScheduleSamUiPersist();
                }
            }
        }

        private void ScheduleSamUiPersist()
        {
            if (!_samUiReady)
                return;
            _samUiPersist?.Schedule();
        }

        private void PersistSamUi()
        {
            var s = new SamTrainPageUiSettings
            {
                Controls = PageUiBinder.Capture(this, SamPersistControlNames)
            };
            s.Save();
        }

        private void ApplySamUi()
        {
            var s = SamTrainPageUiSettings.Load();
            PageUiBinder.Apply(this, s.Controls, SamPersistControlNames);
        }
    }
}
