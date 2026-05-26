using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace CalibOperatorCLI_Example
{
    public partial class MainWindow : Window
    {
        private const string LastFlowFileName = "last_flow_path.txt";
        private PlcPage _plcPage;
        private ControllerLightPage _controllerLightPage;
        private HistogramPage _histogramPage;
        private FlowHostPage _flowHostPage;
        private YoloSegTrainPage _yoloSegTrainPage;
        private HalconDlSegPage _halconDlSegPage;
        private SamTrainPage _samTrainPage;
        private HalconShapeModelPage _halconShapeModelPage;

        public MainWindow()
        {
            InitializeComponent();

            // 海康 SDK：进程级初始化一次（流程编排 camera_loop 等会创建独立 CameraService）
            try
            {
                CameraService.InitializeSDK();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"相机 SDK 初始化失败: {ex.Message}", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _plcPage = new PlcPage();
            _controllerLightPage = new ControllerLightPage();
            _histogramPage = new HistogramPage();
            _flowHostPage = new FlowHostPage();
            _flowHostPage.FlowLoaded += SaveLastFlowPath;
            _yoloSegTrainPage = new YoloSegTrainPage();
            _halconDlSegPage = new HalconDlSegPage();
            _samTrainPage = new SamTrainPage();
            _halconShapeModelPage = new HalconShapeModelPage();

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
            var dim = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            var accent = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
            NavPlc.Background = dim;
            NavController.Background = dim;
            NavFlow.Background = dim;
            NavAdvanced.Background = dim;

            switch (tab)
            {
                case "Plc":
                    NavPlc.Background = accent;
                    break;
                case "Controller":
                    NavController.Background = accent;
                    break;
                case "Flow":
                    NavFlow.Background = accent;
                    break;
                case "Histogram":
                case "YoloSeg":
                case "HalconDlSeg":
                case "SamOnnx":
                case "HalconShapeModel":
                    NavAdvanced.Background = accent;
                    break;
            }
        }

        private void NavAdvanced_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.Placement = PlacementMode.Bottom;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private void NavPlc_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo(_plcPage);
            HighlightTab("Plc");
        }

        private void NavController_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo(_controllerLightPage);
            HighlightTab("Controller");
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

        private void NavHalconDlSeg_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo(_halconDlSegPage);
            HighlightTab("HalconDlSeg");
        }

        private void NavSamOnnx_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo(_samTrainPage);
            HighlightTab("SamOnnx");
        }

        private void NavHalconShapeModel_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo(_halconShapeModelPage);
            HighlightTab("HalconShapeModel");
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
            HalconShapeMatchGridDiagnostics.EnableForFlowFile(flowFilePath, mirrorConsole: true);
            try
            {
                try { Console.WriteLine($"[GridFilter] 诊断日志: {HalconShapeMatchGridDiagnostics.LogFilePath}"); } catch { /* ignore */ }
                return await fp.RunAllAsync(clearLog: true, preferNativeEngine: preferNativeEngine);
            }
            finally
            {
                fp.MirrorErrorsToStderr = false;
                fp.TraceEnginePathToConsole = false;
                HalconShapeMatchGridDiagnostics.Disable();
            }
        }

        private static string GetLastFlowRecordPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppProduct.AppDataFolderName);
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
