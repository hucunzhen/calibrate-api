using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CalibOperatorCLI_Example
{
    public partial class MainWindow : Window
    {
        private CalibrationPage _calibrationPage;
        private TrajectoryPage _trajectoryPage;
        private PlcPage _plcPage;
        private HistogramPage _histogramPage;

        /// <summary>
        /// 全局相机服务实例
        /// </summary>
        public static CameraService? Camera { get; private set; }

        public MainWindow()
        {
            InitializeComponent();

            // 初始化海康 SDK
            try
            {
                CameraService.InitializeSDK();
                Camera = new CameraService();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"相机 SDK 初始化失败: {ex.Message}", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _calibrationPage = new CalibrationPage();
            _trajectoryPage = new TrajectoryPage();
            _plcPage = new PlcPage();
            _histogramPage = new HistogramPage();

            // 默认显示标定页
            NavigateTo(_calibrationPage);
            HighlightTab("Calibration");
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // 释放相机资源 + 反初始化 SDK
            Camera?.Dispose();
            Camera = null;
            CameraService.FinalizeSDK();
        }

        private void NavigateTo(Page page)
        {
            MainFrame.Navigate(page);
        }

        private void HighlightTab(string tab)
        {
            // Reset all tabs
            NavCalibration.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            NavTrajectory.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            NavPlc.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            NavHistogram.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));

            switch (tab)
            {
                case "Calibration":
                    NavCalibration.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
                    break;
                case "Trajectory":
                    NavTrajectory.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
                    break;
                case "Plc":
                    NavPlc.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
                    break;
                case "Histogram":
                    NavHistogram.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
                    break;
            }
        }

        private void NavCalibration_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo(_calibrationPage);
            HighlightTab("Calibration");
        }

        private void NavTrajectory_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo(_trajectoryPage);
            HighlightTab("Trajectory");
        }

        private void NavPlc_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo(_plcPage);
            HighlightTab("Plc");
        }

        private void NavHistogram_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo(_histogramPage);
            HighlightTab("Histogram");
        }
    }
}
