using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CalibOperatorCLI_Example
{
    public partial class HistogramPage
    {
        private static readonly string[] HistogramPersistControlNames =
        {
            "ChkShowOtsu", "ChkShowMean", "ChkShowCustom", "TxtCustomThreshold",
            "ChkLogScale", "ChkShowCumulative", "ChkShowStats",
            "ChkEnableRange", "TxtRangeMin", "TxtRangeMax"
        };

        private UiPersistScheduler? _histogramUiPersist;
        private bool _histogramUiReady;

        internal void SaveSession() => PersistHistogramUi();

        private void EnsureHistogramUiPersist()
        {
            if (_histogramUiPersist != null)
                return;
            _histogramUiPersist = new UiPersistScheduler(PersistHistogramUi);
            Loaded += (_, _) =>
            {
                ApplyHistogramUi();
                _histogramUiReady = true;
                AttachHistogramUiPersistHandlers();
            };
            Unloaded += (_, _) => PersistHistogramUi();
        }

        private void AttachHistogramUiPersistHandlers()
        {
            foreach (string name in HistogramPersistControlNames)
            {
                if (FindName(name) is TextBox tb)
                {
                    tb.LostFocus += (_, _) => ScheduleHistogramUiPersist();
                    tb.TextChanged += (_, _) => ScheduleHistogramUiPersist();
                }
                else if (FindName(name) is CheckBox cb)
                {
                    cb.Click += (_, _) => ScheduleHistogramUiPersist();
                }
            }
        }

        private void ScheduleHistogramUiPersist()
        {
            if (!_histogramUiReady)
                return;
            _histogramUiPersist?.Schedule();
        }

        private void PersistHistogramUi()
        {
            var s = new HistogramPageUiSettings
            {
                ImagePath = string.IsNullOrWhiteSpace(_loadedImagePath) ? "" : Path.GetFullPath(_loadedImagePath),
                Controls = PageUiBinder.Capture(this, HistogramPersistControlNames)
            };
            s.Save();
        }

        private void ApplyHistogramUi()
        {
            var s = HistogramPageUiSettings.Load();
            PageUiBinder.Apply(this, s.Controls, HistogramPersistControlNames);

            if (!string.IsNullOrWhiteSpace(s.ImagePath) && File.Exists(s.ImagePath))
            {
                try
                {
                    LoadImage(s.ImagePath);
                    StatusText.Text = $"已恢复: {Path.GetFileName(s.ImagePath)}";
                }
                catch
                {
                    // ignored
                }
            }
        }

        private string? _loadedImagePath;
    }
}
