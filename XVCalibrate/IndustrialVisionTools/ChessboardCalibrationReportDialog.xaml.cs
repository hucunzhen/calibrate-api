using System.Windows;

namespace CalibOperatorCLI_Example
{
    public partial class ChessboardCalibrationReportDialog : Window
    {
        private ChessboardCalibrationReportDialog(string headline, string reportBody)
        {
            InitializeComponent();
            TxtHeadline.Text = headline;
            TxtReport.Text = reportBody;
        }

        public static void ShowDialog(Window? owner, string calibrationJson)
        {
            if (!ChessboardCalibrationQuality.TryBuildPopupReport(
                    calibrationJson, out string headline, out string body))
                return;

            bool passed = headline.StartsWith('✓');
            ShowTextReport(
                owner,
                passed ? "棋盘格标定质检 · 合格" : "棋盘格标定质检 · 不合格",
                headline,
                body);
        }

        public static void ShowTextReport(Window? owner, string windowTitle, string headline, string reportBody)
        {
            var dlg = new ChessboardCalibrationReportDialog(headline, reportBody)
            {
                Owner = owner,
                Title = windowTitle
            };
            dlg.ShowDialog();
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
