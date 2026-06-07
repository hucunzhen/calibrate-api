using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        private const string OpenFlowTabsFileName = "open_flow_tabs.json";

        private sealed class FlowTabsSessionFile
        {
            public int ActiveIndex { get; set; }
            public List<string?> Tabs { get; set; } = new();
        }

        private static readonly JsonSerializerOptions FlowSessionJsonOptions = new()
        {
            WriteIndented = true
        };
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
            _flowHostPage.FlowLoaded += _ => SaveFlowSession();
            _flowHostPage.OpenTabsChanged += SaveFlowSession;
            _yoloSegTrainPage = new YoloSegTrainPage();
            _halconDlSegPage = new HalconDlSegPage();
            _samTrainPage = new SamTrainPage();
            _halconShapeModelPage = new HalconShapeModelPage();

            RestoreLastNavigationTab();
            TryRestoreFlowSessionOnStartup();
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveCurrentPageSession();
            SaveNavigationTab();
            base.OnClosed(e);
            try { CameraService.FinalizeSDK(); } catch { /* ignored */ }
        }

        private void NavigateTo(Page page)
        {
            SaveCurrentPageSession();
            MainFrame.Navigate(page);
            SaveNavigationTab(page);
        }

        private void SaveCurrentPageSession()
        {
            switch (MainFrame.Content)
            {
                case PlcPage:
                    _plcPage.SaveSession();
                    break;
                case ControllerLightPage:
                    _controllerLightPage.SaveSession();
                    break;
                case HistogramPage:
                    _histogramPage.SaveSession();
                    break;
                case FlowHostPage:
                    _flowHostPage.ActiveFlowOrFirst()?.SaveToolbarDefaultsToStore();
                    SaveFlowSession();
                    break;
                case YoloSegTrainPage:
                    _yoloSegTrainPage.SaveSession();
                    break;
                case HalconDlSegPage:
                    _halconDlSegPage.SaveSession();
                    break;
                case SamTrainPage:
                    _samTrainPage.SaveSession();
                    break;
                case HalconShapeModelPage:
                    _halconShapeModelPage.SaveSession();
                    break;
            }
        }

        private static string TabKeyFromPage(Page page) => page switch
        {
            PlcPage => "Plc",
            ControllerLightPage => "Controller",
            FlowHostPage => "Flow",
            HistogramPage => "Histogram",
            YoloSegTrainPage => "YoloSeg",
            HalconDlSegPage => "HalconDlSeg",
            SamTrainPage => "SamOnnx",
            HalconShapeModelPage => "HalconShapeModel",
            _ => "Flow"
        };

        private void SaveNavigationTab(Page? activePage = null)
        {
            try
            {
                Page page = activePage ?? (MainFrame.Content as Page) ?? _flowHostPage;
                var s = new AppNavigationUiSettings { LastTab = TabKeyFromPage(page) };
                s.Save();
            }
            catch
            {
                // 非关键
            }
        }

        private void RestoreLastNavigationTab()
        {
            string tab = AppNavigationUiSettings.Load().LastTab;
            switch (tab)
            {
                case "Plc":
                    NavigateTo(_plcPage);
                    HighlightTab("Plc");
                    break;
                case "Controller":
                    NavigateTo(_controllerLightPage);
                    HighlightTab("Controller");
                    break;
                case "Histogram":
                    NavigateTo(_histogramPage);
                    HighlightTab("Histogram");
                    break;
                case "YoloSeg":
                    NavigateTo(_yoloSegTrainPage);
                    HighlightTab("YoloSeg");
                    break;
                case "HalconDlSeg":
                    NavigateTo(_halconDlSegPage);
                    HighlightTab("HalconDlSeg");
                    break;
                case "SamOnnx":
                    NavigateTo(_samTrainPage);
                    HighlightTab("SamOnnx");
                    break;
                case "HalconShapeModel":
                    NavigateTo(_halconShapeModelPage);
                    HighlightTab("HalconShapeModel");
                    _halconShapeModelPage.RestoreSessionOnShow();
                    break;
                default:
                    NavigateTo(_flowHostPage);
                    HighlightTab("Flow");
                    break;
            }
        }

        private void HighlightTab(string tab)
        {
            var dim = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            var accent = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
            NavPlc.Background = dim;
            NavController.Background = dim;
            NavFlow.Background = dim;
            NavHalconShapeModel.Background = dim;
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
                case "HalconShapeModel":
                    NavHalconShapeModel.Background = accent;
                    break;
                case "Histogram":
                case "YoloSeg":
                case "HalconDlSeg":
                case "SamOnnx":
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
            // 不在此处自动加载 last_flow：流程页实例常驻内存，切回时应保留当前编辑内容。
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
            _halconShapeModelPage.RestoreSessionOnShow();
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

            SaveFlowSession();
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

        private static string GetAppDataRecordPath(string fileName)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppProduct.AppDataFolderName);
            return Path.Combine(dir, fileName);
        }

        private static void EnsureAppDataDirectory()
        {
            string? parent = Path.GetDirectoryName(GetAppDataRecordPath(OpenFlowTabsFileName));
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
        }

        private void SaveFlowSession()
        {
            try
            {
                var snapshot = _flowHostPage.GetSessionSnapshot();
                EnsureAppDataDirectory();

                var dto = new FlowTabsSessionFile
                {
                    ActiveIndex = snapshot.ActiveIndex,
                    Tabs = snapshot.Tabs.ToList()
                };
                File.WriteAllText(
                    GetAppDataRecordPath(OpenFlowTabsFileName),
                    JsonSerializer.Serialize(dto, FlowSessionJsonOptions));

                string? activePath = snapshot.ActiveIndex >= 0 && snapshot.ActiveIndex < snapshot.Tabs.Count
                    ? snapshot.Tabs[snapshot.ActiveIndex]
                    : snapshot.Tabs.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                SaveLastFlowPath(activePath);
            }
            catch
            {
                // 非关键流程，忽略持久化失败
            }
        }

        private static void SaveLastFlowPath(string? flowPath)
        {
            if (string.IsNullOrWhiteSpace(flowPath)) return;
            try
            {
                string full = Path.GetFullPath(flowPath);
                EnsureAppDataDirectory();
                File.WriteAllText(GetAppDataRecordPath(LastFlowFileName), full);
            }
            catch
            {
                // 非关键流程，忽略持久化失败
            }
        }

        private static FlowTabsSessionFile? TryReadFlowSession()
        {
            try
            {
                string recordPath = GetAppDataRecordPath(OpenFlowTabsFileName);
                if (File.Exists(recordPath))
                {
                    var dto = JsonSerializer.Deserialize<FlowTabsSessionFile>(File.ReadAllText(recordPath), FlowSessionJsonOptions);
                    if (dto?.Tabs != null && dto.Tabs.Count > 0)
                        return dto;
                }

                string? legacy = TryReadLastFlowPath();
                if (!string.IsNullOrWhiteSpace(legacy))
                {
                    return new FlowTabsSessionFile
                    {
                        ActiveIndex = 0,
                        Tabs = new List<string?> { legacy }
                    };
                }
            }
            catch
            {
                // ignored
            }

            return null;
        }

        private static string? TryReadLastFlowPath()
        {
            try
            {
                string recordPath = GetAppDataRecordPath(LastFlowFileName);
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

        private void TryRestoreFlowSessionOnStartup()
        {
            var session = TryReadFlowSession();
            if (session == null)
            {
                _flowHostPage.RestoreOpenFlows(Array.Empty<string?>(), 0);
                return;
            }

            _flowHostPage.RestoreOpenFlows(session.Tabs, session.ActiveIndex);
        }
    }
}
