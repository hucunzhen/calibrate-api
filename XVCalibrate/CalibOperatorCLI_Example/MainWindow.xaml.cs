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
        private PlcPage _plcPage;
        private HistogramPage _histogramPage;
        private FlowHostPage _flowHostPage;
        private YoloSegTrainPage _yoloSegTrainPage;
        private SamTrainPage _samTrainPage;

        public MainWindow()
        {
            InitializeComponent();

            // 海康 SDK：进程级初始化一次（组态 camera_loop 等会创建独立 CameraService）
            try
            {
                CameraService.InitializeSDK();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"相机 SDK 初始化失败: {ex.Message}", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _plcPage = new PlcPage();
            _histogramPage = new HistogramPage();
            _flowHostPage = new FlowHostPage();
            _flowHostPage.FlowLoaded += SaveLastFlowPath;
            _yoloSegTrainPage = new YoloSegTrainPage();
            _samTrainPage = new SamTrainPage();

            NavigateTo(_flowHostPage);
            HighlightTab("Flow");
            TryAutoLoadLastFlowOnFlowPageSwitch();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try { CameraService.FinalizeSDK(); } catch { /* ignored */ }
        }

        private void NavigateTo(Page page)
        {
            MainFrame.Navigate(page);
        }

        private void HighlightTab(string tab)
        {
            NavPlc.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            NavHistogram.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            NavFlow.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            NavYoloSeg.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            NavSamOnnx.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));

            switch (tab)
            {
                case "Plc":
                    NavPlc.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
                    break;
                case "Histogram":
                    NavHistogram.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
                    break;
                case "Flow":
                    NavFlow.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
                    break;
                case "YoloSeg":
                    NavYoloSeg.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
                    break;
                case "SamOnnx":
                    NavSamOnnx.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
                    break;
            }
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
            NavigateTo(_flowHostPage);
            HighlightTab("Flow");
            TryAutoLoadLastFlowOnFlowPageSwitch();
        }

        private void NavYoloSeg_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo(_yoloSegTrainPage);
            HighlightTab("YoloSeg");
        }

        private void NavSamOnnx_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo(_samTrainPage);
            HighlightTab("SamOnnx");
        }

        public async Task<bool> RunFlowConfigInBackgroundAsync(string flowFilePath, bool preferNativeEngine = false)
        {
            if (string.IsNullOrWhiteSpace(flowFilePath))
                throw new ArgumentException("Flow 文件路径为空", nameof(flowFilePath));
            if (!File.Exists(flowFilePath))
                throw new FileNotFoundException("Flow 文件不存在", flowFilePath);

            NavigateTo(_flowHostPage);
            HighlightTab("Flow");

            var fp = _flowHostPage.ActiveFlowOrFirst();
            if (fp == null)
                return false;
            bool loaded = fp.LoadFlowFromFile(flowFilePath, showErrorDialog: false);
            if (!loaded) return false;

            SaveLastFlowPath(flowFilePath);
            fp.MirrorErrorsToStderr = true;
            fp.TraceEnginePathToConsole = true;
            try
            {
                return await fp.RunAllAsync(clearLog: true, preferNativeEngine: preferNativeEngine);
            }
            finally
            {
                fp.MirrorErrorsToStderr = false;
                fp.TraceEnginePathToConsole = false;
            }
        }

        private static string GetLastFlowRecordPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CalibOperatorCLI_Example");
            return Path.Combine(dir, LastFlowFileName);
        }

        private static void SaveLastFlowPath(string? flowPath)
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

            _flowHostPage.TryAutoLoadLastFlowIfApplicable(lastPath);
        }
    }
}
