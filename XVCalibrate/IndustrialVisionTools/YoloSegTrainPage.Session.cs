using System.Windows;
using System.Windows.Controls;

namespace CalibOperatorCLI_Example
{
    public partial class YoloSegTrainPage
    {
        private static readonly string[] YoloSegPersistControlNames =
        {
            "TxtDatasetRoot", "TxtSourceFolder", "TxtClassNames",
            "TxtAugDegrees", "TxtAugTranslate", "TxtAugScale", "TxtAugShear", "TxtAugPerspective",
            "TxtAugFliplr", "TxtAugFlipud", "TxtAugMosaic", "TxtAugMixup", "TxtAugCopyPaste",
            "TxtAugHsvH", "TxtAugHsvS", "TxtAugHsvV",
            "TxtPython", "TxtWeights", "TxtConf", "TxtSingleClass", "TxtDevice", "TxtAutoLabelScript",
            "TxtEpochs", "TxtImgSz", "TxtBatch", "TxtTrainScript", "TxtTrainPatience",
            "TxtInferImgsz", "TxtInferImagePath", "TxtInferScript",
            "ChkAnnotResampleContour", "TxtAnnotResampleSpacing"
        };

        private UiPersistScheduler? _yoloUiPersist;
        private bool _yoloUiReady;

        internal void SaveSession() => PersistYoloSegUi();

        private void EnsureYoloSegUiPersist()
        {
            if (_yoloUiPersist != null)
                return;
            _yoloUiPersist = new UiPersistScheduler(PersistYoloSegUi);
            Loaded += (_, _) =>
            {
                ApplyYoloSegUi();
                _yoloUiReady = true;
                AttachYoloSegUiPersistHandlers();
            };
            Unloaded += (_, _) => PersistYoloSegUi();
        }

        private void AttachYoloSegUiPersistHandlers()
        {
            foreach (string name in YoloSegPersistControlNames)
            {
                if (FindName(name) is TextBox tb)
                {
                    tb.LostFocus += (_, _) => ScheduleYoloSegUiPersist();
                    tb.TextChanged += (_, _) => ScheduleYoloSegUiPersist();
                }
                else if (FindName(name) is CheckBox cb)
                {
                    cb.Checked += (_, _) => ScheduleYoloSegUiPersist();
                    cb.Unchecked += (_, _) => ScheduleYoloSegUiPersist();
                }
            }
        }

        private void ScheduleYoloSegUiPersist()
        {
            if (!_yoloUiReady)
                return;
            _yoloUiPersist?.Schedule();
        }

        private void PersistYoloSegUi()
        {
            var s = new YoloSegTrainPageUiSettings
            {
                Controls = PageUiBinder.Capture(this, YoloSegPersistControlNames)
            };
            s.Save();
        }

        private void ApplyYoloSegUi()
        {
            var s = YoloSegTrainPageUiSettings.Load();
            PageUiBinder.Apply(this, s.Controls, YoloSegPersistControlNames);
        }
    }
}
