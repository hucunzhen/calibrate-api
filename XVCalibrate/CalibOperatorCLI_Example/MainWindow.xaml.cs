using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CalibOperatorCLI_Example
{
    public partial class MainWindow : Window
    {
        private const string LastFlowFileName = "last_flow_path.txt";
        private CalibrationPage _calibrationPage;
        private TrajectoryPage _trajectoryPage;
        private PlcPage _plcPage;
        private HistogramPage _histogramPage;
        private FlowPage _flowPage;

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
            _flowPage = new FlowPage();
            _flowPage.FlowLoaded += SaveLastFlowPath;

            // 将轨迹检测结果获取委托注入 PlcPage，使其可访问最新轨迹
            _plcPage.GetTrajectoryResult = () => _trajectoryPage.LastResult;

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
            NavFlow.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));

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
                case "Flow":
                    NavFlow.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
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

        private void NavFlow_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo(_flowPage);
            HighlightTab("Flow");
            TryAutoLoadLastFlowOnFlowPageSwitch();
        }

        public async Task<bool> RunFlowConfigInBackgroundAsync(string flowFilePath)
        {
            if (string.IsNullOrWhiteSpace(flowFilePath))
                throw new ArgumentException("Flow 文件路径为空", nameof(flowFilePath));
            if (!File.Exists(flowFilePath))
                throw new FileNotFoundException("Flow 文件不存在", flowFilePath);

            NavigateTo(_flowPage);
            HighlightTab("Flow");

            bool loaded = _flowPage.LoadFlowFromFile(flowFilePath, showErrorDialog: false);
            if (!loaded) return false;

            SaveLastFlowPath(flowFilePath);
            return await _flowPage.RunAllAsync(clearLog: true, preferNativeEngine: true);
        }

        private static string GetLastFlowRecordPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CalibOperatorCLI_Example");
            return Path.Combine(dir, LastFlowFileName);
        }

        private static void SaveLastFlowPath(string flowPath)
        {
            if (string.IsNullOrWhiteSpace(flowPath)) return;
            try
            {
                string full = Path.GetFullPath(flowPath);
                string recordPath = GetLastFlowRecordPath();
                string? parent = Path.GetDirectoryName(recordPath);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);
                File.WriteAllText(recordPath, full);
            }
            catch
            {
                // 非关键流程，忽略持久化失败
            }
        }

        private static string? TryReadLastFlowPath()
        {
            try
            {
                string recordPath = GetLastFlowRecordPath();
                if (!File.Exists(recordPath)) return null;
                string path = File.ReadAllText(recordPath).Trim();
                if (string.IsNullOrWhiteSpace(path)) return null;
                return path;
            }
            catch
            {
                return null;
            }
        }

        private void TryAutoLoadLastFlowOnFlowPageSwitch()
        {
            string? lastPath = TryReadLastFlowPath();
            if (string.IsNullOrWhiteSpace(lastPath) || !File.Exists(lastPath)) return;

            string full = Path.GetFullPath(lastPath);
            string current = _flowPage.CurrentFlowFilePath ?? string.Empty;
            if (string.Equals(full, current, StringComparison.OrdinalIgnoreCase)) return;

            _flowPage.LoadFlowFromFile(full, showErrorDialog: false);
        }
    }
}
