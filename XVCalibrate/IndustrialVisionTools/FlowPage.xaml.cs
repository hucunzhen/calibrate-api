using System;
using System.Globalization;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Numerics;
using Microsoft.Win32;
using CalibOperatorPInvoke;
using HslCommunication;
using HslCommunication.ModBus;
using HslCommunication.Profinet.XINJE;
#if HALCON_ENABLED
using HalconDotNet;
#endif

// 流程页分文件：算子目录 FlowPage.OperatorCatalog.cs；画布模型 FlowPage.CanvasModels.cs；
// 流程编排 JSON DTO FlowPage.FlowDocumentJson.cs；标定几何 FlowPage.CalibrationGeometry.cs。

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage : UserControl
    {
        public string? CurrentFlowFilePath { get; private set; }

        /// <summary>无文件路径且无节点/连线，用于启动时是否可自动恢复 last_flow。</summary>
        public bool IsPristineEmptyDocument =>
            string.IsNullOrWhiteSpace(CurrentFlowFilePath) && _nodes.Count == 0 && _connections.Count == 0;

        public event Action<string?>? FlowLoaded;

        /// <summary>若宿主支持多标签，返回 true 表示已在其它标签打开路径；否则走当前页加载。</summary>
        public Func<string, bool>? TryLoadFlowInNewTab { get; set; }

        /// <summary>若宿主支持多标签，返回 true 表示已新建空白标签并切换；否则由当前页自行重置。</summary>
        public Func<bool>? RequestNewEmptyFlowTab { get; set; }

        /// <summary>
        /// CLI <c>--flow</c> 自动执行时为 true：流程日志里标记为错误的行同时写入标准错误输出。
        /// </summary>
        public bool MirrorErrorsToStderr { get; set; }

        /// <summary>
        /// CLI 后台跑 Flow 时为 true：将「实际使用的引擎路径」简要写入标准输出，便于确认 Native 是否成功或未回退托管。
        /// </summary>
        public bool TraceEnginePathToConsole { get; set; }

        /// <summary>Smoke test / CLI 诊断：读取当前流程日志文本。</summary>
        public string GetFlowExecutionLogText() =>
            LogBox.Dispatcher.CheckAccess()
                ? LogBox.Text
                : LogBox.Dispatcher.Invoke(() => LogBox.Text);

        // ================================================================
        // 页面状态
        // ================================================================

        /// <summary>SAM/OWLv2 相关 Flow 参数在 UI 解析时的上限（防误填）；需要更多路数可调大该常量。</summary>
        private const int FlowSamMaskMergeParamUpperBound = 4096;

        private const int FlowSamTextMaxDetectionsUpperBound = 4096;

        private readonly List<FlowNode> _nodes = new List<FlowNode>();
        private readonly List<FlowConnection> _connections = new List<FlowConnection>();
        private readonly ObservableCollection<ToolboxGroup> _toolboxGroups = new ObservableCollection<ToolboxGroup>();
        /// <summary>完整工具箱目录（用于筛选时克隆子集到 <see cref="_toolboxGroups"/>）。</summary>
        private readonly List<ToolboxGroup> _toolboxCatalog = new List<ToolboxGroup>();

        // 拖拽状态
        private bool _isDraggingNode;
        private FlowNode? _dragNode;
        private Point _dragStart;
        private Point _nodeStartPos;

        // 连线状态
        private PortVisual? _connectingFromPort;
        private Path? _tempConnectionPath;

        private sealed class LivePreviewSlot
        {
            public Window Window { get; init; } = null!;
            public System.Windows.Controls.Image ImageCtrl { get; init; } = null!;
        }

        /// <summary>连线快捷预览等非「显示图像」算子共用同一窗口。</summary>
        private const string LivePreviewSingletonSlotKey = "__flow_singleton_preview__";

        private readonly Dictionary<string, LivePreviewSlot> _livePreviewBySlot = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Trajectory3DPreviewWindow> _livePreview3dBySlot = new(StringComparer.Ordinal);
        private System.Threading.CancellationTokenSource? _runCts;
        private bool _isRunInProgress;
        private bool _isPanningCanvas;
        private Point _canvasPanStart;
        private double _canvasPanX0;
        private double _canvasPanY0;
        /// <summary>右键落在算子/连线上时不立刻平移，避免抢走 ContextMenu；超过阈值后才平移。</summary>
        private bool _canvasRightPanDeferred;
        private bool _canvasRightPanCommitted;
        private Point _canvasRightPanDownCanvasPoint;
        private const double CanvasRightPanThresholdSquared = 36.0;
        private readonly TranslateTransform _canvasTranslate = new TranslateTransform(0, 0);
        private readonly ScaleTransform _canvasScale = new ScaleTransform(1, 1);
        private readonly TransformGroup _canvasTransform = new TransformGroup();
        private XinJETcpNet? _flowPlc;
        private bool _flowPlcConnected;
        private static PlcConfig? _flowPlcConfig;
        /// <summary>组合算子执行子流程时，子图内已连线的 (节点Id, 输出端口名)。</summary>
        private HashSet<(Guid NodeId, string Port)>? _compositeInnerWiredOutputs;
        /// <summary>send_plc 分批下发时已在批内执行过的下游节点，主流程调度跳过避免重复。</summary>
        private HashSet<Guid>? _skipFlowRunNodeIds;
        private Guid _sendPlcDownstreamChainSourceId;
        private List<FlowNode>? _sendPlcDownstreamChainCache;
        /// <summary>flow_loop / Mask 循环嵌套深度；&gt;0 时 flow_sink / points_sink 跨轮累积。</summary>
        private int _flowLoopSinkAccumulateDepth;
        /// <summary>最近一次 camera_snap / load_image 的全图，供精匹配 FullImage（域内图 In 无法单独做透视 .dfm 变形轮廓）。</summary>
        private CalibImage? _recentFullFrameForHalconFineMatch;

        private bool FlowLoopSinkAccumulateActive => _flowLoopSinkAccumulateDepth > 0;

        private void EnterFlowLoopSinkAccumulate() => _flowLoopSinkAccumulateDepth++;

        private void ExitFlowLoopSinkAccumulate()
        {
            if (_flowLoopSinkAccumulateDepth > 0)
                _flowLoopSinkAccumulateDepth--;
        }

        private static PlcConfig LoadFlowPlcConfig()
        {
            if (_flowPlcConfig != null)
                return _flowPlcConfig;
            try
            {
                string configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plc_config.json");
                if (System.IO.File.Exists(configPath))
                {
                    string json = System.IO.File.ReadAllText(configPath);
                    _flowPlcConfig = JsonSerializer.Deserialize<PlcConfig>(json) ?? new PlcConfig();
                }
                else
                    _flowPlcConfig = new PlcConfig();
            }
            catch
            {
                _flowPlcConfig = new PlcConfig();
            }

            return _flowPlcConfig;
        }

        private static string NormalizeFlowDAddress(string raw)
        {
            string s = (raw ?? "").Trim();
            if (string.IsNullOrEmpty(s))
                throw new InvalidOperationException("无效的 PLC 寄存器地址");
            if (int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int n))
                s = "D" + n;
            if (XinjePlcAddress.IsHdAddress(s))
                throw new InvalidOperationException($"流程 PLC 仅支持 D 区信捷地址，不支持 HD: {raw}");
            return PlcXinjeHelper.NormalizeWordAddress(s, out _);
        }

        private static string FormatFlowPlcOperateFailure(OperateResult result)
        {
            if (result == null)
                return "无响应";
            string msg = string.IsNullOrWhiteSpace(result.Message)
                ? "无详细消息（常见：Modbus 超时、寄存器越界、连接已断开）"
                : result.Message.Trim();
            if (result.ErrorCode != 0)
                msg += $" (ErrorCode={result.ErrorCode})";
            return msg;
        }

        private bool FlowReadPlcBit(string xinjeAddr, int bitIndex)
        {
            XinJETcpNet plc = RequireFlowPlcD();
            string word = PlcXinjeHelper.NormalizeWordAddress(xinjeAddr, out int wordBitOffset);
            int finalBit = bitIndex + wordBitOffset;

            var readResult = plc.ReadUInt16(word);
            if (!readResult.IsSuccess)
                throw new InvalidOperationException($"PLC 读位失败({xinjeAddr}): {readResult.Message}");

            return (readResult.Content & (1 << finalBit)) != 0;
        }

        private void FlowWritePlcBit(string xinjeAddr, int bitIndex, bool set)
        {
            XinJETcpNet plc = RequireFlowPlcD();
            string word = PlcXinjeHelper.NormalizeWordAddress(xinjeAddr, out int wordBitOffset);
            int finalBit = bitIndex + wordBitOffset;

            var readResult = plc.ReadUInt16(word);
            if (!readResult.IsSuccess)
                throw new InvalidOperationException($"PLC 读位失败({xinjeAddr}): {readResult.Message}");

            ushort val = readResult.Content;
            if (set)
                val |= (ushort)(1 << finalBit);
            else
                val &= (ushort)~(1 << finalBit);

            var writeResult = plc.Write(word, val);
            if (!writeResult.IsSuccess)
                throw new InvalidOperationException($"PLC 写位失败({xinjeAddr}): {writeResult.Message}");
        }

        private static string ResolveFlowPlcRegister(string? paramValue, string configKey, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(paramValue))
                return paramValue.Trim();
            var cfg = LoadFlowPlcConfig();
            if (cfg.Registers != null &&
                cfg.Registers.TryGetValue(configKey, out var reg) &&
                !string.IsNullOrWhiteSpace(reg))
                return reg.Trim();
            return fallback;
        }

        /// <summary>轮询直到指定位为 1；超时或取消则失败。</summary>
        private bool FlowWaitPlcBit(string xinjeAddr, int bitIndex, int timeoutMs, int pollIntervalMs)
        {
            timeoutMs = Math.Max(0, timeoutMs);
            pollIntervalMs = Math.Clamp(pollIntervalMs, 5, 5000);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var token = _runCts?.Token ?? System.Threading.CancellationToken.None;

            while (sw.ElapsedMilliseconds <= timeoutMs)
            {
                token.ThrowIfCancellationRequested();
                if (FlowReadPlcBit(xinjeAddr, bitIndex))
                    return true;
                if (sw.ElapsedMilliseconds >= timeoutMs)
                    break;
                System.Threading.Thread.Sleep(pollIntervalMs);
            }

            return false;
        }

        /// <summary>流程专用连接优先；否则复用 PLC 页已连接的信捷 D 客户端（与 PlcPage GVAR 写入同通道）。</summary>
        private XinJETcpNet RequireFlowPlcD()
        {
            if (_flowPlc != null && _flowPlcConnected)
                return _flowPlc;
            if (PlcXinjeSession.Active != null)
                return PlcXinjeSession.Active;
            throw new InvalidOperationException(
                "PLC 未连接：请先执行「PLC连接」算子，或在 PLC 页点击连接。");
        }

        private XinJETcpNet EnsureFlowPlcConnectedFromNodeParams(IReadOnlyDictionary<string, string> paramBag)
        {
            if (_flowPlc != null && _flowPlcConnected)
                return _flowPlc;
            if (PlcXinjeSession.Active != null)
                return PlcXinjeSession.Active;

            string ip = (paramBag.GetValueOrDefault("ip", "192.168.6.6") ?? "192.168.6.6").Trim();
            int port = int.TryParse(paramBag.GetValueOrDefault("port"), out var p) ? p : 502;

            var cfg = LoadFlowPlcConfig();
            byte station = (byte)Math.Clamp(cfg.ModbusStation, 0, 255);
            string? stParam = paramBag.GetValueOrDefault("station");
            if (!string.IsNullOrWhiteSpace(stParam) && byte.TryParse(stParam.Trim(), out var st))
                station = st;

            string series = cfg.GvarList?.PlcSeries ?? "XD";
            string floatFmt = PlcXinjeHelper.ResolveFloatDataFormatString(cfg);
            _flowPlc = PlcXinjeHelper.CreateClient(series, ip, port, station, floatFmt);
            var conn = _flowPlc.ConnectServer();
            if (!conn.IsSuccess)
                throw new InvalidOperationException($"PLC连接失败: {conn.Message}");

            _flowPlcConnected = true;
            PlcXinjeSession.Register(_flowPlc, "Flow");
            return _flowPlc;
        }

        private bool DisconnectFlowOwnedPlc()
        {
            if (_flowPlc == null)
                return false;
            PlcXinjeSession.ClearIfOwnedBy(_flowPlc);
            try { _flowPlc.ConnectClose(); } catch { }
            _flowPlc = null;
            _flowPlcConnected = false;
            return true;
        }

        private const int MaxFlowUndoSteps = 80;
        private static readonly JsonSerializerOptions FlowSnapshotJsonOptions = new JsonSerializerOptions { WriteIndented = false };

        /// <summary>剪贴板自定义格式（另同步写入文本 JSON，便于外部编辑器粘贴）。</summary>
        private const string FlowClipboardDataFormat = "application/x-calibrate-flow-nodes+json";
        private readonly List<string> _flowUndoStack = new List<string>();
        private readonly List<string> _flowRedoStack = new List<string>();
        private bool _suppressFlowUndoRecording;
        private string? _pendingDragUndoSnapshotJson;

        public FlowPage()
        {
            InitializeComponent();
            InitializeToolbox();
            _canvasTransform.Children.Add(_canvasScale);
            _canvasTransform.Children.Add(_canvasTranslate);
            // 与 Scale→Translate 的增量公式一致；默认中心原点会导致缩放偏移并露出外层背景
            FlowCanvas.RenderTransformOrigin = new Point(0, 0);
            FlowCanvas.RenderTransform = _canvasTransform;
            if (StopRunButton != null) StopRunButton.IsEnabled = false;
            RefreshFlowUndoRedoButtons();
        }

        private void ThrowIfExecutionCancelled()
        {
            if (_runCts?.IsCancellationRequested == true)
                throw new OperationCanceledException("用户停止执行");
        }

        /// <summary>已在 UI 提示用户（如 MessageBox），托管 Flow 应中止且不当作未处理崩溃。</summary>
        private sealed class FlowExecutionGracefulStopException : Exception
        {
            public FlowExecutionGracefulStopException(string message, Exception? innerException = null)
                : base(message, innerException)
            {
            }
        }

        /// <summary>
        /// OpenFileDialog 须在 UI 线程。
        /// </summary>
        private static bool ExecuteNodeRequiresUiDispatcher(FlowNode node)
        {
            if (node.Def.TypeId == "load_image")
            {
                string configuredPath = node.Params.GetValueOrDefault("filePath", "")?.Trim() ?? "";
                return string.IsNullOrWhiteSpace(configuredPath);
            }

            if (node.Def.TypeId == "load_image_dir")
            {
                string configuredDir = node.Params.GetValueOrDefault("directory", "")?.Trim() ?? "";
                return string.IsNullOrWhiteSpace(configuredDir);
            }

            if (node.Def.TypeId is "calibrate" or "calibrate_homography")
                return CalibrateRequiresCorrespondenceDialog(node) || CalibrateUsesManualPixelPick(node);

            return false;
        }

        /// <summary>
        /// SAM 等依赖 System.Drawing/GDI+ 的算子须在 STA 后台线程执行；HALCON 等走 <see cref="HalconComputeRunner"/>（MTA + 全局门闩）。
        /// </summary>
        private static bool ExecuteNodeRequiresStaApartment(FlowNode node) =>
            node.Def.TypeId == "sam_onnx_segment";

#if HALCON_ENABLED
        private static bool IsHalconFlowNode(FlowNode node) =>
            node.Def.TypeId.StartsWith("halcon_", StringComparison.OrdinalIgnoreCase);

        private static HalconThreadPolicy ResolveHalconThreadPolicy(FlowNode node) =>
            node.Def.TypeId switch
            {
                "halcon_find_shape_model" => HalconThreadPolicy.ParallelFind,
                "halcon_coarse_shape_match" => HalconThreadPolicy.ParallelFind,
                "halcon_fine_deformable_match" => HalconThreadPolicy.ParallelFind,
                "halcon_fine_scaled_shape_match" => HalconThreadPolicy.ParallelFind,
                "halcon_coarse_fine_shape_match" => HalconThreadPolicy.ParallelFind,
                _ => HalconThreadPolicy.Geometry,
            };
#endif

        private static System.Threading.Tasks.TaskScheduler ResolveNodeExecutionScheduler(FlowNode node) =>
            ExecuteNodeRequiresStaApartment(node)
                ? FlowStaTaskScheduler.Default
                : System.Threading.Tasks.TaskScheduler.Default;

        private static bool CalibrateRequiresCorrespondenceDialog(FlowNode node)
        {
            string v = node.Params.GetValueOrDefault("confirmCorrespondence", "true")?.Trim() ?? "true";
            return !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) && v != "0";
        }

        private static bool CalibrateUsesManualPixelPick(FlowNode node)
        {
            string mode = node.Params.GetValueOrDefault("pixelPickMode", "manual")?.Trim() ?? "manual";
            if (string.Equals(mode, "manual", StringComparison.OrdinalIgnoreCase)
                || mode == "手选" || mode == "手选像素")
                return true;
            if (string.Equals(mode, "detected", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "detect", StringComparison.OrdinalIgnoreCase)
                || mode == "匹配检测点")
                return false;
            return false;
        }

        /// <summary>解析 channels 参数，格式 ch:value;ch:value，如 1:200;2:150。action=multi 时使用。</summary>
        private static int ApplyLightChannelSpec(ControllerSdkSession light, string spec)
        {
            if (string.IsNullOrWhiteSpace(spec))
                throw new InvalidOperationException("光源控制(multi): channels 为空，示例 1:200;2:150");

            int count = 0;
            foreach (var part in spec.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                int sep = p.IndexOf(':');
                if (sep < 0)
                    sep = p.IndexOf('=');
                if (sep < 0)
                    throw new InvalidOperationException($"光源控制(multi): 无效段 '{p}'，应为 通道:亮度");

                string chStr = p[..sep].Trim();
                string valStr = p[(sep + 1)..].Trim();
                if (!int.TryParse(chStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch))
                    throw new InvalidOperationException($"光源控制(multi): 无效通道 '{chStr}'");
                if (!int.TryParse(valStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                    throw new InvalidOperationException($"光源控制(multi): 无效亮度 '{valStr}'");

                light.SetDigitalValue(Math.Clamp(ch, 1, 64), val);
                count++;
            }

            if (count == 0)
                throw new InvalidOperationException("光源控制(multi): 未解析到任何通道");
            return count;
        }

        private static bool CalibrateNeedsCorrespondenceDialog(FlowNode node, Point2D[]? imagePts, int worldCount)
        {
            if (CalibrateUsesManualPixelPick(node) || CalibrateRequiresCorrespondenceDialog(node))
                return true;

            string mode = node.Params.GetValueOrDefault("pixelPickMode", "manual")?.Trim() ?? "manual";
            if (string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase))
                return imagePts == null || imagePts.Length != worldCount;

            return false;
        }

        /// <summary>解析循环次数：≤0 / inf / 无限 → 无限循环；否则为有限次数（至少 1）。</summary>
        private static void ParseFlowLoopCountParam(string? raw, out int repeatCount, out bool infinite)
        {
            string s = (raw ?? "").Trim();
            infinite = false;
            if (string.IsNullOrEmpty(s))
            {
                repeatCount = 3;
                return;
            }

            if (string.Equals(s, "inf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "infinite", StringComparison.OrdinalIgnoreCase)
                || s == "∞" || s == "无限")
            {
                infinite = true;
                repeatCount = 0;
                return;
            }

            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out repeatCount))
            {
                repeatCount = 3;
                return;
            }

            if (repeatCount <= 0)
            {
                infinite = true;
                repeatCount = 0;
                return;
            }
        }

        private static double[]? TryParseFlowLoopStepValues(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var list = new List<double>();
            foreach (var part in raw.Split(new[] { ';', ',', '\n', '\r', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                if (string.IsNullOrEmpty(p))
                    continue;
                if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    || double.TryParse(p, NumberStyles.Float, CultureInfo.CurrentCulture, out d))
                    list.Add(d);
                else if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
                    list.Add(iv);
                else
                    throw new InvalidOperationException($"循环 stepValues: 无效数值 '{p}'");
            }

            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>解析循环计划：若 stepValues 非空则固定次数=列表长度，且不可为无限循环。</summary>
        private static void ResolveFlowLoopSchedule(
            string? countRaw,
            string? stepValuesRaw,
            out int repeatCount,
            out bool infinite,
            out double[]? stepValues)
        {
            ParseFlowLoopCountParam(countRaw, out repeatCount, out infinite);
            stepValues = TryParseFlowLoopStepValues(stepValuesRaw);
            if (stepValues == null || stepValues.Length == 0)
                return;

            if (infinite)
                throw new InvalidOperationException("循环: 已填写 stepValues 时不能使用无限循环，请将 count 设为正整数或留空 stepValues");

            repeatCount = stepValues.Length;
            infinite = false;
        }

        private static readonly char[] FlowLoopItemPortSeparators = { ',', ';', '\r', '\n', '\t', ' ' };

        private static readonly HashSet<string> FlowLoopReservedPortNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "After", "Out", "Index", "Count", "StepValue"
        };

        private static List<string> ParseFlowLoopItemPortNames(string? raw)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                foreach (var token in raw.Split(FlowLoopItemPortSeparators, StringSplitOptions.RemoveEmptyEntries))
                {
                    string name = token.Trim();
                    if (name.EndsWith("List", StringComparison.OrdinalIgnoreCase) && name.Length > 4)
                        name = name[..^4].Trim();
                    if (string.IsNullOrWhiteSpace(name) || FlowLoopReservedPortNames.Contains(name))
                        continue;
                    if (seen.Add(name))
                        names.Add(name);
                }
            }

            if (names.Count == 0)
                names.Add("In");
            return names;
        }

        private static List<(string InputPort, string OutputPort)> BuildFlowLoopListPortMaps(IReadOnlyDictionary<string, string> paramBag)
        {
            const string defaultItemPorts = "In,In2,In3,In4,CoarseRow,CoarseColumn,CoarseAngle,CoarseScale,CoarseScore";
            string raw = paramBag.GetValueOrDefault("itemPorts", defaultItemPorts) ?? defaultItemPorts;
            var names = ParseFlowLoopItemPortNames(raw);
            return names.Select(n => ($"{n}List", n)).ToList();
        }

        private static readonly HashSet<string> ListPickReservedPortNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "AtIndex", "Index", "Count"
        };

        private static List<string> ParseListPickItemPortNames(string? raw)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                foreach (var token in raw.Split(FlowLoopItemPortSeparators, StringSplitOptions.RemoveEmptyEntries))
                {
                    string name = token.Trim();
                    if (name.EndsWith("List", StringComparison.OrdinalIgnoreCase) && name.Length > 4)
                        name = name[..^4].Trim();
                    if (string.IsNullOrWhiteSpace(name) || ListPickReservedPortNames.Contains(name))
                        continue;
                    if (seen.Add(name))
                        names.Add(name);
                }
            }

            if (names.Count == 0)
                names.Add("In");
            return names;
        }

        private static List<(string InputPort, string OutputPort)> BuildListPickListPortMaps(IReadOnlyDictionary<string, string> paramBag)
        {
            const string defaultItemPorts = "In,In2,In3";
            string raw = paramBag.GetValueOrDefault("itemPorts", defaultItemPorts) ?? defaultItemPorts;
            var names = ParseListPickItemPortNames(raw);
            return names.Select(n => ($"{n}List", n)).ToList();
        }

        private static string GetFlowLoopPortColorHex(string baseName)
        {
            if (baseName.StartsWith("CoarseRow", StringComparison.OrdinalIgnoreCase))
                return "#8BC34A";
            if (baseName.StartsWith("CoarseColumn", StringComparison.OrdinalIgnoreCase))
                return "#03A9F4";
            if (baseName.StartsWith("CoarseAngle", StringComparison.OrdinalIgnoreCase))
                return "#FF9800";
            if (baseName.StartsWith("CoarseScale", StringComparison.OrdinalIgnoreCase))
                return "#26A69A";
            if (baseName.StartsWith("CoarseScore", StringComparison.OrdinalIgnoreCase))
                return "#FFEB3B";
            if (baseName.StartsWith("In", StringComparison.OrdinalIgnoreCase))
                return "#FF9800";
            return "#90A4AE";
        }

        private static IReadOnlyList<PortDef> BuildFlowLoopDynamicPorts(FlowNode node)
        {
            var listPairs = BuildFlowLoopListPortMaps(node.Params);
            var ports = new List<PortDef>(node.Def.Ports);
            var inputNames = new HashSet<string>(ports.Where(p => p.Direction == PortDirection.Input).Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            var outputNames = new HashSet<string>(ports.Where(p => p.Direction == PortDirection.Output).Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var (inputPort, outputPort) in listPairs)
            {
                string c = GetFlowLoopPortColorHex(outputPort);
                if (inputNames.Add(inputPort))
                {
                    ports.Add(new PortDef
                    {
                        Name = inputPort,
                        Direction = PortDirection.Input,
                        DataType = typeof(object),
                        ColorHex = c,
                        IsOptional = true
                    });
                }

                if (outputNames.Add(outputPort))
                {
                    ports.Add(new PortDef
                    {
                        Name = outputPort,
                        Direction = PortDirection.Output,
                        DataType = typeof(object),
                        ColorHex = c,
                        IsOptional = true
                    });
                }
            }

            return ports;
        }

        private static IReadOnlyList<PortDef> BuildListPickDynamicPorts(FlowNode node)
        {
            var listPairs = BuildListPickListPortMaps(node.Params);
            var ports = new List<PortDef>(node.Def.Ports);
            var inputNames = new HashSet<string>(ports.Where(p => p.Direction == PortDirection.Input).Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            var outputNames = new HashSet<string>(ports.Where(p => p.Direction == PortDirection.Output).Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var (inputPort, outputPort) in listPairs)
            {
                string c = GetFlowLoopPortColorHex(outputPort);
                if (inputNames.Add(inputPort))
                {
                    ports.Add(new PortDef
                    {
                        Name = inputPort,
                        Direction = PortDirection.Input,
                        DataType = typeof(object),
                        ColorHex = c,
                        IsOptional = true
                    });
                }

                if (outputNames.Add(outputPort))
                {
                    ports.Add(new PortDef
                    {
                        Name = outputPort,
                        Direction = PortDirection.Output,
                        DataType = typeof(object),
                        ColorHex = c,
                        IsOptional = true
                    });
                }
            }

            return ports;
        }

        private static bool TryGetFlowLoopListLength(object? obj, out int length)
        {
            length = 0;
            switch (obj)
            {
                case null:
                    return false;
                case CalibImage[] arrImg:
                    length = arrImg.Length;
                    return true;
                case List<CalibImage> listImg:
                    length = listImg.Count;
                    return true;
                case double[] arrD:
                    length = arrD.Length;
                    return true;
                case List<double> listD:
                    length = listD.Count;
                    return true;
                case float[] arrF:
                    length = arrF.Length;
                    return true;
                case List<float> listF:
                    length = listF.Count;
                    return true;
                case int[] arrI:
                    length = arrI.Length;
                    return true;
                case List<int> listI:
                    length = listI.Count;
                    return true;
                case long[] arrL:
                    length = arrL.Length;
                    return true;
                case List<long> listL:
                    length = listL.Count;
                    return true;
                case List<object?> listObj:
                    length = listObj.Count;
                    return true;
                case object[] arrObj:
                    length = arrObj.Length;
                    return true;
                case Point2D[] arrPts:
                    length = arrPts.Length;
                    return true;
                case List<Point2D> listPts:
                    length = listPts.Count;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryGetFlowLoopListValueAt(object? obj, int index, out object? value)
        {
            value = null;
            if (index < 0)
                return false;
            switch (obj)
            {
                case CalibImage[] arrImg when index < arrImg.Length:
                    value = arrImg[index];
                    return true;
                case List<CalibImage> listImg when index < listImg.Count:
                    value = listImg[index];
                    return true;
                case double[] arrD when index < arrD.Length:
                    value = arrD[index];
                    return true;
                case List<double> listD when index < listD.Count:
                    value = listD[index];
                    return true;
                case float[] arrF when index < arrF.Length:
                    value = arrF[index];
                    return true;
                case List<float> listF when index < listF.Count:
                    value = listF[index];
                    return true;
                case int[] arrI when index < arrI.Length:
                    value = arrI[index];
                    return true;
                case List<int> listI when index < listI.Count:
                    value = listI[index];
                    return true;
                case long[] arrL when index < arrL.Length:
                    value = arrL[index];
                    return true;
                case List<long> listL when index < listL.Count:
                    value = listL[index];
                    return true;
                case List<object?> listObj when index < listObj.Count:
                    value = listObj[index];
                    return true;
                case object[] arrObj when index < arrObj.Length:
                    value = arrObj[index];
                    return true;
                case Point2D[] arrPts when index < arrPts.Length:
                    value = arrPts[index];
                    return true;
                case List<Point2D> listPts when index < listPts.Count:
                    value = listPts[index];
                    return true;
                default:
                    return false;
            }
        }

        private static int ResolveListPickIndex(IReadOnlyDictionary<string, object?> inputs, IReadOnlyDictionary<string, string> paramBag)
        {
            if (inputs.TryGetValue("AtIndex", out var idxObj) && idxObj != null)
            {
                switch (idxObj)
                {
                    case int i:
                        return i;
                    case long l:
                        return l > int.MaxValue ? int.MaxValue : l < int.MinValue ? int.MinValue : (int)l;
                    case double d:
                        return (int)d;
                    case float f:
                        return (int)f;
                }
            }

            if (int.TryParse(
                    paramBag.GetValueOrDefault("index", "0"),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int parsed))
                return parsed;
            return 0;
        }

        private static void ApplyListPickOutputs(
            FlowNode node,
            IReadOnlyDictionary<string, object?> inputs,
            int pickIndex,
            IReadOnlyList<(string InputPort, string OutputPort)> listPortMaps)
        {
            var connected = new List<(string InputPort, string OutputPort, int Length)>();
            foreach (var (inputPort, outputPort) in listPortMaps)
            {
                if (!inputs.TryGetValue(inputPort, out var obj) || obj == null)
                    continue;
                if (!TryGetFlowLoopListLength(obj, out int len))
                    throw new InvalidOperationException($"列表取项: {inputPort} 不是支持的列表类型");
                connected.Add((inputPort, outputPort, len));
            }

            if (connected.Count == 0)
                throw new InvalidOperationException("列表取项: 至少连接一路列表输入（如 InList）");

            var activeLengths = connected.Where(c => c.Length > 0).Select(c => c.Length).ToList();
            int listCount = activeLengths.Count > 0 ? activeLengths[0] : 0;
            if (activeLengths.Count > 1 && !activeLengths.All(l => l == listCount))
            {
                string detail = string.Join(", ", connected.Select(c => $"{c.InputPort}={c.Length}"));
                throw new InvalidOperationException($"列表取项: 多路列表长度不一致（{detail}）");
            }

            node.Outputs["Count"] = listCount;
            node.Outputs["Index"] = pickIndex;

            if (listCount == 0)
            {
                foreach (var (_, outputPort, _) in connected)
                    node.Outputs[outputPort] = null;
                return;
            }

            if (pickIndex < 0 || pickIndex >= listCount)
                throw new InvalidOperationException($"列表取项: 索引 {pickIndex} 超出范围 [0, {listCount - 1}]");

            foreach (var (inputPort, outputPort, _) in connected)
            {
                if (!inputs.TryGetValue(inputPort, out var obj))
                    continue;
                if (!TryGetFlowLoopListValueAt(obj, pickIndex, out var item))
                    throw new InvalidOperationException($"列表取项: 无法读取 {inputPort}[{pickIndex}]");
                node.Outputs[outputPort] = item;
            }
        }

        private static Dictionary<string, int> CollectFlowLoopListLengths(
            IReadOnlyDictionary<string, object?> inputs,
            IReadOnlyList<(string InputPort, string OutputPort)> listPortMaps)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (inputPort, _) in listPortMaps)
            {
                if (!inputs.TryGetValue(inputPort, out var obj) || !TryGetFlowLoopListLength(obj, out int len))
                    continue;
                map[inputPort] = len;
            }

            return map;
        }

        private static void ApplyFlowLoopListOutputs(
            FlowNode loopNode,
            IReadOnlyDictionary<string, object?> inputs,
            int roundIndex,
            IReadOnlyList<(string InputPort, string OutputPort)> listPortMaps)
        {
            foreach (var (inputPort, outputPort) in listPortMaps)
            {
                if (!inputs.TryGetValue(inputPort, out var obj))
                    continue;
                if (!TryGetFlowLoopListValueAt(obj, roundIndex, out var item))
                    continue;
                loopNode.Outputs[outputPort] = item;
                if (string.Equals(outputPort, "In", StringComparison.Ordinal))
                    loopNode.Outputs["Out"] = item;
            }
        }

        private static int ResolveLightControlValue(Dictionary<string, object?> inputs, IReadOnlyDictionary<string, string> parameters)
        {
            if (inputs.TryGetValue("Value", out var valObj) && valObj != null)
            {
                switch (valObj)
                {
                    case int vi:
                        return vi;
                    case long vl:
                        return (int)Math.Clamp(vl, int.MinValue, int.MaxValue);
                    case float vf:
                        return (int)Math.Round(vf);
                    case double vd:
                        return (int)Math.Round(vd);
                    default:
                        if (int.TryParse(valObj.ToString()?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                            return parsed;
                        if (double.TryParse(valObj.ToString()?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pd))
                            return (int)Math.Round(pd);
                        throw new InvalidOperationException($"光源控制: Value 输入无法解析为整数: {valObj}");
                }
            }

            return int.TryParse(parameters.GetValueOrDefault("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v
                : 0;
        }

        private async System.Threading.Tasks.Task ExecuteNodeForRunAsync(
            FlowNode node,
            string? timingScope = null,
            Dictionary<string, object?>? explicitInputs = null)
        {
            ThrowIfExecutionCancelled();
            var sw = Stopwatch.StartNew();
            try
            {
                if (ExecuteNodeRequiresUiDispatcher(node))
                {
                    await System.Threading.Tasks.Task.Yield();
                    ExecuteNode(node, explicitInputs);
                    return;
                }

#if HALCON_ENABLED
                if (IsHalconFlowNode(node))
                {
                    await HalconComputeRunner.RunAsync(
                        () => ExecuteNode(node, explicitInputs, halconGateEntered: true),
                        ResolveHalconThreadPolicy(node),
                        _runCts?.Token ?? System.Threading.CancellationToken.None);
                    return;
                }
#endif

                await System.Threading.Tasks.Task.Factory.StartNew(
                    () =>
                    {
                        ThrowIfExecutionCancelled();
                        ExecuteNode(node, explicitInputs);
                    },
                    _runCts?.Token ?? System.Threading.CancellationToken.None,
                    System.Threading.Tasks.TaskCreationOptions.None,
                    ResolveNodeExecutionScheduler(node));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FlowExecutionGracefulStopException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new FlowExecutionGracefulStopException(
                    $"节点「{node.Def.DisplayName}」执行失败，流程已中止。",
                    ex);
            }
            finally
            {
                sw.Stop();
                LogOperatorTiming(node, sw.Elapsed.TotalMilliseconds, timingScope);
            }
        }

        private void InitializeToolbox()
        {
            string[] categoryOrder =
            {
                "输入",
                "AI模型",
                "预处理",
                "后处理",
                "验证",
                "检测",
                "流程",
                "标定",
                "光源",
                "输出",
                "可视化",
                "HALCON"
            };

            _toolboxCatalog.Clear();
            var grouped = OperatorRegistry
                .GroupBy(o => o.Category ?? "")
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.DisplayName).ToList());

            foreach (var category in categoryOrder)
            {
                if (!grouped.TryGetValue(category, out var ops) || ops.Count == 0) continue;
                var group = new ToolboxGroup { Name = category, IsExpanded = true };
                foreach (var op in ops) group.Operators.Add(op);
                _toolboxCatalog.Add(group);
                grouped.Remove(category);
            }

            foreach (var kv in grouped.OrderBy(k => k.Key))
            {
                var group = new ToolboxGroup { Name = kv.Key, IsExpanded = true };
                foreach (var op in kv.Value) group.Operators.Add(op);
                _toolboxCatalog.Add(group);
            }

            ApplyToolboxFilter(ToolboxFilterBox?.Text);
            OperatorToolbox.ItemsSource = _toolboxGroups;
        }

        private static ToolboxGroup CloneToolboxGroup(ToolboxGroup src)
        {
            var ng = new ToolboxGroup { Name = src.Name, IsExpanded = src.IsExpanded };
            foreach (var op in src.Operators) ng.Operators.Add(op);
            return ng;
        }

        private void ApplyToolboxFilter(string? rawQuery)
        {
            var q = (rawQuery ?? "").Trim();
            _toolboxGroups.Clear();
            if (string.IsNullOrEmpty(q))
            {
                foreach (var src in _toolboxCatalog)
                    _toolboxGroups.Add(CloneToolboxGroup(src));
                return;
            }

            foreach (var src in _toolboxCatalog)
            {
                var matching = src.Operators.Where(op =>
                    (op.DisplayName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (op.TypeId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (!string.IsNullOrEmpty(op.Description) && op.Description.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
                if (matching.Count == 0) continue;
                var ng = new ToolboxGroup { Name = src.Name, IsExpanded = true };
                foreach (var op in matching) ng.Operators.Add(op);
                _toolboxGroups.Add(ng);
            }
        }

        private void ToolboxFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyToolboxFilter(ToolboxFilterBox?.Text);
        }

        // ================================================================
        // 从工具箱拖拽到画布
        // ================================================================

        private void OperatorItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is OperatorDef def)
            {
                var data = new DataObject("OperatorDef", def);
                DragDrop.DoDragDrop(fe, data, DragDropEffects.Copy);
            }
        }

        private void FlowCanvas_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("OperatorDef"))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void FlowCanvas_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("OperatorDef")) return;

            var def = (OperatorDef)e.Data.GetData("OperatorDef");
            var pos = e.GetPosition(FlowCanvas);
            PushFlowUndoSnapshotBeforeChange();
            AddNode(def, pos.X - def.DefaultWidth / 2, pos.Y - def.DefaultHeight / 2);
            e.Handled = true;
        }

        // ================================================================
        // 节点创建
        // ================================================================

        private FlowNode AddNode(OperatorDef def, double x, double y, Guid? restoreId = null)
        {
            var node = new FlowNode(def, x, y, restoreId);
            _nodes.Add(node);
            CreateNodeVisual(node);
            return node;
        }

        private static readonly char[] CompositePortListSeparators = { ',', ';', '\r', '\n', '\t', ' ' };

        private static IReadOnlyList<PortDef> GetNodePortDefinitions(FlowNode node)
        {
            if (string.Equals(node.Def.TypeId, "composite", StringComparison.Ordinal))
                return BuildCompositeExternalPorts(node.Params);
            if (string.Equals(node.Def.TypeId, "flow_loop", StringComparison.Ordinal))
                return BuildFlowLoopDynamicPorts(node);
            if (string.Equals(node.Def.TypeId, "list_pick", StringComparison.Ordinal))
                return BuildListPickDynamicPorts(node);
            return node.Def.Ports;
        }

        private static List<PortDef> BuildCompositeExternalPorts(IReadOnlyDictionary<string, string> paramBag)
        {
            string? rawInputs = null;
            if (paramBag.TryGetValue("inputPorts", out var vIn))
                rawInputs = vIn;
            string? rawOutputs = null;
            if (paramBag.TryGetValue("outputPorts", out var vOut))
                rawOutputs = vOut;

            var inputNames = ParseCompositeExternalPortNames(rawInputs, isInput: true);
            var outputNames = ParseCompositeExternalPortNames(rawOutputs, isInput: false);

            var ports = new List<PortDef>(inputNames.Count + outputNames.Count);
            foreach (string name in inputNames)
            {
                ports.Add(new PortDef
                {
                    Name = name,
                    Direction = PortDirection.Input,
                    DataType = typeof(object),
                    ColorHex = "#607D8B"
                });
            }

            for (int i = 0; i < outputNames.Count; i++)
            {
                ports.Add(new PortDef
                {
                    Name = outputNames[i],
                    Direction = PortDirection.Output,
                    DataType = typeof(object),
                    ColorHex = i == 1 ? "#78909C" : "#607D8B"
                });
            }

            return ports;
        }

        private static List<string> ParseCompositeExternalPortNames(string? raw, bool isInput)
        {
            var fallback = isInput
                ? new[] { "In" }
                : new[] { "Out", "Out2" };
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parts = raw.Split(CompositePortListSeparators, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    string name = p.Trim();
                    if (name.Length == 0 || !seen.Add(name))
                        continue;
                    names.Add(name);
                }
            }

            if (names.Count == 0)
                names.AddRange(fallback);
            return names;
        }

        private static string BuildPortSchemaSignature(IEnumerable<PortDef> ports) =>
            string.Join("|", ports.Select(p => $"{p.Direction}:{p.Name}"));

        private static string BuildCurrentNodeVisualPortSchemaSignature(FlowNode node) =>
            string.Join("|", node.PortVisuals.Select(pv => $"{pv.Definition.Direction}:{pv.Definition.Name}"));

        private static string BuildDesiredNodePortSchemaSignature(FlowNode node) =>
            BuildPortSchemaSignature(GetNodePortDefinitions(node));

        private static bool NodeUsesDynamicPorts(FlowNode node) =>
            string.Equals(node.Def.TypeId, "composite", StringComparison.Ordinal)
            || string.Equals(node.Def.TypeId, "flow_loop", StringComparison.Ordinal)
            || string.Equals(node.Def.TypeId, "list_pick", StringComparison.Ordinal);

        private void EnsureDynamicNodePortLayout(FlowNode node)
        {
            if (!NodeUsesDynamicPorts(node))
                return;

            string current = BuildCurrentNodeVisualPortSchemaSignature(node);
            string desired = BuildDesiredNodePortSchemaSignature(node);
            if (string.Equals(current, desired, StringComparison.Ordinal))
            {
                if (string.Equals(node.Def.TypeId, "composite", StringComparison.Ordinal))
                    RefreshCompositeNodeCaption(node);
                return;
            }

            RebuildNodeVisualAndReconnect(node);
        }

        private void RebuildNodeVisualAndReconnect(FlowNode node)
        {
            var affected = _connections
                .Where(c => c.FromPort.Owner == node || c.ToPort.Owner == node)
                .ToList();
            var reconnect = new List<(Guid OtherNodeId, string OtherPort, PortDirection OtherDir, string ThisPort, PortDirection ThisDir)>();
            foreach (var c in affected)
            {
                if (c.FromPort.Owner == node)
                {
                    reconnect.Add((c.ToPort.Owner.Id, c.ToPort.Definition.Name, PortDirection.Input, c.FromPort.Definition.Name, PortDirection.Output));
                }
                else
                {
                    reconnect.Add((c.FromPort.Owner.Id, c.FromPort.Definition.Name, PortDirection.Output, c.ToPort.Definition.Name, PortDirection.Input));
                }

                if (c.PathVisual != null)
                    FlowCanvas.Children.Remove(c.PathVisual);
                _connections.Remove(c);
            }

            if (node.Visual != null)
                FlowCanvas.Children.Remove(node.Visual);
            node.Visual = null;
            node.CompositeCaptionText = null;
            node.PortVisuals.Clear();

            CreateNodeVisual(node);

            foreach (var r in reconnect)
            {
                var thisPort = node.PortVisuals.FirstOrDefault(p =>
                    p.Definition.Direction == r.ThisDir &&
                    string.Equals(p.Definition.Name, r.ThisPort, StringComparison.Ordinal));
                var otherNode = _nodes.FirstOrDefault(n => n.Id == r.OtherNodeId);
                if (thisPort == null || otherNode == null)
                    continue;
                var otherPort = otherNode.PortVisuals.FirstOrDefault(p =>
                    p.Definition.Direction == r.OtherDir &&
                    string.Equals(p.Definition.Name, r.OtherPort, StringComparison.Ordinal));
                if (otherPort == null)
                    continue;

                var from = thisPort.Definition.Direction == PortDirection.Output ? thisPort : otherPort;
                var to = thisPort.Definition.Direction == PortDirection.Input ? thisPort : otherPort;
                if (CanConnect(from, to))
                    CreateConnectionCore(from, to, pushUndoSnapshot: false);
            }

            UpdatePortPositions(node);
            UpdateAllConnections();
        }

        private void CreateNodeVisual(FlowNode node)
        {
            var def = node.Def;
            var ports = GetNodePortDefinitions(node);
            double w = def.DefaultWidth;
            int inputCount = ports.Count(p => p.Direction == PortDirection.Input);
            int outputCount = ports.Count(p => p.Direction == PortDirection.Output);
            int rowCount = Math.Max(inputCount, outputCount);
            if (rowCount == 0) rowCount = 1;
            string? capStrForHeight = null;
            if (def.TypeId == "composite")
                capStrForHeight = FormatCompositeNodeSubtitleStatic(node);
            bool showCompositeCaption = def.TypeId == "composite" && !string.IsNullOrWhiteSpace(capStrForHeight);
            // 标题+端口区+摘要的最小自适应高度，避免新增端口被裁切
            double minAutoHeight = 52 + rowCount * 18 + 18 + (showCompositeCaption ? 16 : 0);
            double h = Math.Max(def.DefaultHeight, minAutoHeight);

            // 主容器
            var border = new Border
            {
                Width = w,
                Height = h,
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Tag = node
            };
            border.MouseLeftButtonDown += Node_MouseLeftButtonDown;

            // 右键菜单
            var menuCopy = new MenuItem { Header = "复制算子" };
            menuCopy.Click += (_, _) => CopyNodesToClipboard(new[] { node });

            var menuPaste = new MenuItem { Header = "粘贴算子" };
            menuPaste.Click += (_, _) => PasteNodesFromClipboard(node, 120, 120);

            var menuDelete = new MenuItem { Header = "删除算子" };
            menuDelete.Click += (s, e) => DeleteNode(node);

            var menuParams = new MenuItem { Header = "算子配置面板" };
            menuParams.Click += (s, e) => EditNodeParams(node);
            var menuEnable = new MenuItem { Header = "启用节点" };
            menuEnable.Click += (_, _) => SetNodeEnabledFromContextMenu(node, enabled: true);
            var menuDisable = new MenuItem { Header = "禁用节点" };
            menuDisable.Click += (_, _) => SetNodeEnabledFromContextMenu(node, enabled: false);

            var menuCompositeVars = new MenuItem { Header = "查看子流程变量" };
            menuCompositeVars.Click += (_, _) => ShowCompositeVariablesWindow(node);

            var menuOpenInnerFlow = new MenuItem { Header = "打开子流程（新标签）" };
            menuOpenInnerFlow.Click += (_, _) => OpenCompositeInnerFlowInNewTab(node);

            var menuRunTo = new MenuItem { Header = "执行到此节点（含上游）" };
            menuRunTo.Click += (_, _) => { _ = RunUpstreamToNodeAsync(node); };

            var menuPreviewPts = new MenuItem { Header = "预览点位轨迹" };

            var ctx = new ContextMenu();
            ctx.Items.Add(menuCopy);
            ctx.Items.Add(menuPaste);
            ctx.Items.Add(new Separator());
            ctx.Items.Add(menuEnable);
            ctx.Items.Add(menuDisable);
            ctx.Items.Add(menuParams);
            if (def.TypeId == "composite")
            {
                ctx.Items.Add(menuOpenInnerFlow);
                ctx.Items.Add(menuCompositeVars);
            }
            ctx.Items.Add(menuRunTo);
            ctx.Items.Add(menuPreviewPts);
            ctx.Items.Add(menuDelete);
            ctx.Opened += (_, _) =>
            {
                bool enabled = IsNodeEnabled(node);
                menuEnable.IsEnabled = !enabled;
                menuDisable.IsEnabled = enabled;
                menuPreviewPts.Items.Clear();
                foreach (var kv in node.Outputs)
                {
                    if (kv.Value is Point2D[] pa)
                    {
                        string hdr = pa.Length == 0 ? $"{kv.Key} (0 点)" : $"{kv.Key} ({pa.Length} 点)";
                        var mi = new MenuItem { Header = hdr, IsEnabled = pa.Length > 0 };
                        if (pa.Length > 0)
                        {
                            Point2D[] snapshot = (Point2D[])pa.Clone();
                            string port = kv.Key;
                            var barFromNode = TryGetBarIdsForPointPort(node, port, pa.Length);
                            var barSnap = barFromNode == null ? null : (int[])barFromNode.Clone();
                            string? join = DefaultPointLineJoinModeForPreview(node.Def.TypeId, port);
                            mi.Click += (_, _) =>
                                ShowPointsPolylinePreview(snapshot, $"{node.Def.DisplayName} · {port}", barSnap, join);
                        }
                        menuPreviewPts.Items.Add(mi);
                    }
                    else if (kv.Value is CalibPoint3D[] p3)
                    {
                        string hdr = p3.Length == 0 ? $"{kv.Key} (0 点·3D)" : $"{kv.Key} ({p3.Length} 点·3D)";
                        var mi = new MenuItem { Header = hdr, IsEnabled = p3.Length > 0 };
                        if (p3.Length > 0)
                        {
                            CalibPoint3D[] snapshot = (CalibPoint3D[])p3.Clone();
                            string port = kv.Key;
                            var barFromNode = TryGetBarIdsForPointPort(node, port, p3.Length);
                            var barSnap = barFromNode == null ? null : (int[])barFromNode.Clone();
                            string? join = DefaultPointLineJoinModeForPreview(node.Def.TypeId, port);
                            mi.Click += (_, _) =>
                            {
                                var xy = snapshot.Select(p => new Point2D(p.X, p.Y)).ToArray();
                                ShowPointsPolylinePreview(xy, $"{node.Def.DisplayName} · {port} (XY投影)", barSnap, join);
                            };
                        }
                        menuPreviewPts.Items.Add(mi);
                    }
                }
                menuPreviewPts.Visibility = menuPreviewPts.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            };

            border.ContextMenu = ctx;

            var panel = new StackPanel { Tag = node };

            // 标题栏
            var titleBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x37, 0x37, 0x3D)),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Padding = new Thickness(8, 4, 8, 4),
                Tag = node
            };
            var titleText = new TextBlock
            {
                Text = def.DisplayName,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };
            if (def.TypeId == "composite")
            {
                var cap = new TextBlock
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)),
                    FontSize = 10,
                    FontWeight = FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = w - 16
                };
                node.CompositeCaptionText = cap;
                string capInit = FormatCompositeNodeSubtitleStatic(node);
                cap.Text = capInit;
                cap.Visibility = string.IsNullOrWhiteSpace(capInit) ? Visibility.Collapsed : Visibility.Visible;
                var titleStack = new StackPanel();
                titleStack.Children.Add(titleText);
                titleStack.Children.Add(cap);
                titleBorder.Child = titleStack;
            }
            else
            {
                titleBorder.Child = titleText;
            }
            panel.Children.Add(titleBorder);

            // 端口区域
            var portsGrid = new Grid { Margin = new Thickness(6, 4, 6, 4) };
            portsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            portsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int inputIdx = 0;
            int outputIdx = 0;

            for (int r = 0; r < rowCount; r++)
            {
                portsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            }

            foreach (var portDef in ports)
            {
                var portEllipse = new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(portDef.ColorHex)),
                    Fill = Brushes.Transparent,
                    StrokeThickness = 2,
                    Cursor = Cursors.Cross
                };

                var portVisual = new PortVisual(portDef, node, portEllipse);
                node.PortVisuals.Add(portVisual);
                portEllipse.Tag = portVisual;
                portEllipse.MouseLeftButtonDown += Port_MouseLeftButtonDown;

                var label = new TextBlock
                {
                    Text = portDef.Name,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var portPanel = new StackPanel { Orientation = Orientation.Horizontal };

                if (portDef.Direction == PortDirection.Input)
                {
                    portPanel.Children.Add(portEllipse);
                    portPanel.Children.Add(label);
                    Grid.SetColumn(portPanel, 0);
                    Grid.SetRow(portPanel, inputIdx);
                    portPanel.HorizontalAlignment = HorizontalAlignment.Left;
                    inputIdx++;
                }
                else
                {
                    label.HorizontalAlignment = HorizontalAlignment.Right;
                    portPanel.Children.Add(label);
                    portPanel.Children.Add(portEllipse);
                    Grid.SetColumn(portPanel, 1);
                    Grid.SetRow(portPanel, outputIdx);
                    portPanel.HorizontalAlignment = HorizontalAlignment.Right;
                    outputIdx++;
                }

                portsGrid.Children.Add(portPanel);
            }

            panel.Children.Add(portsGrid);

            // 结果摘要文本（执行后显示）
            var summaryText = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xCB, 0xC4)),  // 浅青色
                FontSize = 9,
                Margin = new Thickness(8, 2, 8, 4),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = w - 16,
                Tag = "SummaryText"
            };
            panel.Children.Add(summaryText);
            node.ResultSummary = null;

            border.Child = panel;

            Canvas.SetLeft(border, node.X);
            Canvas.SetTop(border, node.Y);
            FlowCanvas.Children.Add(border);
            node.Visual = border;

            if (def.TypeId == "composite")
                RefreshCompositeNodeCaption(node);
            else
                UpdatePortPositions(node);
        }

        private static string FormatCompositeNodeSubtitleStatic(FlowNode node)
        {
            string path = node.Params.GetValueOrDefault("innerFlowPath", "")?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    return System.IO.Path.GetFileName(path);
                }
                catch
                {
                    return path;
                }
            }

            string emb = node.Params.GetValueOrDefault("innerFlowJson", "")?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(emb))
                return "内嵌 JSON";
            return "";
        }

        private static double MeasureCompositeNodeMinHeight(FlowNode node)
        {
            var def = node.Def;
            var ports = GetNodePortDefinitions(node);
            int inputCount = ports.Count(p => p.Direction == PortDirection.Input);
            int outputCount = ports.Count(p => p.Direction == PortDirection.Output);
            int rowCount = Math.Max(Math.Max(inputCount, outputCount), 1);
            bool showCap = node.Def.TypeId == "composite" && !string.IsNullOrWhiteSpace(FormatCompositeNodeSubtitleStatic(node));
            double minAutoHeight = 52 + rowCount * 18 + 18 + (showCap ? 16 : 0);
            return Math.Max(def.DefaultHeight, minAutoHeight);
        }

        private void RefreshCompositeNodeCaption(FlowNode node)
        {
            if (node.Def.TypeId != "composite" || node.CompositeCaptionText == null) return;
            string cap = FormatCompositeNodeSubtitleStatic(node);
            node.CompositeCaptionText.Text = cap;
            node.CompositeCaptionText.Visibility = string.IsNullOrWhiteSpace(cap) ? Visibility.Collapsed : Visibility.Visible;

            if (node.Visual is Border b)
                b.Height = MeasureCompositeNodeMinHeight(node);

            UpdatePortPositions(node);
            UpdateAllConnections();
        }

        private void UpdatePortPositions(FlowNode node)
        {
            if (node.Visual == null) return;

            double nodeLeft = Canvas.GetLeft(node.Visual);
            double nodeTop = Canvas.GetTop(node.Visual);

            foreach (var pv in node.PortVisuals)
            {
                // 获取端口椭圆在画布坐标系中的位置
                var ellipseCenter = pv.Ellipse.TransformToVisual(FlowCanvas)
                    .Transform(new Point(pv.Ellipse.ActualWidth / 2, pv.Ellipse.ActualHeight / 2));
                pv.Center = ellipseCenter;
            }
        }

        // ================================================================
        // 节点拖拽
        // ================================================================

        private static FlowNode? FindFlowNodeFromElement(object? sender)
        {
            if (sender is not DependencyObject dep)
                return null;
            while (dep != null)
            {
                if (dep is FrameworkElement fe && fe.Tag is FlowNode node)
                    return node;
                dep = VisualTreeHelper.GetParent(dep);
            }

            return null;
        }

        private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                var flowNode = FindFlowNodeFromElement(sender);
                if (flowNode != null)
                {
                    if (flowNode.Def.TypeId == "composite")
                        ShowCompositeVariablesWindow(flowNode);
                    else if (flowNode.Def.Params.Count > 0)
                        EditNodeParams(flowNode);
                    e.Handled = true;
                }
                return;
            }

            if (FindFlowNodeFromElement(sender) is FlowNode node && node.Visual != null)
            {
                _isDraggingNode = true;
                _dragNode = node;
                _dragStart = e.GetPosition(FlowCanvas);
                _nodeStartPos = new Point(Canvas.GetLeft(node.Visual), Canvas.GetTop(node.Visual));
                _pendingDragUndoSnapshotJson = _suppressFlowUndoRecording ? null : SerializeFlowSnapshotCompact();
                node.Visual.CaptureMouse();
                e.Handled = true;
            }
        }

        // ================================================================
        // 端口连线交互
        // ================================================================

        private void Port_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Ellipse ellipse && ellipse.Tag is PortVisual pv)
            {
                // 开始连线
                _connectingFromPort = pv;
                ellipse.CaptureMouse();

                _tempConnectionPath = new Path
                {
                    Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pv.Definition.ColorHex)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 4, 4 },
                    Data = new PathGeometry()
                };
                FlowCanvas.Children.Add(_tempConnectionPath);
                e.Handled = true;
            }
        }

        // ================================================================
        // 画布鼠标事件（节点拖拽 + 连线绘制）
        // ================================================================

        private void FlowCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 点击空白处取消连线
            if (_connectingFromPort != null)
            {
                CancelConnection();
                e.Handled = true;
            }
        }

        private void FlowCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(FlowCanvas);

            // 画布平移（右键拖拽）
            if (_isPanningCanvas && e.RightButton == MouseButtonState.Pressed)
            {
                var screenPos = e.GetPosition(this);
                _canvasTranslate.X = _canvasPanX0 + (screenPos.X - _canvasPanStart.X);
                _canvasTranslate.Y = _canvasPanY0 + (screenPos.Y - _canvasPanStart.Y);
                e.Handled = true;
                return;
            }

            // 节点拖拽
            if (_isDraggingNode && _dragNode?.Visual != null)
            {
                double dx = pos.X - _dragStart.X;
                double dy = pos.Y - _dragStart.Y;
                double newX = Math.Max(0, _nodeStartPos.X + dx);
                double newY = Math.Max(0, _nodeStartPos.Y + dy);

                Canvas.SetLeft(_dragNode.Visual, newX);
                Canvas.SetTop(_dragNode.Visual, newY);
                _dragNode.X = newX;
                _dragNode.Y = newY;

                UpdatePortPositions(_dragNode);
                UpdateAllConnections();
                e.Handled = true;
            }

            // 连线拖拽
            if (_connectingFromPort != null && _tempConnectionPath != null)
            {
                UpdateTempConnection(pos);
                e.Handled = true;
            }
        }

        private void FlowCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 结束节点拖拽
            if (_isDraggingNode && _dragNode?.Visual != null)
            {
                var dragged = _dragNode;
                double curX = Canvas.GetLeft(dragged.Visual);
                double curY = Canvas.GetTop(dragged.Visual);
                double dx = curX - _nodeStartPos.X;
                double dy = curY - _nodeStartPos.Y;
                bool movedMeaningfully = dx * dx + dy * dy > 9;

                dragged.Visual.ReleaseMouseCapture();
                _isDraggingNode = false;
                _dragNode = null;

                if (movedMeaningfully && _pendingDragUndoSnapshotJson != null)
                    PushPreSerializedFlowUndoEntry(_pendingDragUndoSnapshotJson);
                _pendingDragUndoSnapshotJson = null;
            }
            else
                _pendingDragUndoSnapshotJson = null;

            // 结束连线（检查是否落在目标端口上）
            if (_connectingFromPort != null)
            {
                _connectingFromPort.Ellipse.ReleaseMouseCapture();
                var target = FindPortAtPosition(e.GetPosition(FlowCanvas));
                if (target != null && CanConnect(_connectingFromPort, target))
                {
                    CreateConnection(_connectingFromPort, target);
                }
                else
                {
                    CancelConnection();
                }
            }

            e.Handled = true;
        }

        private PortVisual? FindPortAtPosition(Point pos)
        {
            foreach (var node in _nodes)
            {
                foreach (var pv in node.PortVisuals)
                {
                    UpdatePortPositions(node);
                    double dx = pos.X - pv.Center.X;
                    double dy = pos.Y - pv.Center.Y;
                    if (dx * dx + dy * dy < 144) // 12px radius
                        return pv;
                }
            }
            return null;
        }

        private bool CanConnect(PortVisual from, PortVisual to)
        {
            // 不同方向才能连
            if (from.Definition.Direction == to.Definition.Direction) return false;
            // 不能自连
            if (from.Owner == to.Owner) return false;
            // 相同数据类型（object 端口可与任意类型互连，便于组合算子等）
            if (!PortTypesCompatible(from.Definition.DataType, to.Definition.DataType)) return false;
            // 不重复连线
            if (_connections.Any(c =>
                (c.FromPort == from && c.ToPort == to) ||
                (c.FromPort == to && c.ToPort == from))) return false;
            // 目标端口只能有一条输入连线
            if (to.Definition.Direction == PortDirection.Input &&
                _connections.Any(c => c.ToPort == to)) return false;

            return true;
        }

        private static bool PortTypesCompatible(Type a, Type b)
        {
            if (a == b) return true;
            if (a == typeof(object) || b == typeof(object)) return true;
            // 基座 3D 点列可连入仍声明为 Point2D[] 的端口（显示叠加取 XY；PLC 写 float 时写 X,Y）
            if (a == typeof(CalibPoint3D[]) && b == typeof(Point2D[])) return true;
            if (AreGenericListTypesCompatible(a, b)) return true;
            return false;
        }

        private static bool AreGenericListTypesCompatible(Type a, Type b)
        {
            if (!TryGetListElementType(a, out Type? elemA) || !TryGetListElementType(b, out Type? elemB))
                return false;
            if (elemA == elemB) return true;
            if (elemA == typeof(object) || elemB == typeof(object)) return true;
            return PortTypesCompatible(elemA!, elemB!);
        }

        private static bool TryGetListElementType(Type type, out Type? elementType)
        {
            elementType = null;
            if (!type.IsGenericType)
                return false;
            if (type.GetGenericTypeDefinition() != typeof(List<>))
                return false;
            elementType = type.GetGenericArguments()[0];
            return true;
        }

        /// <summary>匹配中心点列排序：返回原下标 permute，pts[k]=原 (cols[order[k]], rows[order[k]])。</summary>
        private static int[] SortShapeMatchCenterIndices(
            int n,
            double[] rows,
            double[] cols,
            int[]? gridRow,
            int[]? gridCol,
            string sortMode)
        {
            var order = Enumerable.Range(0, n).ToArray();
            if (n <= 1 || sortMode is "none" or "preserve")
                return order;

            bool canGrid = sortMode is "grid" or "grid_row_col"
                && gridRow != null && gridCol != null
                && gridRow.Length >= n && gridCol.Length >= n
                && gridRow.Take(n).All(r => r >= 0)
                && gridCol.Take(n).All(c => c >= 0);

            if (canGrid)
            {
                Array.Sort(order, (a, b) =>
                {
                    int cmp = gridRow![a].CompareTo(gridRow[b]);
                    return cmp != 0 ? cmp : gridCol![a].CompareTo(gridCol[b]);
                });
                return order;
            }

            if (sortMode is "xy" or "col_row" or "x_then_y")
            {
                Array.Sort(order, (a, b) =>
                {
                    int cmp = cols[a].CompareTo(cols[b]);
                    return cmp != 0 ? cmp : rows[a].CompareTo(rows[b]);
                });
                return order;
            }

            // yx / row_col / row_major：先行(Y)后列(X)，与九点世界坐标默认顺序一致
            Array.Sort(order, (a, b) =>
            {
                int cmp = rows[a].CompareTo(rows[b]);
                return cmp != 0 ? cmp : cols[a].CompareTo(cols[b]);
            });
            return order;
        }

        // ================================================================
        // 连线管理
        // ================================================================

        private void CreateConnection(PortVisual from, PortVisual to)
            => CreateConnectionCore(from, to, pushUndoSnapshot: true);

        private void CreateConnectionCore(PortVisual from, PortVisual to, bool pushUndoSnapshot)
        {
            if (pushUndoSnapshot)
                PushFlowUndoSnapshotBeforeChange();

            // 确保 from 是 output，to 是 input
            var outPort = from.Definition.Direction == PortDirection.Output ? from : to;
            var inPort = from.Definition.Direction == PortDirection.Input ? from : to;

            var conn = new FlowConnection(outPort, inPort);
            var path = new Path
            {
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(outPort.Definition.ColorHex)),
                StrokeThickness = 2,
                Data = new PathGeometry(),
                Tag = conn
            };
            path.MouseRightButtonDown += Connection_MouseRightButtonDown;
            path.MouseLeftButtonDown += Connection_MouseLeftButtonDown;

            conn.PathVisual = path;
            _connections.Add(conn);
            FlowCanvas.Children.Add(path);

            UpdateConnectionPath(conn);
            CleanupTempConnection();
        }

        private void UpdateConnectionPath(FlowConnection conn)
        {
            if (conn.PathVisual == null) return;

            UpdatePortPositions(conn.FromPort.Owner);
            UpdatePortPositions(conn.ToPort.Owner);

            var start = conn.FromPort.Center;
            var end = conn.ToPort.Center;
            conn.PathVisual.Data = CreateBezierGeometry(start, end);
        }

        private PathGeometry CreateBezierGeometry(Point start, Point end)
        {
            double dx = Math.Abs(end.X - start.X);
            double offset = Math.Max(dx * 0.5, 50);

            var fig = new PathFigure
            {
                StartPoint = start,
                IsClosed = false
            };

            var seg = new BezierSegment
            {
                Point1 = new Point(start.X + offset, start.Y),
                Point2 = new Point(end.X - offset, end.Y),
                Point3 = end
            };

            fig.Segments.Add(seg);

            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            return geo;
        }

        private void UpdateTempConnection(Point mousePos)
        {
            if (_tempConnectionPath == null || _connectingFromPort == null) return;

            UpdatePortPositions(_connectingFromPort.Owner);
            var start = _connectingFromPort.Center;
            var end = mousePos;

            if (_connectingFromPort.Definition.Direction == PortDirection.Output)
                _tempConnectionPath.Data = CreateBezierGeometry(start, end);
            else
                _tempConnectionPath.Data = CreateBezierGeometry(end, start);
        }

        private void CancelConnection()
        {
            CleanupTempConnection();
            _connectingFromPort = null;
        }

        private void CleanupTempConnection()
        {
            if (_tempConnectionPath != null)
            {
                FlowCanvas.Children.Remove(_tempConnectionPath);
                _tempConnectionPath = null;
            }
        }

        /// <summary>算子节点、连线：右键单击保留菜单/删除连线；移动超过阈值后才拖动画布。</summary>
        private static bool ShouldDeferCanvasRightPanImmediate(DependencyObject? src)
        {
            while (src != null)
            {
                if (src is Border b && b.Tag is FlowNode)
                    return true;
                if (src is Path p && p.Tag is FlowConnection)
                    return true;
                src = VisualTreeHelper.GetParent(src);
            }

            return false;
        }

        private void StartCanvasRightPan(Point screenPos)
        {
            _isPanningCanvas = true;
            _canvasPanStart = screenPos;
            _canvasPanX0 = _canvasTranslate.X;
            _canvasPanY0 = _canvasTranslate.Y;
            FlowCanvas.CaptureMouse();
            FlowCanvas.Cursor = Cursors.ScrollAll;
        }

        private void FlowCanvas_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            bool defer = ShouldDeferCanvasRightPanImmediate(e.OriginalSource as DependencyObject);
            _canvasRightPanDownCanvasPoint = e.GetPosition(FlowCanvas);
            _canvasRightPanDeferred = defer;
            _canvasRightPanCommitted = false;

            if (!defer)
            {
                StartCanvasRightPan(e.GetPosition(this));
                _canvasRightPanCommitted = true;
                e.Handled = true;
            }
        }

        private void FlowCanvas_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_canvasRightPanDeferred || _canvasRightPanCommitted || e.RightButton != MouseButtonState.Pressed)
                return;

            var pos = e.GetPosition(FlowCanvas);
            double dx = pos.X - _canvasRightPanDownCanvasPoint.X;
            double dy = pos.Y - _canvasRightPanDownCanvasPoint.Y;
            if (dx * dx + dy * dy >= CanvasRightPanThresholdSquared)
            {
                StartCanvasRightPan(Mouse.GetPosition(this));
                _canvasRightPanCommitted = true;
                _canvasRightPanDeferred = false;
                e.Handled = true;
            }
        }

        private void FlowCanvas_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanningCanvas)
            {
                _isPanningCanvas = false;
                FlowCanvas.ReleaseMouseCapture();
                FlowCanvas.Cursor = null;
                e.Handled = true;
            }

            _canvasRightPanDeferred = false;
            _canvasRightPanCommitted = false;
        }

        private void FlowCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double factor = e.Delta > 0 ? 1.12 : (1.0 / 1.12);
            double newScale = Math.Max(0.2, Math.Min(5.0, _canvasScale.ScaleX * factor));
            if (Math.Abs(newScale - _canvasScale.ScaleX) < 1e-9) return;

            // 以鼠标在画布逻辑坐标系下的点为锚点（与 RenderTransformOrigin 左上原点 + Scale→Translate 一致）
            var mouseCanvas = e.GetPosition(FlowCanvas);
            double parentX = _canvasScale.ScaleX * mouseCanvas.X + _canvasTranslate.X;
            double parentY = _canvasScale.ScaleY * mouseCanvas.Y + _canvasTranslate.Y;

            _canvasScale.ScaleX = newScale;
            _canvasScale.ScaleY = newScale;
            _canvasTranslate.X = parentX - newScale * mouseCanvas.X;
            _canvasTranslate.Y = parentY - newScale * mouseCanvas.Y;

            e.Handled = true;
        }

        private void UpdateAllConnections()
        {
            foreach (var conn in _connections)
                UpdateConnectionPath(conn);
        }

        private void Connection_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Path path && path.Tag is FlowConnection conn)
            {
                // 右键删除连线
                PushFlowUndoSnapshotBeforeChange();
                _connections.Remove(conn);
                if (conn.PathVisual != null)
                    FlowCanvas.Children.Remove(conn.PathVisual);
                e.Handled = true;
            }
        }

        private void Connection_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Path path || path.Tag is not FlowConnection conn) return;

            if (!conn.FromPort.Owner.Outputs.TryGetValue(conn.FromPort.Definition.Name, out var data) || data == null)
            {
                MessageBox.Show(
                    $"该连线上暂无可用数据。\n上游节点：{conn.FromPort.Owner.Def.DisplayName}\n端口：{conn.FromPort.Definition.Name}\n请先运行 Flow。",
                    "连线数据",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                e.Handled = true;
                return;
            }

            // 图像类型：直接弹窗预览
            if (data is CalibImage img)
            {
                ShowImagePreview(img);
                e.Handled = true;
                return;
            }

            // 点位类型：有同节点图像则叠加预览；否则纯轨迹图。auto 时仅多条条号才分段；「轮廓转焊道路径」的 Points 预览强制整条折线。
            if (data is Point2D[] pts)
            {
                var baseImg = conn.FromPort.Owner.Outputs.Values.OfType<CalibImage>().FirstOrDefault();
                var barIds = TryGetBarIdsForPointPort(conn.FromPort.Owner, conn.FromPort.Definition.Name, pts.Length);
                var barSnap = barIds == null ? null : (int[])barIds.Clone();
                string? joinMode = DefaultPointLineJoinModeForPreview(conn.FromPort.Owner.Def.TypeId, conn.FromPort.Definition.Name);
                if (baseImg != null)
                {
                    ShowImagePreview(baseImg, pts, 3, null, null, null, barSnap, joinMode, null);
                }
                else
                {
                    string title = $"点位轨迹 · {conn.FromPort.Owner.Def.DisplayName} · {conn.FromPort.Definition.Name}";
                    ShowPointsPolylinePreview(pts, title, barSnap, joinMode);
                }
                e.Handled = true;
                return;
            }

            if (data is CalibPoint3D[] pts3)
            {
                var baseImg = conn.FromPort.Owner.Outputs.Values.OfType<CalibImage>().FirstOrDefault();
                var barIds = TryGetBarIdsForPointPort(conn.FromPort.Owner, conn.FromPort.Definition.Name, pts3.Length);
                var barSnap = barIds == null ? null : (int[])barIds.Clone();
                string? joinMode = DefaultPointLineJoinModeForPreview(conn.FromPort.Owner.Def.TypeId, conn.FromPort.Definition.Name);
                var ptsXy = pts3.Select(p => new Point2D(p.X, p.Y)).ToArray();
                if (baseImg != null)
                {
                    ShowImagePreview(baseImg, ptsXy, 3, null, null, null, barSnap, joinMode, null);
                }
                else
                {
                    string title = $"点位轨迹(基座3D·XY投影) · {conn.FromPort.Owner.Def.DisplayName} · {conn.FromPort.Definition.Name}";
                    ShowPointsPolylinePreview(ptsXy, title, barSnap, joinMode);
                }
                e.Handled = true;
                return;
            }

            if (data is ValueTuple<int[], int[], int[], int> contours)
            {
                var baseImg = conn.FromPort.Owner.Outputs.Values.OfType<CalibImage>().FirstOrDefault();
                ShowContoursPreview(contours, baseImg);
                e.Handled = true;
                return;
            }

            if (data is ValueTuple<double[], double[], int[], int>)
            {
                MessageBox.Show(
                    BuildConnectionDataText(conn, data),
                    "连线数据 · 世界轮廓条带",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                e.Handled = true;
                return;
            }

            // 其他类型：结构化文本展示
            MessageBox.Show(
                BuildConnectionDataText(conn, data),
                "连线数据",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            e.Handled = true;
        }

        private static string BuildConnectionDataText(FlowConnection conn, object data)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"上游节点: {conn.FromPort.Owner.Def.DisplayName}");
            sb.AppendLine($"下游节点: {conn.ToPort.Owner.Def.DisplayName}");
            sb.AppendLine($"端口: {conn.FromPort.Definition.Name} -> {conn.ToPort.Definition.Name}");
            sb.AppendLine($"类型: {data.GetType().Name}");
            sb.AppendLine();
            sb.Append(FormatFlowPortValue(data));

            return sb.ToString();
        }

        // ================================================================
        // 工具栏按钮
        // ================================================================

        private void OpenFlowHelp_Click(object sender, RoutedEventArgs e)
        {
            string? readme = ResolveFlowHelpReadmePath(CurrentFlowFilePath);
            if (readme == null)
            {
                MessageBox.Show(
                    "未找到帮助文档。\n\n请在 flow 同目录放置 README.md，或打开仓库 flows/ 下的示例 flow。\n索引：flows/README.md",
                    "帮助",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = readme,
                    UseShellExecute = true
                });
                StatusText.Text = $"帮助: {System.IO.Path.GetFileName(readme)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开帮助文档:\n{readme}\n\n{ex.Message}", "帮助", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>flows 部署指南 → 当前 flow 同目录 README.md → flows 索引。</summary>
        private static string? ResolveFlowHelpReadmePath(string? flowFilePath)
        {
            static string? FirstExistingReadme(string dir)
            {
                if (string.IsNullOrWhiteSpace(dir) || !System.IO.Directory.Exists(dir))
                    return null;
                string direct = System.IO.Path.Combine(dir, "README.md");
                if (System.IO.File.Exists(direct))
                    return direct;
                if (string.Equals(System.IO.Path.GetFileName(dir), "halcon", StringComparison.OrdinalIgnoreCase))
                {
                    var halconDocs = System.IO.Directory.GetFiles(dir, "README*.md");
                    if (halconDocs.Length > 0)
                        return halconDocs[0];
                }
                return null;
            }

            static string? FindFlowsRootGuide(string? startDir)
            {
                string? dir = startDir;
                for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
                {
                    if (string.Equals(System.IO.Path.GetFileName(dir), "flows", StringComparison.OrdinalIgnoreCase))
                    {
                        string guide = System.IO.Path.Combine(dir, "README_workflow_guide.md");
                        if (System.IO.File.Exists(guide))
                            return guide;
                        string index = System.IO.Path.Combine(dir, "README.md");
                        if (System.IO.File.Exists(index))
                            return index;
                        return null;
                    }
                    dir = System.IO.Path.GetDirectoryName(dir);
                }
                return null;
            }

            if (!string.IsNullOrWhiteSpace(flowFilePath))
            {
                string flowDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(flowFilePath)) ?? "";
                var flowsGuide = FindFlowsRootGuide(flowDir);
                if (flowsGuide != null)
                    return flowsGuide;

                string? dir = flowDir;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
                {
                    var found = FirstExistingReadme(dir);
                    if (found != null)
                        return found;
                    dir = System.IO.Path.GetDirectoryName(dir);
                }
            }

            string? probe = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(probe); i++)
            {
                string flowsGuide = System.IO.Path.Combine(probe, "flows", "README_workflow_guide.md");
                if (System.IO.File.Exists(flowsGuide))
                    return flowsGuide;
                string flowsIndex = System.IO.Path.Combine(probe, "flows", "README.md");
                if (System.IO.File.Exists(flowsIndex))
                    return flowsIndex;
                probe = System.IO.Path.GetDirectoryName(probe);
            }

            return null;
        }

        // ================================================================
        // 保存 / 加载流程编排
        // ================================================================

        private void SaveFlow_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CurrentFlowFilePath))
            {
                SaveFlowAs_Click(sender, e);
                return;
            }

            try
            {
                WriteFlowToFile(CurrentFlowFilePath);
                StatusText.Text = $"已保存: {System.IO.Path.GetFileName(CurrentFlowFilePath)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveFlowAs_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "流程文件|*.flow.json|所有文件|*.*",
                DefaultExt = ".flow.json",
                Title = "流程另存为"
            };
            if (!string.IsNullOrWhiteSpace(CurrentFlowFilePath))
            {
                dlg.InitialDirectory = System.IO.Path.GetDirectoryName(CurrentFlowFilePath);
                dlg.FileName = System.IO.Path.GetFileName(CurrentFlowFilePath);
            }

            if (dlg.ShowDialog() != true) return;

            try
            {
                WriteFlowToFile(dlg.FileName);
                CurrentFlowFilePath = System.IO.Path.GetFullPath(dlg.FileName);
                FlowLoaded?.Invoke(CurrentFlowFilePath);
                StatusText.Text = $"已另存为: {System.IO.Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"另存为失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void WriteFlowToFile(string filePath)
        {
            var data = BuildCurrentFlowData();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(data, options);
            System.IO.File.WriteAllText(filePath, json);
        }

        private FlowData BuildCurrentFlowData()
        {
            var nodes = _nodes.Select(n => new FlowNodeData
            {
                Id = n.Id.ToString(),
                TypeId = n.Def.TypeId,
                X = n.X,
                Y = n.Y,
                Params = new Dictionary<string, string>(n.Params)
            }).ToList();

            var connections = _connections.Select(c => new FlowConnData
            {
                FromNodeId = c.FromPort.Owner.Id.ToString(),
                FromPort = c.FromPort.Definition.Name,
                ToNodeId = c.ToPort.Owner.Id.ToString(),
                ToPort = c.ToPort.Definition.Name
            }).ToList();

            PersistLatticeConfigFromUi();
            var data = new FlowData { Nodes = nodes, Connections = connections };
            MergeFlowMetaInto(data);
            return data;
        }

        private FlowData BuildClipboardFlowSubset(IReadOnlyList<FlowNode> nodes)
        {
            var set = new HashSet<FlowNode>(nodes);
            var nodeDatas = nodes
                .Select(n => new FlowNodeData
                {
                    Id = n.Id.ToString(),
                    TypeId = n.Def.TypeId,
                    X = n.X,
                    Y = n.Y,
                    Params = new Dictionary<string, string>(n.Params),
                })
                .ToList();
            var connections = _connections
                .Where(c => set.Contains(c.FromPort.Owner) && set.Contains(c.ToPort.Owner))
                .Select(c => new FlowConnData
                {
                    FromNodeId = c.FromPort.Owner.Id.ToString(),
                    FromPort = c.FromPort.Definition.Name,
                    ToNodeId = c.ToPort.Owner.Id.ToString(),
                    ToPort = c.ToPort.Definition.Name,
                })
                .ToList();
            return new FlowData { Nodes = nodeDatas, Connections = connections };
        }

        private void CopyNodesToClipboard(IReadOnlyList<FlowNode> nodes)
        {
            if (nodes == null || nodes.Count == 0) return;
            try
            {
                var payload = BuildClipboardFlowSubset(nodes);
                string json = JsonSerializer.Serialize(payload, FlowSnapshotJsonOptions);
                var pkg = new DataObject();
                pkg.SetData(FlowClipboardDataFormat, json);
                pkg.SetText(json);
                Clipboard.SetDataObject(pkg, copy: true);
                StatusText.Text = $"已复制 {nodes.Count} 个算子（可粘贴到本画布或其它实例）";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"复制失败: {ex.Message}", "复制算子", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static bool TryDeserializeClipboardFlow(string? json, out FlowData? data)
        {
            data = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var parsed = JsonSerializer.Deserialize<FlowData>(json);
                if (parsed?.Nodes == null || parsed.Nodes.Count == 0) return false;
                data = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryGetFlowDataFromClipboard(out FlowData? data)
        {
            data = null;
            try
            {
                IDataObject? clip = Clipboard.GetDataObject();
                if (clip == null) return false;
                string? json = clip.GetData(FlowClipboardDataFormat) as string;
                if (string.IsNullOrWhiteSpace(json) && clip.GetDataPresent(DataFormats.Text))
                    json = clip.GetData(DataFormats.UnicodeText) as string ?? clip.GetData(DataFormats.Text) as string;
                return TryDeserializeClipboardFlow(json, out data);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 粘贴剪贴板中的算子子图；<paramref name="anchorNode"/> 存在时以其左上角为参照偏移 (+40,+40)，否则使用画布坐标。
        /// </summary>
        private void PasteNodesFromClipboard(FlowNode? anchorNode, double fallbackCanvasX, double fallbackCanvasY)
        {
            if (!TryGetFlowDataFromClipboard(out var data) || data == null)
            {
                MessageBox.Show("剪贴板中没有可用的流程算子数据（需为本工具复制的 JSON）。", "粘贴算子", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var defLookup = OperatorRegistry.ToDictionary(d => d.TypeId);
            foreach (var nd in data.Nodes)
            {
                if (!defLookup.ContainsKey(nd.TypeId))
                {
                    MessageBox.Show($"剪贴板包含未知算子类型「{nd.TypeId}」，当前工具箱未注册。", "粘贴算子", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            double minX = data.Nodes.Min(n => n.X);
            double minY = data.Nodes.Min(n => n.Y);
            double anchorX = anchorNode != null ? anchorNode.X + 40 : fallbackCanvasX;
            double anchorY = anchorNode != null ? anchorNode.Y + 40 : fallbackCanvasY;
            double ox = anchorX - minX;
            double oy = anchorY - minY;

            PushFlowUndoSnapshotBeforeChange();

            var mapOldIdToNode = new Dictionary<string, FlowNode>(StringComparer.Ordinal);
            foreach (var nd in data.Nodes)
            {
                var def = defLookup[nd.TypeId];
                var fn = AddNode(def, nd.X + ox, nd.Y + oy, Guid.NewGuid());
                if (nd.Params != null)
                {
                    foreach (var kv in nd.Params)
                    {
                        if (fn.Params.ContainsKey(kv.Key))
                            fn.Params[kv.Key] = kv.Value;
                    }
                }

                if (NodeUsesDynamicPorts(fn))
                    EnsureDynamicNodePortLayout(fn);

                mapOldIdToNode[nd.Id] = fn;
            }

            var conns = data.Connections ?? new List<FlowConnData>();
            foreach (var cd in conns)
            {
                if (!mapOldIdToNode.TryGetValue(cd.FromNodeId, out var fromN)) continue;
                if (!mapOldIdToNode.TryGetValue(cd.ToNodeId, out var toN)) continue;
                var fromPort = fromN.PortVisuals.FirstOrDefault(p =>
                    p.Definition.Name == cd.FromPort && p.Definition.Direction == PortDirection.Output);
                string toPortName = NormalizeHoughLinesInputPort(toN, cd.ToPort);
                var toPort = toN.PortVisuals.FirstOrDefault(p =>
                    p.Definition.Name == toPortName && p.Definition.Direction == PortDirection.Input);
                if (fromPort != null && toPort != null && CanConnect(fromPort, toPort))
                    CreateConnection(fromPort, toPort);
            }

            FlowCanvas.UpdateLayout();
            foreach (var n in mapOldIdToNode.Values)
                UpdatePortPositions(n);
            UpdateAllConnections();
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    FlowCanvas.UpdateLayout();
                    foreach (var n in mapOldIdToNode.Values)
                        UpdatePortPositions(n);
                    UpdateAllConnections();
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);

            StatusText.Text = $"已粘贴 {data.Nodes.Count} 个算子";
        }

        private string SerializeFlowSnapshotCompact()
            => JsonSerializer.Serialize(BuildCurrentFlowData(), FlowSnapshotJsonOptions);

        private void TrimFlowUndoListFromOldest(List<string> list)
        {
            while (list.Count > MaxFlowUndoSteps)
                list.RemoveAt(0);
        }

        private void PushFlowUndoSnapshotBeforeChange()
        {
            if (_suppressFlowUndoRecording) return;
            _flowRedoStack.Clear();
            _flowUndoStack.Add(SerializeFlowSnapshotCompact());
            TrimFlowUndoListFromOldest(_flowUndoStack);
            RefreshFlowUndoRedoButtons();
        }

        private void PushPreSerializedFlowUndoEntry(string snapshotJson)
        {
            if (_suppressFlowUndoRecording) return;
            _flowRedoStack.Clear();
            _flowUndoStack.Add(snapshotJson);
            TrimFlowUndoListFromOldest(_flowUndoStack);
            RefreshFlowUndoRedoButtons();
        }

        private void RefreshFlowUndoRedoButtons()
        {
            if (UndoFlowButton != null) UndoFlowButton.IsEnabled = _flowUndoStack.Count > 0;
            if (RedoFlowButton != null) RedoFlowButton.IsEnabled = _flowRedoStack.Count > 0;
        }

        private void ClearCanvasCore()
        {
            foreach (var conn in _connections)
            {
                if (conn.PathVisual != null)
                    FlowCanvas.Children.Remove(conn.PathVisual);
            }
            _connections.Clear();

            foreach (var node in _nodes)
            {
                if (node.Visual != null)
                    FlowCanvas.Children.Remove(node.Visual);
            }
            _nodes.Clear();
        }

        private void PopulateCanvasFromFlowData(FlowData data)
        {
            var defLookup = OperatorRegistry.ToDictionary(d => d.TypeId);
            var nodeLookup = new Dictionary<string, FlowNode>();
            foreach (var nd in data.Nodes)
            {
                if (!defLookup.TryGetValue(nd.TypeId, out var def))
                    throw new Exception($"未知算子类型: {nd.TypeId}");
                Guid? restoreId = Guid.TryParse(nd.Id, out var gid) ? gid : null;
                var node = AddNode(def, nd.X, nd.Y, restoreId);
                if (nd.Params != null)
                {
                    foreach (var kv in nd.Params)
                    {
                        if (node.Params.ContainsKey(kv.Key))
                            node.Params[kv.Key] = kv.Value;
                    }
                }

                // CreateNodeVisual 在合并 JSON 参数之前执行，组合算子副标题依赖 innerFlowPath/innerFlowJson，此处再刷新一次。
                if (NodeUsesDynamicPorts(node))
                    EnsureDynamicNodePortLayout(node);

                nodeLookup[nd.Id] = node;
            }

            foreach (var cd in data.Connections)
            {
                if (!nodeLookup.TryGetValue(cd.FromNodeId, out var fromNode))
                    continue;
                if (!nodeLookup.TryGetValue(cd.ToNodeId, out var toNode))
                    continue;

                var fromPort = fromNode.PortVisuals.FirstOrDefault(p =>
                    p.Definition.Name == cd.FromPort && p.Definition.Direction == PortDirection.Output);
                string toPortName = NormalizeHoughLinesInputPort(toNode, cd.ToPort);
                var toPort = toNode.PortVisuals.FirstOrDefault(p =>
                    p.Definition.Name == toPortName && p.Definition.Direction == PortDirection.Input);

                if (fromPort != null && toPort != null && CanConnect(fromPort, toPort))
                    CreateConnection(fromPort, toPort);
            }

            FlowCanvas.UpdateLayout();
            foreach (var n in _nodes) UpdatePortPositions(n);
            UpdateAllConnections();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                FlowCanvas.UpdateLayout();
                foreach (var n in _nodes) UpdatePortPositions(n);
                UpdateAllConnections();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void RestoreFlowFromSnapshotJson(string json)
        {
            var data = JsonSerializer.Deserialize<FlowData>(json);
            if (data == null) return;
            _suppressFlowUndoRecording = true;
            try
            {
                ClearCanvasCore();
                PopulateCanvasFromFlowData(data);
                ApplyFlowMetaFromData(data);
                UpdateLatticeConfigUi();
                UpdateStandaloneDebugUi();
            }
            finally
            {
                _suppressFlowUndoRecording = false;
            }
        }

        private void UndoFlow_Click(object sender, RoutedEventArgs e) => PerformFlowUndo();

        private void RedoFlow_Click(object sender, RoutedEventArgs e) => PerformFlowRedo();

        private void FlowPage_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
            {
                // 不用 TextBoxBase（部分目标框架/引用下不可见）；排除常见文本编辑控件即可
                if (Keyboard.FocusedElement is TextBox or RichTextBox or PasswordBox)
                    return;
                var p = Mouse.GetPosition(FlowCanvas);
                PasteNodesFromClipboard(anchorNode: null, fallbackCanvasX: p.X, fallbackCanvasY: p.Y);
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
            {
                PerformFlowUndo();
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
            {
                PerformFlowRedo();
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z)
            {
                PerformFlowRedo();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F5)
            {
                if (Keyboard.FocusedElement is TextBox or RichTextBox or PasswordBox)
                    return;
                e.Handled = true;
                _ = RunAllAsync(clearLog: true, preferNativeEngine: false);
            }
        }

        private void PerformFlowUndo()
        {
            if (_flowUndoStack.Count == 0) return;
            var previous = _flowUndoStack[_flowUndoStack.Count - 1];
            _flowUndoStack.RemoveAt(_flowUndoStack.Count - 1);
            var current = SerializeFlowSnapshotCompact();
            _flowRedoStack.Add(current);
            TrimFlowUndoListFromOldest(_flowRedoStack);
            RestoreFlowFromSnapshotJson(previous);
            StatusText.Text = "已撤销";
            RefreshFlowUndoRedoButtons();
        }

        private void PerformFlowRedo()
        {
            if (_flowRedoStack.Count == 0) return;
            var next = _flowRedoStack[_flowRedoStack.Count - 1];
            _flowRedoStack.RemoveAt(_flowRedoStack.Count - 1);
            var current = SerializeFlowSnapshotCompact();
            _flowUndoStack.Add(current);
            TrimFlowUndoListFromOldest(_flowUndoStack);
            RestoreFlowFromSnapshotJson(next);
            StatusText.Text = "已重做";
            RefreshFlowUndoRedoButtons();
        }

        public bool LoadFlowFromFile(string filePath, bool showErrorDialog = true)
        {
            try
            {
                var json = System.IO.File.ReadAllText(filePath);
                var data = JsonSerializer.Deserialize<FlowData>(json);
                if (data == null) throw new Exception("文件内容为空");

                PushFlowUndoSnapshotBeforeChange();
                _suppressFlowUndoRecording = true;
                try
                {
                    ClearCanvasCore();
                    PopulateCanvasFromFlowData(data);
                }
                finally
                {
                    _suppressFlowUndoRecording = false;
                }
                _flowRedoStack.Clear();
                RefreshFlowUndoRedoButtons();

                CurrentFlowFilePath = System.IO.Path.GetFullPath(filePath);
                FlowLoaded?.Invoke(CurrentFlowFilePath);
                ApplyFlowMetaFromData(data);
                ApplyToolbarDefaultsFromStore();
                UpdateLatticeConfigUi();
                UpdateStandaloneDebugUi();
                if (IsStandaloneDebugActive)
                    StatusText.Text = $"已加载(子流程调试): {System.IO.Path.GetFileName(filePath)} ({data.Nodes.Count} 节点)";
                else
                    StatusText.Text = $"已加载: {System.IO.Path.GetFileName(filePath)} ({data.Nodes.Count} 节点, {data.Connections.Count} 连线)";
                return true;
            }
            catch (Exception ex)
            {
                if (showErrorDialog)
                    MessageBox.Show($"加载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = $"加载失败: {ex.Message}";
                AppendLog($"[ERROR] 加载流程失败: {ex.Message}", true);
                return false;
            }
        }

        private void LoadFlow_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "流程文件|*.flow.json|所有文件|*.*",
                DefaultExt = ".flow.json",
                Title = "加载流程"
            };
            if (dlg.ShowDialog() != true) return;
            LoadFlowFromFile(dlg.FileName, showErrorDialog: true);
        }

        private void LoadFlowNewTab_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "流程文件|*.flow.json|所有文件|*.*",
                DefaultExt = ".flow.json",
                Title = "在新标签打开流程"
            };
            if (dlg.ShowDialog() != true) return;
            if (TryLoadFlowInNewTab?.Invoke(dlg.FileName) == true)
                return;
            LoadFlowFromFile(dlg.FileName, showErrorDialog: true);
        }

        private void NewFlowTab_Click(object sender, RoutedEventArgs e)
        {
            if (RequestNewEmptyFlowTab?.Invoke() == true)
                return;
            PushFlowUndoSnapshotBeforeChange();
            ResetToEmptyDocument();
        }

        /// <summary>清空画布并重置路径（保留单标签时用于「关闭」语义）。</summary>
        public void ResetToEmptyDocument()
        {
            _suppressFlowUndoRecording = true;
            try
            {
                ClearCanvasCore();
                CurrentFlowFilePath = null;
                _flowMeta.Clear();
                ApplyToolbarDefaultsFromStore();
                UpdateLatticeConfigUi();
                UpdateStandaloneDebugUi();
                _flowUndoStack.Clear();
                _flowRedoStack.Clear();
                RefreshFlowUndoRedoButtons();
                StatusText.Text = "未命名";
                FlowLoaded?.Invoke(null);
            }
            finally
            {
                _suppressFlowUndoRecording = false;
            }
        }


        private void ClearCanvas_Click(object sender, RoutedEventArgs e)
        {
            PushFlowUndoSnapshotBeforeChange();
            ClearCanvasCore();
            StatusText.Text = "画布已清空";
            RefreshFlowUndoRedoButtons();
        }

        private void DeleteNode(FlowNode node)
        {
            PushFlowUndoSnapshotBeforeChange();

            // 删除与该节点相关的所有连线
            var toRemove = _connections.Where(c => c.FromPort.Owner == node || c.ToPort.Owner == node).ToList();
            foreach (var conn in toRemove)
            {
                if (conn.PathVisual != null)
                    FlowCanvas.Children.Remove(conn.PathVisual);
                _connections.Remove(conn);
            }

            // 删除节点视觉元素
            if (node.Visual != null)
                FlowCanvas.Children.Remove(node.Visual);
            _nodes.Remove(node);

            StatusText.Text = $"已删除: {node.Def.DisplayName}";
        }

        private void AutoLayout_Click(object sender, RoutedEventArgs e)
        {
            if (_nodes.Count == 0) return;

            PushFlowUndoSnapshotBeforeChange();

            // 拓扑排序 + 自动布局
            var sorted = TopologicalSort();

            double xBase = 60;
            double yBase = 40;
            double xStep = 240;
            double yStep = 120;

            // 分层：按拓扑层级排列
            var levels = new Dictionary<FlowNode, int>();
            var inDegree = new Dictionary<FlowNode, int>();

            foreach (var n in _nodes) inDegree[n] = 0;
            foreach (var c in _connections)
            {
                if (inDegree.ContainsKey(c.ToPort.Owner))
                    inDegree[c.ToPort.Owner]++;
            }

            var queue = new Queue<FlowNode>();
            foreach (var n in _nodes)
            {
                if (inDegree[n] == 0)
                {
                    levels[n] = 0;
                    queue.Enqueue(n);
                }
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var c in _connections)
                {
                    if (c.FromPort.Owner == current)
                    {
                        var target = c.ToPort.Owner;
                        int newLevel = levels[current] + 1;
                        if (!levels.ContainsKey(target) || levels[target] < newLevel)
                            levels[target] = newLevel;
                        inDegree[target]--;
                        if (inDegree[target] == 0 && !levels.ContainsKey(target))
                        {
                            levels[target] = newLevel;
                            queue.Enqueue(target);
                        }
                    }
                }
            }

            // 未分层的节点放到最后一层
            foreach (var n in _nodes)
            {
                if (!levels.ContainsKey(n))
                    levels[n] = levels.Values.DefaultIfEmpty(0).Max() + 1;
            }

            // 按层级分组
            var groups = _nodes.GroupBy(n => levels[n])
                              .OrderBy(g => g.Key)
                              .ToList();

            foreach (var group in groups)
            {
                int col = group.Key;
                for (int i = 0; i < group.Count(); i++)
                {
                    var n = group.ElementAt(i);
                    double newX = xBase + col * xStep;
                    double newY = yBase + i * yStep;

                    if (n.Visual != null)
                    {
                        Canvas.SetLeft(n.Visual, newX);
                        Canvas.SetTop(n.Visual, newY);
                    }
                    n.X = newX;
                    n.Y = newY;
                }
            }

            // 更新端口和连线
            foreach (var n in _nodes) UpdatePortPositions(n);
            UpdateAllConnections();

            StatusText.Text = "自动布局完成";
        }

        private List<FlowNode> TopologicalSort()
        {
            var visited = new HashSet<Guid>();
            var result = new List<FlowNode>();
            var inDegree = new Dictionary<Guid, int>();
            var nodeById = _nodes.ToDictionary(n => n.Id);

            var saveBeforeLoad = new List<(Guid SaveId, Guid LoadId)>();
            foreach (var save in _nodes.Where(n => n.Def.TypeId == "save_calibration_result"))
            {
                foreach (var load in _nodes.Where(n => n.Def.TypeId == "load_calibration_result"))
                    saveBeforeLoad.Add((save.Id, load.Id));
            }

            foreach (var n in _nodes) inDegree[n.Id] = 0;
            foreach (var c in _connections) inDegree[c.ToPort.Owner.Id]++;
            foreach (var (_, loadId) in saveBeforeLoad)
                inDegree[loadId]++;

            var queue = new Queue<FlowNode>();
            foreach (var n in _nodes)
            {
                if (inDegree[n.Id] == 0)
                    queue.Enqueue(n);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (visited.Contains(current.Id)) continue;
                visited.Add(current.Id);
                result.Add(current);

                foreach (var c in _connections)
                {
                    if (c.FromPort.Owner == current)
                    {
                        inDegree[c.ToPort.Owner.Id]--;
                        if (inDegree[c.ToPort.Owner.Id] == 0)
                            queue.Enqueue(c.ToPort.Owner);
                    }
                }

                foreach (var (saveId, loadId) in saveBeforeLoad)
                {
                    if (current.Id != saveId)
                        continue;
                    inDegree[loadId]--;
                    if (inDegree[loadId] == 0 && nodeById.TryGetValue(loadId, out var loadNode))
                        queue.Enqueue(loadNode);
                }
            }

            // 添加未被访问的节点
            foreach (var n in _nodes)
            {
                if (!visited.Contains(n.Id))
                    result.Add(n);
            }

            return result;
        }

        /// <summary>从目标节点沿输入边反向遍历，得到运行该节点所需的全部上游（含自身）。</summary>
        private HashSet<FlowNode> CollectPredecessorsIncludingSelf(FlowNode target)
        {
            var set = new HashSet<FlowNode>();
            var q = new Queue<FlowNode>();
            q.Enqueue(target);
            set.Add(target);
            while (q.Count > 0)
            {
                var n = q.Dequeue();
                foreach (var c in _connections)
                {
                    if (!ReferenceEquals(c.ToPort.Owner, n)) continue;
                    var pred = c.FromPort.Owner;
                    if (set.Add(pred))
                        q.Enqueue(pred);
                }
            }

            return set;
        }

        // ================================================================
        // 算子执行引擎
        // ================================================================

        /// <summary>
        /// 获取节点的输入端口数据（从上游连线的输出端口取值）
        /// </summary>
        private object? GetInputData(FlowNode node, string portName)
        {
            var inPort = node.PortVisuals.FirstOrDefault(pv =>
                pv.Definition.Direction == PortDirection.Input && pv.Definition.Name == portName);
            if (inPort == null) return null;

            var conn = _connections.FirstOrDefault(c => c.ToPort == inPort);
            if (conn == null) return null;

            return GetUpstreamOutputValue(conn.FromPort.Owner, conn.FromPort.Definition.Name);
        }

        /// <summary>
        /// 获取所有上游连接的输出数据
        /// </summary>
        private Dictionary<string, object?> GetNodeInputs(FlowNode node)
        {
            var inputs = new Dictionary<string, object?>();
            foreach (var pv in node.PortVisuals.Where(p => p.Definition.Direction == PortDirection.Input))
            {
                var conn = _connections.FirstOrDefault(c => c.ToPort == pv);
                if (conn != null)
                    inputs[pv.Definition.Name] = GetUpstreamOutputValue(
                        conn.FromPort.Owner, conn.FromPort.Definition.Name);
            }
            return inputs;
        }

        private static bool TryParseBoolConfig(string? text, bool fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
                return fallback;
            string s = text.Trim();
            if (bool.TryParse(s, out bool b))
                return b;
            if (int.TryParse(s, out int i))
                return i != 0;
            return s.ToLowerInvariant() switch
            {
                "on" or "yes" or "y" or "enable" or "enabled" or "启用" => true,
                "off" or "no" or "n" or "disable" or "disabled" or "禁用" => false,
                _ => fallback
            };
        }

        private static bool IsNodeEnabled(FlowNode node)
        {
            bool enabled = TryParseBoolConfig(node.Params.GetValueOrDefault("enable"), true);
            bool disabled = TryParseBoolConfig(node.Params.GetValueOrDefault("disable"), false);
            return enabled && !disabled;
        }

        private void SetNodeEnabledFromContextMenu(FlowNode node, bool enabled)
        {
            PushFlowUndoSnapshotBeforeChange();
            node.Params["enable"] = enabled ? "true" : "false";
            node.Params["disable"] = enabled ? "false" : "true";
            node.ErrorMessage = null;
            node.ResultSummary = enabled ? "已启用" : "已禁用";
            UpdateNodeSummary(node);
            StatusText.Text = enabled
                ? $"已启用: {node.Def.DisplayName}"
                : $"已禁用: {node.Def.DisplayName}";
            StatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
        }

        private void ApplyDisabledNodePassthrough(FlowNode node, IReadOnlyDictionary<string, object?> inputs)
        {
            var ports = GetNodePortDefinitions(node);
            object? primary = null;
            if (!inputs.TryGetValue("In", out primary))
            {
                if (!inputs.TryGetValue("Image", out primary))
                    primary = inputs.Values.FirstOrDefault(v => v != null);
            }

            foreach (var op in ports.Where(p => p.Direction == PortDirection.Output))
            {
                if (inputs.TryGetValue(op.Name, out var sameName))
                {
                    node.Outputs[op.Name] = sameName;
                    continue;
                }
                if (string.Equals(op.Name, "Out", StringComparison.Ordinal))
                {
                    if (inputs.TryGetValue("In", out var inVal))
                        node.Outputs[op.Name] = inVal;
                    else if (inputs.TryGetValue("Image", out var imgVal))
                        node.Outputs[op.Name] = imgVal;
                    continue;
                }
                if (string.Equals(op.Name, "Image", StringComparison.Ordinal) && inputs.TryGetValue("In", out var inImage))
                {
                    node.Outputs[op.Name] = inImage;
                    continue;
                }
                if (string.Equals(op.Name, "Out2", StringComparison.Ordinal) && primary != null)
                {
                    node.Outputs[op.Name] = primary;
                }
            }

            if (node.Outputs.Count == 0 && primary != null)
            {
                var firstOut = ports.FirstOrDefault(p => p.Direction == PortDirection.Output);
                if (firstOut != null)
                    node.Outputs[firstOut.Name] = primary;
            }
        }

        /// <summary>加载流程时兼容旧版/别名输入端口名。</summary>
        private static string NormalizeFlowInputPortName(FlowNode toNode, string? toPort)
        {
            if (toNode.Def.TypeId == "hough_lines" && string.Equals(toPort, "Image", StringComparison.OrdinalIgnoreCase))
                return "Edge";
            if (toNode.Def.TypeId == "save_image" && string.Equals(toPort, "In", StringComparison.OrdinalIgnoreCase))
                return "Image";
            return toPort ?? "";
        }

        /// <summary>旧版霍夫线段输入端口名为 Image，现改为 Edge；加载流程时自动映射。</summary>
        private static string NormalizeHoughLinesInputPort(FlowNode toNode, string? toPort) =>
            NormalizeFlowInputPortName(toNode, toPort);

        /// <summary>
        /// 高亮/恢复节点边框（执行状态可视化）
        /// </summary>
        private void SetNodeStatus(FlowNode node, bool running, bool error = false)
        {
            if (node.Visual is not Border border) return;
            border.Dispatcher.Invoke(() =>
            {
                if (error)
                    border.BorderBrush = new SolidColorBrush(Colors.Red);
                else if (running)
                    border.BorderBrush = new SolidColorBrush(Colors.Orange);
                else
                    border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
                border.BorderThickness = new Thickness(running || error ? 2 : 1);
            });
        }

        /// <summary>
        /// 向日志区追加一行（线程安全）
        /// </summary>
        private void AppendLog(string text, bool isError = false)
        {
            if (isError && MirrorErrorsToStderr)
            {
                try
                {
                    Console.Error.WriteLine($"[Flow] {text}");
                }
                catch
                {
                    // ignored
                }
            }

            LogBox.Dispatcher.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                LogBox.AppendText($"[{timestamp}] {text}\n");
                LogBox.ScrollToEnd();
                if (isError)
                {
                    // 最后追加的行标红不方便，改为整体不改色，靠前缀 [ERROR] 区分
                }
            });
        }

        private void LogOperatorTiming(FlowNode node, double elapsedMs, string? timingScope = null)
        {
            string scope = string.IsNullOrEmpty(timingScope) ? "" : $"[{timingScope}] ";
            string line = $"{scope}[Timing] {node.Def.TypeId} ({node.Def.DisplayName}) {elapsedMs:F2} ms";
            AppendLog(line);
            if (TraceEnginePathToConsole)
                TryTraceEnginePathToConsole(line);
        }

        private static void TryTraceEnginePathToConsole(string message)
        {
            try { Console.Out.WriteLine(message); }
            catch { /* ignore */ }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogBox.Text = "";
        }

        private static Point2D[] SmoothPoints(Point2D[] points, int windowRadius = 1)
        {
            if (points == null || points.Length < 3 || windowRadius <= 0) return points ?? Array.Empty<Point2D>();
            int n = points.Length;
            var smoothed = new Point2D[n];
            for (int i = 0; i < n; i++)
            {
                double sx = 0;
                double sy = 0;
                int cnt = 0;
                for (int k = -windowRadius; k <= windowRadius; k++)
                {
                    int idx = i + k;
                    if (idx < 0) idx += n;
                    if (idx >= n) idx -= n;
                    sx += points[idx].X;
                    sy += points[idx].Y;
                    cnt++;
                }
                smoothed[i] = new Point2D(sx / cnt, sy / cnt);
            }
            return smoothed;
        }

        private static Point2D[] SimplifyClosedPolyline(Point2D[] points, double epsilon)
        {
            if (points == null || points.Length < 4 || epsilon <= 0) return points ?? Array.Empty<Point2D>();
            var open = points.ToList();
            open.Add(points[0]);
            var simplified = SimplifyOpenPolyline(open, epsilon);
            if (simplified.Count > 1)
                simplified.RemoveAt(simplified.Count - 1);
            return EnsureClosedPolylineEndpoints(simplified.ToArray());
        }

        /// <summary>简化后的闭合轮廓补回终点，供显示 DrawPolygon / 焊道闭合段使用。</summary>
        private static Point2D[] EnsureClosedPolylineEndpoints(Point2D[]? pts)
        {
            if (pts == null || pts.Length < 2)
                return pts ?? Array.Empty<Point2D>();
            double dx = pts[0].X - pts[^1].X;
            double dy = pts[0].Y - pts[^1].Y;
            if (dx * dx + dy * dy <= 1e-12)
                return pts;
            var closed = new Point2D[pts.Length + 1];
            Array.Copy(pts, closed, pts.Length);
            closed[^1] = pts[0];
            return closed;
        }

        private static List<Point2D> SimplifyOpenPolyline(List<Point2D> points, double epsilon)
        {
            if (points.Count <= 2) return new List<Point2D>(points);

            int index = -1;
            double maxDist = -1;
            var start = points[0];
            var end = points[^1];

            for (int i = 1; i < points.Count - 1; i++)
            {
                double dist = PointLineDistance(points[i], start, end);
                if (dist > maxDist)
                {
                    maxDist = dist;
                    index = i;
                }
            }

            if (maxDist <= epsilon || index <= 0)
                return new List<Point2D> { start, end };

            var left = SimplifyOpenPolyline(points.GetRange(0, index + 1), epsilon);
            var right = SimplifyOpenPolyline(points.GetRange(index, points.Count - index), epsilon);
            left.RemoveAt(left.Count - 1);
            left.AddRange(right);
            return left;
        }

        private static double PointLineDistance(Point2D p, Point2D a, Point2D b)
        {
            double vx = b.X - a.X;
            double vy = b.Y - a.Y;
            double wx = p.X - a.X;
            double wy = p.Y - a.Y;

            double c1 = vx * wx + vy * wy;
            if (c1 <= 0) return Math.Sqrt(wx * wx + wy * wy);

            double c2 = vx * vx + vy * vy;
            if (c2 <= 1e-9)
                return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));

            double t = c1 / c2;
            double px = a.X + t * vx;
            double py = a.Y + t * vy;
            double dx = p.X - px;
            double dy = p.Y - py;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static Point2D[] ResampleClosedPolyline(Point2D[] points, int targetCount)
        {
            if (points == null || points.Length == 0 || targetCount <= 0) return Array.Empty<Point2D>();
            if (points.Length == 1) return Enumerable.Repeat(points[0], targetCount).ToArray();

            int n = points.Length;
            var cum = new double[n + 1];
            cum[0] = 0;
            for (int i = 0; i < n; i++)
            {
                var p0 = points[i];
                var p1 = points[(i + 1) % n];
                double dx = p1.X - p0.X;
                double dy = p1.Y - p0.Y;
                cum[i + 1] = cum[i] + Math.Sqrt(dx * dx + dy * dy);
            }

            double perimeter = cum[n];
            if (perimeter <= 1e-6) return Enumerable.Repeat(points[0], targetCount).ToArray();

            var result = new Point2D[targetCount];
            for (int i = 0; i < targetCount; i++)
            {
                double s = (i * perimeter) / targetCount;
                int seg = 0;
                while (seg < n - 1 && cum[seg + 1] < s) seg++;
                double segStart = cum[seg];
                double segLen = cum[seg + 1] - segStart;
                var a = points[seg];
                var b = points[(seg + 1) % n];
                double t = segLen <= 1e-9 ? 0 : (s - segStart) / segLen;
                result[i] = new Point2D(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            }
            return result;
        }

        private static double PointDistance(Point2D a, Point2D b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static List<Point2D[]> SplitIntoClosedRegions(Point2D[] points, double splitGapFactor, int minRegionPoints)
        {
            var regions = new List<Point2D[]>();
            if (points == null || points.Length == 0) return regions;
            if (points.Length < 4)
            {
                regions.Add(points);
                return regions;
            }

            var steps = new List<double>(points.Length - 1);
            for (int i = 0; i < points.Length - 1; i++)
                steps.Add(PointDistance(points[i], points[i + 1]));
            var ordered = steps.OrderBy(v => v).ToArray();
            double medianStep = ordered.Length == 0 ? 0 : ordered[ordered.Length / 2];
            if (medianStep <= 1e-9 || splitGapFactor <= 1.0)
            {
                regions.Add(points);
                return regions;
            }

            double threshold = medianStep * splitGapFactor;
            int start = 0;
            for (int i = 0; i < points.Length - 1; i++)
            {
                if (steps[i] <= threshold) continue;
                int len = i - start + 1;
                if (len >= Math.Max(3, minRegionPoints))
                    regions.Add(points.Skip(start).Take(len).ToArray());
                start = i + 1;
            }

            int tailLen = points.Length - start;
            if (tailLen >= Math.Max(3, minRegionPoints))
                regions.Add(points.Skip(start).Take(tailLen).ToArray());

            // 若自动分割失败，回退整体拟合
            if (regions.Count == 0)
                regions.Add(points);
            return regions;
        }

        private static List<(Point2D[] Points, int[] BarIds)> SplitRegionsByBarIds(Point2D[] points, int[] barIds, int minRegionPoints)
        {
            var regions = new List<(Point2D[] Points, int[] BarIds)>();
            if (points == null || barIds == null || points.Length == 0 || barIds.Length != points.Length)
                return regions;

            int start = 0;
            for (int i = 1; i < barIds.Length; i++)
            {
                if (barIds[i] == barIds[i - 1]) continue;
                int len = i - start;
                if (len >= Math.Max(3, minRegionPoints))
                    regions.Add((points.Skip(start).Take(len).ToArray(), barIds.Skip(start).Take(len).ToArray()));
                start = i;
            }

            int tailLen = barIds.Length - start;
            if (tailLen >= Math.Max(3, minRegionPoints))
                regions.Add((points.Skip(start).Take(tailLen).ToArray(), barIds.Skip(start).Take(tailLen).ToArray()));

            return regions;
        }

        /// <summary>
        /// 将 (flatX, flatY, contourLengths, contourCount) 拆成多条轮廓折线（像素坐标）。
        /// </summary>
        private static List<Point2D[]> ExplodeContourTupleToPolylines(
            int[] flatX, int[] flatY, int[] contourLengths, int contourCount)
        {
            var list = new List<Point2D[]>();
            if (flatX == null || flatY == null || contourLengths == null || contourCount <= 0)
                return list;
            int offset = 0;
            for (int i = 0; i < contourCount && i < contourLengths.Length; i++)
            {
                int len = contourLengths[i];
                if (len <= 0 || offset + len > flatX.Length || offset + len > flatY.Length)
                {
                    offset += Math.Max(0, len);
                    continue;
                }

                var seg = new Point2D[len];
                for (int j = 0; j < len; j++)
                    seg[j] = new Point2D(flatX[offset + j], flatY[offset + j]);
                offset += len;
                list.Add(seg);
            }

            return list;
        }

        /// <summary>
        /// 将 (flatX, flatY, contourLengths, contourCount) 拆成多条轮廓折线（双精度坐标，一般为世界 mm）。
        /// </summary>
        private static List<Point2D[]> ExplodeContourDoubleTupleToPolylines(
            double[] flatX, double[] flatY, int[] contourLengths, int contourCount)
        {
            var list = new List<Point2D[]>();
            if (flatX == null || flatY == null || contourLengths == null || contourCount <= 0)
                return list;
            int offset = 0;
            for (int i = 0; i < contourCount && i < contourLengths.Length; i++)
            {
                int len = contourLengths[i];
                if (len <= 0 || offset + len > flatX.Length || offset + len > flatY.Length)
                {
                    offset += Math.Max(0, len);
                    continue;
                }

                var seg = new Point2D[len];
                for (int j = 0; j < len; j++)
                    seg[j] = new Point2D(flatX[offset + j], flatY[offset + j]);
                offset += len;
                list.Add(seg);
            }

            return list;
        }

        /// <summary>
        /// 将「采样点 + 条号」还原为多条折线：一条轮廓应对应同一 BarId，仅在条号变化处分条。
        /// BarIds 与 Points 不等长时退化为整条点列一条线。
        /// </summary>
        private static List<Point2D[]> SplitSampledPointsToContourPolylines(Point2D[] points, int[]? barIds)
        {
            SplitSampledPointsToContourPolylinesWithBarIds(points, barIds, out var segs, out _);
            return segs;
        }

        /// <summary>与 <see cref="SplitSampledPointsToContourPolylines"/> 相同，并输出每条折线对应的条号（用于焊道分段合并时禁止跨条直连）。</summary>
        private static void OffsetPolylinesWithBarIds(
            Point2D[] pts,
            int[]? barIds,
            double distance,
            bool closed,
            out Point2D[] outPts,
            out int[] outBarIds)
        {
            if (barIds == null || barIds.Length != pts.Length)
            {
                outPts = PolylineUniformOffset.Offset(pts, distance, closed);
                outBarIds = new int[outPts.Length];
                return;
            }

            SplitSampledPointsToContourPolylinesWithBarIds(pts, barIds, out var groups, out var segBarIds);
            var outList = new List<Point2D>();
            var idList = new List<int>();
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var seg = groups[gi];
                if (seg == null || seg.Length < 2)
                    continue;

                int barId = gi < segBarIds.Count ? segBarIds[gi] : gi;
                bool segClosed = closed && seg.Length >= 3;
                var off = PolylineUniformOffset.Offset(seg, distance, segClosed);
                foreach (var p in off)
                {
                    outList.Add(p);
                    idList.Add(barId);
                }
            }

            outPts = outList.ToArray();
            outBarIds = idList.ToArray();
        }

        private static void SplitSampledPointsToContourPolylinesWithBarIds(
            Point2D[] points,
            int[]? barIds,
            out List<Point2D[]> segments,
            out List<int> segmentBarIds)
        {
            segments = new List<Point2D[]>();
            segmentBarIds = new List<int>();
            if (points == null || points.Length == 0)
                return;
            if (barIds == null || barIds.Length != points.Length)
            {
                segments.Add(points);
                segmentBarIds.Add(0);
                return;
            }

            int start = 0;
            for (int i = 1; i <= points.Length; i++)
            {
                if (i == points.Length || barIds[i] != barIds[i - 1])
                {
                    int len = i - start;
                    if (len > 0)
                    {
                        var seg = new Point2D[len];
                        Array.Copy(points, start, seg, 0, len);
                        segments.Add(seg);
                        segmentBarIds.Add(barIds[start]);
                    }

                    start = i;
                }
            }
        }

        private static (List<Point2D[]> Segments, List<int> BarIds) ZipRemoveEmptyContourSegments(
            List<Point2D[]> segments,
            List<int> segmentBarIds)
        {
            var s2 = new List<Point2D[]>();
            var b2 = new List<int>();
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (s == null || s.Length == 0)
                    continue;
                s2.Add(s);
                int bid = i < segmentBarIds.Count ? segmentBarIds[i] : i;
                b2.Add(bid);
            }

            return (s2, b2);
        }

        private static double WeldPolylineOpenLength(Point2D[] c)
        {
            if (c == null || c.Length < 2)
                return 0;
            double s = 0;
            for (int i = 0; i < c.Length - 1; i++)
                s += PointDistance(c[i], c[i + 1]);
            return s;
        }

        private static bool ShouldAutoCloseContourWeld(Point2D[] c)
        {
            if (c == null || c.Length < 3)
                return false;
            double per = WeldPolylineOpenLength(c);
            if (per < 1e-9)
                return false;
            double gap = PointDistance(c[0], c[^1]);
            if (gap < 1e-9)
                return false;
            double medE = MedianEdgeLengthForContour(c);
            return gap / per <= 0.38 || (medE > 1e-12 && gap <= medE * 8.0);
        }

        private static double MedianEdgeLengthForContour(Point2D[] c)
        {
            if (c == null || c.Length < 2)
                return 0;
            var e = new double[c.Length - 1];
            for (int i = 0; i < c.Length - 1; i++)
                e[i] = PointDistance(c[i], c[i + 1]);
            Array.Sort(e);
            return e[e.Length / 2];
        }

        private static void AppendWeldContourClosingIfNeeded(
            List<Point2D> path,
            Point2D[] c,
            string closeContourMode,
            double transitSpacingPx)
        {
            if (c == null || c.Length < 3 || path.Count == 0)
                return;
            var mode = (closeContourMode ?? "auto").Trim().ToLowerInvariant();
            if (mode is "false" or "0" or "no" or "off")
                return;
            bool shouldClose = mode is "true" or "1" or "yes" or "on" || ShouldAutoCloseContourWeld(c);
            if (!shouldClose)
                return;
            var last = path[path.Count - 1];
            if (PointDistance(last, c[0]) < 1e-9)
                return;
            AppendLineTransit(path, last, c[0], transitSpacingPx);
        }

        private static bool BarSplitIsStrictByBar(string barSplitMode)
        {
            var m = (barSplitMode ?? "").Trim().ToLowerInvariant();
            return m is "by_bar" or "bars" or "split";
        }

        /// <summary>
        /// 同一条轮廓上相邻点步长应连续，条号应恒定（一条轮廓一个 BarId）。
        /// 将「步长 ≤ 阈值且条号相对前一点突变」的点的条号改为与前一点相同，消除轮廓内的条号噪声。
        /// </summary>
        private static int[] SanitizeBarIdsUnifyAlongShortSteps(Point2D[] pts, int[] barIds, double transitSpacingHint)
        {
            if (pts == null || barIds == null || pts.Length < 2 || barIds.Length != pts.Length)
                return barIds;

            var steps = new double[pts.Length - 1];
            for (int i = 0; i < pts.Length - 1; i++)
            {
                double dx = pts[i + 1].X - pts[i].X;
                double dy = pts[i + 1].Y - pts[i].Y;
                steps[i] = Math.Sqrt(dx * dx + dy * dy);
            }

            var sorted = (double[])steps.Clone();
            Array.Sort(sorted);
            double med = sorted[sorted.Length / 2];
            double th = Math.Max(med * 4.0, 1e-9);
            if (transitSpacingHint > 1e-12)
                th = Math.Max(th, transitSpacingHint * 3.0);

            var o = (int[])barIds.Clone();
            for (int i = 1; i < pts.Length; i++)
            {
                if (steps[i - 1] <= th && o[i] != o[i - 1])
                    o[i] = o[i - 1];
            }

            return o;
        }

        /// <summary>像素条带轮廓逐点变换为双精度条带（结构不变：条数、每条点数序列与输入一致）。</summary>
        private static ValueTuple<double[], double[], int[], int> TransformPixelContourTupleToWorldDoubleTuple(
            ValueTuple<int[], int[], int[], int> pixel,
            Func<Point2D, Point2D> toWorld)
        {
            var (ix, iy, lens, cnt) = pixel;
            if (cnt <= 0 || lens == null || ix == null || iy == null)
                return (Array.Empty<double>(), Array.Empty<double>(), Array.Empty<int>(), 0);

            int nCont = Math.Min(cnt, lens.Length);
            var lensOut = new int[nCont];
            var ptsX = new List<double>();
            var ptsY = new List<double>();
            int off = 0;
            for (int ci = 0; ci < nCont; ci++)
            {
                int L = lens[ci];
                if (L <= 0)
                {
                    lensOut[ci] = 0;
                    continue;
                }

                if (off + L > ix.Length || off + L > iy.Length)
                    throw new InvalidOperationException("轮廓像素→世界: Contours 数据长度与 contourLengths 不一致");

                for (int j = 0; j < L; j++)
                {
                    var w = toWorld(new Point2D(ix[off + j], iy[off + j]));
                    ptsX.Add(w.X);
                    ptsY.Add(w.Y);
                }

                lensOut[ci] = L;
                off += L;
            }

            return (ptsX.ToArray(), ptsY.ToArray(), lensOut, nCont);
        }

        /// <summary>从输入端口选择唯一一路像素→世界映射（Affine / H / Poly）。</summary>
        private static Func<Point2D, Point2D> ResolvePixelToWorldMapperForContours(Dictionary<string, object?> inputs)
        {
            bool hasPoly = inputs.TryGetValue("Poly", out var pObj) && pObj is Poly2DTransform;
            bool hasH = inputs.TryGetValue("H", out var hObj) && hObj is HomographyTransform;
            bool hasA = inputs.TryGetValue("Transform", out var aObj) && aObj is AffineTransform;
            int n = (hasPoly ? 1 : 0) + (hasH ? 1 : 0) + (hasA ? 1 : 0);
            if (n != 1)
                throw new InvalidOperationException(n == 0
                    ? "轮廓像素→世界: 请连接 Transform、H、Poly 之一"
                    : "轮廓像素→世界: 请只连接 Transform、H、Poly 中的一路");

            if (hasPoly)
            {
                var poly = (Poly2DTransform)pObj!;
                return pt => ApplyPoly2D(pt, poly);
            }

            if (hasH)
            {
                var hom = (HomographyTransform)hObj!;
                if (!HomographyMapsToWorld(hom))
                    throw new InvalidOperationException(
                        "轮廓像素→世界: 所接 H 为图像坐标系标定(px→px)，请改用 world 模式 H 或仅在图像系下处理轮廓");
                return pt => ApplyHomography(pt, hom);
            }

            var aff = (AffineTransform)aObj!;
            CalibAPI.SetTransform(aff);
            return pt => CalibAPI.ImageToWorld(pt, aff);
        }

        /// <summary>直线移行插补：不包含起点 a，终点 b 总会写入（与 a 重合时仅写一次 b）。spacing≤0 时只追加 b。</summary>
        private static void AppendLineTransit(List<Point2D> path, Point2D a, Point2D b, double spacingPx)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-12)
            {
                AppendDedupePoint(path, b);
                return;
            }

            if (spacingPx <= 0)
            {
                AppendDedupePoint(path, b);
                return;
            }

            int n = Math.Max(1, (int)Math.Ceiling(len / spacingPx));
            for (int i = 1; i <= n; i++)
            {
                double t = (double)i / n;
                AppendDedupePoint(path, new Point2D(a.X + t * dx, a.Y + t * dy));
            }
        }

        private static void AppendDedupePoint(List<Point2D> path, Point2D p)
        {
            if (path.Count > 0)
            {
                var last = path[path.Count - 1];
                if (Math.Abs(last.X - p.X) < 1e-9 && Math.Abs(last.Y - p.Y) < 1e-9)
                    return;
            }

            path.Add(p);
        }

        /// <summary>
        /// 轮廓顺序焊接：每条轮廓走完后先闭合（可选），再到 <paramref name="retreat"/>，再接近下一条轮廓。
        /// </summary>
        private static Point2D[] BuildWeldPathWithRetreatBetweenContours(
            List<Point2D[]> contours,
            Point2D retreat,
            bool leadIn,
            bool leadOut,
            double transitSpacingPx,
            string closeContourMode)
        {
            var path = new List<Point2D>();
            for (int i = 0; i < contours.Count; i++)
            {
                var c = contours[i];
                if (c == null || c.Length == 0) continue;

                if (i == 0)
                {
                    if (leadIn)
                        AppendLineTransit(path, retreat, c[0], transitSpacingPx);
                    else
                        AppendDedupePoint(path, c[0]);
                    for (int k = 1; k < c.Length; k++)
                        AppendDedupePoint(path, c[k]);
                    AppendWeldContourClosingIfNeeded(path, c, closeContourMode, transitSpacingPx);
                }
                else
                {
                    var prev = path[path.Count - 1];
                    AppendLineTransit(path, prev, retreat, transitSpacingPx);
                    AppendLineTransit(path, retreat, c[0], transitSpacingPx);
                    for (int k = 1; k < c.Length; k++)
                        AppendDedupePoint(path, c[k]);
                    AppendWeldContourClosingIfNeeded(path, c, closeContourMode, transitSpacingPx);
                }
            }

            if (leadOut && path.Count > 0)
            {
                var last = path[path.Count - 1];
                AppendLineTransit(path, last, retreat, transitSpacingPx);
            }

            return path.ToArray();
        }

        /// <summary>
        /// 焊道 auto：仅当相邻分段为<strong>同一 BarId</strong>且端距小于阈值时才合并，避免不同轮廓被焊成直连线。
        /// barSplit：auto / by_bar / single。
        /// </summary>
        private static (List<Point2D[]> Segments, List<int> BarIds, string? Note) NormalizeWeldContourSegmentsForRetreat(
            List<Point2D[]> segments,
            List<int> segmentBarIds,
            string barSplitMode,
            double transitSpacing,
            double segmentJoinMaxDistOverride)
        {
            string? summaryNote = null;
            if (segments == null || segments.Count == 0)
                return (segments ?? new List<Point2D[]>(), segmentBarIds ?? new List<int>(), null);

            var mode = (barSplitMode ?? "auto").Trim().ToLowerInvariant();
            if (mode is "single" or "one" or "polyline")
            {
                var one = new List<Point2D>();
                foreach (var s in segments)
                {
                    if (s == null || s.Length == 0) continue;
                    foreach (var p in s)
                        AppendDedupePoint(one, p);
                }

                summaryNote = " · 强制单条轨迹(无条间回退)";
                return (new List<Point2D[]> { one.ToArray() }, new List<int> { 0 }, summaryNote);
            }

            if (mode is "by_bar" or "bars" or "split")
                return (segments, segmentBarIds, null);

            if (segments.Count <= 1)
                return (segments, segmentBarIds, null);

            int nonemptySegCount = segments.Count(s => s != null && s.Length > 0);
            if (nonemptySegCount < 2)
                return (segments, segmentBarIds, null);

            while (segmentBarIds.Count < segments.Count)
                segmentBarIds.Add(segmentBarIds.Count);
            while (segmentBarIds.Count > segments.Count)
                segmentBarIds.RemoveAt(segmentBarIds.Count - 1);

            double jm = segmentJoinMaxDistOverride > 1e-15
                ? segmentJoinMaxDistOverride
                : InferWeldJoinMaxFromPointSteps(segments, transitSpacing);
            if (jm <= 1e-15)
                return (segments, segmentBarIds, null);

            var merged = CoalesceWeldSegmentsByEndGapAndBarId(segments, segmentBarIds, jm);
            for (int iter = 0; iter < 65536; iter++)
            {
                var again = CoalesceWeldSegmentsByEndGapAndBarId(merged.Segments, merged.BarIds, jm);
                if (again.Segments.Count == merged.Segments.Count)
                    break;
                merged = again;
            }

            if (merged.Segments.Count < segments.Count)
                summaryNote = $" · 同条号端距≤{jm.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)}合并分段 {segments.Count}→{merged.Segments.Count}条";
            return (merged.Segments, merged.BarIds, summaryNote);
        }

        /// <summary>
        /// 用点列步长估计「同一条轮廓上相邻分段」的端距上限：略大于典型步长，远小于条与条之间的空移。
        /// </summary>
        private static double InferWeldJoinMaxFromPointSteps(List<Point2D[]> segments, double transitSpacing)
        {
            var flat = segments.Where(s => s != null && s.Length > 0).SelectMany(s => s!).ToArray();
            if (flat.Length < 2)
                return 0;

            var d = new double[flat.Length - 1];
            for (int i = 0; i < flat.Length - 1; i++)
            {
                double dx = flat[i + 1].X - flat[i].X;
                double dy = flat[i + 1].Y - flat[i].Y;
                d[i] = Math.Sqrt(dx * dx + dy * dy);
            }

            Array.Sort(d);
            double med = d[d.Length / 2];
            int p90i = Math.Min(d.Length - 1, (int)Math.Floor((d.Length - 1) * 0.9));
            double p90 = d[p90i];
            if (med < 1e-12 && p90 < 1e-12)
                return 0;

            double baseStep = Math.Max(med, p90 * 0.35);
            double th = baseStep * 10.0;
            if (transitSpacing > 1e-12)
                th = Math.Max(th, transitSpacing * 5.0);
            return th;
        }

        private static (List<Point2D[]> Segments, List<int> BarIds) CoalesceWeldSegmentsByEndGapAndBarId(
            List<Point2D[]> segments,
            IReadOnlyList<int> segmentBarId,
            double joinMaxDist)
        {
            var res = new List<Point2D[]>();
            var resBid = new List<int>();
            var buf = new List<Point2D>();
            var bufIds = new List<int>();
            bool hasBuf = false;

            for (int j = 0; j < segments.Count; j++)
            {
                var s = segments[j];
                if (s == null || s.Length == 0)
                    continue;
                int bid = j < segmentBarId.Count ? segmentBarId[j] : j;
                if (!hasBuf)
                {
                    foreach (var p in s)
                        AppendDedupePoint(buf, p);
                    bufIds.Add(bid);
                    hasBuf = true;
                    continue;
                }

                var last = buf[buf.Count - 1];
                var head = s[0];
                double dx = head.X - last.X, dy = head.Y - last.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                int bufId = bufIds[0];
                if (dist <= joinMaxDist && bid == bufId)
                {
                    foreach (var p in s)
                        AppendDedupePoint(buf, p);
                }
                else
                {
                    res.Add(buf.ToArray());
                    resBid.Add(bufId);
                    buf.Clear();
                    bufIds.Clear();
                    foreach (var p in s)
                        AppendDedupePoint(buf, p);
                    bufIds.Add(bid);
                }
            }

            if (hasBuf && buf.Count > 0)
            {
                res.Add(buf.ToArray());
                resBid.Add(bufIds[0]);
            }

            return (res, resBid);
        }

        private static (int[] flatX, int[] flatY, int[] lengths, int count) FilterContoursByGeometry(
            ValueTuple<int[], int[], int[], int> contourData,
            double minArea, double maxArea, double minAspect, double maxAspect,
            double minCircularity, double maxCircularity, int targetCount,
            bool sortByCentroidY)
        {
            var (flatX, flatY, contourLengths, contourCount) = contourData;
            if (contourCount <= 0 || contourLengths == null || contourLengths.Length == 0)
                return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

            var accepted = new List<(int Start, int Len, double Area, double Cy)>();
            int offset = 0;
            for (int ci = 0; ci < contourCount && ci < contourLengths.Length; ci++)
            {
                int len = contourLengths[ci];
                if (len < 3 || offset + len > flatX.Length || offset + len > flatY.Length)
                {
                    offset += Math.Max(0, len);
                    continue;
                }

                int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
                double perimeter = 0;
                var pts = new List<System.Drawing.Point>(len);
                for (int k = 0; k < len; k++)
                {
                    int x = flatX[offset + k];
                    int y = flatY[offset + k];
                    pts.Add(new System.Drawing.Point(x, y));
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;

                    int nk = (k + 1) % len;
                    double dx = flatX[offset + nk] - x;
                    double dy = flatY[offset + nk] - y;
                    perimeter += Math.Sqrt(dx * dx + dy * dy);
                }

                double area = PolygonArea(pts);
                double w = Math.Max(1, maxX - minX + 1);
                double h = Math.Max(1, maxY - minY + 1);
                double aspect = w / h;
                double circularity = perimeter <= 1e-6 ? 0 : (4.0 * Math.PI * area) / (perimeter * perimeter);

                bool circOk = (minCircularity <= 0.0 || circularity >= minCircularity)
                    && (maxCircularity >= 1.0 || circularity <= maxCircularity);
                if (area >= minArea && area <= maxArea &&
                    aspect >= minAspect && aspect <= maxAspect &&
                    circOk)
                {
                    double cy = (minY + maxY) * 0.5;
                    accepted.Add((offset, len, area, cy));
                }

                offset += len;
            }

            if (accepted.Count == 0)
                return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

            var selected = accepted
                .OrderByDescending(c => c.Area)
                .Take(Math.Max(1, targetCount))
                .ToList();
            if (sortByCentroidY)
                selected = selected.OrderBy(c => c.Cy).ToList();

            var outLens = new List<int>(selected.Count);
            var outX = new List<int>(selected.Sum(s => s.Len));
            var outY = new List<int>(selected.Sum(s => s.Len));

            foreach (var s in selected)
            {
                outLens.Add(s.Len);
                for (int i = 0; i < s.Len; i++)
                {
                    outX.Add(flatX[s.Start + i]);
                    outY.Add(flatY[s.Start + i]);
                }
            }

            return (outX.ToArray(), outY.ToArray(), outLens.ToArray(), outLens.Count);
        }

        private static Point2D[] ResampleContourByArc(IReadOnlyList<Point2D> points, int sampleCount)
        {
            if (points == null || points.Count < 3 || sampleCount < 8) return Array.Empty<Point2D>();
            int n = points.Count;
            var cum = new double[n + 1];
            cum[0] = 0;
            for (int i = 0; i < n; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % n];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                cum[i + 1] = cum[i] + Math.Sqrt(dx * dx + dy * dy);
            }
            double perimeter = cum[n];
            if (perimeter <= 1e-6) return Array.Empty<Point2D>();

            var sampled = new Point2D[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                double s = i * perimeter / sampleCount;
                int seg = 0;
                while (seg < n - 1 && cum[seg + 1] < s) seg++;
                double segLen = cum[seg + 1] - cum[seg];
                double t = segLen <= 1e-9 ? 0 : (s - cum[seg]) / segLen;
                var p0 = points[seg];
                var p1 = points[(seg + 1) % n];
                sampled[i] = new Point2D(p0.X + (p1.X - p0.X) * t, p0.Y + (p1.Y - p0.Y) * t);
            }
            return sampled;
        }

        private static Point2D[] NormalizeShape(Point2D[] shape)
        {
            if (shape == null || shape.Length == 0) return Array.Empty<Point2D>();
            double cx = shape.Average(p => p.X);
            double cy = shape.Average(p => p.Y);
            double scale = Math.Sqrt(shape.Sum(p =>
            {
                double dx = p.X - cx, dy = p.Y - cy;
                return dx * dx + dy * dy;
            }) / shape.Length);
            if (scale <= 1e-9) scale = 1.0;
            return shape.Select(p => new Point2D((p.X - cx) / scale, (p.Y - cy) / scale)).ToArray();
        }

        private static double ComputeShapeDistance(Point2D[] a, Point2D[] b)
        {
            if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return double.PositiveInfinity;
            int n = a.Length;
            double best = double.PositiveInfinity;
            for (int shift = 0; shift < n; shift++)
            {
                double err1 = 0;
                double err2 = 0;
                for (int i = 0; i < n; i++)
                {
                    var pa = a[i];
                    var pb1 = b[(i + shift) % n];
                    var pb2 = b[(n - i + shift) % n]; // 反向匹配，消除走向差异
                    double dx1 = pa.X - pb1.X, dy1 = pa.Y - pb1.Y;
                    double dx2 = pa.X - pb2.X, dy2 = pa.Y - pb2.Y;
                    err1 += dx1 * dx1 + dy1 * dy1;
                    err2 += dx2 * dx2 + dy2 * dy2;
                }
                double cur = Math.Min(err1, err2) / n;
                if (cur < best) best = cur;
            }
            return Math.Sqrt(best);
        }

        private static (int[] flatX, int[] flatY, int[] lengths, int count) MatchContoursByTemplate(
            ValueTuple<int[], int[], int[], int> contourData, int templateIndex, double maxDistance, int samplePoints, int targetCount)
        {
            var (flatX, flatY, contourLengths, contourCount) = contourData;
            if (contourCount <= 0 || contourLengths == null || contourLengths.Length == 0)
                return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

            var contours = new List<(int Start, int Len, double Area, Point2D[] Pts)>();
            int offset = 0;
            for (int ci = 0; ci < contourCount && ci < contourLengths.Length; ci++)
            {
                int len = contourLengths[ci];
                if (len < 8 || offset + len > flatX.Length || offset + len > flatY.Length)
                {
                    offset += Math.Max(0, len);
                    continue;
                }
                var pts = new List<System.Drawing.Point>(len);
                var pts2 = new Point2D[len];
                for (int i = 0; i < len; i++)
                {
                    int x = flatX[offset + i], y = flatY[offset + i];
                    pts.Add(new System.Drawing.Point(x, y));
                    pts2[i] = new Point2D(x, y);
                }
                contours.Add((offset, len, PolygonArea(pts), pts2));
                offset += len;
            }
            if (contours.Count == 0) return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

            int tplIdx = templateIndex;
            if (tplIdx < 0 || tplIdx >= contours.Count)
            {
                double bestArea = double.MinValue;
                tplIdx = 0;
                for (int i = 0; i < contours.Count; i++)
                {
                    if (contours[i].Area > bestArea)
                    {
                        bestArea = contours[i].Area;
                        tplIdx = i;
                    }
                }
            }

            var tplResampled = ResampleContourByArc(contours[tplIdx].Pts, Math.Max(32, samplePoints));
            var tplNorm = NormalizeShape(tplResampled);
            if (tplNorm.Length == 0) return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

            var matched = new List<(int Start, int Len, double Dist)>();
            for (int i = 0; i < contours.Count; i++)
            {
                var cResampled = ResampleContourByArc(contours[i].Pts, tplNorm.Length);
                var cNorm = NormalizeShape(cResampled);
                double dist = ComputeShapeDistance(tplNorm, cNorm);
                if (dist <= maxDistance)
                    matched.Add((contours[i].Start, contours[i].Len, dist));
            }
            if (matched.Count == 0) return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

            var selected = matched.OrderBy(m => m.Dist).Take(Math.Max(1, targetCount)).ToList();
            var outX = new List<int>(selected.Sum(s => s.Len));
            var outY = new List<int>(selected.Sum(s => s.Len));
            var outLens = new List<int>(selected.Count);
            foreach (var s in selected)
            {
                outLens.Add(s.Len);
                for (int i = 0; i < s.Len; i++)
                {
                    outX.Add(flatX[s.Start + i]);
                    outY.Add(flatY[s.Start + i]);
                }
            }
            return (outX.ToArray(), outY.ToArray(), outLens.ToArray(), outLens.Count);
        }

        private static bool[,] BitmapToBinary(System.Drawing.Bitmap bmp, byte threshold = 1)
        {
            int w = bmp.Width, h = bmp.Height;
            var data = new bool[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    data[y, x] = ((c.R + c.G + c.B) / 3) >= threshold;
                }
            }
            return data;
        }

        private static bool[,] DownsampleBinary(bool[,] src, int factor)
        {
            if (factor <= 1) return src;
            int h = src.GetLength(0), w = src.GetLength(1);
            int nh = Math.Max(1, h / factor), nw = Math.Max(1, w / factor);
            var dst = new bool[nh, nw];
            for (int y = 0; y < nh; y++)
            {
                int sy = Math.Min(h - 1, y * factor);
                for (int x = 0; x < nw; x++)
                {
                    int sx = Math.Min(w - 1, x * factor);
                    dst[y, x] = src[sy, sx];
                }
            }
            return dst;
        }

        private static bool[,] ResizeBinary(bool[,] src, int maxSide)
        {
            int h = src.GetLength(0), w = src.GetLength(1);
            int maxDim = Math.Max(w, h);
            if (maxSide <= 0 || maxDim <= maxSide) return src;
            double scale = (double)maxSide / maxDim;
            int nw = Math.Max(1, (int)Math.Round(w * scale));
            int nh = Math.Max(1, (int)Math.Round(h * scale));
            var dst = new bool[nh, nw];
            for (int y = 0; y < nh; y++)
            {
                int sy = Math.Min(h - 1, (int)Math.Round(y / scale));
                for (int x = 0; x < nw; x++)
                {
                    int sx = Math.Min(w - 1, (int)Math.Round(x / scale));
                    dst[y, x] = src[sy, sx];
                }
            }
            return dst;
        }

        private static (int X, int Y, int W, int H)? LargestConnectedBoundingBox(bool[,] bin)
        {
            int h = bin.GetLength(0), w = bin.GetLength(1);
            var vis = new bool[h, w];
            (int X, int Y, int W, int H)? best = null;
            int bestCount = 0;
            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!bin[y, x] || vis[y, x]) continue;
                    int minX = x, maxX = x, minY = y, maxY = y, cnt = 0;
                    var q = new Queue<(int X, int Y)>();
                    q.Enqueue((x, y));
                    vis[y, x] = true;
                    while (q.Count > 0)
                    {
                        var p = q.Dequeue();
                        cnt++;
                        if (p.X < minX) minX = p.X;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.Y > maxY) maxY = p.Y;
                        for (int k = 0; k < 4; k++)
                        {
                            int nx = p.X + dx[k], ny = p.Y + dy[k];
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            if (vis[ny, nx] || !bin[ny, nx]) continue;
                            vis[ny, nx] = true;
                            q.Enqueue((nx, ny));
                        }
                    }
                    if (cnt > bestCount)
                    {
                        bestCount = cnt;
                        best = (minX, minY, maxX - minX + 1, maxY - minY + 1);
                    }
                }
            }
            return best;
        }

        private static bool[,] CropBinary(bool[,] src, int x, int y, int w, int h)
        {
            int sh = src.GetLength(0), sw = src.GetLength(1);
            int cx = Math.Max(0, Math.Min(sw - 1, x));
            int cy = Math.Max(0, Math.Min(sh - 1, y));
            int cw = Math.Max(1, Math.Min(sw - cx, w));
            int ch = Math.Max(1, Math.Min(sh - cy, h));
            var dst = new bool[ch, cw];
            for (int yy = 0; yy < ch; yy++)
                for (int xx = 0; xx < cw; xx++)
                    dst[yy, xx] = src[cy + yy, cx + xx];
            return dst;
        }

        private static List<(int X, int Y)> CollectTruePoints(bool[,] bin)
        {
            int h = bin.GetLength(0), w = bin.GetLength(1);
            var pts = new List<(int X, int Y)>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (bin[y, x]) pts.Add((x, y));
            return pts;
        }

        private static bool[,] ErodeBinary(bool[,] src, int radius)
        {
            if (radius <= 0) return src;
            int h = src.GetLength(0), w = src.GetLength(1);
            var dst = new bool[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool ok = true;
                    for (int dy = -radius; dy <= radius && ok; dy++)
                    {
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int ny = y + dy, nx = x + dx;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h || !src[ny, nx]) { ok = false; break; }
                        }
                    }
                    dst[y, x] = ok;
                }
            }
            return dst;
        }

        private static bool[,] DilateBinary(bool[,] src, int radius)
        {
            if (radius <= 0) return src;
            int h = src.GetLength(0), w = src.GetLength(1);
            var dst = new bool[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool on = false;
                    for (int dy = -radius; dy <= radius && !on; dy++)
                    {
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int ny = y + dy, nx = x + dx;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            if (src[ny, nx]) { on = true; break; }
                        }
                    }
                    dst[y, x] = on;
                }
            }
            return dst;
        }

        private static bool[,] CalibImageToBinary(CalibImage img, int foregroundThreshold)
        {
            if (img == null) throw new ArgumentNullException(nameof(img));
            var n = img.GetNativeStruct();
            int w = n.width, h = n.height;
            if (w <= 0 || h <= 0)
                throw new ArgumentException("图像尺寸无效", nameof(img));
            var buf = new byte[w * h];
            FillGrayBytesFromCalib(img, buf);
            var data = new bool[h, w];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte v = buf[row + x];
                    data[y, x] = foregroundThreshold < 0 ? v != 0 : v > foregroundThreshold;
                }
            }
            return data;
        }

        private static void EnsureOddKernel(ref int k)
        {
            if (k < 1) k = 1;
            if ((k & 1) == 0) k++;
        }

        private static bool[,] ErodeBinaryRect(bool[,] src, int kw, int kh)
        {
            int h = src.GetLength(0), w = src.GetLength(1);
            EnsureOddKernel(ref kw);
            EnsureOddKernel(ref kh);
            int rx = kw / 2, ry = kh / 2;
            var dst = new bool[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool ok = true;
                    for (int dy = -ry; dy <= ry && ok; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= h) { ok = false; break; }
                        for (int dx = -rx; dx <= rx; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= w || !src[yy, xx]) { ok = false; break; }
                        }
                    }
                    dst[y, x] = ok;
                }
            }
            return dst;
        }

        private static bool[,] DilateBinaryRect(bool[,] src, int kw, int kh)
        {
            int h = src.GetLength(0), w = src.GetLength(1);
            EnsureOddKernel(ref kw);
            EnsureOddKernel(ref kh);
            int rx = kw / 2, ry = kh / 2;
            var dst = new bool[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool on = false;
                    for (int dy = -ry; dy <= ry && !on; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= h) continue;
                        for (int dx = -rx; dx <= rx; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= w) continue;
                            if (src[yy, xx]) { on = true; break; }
                        }
                    }
                    dst[y, x] = on;
                }
            }
            return dst;
        }

        /// <summary>矩形结构元开运算：先腐蚀再膨胀，常用横向核打断竖直细桥接。</summary>
        private static bool[,] MorphOpenBinaryRect(bool[,] src, int kw, int kh)
            => DilateBinaryRect(ErodeBinaryRect(src, kw, kh), kw, kh);

        private static bool[,] MorphCloseBinaryRect(bool[,] src, int kw, int kh)
            => ErodeBinaryRect(DilateBinaryRect(src, kw, kh), kw, kh);

        private static bool[,] ApplyBinaryMorphRectOp(bool[,] src, string op, int kw, int kh)
        {
            string m = (op ?? "open").Trim().ToLowerInvariant();
            if (m.Length == 0) m = "open";
            if (m == "erode") return ErodeBinaryRect(src, kw, kh);
            if (m == "dilate") return DilateBinaryRect(src, kw, kh);
            if (m == "close") return MorphCloseBinaryRect(src, kw, kh);
            return MorphOpenBinaryRect(src, kw, kh);
        }

        private static bool[,] RemoveSmallComponents(bool[,] src, int minSize)
        {
            if (minSize <= 1) return src;
            int h = src.GetLength(0), w = src.GetLength(1);
            var vis = new bool[h, w];
            var keep = new bool[h, w];
            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!src[y, x] || vis[y, x]) continue;
                    var comp = new List<(int X, int Y)>();
                    var q = new Queue<(int X, int Y)>();
                    q.Enqueue((x, y));
                    vis[y, x] = true;
                    while (q.Count > 0)
                    {
                        var p = q.Dequeue();
                        comp.Add(p);
                        for (int k = 0; k < 4; k++)
                        {
                            int nx = p.X + dx[k], ny = p.Y + dy[k];
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            if (vis[ny, nx] || !src[ny, nx]) continue;
                            vis[ny, nx] = true;
                            q.Enqueue((nx, ny));
                        }
                    }
                    if (comp.Count >= minSize)
                        foreach (var p in comp) keep[p.Y, p.X] = true;
                }
            }
            return keep;
        }

        private static Point2D[] GlobalShapeMatchTopN(bool[,] edge, bool[,] template, int topN, double minScore, int step, double areaWeight)
        {
            int h = edge.GetLength(0), w = edge.GetLength(1);
            int th = template.GetLength(0), tw = template.GetLength(1);
            if (tw <= 0 || th <= 0 || tw > w || th > h) return Array.Empty<Point2D>();
            var tPts = CollectTruePoints(template);
            if (tPts.Count == 0) return Array.Empty<Point2D>();
            int tplCount = tPts.Count;
            step = Math.Max(1, step);
            areaWeight = Math.Max(0, areaWeight);

            // 边缘积分图，用于 O(1) 计算窗口内边缘像素密度
            var integral = new int[h + 1, w + 1];
            for (int y = 0; y < h; y++)
            {
                int rowAcc = 0;
                for (int x = 0; x < w; x++)
                {
                    rowAcc += edge[y, x] ? 1 : 0;
                    integral[y + 1, x + 1] = integral[y, x + 1] + rowAcc;
                }
            }

            // 评分改为与 cv2.matchTemplate 的 TM_CCORR_NORMED 等价形式（在二值图上）：
            // baseScore = sum(I*T) / sqrt(sum(I^2)*sum(T^2))
            // 其中 I/T 为 0/1 时，sum(I*T)=hit，sum(I^2)=窗口内边缘像素数，sum(T^2)=模板像素数。
            var cands = new List<(double RankScore, double BaseScore, int Cx, int Cy)>();
            for (int y = 0; y <= h - th; y += step)
            {
                for (int x = 0; x <= w - tw; x += step)
                {
                    int hit = 0;
                    for (int i = 0; i < tPts.Count; i++)
                    {
                        var p = tPts[i];
                        if (edge[y + p.Y, x + p.X]) hit++;
                    }
                    int x2 = x + tw, y2 = y + th;
                    int winEdge = integral[y2, x2] - integral[y, x2] - integral[y2, x] + integral[y, x];
                    if (winEdge <= 0) continue;

                    double denom = Math.Sqrt((double)winEdge * tplCount);
                    if (denom <= 1e-9) continue;
                    double baseScore = hit / denom;
                    if (baseScore < minScore) continue;

                    double localDensity = (double)winEdge / Math.Max(1, tw * th);
                    double rankScore = baseScore * (1.0 + areaWeight * localDensity);
                    cands.Add((rankScore, baseScore, x + tw / 2, y + th / 2));
                }
            }
            if (cands.Count == 0) return Array.Empty<Point2D>();

            var ordered = cands.OrderByDescending(c => c.RankScore).ToList();
            var outPts = new List<Point2D>();
            double minDist2 = Math.Max(tw, th) * Math.Max(tw, th) * 0.16; // 简单 NMS
            foreach (var c in ordered)
            {
                bool overlap = outPts.Any(p =>
                {
                    double dx = p.X - c.Cx, dy = p.Y - c.Cy;
                    return dx * dx + dy * dy < minDist2;
                });
                if (overlap) continue;
                outPts.Add(new Point2D(c.Cx, c.Cy));
                if (outPts.Count >= topN) break;
            }
            return outPts.ToArray();
        }

        private static Point2D[] TemplateToPolyline(bool[,] template, int maxPoints = 512)
        {
            var pts = CollectTruePoints(template);
            if (pts.Count < 8) return Array.Empty<Point2D>();
            double cx = pts.Average(p => p.X);
            double cy = pts.Average(p => p.Y);
            var ordered = pts
                .OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx))
                .Select(p => new Point2D(p.X, p.Y))
                .ToArray();
            if (ordered.Length <= maxPoints) return ordered;
            double stride = (double)ordered.Length / maxPoints;
            var sampled = new Point2D[maxPoints];
            for (int i = 0; i < maxPoints; i++)
                sampled[i] = ordered[(int)Math.Floor(i * stride)];
            return sampled;
        }

        private static ValueTuple<int[], int[], int[], int> BuildMatchedContoursFromTemplate(Point2D[] centers, Point2D[] templatePolyline, int factor)
        {
            if (centers == null || centers.Length == 0 || templatePolyline == null || templatePolyline.Length == 0)
                return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

            double tcx = templatePolyline.Average(p => p.X);
            double tcy = templatePolyline.Average(p => p.Y);
            var lens = new int[centers.Length];
            var outX = new List<int>(centers.Length * templatePolyline.Length);
            var outY = new List<int>(centers.Length * templatePolyline.Length);

            for (int i = 0; i < centers.Length; i++)
            {
                lens[i] = templatePolyline.Length;
                double dx = centers[i].X / factor - tcx;
                double dy = centers[i].Y / factor - tcy;
                for (int k = 0; k < templatePolyline.Length; k++)
                {
                    int x = (int)Math.Round((templatePolyline[k].X + dx) * factor);
                    int y = (int)Math.Round((templatePolyline[k].Y + dy) * factor);
                    outX.Add(x);
                    outY.Add(y);
                }
            }

            return (outX.ToArray(), outY.ToArray(), lens, centers.Length);
        }

        private static bool[,] BuildFusedTemplateFromContours(
            ValueTuple<int[], int[], int[], int> contourData, int templateBars, int canvasSize, double consensus)
        {
            var (flatX, flatY, contourLengths, contourCount) = contourData;
            if (contourCount <= 0 || contourLengths == null || contourLengths.Length == 0)
                return new bool[Math.Max(16, canvasSize), Math.Max(16, canvasSize)];

            templateBars = Math.Max(1, templateBars);
            canvasSize = Math.Max(16, canvasSize);
            consensus = Math.Max(0.01, Math.Min(1.0, consensus));

            var cand = new List<(int Start, int Len, double Area)>();
            int offset = 0;
            for (int ci = 0; ci < contourCount && ci < contourLengths.Length; ci++)
            {
                int len = contourLengths[ci];
                if (len >= 8 && offset + len <= flatX.Length && offset + len <= flatY.Length)
                {
                    var pts = new List<System.Drawing.Point>(len);
                    for (int i = 0; i < len; i++)
                        pts.Add(new System.Drawing.Point(flatX[offset + i], flatY[offset + i]));
                    cand.Add((offset, len, PolygonArea(pts)));
                }
                offset += Math.Max(0, len);
            }
            if (cand.Count == 0) return new bool[canvasSize, canvasSize];

            var selected = cand.OrderByDescending(c => c.Area).Take(templateBars).ToList();
            int globalMinX = int.MaxValue, globalMaxX = int.MinValue, globalMinY = int.MaxValue, globalMaxY = int.MinValue;
            foreach (var s in selected)
            {
                for (int i = 0; i < s.Len; i++)
                {
                    int x = flatX[s.Start + i], y = flatY[s.Start + i];
                    if (x < globalMinX) globalMinX = x;
                    if (x > globalMaxX) globalMaxX = x;
                    if (y < globalMinY) globalMinY = y;
                    if (y > globalMaxY) globalMaxY = y;
                }
            }

            int gw = Math.Max(1, globalMaxX - globalMinX + 1);
            int gh = Math.Max(1, globalMaxY - globalMinY + 1);
            int outW = Math.Max(canvasSize, gw + 4);
            int outH = Math.Max(canvasSize, gh + 4);
            int xPad = Math.Max(2, (outW - gw) / 2);
            int yPad = Math.Max(2, (outH - gh) / 2);

            int[,] acc = new int[outH, outW];
            foreach (var s in selected)
            {
                for (int i = 0; i < s.Len; i++)
                {
                    int x = flatX[s.Start + i], y = flatY[s.Start + i];
                    int nx = (x - globalMinX) + xPad;
                    int ny = (y - globalMinY) + yPad;
                    if (nx >= 0 && ny >= 0 && nx < outW && ny < outH)
                        acc[ny, nx]++;
                }
            }

            var bin = new bool[outH, outW];
            int th = Math.Max(1, (int)Math.Ceiling(selected.Count * consensus));
            for (int y = 0; y < outH; y++)
                for (int x = 0; x < outW; x++)
                    bin[y, x] = acc[y, x] >= th;
            return bin;
        }

        private static Point2D[] BestCyclicAlign(Point2D[] src, Point2D[] reference)
        {
            int n = src.Length;
            double bestErr = double.PositiveInfinity;
            Point2D[] best = src;

            void TryAlign(bool reverse)
            {
                for (int shift = 0; shift < n; shift++)
                {
                    double err = 0;
                    var aligned = new Point2D[n];
                    for (int i = 0; i < n; i++)
                    {
                        int idx = reverse ? (n - 1 - i + shift + n) % n : (i + shift) % n;
                        var p = src[idx];
                        aligned[i] = p;
                        double dx = p.X - reference[i].X;
                        double dy = p.Y - reference[i].Y;
                        err += dx * dx + dy * dy;
                    }
                    if (err < bestErr)
                    {
                        bestErr = err;
                        best = aligned;
                    }
                }
            }

            TryAlign(false);
            TryAlign(true);
            return best;
        }

        private static void DrawLineOnBinary(bool[,] canvas, int x0, int y0, int x1, int y1)
        {
            int w = canvas.GetLength(1), h = canvas.GetLength(0);
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                if (x0 >= 0 && y0 >= 0 && x0 < w && y0 < h) canvas[y0, x0] = true;
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        private static bool[,] BuildMutualTemplateFromContours(
            ValueTuple<int[], int[], int[], int> contourData, int templateBars, int canvasSize, int samplePoints)
        {
            var (flatX, flatY, contourLengths, contourCount) = contourData;
            if (contourCount <= 0 || contourLengths == null || contourLengths.Length == 0)
                return new bool[Math.Max(16, canvasSize), Math.Max(16, canvasSize)];

            templateBars = Math.Max(1, templateBars);
            canvasSize = Math.Max(16, canvasSize);
            samplePoints = Math.Max(24, samplePoints);

            var cand = new List<(int Start, int Len, double Area)>();
            int offset = 0;
            for (int ci = 0; ci < contourCount && ci < contourLengths.Length; ci++)
            {
                int len = contourLengths[ci];
                if (len >= 8 && offset + len <= flatX.Length && offset + len <= flatY.Length)
                {
                    var pts = new List<System.Drawing.Point>(len);
                    for (int i = 0; i < len; i++)
                        pts.Add(new System.Drawing.Point(flatX[offset + i], flatY[offset + i]));
                    cand.Add((offset, len, PolygonArea(pts)));
                }
                offset += Math.Max(0, len);
            }
            if (cand.Count == 0) return new bool[canvasSize, canvasSize];

            var selected = cand.OrderByDescending(c => c.Area).Take(templateBars).ToList();
            var centeredShapes = new List<Point2D[]>();
            var centroids = new List<Point2D>();
            foreach (var s in selected)
            {
                var pts = new Point2D[s.Len];
                for (int i = 0; i < s.Len; i++)
                    pts[i] = new Point2D(flatX[s.Start + i], flatY[s.Start + i]);
                var resampled = ResampleContourByArc(pts, samplePoints);
                if (resampled.Length == 0) continue;
                double cx = resampled.Average(p => p.X);
                double cy = resampled.Average(p => p.Y);
                centroids.Add(new Point2D(cx, cy));
                centeredShapes.Add(resampled.Select(p => new Point2D(p.X - cx, p.Y - cy)).ToArray());
            }
            if (centeredShapes.Count == 0) return new bool[canvasSize, canvasSize];

            var reference = centeredShapes[0];
            var aligned = new List<Point2D[]> { reference };
            for (int i = 1; i < centeredShapes.Count; i++)
                aligned.Add(BestCyclicAlign(centeredShapes[i], reference));

            var avgCentered = new Point2D[samplePoints];
            for (int i = 0; i < samplePoints; i++)
            {
                double sx = 0, sy = 0;
                for (int k = 0; k < aligned.Count; k++)
                {
                    sx += aligned[k][i].X;
                    sy += aligned[k][i].Y;
                }
                avgCentered[i] = new Point2D(sx / aligned.Count, sy / aligned.Count);
            }

            double meanCx = centroids.Average(c => c.X);
            double meanCy = centroids.Average(c => c.Y);
            var avg = avgCentered.Select(p => new Point2D(p.X + meanCx, p.Y + meanCy)).ToArray();

            double minX = avg.Min(p => p.X), maxX = avg.Max(p => p.X);
            double minY = avg.Min(p => p.Y), maxY = avg.Max(p => p.Y);
            int w = Math.Max(1, (int)Math.Ceiling(maxX - minX + 1));
            int h = Math.Max(1, (int)Math.Ceiling(maxY - minY + 1));
            int outW = Math.Max(canvasSize, w + 4);
            int outH = Math.Max(canvasSize, h + 4);
            int xPad = Math.Max(2, (outW - w) / 2);
            int yPad = Math.Max(2, (outH - h) / 2);

            var canvas = new bool[outH, outW];
            var pix = new (int X, int Y)[samplePoints];
            for (int i = 0; i < samplePoints; i++)
            {
                int x = (int)Math.Round(avg[i].X - minX) + xPad;
                int y = (int)Math.Round(avg[i].Y - minY) + yPad;
                pix[i] = (x, y);
            }
            for (int i = 0; i < samplePoints; i++)
            {
                var a = pix[i];
                var b = pix[(i + 1) % samplePoints];
                DrawLineOnBinary(canvas, a.X, a.Y, b.X, b.Y);
            }
            return canvas;
        }

        private static bool[,] ExtractCenterline(bool[,] bin)
        {
            int h = bin.GetLength(0), w = bin.GetLength(1);
            var outBin = new bool[h, w];

            // 行方向：取每行最左与最右的中点
            for (int y = 0; y < h; y++)
            {
                int left = -1, right = -1;
                for (int x = 0; x < w; x++)
                {
                    if (!bin[y, x]) continue;
                    if (left < 0) left = x;
                    right = x;
                }
                if (left >= 0 && right >= left)
                {
                    int cx = (left + right) / 2;
                    outBin[y, cx] = true;
                }
            }

            // 列方向：取每列最上与最下的中点（补全断裂）
            for (int x = 0; x < w; x++)
            {
                int top = -1, bottom = -1;
                for (int y = 0; y < h; y++)
                {
                    if (!bin[y, x]) continue;
                    if (top < 0) top = y;
                    bottom = y;
                }
                if (top >= 0 && bottom >= top)
                {
                    int cy = (top + bottom) / 2;
                    outBin[cy, x] = true;
                }
            }

            return outBin;
        }

        private static CalibImage BinaryToCalibImage(bool[,] bin)
        {
            int h = bin.GetLength(0), w = bin.GetLength(1);
            var img = new CalibImage(w, h, 1);
            var native = img.GetNativeStruct();
            var bytes = new byte[w * h];
            int idx = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                    bytes[idx++] = bin[y, x] ? (byte)255 : (byte)0;
            }
            Marshal.Copy(bytes, 0, native.data, bytes.Length);
            return img;
        }

        private static void FillGrayBytesFromCalib(CalibImage img, byte[] dstGray)
        {
            var n = img.GetNativeStruct();
            int w = n.width, h = n.height;
            int ch = n.channels;
            int nPix = w * h;
            if (dstGray.Length < nPix)
                throw new InvalidOperationException("内部缓冲区过小");
            if (ch == 1)
            {
                Marshal.Copy(n.data, dstGray, 0, nPix);
                return;
            }
            if (ch == 3)
            {
                var tmp = new byte[nPix * 3];
                Marshal.Copy(n.data, tmp, 0, tmp.Length);
                for (int p = 0; p < nPix; p++)
                {
                    int b = tmp[p * 3], g = tmp[p * 3 + 1], r = tmp[p * 3 + 2];
                    dstGray[p] = (byte)Math.Clamp((int)(0.299 * r + 0.587 * g + 0.114 * b), 0, 255);
                }
                return;
            }
            throw new InvalidOperationException($"二值图合并: 不支持的通道数 {ch}");
        }

        private static CalibImage BinaryMergeCalibImages(CalibImage imgA, CalibImage imgB, string mergeMode, int foregroundThreshold)
        {
            var na = imgA.GetNativeStruct();
            var nb = imgB.GetNativeStruct();
            if (na.width != nb.width || na.height != nb.height)
                throw new InvalidOperationException($"二值图合并: 尺寸须一致，当前 {na.width}x{na.height} 与 {nb.width}x{nb.height}");
            int w = na.width, h = na.height;
            int nPix = w * h;
            var ga = new byte[nPix];
            var gb = new byte[nPix];
            FillGrayBytesFromCalib(imgA, ga);
            FillGrayBytesFromCalib(imgB, gb);
            var ba = new byte[nPix];
            var bb = new byte[nPix];
            if (foregroundThreshold < 0)
            {
                Buffer.BlockCopy(ga, 0, ba, 0, nPix);
                Buffer.BlockCopy(gb, 0, bb, 0, nPix);
            }
            else
            {
                for (int i = 0; i < nPix; i++)
                {
                    ba[i] = (byte)(ga[i] > foregroundThreshold ? 255 : 0);
                    bb[i] = (byte)(gb[i] > foregroundThreshold ? 255 : 0);
                }
            }
            string m = (mergeMode ?? "or").Trim();
            if (m.Length == 0) m = "or";
            m = m.ToLowerInvariant();
            var dst = new byte[nPix];
            if (m == "and")
            {
                for (int i = 0; i < nPix; i++)
                    dst[i] = (byte)(ba[i] & bb[i]);
            }
            else if (m == "xor")
            {
                for (int i = 0; i < nPix; i++)
                    dst[i] = (byte)(ba[i] ^ bb[i]);
            }
            else
            {
                for (int i = 0; i < nPix; i++)
                    dst[i] = (byte)(ba[i] | bb[i]);
            }
            var outImg = new CalibImage(w, h, 1);
            var no = outImg.GetNativeStruct();
            Marshal.Copy(dst, 0, no.data, nPix);
            return outImg;
        }

        private static string NormalizeGrayBlendMode(string? blendMode)
        {
            string m = (blendMode ?? "weighted").Trim();
            if (m.Length == 0) m = "weighted";
            m = m.ToLowerInvariant();
            if (m == "sum") return "add";
            if (m == "sub") return "subtract";
            if (m == "blend" || m == "mix" || m == "linear") return "weighted";
            return m;
        }

        private static CalibImage GrayMergeCalibImages(CalibImage imgA, CalibImage imgB, string? blendMode, double ratioA)
        {
            var na = imgA.GetNativeStruct();
            var nb = imgB.GetNativeStruct();
            if (na.width != nb.width || na.height != nb.height)
                throw new InvalidOperationException($"灰度合并: 尺寸须一致，当前 {na.width}x{na.height} 与 {nb.width}x{nb.height}");
            int nPix = na.width * na.height;
            var ga = new byte[nPix];
            var gb = new byte[nPix];
            FillGrayBytesFromCalib(imgA, ga);
            FillGrayBytesFromCalib(imgB, gb);
            var dst = new byte[nPix];
            string m = NormalizeGrayBlendMode(blendMode);
            if (m == "add")
            {
                for (int i = 0; i < nPix; i++)
                    dst[i] = (byte)Math.Clamp((int)ga[i] + (int)gb[i], 0, 255);
            }
            else if (m == "subtract")
            {
                for (int i = 0; i < nPix; i++)
                    dst[i] = (byte)Math.Clamp((int)ga[i] - (int)gb[i], 0, 255);
            }
            else
            {
                ratioA = Math.Clamp(ratioA, 0.0, 1.0);
                double wB = 1.0 - ratioA;
                for (int i = 0; i < nPix; i++)
                    dst[i] = (byte)Math.Clamp(Math.Round(ratioA * ga[i] + wB * gb[i]), 0.0, 255.0);
            }
            var outImg = new CalibImage(na.width, na.height, 1);
            var no = outImg.GetNativeStruct();
            Marshal.Copy(dst, 0, no.data, nPix);
            return outImg;
        }

        /// <summary>
        /// 按灰度直方图分位数确定 [low,high]，区间内为 255，否则 0。excludeLowPercent / excludeHighPercent 为要剥离的暗端、亮端像素占全图比例（0~100）。
        /// </summary>
        private static CalibImage GrayRangeBinaryPercentile(CalibImage src, double excludeLowPercent, double excludeHighPercent, out int usedLow, out int usedHigh)
        {
            var n = src.GetNativeStruct();
            int w = n.width, h = n.height;
            int count = w * h;
            if (count <= 0)
                throw new InvalidOperationException("灰度范围二值化: 图像为空");

            excludeLowPercent = Math.Clamp(excludeLowPercent, 0.0, 100.0);
            excludeHighPercent = Math.Clamp(excludeHighPercent, 0.0, 100.0);
            if (excludeLowPercent + excludeHighPercent >= 100.0)
                throw new InvalidOperationException($"灰度范围二值化: 两端剔除比例之和须小于 100%（当前 {excludeLowPercent + excludeHighPercent:F1}%）");

            var gray = new byte[count];
            FillGrayBytesFromCalib(src, gray);
            var hist = new int[256];
            for (int i = 0; i < count; i++)
                hist[gray[i]]++;

            double total = count;
            double lowMass = excludeLowPercent * 0.01 * total;
            double highMass = (100.0 - excludeHighPercent) * 0.01 * total;

            usedLow = 255;
            int cum = 0;
            for (int i = 0; i < 256; i++)
            {
                cum += hist[i];
                if (cum >= lowMass - 1e-9)
                {
                    usedLow = i;
                    break;
                }
            }

            usedHigh = 0;
            cum = 0;
            for (int i = 0; i < 256; i++)
            {
                cum += hist[i];
                if (cum >= highMass - 1e-9)
                {
                    usedHigh = i;
                    break;
                }
            }

            if (usedLow > usedHigh)
                (usedLow, usedHigh) = (usedHigh, usedLow);

            var bin = new byte[count];
            for (int i = 0; i < count; i++)
            {
                byte g = gray[i];
                bin[i] = (g >= usedLow && g <= usedHigh) ? (byte)255 : (byte)0;
            }

            var outImg = new CalibImage(w, h, 1);
            var no = outImg.GetNativeStruct();
            Marshal.Copy(bin, 0, no.data, count);
            return outImg;
        }

        /// <summary>矩形灰度形态：腐蚀=min、膨胀=max；输出单通道灰度。</summary>
        private static CalibImage GrayMorphRectCalibImage(CalibImage src, bool dilate, int kh, int kw)
        {
            var n = src.GetNativeStruct();
            int w = n.width, h = n.height;
            int nPix = w * h;
            var gray = new byte[nPix];
            FillGrayBytesFromCalib(src, gray);
            var dst = new byte[nPix];
            int rh = kh / 2;
            int rw = kw / 2;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte v = dilate ? (byte)0 : (byte)255;
                    for (int dy = -rh; dy <= rh; dy++)
                    {
                        int yy = Math.Clamp(y + dy, 0, h - 1);
                        int row = yy * w;
                        for (int dx = -rw; dx <= rw; dx++)
                        {
                            int xx = Math.Clamp(x + dx, 0, w - 1);
                            byte g = gray[row + xx];
                            if (dilate)
                            {
                                if (g > v) v = g;
                            }
                            else
                            {
                                if (g < v) v = g;
                            }
                        }
                    }
                    dst[y * w + x] = v;
                }
            }
            var outImg = new CalibImage(w, h, 1);
            var no = outImg.GetNativeStruct();
            Marshal.Copy(dst, 0, no.data, nPix);
            return outImg;
        }

        private static CalibImage SobelEdgeImage(CalibImage src, int threshold)
        {
            using var bmp = src.ToBitmap();
            if (bmp == null) throw new InvalidOperationException("Sobel: 输入图像转换失败");
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * w + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                }
            }

            threshold = Math.Max(0, Math.Min(255, threshold));
            var outBytes = new byte[w * h];
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int p00 = gray[(y - 1) * w + (x - 1)], p01 = gray[(y - 1) * w + x], p02 = gray[(y - 1) * w + (x + 1)];
                    int p10 = gray[y * w + (x - 1)],     p12 = gray[y * w + (x + 1)];
                    int p20 = gray[(y + 1) * w + (x - 1)], p21 = gray[(y + 1) * w + x], p22 = gray[(y + 1) * w + (x + 1)];

                    int gx = -p00 + p02 - 2 * p10 + 2 * p12 - p20 + p22;
                    int gy = -p00 - 2 * p01 - p02 + p20 + 2 * p21 + p22;
                    int mag = (int)Math.Sqrt(gx * gx + gy * gy);
                    outBytes[y * w + x] = mag >= threshold ? (byte)255 : (byte)0;
                }
            }

            var edge = new CalibImage(w, h, 1);
            var native = edge.GetNativeStruct();
            Marshal.Copy(outBytes, 0, native.data, outBytes.Length);
            return edge;
        }

        /// <summary>
        /// OpenCV 风格 Scharr 3×3，梯度幅值二值化输出。
        /// Gx: [-3,0,3; -10,0,10; -3,0,3], Gy: [-3,-10,-3; 0,0,0; 3,10,3]
        /// </summary>
        private static CalibImage ScharrEdgeImage(CalibImage src, int threshold)
        {
            using var bmp = src.ToBitmap();
            if (bmp == null) throw new InvalidOperationException("Scharr: 输入图像转换失败");
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * w + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                }
            }

            threshold = Math.Max(0, Math.Min(255, threshold));
            var outBytes = new byte[w * h];
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int p00 = gray[(y - 1) * w + (x - 1)], p01 = gray[(y - 1) * w + x], p02 = gray[(y - 1) * w + (x + 1)];
                    int p10 = gray[y * w + (x - 1)], p12 = gray[y * w + (x + 1)];
                    int p20 = gray[(y + 1) * w + (x - 1)], p21 = gray[(y + 1) * w + x], p22 = gray[(y + 1) * w + (x + 1)];

                    int gx = -3 * p00 + 3 * p02 - 10 * p10 + 10 * p12 - 3 * p20 + 3 * p22;
                    int gy = -3 * p00 - 10 * p01 - 3 * p02 + 3 * p20 + 10 * p21 + 3 * p22;
                    int mag = (int)Math.Sqrt(gx * gx + gy * gy);
                    outBytes[y * w + x] = mag >= threshold ? (byte)255 : (byte)0;
                }
            }

            var edge = new CalibImage(w, h, 1);
            var native = edge.GetNativeStruct();
            Marshal.Copy(outBytes, 0, native.data, outBytes.Length);
            return edge;
        }

        private static int NextPow2(int v)
        {
            v = Math.Max(1, v);
            int p = 1;
            while (p < v) p <<= 1;
            return p;
        }

        private static void FFT1D(Complex[] a, bool inverse)
        {
            int n = a.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) (a[i], a[j]) = (a[j], a[i]);
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
                Complex wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
                for (int i = 0; i < n; i += len)
                {
                    Complex w = Complex.One;
                    int half = len >> 1;
                    for (int j = 0; j < half; j++)
                    {
                        Complex u = a[i + j];
                        Complex v = a[i + j + half] * w;
                        a[i + j] = u + v;
                        a[i + j + half] = u - v;
                        w *= wlen;
                    }
                }
            }
            if (inverse)
            {
                for (int i = 0; i < n; i++) a[i] /= n;
            }
        }

        private static void FFT2D(Complex[,] data, bool inverse)
        {
            int h = data.GetLength(0), w = data.GetLength(1);
            var row = new Complex[w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) row[x] = data[y, x];
                FFT1D(row, inverse);
                for (int x = 0; x < w; x++) data[y, x] = row[x];
            }

            var col = new Complex[h];
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++) col[y] = data[y, x];
                FFT1D(col, inverse);
                for (int y = 0; y < h; y++) data[y, x] = col[y];
            }
        }

        /// <summary>
        /// 频域相位检测器（纯 C# FFT）：
        /// 2D FFT -> Log-Gabor 带通 -> 相位归一化(phase-only) -> iFFT -> 阈值边缘
        /// </summary>
        private static (CalibImage Response, CalibImage Edge) PhaseCongruencyEdgeImage(CalibImage src, double threshold, double noiseSigma, int blurKsize, string? debugDumpPrefix = null)
        {
            using var bmp = src.ToBitmap();
            if (bmp == null) throw new InvalidOperationException("PhaseCongruency: 输入图像转换失败");
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * w + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                }
            }

            blurKsize = Math.Max(1, blurKsize);
            if ((blurKsize & 1) == 0) blurKsize += 1;
            threshold = Math.Max(0.0, Math.Min(1.0, threshold));
            noiseSigma = Math.Max(0.0, Math.Min(1.0, noiseSigma));

            var srcF = new float[w * h];
            for (int i = 0; i < gray.Length; i++) srcF[i] = gray[i] / 255f;
            if (blurKsize > 1)
            {
                int[] k = blurKsize <= 3 ? new[] { 1, 2, 1 } : new[] { 1, 4, 6, 4, 1 };
                int kr = k.Length / 2;
                float sum = k.Sum();
                var tmp = new float[w * h];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float acc = 0;
                        for (int i = -kr; i <= kr; i++)
                        {
                            int nx = Math.Max(0, Math.Min(w - 1, x + i));
                            acc += srcF[y * w + nx] * k[i + kr];
                        }
                        tmp[y * w + x] = acc / sum;
                    }
                }
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float acc = 0;
                        for (int i = -kr; i <= kr; i++)
                        {
                            int ny = Math.Max(0, Math.Min(h - 1, y + i));
                            acc += tmp[ny * w + x] * k[i + kr];
                        }
                        srcF[y * w + x] = acc / sum;
                    }
                }
            }

            // 控制频域计算规模（保持实时可用）
            int maxDim = 512;
            int dw = w, dh = h;
            int ds = 1;
            while (Math.Max(dw, dh) > maxDim)
            {
                ds <<= 1;
                dw = Math.Max(1, w / ds);
                dh = Math.Max(1, h / ds);
            }

            var small = new float[dw * dh];
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                    small[y * dw + x] = srcF[Math.Min(h - 1, y * ds) * w + Math.Min(w - 1, x * ds)];

            int fw = NextPow2(dw);
            int fh = NextPow2(dh);
            var spec = new Complex[fh, fw];
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                    spec[y, x] = new Complex(small[y * dw + x], 0);
            FFT2D(spec, inverse: false);

            // Log-Gabor 带通（频域），centerFreq 与阈值联动：阈值越低频带越宽
            double centerFreq = 0.22;
            double sigmaOnf = 0.55 + 0.25 * (1.0 - threshold);
            double logSigma = Math.Log(Math.Max(1.05, sigmaOnf));
            if (Math.Abs(logSigma) < 1e-9) logSigma = 0.2;

            var outBytes = new byte[w * h];
            var respBytes = new byte[w * h];
            const double eps = 1e-9;
            var bandSpec = new Complex[fh, fw];
            var phaseSpec = new Complex[fh, fw];
            for (int y = 0; y < fh; y++)
            {
                double fy = (y <= fh / 2 ? y : y - fh) / (double)fh;
                for (int x = 0; x < fw; x++)
                {
                    double fx = (x <= fw / 2 ? x : x - fw) / (double)fw;
                    double r = Math.Sqrt(fx * fx + fy * fy);
                    if (r < 1e-9)
                    {
                        bandSpec[y, x] = Complex.Zero;
                        phaseSpec[y, x] = Complex.Zero;
                        continue;
                    }

                    double lr = Math.Log(r / centerFreq);
                    double lg = Math.Exp(-(lr * lr) / (2.0 * logSigma * logSigma));
                    Complex f = spec[y, x];
                    double mag = f.Magnitude + eps;
                    bandSpec[y, x] = lg * f;           // 保留幅值（结构更接近原图）
                    phaseSpec[y, x] = (lg / mag) * f;  // phase-only（边缘增强）
                }
            }

            FFT2D(bandSpec, inverse: true);
            FFT2D(phaseSpec, inverse: true);

            // 统计带通响应和相位响应范围（小图）
            double minBand = double.MaxValue, maxBand = double.MinValue;
            double minPhase = double.MaxValue, maxPhase = double.MinValue;
            for (int y = 0; y < dh; y++)
            {
                for (int x = 0; x < dw; x++)
                {
                    double vb = bandSpec[y, x].Magnitude;
                    double vp = phaseSpec[y, x].Magnitude;
                    if (vb < minBand) minBand = vb;
                    if (vb > maxBand) maxBand = vb;
                    if (vp < minPhase) minPhase = vp;
                    if (vp > maxPhase) maxPhase = vp;
                }
            }
            double bandRange = Math.Max(1e-9, maxBand - minBand);
            double phaseRange = Math.Max(1e-9, maxPhase - minPhase);
            double th = Math.Max(0.0, Math.Min(1.0, threshold));
            double damp = Math.Max(0.0, Math.Min(1.0, noiseSigma));

            var response01 = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(dh - 1, y / ds);
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(dw - 1, x / ds);
                    double vb = (bandSpec[sy, sx].Magnitude - minBand) / bandRange;
                    double vp = (phaseSpec[sy, sx].Magnitude - minPhase) / phaseRange;
                    // 响应图以保幅结构为主，叠加少量 phase-only 增强边缘
                    double v = 0.78 * vb + 0.22 * vp;
                    v *= (1.0 - 0.45 * damp);
                    response01[y * w + x] = (float)v;
                    int rv = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, v)) * 255.0);
                    respBytes[y * w + x] = (byte)rv;
                }
            }

            // Edge: 基于响应图做 Canny 风格后处理（梯度 -> NMS -> 双阈值连通）
            var gxArr = new float[w * h];
            var gyArr = new float[w * h];
            var gradMag = new float[w * h];
            for (int y = 1; y < h - 1; y++)
            {
                int y0 = (y - 1) * w, y1 = y * w, y2 = (y + 1) * w;
                for (int x = 1; x < w - 1; x++)
                {
                    float p00 = response01[y0 + x - 1], p01 = response01[y0 + x], p02 = response01[y0 + x + 1];
                    float p10 = response01[y1 + x - 1],                          p12 = response01[y1 + x + 1];
                    float p20 = response01[y2 + x - 1], p21 = response01[y2 + x], p22 = response01[y2 + x + 1];
                    float gx = -p00 + p02 - 2f * p10 + 2f * p12 - p20 + p22;
                    float gy = -p00 - 2f * p01 - p02 + p20 + 2f * p21 + p22;
                    gxArr[y1 + x] = gx;
                    gyArr[y1 + x] = gy;
                    gradMag[y1 + x] = (float)Math.Sqrt(gx * gx + gy * gy);
                }
            }

            var nms = new float[w * h];
            var nmsVals = new List<float>(Math.Max(1, (w - 2) * (h - 2)));
            for (int y = 1; y < h - 1; y++)
            {
                int row = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    int idx = row + x;
                    float gm = gradMag[idx];
                    if (gm <= 1e-9f) continue;

                    float ang = (float)(Math.Atan2(gyArr[idx], gxArr[idx]) * 180.0 / Math.PI);
                    if (ang < 0f) ang += 180f;

                    float n1, n2;
                    if ((ang >= 0f && ang < 22.5f) || (ang >= 157.5f && ang <= 180f))
                    {
                        n1 = gradMag[idx - 1];
                        n2 = gradMag[idx + 1];
                    }
                    else if (ang >= 22.5f && ang < 67.5f)
                    {
                        n1 = gradMag[idx - w + 1];
                        n2 = gradMag[idx + w - 1];
                    }
                    else if (ang >= 67.5f && ang < 112.5f)
                    {
                        n1 = gradMag[idx - w];
                        n2 = gradMag[idx + w];
                    }
                    else
                    {
                        n1 = gradMag[idx - w - 1];
                        n2 = gradMag[idx + w + 1];
                    }

                    if (gm >= n1 && gm >= n2)
                    {
                        nms[idx] = gm;
                        nmsVals.Add(gm);
                    }
                }
            }

            nmsVals.Sort();
            float q70 = nmsVals.Count > 0 ? nmsVals[(int)(0.70 * (nmsVals.Count - 1))] : 0f;
            float q92 = nmsVals.Count > 0 ? nmsVals[(int)(0.92 * (nmsVals.Count - 1))] : 0f;
            float tBlend = (float)Math.Max(0.0, Math.Min(1.0, th));
            float high = q70 + (q92 - q70) * tBlend;
            high *= (float)(1.0 - 0.25 * damp);
            float low = high * (0.38f + 0.15f * (float)damp);

            var marks = new byte[w * h]; // 0=none,1=weak,2=strong
            var q = new Queue<int>();
            for (int y = 1; y < h - 1; y++)
            {
                int row = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    int idx = row + x;
                    float v = nms[idx];
                    if (v >= high)
                    {
                        marks[idx] = 2;
                        q.Enqueue(idx);
                    }
                    else if (v >= low)
                    {
                        marks[idx] = 1;
                    }
                }
            }

            // hysteresis: 保留与强边连通的弱边
            while (q.Count > 0)
            {
                int idx = q.Dequeue();
                int cy = idx / w;
                int cx = idx - cy * w;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cy + dy;
                    if (ny <= 0 || ny >= h - 1) continue;
                    int nrow = ny * w;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = cx + dx;
                        if (nx <= 0 || nx >= w - 1) continue;
                        int ni = nrow + nx;
                        if (marks[ni] == 1)
                        {
                            marks[ni] = 2;
                            q.Enqueue(ni);
                        }
                    }
                }
            }

            for (int i = 0; i < outBytes.Length; i++)
                outBytes[i] = marks[i] == 2 ? (byte)255 : (byte)0;

            if (!string.IsNullOrWhiteSpace(debugDumpPrefix))
            {
                try
                {
                    string prefix = System.IO.Path.GetFullPath(debugDumpPrefix.Trim());
                    string? dir = System.IO.Path.GetDirectoryName(prefix);
                    if (!string.IsNullOrWhiteSpace(dir))
                        System.IO.Directory.CreateDirectory(dir);

                    // 响应图（0~1 -> 0~255）
                    using var respBmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int v = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, response01[y * w + x])) * 255.0);
                            var c = System.Drawing.Color.FromArgb(v, v, v);
                            respBmp.SetPixel(x, y, c);
                        }
                    }
                    respBmp.Save(prefix + "_response.bmp", System.Drawing.Imaging.ImageFormat.Bmp);

                    // 二值图
                    using var binBmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            byte b = outBytes[y * w + x];
                            var c = System.Drawing.Color.FromArgb(b, b, b);
                            binBmp.SetPixel(x, y, c);
                        }
                    }
                    binBmp.Save(prefix + "_binary.bmp", System.Drawing.Imaging.ImageFormat.Bmp);
                }
                catch
                {
                    // 调试输出失败不影响主流程
                }
            }

            var resp = new CalibImage(w, h, 1);
            var respNative = resp.GetNativeStruct();
            Marshal.Copy(respBytes, 0, respNative.data, respBytes.Length);

            var edge = new CalibImage(w, h, 1);
            var native = edge.GetNativeStruct();
            Marshal.Copy(outBytes, 0, native.data, outBytes.Length);
            return (resp, edge);
        }

        private static (CalibImage Filtered, CalibImage Binary) FrequencyFilterToBinaryImage(
            CalibImage src, string mode, double lowCut, double highCut, double threshold, bool useOtsu)
        {
            using var bmp = src.ToBitmap();
            if (bmp == null) throw new InvalidOperationException("频域滤波二值化: 输入图像转换失败");
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * w + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                }
            }

            mode = (mode ?? "bandpass").Trim().ToLowerInvariant();
            if (mode != "lowpass" && mode != "highpass" && mode != "bandpass") mode = "bandpass";
            lowCut = Math.Max(0.0, Math.Min(0.5, lowCut));
            highCut = Math.Max(0.0, Math.Min(0.5, highCut));
            if (highCut < lowCut)
            {
                double t = lowCut;
                lowCut = highCut;
                highCut = t;
            }
            threshold = Math.Max(0.0, Math.Min(1.0, threshold));

            int fw = NextPow2(w);
            int fh = NextPow2(h);
            var spec = new Complex[fh, fw];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                    spec[y, x] = new Complex(gray[y * w + x] / 255.0, 0);
            }
            FFT2D(spec, inverse: false);

            for (int y = 0; y < fh; y++)
            {
                double fy = (y <= fh / 2 ? y : y - fh) / (double)fh;
                for (int x = 0; x < fw; x++)
                {
                    double fx = (x <= fw / 2 ? x : x - fw) / (double)fw;
                    double r = Math.Sqrt(fx * fx + fy * fy);
                    bool keep = mode switch
                    {
                        "lowpass" => r <= highCut,
                        "highpass" => r >= lowCut,
                        _ => r >= lowCut && r <= highCut
                    };
                    if (!keep) spec[y, x] = Complex.Zero;
                }
            }

            FFT2D(spec, inverse: true);
            var filtered = new float[w * h];
            double vmin = double.MaxValue, vmax = double.MinValue;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double v = spec[y, x].Magnitude;
                    filtered[y * w + x] = (float)v;
                    if (v < vmin) vmin = v;
                    if (v > vmax) vmax = v;
                }
            }
            double vr = Math.Max(1e-9, vmax - vmin);
            var filteredBytes = new byte[w * h];
            for (int i = 0; i < filtered.Length; i++)
                filteredBytes[i] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round((filtered[i] - vmin) / vr * 255.0)));

            int binTh;
            if (useOtsu)
            {
                int[] hist = new int[256];
                for (int i = 0; i < filteredBytes.Length; i++) hist[filteredBytes[i]]++;
                int total = filteredBytes.Length;
                double sum = 0;
                for (int i = 0; i < 256; i++) sum += i * hist[i];
                double sumB = 0;
                int wB = 0;
                double maxVar = -1;
                int best = 127;
                for (int t = 0; t < 256; t++)
                {
                    wB += hist[t];
                    if (wB == 0) continue;
                    int wF = total - wB;
                    if (wF == 0) break;
                    sumB += t * hist[t];
                    double mB = sumB / wB;
                    double mF = (sum - sumB) / wF;
                    double between = wB * (double)wF * (mB - mF) * (mB - mF);
                    if (between > maxVar)
                    {
                        maxVar = between;
                        best = t;
                    }
                }
                binTh = best;
            }
            else
            {
                binTh = (int)Math.Round(threshold * 255.0);
            }

            var binBytes = new byte[w * h];
            for (int i = 0; i < binBytes.Length; i++)
                binBytes[i] = filteredBytes[i] >= binTh ? (byte)255 : (byte)0;

            var filteredImg = new CalibImage(w, h, 1);
            var fNative = filteredImg.GetNativeStruct();
            Marshal.Copy(filteredBytes, 0, fNative.data, filteredBytes.Length);

            var binaryImg = new CalibImage(w, h, 1);
            var bNative = binaryImg.GetNativeStruct();
            Marshal.Copy(binBytes, 0, bNative.data, binBytes.Length);
            return (filteredImg, binaryImg);
        }

        private static (CalibImage Filtered, CalibImage Binary) LocalFreqSauvolaNiblackImage(
            CalibImage src, string method, int windowSize, double k, double r, double lowCut, double highCut)
        {
            using var bmp = src.ToBitmap();
            if (bmp == null) throw new InvalidOperationException("局部频域阈值: 输入图像转换失败");
            int w = bmp.Width, h = bmp.Height;
            var gray = new double[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * w + x] = ((c.R * 30 + c.G * 59 + c.B * 11) / 100.0) / 255.0;
                }
            }

            method = (method ?? "sauvola").Trim().ToLowerInvariant();
            if (method != "sauvola" && method != "niblack") method = "sauvola";
            windowSize = Math.Max(3, windowSize);
            if ((windowSize & 1) == 0) windowSize += 1;
            int wr = windowSize / 2;
            r = Math.Max(1e-6, Math.Min(1.0, r));
            lowCut = Math.Max(0.0, Math.Min(0.5, lowCut));
            highCut = Math.Max(0.0, Math.Min(0.5, highCut));
            if (highCut < lowCut)
            {
                double tmp = lowCut;
                lowCut = highCut;
                highCut = tmp;
            }

            // 频域带通
            int fw = NextPow2(w);
            int fh = NextPow2(h);
            var spec = new Complex[fh, fw];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    spec[y, x] = new Complex(gray[y * w + x], 0);
            FFT2D(spec, inverse: false);

            for (int y = 0; y < fh; y++)
            {
                double fy = (y <= fh / 2 ? y : y - fh) / (double)fh;
                for (int x = 0; x < fw; x++)
                {
                    double fx = (x <= fw / 2 ? x : x - fw) / (double)fw;
                    double fr = Math.Sqrt(fx * fx + fy * fy);
                    if (!(fr >= lowCut && fr <= highCut))
                        spec[y, x] = Complex.Zero;
                }
            }

            FFT2D(spec, inverse: true);
            var filtered01 = new double[w * h];
            double fmin = double.MaxValue, fmax = double.MinValue;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double v = spec[y, x].Magnitude;
                    filtered01[y * w + x] = v;
                    if (v < fmin) fmin = v;
                    if (v > fmax) fmax = v;
                }
            }
            double frange = Math.Max(1e-9, fmax - fmin);
            var filteredBytes = new byte[w * h];
            for (int i = 0; i < filtered01.Length; i++)
            {
                filtered01[i] = (filtered01[i] - fmin) / frange;
                filteredBytes[i] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(filtered01[i] * 255.0)));
            }

            // 积分图用于局部均值/方差
            int iw = w + 1, ih = h + 1;
            var integ = new double[iw * ih];
            var integ2 = new double[iw * ih];
            for (int y = 1; y <= h; y++)
            {
                double row = 0, row2 = 0;
                int srcRow = (y - 1) * w;
                int iiRow = y * iw;
                int iiPrev = (y - 1) * iw;
                for (int x = 1; x <= w; x++)
                {
                    double v = filtered01[srcRow + (x - 1)];
                    row += v;
                    row2 += v * v;
                    integ[iiRow + x] = integ[iiPrev + x] + row;
                    integ2[iiRow + x] = integ2[iiPrev + x] + row2;
                }
            }

            static double RectSum(double[] itg, int stride, int x0, int y0, int x1, int y1)
            {
                return itg[y1 * stride + x1] - itg[y0 * stride + x1] - itg[y1 * stride + x0] + itg[y0 * stride + x0];
            }

            var bin = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - wr);
                int y1 = Math.Min(h - 1, y + wr);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - wr);
                    int x1 = Math.Min(w - 1, x + wr);
                    int ax0 = x0, ay0 = y0, ax1 = x1 + 1, ay1 = y1 + 1;
                    int area = (x1 - x0 + 1) * (y1 - y0 + 1);
                    double sum = RectSum(integ, iw, ax0, ay0, ax1, ay1);
                    double sum2 = RectSum(integ2, iw, ax0, ay0, ax1, ay1);
                    double mean = sum / Math.Max(1, area);
                    double var = Math.Max(0.0, sum2 / Math.Max(1, area) - mean * mean);
                    double std = Math.Sqrt(var);
                    double t = method == "niblack"
                        ? mean + k * std
                        : mean * (1.0 + k * ((std / r) - 1.0));
                    bin[y * w + x] = filtered01[y * w + x] >= t ? (byte)255 : (byte)0;
                }
            }

            var filteredImg = new CalibImage(w, h, 1);
            var fNative = filteredImg.GetNativeStruct();
            Marshal.Copy(filteredBytes, 0, fNative.data, filteredBytes.Length);

            var binaryImg = new CalibImage(w, h, 1);
            var bNative = binaryImg.GetNativeStruct();
            Marshal.Copy(bin, 0, bNative.data, bin.Length);
            return (filteredImg, binaryImg);
        }

        private static (int WhitePixels, double WhiteRatio) AnalyzeBinaryImage(CalibImage img)
        {
            var native = img.GetNativeStruct();
            if (native.data == IntPtr.Zero || native.width <= 0 || native.height <= 0)
                return (0, 0);
            int len = native.width * native.height;
            var bytes = new byte[len];
            Marshal.Copy(native.data, bytes, 0, len);
            int white = 0;
            for (int i = 0; i < len; i++) if (bytes[i] > 0) white++;
            return (white, len > 0 ? (double)white / len : 0);
        }

        private static CalibImage PreFilterImage(CalibImage src, string mode, int ksize)
        {
            using var bmp = src.ToBitmap();
            if (bmp == null) throw new InvalidOperationException("预滤波: 输入图像转换失败");
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * w + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                }
            }

            ksize = Math.Max(3, ksize);
            if ((ksize & 1) == 0) ksize += 1;
            int r = ksize / 2;
            var outBytes = new byte[w * h];
            mode = (mode ?? "gaussian").Trim().ToLowerInvariant();

            if (mode == "median")
            {
                var win = new List<byte>(ksize * ksize);
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        win.Clear();
                        for (int dy = -r; dy <= r; dy++)
                        {
                            int ny = Math.Max(0, Math.Min(h - 1, y + dy));
                            for (int dx = -r; dx <= r; dx++)
                            {
                                int nx = Math.Max(0, Math.Min(w - 1, x + dx));
                                win.Add(gray[ny * w + nx]);
                            }
                        }
                        win.Sort();
                        outBytes[y * w + x] = win[win.Count / 2];
                    }
                }
            }
            else
            {
                // 简单可分离高斯近似：binomial 权重
                int[] k = ksize switch
                {
                    3 => new[] { 1, 2, 1 },
                    5 => new[] { 1, 4, 6, 4, 1 },
                    _ => new[] { 1, 4, 6, 4, 1 }
                };
                int kr = k.Length / 2;
                int sum = k.Sum();
                var tmp = new int[w * h];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int acc = 0;
                        for (int i = -kr; i <= kr; i++)
                        {
                            int nx = Math.Max(0, Math.Min(w - 1, x + i));
                            acc += gray[y * w + nx] * k[i + kr];
                        }
                        tmp[y * w + x] = acc / sum;
                    }
                }
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int acc = 0;
                        for (int i = -kr; i <= kr; i++)
                        {
                            int ny = Math.Max(0, Math.Min(h - 1, y + i));
                            acc += tmp[ny * w + x] * k[i + kr];
                        }
                        outBytes[y * w + x] = (byte)Math.Max(0, Math.Min(255, acc / sum));
                    }
                }
            }

            var output = new CalibImage(w, h, 1);
            var native = output.GetNativeStruct();
            Marshal.Copy(outBytes, 0, native.data, outBytes.Length);
            return output;
        }

        private static CalibImage NLMeansDenoiseImage(CalibImage src, double hStrength, int searchWindow, int templateWindow)
        {
            using var bmp = src.ToBitmap();
            if (bmp == null) throw new InvalidOperationException("NLMeans: 输入图像转换失败");
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * w + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                }
            }

            searchWindow = Math.Max(3, searchWindow);
            if ((searchWindow & 1) == 0) searchWindow += 1;
            templateWindow = Math.Max(3, templateWindow);
            if ((templateWindow & 1) == 0) templateWindow += 1;
            // 控制计算复杂度，避免 UI 卡死
            searchWindow = Math.Min(17, searchWindow);
            templateWindow = Math.Min(5, templateWindow);

            int sr = searchWindow / 2;
            int tr = templateWindow / 2;
            int patchArea = templateWindow * templateWindow;
            double hh = Math.Max(1e-6, hStrength * hStrength * patchArea);

            var outBytes = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double wSum = 0.0;
                    double vSum = 0.0;

                    for (int dy = -sr; dy <= sr; dy++)
                    {
                        int ny = Math.Max(0, Math.Min(h - 1, y + dy));
                        for (int dx = -sr; dx <= sr; dx++)
                        {
                            int nx = Math.Max(0, Math.Min(w - 1, x + dx));
                            double dist2 = 0.0;
                            for (int py = -tr; py <= tr; py++)
                            {
                                int y1 = Math.Max(0, Math.Min(h - 1, y + py));
                                int y2 = Math.Max(0, Math.Min(h - 1, ny + py));
                                int row1 = y1 * w;
                                int row2 = y2 * w;
                                for (int px = -tr; px <= tr; px++)
                                {
                                    int x1 = Math.Max(0, Math.Min(w - 1, x + px));
                                    int x2 = Math.Max(0, Math.Min(w - 1, nx + px));
                                    int d = gray[row1 + x1] - gray[row2 + x2];
                                    dist2 += d * d;
                                }
                            }

                            double weight = Math.Exp(-dist2 / hh);
                            wSum += weight;
                            vSum += weight * gray[ny * w + nx];
                        }
                    }

                    int outV = (int)Math.Round(vSum / Math.Max(1e-9, wSum));
                    outBytes[y * w + x] = (byte)Math.Max(0, Math.Min(255, outV));
                }
            }

            var output = new CalibImage(w, h, 1);
            var native = output.GetNativeStruct();
            Marshal.Copy(outBytes, 0, native.data, outBytes.Length);
            return output;
        }

        private static Point2D[] FilterPointsInImage(Point2D[] points, int width, int height)
        {
            if (points == null || points.Length == 0) return Array.Empty<Point2D>();
            return points.Where(p => p.X >= 0 && p.X < width && p.Y >= 0 && p.Y < height).ToArray();
        }

        private static double PolygonArea(IReadOnlyList<System.Drawing.Point> pts)
        {
            if (pts == null || pts.Count < 3) return 0;
            double area2 = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var p1 = pts[i];
                var p2 = pts[(i + 1) % pts.Count];
                area2 += (double)p1.X * p2.Y - (double)p2.X * p1.Y;
            }
            return Math.Abs(area2) * 0.5;
        }

        private static CalibImage BuildMaskFromContours(ValueTuple<int[], int[], int[], int> contourData, int contourIdx, int width, int height)
        {
            var (flatX, flatY, contourLengths, contourCount) = contourData;
            if (contourCount <= 0 || contourLengths == null || contourLengths.Length == 0)
                return new CalibImage(width, height, 1);

            int selected = contourIdx;
            if (selected < 0)
            {
                double maxArea = -1;
                int maxIdx = -1;
                int offset = 0;
                for (int i = 0; i < contourCount && i < contourLengths.Length; i++)
                {
                    int len = contourLengths[i];
                    if (len < 3)
                    {
                        offset += Math.Max(0, len);
                        continue;
                    }

                    if (offset + len > flatX.Length || offset + len > flatY.Length)
                        break;

                    var pts = new List<System.Drawing.Point>(len);
                    for (int k = 0; k < len; k++)
                        pts.Add(new System.Drawing.Point(flatX[offset + k], flatY[offset + k]));
                    var area = PolygonArea(pts);

                    if (area > maxArea)
                    {
                        maxArea = area;
                        maxIdx = i;
                    }
                    offset += len;
                }
                selected = maxIdx;
            }

            if (selected < 0 || selected >= contourCount || selected >= contourLengths.Length)
                throw new InvalidOperationException($"轮廓索引无效: {contourIdx}");

            int start = 0;
            for (int i = 0; i < selected; i++) start += contourLengths[i];
            int selectedLen = contourLengths[selected];
            if (selectedLen < 3) throw new InvalidOperationException("目标轮廓点数不足，无法生成 Mask");
            if (start + selectedLen > flatX.Length || start + selectedLen > flatY.Length)
                throw new InvalidOperationException("Contours 数据损坏，超出数组边界");

            var selectedPts = new System.Drawing.Point[selectedLen];
            for (int i = 0; i < selectedLen; i++)
                selectedPts[i] = new System.Drawing.Point(flatX[start + i], flatY[start + i]);

            using var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Black);
                g.FillPolygon(System.Drawing.Brushes.White, selectedPts);
            }

            var bytes = new byte[width * height];
            var rect = new System.Drawing.Rectangle(0, 0, width, height);
            var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                int srcStride = Math.Abs(bd.Stride);
                var row = new byte[srcStride];
                for (int y = 0; y < height; y++)
                {
                    IntPtr srcRow = IntPtr.Add(bd.Scan0, y * srcStride);
                    Marshal.Copy(srcRow, row, 0, srcStride);
                    for (int x = 0; x < width; x++)
                    {
                        int bgr = x * 3;
                        bytes[y * width + x] = (byte)(row[bgr] > 0 || row[bgr + 1] > 0 || row[bgr + 2] > 0 ? 255 : 0);
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }

            var mask = new CalibImage(width, height, 1);
            var native = mask.GetNativeStruct();
            Marshal.Copy(bytes, 0, native.data, bytes.Length);
            return mask;
        }

        /// <summary>当前流程或嵌套子流程 .flow.json 所在目录；未保存且无嵌套基准时返回 null。</summary>
        private string? GetFlowBaseDirectory(string? compositeInnerFlowBaseDir = null)
        {
            if (!string.IsNullOrWhiteSpace(compositeInnerFlowBaseDir))
                return System.IO.Path.GetFullPath(compositeInnerFlowBaseDir.Trim());
            if (!string.IsNullOrEmpty(CurrentFlowFilePath))
            {
                var dir = System.IO.Path.GetDirectoryName(CurrentFlowFilePath);
                if (!string.IsNullOrEmpty(dir))
                    return System.IO.Path.GetFullPath(dir);
            }
            return null;
        }

        /// <summary>将绝对路径转为相对 flow 目录的路径；跨盘符时保留绝对路径。</summary>
        private string FormatPathForFlowParam(string absolutePath, string? compositeInnerFlowBaseDir = null)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
                return absolutePath;
            absolutePath = System.IO.Path.GetFullPath(absolutePath.Trim());
            var flowBase = GetFlowBaseDirectory(compositeInnerFlowBaseDir);
            if (string.IsNullOrEmpty(flowBase))
                return absolutePath;
            try
            {
                flowBase = System.IO.Path.GetFullPath(flowBase);
                string flowRoot = System.IO.Path.GetPathRoot(flowBase) ?? "";
                string fileRoot = System.IO.Path.GetPathRoot(absolutePath) ?? "";
                if (!string.Equals(flowRoot, fileRoot, StringComparison.OrdinalIgnoreCase))
                    return absolutePath;
                return System.IO.Path.GetRelativePath(flowBase, absolutePath);
            }
            catch
            {
                return absolutePath;
            }
        }

        private static bool IsFlowRelativePathParam(string paramName) =>
            paramName is "filePath" or "directory" or "imageDirectory" or "innerFlowPath"
                or "calibrationJsonFile" or "worldPointsFile" or "templatePath" or "debugDumpPrefix"
                or "uvProjectionSvg" or "encoderPath" or "decoderPath" or "owlv2OnnxPath" or "tokenizerPath"
                or "scriptPath" or "launcherScript" or "checkpointPath" or "jitRepoRoot" or "weightsPath";

        /// <param name="relativeBaseDirectory">嵌套组合算子时传入当前子流程 .flow.json 所在目录；顶层为 null 则用 CurrentFlowFilePath 目录。</param>
        private string ResolveCompositeFlowPath(string path, string? relativeBaseDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            path = path.Trim();
            if (System.IO.Path.IsPathRooted(path))
                return System.IO.Path.GetFullPath(path);
            var flowBase = GetFlowBaseDirectory(relativeBaseDirectory);
            if (!string.IsNullOrEmpty(flowBase))
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(flowBase, path));
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
        }

        private static List<FlowNode> TopologicalSortInner(
            List<FlowNode> nodes,
            List<(Guid FromId, string FromPort, Guid ToId, string ToPort)> edges,
            Dictionary<Guid, FlowNode> idMap)
        {
            var inDegree = nodes.ToDictionary(n => n.Id, _ => 0);
            foreach (var e in edges)
            {
                if (inDegree.ContainsKey(e.ToId))
                    inDegree[e.ToId]++;
            }

            var visited = new HashSet<Guid>();
            var result = new List<FlowNode>();
            var queue = new Queue<FlowNode>();
            foreach (var n in nodes)
            {
                if (inDegree[n.Id] == 0)
                    queue.Enqueue(n);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (visited.Contains(current.Id)) continue;
                visited.Add(current.Id);
                result.Add(current);

                foreach (var e in edges)
                {
                    if (e.FromId != current.Id) continue;
                    if (!inDegree.ContainsKey(e.ToId)) continue;
                    inDegree[e.ToId]--;
                    if (inDegree[e.ToId] == 0 && idMap.TryGetValue(e.ToId, out var nextNode))
                        queue.Enqueue(nextNode);
                }
            }

            if (visited.Count != nodes.Count)
                throw new InvalidOperationException("组合算子: 子流程存在循环依赖");

            return result;
        }

        private static object? GetInnerUpstreamOutputValue(FlowNode fromNode, string preferredPortName)
        {
            if (fromNode.Outputs.TryGetValue(preferredPortName, out var direct) && direct != null)
                return direct;

            if (string.Equals(preferredPortName, "Xld", StringComparison.OrdinalIgnoreCase)
                && fromNode.Outputs.TryGetValue("DeformedXld", out var deformed)
                && deformed != null)
                return deformed;

            return fromNode.Outputs.GetValueOrDefault(preferredPortName);
        }

        private static Dictionary<string, object?> BuildInnerInputsFromEdges(
            Guid innerId,
            List<(Guid FromId, string FromPort, Guid ToId, string ToPort)> edges,
            Dictionary<Guid, FlowNode> idMap)
        {
            var inputs = new Dictionary<string, object?>();
            foreach (var e in edges)
            {
                if (e.ToId != innerId) continue;
                if (!idMap.TryGetValue(e.FromId, out var fromNode)) continue;
                inputs[e.ToPort] = GetInnerUpstreamOutputValue(fromNode, e.FromPort);
            }
            return inputs;
        }

        private void RememberRecentFullFrameForHalconFine(CalibImage? img)
        {
            if (img == null || img.Width <= 0 || img.Height <= 0)
                return;
            _recentFullFrameForHalconFineMatch?.Dispose();
            _recentFullFrameForHalconFineMatch = CalibAPI.DuplicateImage(img);
        }

        /// <summary>透视 .dfm 精匹配需在全图 ROI 上搜；loop 仅传入域内图 In 时自动补 FullImage。</summary>
        private void TryAttachFineMatchFullImage(
            IDictionary<string, object?> inputs,
            IReadOnlyDictionary<string, object?>? compositeInputs)
        {
            if (inputs.TryGetValue("FullImage", out var existing) && existing is CalibImage full && full.Width > 0)
                return;

            if (compositeInputs != null)
            {
                foreach (string key in new[] { "FullImage", "Image", "In" })
                {
                    if (!compositeInputs.TryGetValue(key, out var obj) || obj is not CalibImage candidate || candidate.Width <= 0)
                        continue;
                    if (inputs.TryGetValue("In", out var domainObj) && domainObj is CalibImage domain
                        && domain.Width > 0 && domain.Height > 0)
                    {
                        long candPx = (long)candidate.Width * candidate.Height;
                        long domPx = (long)domain.Width * domain.Height;
                        if (candPx <= domPx * 105 / 100)
                            continue;
                    }

                    inputs["FullImage"] = candidate;
                    return;
                }
            }

            if (_recentFullFrameForHalconFineMatch != null)
                inputs["FullImage"] = _recentFullFrameForHalconFineMatch;
        }

        /// <summary>主流程拍照/读图后供嵌套精组合使用 FullImage（透视 .dfm）。</summary>
        private void InjectRecentFullFrameForFineComposite(IDictionary<string, object?> compositeInputs)
        {
            if (_recentFullFrameForHalconFineMatch == null)
                return;
            if (!compositeInputs.ContainsKey("FullImage"))
                compositeInputs["FullImage"] = _recentFullFrameForHalconFineMatch;
        }

        private static void MergeCompositeExternalInputs(
            FlowNode inner,
            Dictionary<string, object?> innerInputs,
            Dictionary<string, object?> compositeInputs,
            CompositeBindingsSpec? spec)
        {
            if (spec?.Inputs == null) return;
            foreach (var bi in spec.Inputs)
            {
                if (bi == null || string.IsNullOrWhiteSpace(bi.NodeId)) continue;
                if (!Guid.TryParse(bi.NodeId.Trim(), out var nid) || nid != inner.Id) continue;
                if (string.IsNullOrWhiteSpace(bi.Port) || string.IsNullOrWhiteSpace(bi.External)) continue;
                if (!innerInputs.ContainsKey(bi.Port))
                    innerInputs[bi.Port] = compositeInputs.GetValueOrDefault(bi.External);
            }
        }

        private static void AutoFillUnboundCompositeInnerInputs(
            FlowNode inner,
            Dictionary<string, object?> innerInputs,
            Dictionary<string, object?> compositeInputs,
            bool innerIsSourceInSubgraph)
        {
            foreach (var pd in inner.Def.Ports)
            {
                if (pd.Direction != PortDirection.Input)
                    continue;
                string p = pd.Name;
                if (innerInputs.TryGetValue(p, out var existing) && existing != null)
                    continue;

                if (compositeInputs.TryGetValue(p, out var sameName) && sameName != null)
                {
                    innerInputs[p] = sameName;
                    continue;
                }

                if (!innerIsSourceInSubgraph)
                    continue;

                if (!compositeInputs.TryGetValue("In", out var inVal) || inVal == null)
                    continue;

                if (string.Equals(p, "In", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p, "Image", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p, "Img", StringComparison.OrdinalIgnoreCase))
                {
                    innerInputs[p] = inVal;
                }
            }
        }

        private static (FlowNode node, string port)? PickUnusedTerminalInnerOutput(
            List<FlowNode> sortedAsc,
            List<(Guid FromId, string FromPort, Guid ToId, string ToPort)> edges,
            HashSet<(Guid NodeId, string Port)> consumed)
        {
            for (int i = sortedAsc.Count - 1; i >= 0; i--)
            {
                var inner = sortedAsc[i];
                foreach (var po in inner.Def.Ports)
                {
                    if (po.Direction != PortDirection.Output)
                        continue;
                    if (edges.Any(e => e.FromId == inner.Id && string.Equals(e.FromPort, po.Name, StringComparison.Ordinal)))
                        continue;
                    if (!inner.Outputs.TryGetValue(po.Name, out var val) || val == null)
                        continue;
                    if (consumed.Contains((inner.Id, po.Name)))
                        continue;
                    return (inner, po.Name);
                }
            }
            return null;
        }

        private static List<CompositeIoBind> BuildEffectiveCompositeOutputBinds(
            FlowNode compositeNode,
            CompositeBindingsSpec spec,
            List<FlowNode> sortedInner,
            List<(Guid FromId, string FromPort, Guid ToId, string ToPort)> edges,
            bool allowAutoTerminalPick,
            Dictionary<Guid, FlowNode> idMap)
        {
            var list = new List<CompositeIoBind>();
            if (spec.Outputs != null)
            {
                foreach (var ob in spec.Outputs)
                {
                    if (ob == null || string.IsNullOrWhiteSpace(ob.External) ||
                        string.IsNullOrWhiteSpace(ob.NodeId) || string.IsNullOrWhiteSpace(ob.Port))
                        continue;
                    list.Add(ob);
                }
            }

            if (!allowAutoTerminalPick)
                return list;

            var consumed = new HashSet<(Guid NodeId, string Port)>();
            foreach (var ob in list)
            {
                if (Guid.TryParse(ob.NodeId.Trim(), out var g))
                    consumed.Add((g, ob.Port.Trim()));
            }

            foreach (var e in edges)
            {
                if (!idMap.TryGetValue(e.ToId, out var toN) || toN.Def.TypeId != "composite_bind_out")
                    continue;
                if (!string.Equals(e.ToPort, "In", StringComparison.OrdinalIgnoreCase))
                    continue;
                consumed.Add((e.FromId, (e.FromPort ?? "").Trim()));
            }

            foreach (var extDef in BuildCompositeExternalPorts(compositeNode.Params).Where(p => p.Direction == PortDirection.Output))
            {
                string ext = extDef.Name;
                if (list.Any(b => string.Equals(b.External, ext, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var pick = PickUnusedTerminalInnerOutput(sortedInner, edges, consumed);
                if (pick == null)
                    continue;
                list.Add(new CompositeIoBind
                {
                    External = ext,
                    NodeId = pick.Value.node.Id.ToString("D"),
                    Port = pick.Value.port
                });
                consumed.Add((pick.Value.node.Id, pick.Value.port));
            }

            return list;
        }

        private static void AppendCompositeOutputBindsFromWiredBindOuts(
            Dictionary<Guid, FlowNode> idMap,
            List<(Guid FromId, string FromPort, Guid ToId, string ToPort)> edges,
            CompositeBindingsSpec spec)
        {
            if (spec.Outputs == null) spec.Outputs = new List<CompositeIoBind>();

            foreach (var e in edges)
            {
                if (!idMap.TryGetValue(e.ToId, out var toN) || toN.Def.TypeId != "composite_bind_out")
                    continue;
                if (!string.Equals(e.ToPort, "In", StringComparison.OrdinalIgnoreCase))
                    continue;
                string ext = toN.Params.GetValueOrDefault("externalPort", "Out")?.Trim() ?? "Out";
                if (string.IsNullOrWhiteSpace(ext)) continue;
                string fromPort = (e.FromPort ?? "").Trim();
                if (string.IsNullOrWhiteSpace(fromPort)) continue;
                spec.Outputs.Add(new CompositeIoBind
                {
                    External = ext,
                    NodeId = e.FromId.ToString("D"),
                    Port = fromPort
                });
            }
        }

        private static void AppendCompositeBindsFromMarkerNodes(
            Dictionary<Guid, FlowNode> idMap,
            CompositeBindingsSpec spec)
        {
            if (spec.Inputs == null) spec.Inputs = new List<CompositeIoBind>();
            if (spec.Outputs == null) spec.Outputs = new List<CompositeIoBind>();

            foreach (var n in idMap.Values)
            {
                if (n.Def.TypeId == "composite_bind_in")
                {
                    string ext = n.Params.GetValueOrDefault("externalPort", "")?.Trim() ?? "";
                    string nid = n.Params.GetValueOrDefault("innerNodeId", "")?.Trim() ?? "";
                    string port = n.Params.GetValueOrDefault("innerPort", "")?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(ext) || string.IsNullOrWhiteSpace(nid) || string.IsNullOrWhiteSpace(port))
                        continue;
                    if (!Guid.TryParse(nid, out var gid) || !idMap.ContainsKey(gid))
                        continue;
                    spec.Inputs.Add(new CompositeIoBind { External = ext, NodeId = nid, Port = port });
                }
                else if (n.Def.TypeId == "composite_bind_out")
                {
                    string ext = n.Params.GetValueOrDefault("externalPort", "")?.Trim() ?? "";
                    string nid = n.Params.GetValueOrDefault("innerNodeId", "")?.Trim() ?? "";
                    string port = n.Params.GetValueOrDefault("innerPort", "")?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(ext) || string.IsNullOrWhiteSpace(nid) || string.IsNullOrWhiteSpace(port))
                        continue;
                    if (!Guid.TryParse(nid, out var gid) || !idMap.ContainsKey(gid))
                        continue;
                    spec.Outputs.Add(new CompositeIoBind { External = ext, NodeId = nid, Port = port });
                }
            }
        }

        private static string FormatCompositeInnerNodeIdentity(FlowNode inner) =>
            $"「{inner.Def.DisplayName}」({inner.Def.TypeId}, {inner.Id.ToString("N")[..8]})";

        private static void RethrowCompositeInnerNodeFailure(
            FlowNode inner,
            string? compositeFlowLabel,
            Exception ex,
            int? maskLoopRoundIndex = null)
        {
            string msg = ex.Message ?? ex.GetType().Name;
            if (msg.StartsWith("组合算子子流程失败:", StringComparison.Ordinal))
                throw new InvalidOperationException(msg, ex);

            string roundPart = maskLoopRoundIndex.HasValue
                ? $" [Mask 第 {maskLoopRoundIndex.Value + 1} 轮]"
                : "";
            string flowPart = string.IsNullOrWhiteSpace(compositeFlowLabel)
                ? ""
                : $" 子流程={compositeFlowLabel}";
            throw new InvalidOperationException(
                $"组合算子子流程失败:{flowPart}{roundPart} 内部算子 {FormatCompositeInnerNodeIdentity(inner)}: {msg}",
                ex);
        }

        private void ExecuteCompositeInnerNode(
            FlowNode inner,
            Dictionary<string, object?> innerInputs,
            Dictionary<string, object?> compositeInputs,
            string? compositeInnerFlowBaseDir,
            string? compositeFlowLabel,
            int? maskLoopRoundIndex = null)
        {
            try
            {
                ExecuteNode(inner, innerInputs, compositeInputs, compositeInnerFlowBaseDir);
            }
            catch (Exception ex)
            {
                RethrowCompositeInnerNodeFailure(inner, compositeFlowLabel, ex, maskLoopRoundIndex);
            }
        }

        /// <param name="innerFlowResolveBaseDir">当前组合嵌套在上层子流程内时，为其 innerFlowPath 相对路径提供基准目录（通常为上层子流程 .flow.json 所在文件夹）。</param>
        private void ExecuteCompositeSubFlow(FlowNode compositeNode, Dictionary<string, object?> compositeInputs, string? innerFlowResolveBaseDir = null)
        {
            InjectRecentFullFrameForFineComposite(compositeInputs);

            string path = compositeNode.Params.GetValueOrDefault("innerFlowPath", "")?.Trim() ?? "";
            string embedded = compositeNode.Params.GetValueOrDefault("innerFlowJson", "") ?? "";
            string bindRaw = compositeNode.Params.GetValueOrDefault("bindingsJson", "") ?? "";

            string? compositeFlowLabel = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    compositeFlowLabel = System.IO.Path.GetFileName(
                        ResolveCompositeFlowPath(path, innerFlowResolveBaseDir));
                }
                catch
                {
                    compositeFlowLabel = path;
                }
            }
            else if (!string.IsNullOrWhiteSpace(embedded.Trim()))
            {
                compositeFlowLabel = "内嵌 JSON";
            }

            string jsonText;
            string? baseDirForNestedComposites;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var rp = ResolveCompositeFlowPath(path, innerFlowResolveBaseDir);
                if (!System.IO.File.Exists(rp))
                    throw new InvalidOperationException($"组合算子: 子流程文件不存在: {rp}");
                jsonText = System.IO.File.ReadAllText(rp);
                baseDirForNestedComposites = System.IO.Path.GetDirectoryName(rp);
            }
            else if (!string.IsNullOrWhiteSpace(embedded.Trim()))
            {
                jsonText = embedded.Trim();
                baseDirForNestedComposites = innerFlowResolveBaseDir;
            }
            else
                throw new InvalidOperationException("组合算子: 请设置 innerFlowPath 或 innerFlowJson");

            var jOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var data = JsonSerializer.Deserialize<FlowData>(jsonText, jOpts);
            if (data?.Nodes == null || data.Nodes.Count == 0)
                throw new InvalidOperationException("组合算子: 子流程为空");

            CompositeBindingsSpec spec;
            if (string.IsNullOrWhiteSpace(bindRaw.Trim()))
            {
                spec = new CompositeBindingsSpec { Inputs = new List<CompositeIoBind>(), Outputs = new List<CompositeIoBind>() };
            }
            else
            {
                try
                {
                    spec = JsonSerializer.Deserialize<CompositeBindingsSpec>(bindRaw.Trim(), jOpts)
                           ?? new CompositeBindingsSpec { Inputs = new List<CompositeIoBind>(), Outputs = new List<CompositeIoBind>() };
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException($"组合算子: bindingsJson 解析失败: {ex.Message}");
                }

                if (spec.Inputs == null)
                    spec.Inputs = new List<CompositeIoBind>();
                if (spec.Outputs == null)
                    spec.Outputs = new List<CompositeIoBind>();
            }

            var defLookup = OperatorRegistry.ToDictionary(d => d.TypeId);
            var idMap = new Dictionary<Guid, FlowNode>();
            foreach (var nd in data.Nodes)
            {
                if (!defLookup.TryGetValue(nd.TypeId, out var def))
                    throw new InvalidOperationException($"组合算子: 未知子节点类型 {nd.TypeId}");
                if (!Guid.TryParse(nd.Id?.Trim(), out var nid))
                    throw new InvalidOperationException($"组合算子: 子节点 Id 必须为 GUID 字符串: {nd.Id}");
                var fn = new FlowNode(def, 0, 0, nid);
                if (nd.Params != null)
                {
                    foreach (var kv in nd.Params)
                        fn.Params[kv.Key] = kv.Value;
                }
                idMap[nid] = fn;
            }

            var edges = new List<(Guid FromId, string FromPort, Guid ToId, string ToPort)>();
            foreach (var cd in data.Connections ?? new List<FlowConnData>())
            {
                if (!Guid.TryParse(cd.FromNodeId?.Trim(), out var fid)) continue;
                if (!Guid.TryParse(cd.ToNodeId?.Trim(), out var tid)) continue;
                string toPort = cd.ToPort ?? "";
                if (idMap.TryGetValue(tid, out var toInner) && toInner.Def.TypeId == "hough_lines" &&
                    string.Equals(toPort, "Image", StringComparison.Ordinal))
                    toPort = "Edge";
                edges.Add((fid, cd.FromPort ?? "", tid, toPort));
            }

            AppendCompositeBindsFromMarkerNodes(idMap, spec);
            AppendCompositeOutputBindsFromWiredBindOuts(idMap, edges, spec);
            bool strictCompositeInputBinding = idMap.Values.Any(n => n.Def.TypeId == "composite_bind_in");
            bool strictCompositeOutputBinding = idMap.Values.Any(n => n.Def.TypeId == "composite_bind_out");

            var innerList = idMap.Values.ToList();
            var sorted = TopologicalSortInner(innerList, edges, idMap);

            var innerWiredOutputs = new HashSet<(Guid NodeId, string Port)>();
            foreach (var e in edges)
                innerWiredOutputs.Add((e.FromId, e.FromPort));

            _compositeInnerWiredOutputs = innerWiredOutputs;
            try
            {
                // 检测子流程中是否有 halcon_coarse_shape_reduce_domain loopEmit=true 需要循环展开
                FlowNode? compositeMaskEachNode = sorted
                    .FirstOrDefault(n => n.Def.TypeId == "halcon_coarse_shape_reduce_domain" &&
                                         !string.Equals(n.Params.GetValueOrDefault("loopEmit", "true"), "false",
                                             StringComparison.OrdinalIgnoreCase));

#if HALCON_ENABLED
                if (compositeMaskEachNode != null)
                {
                    // 本地辅助：计算子流程内的上下游关系
                    HashSet<Guid> CompositeDownstreamIds(Guid sourceId)
                    {
                        var result = new HashSet<Guid> { sourceId };
                        var q = new Queue<Guid>();
                        q.Enqueue(sourceId);
                        while (q.Count > 0)
                        {
                            var cur = q.Dequeue();
                            foreach (var e in edges)
                            {
                                if (e.FromId != cur) continue;
                                if (result.Add(e.ToId))
                                    q.Enqueue(e.ToId);
                            }
                        }
                        return result;
                    }

                    var downstreamIds = CompositeDownstreamIds(compositeMaskEachNode.Id);
                    var preNodes = sorted.Where(n => !downstreamIds.Contains(n.Id)).ToList();
                    var postNodes = sorted.Where(n => downstreamIds.Contains(n.Id) && n.Id != compositeMaskEachNode.Id).ToList();
                    var fineNodes = postNodes.Where(IsHalconMaskLoopFineMatchNode).ToList();
                    var fineAccumulators = fineNodes.ToDictionary(n => n.Id, _ => new FineMatchRoundAccumulator());

                    // 前置节点执行一次
                    foreach (var inner in preNodes)
                    {
                        var innerInputs = BuildInnerInputsFromEdges(inner.Id, edges, idMap);
                        MergeCompositeExternalInputs(inner, innerInputs, compositeInputs, spec);
                        bool innerIsSource = !edges.Any(e => e.ToId == inner.Id);
                        if (!strictCompositeInputBinding)
                            AutoFillUnboundCompositeInnerInputs(inner, innerInputs, compositeInputs, innerIsSource);
                        var swInner = Stopwatch.StartNew();
                        ExecuteCompositeInnerNode(inner, innerInputs, compositeInputs, baseDirForNestedComposites, compositeFlowLabel);
                        swInner.Stop();
                        LogOperatorTiming(inner, swInner.Elapsed.TotalMilliseconds, "composite");
                    }

                    // 构建 mask batch
                    var maskInputs = BuildInnerInputsFromEdges(compositeMaskEachNode.Id, edges, idMap);
                    MergeCompositeExternalInputs(compositeMaskEachNode, maskInputs, compositeInputs, spec);
                    var batch = BuildCoarseShapeMaskBatchForNode(compositeMaskEachNode, maskInputs);
                    if (batch.Count == 0)
                        throw new InvalidOperationException("HALCON 粗形状Mask: 无粗候选，无法生成 Mask（组合算子内）");
                    compositeMaskEachNode.Executed = true;
                    compositeMaskEachNode.ErrorMessage = null;

                    var postLoopDeferred = ComputePostLoopDeferredNodesCore(postNodes, n =>
                        edges.Where(e => e.ToId == n.Id && idMap.TryGetValue(e.FromId, out var from) && postNodes.Contains(from))
                            .Select(e => idMap[e.FromId]));
                    var perRoundNodes = postNodes.Where(n => !postLoopDeferred.Contains(n)).ToList();

                    EnterFlowLoopSinkAccumulate();
                    try
                    {
                        ResetFlowSinkAccumulators(perRoundNodes);

                        // 循环展开
                        for (int mi = 0; mi < batch.Count; mi++)
                        {
                            SetCoarseShapeMaskRoundOutputs(compositeMaskEachNode, batch, mi);
                            foreach (var inner in perRoundNodes)
                            {
                                inner.Outputs.Clear();
                                inner.ErrorMessage = null;
                                inner.Executed = false;
                                var innerInputs = BuildInnerInputsFromEdges(inner.Id, edges, idMap);
                                MergeCompositeExternalInputs(inner, innerInputs, compositeInputs, spec);
                                if (IsHalconMaskLoopFineMatchNode(inner))
                                {
                                    TryAttachFineMatchFullImage(innerInputs, compositeInputs);
                                    ApplyFineMatchMaskLoopInputs(innerInputs, maskInputs, batch, mi);
                                }

                                bool innerIsSource = !edges.Any(e => e.ToId == inner.Id);
                                if (!strictCompositeInputBinding)
                                    AutoFillUnboundCompositeInnerInputs(inner, innerInputs, compositeInputs, innerIsSource);
                                var swInner = Stopwatch.StartNew();
                                ExecuteCompositeInnerNode(inner, innerInputs, compositeInputs, baseDirForNestedComposites, compositeFlowLabel, mi);
                                swInner.Stop();
                                LogOperatorTiming(inner, swInner.Elapsed.TotalMilliseconds, "composite");

                                if (fineAccumulators.TryGetValue(inner.Id, out var acc))
                                    AccumulateFineMatchRound(inner, acc);
                            }
                        }
                    }
                    finally
                    {
                        ExitFlowLoopSinkAccumulate();
                    }

                    RefreshPointsSinkMergedOutputs(perRoundNodes);

                    // 汇总精匹配结果
                    foreach (FlowNode fn in fineNodes)
                    {
                        fn.Executed = true;
                        fn.ErrorMessage = null;
                        ApplyFineMatchAccumulator(fn, fineAccumulators[fn.Id]);
                    }

                    var orderedDeferred = postNodes.Where(postLoopDeferred.Contains).ToList();
                    foreach (var inner in orderedDeferred)
                    {
                        inner.Outputs.Clear();
                        inner.ErrorMessage = null;
                        inner.Executed = false;
                        var innerInputs = BuildInnerInputsFromEdges(inner.Id, edges, idMap);
                        MergeCompositeExternalInputs(inner, innerInputs, compositeInputs, spec);
                        bool innerIsSource = !edges.Any(e => e.ToId == inner.Id);
                        if (!strictCompositeInputBinding)
                            AutoFillUnboundCompositeInnerInputs(inner, innerInputs, compositeInputs, innerIsSource);
                        var swInner = Stopwatch.StartNew();
                        ExecuteCompositeInnerNode(inner, innerInputs, compositeInputs, baseDirForNestedComposites, compositeFlowLabel);
                        swInner.Stop();
                        LogOperatorTiming(inner, swInner.Elapsed.TotalMilliseconds, "composite");
                    }

                    compositeMaskEachNode.ResultSummary = $"Mask循环 {batch.Count} 轮（组合算子内）"
                        + (orderedDeferred.Count > 0 ? $"，延后 {orderedDeferred.Count}" : "");
                }
                else
#endif
                {
                    // 无 mask-each-loop 时（或非 HALCON 构建）：顺序执行所有节点
                    foreach (var inner in sorted)
                    {
                        var innerInputs = BuildInnerInputsFromEdges(inner.Id, edges, idMap);
                        MergeCompositeExternalInputs(inner, innerInputs, compositeInputs, spec);
                        if (IsHalconMaskLoopFineMatchNode(inner))
                            TryAttachFineMatchFullImage(innerInputs, compositeInputs);
                        bool innerIsSource = !edges.Any(e => e.ToId == inner.Id);
                        if (!strictCompositeInputBinding)
                            AutoFillUnboundCompositeInnerInputs(inner, innerInputs, compositeInputs, innerIsSource);
                        var swInner = Stopwatch.StartNew();
                        ExecuteCompositeInnerNode(inner, innerInputs, compositeInputs, baseDirForNestedComposites, compositeFlowLabel);
                        swInner.Stop();
                        LogOperatorTiming(inner, swInner.Elapsed.TotalMilliseconds, "composite");
                    }
                }

                compositeNode.LastCompositeRun = CaptureCompositeRunSnapshot(sorted, compositeInputs, compositeFlowLabel);
                LogCompositeContourPipelineDigest(compositeNode);
            }
            finally
            {
                _compositeInnerWiredOutputs = null;
            }

            var effectiveOutputs = BuildEffectiveCompositeOutputBinds(
                compositeNode, spec, sorted, edges, allowAutoTerminalPick: !strictCompositeOutputBinding, idMap);
            if (effectiveOutputs.Count == 0)
                throw new InvalidOperationException("组合算子: 无有效输出绑定（请将子节点输出连到「组合绑定出」的 In、在 bindingsJson.outputs 中声明，或保证子图存在未连出线的末端输出端口）");

            compositeNode.Outputs.Clear();
            foreach (var ob in effectiveOutputs)
            {
                if (ob == null || string.IsNullOrWhiteSpace(ob.External) ||
                    string.IsNullOrWhiteSpace(ob.NodeId) || string.IsNullOrWhiteSpace(ob.Port))
                    continue;
                if (!Guid.TryParse(ob.NodeId.Trim(), out var oid)) continue;
                if (!idMap.TryGetValue(oid, out var srcNode)) continue;
                compositeNode.Outputs[ob.External] = srcNode.Outputs.GetValueOrDefault(ob.Port);
            }

            compositeNode.ResultSummary = $"Composite {innerList.Count} nodes → {compositeNode.Outputs.Count} outs";
        }


        private static string FormatCalibrationFullSummary(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var sb = new StringBuilder();

                static string Num(JsonElement obj, string name) =>
                    obj.TryGetProperty(name, out var v) ? v.GetDouble().ToString("G9") : "?";

                if (root.TryGetProperty("intrinsics", out var intr))
                {
                    sb.AppendLine("[内参 · 标定求解]");
                    sb.AppendLine($"fx={Num(intr, "fx")} fy={Num(intr, "fy")} cx={Num(intr, "cx")} cy={Num(intr, "cy")} RMS={Num(intr, "rms")}");
                    sb.AppendLine($"k1={Num(intr, "k1")} k2={Num(intr, "k2")} p1={Num(intr, "p1")} p2={Num(intr, "p2")} k3={Num(intr, "k3")}");
                }

                if (root.TryGetProperty("extrinsicsPerView", out var views) && views.ValueKind == JsonValueKind.Array)
                {
                    sb.AppendLine("[外参 · 每视图 board→camera]");
                    int idx = 0;
                    foreach (var el in views.EnumerateArray())
                    {
                        idx++;
                        var path = el.TryGetProperty("imagePath", out var pp) ? pp.GetString() ?? "" : "";
                        sb.AppendLine($"#{idx} {path}");
                        if (el.TryGetProperty("rvec", out var rv) && rv.ValueKind == JsonValueKind.Array)
                        {
                            var a = rv.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                            if (a.Length >= 3)
                                sb.AppendLine($"  rvec(rad)=[{a[0]:G9}, {a[1]:G9}, {a[2]:G9}]");
                        }
                        if (el.TryGetProperty("tvec", out var tv) && tv.ValueKind == JsonValueKind.Array)
                        {
                            var a = tv.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                            if (a.Length >= 3)
                                sb.AppendLine($"  tvec(与方格单位一致)=[{a[0]:G9}, {a[1]:G9}, {a[2]:G9}]");
                        }
                    }
                }

                if (root.TryGetProperty("convention", out var cnv))
                    sb.AppendLine(cnv.GetString());

                sb.AppendLine();
                sb.AppendLine("[用法说明 · 内参与外参]");
                sb.AppendLine("· 内参 intrinsics：描述相机本身（fx,fy 焦距像素、cx,cy 主点、k1… 畸变）。同一相机、固定成像条件下可复用于所有帧；用于 undistort、或配合外参做 projectPoints（3D→像素）。");
                sb.AppendLine("· 外参 extrinsicsPerView：每一张参与标定且成功的图像对应一组 rvec、tvec，表示「棋盘格坐标系 → 相机坐标系」的位姿（OpenCV: P_cam = R·P_board + t；详见上方 convention）。");
                sb.AppendLine("· 选用外参：不同视图角度不同，不能把视图 A 的 rvec/tvec 当作视图 B 的场景位姿。Hand–Eye 或多相机需在其他链路估计；单相机静态场景应对「当前帧」用 solvePnP 等与棋盘共面的点重算外参，或固定棋盘位姿后只用对应那张图的外参。");
                sb.AppendLine("· tvec、棋盘角点世界坐标的长度单位与标定节点 squareSizeMm 一致（例如毫米）。");
                sb.AppendLine("· 像素轨迹→棋盘平面 XY：Flow 算子「棋盘像素→世界(mm)」，输入 Points + CalibrationJson，参数 viewIndex 选用 extrinsicsPerView[i]；示例 test_images/chessboard_trajectory_to_world.flow.json。");
                return sb.ToString().TrimEnd();
            }
            catch
            {
                return json;
            }
        }

        private readonly struct HoughLineSeg
        {
            public int X1 { get; }
            public int Y1 { get; }
            public int X2 { get; }
            public int Y2 { get; }
            public HoughLineSeg(int x1, int y1, int x2, int y2)
            {
                X1 = x1;
                Y1 = y1;
                X2 = x2;
                Y2 = y2;
            }
            public double Length
            {
                get
                {
                    double dx = X2 - X1, dy = Y2 - Y1;
                    return Math.Sqrt(dx * dx + dy * dy);
                }
            }
        }

        private static List<HoughLineSeg> ParseHoughLinesJsonToSegs(string? json)
        {
            var list = new List<HoughLineSeg>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            int pos = 0;
            while (pos < json.Length)
            {
                int lb = json.IndexOf('[', pos);
                if (lb < 0) break;
                int rb = json.IndexOf(']', lb + 1);
                if (rb < 0) break;
                ReadOnlySpan<char> chunk = json.AsSpan(lb + 1, rb - lb - 1);
                int comma1 = chunk.IndexOf(',');
                if (comma1 < 0) { pos = rb + 1; continue; }
                int comma2Rel = chunk.Slice(comma1 + 1).IndexOf(',');
                if (comma2Rel < 0) { pos = rb + 1; continue; }
                int comma2 = comma1 + 1 + comma2Rel;
                int comma3Rel = chunk.Slice(comma2 + 1).IndexOf(',');
                if (comma3Rel < 0) { pos = rb + 1; continue; }
                int comma3 = comma2 + 1 + comma3Rel;
                if (!int.TryParse(chunk.Slice(0, comma1).Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var x1) ||
                    !int.TryParse(chunk.Slice(comma1 + 1, comma2 - comma1 - 1).Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var y1) ||
                    !int.TryParse(chunk.Slice(comma2 + 1, comma3 - comma2 - 1).Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var x2) ||
                    !int.TryParse(chunk.Slice(comma3 + 1).Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var y2))
                {
                    pos = rb + 1;
                    continue;
                }
                list.Add(new HoughLineSeg(x1, y1, x2, y2));
                pos = rb + 1;
            }
            return list;
        }

        private static string FormatHoughLinesJson(List<HoughLineSeg> segs)
        {
            if (segs == null || segs.Count == 0) return "[]";
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                if (i > 0) sb.Append(',');
                sb.Append('[').Append(s.X1).Append(',').Append(s.Y1).Append(',').Append(s.X2).Append(',').Append(s.Y2).Append(']');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static List<HoughLineSeg> HoughLinesJsonNonMaxSuppression(List<HoughLineSeg> input, double angleTolDeg, double rhoTolPx)
        {
            var result = new List<HoughLineSeg>();
            if (input == null || input.Count == 0) return result;
            double angleTolRad = angleTolDeg * Math.PI / 180.0;
            if (angleTolRad < 1e-9) angleTolRad = 1e-9;
            if (rhoTolPx < 1e-9) rhoTolPx = 1e-9;
            var best = new Dictionary<(int tb, int rb), HoughLineSeg>();
            foreach (var seg in input)
            {
                if (seg.Length < 1e-6) continue;
                double dx = seg.X2 - seg.X1, dy = seg.Y2 - seg.Y1;
                double thetaLine = Math.Atan2(dy, dx);
                double thetaN = thetaLine + Math.PI / 2;
                while (thetaN < 0) thetaN += Math.PI;
                while (thetaN >= Math.PI) thetaN -= Math.PI;
                double mx = (seg.X1 + seg.X2) * 0.5, my = (seg.Y1 + seg.Y2) * 0.5;
                double rho = mx * Math.Cos(thetaN) + my * Math.Sin(thetaN);
                int tb = (int)Math.Floor(thetaN / angleTolRad);
                int rb = (int)Math.Floor(rho / rhoTolPx);
                var key = (tb, rb);
                if (!best.TryGetValue(key, out var cur) || seg.Length > cur.Length)
                    best[key] = seg;
            }
            result.AddRange(best.Values);
            return result;
        }

        private static List<HoughLineSeg> HoughLinesJsonLengthThreshold(List<HoughLineSeg> input, double minLen, double maxLen)
        {
            if (input == null || input.Count == 0) return new List<HoughLineSeg>();
            double maxL = maxLen <= 0 ? double.PositiveInfinity : maxLen;
            var result = new List<HoughLineSeg>(input.Count);
            foreach (var s in input)
            {
                double len = s.Length;
                if (len >= minLen && len <= maxL)
                    result.Add(s);
            }
            return result;
        }

        private static Point2D[] ParseWorldPointsParam(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException("世界坐标为空，请填写 worldPoints 或 worldPointsFile");
            var parts = raw.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var points = new List<Point2D>(parts.Length);
            foreach (var p in parts)
            {
                var xy = p.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (xy.Length != 2 ||
                    !double.TryParse(xy[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                    !double.TryParse(xy[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                    throw new InvalidOperationException($"世界坐标格式错误: '{p}'，应为 x,y（与点列转文本输出一致）");
                points.Add(new Point2D(x, y));
            }
            if (points.Count < 4)
                throw new InvalidOperationException("世界坐标点数量不足，至少需要4个点");
            return points.ToArray();
        }

        private static bool NodeUsesCalibrateWorldPointParams(FlowNode node) =>
            node.Def.TypeId is "calibrate" or "calibrate_homography";

        /// <summary>九点/透视标定：worldPointsFile 优先（点列转文本落盘），否则解析 worldPoints 参数字符串。</summary>
        private Point2D[] ResolveCalibrateWorldPoints(FlowNode node, string? flowBaseDir = null)
        {
            string? fileParam = node.Params.GetValueOrDefault("worldPointsFile", "")?.Trim();
            if (!string.IsNullOrWhiteSpace(fileParam))
            {
                string path = ResolveCompositeFlowPath(fileParam, flowBaseDir);
                if (!System.IO.File.Exists(path))
                    throw new System.IO.FileNotFoundException($"标定: 世界坐标文件不存在: {path}");
                string content = System.IO.File.ReadAllText(path, Encoding.UTF8);
                var fromFile = ParseWorldPointsParam(content);
                return fromFile;
            }

            string worldRaw = node.Params.GetValueOrDefault("worldPoints", "")
                ?? node.Params.GetValueOrDefault("points", "");
            return ParseWorldPointsParam(worldRaw);
        }

        private Point2D[] ResolveCalibrateWorldPointsForNode(
            FlowNode node,
            Dictionary<string, object?> inputs,
            string? flowBaseDir)
        {
            if (inputs.TryGetValue("WorldPts", out var wpObj) && wpObj is Point2D[] fromPort && fromPort.Length > 0)
                return fromPort;
            return ResolveCalibrateWorldPoints(node, flowBaseDir);
        }

        private Point2D[] ResolveCalibrateAlignedImagePoints(
            FlowNode node,
            Dictionary<string, object?> inputs,
            Point2D[] worldPts,
            CalibImage? calibImage,
            string contextLabel)
        {
            var imagePts = inputs.TryGetValue("ImagePts", out var ipObj) ? ipObj as Point2D[] : null;
            bool manualPick = CalibrateUsesManualPixelPick(node);
            bool needDialog = CalibrateNeedsCorrespondenceDialog(node, imagePts, worldPts.Length);

            if (needDialog)
            {
                if (calibImage == null)
                    throw new InvalidOperationException(
                        $"{contextLabel}: 手选像素或图像确认对应需要连接 Image 端口（与取图/加载图像同源）");

                bool dialogManual = manualPick || imagePts == null || imagePts.Length == 0;
                var owner = Window.GetWindow(this);
                var dlg = new NinePointCorrespondenceDialog(
                    calibImage, imagePts, worldPts, owner, dialogManual);
                if (dlg.ShowDialog() != true || dlg.ResultImagePoints == null)
                    throw new OperationCanceledException($"{contextLabel}已取消：未确认点对应关系");
                return dlg.ResultImagePoints;
            }

            if (imagePts == null)
                throw new InvalidOperationException($"{contextLabel}: 缺少 ImagePts（检测到的图像点）");
            if (imagePts.Length != worldPts.Length)
                throw new InvalidOperationException(
                    $"{contextLabel}: 图像点 {imagePts.Length} 个，世界点 {worldPts.Length} 个，数量须一致（请调整 worldPoints/worldPointsFile 或检测数量）");
            return imagePts;
        }

        private static bool HomographyTargetSpaceIsImage(string? targetSpace) =>
            string.Equals(targetSpace?.Trim(), "image", StringComparison.OrdinalIgnoreCase)
            || targetSpace?.Trim() == "图像";

        /// <summary>空 TargetSpace 视为旧版 world 标定。</summary>
        private static bool HomographyMapsToWorld(HomographyTransform h) =>
            !HomographyTargetSpaceIsImage(h.TargetSpace);

        private static bool ParseHomographyTargetSpaceParam(FlowNode node, bool defaultImage)
        {
            string raw = node.Params.GetValueOrDefault("targetSpace", defaultImage ? "image" : "world")?.Trim() ?? "";
            if (string.IsNullOrEmpty(raw))
                return !defaultImage;
            if (HomographyTargetSpaceIsImage(raw))
                return false;
            if (string.Equals(raw, "world", StringComparison.OrdinalIgnoreCase) || raw == "世界")
                return true;
            if (string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase))
                return !defaultImage;
            throw new InvalidOperationException($"未知 targetSpace='{raw}'（image/world）");
        }

        private static bool ResolveHomographyApplyMapsToWorld(FlowNode node, HomographyTransform h)
        {
            string raw = node.Params.GetValueOrDefault("targetSpace", "auto")?.Trim() ?? "auto";
            if (string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase) || raw == "自动")
                return HomographyMapsToWorld(h);
            return ParseHomographyTargetSpaceParam(node, defaultImage: false);
        }

        private static (double Avg, double Max) MeasureHomographyReprojection(
            Point2D[] imagePts, Point2D[] targetPts, HomographyTransform h)
        {
            double sum = 0, max = 0;
            for (int i = 0; i < imagePts.Length; i++)
            {
                var mapped = ApplyHomography(imagePts[i], h);
                double dx = mapped.X - targetPts[i].X;
                double dy = mapped.Y - targetPts[i].Y;
                double e = Math.Sqrt(dx * dx + dy * dy);
                sum += e;
                if (e > max) max = e;
            }

            return (sum / imagePts.Length, max);
        }

        private static CalibImage GrabOneCameraFrameOrThrow(int deviceIndex, int targetWidth, int targetHeight)
        {
            using var cam = new CameraService();
            if (!cam.ConnectByIndex(deviceIndex))
                throw new InvalidOperationException($"相机连接失败，deviceIndex={deviceIndex}");
            var img = cam.GrabOneFrame(targetWidth, targetHeight);
            if (img == null)
                throw new InvalidOperationException($"相机取图失败: {cam.LastError ?? "未知错误"}");
            return img;
        }

        private static (CalibImage LastImage, int Count) GrabLoopCameraFramesOrThrow(
            int deviceIndex, int frameCount, int intervalMs, int targetWidth, int targetHeight)
        {
            frameCount = Math.Max(1, frameCount);
            intervalMs = Math.Max(0, intervalMs);
            CalibImage? last = null;
            int okCount = 0;
            using var cam = new CameraService();
            if (!cam.ConnectByIndex(deviceIndex))
                throw new InvalidOperationException($"相机连接失败，deviceIndex={deviceIndex}");
            for (int i = 0; i < frameCount; i++)
            {
                var img = cam.GrabOneFrame(targetWidth, targetHeight);
                if (img != null)
                {
                    last?.Dispose();
                    last = img;
                    okCount++;
                }
                if (intervalMs > 0 && i < frameCount - 1)
                    System.Threading.Thread.Sleep(intervalMs);
            }
            if (last == null)
                throw new InvalidOperationException($"相机循环取图失败: {cam.LastError ?? "未抓到有效帧"}");
            return (last, okCount);
        }

        private static (List<CalibImage> Frames, int Count) GrabLoopCameraFramesListOrThrow(
            int deviceIndex, int frameCount, int intervalMs, int targetWidth, int targetHeight)
        {
            frameCount = Math.Max(1, frameCount);
            intervalMs = Math.Max(0, intervalMs);
            var frames = new List<CalibImage>(frameCount);
            int okCount = 0;
            using var cam = new CameraService();
            if (!cam.ConnectByIndex(deviceIndex))
                throw new InvalidOperationException($"相机连接失败，deviceIndex={deviceIndex}");
            for (int i = 0; i < frameCount; i++)
            {
                var img = cam.GrabOneFrame(targetWidth, targetHeight);
                if (img != null)
                {
                    frames.Add(img);
                    okCount++;
                }
                if (intervalMs > 0 && i < frameCount - 1)
                    System.Threading.Thread.Sleep(intervalMs);
            }
            if (frames.Count == 0)
                throw new InvalidOperationException($"相机循环取图失败: {cam.LastError ?? "未抓到有效帧"}");
            return (frames, okCount);
        }

        private HashSet<FlowNode> GetDownstreamNodes(FlowNode source)
        {
            var result = new HashSet<FlowNode>();
            var q = new Queue<FlowNode>();
            q.Enqueue(source);
            result.Add(source);
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                foreach (var c in _connections)
                {
                    if (c.FromPort.Owner != cur) continue;
                    var nxt = c.ToPort.Owner;
                    if (result.Add(nxt))
                        q.Enqueue(nxt);
                }
            }
            return result;
        }

        private int MarkDownstreamNodesSkippedFrom(
            FlowNode source,
            string reason,
            string stageTag,
            FlowExecutionGracefulStopException? stopEx = null)
        {
            _skipFlowRunNodeIds ??= new HashSet<Guid>();
            var downstream = GetDownstreamNodes(source);
            downstream.Remove(source);
            int added = 0;
            foreach (var dn in downstream)
            {
                if (_skipFlowRunNodeIds.Add(dn.Id))
                    added++;
            }

            AppendLog($"[{stageTag}] {source.Def.DisplayName}: {reason}，后续分支已跳过 {added} 个节点", true);
            string? detail = !string.IsNullOrWhiteSpace(source.ErrorMessage)
                ? source.ErrorMessage
                : stopEx?.InnerException?.Message;
            if (!string.IsNullOrWhiteSpace(detail)
                && !reason.Contains(detail, StringComparison.Ordinal))
            {
                AppendLog($"[{stageTag}] {source.Def.DisplayName}: 详情: {detail}", true);
            }

            return added;
        }

        /// <summary>send_plc 下游子节点，按主流程拓扑序排列（不含 send_plc 自身）。</summary>
        private List<FlowNode> GetOrderedDownstreamOfNode(FlowNode source)
        {
            var downstream = GetDownstreamNodes(source);
            downstream.Remove(source);
            var sorted = TopologicalSort();
            return sorted.Where(downstream.Contains).ToList();
        }

        /// <summary>
        /// flow_loop 本层执行计划：
        /// PostNodes=本层下游（遇到下一层 flow_loop 截断）；
        /// ChildLoops=本层每轮需要触发的下一层 flow_loop（按拓扑序）。
        /// </summary>
        private (List<FlowNode> PostNodes, List<FlowNode> ChildLoops) GetOrderedLoopLocalPlan(
            FlowNode loopNode,
            IReadOnlyList<FlowNode> sorted)
        {
            var local = new HashSet<FlowNode>();
            var childLoops = new HashSet<FlowNode>();
            var visited = new HashSet<FlowNode> { loopNode };
            var q = new Queue<FlowNode>();
            q.Enqueue(loopNode);
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                foreach (var c in _connections)
                {
                    if (c.FromPort.Owner != cur)
                        continue;
                    var nxt = c.ToPort.Owner;
                    if (!visited.Add(nxt))
                        continue;
                    if (string.Equals(nxt.Def.TypeId, "flow_loop", StringComparison.Ordinal) && nxt != loopNode)
                    {
                        childLoops.Add(nxt); // loop 边界：不跨进子 loop，由递归执行
                        continue;
                    }

                    local.Add(nxt);
                    q.Enqueue(nxt);
                }
            }

            var postNodes = sorted.Where(local.Contains).ToList();
            var orderedChildLoops = sorted.Where(childLoops.Contains).ToList();
            return (postNodes, orderedChildLoops);
        }

#if HALCON_ENABLED
        private static double[] CoerceCoarseShapeSeries(object? value, string portName)
        {
            switch (value)
            {
                case null:
                    throw new InvalidOperationException($"HALCON 粗形状Mask: 缺少 {portName}");
                case double[] arr when arr.Length > 0:
                    return arr;
                case double d:
                    return new[] { d };
                case float f:
                    return new[] { (double)f };
                case int i:
                    return new[] { (double)i };
                case long l:
                    return new[] { (double)l };
                case List<double> listD when listD.Count > 0:
                    return listD.ToArray();
                case List<float> listF when listF.Count > 0:
                    return listF.Select(x => (double)x).ToArray();
                case List<int> listI when listI.Count > 0:
                    return listI.Select(x => (double)x).ToArray();
                case List<long> listL when listL.Count > 0:
                    return listL.Select(x => (double)x).ToArray();
                case List<object?> listObj when listObj.Count > 0:
                {
                    var data = new List<double>(listObj.Count);
                    foreach (var item in listObj)
                    {
                        if (!HalconFlowBridge.TryReadCoarseScalar(item, out double scalar))
                            throw new InvalidOperationException($"HALCON 粗形状Mask: {portName} 列表中含非数值项");
                        data.Add(scalar);
                    }

                    return data.ToArray();
                }
                default:
                    throw new InvalidOperationException(
                        $"HALCON 粗形状Mask: {portName} 类型不支持（需 double/double[]/List<double> 等）");
            }
        }

        private static HalconCoarseMaskBatch BuildCoarseShapeMaskBatchForNode(
            FlowNode maskNode,
            IReadOnlyDictionary<string, object?> inputs)
        {
            var rdImg = inputs["In"] as CalibImage
                        ?? throw new InvalidOperationException("HALCON 粗形状Mask: 缺少 In");
            long rdModelId = HalconFlowBridge.ResolveRegisteredShapeModelId(Convert.ToInt64(inputs["ModelId"]));
            if (rdModelId < 0)
                throw new InvalidOperationException(
                    "HALCON 粗形状Mask: ModelId 须为有效的形状模板(.shm)，请接 create/load_shape_model");
            double[] rdRows = CoerceCoarseShapeSeries(inputs.GetValueOrDefault("CoarseRow"), "CoarseRow");
            double[] rdCols = CoerceCoarseShapeSeries(inputs.GetValueOrDefault("CoarseColumn"), "CoarseColumn");
            inputs.TryGetValue("CoarseAngle", out var rdAngObj);
            double[]? rdAngles = rdAngObj == null ? null : CoerceCoarseShapeSeries(rdAngObj, "CoarseAngle");
            double[]? rdScales = inputs.TryGetValue("CoarseScale", out var rdScaleObj) && rdScaleObj != null
                ? CoerceCoarseShapeSeries(rdScaleObj, "CoarseScale")
                : null;
            double[]? rdScores = inputs.TryGetValue("CoarseScore", out var rdScObj) && rdScObj != null
                ? CoerceCoarseShapeSeries(rdScObj, "CoarseScore")
                : null;

            double maskErosionPx = double.TryParse(
                maskNode.Params.GetValueOrDefault("maskErosionPx", "2"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double me)
                ? me
                : 2;
            int contourLevel = int.TryParse(
                maskNode.Params.GetValueOrDefault("contourLevel", "1"),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int cl)
                ? Math.Max(1, cl)
                : 1;
            int maxCandidates = int.TryParse(
                maskNode.Params.GetValueOrDefault("maxCandidates", "0"),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int mc)
                ? Math.Max(0, mc)
                : 0;
            double maskFillDilatePx = double.TryParse(
                maskNode.Params.GetValueOrDefault("maskFillDilatePx", "0"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double mfd)
                ? Math.Max(0, mfd)
                : 0;

            return HalconComputeRunner.Run(
                () => HalconFlowBridge.BuildCoarseShapeMaskBatch(
                    rdImg,
                    rdModelId,
                    rdRows,
                    rdCols,
                    rdAngles,
                    rdScales,
                    rdScores,
                    maskErosionPx,
                    contourLevel,
                    maxCandidates,
                    maskFillDilatePx),
                HalconThreadPolicy.Geometry);
        }

        private static bool IsHalconMaskLoopFineMatchNode(FlowNode node) =>
            node.Def.TypeId is "halcon_fine_deformable_match" or "halcon_fine_scaled_shape_match";

        private static void ApplyFineMatchMaskLoopInputs(
            IDictionary<string, object?> inputs,
            IReadOnlyDictionary<string, object?> maskNodeInputs,
            HalconCoarseMaskBatch batch,
            int maskIndex)
        {
            ApplyMaskBatchCoarsePoseToFineInputs(inputs, batch, maskIndex);
            if (!inputs.ContainsKey("FullImage") && batch.SourceImage != null)
                inputs["FullImage"] = batch.SourceImage;
            if (maskNodeInputs.TryGetValue("ModelId", out var modelId) && modelId != null)
            {
                long shapeId = HalconFlowBridge.ResolveRegisteredShapeModelId(Convert.ToInt64(modelId));
                if (shapeId >= 0)
                {
                    if (!inputs.ContainsKey("RigidModelId"))
                        inputs["RigidModelId"] = shapeId;
                    if (!inputs.ContainsKey("ModelId"))
                        inputs["ModelId"] = shapeId;
                }
            }
        }

        private Dictionary<string, object?> BuildFineMatchMaskLoopInputs(
            FlowNode fineNode,
            IReadOnlyDictionary<string, object?> maskNodeInputs,
            HalconCoarseMaskBatch batch,
            int maskIndex)
        {
            var inputs = GetNodeInputs(fineNode);
            ApplyFineMatchMaskLoopInputs(inputs, maskNodeInputs, batch, maskIndex);
            return inputs;
        }

        private static void ApplyMaskBatchCoarsePoseToFineInputs(
            IDictionary<string, object?> inputs,
            HalconCoarseMaskBatch batch,
            int maskIndex)
        {
            if (maskIndex < 0 || maskIndex >= batch.Count)
                return;
            inputs["CoarseRow"] = batch.CoarseRows[maskIndex];
            inputs["CoarseColumn"] = batch.CoarseCols[maskIndex];
            inputs["CoarseAngle"] = batch.CoarseAngles[maskIndex];
            if (maskIndex < batch.CoarseScales.Length)
                inputs["CoarseScale"] = batch.CoarseScales[maskIndex];
        }

        private static long ResolveFineMatchRigidModelId(IReadOnlyDictionary<string, object?> inputs)
        {
            if (inputs.TryGetValue("RigidModelId", out var rigidObj) && rigidObj != null)
            {
                long id = Convert.ToInt64(rigidObj);
                return HalconFlowBridge.ResolveRegisteredShapeModelId(id);
            }

            if (inputs.TryGetValue("ModelId", out var modelObj) && modelObj != null)
            {
                long id = Convert.ToInt64(modelObj);
                return HalconFlowBridge.ResolveRegisteredShapeModelId(id);
            }

            return -1;
        }

        private static void SetCoarseShapeMaskRoundOutputs(FlowNode maskNode, HalconCoarseMaskBatch batch, int index)
        {
            maskNode.Outputs["Mask"] = batch.Masks[index];
            maskNode.Outputs["MaskIndex"] = index;
            maskNode.Outputs["MaskCount"] = batch.Count;
            maskNode.Outputs["CoarseRowOut"] = batch.CoarseRows[index];
            maskNode.Outputs["CoarseColumnOut"] = batch.CoarseCols[index];
            maskNode.Outputs["CoarseAngleOut"] = batch.CoarseAngles[index];
            maskNode.Outputs["CoarseScaleOut"] = index < batch.CoarseScales.Length ? batch.CoarseScales[index] : 1.0;
            maskNode.ResultSummary = $"Mask {index + 1}/{batch.Count}";
        }

        private sealed class FineMatchRoundAccumulator
        {
            public readonly List<double> Rows = new();
            public readonly List<double> Cols = new();
            public readonly List<double> Angles = new();
            public readonly List<double> Scales = new();
            public readonly List<double> Scores = new();
            public readonly List<Point2D[]> Contours = new();
            public int Width;
            public int Height;
            public bool HasShapeXld;
        }

        private static void AccumulateFineMatchRound(FlowNode fineNode, FineMatchRoundAccumulator acc)
        {
            if (fineNode.Outputs.TryGetValue("Row", out var rowObj))
            {
                if (rowObj is double[] rowArr)
                    acc.Rows.AddRange(rowArr);
                else if (HalconFlowBridge.TryReadCoarseScalar(rowObj, out double rowScalar))
                    acc.Rows.Add(rowScalar);
            }

            if (fineNode.Outputs.TryGetValue("Column", out var colObj))
            {
                if (colObj is double[] colArr)
                    acc.Cols.AddRange(colArr);
                else if (HalconFlowBridge.TryReadCoarseScalar(colObj, out double colScalar))
                    acc.Cols.Add(colScalar);
            }

            if (fineNode.Outputs.TryGetValue("Angle", out var angObj))
            {
                if (angObj is double[] angArr)
                    acc.Angles.AddRange(angArr);
                else if (HalconFlowBridge.TryReadCoarseScalar(angObj, out double angScalar))
                    acc.Angles.Add(angScalar);
            }

            if (fineNode.Outputs.TryGetValue("Score", out var scObj))
            {
                if (scObj is double[] scArr)
                    acc.Scores.AddRange(scArr);
                else if (HalconFlowBridge.TryReadCoarseScalar(scObj, out double scScalar))
                    acc.Scores.Add(scScalar);
            }

            if (fineNode.Outputs.TryGetValue("Scale", out var scaleObj))
            {
                if (scaleObj is double[] scaleArr)
                    acc.Scales.AddRange(scaleArr);
                else if (HalconFlowBridge.TryReadCoarseScalar(scaleObj, out double scaleScalar))
                    acc.Scales.Add(scaleScalar);
            }

            if (fineNode.Outputs.TryGetValue("ShapeXld", out var shapeXldObj)
                && shapeXldObj is HalconXldContourBundle shapeBundle
                && shapeBundle.Contours != null)
            {
                acc.HasShapeXld = true;
                acc.Width = shapeBundle.Width;
                acc.Height = shapeBundle.Height;
                acc.Contours.AddRange(shapeBundle.Contours);
            }
            else if (fineNode.Outputs.TryGetValue("DeformedXld", out var xldObj)
                && xldObj is HalconXldContourBundle bundle
                && bundle.Contours != null)
            {
                acc.Width = bundle.Width;
                acc.Height = bundle.Height;
                acc.Contours.AddRange(bundle.Contours);
            }
        }

        private static void ApplyFineMatchAccumulator(FlowNode fineNode, FineMatchRoundAccumulator acc)
        {
            fineNode.Outputs["Row"] = acc.Rows.ToArray();
            fineNode.Outputs["Column"] = acc.Cols.ToArray();
            fineNode.Outputs["Angle"] = acc.Angles.ToArray();
            fineNode.Outputs["Score"] = acc.Scores.ToArray();
            if (acc.Scales.Count > 0)
                fineNode.Outputs["Scale"] = acc.Scales.ToArray();
            if (acc.Contours.Count > 0)
            {
                var xld = new HalconXldContourBundle
                {
                    Width = acc.Width,
                    Height = acc.Height,
                    Contours = acc.Contours
                };
                fineNode.Outputs[acc.HasShapeXld ? "ShapeXld" : "DeformedXld"] = xld;
            }

            fineNode.ResultSummary = acc.Rows.Count == 0
                ? "精匹配无结果"
                : $"精 {acc.Rows.Count} 个（Mask 循环汇总）";
        }

        private async System.Threading.Tasks.Task<bool> RunCoarseShapeMaskEachLoopAsync(List<FlowNode> sorted, FlowNode maskNode)
        {
            var downstream = GetDownstreamNodes(maskNode);
            var preNodes = sorted.Where(n => !downstream.Contains(n)).ToList();
            var postNodes = GetOrderedDownstreamOfNode(maskNode);
            var fineNodes = postNodes.Where(IsHalconMaskLoopFineMatchNode).ToList();
            var fineAccumulators = fineNodes.ToDictionary(n => n.Id, _ => new FineMatchRoundAccumulator());

            AppendLog(
                $"检测到粗形状Mask循环: {maskNode.Def.DisplayName}，前置 {preNodes.Count} 节点，下游 {postNodes.Count} 节点");

            int successCountPre = 0;
            for (int i = 0; i < preNodes.Count; i++)
            {
                ThrowIfExecutionCancelled();
                var node = preNodes[i];
                if (_skipFlowRunNodeIds != null && _skipFlowRunNodeIds.Contains(node.Id))
                {
                    AppendLog($"[MASK-PRE] 跳过: {node.Def.DisplayName}");
                    continue;
                }
                StatusText.Text = $"Mask循环-前置 [{i + 1}/{preNodes.Count}] {node.Def.DisplayName}...";
                AppendLog($"[MASK-PRE {i + 1}/{preNodes.Count}] 执行: {node.Def.DisplayName}");
                await System.Threading.Tasks.Task.Yield();
                try
                {
                    await ExecuteNodeForRunAsync(node, "MASK-PRE");
                    successCountPre++;
                }
                catch (FlowExecutionGracefulStopException ex)
                {
                    MarkDownstreamNodesSkippedFrom(node, ex.Message, "MASK-PRE-STOP");
                    continue;
                }
            }

            var maskInputs = GetNodeInputs(maskNode);
            var batch = BuildCoarseShapeMaskBatchForNode(maskNode, maskInputs);
            if (batch.Count == 0)
                throw new InvalidOperationException("HALCON 粗形状Mask: 无粗候选，无法生成 Mask");

            maskNode.Executed = true;
            maskNode.ErrorMessage = null;
            SetNodeStatus(maskNode, false);
            UpdateNodeSummary(maskNode);

            var postLoopDeferred = ComputePostLoopDeferredNodes(postNodes);
            var perRoundNodes = postNodes.Where(n => !postLoopDeferred.Contains(n)).ToList();
            if (postLoopDeferred.Count > 0)
                AppendLog($"Mask循环延后执行: {postLoopDeferred.Count} 个节点（如轨迹收集之后接简化）");

            EnterFlowLoopSinkAccumulate();
            try
            {
                ResetFlowSinkAccumulators(perRoundNodes);

                for (int mi = 0; mi < batch.Count; mi++)
                {
                    ThrowIfExecutionCancelled();
                    SetCoarseShapeMaskRoundOutputs(maskNode, batch, mi);
                    UpdateNodeSummary(maskNode);
                    AppendLog($"[MASK {mi + 1}/{batch.Count}] 开始下游 {perRoundNodes.Count} 节点");

                    for (int j = 0; j < perRoundNodes.Count; j++)
                    {
                        ThrowIfExecutionCancelled();
                        var node = perRoundNodes[j];
                    if (_skipFlowRunNodeIds != null && _skipFlowRunNodeIds.Contains(node.Id))
                    {
                        AppendLog($"[MASK] 跳过: {node.Def.DisplayName}");
                        continue;
                    }
                        node.Outputs.Clear();
                        node.ErrorMessage = null;
                        node.Executed = false;
                        StatusText.Text =
                            $"Mask[{mi + 1}/{batch.Count}] [{j + 1}/{perRoundNodes.Count}] {node.Def.DisplayName}...";
                        await System.Threading.Tasks.Task.Yield();
                        try
                        {
                            Dictionary<string, object?>? loopInputs = IsHalconMaskLoopFineMatchNode(node)
                                ? BuildFineMatchMaskLoopInputs(node, maskInputs, batch, mi)
                                : null;
                            await ExecuteNodeForRunAsync(node, $"M{mi + 1}", loopInputs);
                        }
                        catch (FlowExecutionGracefulStopException ex)
                        {
                            MarkDownstreamNodesSkippedFrom(node, ex.Message, "MASK-STOP");
                            continue;
                        }

                        if (fineAccumulators.TryGetValue(node.Id, out var acc))
                            AccumulateFineMatchRound(node, acc);
                    }
                }
            }
            finally
            {
                ExitFlowLoopSinkAccumulate();
            }

            RefreshPointsSinkMergedOutputs(perRoundNodes);

            foreach (FlowNode fn in fineNodes)
            {
                fn.Executed = true;
                fn.ErrorMessage = null;
                SetNodeStatus(fn, false);
                ApplyFineMatchAccumulator(fn, fineAccumulators[fn.Id]);
                UpdateNodeSummary(fn);
            }

            var orderedDeferred = postNodes.Where(postLoopDeferred.Contains).ToList();
            if (orderedDeferred.Count > 0)
            {
                AppendLog($"[MASK] 循环后执行 {orderedDeferred.Count} 个节点");
                for (int j = 0; j < orderedDeferred.Count; j++)
                {
                    ThrowIfExecutionCancelled();
                    var node = orderedDeferred[j];
                    if (_skipFlowRunNodeIds != null && _skipFlowRunNodeIds.Contains(node.Id))
                    {
                        AppendLog($"[MASK-POST] 跳过: {node.Def.DisplayName}");
                        continue;
                    }
                    node.Outputs.Clear();
                    node.ErrorMessage = null;
                    node.Executed = false;
                    StatusText.Text = $"Mask循环后 [{j + 1}/{orderedDeferred.Count}] {node.Def.DisplayName}...";
                    await System.Threading.Tasks.Task.Yield();
                    try
                    {
                        await ExecuteNodeForRunAsync(node, "MASK-POST");
                    }
                    catch (FlowExecutionGracefulStopException ex)
                    {
                        MarkDownstreamNodesSkippedFrom(node, ex.Message, "MASK-POST-STOP");
                        continue;
                    }
                }
            }

            int fineTotal = fineNodes.Sum(fn => fineAccumulators[fn.Id].Rows.Count);
            StatusText.Text =
                $"粗形状Mask循环完成: 前置 {successCountPre}/{preNodes.Count}，{batch.Count} 张 Mask，精匹配 {fineTotal} 个"
                + (orderedDeferred.Count > 0 ? $"，延后 {orderedDeferred.Count}" : "");
            StatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
            AppendLog(
                $"========== 粗形状Mask循环完成: masks={batch.Count}，精匹配汇总={fineTotal} ==========");
            return true;
        }
#endif

        /// <summary>循环结束后才执行的节点：exposure_fusion，以及仅由 flow_sink / points_sink 供数的下游。</summary>
        private HashSet<FlowNode> ComputePostLoopDeferredNodes(IReadOnlyList<FlowNode> postNodes) =>
            ComputePostLoopDeferredNodesCore(postNodes, n =>
                _connections.Where(c => c.ToPort.Owner == n && postNodes.Contains(c.FromPort.Owner))
                    .Select(c => c.FromPort.Owner));

        private static HashSet<FlowNode> ComputePostLoopDeferredNodesCore(
            IReadOnlyList<FlowNode> postNodes,
            Func<FlowNode, IEnumerable<FlowNode>> getInboundSourcesInPost)
        {
            var deferred = new HashSet<FlowNode>(postNodes.Where(n => n.Def.TypeId == "exposure_fusion"));
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var n in postNodes)
                {
                    if (deferred.Contains(n))
                        continue;

                    var inSources = getInboundSourcesInPost(n).ToList();
                    if (inSources.Count == 0)
                        continue;

                    if (inSources.All(s =>
                            deferred.Contains(s)
                            || s.Def.TypeId is "flow_sink" or "points_sink"))
                    {
                        deferred.Add(n);
                        changed = true;
                    }
                }
            }

            return deferred;
        }

        private static void ResetFlowSinkAccumulators(IEnumerable<FlowNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.Def.TypeId == "flow_sink")
                    n.SinkAccumulator = new List<object?>();
                if (n.Def.TypeId == "points_sink")
                    n.PointsSinkAccumulator = new PointsSinkRoundAccumulator();
            }
        }

        private static void RefreshPointsSinkMergedOutputs(IEnumerable<FlowNode> perRoundNodes)
        {
            foreach (var n in perRoundNodes.Where(n => n.Def.TypeId == "points_sink"))
            {
                if (n.PointsSinkAccumulator == null)
                    continue;
                ApplyPointsSinkOutputs(n, n.PointsSinkAccumulator, new Dictionary<string, object?>());
            }
        }

        private async System.Threading.Tasks.Task<string> ExecuteManagedFlowLoopAsync(
            FlowNode loopNode,
            IReadOnlyList<FlowNode> sorted,
            HashSet<Guid> executedNodeIds,
            string scopeTag)
        {
            var listPortMaps = BuildFlowLoopListPortMaps(loopNode.Params);
            var (postNodes, childLoops) = GetOrderedLoopLocalPlan(loopNode, sorted);
            var predecessorSet = CollectPredecessorsIncludingSelf(loopNode);
            var preNodes = sorted
                .Where(n => predecessorSet.Contains(n)
                            && n != loopNode
                            && !string.Equals(n.Def.TypeId, "flow_loop", StringComparison.Ordinal)
                            && !executedNodeIds.Contains(n.Id))
                .ToList();

            ResolveFlowLoopSchedule(
                loopNode.Params.GetValueOrDefault("count"),
                loopNode.Params.GetValueOrDefault("stepValues"),
                out var repeatCount,
                out var infiniteLoop,
                out var stepValues);
            int intervalMs = int.TryParse(loopNode.Params.GetValueOrDefault("intervalMs"), out var im) ? im : 0;
            intervalMs = Math.Max(0, intervalMs);

            string loopLabel = infiniteLoop ? "∞" : repeatCount.ToString(CultureInfo.InvariantCulture);
            if (stepValues != null && stepValues.Length > 0)
                AppendLog($"[{scopeTag}] flow_loop: {loopNode.Def.DisplayName} x{loopLabel}，StepValue=[{string.Join("; ", stepValues.Select(v => v.ToString("G", CultureInfo.InvariantCulture)))}]");
            else
                AppendLog($"[{scopeTag}] flow_loop: {loopNode.Def.DisplayName} x{loopLabel}，前置 {preNodes.Count} 节点，本层下游 {postNodes.Count} 节点，子循环 {childLoops.Count} 个");
            if (infiniteLoop)
                AppendLog($"[{scopeTag}] [LOOP] 无限循环：点击「停止」结束");

            var postLoopDeferred = ComputePostLoopDeferredNodes(postNodes);
            var perRoundNodes = postNodes.Where(n => !postLoopDeferred.Contains(n)).ToList();
            if (postLoopDeferred.Count > 0)
                AppendLog($"[{scopeTag}] 循环延后执行: {postLoopDeferred.Count} 个节点（如 Exposure Fusion）");

            int successCountPre = 0;
            int errorCountPre = 0;
            long completedRounds = 0;
            int errorCountPerRound = 0;
            int errorCountPostLoop = 0;
            IReadOnlyDictionary<string, object?> loopInputsForRounds = new Dictionary<string, object?>();
            EnterFlowLoopSinkAccumulate();
            try
            {
                // 仅清理“最近 loop”本层收集器，保证 flow_sink 作用域归属最近 flow_loop。
                // 当最近 loop 再次进入时（例如被上层 loop 每轮触发），会在此处清空上次累积。
                ResetFlowSinkAccumulators(perRoundNodes);

                for (int i = 0; i < preNodes.Count; i++)
                {
                    ThrowIfExecutionCancelled();
                    var node = preNodes[i];
                    if (_skipFlowRunNodeIds != null && _skipFlowRunNodeIds.Contains(node.Id))
                    {
                        AppendLog($"[{scopeTag}][LOOP-PRE] 跳过: {node.Def.DisplayName}");
                        continue;
                    }

                    StatusText.Text = $"循环-前置 [{i + 1}/{preNodes.Count}] {node.Def.DisplayName}...";
                    AppendLog($"[{scopeTag}][LOOP-PRE {i + 1}/{preNodes.Count}] 执行: {node.Def.DisplayName}");
                    await System.Threading.Tasks.Task.Yield();
                    try
                    {
                        await ExecuteNodeForRunAsync(node, $"{scopeTag}-LOOP-PRE");
                        successCountPre++;
                        executedNodeIds.Add(node.Id);
                    }
                    catch (FlowExecutionGracefulStopException ex)
                    {
                        MarkDownstreamNodesSkippedFrom(node, ex.Message, $"{scopeTag}-LOOP-PRE-STOP");
                        executedNodeIds.Add(node.Id);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        errorCountPre++;
                        AppendLog($"[{scopeTag}][LOOP-PRE][ERROR] {node.Def.DisplayName}: {ex.Message}", true);
                        continue;
                    }
                }

                loopInputsForRounds = GetNodeInputs(loopNode);
                var listLengths = CollectFlowLoopListLengths(loopInputsForRounds, listPortMaps);
                var activeListLengths = listLengths.Where(kv => kv.Value > 0).ToList();
                if (activeListLengths.Count > 0)
                {
                    int firstLen = activeListLengths[0].Value;
                    bool sameLen = activeListLengths.All(kv => kv.Value == firstLen);
                    if (!sameLen)
                    {
                        string detail = string.Join(", ", activeListLengths.Select(kv => $"{kv.Key}={kv.Value}"));
                        throw new InvalidOperationException($"循环: 多路列表长度不一致（非空端口：{detail}）");
                    }

                    repeatCount = firstLen;
                    infiniteLoop = false;
                    if (stepValues != null && stepValues.Length > 0 && stepValues.Length != repeatCount)
                        throw new InvalidOperationException(
                            $"循环: stepValues 长度({stepValues.Length})与列表长度({repeatCount})不一致");
                    AppendLog($"[{scopeTag}][LOOP] 列表驱动轮数={repeatCount}（非空端口：{string.Join(", ", activeListLengths.Select(kv => $"{kv.Key}:{kv.Value}"))}）");
                }
                else if (listLengths.Count > 0)
                {
                    repeatCount = 0;
                    infiniteLoop = false;
                    AppendLog($"[{scopeTag}][LOOP] 多路列表均为空，轮数=0");
                }

                long li = 0;
                while (infiniteLoop || li < repeatCount)
                {
                    ThrowIfExecutionCancelled();
                    loopNode.Outputs.Clear();
                    loopNode.Outputs["Index"] = li > int.MaxValue ? int.MaxValue : (int)li;
                    loopNode.Outputs["Count"] = infiniteLoop ? -1 : repeatCount;
                    if (stepValues != null && li < stepValues.Length)
                        loopNode.Outputs["StepValue"] = stepValues[li];
                    ApplyFlowLoopListOutputs(loopNode, loopInputsForRounds, (int)li, listPortMaps);
                    if (!loopNode.Outputs.ContainsKey("Out") && GetInputData(loopNode, "After") is { } afterVal)
                        loopNode.Outputs["Out"] = afterVal;
                    loopNode.Executed = true;
                    loopNode.ErrorMessage = null;
                    SetNodeStatus(loopNode, false);
                    string roundTag = infiniteLoop
                        ? $"loop {li + 1}/∞"
                        : $"loop {li + 1}/{repeatCount}";
                    if (stepValues != null && li < stepValues.Length)
                        roundTag += $" v={stepValues[li]:G}";
                    loopNode.ResultSummary = roundTag;
                    UpdateNodeSummary(loopNode);

                    AppendLog($"[{scopeTag}][LOOP {roundTag}] 开始本层下游 {perRoundNodes.Count} 节点");
                    for (int j = 0; j < perRoundNodes.Count; j++)
                    {
                        ThrowIfExecutionCancelled();
                        var node = perRoundNodes[j];
                        if (_skipFlowRunNodeIds != null && _skipFlowRunNodeIds.Contains(node.Id))
                        {
                            AppendLog($"[{scopeTag}][LOOP] 跳过: {node.Def.DisplayName}");
                            continue;
                        }

                        node.Outputs.Clear();
                        node.ErrorMessage = null;
                        node.Executed = false;
                        StatusText.Text = $"循环[{roundTag}] [{j + 1}/{perRoundNodes.Count}] {node.Def.DisplayName}...";
                        await System.Threading.Tasks.Task.Yield();
                        try
                        {
                            await ExecuteNodeForRunAsync(node, $"{scopeTag}-L{li + 1}");
                            executedNodeIds.Add(node.Id);
                        }
                        catch (FlowExecutionGracefulStopException ex)
                        {
                            MarkDownstreamNodesSkippedFrom(node, ex.Message, $"{scopeTag}-LOOP-STOP", ex);
                            executedNodeIds.Add(node.Id);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"[{scopeTag}][LOOP][ERROR] {node.Def.DisplayName}: {ex.Message}", true);
                            errorCountPerRound++;
                            continue;
                        }
                    }

                    // 子 flow_loop（最近边界）按每轮触发，形成“串联继续执行”语义。
                    foreach (var childLoop in childLoops)
                    {
                        string childDone = await ExecuteManagedFlowLoopAsync(
                            childLoop,
                            sorted,
                            executedNodeIds,
                            $"{scopeTag}:{loopNode.Def.DisplayName}:{li + 1}");
                        AppendLog($"[{scopeTag}][LOOP {roundTag}] 子循环完成: {childLoop.Def.DisplayName} -> {childDone}");
                    }

                    completedRounds++;
                    li++;
                    if (intervalMs > 0 && (infiniteLoop || li < repeatCount))
                        await System.Threading.Tasks.Task.Delay(intervalMs, _runCts?.Token ?? System.Threading.CancellationToken.None);
                }
            }
            finally
            {
                ExitFlowLoopSinkAccumulate();
            }

            RefreshPointsSinkMergedOutputs(perRoundNodes);

            var orderedDeferred = postNodes.Where(postLoopDeferred.Contains).ToList();
            if (orderedDeferred.Count > 0)
            {
                AppendLog($"[{scopeTag}][LOOP] 循环后执行 {orderedDeferred.Count} 个节点");
                for (int j = 0; j < orderedDeferred.Count; j++)
                {
                    ThrowIfExecutionCancelled();
                    var node = orderedDeferred[j];
                    if (_skipFlowRunNodeIds != null && _skipFlowRunNodeIds.Contains(node.Id))
                    {
                        AppendLog($"[{scopeTag}][LOOP-POST] 跳过: {node.Def.DisplayName}");
                        continue;
                    }

                    node.Outputs.Clear();
                    node.ErrorMessage = null;
                    node.Executed = false;
                    StatusText.Text = $"循环后 [{j + 1}/{orderedDeferred.Count}] {node.Def.DisplayName}...";
                    await System.Threading.Tasks.Task.Yield();
                    try
                    {
                        await ExecuteNodeForRunAsync(node, $"{scopeTag}-LOOP-POST");
                        executedNodeIds.Add(node.Id);
                    }
                    catch (FlowExecutionGracefulStopException ex)
                    {
                        MarkDownstreamNodesSkippedFrom(node, ex.Message, $"{scopeTag}-LOOP-POST-STOP");
                        executedNodeIds.Add(node.Id);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"[{scopeTag}][LOOP-POST][ERROR] {node.Def.DisplayName}: {ex.Message}", true);
                        errorCountPostLoop++;
                        continue;
                    }
                }
            }

            executedNodeIds.Add(loopNode.Id);
            string doneRounds = infiniteLoop
                ? $"{completedRounds} 轮（已停止）"
                : $"{completedRounds} 轮";
            StatusText.Text = $"循环完成: {loopNode.Def.DisplayName} 前置 {successCountPre}/{preNodes.Count}，{doneRounds} × 下游 {perRoundNodes.Count} 节点/轮"
                + (orderedDeferred.Count > 0 ? $"，延后 {orderedDeferred.Count}" : "")
                + (errorCountPre > 0 ? $"，前置错误 {errorCountPre}" : "")
                + (errorCountPerRound > 0 ? $"，轮内错误 {errorCountPerRound}" : "")
                + (errorCountPostLoop > 0 ? $"，延后错误 {errorCountPostLoop}" : "");
            StatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
            AppendLog($"========== flow_loop 完成: {loopNode.Def.DisplayName}, {doneRounds}，下游 {perRoundNodes.Count} 节点/轮 ==========");
            return doneRounds;
        }

        private static void AppendPointsSinkRound(
            PointsSinkRoundAccumulator acc,
            Point2D[] pts,
            int[]? barIn,
            string groupIdMode,
            bool loopAccumulate)
        {
            if (pts == null || pts.Length == 0)
            {
                acc.RoundCount++;
                return;
            }

            string mode = (groupIdMode ?? "round").Trim().ToLowerInvariant();
            // 循环累积时 preserve + 每轮 XLD BarIds 常为全 0，会合并成一条焊道
            if (loopAccumulate && mode == "preserve"
                && (barIn == null || barIn.Length != pts.Length || barIn.Distinct().Count() <= 1))
                mode = "round";

            int[] idsForRound;
            if (mode == "preserve" && barIn != null && barIn.Length == pts.Length)
            {
                idsForRound = barIn;
            }
            else if (mode == "offset" && barIn != null && barIn.Length == pts.Length)
            {
                int minBar = barIn.Min();
                int offset = 0;
                if (acc.GroupBarIds.Count > 0)
                    offset = acc.GroupBarIds.Max() + 1 - minBar;
                idsForRound = barIn.Select(b => b + offset).ToArray();
            }
            else
            {
                int gid = acc.RoundCount;
                idsForRound = Enumerable.Repeat(gid, pts.Length).ToArray();
            }

            acc.Points.AddRange(pts);
            acc.GroupBarIds.AddRange(idsForRound);
            acc.RoundCount++;
        }

        private static void ApplyPointsSinkOutputs(FlowNode node, PointsSinkRoundAccumulator acc, Dictionary<string, object?> inputs)
        {
            var mergedPts = acc.Points.ToArray();
            var mergedIds = acc.GroupBarIds.Count == mergedPts.Length
                ? acc.GroupBarIds.ToArray()
                : Enumerable.Repeat(0, mergedPts.Length).ToArray();

            node.Outputs["MergedPoints"] = mergedPts;
            node.Outputs["MergedGroupBarIds"] = mergedIds;
            node.Outputs["BarIds"] = mergedIds;
            node.Outputs["Count"] = acc.RoundCount;
            if (inputs.TryGetValue("After", out var afterSink))
                node.Outputs["Out"] = afterSink;
            else if (mergedPts.Length > 0)
                node.Outputs["Out"] = mergedPts;

            node.ResultSummary = mergedPts.Length == 0
                ? $"收集 {acc.RoundCount} 轮 · 0 点"
                : $"收集 {acc.RoundCount} 轮 · {mergedPts.Length} 点 · 条号 {mergedIds.Distinct().Count()} 种";
        }

        private static List<CalibImage> CoerceToCalibImageList(object? src, string context)
        {
            if (src == null)
                return new List<CalibImage>();

            if (src is List<CalibImage> typed)
                return typed;

            if (src is CalibImage single)
                return new List<CalibImage> { single };

            if (src is System.Collections.IList list)
            {
                var result = new List<CalibImage>(list.Count);
                foreach (var item in list)
                {
                    if (item is CalibImage ci)
                        result.Add(ci);
                    else if (item != null)
                        throw new InvalidOperationException($"{context}: 列表项类型须为 CalibImage，当前为 {item.GetType().Name}");
                }

                return result;
            }

            throw new InvalidOperationException($"{context}: Images 须为 ImageList 或 CalibImage 列表");
        }

        private static List<Point2D[]> CoerceToPoint2DPolylineList(object? src)
        {
            var result = new List<Point2D[]>();
            if (src == null)
                return result;

            if (src is List<Point2D[]> typed)
            {
                foreach (Point2D[]? seg in typed)
                {
                    if (seg != null && seg.Length > 0)
                        result.Add(seg);
                }

                return result;
            }

            if (src is Point2D[][] arr2d)
            {
                foreach (Point2D[]? seg in arr2d)
                {
                    if (seg != null && seg.Length > 0)
                        result.Add(seg);
                }

                return result;
            }

#if HALCON_ENABLED
            if (src is HalconXldContourBundle xld && xld.Contours != null)
            {
                foreach (Point2D[]? seg in xld.Contours)
                {
                    if (seg != null && seg.Length > 0)
                        result.Add(seg);
                }

                return result;
            }
#endif

            if (src is System.Collections.IList list)
            {
                foreach (var item in list)
                {
                    switch (item)
                    {
                        case Point2D[] seg when seg.Length > 0:
                            result.Add(seg);
                            break;
                        case List<Point2D> lp when lp.Count > 0:
                            result.Add(lp.ToArray());
                            break;
                        case CalibPoint3D[] p3 when p3.Length > 0:
                            result.Add(p3.Select(p => new Point2D(p.X, p.Y)).ToArray());
                            break;
                    }
                }

                return result;
            }

            return result;
        }

        private static (Point2D[]? Points, int[]? BarIds) MergeDisplayPointOverlays(
            Point2D[]? singlePts,
            object? pointsListObj,
            int[]? explicitBarIds)
        {
            var polylines = CoerceToPoint2DPolylineList(pointsListObj);
            if (singlePts != null && singlePts.Length > 0)
                polylines.Add(singlePts);

            if (polylines.Count == 0)
                return (null, null);

            if (explicitBarIds != null
                && polylines.Count == 1
                && explicitBarIds.Length == polylines[0].Length)
            {
                return (polylines[0], explicitBarIds);
            }

            var mergedPts = new List<Point2D>();
            var mergedBarIds = new List<int>();
            for (int i = 0; i < polylines.Count; i++)
            {
                Point2D[] seg = polylines[i];
                mergedPts.AddRange(seg);
                mergedBarIds.AddRange(Enumerable.Repeat(i, seg.Length));
            }

            return (mergedPts.ToArray(), mergedBarIds.ToArray());
        }

        private static CalibImage FuseExposureCalibImages(IReadOnlyList<CalibImage> images, string mode)
        {
            if (images == null || images.Count == 0)
                throw new InvalidOperationException("Exposure Fusion: 图像列表为空");

            if (images.Count == 1)
                return CalibAPI.DuplicateImage(images[0]);

            string m = (mode ?? "mertens").Trim().ToLowerInvariant();
            if (m is "mertens" or "exposure" or "exposure_fusion" or "hdr")
                return FuseExposureMertensManaged(images);

#if HALCON_ENABLED
            if (m == "max")
                return FuseExposureChainHalcon(images, useMax: true);
            if (m == "min")
                return FuseExposureChainHalcon(images, useMax: false);
#endif
            if (m == "max")
                return FuseExposureMaxMinManaged(images, useMax: true);
            if (m == "min")
                return FuseExposureMaxMinManaged(images, useMax: false);
            if (m == "mean" || m == "average" || m == "avg")
                return FuseExposureMeanManaged(images);

            throw new InvalidOperationException($"Exposure Fusion: 未知 mode='{mode}'（mertens/max/min/mean）");
        }

#if HALCON_ENABLED
        private static CalibImage FuseExposureChainHalcon(IReadOnlyList<CalibImage> images, bool useMax)
        {
            CalibImage acc = images[0];
            for (int i = 1; i < images.Count; i++)
            {
                var merged = useMax
                    ? HalconFlowBridge.MaxImageToCalib(acc, images[i])
                    : HalconFlowBridge.MinImageToCalib(acc, images[i]);
                if (i > 1)
                    acc.Dispose();
                acc = merged;
            }

            return acc;
        }
#endif

        private static CalibImage FuseExposureMaxMinManaged(IReadOnlyList<CalibImage> images, bool useMax)
        {
            var n0 = images[0].GetNativeStruct();
            int nPix = n0.width * n0.height;
            foreach (var img in images.Skip(1))
            {
                var ni = img.GetNativeStruct();
                if (ni.width != n0.width || ni.height != n0.height)
                    throw new InvalidOperationException($"Exposure Fusion: 尺寸须一致，{n0.width}x{n0.height} 与 {ni.width}x{ni.height}");
            }

            var acc = new byte[nPix];
            FillGrayBytesFromCalib(images[0], acc);
            var buf = new byte[nPix];
            for (int i = 1; i < images.Count; i++)
            {
                FillGrayBytesFromCalib(images[i], buf);
                for (int p = 0; p < nPix; p++)
                    acc[p] = useMax
                        ? (byte)Math.Max(acc[p], buf[p])
                        : (byte)Math.Min(acc[p], buf[p]);
            }

            var outImg = new CalibImage(n0.width, n0.height, 1);
            Marshal.Copy(acc, 0, outImg.GetNativeStruct().data, nPix);
            return outImg;
        }

        private static CalibImage FuseExposureMeanManaged(IReadOnlyList<CalibImage> images)
        {
            var n0 = images[0].GetNativeStruct();
            int nPix = n0.width * n0.height;
            foreach (var img in images.Skip(1))
            {
                var ni = img.GetNativeStruct();
                if (ni.width != n0.width || ni.height != n0.height)
                    throw new InvalidOperationException($"Exposure Fusion: 尺寸须一致，{n0.width}x{n0.height} 与 {ni.width}x{ni.height}");
            }

            var sum = new double[nPix];
            var buf = new byte[nPix];
            foreach (var img in images)
            {
                FillGrayBytesFromCalib(img, buf);
                for (int p = 0; p < nPix; p++)
                    sum[p] += buf[p];
            }

            double inv = 1.0 / images.Count;
            var dst = new byte[nPix];
            for (int p = 0; p < nPix; p++)
                dst[p] = (byte)Math.Clamp(Math.Round(sum[p] * inv), 0.0, 255.0);

            var outImg = new CalibImage(n0.width, n0.height, 1);
            Marshal.Copy(dst, 0, outImg.GetNativeStruct().data, nPix);
            return outImg;
        }

        /// <summary>Mertens 风格：按局部梯度与曝光良好度加权平均（多曝光融合常用）。</summary>
        private static CalibImage FuseExposureMertensManaged(IReadOnlyList<CalibImage> images)
        {
            var n0 = images[0].GetNativeStruct();
            int w = n0.width, h = n0.height, nPix = w * h;
            foreach (var img in images.Skip(1))
            {
                var ni = img.GetNativeStruct();
                if (ni.width != w || ni.height != h)
                    throw new InvalidOperationException($"Exposure Fusion: 尺寸须一致，{w}x{h} 与 {ni.width}x{ni.height}");
            }

            var weights = new double[nPix];
            var gray = new byte[nPix];
            var acc = new double[nPix];
            var norm = new double[nPix];
            const double eps = 1e-6;

            foreach (var img in images)
            {
                FillGrayBytesFromCalib(img, gray);
                ComputeMertensWeights(gray, w, h, weights);
                for (int p = 0; p < nPix; p++)
                {
                    acc[p] += gray[p] * weights[p];
                    norm[p] += weights[p];
                }
            }

            var dst = new byte[nPix];
            for (int p = 0; p < nPix; p++)
            {
                double d = norm[p] > eps ? acc[p] / norm[p] : 0;
                dst[p] = (byte)Math.Clamp(Math.Round(d), 0.0, 255.0);
            }

            var outImg = new CalibImage(w, h, 1);
            Marshal.Copy(dst, 0, outImg.GetNativeStruct().data, nPix);
            return outImg;
        }

        private static void ComputeMertensWeights(byte[] gray, int w, int h, double[] weights)
        {
            for (int y = 1; y < h - 1; y++)
            {
                int row = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    int i = row + x;
                    double gx = gray[i + 1] - gray[i - 1];
                    double gy = gray[i + w] - gray[i - w];
                    double c = Math.Abs(gx) + Math.Abs(gy);
                    double well = 1.0 - Math.Abs((gray[i] - 128.0) / 128.0);
                    well = Math.Max(0.05, well);
                    weights[i] = (c + 1.0) * well;
                }
            }

            for (int i = 0; i < w * h; i++)
            {
                if (weights[i] <= 0)
                    weights[i] = 0.05;
            }
        }

        private List<FlowNode> ResolveSendPlcDownstreamChain(FlowNode sendPlc)
        {
            if (_sendPlcDownstreamChainCache != null && _sendPlcDownstreamChainSourceId == sendPlc.Id)
                return _sendPlcDownstreamChainCache;
            _sendPlcDownstreamChainSourceId = sendPlc.Id;
            _sendPlcDownstreamChainCache = GetOrderedDownstreamOfNode(sendPlc);
            return _sendPlcDownstreamChainCache;
        }

        /// <summary>每批 GVAR 写完后执行 GvarSent 等连线下游（如 POU 使能、等待焊接完成）。</summary>
        private void ExecuteSendPlcDownstreamChain(
            FlowNode sendPlc,
            int batchIndex,
            int batchCount,
            int barId,
            int segmentCount)
        {
            var chain = ResolveSendPlcDownstreamChain(sendPlc);
            if (chain.Count == 0)
                return;

            _skipFlowRunNodeIds ??= new HashSet<Guid>();
            foreach (var dn in chain)
                _skipFlowRunNodeIds.Add(dn.Id);

            sendPlc.Outputs["GvarSent"] = true;
            sendPlc.Outputs["BatchIndex"] = batchIndex;
            sendPlc.Outputs["BatchCount"] = batchCount;
            sendPlc.Outputs["BatchBarId"] = barId;
            sendPlc.Outputs["BatchSegmentCount"] = segmentCount;

            AppendLog(
                $"[send_plc] 批次 {batchIndex + 1}/{batchCount} BarId={barId} 段={segmentCount} → 下游 {chain.Count} 节点");
            foreach (var dn in chain)
            {
                AppendLog($"  [send_plc↓] {dn.Def.DisplayName}");
                ExecuteNode(dn);
                if (!string.IsNullOrEmpty(dn.ErrorMessage))
                    throw new InvalidOperationException(
                        $"发送PLC 下游「{dn.Def.DisplayName}」失败(批次 {batchIndex + 1}/{batchCount}): {dn.ErrorMessage}");
            }
        }

        private bool IsFlowOutputPortWired(FlowNode node, string outputPortName)
        {
            foreach (var c in _connections)
            {
                if (!ReferenceEquals(c.FromPort.Owner, node))
                    continue;
                if (string.Equals(c.FromPort.Definition.Name, outputPortName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>主画布或组合子图内该输出端口是否有下游连线。</summary>
        private bool ShouldEmitCalibrationOutput(FlowNode node, string outputPortName, string? compositeInnerFlowBaseDir)
        {
            if (_compositeInnerWiredOutputs != null)
                return _compositeInnerWiredOutputs.Contains((node.Id, outputPortName));
            return IsFlowOutputPortWired(node, outputPortName);
        }

        private static AffineTransform? CoerceAffineTransform(object? value)
        {
            if (value == null)
                return null;
            if (value is AffineTransform a)
                return a;
            try
            {
                return (AffineTransform)value;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>枚举目录内匹配扩展名的图像路径，去重后按文件名排序（OrdinalIgnoreCase）。</summary>
        private static List<string> CollectSortedImagePathsFromDirectory(string resolvedDir, string extensionsSpec)
        {
            if (!System.IO.Directory.Exists(resolvedDir))
                throw new System.IO.DirectoryNotFoundException($"目录不存在: {resolvedDir}");

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in (extensionsSpec ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var tok = raw.Trim();
                if (tok.Length == 0)
                    continue;
                string pattern;
                if (tok.StartsWith("*.", StringComparison.Ordinal))
                    pattern = tok;
                else if (tok.StartsWith(".", StringComparison.Ordinal))
                    pattern = "*" + tok;
                else
                    pattern = "*." + tok;

                foreach (var p in System.IO.Directory.GetFiles(resolvedDir, pattern, System.IO.SearchOption.TopDirectoryOnly))
                    set.Add(System.IO.Path.GetFullPath(p));
            }

            var list = set.ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>解析棋盘格内参标定用的图像路径：分号列表 + 可选目录扫描（默认 Image_ 前缀 .bmp）。</summary>
        private List<string> ResolveChessboardCalibrationImagePaths(FlowNode node, string? compositeInnerFlowBaseDir)
        {
            var resolved = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddPath(string full)
            {
                full = System.IO.Path.GetFullPath(full);
                if (seen.Add(full))
                    resolved.Add(full);
            }

            var rawPaths = node.Params.GetValueOrDefault("imagePaths", "") ?? "";
            foreach (var seg in rawPaths.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = seg.Trim();
                if (string.IsNullOrEmpty(p))
                    continue;
                AddPath(System.IO.Path.IsPathRooted(p)
                    ? p
                    : ResolveCompositeFlowPath(p, compositeInnerFlowBaseDir));
            }

            var dirParam = node.Params.GetValueOrDefault("imageDirectory", "")?.Trim();
            if (!string.IsNullOrEmpty(dirParam))
            {
                var resolvedDir = ResolveCompositeFlowPath(dirParam, compositeInnerFlowBaseDir);
                var extSpec = node.Params.GetValueOrDefault("extensions", ".bmp") ?? ".bmp";
                var prefix = node.Params.GetValueOrDefault("namePrefix", "Image_") ?? "";
                var fromDir = CollectSortedImagePathsFromDirectory(resolvedDir, extSpec);
                if (!string.IsNullOrEmpty(prefix))
                {
                    fromDir = fromDir
                        .Where(p => System.IO.Path.GetFileName(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                foreach (var p in fromDir)
                    AddPath(p);
            }

            return resolved;
        }

        /// <summary>解析棋盘标定 JSON：端口优先，否则 calibrationJsonFile 参数；规范为含 extrinsicsPerView 的整包。</summary>
        private string ResolveChessboardCalibrationJson(FlowNode node, Dictionary<string, object?> inputs, string? compositeInnerFlowBaseDir, bool requireExtrinsics)
        {
            string? raw = null;
            if (inputs.TryGetValue("CalibrationJson", out var cjObj) && cjObj is string cjs && !string.IsNullOrWhiteSpace(cjs))
                raw = cjs;
            else
            {
                var path = node.Params.GetValueOrDefault("calibrationJsonFile", "")?.Trim();
                if (!string.IsNullOrEmpty(path))
                {
                    var resolved = ResolveCompositeFlowPath(path, compositeInnerFlowBaseDir);
                    if (!System.IO.File.Exists(resolved))
                        throw new System.IO.FileNotFoundException($"标定 JSON 文件不存在: {resolved}");
                    raw = System.IO.File.ReadAllText(resolved, Encoding.UTF8);
                }
            }

            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException(
                    "缺少 CalibrationJson：请连接「棋盘格内参标定」的 CalibrationJson 端口（勿接 IntrinsicsJson），或填写参数 calibrationJsonFile。");

            return requireExtrinsics
                ? CalibAPI.NormalizeChessboardCalibrationJson(raw)
                : CalibAPI.NormalizeIntrinsicsCalibrationJson(raw);
        }

        /// <summary>0=board 裁剪；1=local 原图仅板内；2=plane 整图按标定板平面单应。</summary>
        private static int ParsePerspectiveOutputMode(IReadOnlyDictionary<string, string?> paramBag)
        {
            if (!paramBag.TryGetValue("perspectiveOutputFrame", out var raw) || string.IsNullOrWhiteSpace(raw))
                return 0;
            var t = raw.Trim();
            if (string.Equals(t, "plane", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "full_plane", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "homography", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (string.Equals(t, "full", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "local", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "board_local", StringComparison.OrdinalIgnoreCase)
                || t == "1"
                || string.Equals(t, "true", StringComparison.OrdinalIgnoreCase))
                return 1;
            return 0;
        }

        private static int ParsePerspectiveOutputScale(IReadOnlyDictionary<string, string?> paramBag)
        {
            if (!paramBag.TryGetValue("perspectiveOutputScale", out var raw) || string.IsNullOrWhiteSpace(raw))
                return 0;
            var t = raw.Trim();
            return string.Equals(t, "board_pixels", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "pixels", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "image", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
        }

        private static bool ParseAssumeUndistortedForWarp(IReadOnlyDictionary<string, string?> paramBag, bool undistortEnabledInSameNode)
        {
            if (!paramBag.TryGetValue("assumeUndistorted", out var raw) || string.IsNullOrWhiteSpace(raw))
                return undistortEnabledInSameNode;
            var t = raw.Trim();
            if (string.Equals(t, "auto", StringComparison.OrdinalIgnoreCase))
                return undistortEnabledInSameNode;
            return string.Equals(t, "true", StringComparison.OrdinalIgnoreCase)
                || t == "1"
                || string.Equals(t, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ParseFlowBoolParam(IReadOnlyDictionary<string, string?> paramBag, string key, bool defaultValue = false)
        {
            if (!paramBag.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                return defaultValue;
            var t = raw.Trim();
            return string.Equals(t, "true", StringComparison.OrdinalIgnoreCase)
                || t == "1"
                || string.Equals(t, "yes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>标定图像矫正：按参数对图像做内参去畸变与棋盘透视展开。</summary>
        private CalibImage ApplyOptionalCameraCorrection(
            FlowNode node,
            Dictionary<string, object?> inputs,
            CalibImage image,
            string? compositeInnerFlowBaseDir)
        {
            bool undistort = ParseFlowBoolParam(node.Params, "enableUndistort");
            bool perspective = ParseFlowBoolParam(node.Params, "enablePerspective");
            if (!undistort && !perspective)
                return image;

            double alpha = double.TryParse(node.Params.GetValueOrDefault("undistortAlpha"), out double av) ? av : -1.0;
            CalibImage current = image;
            if (undistort)
            {
                string calJsonU = ResolveChessboardCalibrationJson(node, inputs, compositeInnerFlowBaseDir, requireExtrinsics: false);
                current = CalibAPI.UndistortImage(current, calJsonU, alpha);
                CalibAPI.EnsureNativeImageChannels(current);
            }

            if (perspective)
            {
                string calJsonP = ResolveChessboardCalibrationJson(node, inputs, compositeInnerFlowBaseDir, requireExtrinsics: true);
                int viewIdx = int.TryParse(node.Params.GetValueOrDefault("viewIndex"), out int vi) ? vi : 0;
                int cols = int.TryParse(node.Params.GetValueOrDefault("cols"), out int cc) ? cc : 9;
                int rows = int.TryParse(node.Params.GetValueOrDefault("rows"), out int rr) ? rr : 6;
                double sqMm = double.TryParse(node.Params.GetValueOrDefault("squareSizeMm"), out double sqv) ? sqv : 25.0;
                double pxPerMm = double.TryParse(node.Params.GetValueOrDefault("pxPerMm"), out double ppm) ? ppm : 1.0;
                int perspMode = ParsePerspectiveOutputMode(node.Params);
                int outScale = ParsePerspectiveOutputScale(node.Params);
                bool assumeUnd = ParseAssumeUndistortedForWarp(node.Params, undistort);
                current = CalibAPI.WarpToChessboardPlane(current, calJsonP, viewIdx, cols, rows, sqMm, pxPerMm, perspMode, assumeUnd, outScale);
                CalibAPI.EnsureNativeImageChannels(current);
            }

            return current;
        }

        /// <summary>解析「加载图像目录」的根路径；相对路径相对 flow 文件目录。目录为空时弹出选文件夹；取消则返回 false。</summary>
        private bool TryResolveLoadImageDirectory(FlowNode node, string? compositeInnerFlowBaseDir, out string resolvedDir)
        {
            resolvedDir = "";
            string configuredDir = node.Params.GetValueOrDefault("directory", "")?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(configuredDir))
            {
                resolvedDir = ResolveCompositeFlowPath(configuredDir, compositeInnerFlowBaseDir);
                return true;
            }

            using var fbd = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择图像所在目录",
                UseDescriptionForTitle = true
            };
            if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return false;
            resolvedDir = System.IO.Path.GetFullPath(fbd.SelectedPath);
            node.Params["directory"] = FormatPathForFlowParam(resolvedDir, compositeInnerFlowBaseDir);
            return true;
        }

        private void ExecuteNode(
            FlowNode node,
            Dictionary<string, object?>? explicitInputs = null,
            Dictionary<string, object?>? compositeExternalInputsForBindIn = null,
            string? compositeInnerFlowBaseDir = null,
            bool halconGateEntered = false)
        {
#if HALCON_ENABLED
            if (!halconGateEntered && IsHalconFlowNode(node))
            {
                HalconComputeRunner.Run(
                    () => ExecuteNode(node, explicitInputs, compositeExternalInputsForBindIn, compositeInnerFlowBaseDir, halconGateEntered: true),
                    ResolveHalconThreadPolicy(node));
                return;
            }
#endif

            var inputs = explicitInputs ?? GetNodeInputs(node);
            node.Outputs.Clear();
            node.ErrorMessage = null;
            node.ResultSummary = null;
            SetNodeStatus(node, true);

            if (!IsNodeEnabled(node))
            {
                throw new FlowExecutionGracefulStopException($"节点已禁用，后续已停止: {node.Def.DisplayName}");
            }

            try
            {
                string? flowBaseDir = GetFlowBaseDirectory(compositeInnerFlowBaseDir);
                switch (node.Def.TypeId)
                {
                    case "load_image":
                    {
                        string configuredPath = node.Params.GetValueOrDefault("filePath", "")?.Trim() ?? "";
                        string resolvedPath = string.IsNullOrWhiteSpace(configuredPath)
                            ? ""
                            : ResolveCompositeFlowPath(configuredPath, compositeInnerFlowBaseDir);

                        CalibImage? loaded = null;
                        if (!string.IsNullOrWhiteSpace(resolvedPath))
                        {
                            if (!System.IO.File.Exists(resolvedPath))
                                throw new System.IO.FileNotFoundException($"加载图像失败，文件不存在: {resolvedPath}");
                            loaded = CalibAPI.LoadImage(resolvedPath);
                        }
                        else
                        {
                            var dlg = new OpenFileDialog
                            {
                                Filter = "图像文件|*.bmp;*.png;*.jpg;*.tif|所有文件|*.*",
                                Title = "选择图像文件"
                            };
                            if (dlg.ShowDialog() == true)
                            {
                                string storedPath = FormatPathForFlowParam(dlg.FileName, compositeInnerFlowBaseDir);
                                node.Params["filePath"] = storedPath;
                                loaded = CalibAPI.LoadImage(dlg.FileName);
                            }
                            else
                            {
                                node.ErrorMessage = "用户取消";
                                break;
                            }
                        }

                        RememberRecentFullFrameForHalconFine(loaded);
                        node.Outputs["Image"] = loaded;
                        if (inputs.TryGetValue("After", out var afterLoad))
                            node.Outputs["Out"] = afterLoad;
                        else if (loaded != null)
                            node.Outputs["Out"] = loaded;
                        string fileLabel = !string.IsNullOrWhiteSpace(resolvedPath)
                            ? System.IO.Path.GetFileName(resolvedPath)
                            : System.IO.Path.GetFileName(node.Params.GetValueOrDefault("filePath", "") ?? "");
                        node.ResultSummary = inputs.ContainsKey("After")
                            ? $"Loaded {fileLabel}（After 上游已执行）"
                            : $"Loaded {fileLabel}";
                        break;
                    }

                    case "load_image_dir":
                    {
                        if (!TryResolveLoadImageDirectory(node, compositeInnerFlowBaseDir, out var resolvedDir))
                        {
                            node.ErrorMessage = "用户取消";
                            break;
                        }

                        string extSpec = node.Params.GetValueOrDefault("extensions", ".bmp;.png;.jpg;.jpeg;.tif;.tiff")
                                         ?? ".bmp;.png;.jpg;.jpeg;.tif;.tiff";
                        int idx = int.TryParse(
                            node.Params.GetValueOrDefault("index"),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var ix)
                            ? ix
                            : 0;
                        var paths = CollectSortedImagePathsFromDirectory(resolvedDir, extSpec);
                        if (paths.Count == 0)
                        {
                            throw new InvalidOperationException(
                                $"加载图像目录: 目录内无匹配图像 ({resolvedDir})，扩展名: {extSpec}");
                        }

                        if (idx < 0)
                            idx = 0;
                        else if (idx >= paths.Count)
                            idx = paths.Count - 1;

                        var img = CalibAPI.LoadImage(paths[idx]);
                        node.Outputs["Image"] = img;
                        node.Outputs["Count"] = paths.Count;
                        node.Outputs["Path"] = paths[idx];
                        node.ResultSummary = $"{paths.Count} 张 · #{idx} {System.IO.Path.GetFileName(paths[idx])}";
                        break;
                    }

                    case "jit_sample":
                    {
                        string py = node.Params.GetValueOrDefault("pythonPath", "python") ?? "python";
                        string launcherRel = node.Params.GetValueOrDefault("launcherScript", JitSampleBridge.DefaultLauncherRepoRelative)
                                             ?? JitSampleBridge.DefaultLauncherRepoRelative;
                        string launcherAbs = SamOnnxSegmentation.ResolveModelPath(launcherRel, flowBaseDir);
                        string repoRoot = node.Params.GetValueOrDefault("jitRepoRoot", "")?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(repoRoot))
                            throw new InvalidOperationException("JiT采样: 请填写 jitRepoRoot（just-image-transformer 克隆路径）");
                        string repoRootAbs = JitSampleBridge.ResolveExistingDirectory(repoRoot, flowBaseDir);
                        string cfgYaml = node.Params.GetValueOrDefault("configYaml", JitSampleBridge.DefaultConfigYamlRelative)
                                         ?? JitSampleBridge.DefaultConfigYamlRelative;
                        string ckRel = node.Params.GetValueOrDefault("checkpointPath", "")?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(ckRel))
                            throw new InvalidOperationException("JiT采样: 请填写 checkpointPath（model.npz 或含 model.npz 的 .zip）");
                        string ckAbs = SamOnnxSegmentation.ResolveModelPath(ckRel, flowBaseDir);
                        int seed = int.TryParse(node.Params.GetValueOrDefault("seed"), out var sd) ? sd : 555;
                        int label = int.TryParse(node.Params.GetValueOrDefault("label"), out var lb) ? lb : 123;
                        double cfgS = double.TryParse(
                            node.Params.GetValueOrDefault("cfgStrength"),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var cfgVal)
                            ? cfgVal
                            : 3.0;
                        int steps = int.TryParse(node.Params.GetValueOrDefault("numSteps"), out var st) ? st : 50;
                        string schedule = node.Params.GetValueOrDefault("schedule", "linear") ?? "linear";
                        int timeoutSec = int.TryParse(node.Params.GetValueOrDefault("timeoutSec"), out var ts) ? ts : 3600;
                        timeoutSec = Math.Max(120, timeoutSec);
                        string tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "calibrate_jit_" + Guid.NewGuid().ToString("N"));
                        System.IO.Directory.CreateDirectory(tmpDir);
                        string outPng = System.IO.Path.Combine(tmpDir, "jit_out.png");
                        try
                        {
                            var img = JitSampleBridge.Run(
                                launcherAbs,
                                repoRootAbs,
                                py.Trim(),
                                cfgYaml.Trim(),
                                ckAbs,
                                outPng,
                                seed,
                                label,
                                cfgS,
                                steps,
                                schedule.Trim(),
                                timeoutSec * 1000);
                            node.Outputs["Image"] = img;
                        }
                        finally
                        {
                            try
                            {
                                if (System.IO.Directory.Exists(tmpDir))
                                    System.IO.Directory.Delete(tmpDir, recursive: true);
                            }
                            catch
                            {
                                // ignored
                            }
                        }

                        break;
                    }

                    case "camera_snap":
                    {
                        int deviceIndex = int.TryParse(node.Params.GetValueOrDefault("deviceIndex"), out var di) ? di : 0;
                        int targetWidth = int.TryParse(node.Params.GetValueOrDefault("targetWidth"), out var tw) ? tw : 0;
                        int targetHeight = int.TryParse(node.Params.GetValueOrDefault("targetHeight"), out var th) ? th : 0;
                        var img = GrabOneCameraFrameOrThrow(deviceIndex, targetWidth, targetHeight);
                        RememberRecentFullFrameForHalconFine(img);
                        node.Outputs["Image"] = img;
                        if (inputs.TryGetValue("After", out var afterSnap))
                            node.Outputs["Out"] = afterSnap;
                        node.ResultSummary = inputs.ContainsKey("After")
                            ? $"Snap OK: dev={deviceIndex}（After 上游已执行）"
                            : $"Snap OK: dev={deviceIndex}";
                        break;
                    }

                    case "camera_loop":
                    {
                        int deviceIndex = int.TryParse(node.Params.GetValueOrDefault("deviceIndex"), out var di) ? di : 0;
                        string mode = (node.Params.GetValueOrDefault("mode", "last_only") ?? "last_only").Trim().ToLowerInvariant();
                        int frameCount = int.TryParse(node.Params.GetValueOrDefault("frameCount"), out var fc) ? fc : 10;
                        int intervalMs = int.TryParse(node.Params.GetValueOrDefault("intervalMs"), out var im) ? im : 100;
                        int targetWidth = int.TryParse(node.Params.GetValueOrDefault("targetWidth"), out var tw) ? tw : 0;
                        int targetHeight = int.TryParse(node.Params.GetValueOrDefault("targetHeight"), out var th) ? th : 0;
                        var loopResult = GrabLoopCameraFramesOrThrow(deviceIndex, frameCount, intervalMs, targetWidth, targetHeight);
                        node.Outputs["Image"] = loopResult.LastImage;
                        node.Outputs["Count"] = loopResult.Count;
                        node.ResultSummary = mode == "per_frame"
                            ? $"Loop(per_frame): captured {loopResult.Count}/{Math.Max(1, frameCount)} (single-node fallback=last frame)"
                            : $"Loop OK: {loopResult.Count}/{Math.Max(1, frameCount)}";
                        break;
                    }

                    case "flow_sink":
                    {
                        bool acceptNull = string.Equals(
                            node.Params.GetValueOrDefault("acceptNull", "false")?.Trim(),
                            "true",
                            StringComparison.OrdinalIgnoreCase)
                            || node.Params.GetValueOrDefault("acceptNull", "false")?.Trim() == "1";

                        if (!FlowLoopSinkAccumulateActive)
                            node.SinkAccumulator = new List<object?>();
                        else
                            node.SinkAccumulator ??= new List<object?>();

                        var acc = node.SinkAccumulator!;
                        inputs.TryGetValue("In", out var inVal);
                        if (inVal != null || acceptNull)
                            acc.Add(inVal);

                        var imageList = new List<CalibImage>();
                        foreach (var item in acc)
                        {
                            if (item is CalibImage ci)
                                imageList.Add(ci);
                        }

                        bool allImageOrNull = acc.All(item => item == null || item is CalibImage);
                        node.Outputs["List"] = allImageOrNull
                            ? imageList
                            : new List<object?>(acc);

                        var collectedPoints = new List<Point2D[]>();
                        foreach (var item in acc)
                        {
                            switch (item)
                            {
                                case Point2D[] pts when pts.Length > 0:
                                    collectedPoints.Add(pts);
                                    break;
                                case CalibPoint3D[] p3 when p3.Length > 0:
                                    collectedPoints.Add(p3.Select(p => new Point2D(p.X, p.Y)).ToArray());
                                    break;
                            }
                        }

                        node.Outputs["PointsList"] = collectedPoints.Count > 0 ? collectedPoints : null;
                        node.Outputs["Count"] = acc.Count;
                        if (inputs.TryGetValue("After", out var afterSink))
                            node.Outputs["Out"] = afterSink;
                        else if (acc.Count > 0)
                            node.Outputs["Out"] = acc[^1];

                        node.ResultSummary = FlowLoopSinkAccumulateActive
                            ? collectedPoints.Count > 0
                                ? $"收集 {acc.Count} 项（点列 {collectedPoints.Count} · 图 {imageList.Count}）"
                                : $"收集 {acc.Count} 项（图 {imageList.Count}）"
                            : collectedPoints.Count > 0
                                ? $"收集 {acc.Count} 项（点列 {collectedPoints.Count}）"
                                : $"收集 {acc.Count} 项（单轮试跑）";
                        break;
                    }

                    case "points_sink":
                    {
                        bool acceptEmpty = string.Equals(
                            node.Params.GetValueOrDefault("acceptEmpty", "true")?.Trim(),
                            "true",
                            StringComparison.OrdinalIgnoreCase)
                            || node.Params.GetValueOrDefault("acceptEmpty", "true")?.Trim() == "1";
                        string groupIdMode = node.Params.GetValueOrDefault("groupIdMode", "round") ?? "round";

                        if (!FlowLoopSinkAccumulateActive)
                            node.PointsSinkAccumulator = new PointsSinkRoundAccumulator();
                        else
                            node.PointsSinkAccumulator ??= new PointsSinkRoundAccumulator();

                        var acc = node.PointsSinkAccumulator!;
                        var pts = inputs.TryGetValue("Points", out var pObj) ? pObj as Point2D[] : null;
                        pts ??= Array.Empty<Point2D>();

                        int[]? barIn = null;
                        if (inputs.TryGetValue("GroupBarIds", out var gbObj) && gbObj is int[] gb && gb.Length == pts.Length)
                            barIn = gb;
                        else if (inputs.TryGetValue("BarIds", out var bObj) && bObj is int[] ba && ba.Length == pts.Length)
                            barIn = ba;

                        if (pts.Length > 0 || acceptEmpty)
                            AppendPointsSinkRound(acc, pts, barIn, groupIdMode, FlowLoopSinkAccumulateActive);

                        ApplyPointsSinkOutputs(node, acc, inputs);
                        if (!FlowLoopSinkAccumulateActive)
                            node.ResultSummary += "（单轮试跑）";
                        break;
                    }

                    case "exposure_fusion":
                    {
                        inputs.TryGetValue("Images", out var imagesObj);
                        var images = CoerceToCalibImageList(imagesObj, "Exposure Fusion");
                        if (images.Count == 0)
                            throw new InvalidOperationException("Exposure Fusion: 缺少 Images 或列表为空");

                        string mode = node.Params.GetValueOrDefault("mode", "mertens") ?? "mertens";
                        var fused = FuseExposureCalibImages(images, mode);
                        node.Outputs["Image"] = fused;
                        node.ResultSummary = $"{mode} x{images.Count} → {fused.GetNativeStruct().width}x{fused.GetNativeStruct().height}";
                        break;
                    }

                    case "list_pick":
                    {
                        var listPortMaps = BuildListPickListPortMaps(node.Params);
                        int pickIndex = ResolveListPickIndex(inputs, node.Params);
                        ApplyListPickOutputs(node, inputs, pickIndex, listPortMaps);
                        int count = node.Outputs.TryGetValue("Count", out var cObj) && cObj is int ci ? ci : 0;
                        var picked = listPortMaps
                            .Where(m => node.Outputs.ContainsKey(m.OutputPort))
                            .Select(m => m.OutputPort)
                            .ToList();
                        node.ResultSummary = count == 0
                            ? "empty list"
                            : picked.Count == 0
                                ? $"idx={pickIndex}/{count - 1}"
                                : $"idx={pickIndex}/{count - 1} → {string.Join(", ", picked)}";
                        break;
                    }

                    case "flow_loop":
                    {
                        var listPortMaps = BuildFlowLoopListPortMaps(node.Params);
                        ResolveFlowLoopSchedule(
                            node.Params.GetValueOrDefault("count"),
                            node.Params.GetValueOrDefault("stepValues"),
                            out var repeatCount,
                            out var infinite,
                            out var stepValues);
                        var listLengths = CollectFlowLoopListLengths(inputs, listPortMaps);
                        var activeListLengths = listLengths.Where(kv => kv.Value > 0).ToList();
                        if (activeListLengths.Count > 0)
                        {
                            int firstLen = activeListLengths[0].Value;
                            bool sameLen = activeListLengths.All(kv => kv.Value == firstLen);
                            if (!sameLen)
                            {
                                string detail = string.Join(", ", activeListLengths.Select(kv => $"{kv.Key}={kv.Value}"));
                                throw new InvalidOperationException($"循环: 多路列表长度不一致（非空端口：{detail}）");
                            }

                            repeatCount = firstLen;
                            infinite = false;
                            if (stepValues != null && stepValues.Length > 0 && stepValues.Length != repeatCount)
                                throw new InvalidOperationException(
                                    $"循环: stepValues 长度({stepValues.Length})与列表长度({repeatCount})不一致");
                        }
                        else if (listLengths.Count > 0)
                        {
                            // 多路列表均为空时按 0 轮处理，避免把空列表与有数据列表混接时误报。
                            repeatCount = 0;
                            infinite = false;
                        }

                        if (inputs.TryGetValue("After", out var afterObj))
                            node.Outputs["Out"] = afterObj;
                        ApplyFlowLoopListOutputs(node, inputs, 0, listPortMaps);
                        node.Outputs["Index"] = 0;
                        node.Outputs["Count"] = infinite ? -1 : repeatCount;
                        if (stepValues != null && stepValues.Length > 0)
                            node.Outputs["StepValue"] = stepValues[0];
                        node.ResultSummary = infinite
                            ? "Loop ∞（单节点试跑；全流程「运行」将无限重复下游，点「停止」结束）"
                            : activeListLengths.Count > 0 || listLengths.Count > 0
                                ? $"Loop x{repeatCount}（多路列表展开，试跑输出第 1 轮）"
                            : stepValues != null && stepValues.Length > 0
                                ? $"Loop x{repeatCount}（StepValue 列表，试跑输出第 1 项={stepValues[0]:G}）"
                                : $"Loop x{repeatCount}（单节点试跑；全流程「运行」才会重复执行下游 {repeatCount} 次）";
                        break;
                    }

                    case "world_coords":
                    {
                        string raw = node.Params.GetValueOrDefault("points", "") ?? "";
                        var coords = ParseWorldPointsParam(raw);
                        node.Outputs["Points"] = coords;
                        node.ResultSummary = $"Points: {coords.Length}";
                        break;
                    }

                    case "weld_trajectory_world":
                    {
                        string pattern = node.Params.GetValueOrDefault("pattern", "九宫格 (3×3)") ?? "九宫格 (3×3)";
                        double cx = double.TryParse(node.Params.GetValueOrDefault("centerX"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tcx) ? tcx : 0.0;
                        double cy = double.TryParse(node.Params.GetValueOrDefault("centerY"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tcy) ? tcy : 0.0;
                        double cz = double.TryParse(node.Params.GetValueOrDefault("centerZ"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tcz) ? tcz : 0.0;
                        double step = double.TryParse(node.Params.GetValueOrDefault("stepMm"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ts) ? ts : 10.0;
                        double stepX = double.TryParse(node.Params.GetValueOrDefault("stepXmm"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tsx) ? tsx : 0.0;
                        double stepY = double.TryParse(node.Params.GetValueOrDefault("stepYmm"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tsy) ? tsy : 0.0;
                        double arm = double.TryParse(node.Params.GetValueOrDefault("armMm"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ta) ? ta : 50.0;
                        double legX = double.TryParse(node.Params.GetValueOrDefault("legXmm"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lx) ? lx : 50.0;
                        double legY = double.TryParse(node.Params.GetValueOrDefault("legYmm"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ly) ? ly : 50.0;
                        double ang = double.TryParse(node.Params.GetValueOrDefault("angleDeg"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ad) ? ad : 0.0;
                        int gcols = int.TryParse(node.Params.GetValueOrDefault("gridCols"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var gc) ? gc : 3;
                        int grows = int.TryParse(node.Params.GetValueOrDefault("gridRows"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var gr) ? gr : 3;
                        int samp = int.TryParse(node.Params.GetValueOrDefault("samplesPerSegment"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var sp) ? sp : 1;

                        var coords = GenerateWeldTrajectoryWorld(pattern, cx, cy, step, stepX, stepY, arm, legX, legY, ang, gcols, grows, samp);
                        var coords3 = GenerateWeldTrajectoryWorld3D(pattern, cx, cy, cz, step, stepX, stepY, arm, legX, legY, ang, gcols, grows, samp);
                        node.Outputs["Points"] = coords;
                        node.Outputs["Points3D"] = coords3;
                        var (effSx, effSy) = ResolveWeldTrajectorySteps(step, stepX, stepY);
                        node.ResultSummary = $"{pattern.Trim()}: {coords.Length} pts (center {cx:G},{cy:G},{cz:G} mm, ΔX={effSx:G} ΔY={effSy:G})";
                        break;
                    }

                    case "grayscale":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("灰度化: 缺少输入图像");
                        using var detector = new TrajectoryStepDetector();
                        detector.ConvertToGrayscale(srcImg);
                        var gray = detector.GetStepImage(1);
                        node.Outputs["Out"] = gray;
                        break;
                    }

                    case "image_flip":
                    {
                        var srcImg = TryResolveCalibImageInput(inputs)
                            ?? throw new InvalidOperationException("图像翻转: 缺少输入图像");
                        string flipMode = node.Params.GetValueOrDefault("flipMode", "horizontal") ?? "horizontal";
                        var flipped = CalibImageTransform.Flip(srcImg, flipMode);
                        PublishCalibImageOutputs(node, flipped);
                        node.ResultSummary = $"flip {flipMode.Trim()} → {flipped.Width}x{flipped.Height}";
                        break;
                    }

                    case "image_rotate":
                    {
                        var srcImg = TryResolveCalibImageInput(inputs)
                            ?? throw new InvalidOperationException("图像旋转: 缺少输入图像");
                        double angleDeg = CalibImageTransform.ParseAngleDegrees(node.Params.GetValueOrDefault("angleDeg"), 90);
                        string expandRaw = node.Params.GetValueOrDefault("expandCanvas", "true") ?? "true";
                        bool expand = !string.Equals(expandRaw.Trim(), "false", StringComparison.OrdinalIgnoreCase) && expandRaw.Trim() != "0";
                        var rotated = CalibImageTransform.Rotate(srcImg, angleDeg, expand);
                        PublishCalibImageOutputs(node, rotated);
                        node.ResultSummary = $"rotate CW {angleDeg:G}° → {rotated.Width}x{rotated.Height}";
                        break;
                    }

                    case "image_resize":
                    {
                        var srcImg = TryResolveCalibImageInput(inputs)
                            ?? throw new InvalidOperationException("图像缩放: 缺少输入图像");
                        string mode = node.Params.GetValueOrDefault("mode", "factor") ?? "factor";
                        double scale = CalibImageTransform.ParseScale(node.Params.GetValueOrDefault("scale"), 1.0);
                        int tw = CalibImageTransform.ParsePositiveOrZeroInt(node.Params.GetValueOrDefault("width"));
                        int th = CalibImageTransform.ParsePositiveOrZeroInt(node.Params.GetValueOrDefault("height"));
                        int maxSide = CalibImageTransform.ParsePositiveOrZeroInt(node.Params.GetValueOrDefault("maxSide"));
                        string keepRaw = node.Params.GetValueOrDefault("keepAspect", "true") ?? "true";
                        bool keepAspect = !string.Equals(keepRaw.Trim(), "false", StringComparison.OrdinalIgnoreCase) && keepRaw.Trim() != "0";
                        string interp = node.Params.GetValueOrDefault("interpolation", "linear") ?? "linear";
                        var resized = CalibImageTransform.Resize(srcImg, mode, scale, tw, th, maxSide, keepAspect, interp);
                        PublishCalibImageOutputs(node, resized);
                        node.ResultSummary = $"resize {mode} {srcImg.Width}x{srcImg.Height} → {resized.Width}x{resized.Height}";
                        break;
                    }

                    case "clahe":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("CLAHE: 缺少输入图像");
                        double clipLimit = double.Parse(node.Params["clipLimit"]);
                        int tileSize = int.Parse(node.Params["tileSize"]);
                        using var detector = new TrajectoryStepDetector();
                        detector.ConvertToGrayscale(srcImg);
                        detector.ApplyCLAHE(clipLimit, tileSize);
                        var claheImg = detector.GetStepImage(1);
                        node.Outputs["Out"] = claheImg;
                        break;
                    }

                    case "binarize":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("二值化: 缺少输入图像");
                        int blurSize = int.Parse(node.Params["blurSize"]);
                        int morphSize = int.Parse(node.Params["morphSize"]);
                        using var detector = new TrajectoryStepDetector();
                        detector.ConvertToGrayscale(srcImg);
                        detector.PreprocessAndFindContours(blurSize, morphSize, false);
                        var binImg = detector.GetStepImage(2);
                        node.Outputs["Out"] = binImg;
                        break;
                    }

                    case "gray_range_binary":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("灰度范围二值化: 缺少输入图像");
                        string rangeMode = (node.Params.GetValueOrDefault("rangeMode", "fixed") ?? "fixed").Trim().ToLowerInvariant();
                        if (rangeMode == "percentile")
                        {
                            double exL = double.TryParse(
                                node.Params.GetValueOrDefault("percentileExcludeLow"),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var pl)
                                ? pl
                                : 10.0;
                            double exH = double.TryParse(
                                node.Params.GetValueOrDefault("percentileExcludeHigh"),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var ph)
                                ? ph
                                : 10.0;
                            var grayBinImg = GrayRangeBinaryPercentile(srcImg, exL, exH, out var usedLo, out var usedHi);
                            node.Outputs["Out"] = grayBinImg;
                            node.ResultSummary = $"gray_bin pct −{exL:0.#}%/−{exH:0.#}% → [{usedLo},{usedHi}]";
                        }
                        else
                        {
                            int grayLow = int.TryParse(node.Params.GetValueOrDefault("grayLow"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var gl) ? gl : 5;
                            int grayHigh = int.TryParse(node.Params.GetValueOrDefault("grayHigh"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var gh) ? gh : 50;
                            grayLow = Math.Clamp(grayLow, 0, 255);
                            grayHigh = Math.Clamp(grayHigh, 0, 255);
                            if (grayLow > grayHigh)
                                (grayLow, grayHigh) = (grayHigh, grayLow);
                            using var detector = new TrajectoryStepDetector();
                            detector.ConvertToGrayscale(srcImg);
                            detector.GrayRangeBinary(grayLow, grayHigh);
                            var grayBinImg = detector.GetStepImage(2);
                            node.Outputs["Out"] = grayBinImg;
                            node.ResultSummary = $"gray_bin [{grayLow},{grayHigh}]";
                        }
                        break;
                    }

                    case "binary_merge":
                    {
                        var imgA = inputs["InA"] as CalibImage;
                        var imgB = inputs["InB"] as CalibImage;
                        if (imgA == null || imgB == null) throw new InvalidOperationException("二值图合并: 需要 InA、InB");
                        string mergeMode = (node.Params.GetValueOrDefault("mergeMode", "or") ?? "or").Trim();
                        int fgTh = int.TryParse(node.Params.GetValueOrDefault("foregroundThreshold"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ft) ? ft : 0;
                        var merged = BinaryMergeCalibImages(imgA, imgB, mergeMode, fgTh);
                        node.Outputs["Out"] = merged;
                        node.ResultSummary = $"binary_merge {mergeMode}";
                        break;
                    }

                    case "binary_morph_rect":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("矩形形态学: 缺少输入图像");
                        int fgTh = int.TryParse(
                            node.Params.GetValueOrDefault("foregroundThreshold"),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var ft)
                            ? ft
                            : 0;
                        string op = (node.Params.GetValueOrDefault("op", "open") ?? "open").Trim();
                        int iterations = int.TryParse(
                            node.Params.GetValueOrDefault("iterations"),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var it)
                            ? Math.Max(1, it)
                            : 1;
                        string orient = (node.Params.GetValueOrDefault("orientation", "horizontal_strips") ?? "horizontal_strips").Trim().ToLowerInvariant();
                        bool customKw = int.TryParse(node.Params.GetValueOrDefault("kernelW"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var kwParsed) && kwParsed > 0;
                        bool customKh = int.TryParse(node.Params.GetValueOrDefault("kernelH"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var khParsed) && khParsed > 0;
                        int kw, kh;
                        if (customKw || customKh)
                        {
                            kw = customKw ? kwParsed : 31;
                            kh = customKh ? khParsed : 5;
                        }
                        else if (orient.Contains("vertical"))
                        {
                            kw = 5;
                            kh = 31;
                        }
                        else
                        {
                            kw = 31;
                            kh = 5;
                        }

                        EnsureOddKernel(ref kw);
                        EnsureOddKernel(ref kh);
                        var bin = CalibImageToBinary(inImg, fgTh);
                        for (int i = 0; i < iterations; i++)
                            bin = ApplyBinaryMorphRectOp(bin, op, kw, kh);
                        int minComp = int.TryParse(
                            node.Params.GetValueOrDefault("minComponentPixels"),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var mc)
                            ? mc
                            : 0;
                        if (minComp > 1)
                            bin = RemoveSmallComponents(bin, minComp);
                        node.Outputs["Out"] = BinaryToCalibImage(bin);
                        node.ResultSummary = $"{op} rect {kw}x{kh} x{iterations}";
                        break;
                    }

                    case "gray_erode_rect":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("灰度腐蚀: 缺少输入图像");
                        int kw = int.TryParse(node.Params.GetValueOrDefault("kernelW"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var kwp) ? kwp : 5;
                        int kh = int.TryParse(node.Params.GetValueOrDefault("kernelH"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var khp) ? khp : 5;
                        EnsureOddKernel(ref kw);
                        EnsureOddKernel(ref kh);
                        int iterations = int.TryParse(node.Params.GetValueOrDefault("iterations"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var it) ? Math.Max(1, it) : 1;
                        CalibImage cur = inImg;
                        for (int i = 0; i < iterations; i++)
                        {
                            var next = GrayMorphRectCalibImage(cur, dilate: false, kh, kw);
                            if (i > 0)
                                cur.Dispose();
                            cur = next;
                        }
                        node.Outputs["Out"] = cur;
                        node.ResultSummary = $"gray_erode {kw}x{kh} x{iterations}";
                        break;
                    }

                    case "gray_dilate_rect":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("灰度膨胀: 缺少输入图像");
                        int kw = int.TryParse(node.Params.GetValueOrDefault("kernelW"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var kwp2) ? kwp2 : 5;
                        int kh = int.TryParse(node.Params.GetValueOrDefault("kernelH"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var khp2) ? khp2 : 5;
                        EnsureOddKernel(ref kw);
                        EnsureOddKernel(ref kh);
                        int iterations = int.TryParse(node.Params.GetValueOrDefault("iterations"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var it2) ? Math.Max(1, it2) : 1;
                        CalibImage cur = inImg;
                        for (int i = 0; i < iterations; i++)
                        {
                            var next = GrayMorphRectCalibImage(cur, dilate: true, kh, kw);
                            if (i > 0)
                                cur.Dispose();
                            cur = next;
                        }
                        node.Outputs["Out"] = cur;
                        node.ResultSummary = $"gray_dilate {kw}x{kh} x{iterations}";
                        break;
                    }

                    case "gray_blend_ratio":
                    {
                        var imgA = inputs["InA"] as CalibImage;
                        var imgB = inputs["InB"] as CalibImage;
                        if (imgA == null || imgB == null) throw new InvalidOperationException("灰度合并: 需要 InA、InB");
                        string blendMode = (node.Params.GetValueOrDefault("grayBlendMode", "weighted") ?? "weighted").Trim();
                        double ratioA = double.TryParse(node.Params.GetValueOrDefault("ratioA"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ra) ? ra : 0.5;
                        ratioA = Math.Clamp(ratioA, 0.0, 1.0);
                        var blended = GrayMergeCalibImages(imgA, imgB, blendMode, ratioA);
                        node.Outputs["Out"] = blended;
                        node.ResultSummary = $"gray_merge {NormalizeGrayBlendMode(blendMode)} ratioA={ratioA:G4}";
                        break;
                    }

                    case "canny":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("Canny: 缺少输入图像");
                        using var detector = new TrajectoryStepDetector();
                        detector.ConvertToGrayscale(srcImg);
                        detector.CannyEdges(0, 0, 0); // 全部自适应
                        var edgeImg = detector.GetStepImage(6);
                        node.Outputs["Edge"] = edgeImg;
                        break;
                    }

                    case "sobel":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("Sobel: 缺少输入图像");
                        int threshold = int.TryParse(node.Params.GetValueOrDefault("threshold"), out var th) ? th : 48;
                        var edgeImg = SobelEdgeImage(srcImg, threshold);
                        node.Outputs["Edge"] = edgeImg;
                        break;
                    }

                    case "scharr":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("Scharr: 缺少输入图像");
                        int threshold = int.TryParse(node.Params.GetValueOrDefault("threshold"), out var th) ? th : 48;
                        var edgeImg = ScharrEdgeImage(srcImg, threshold);
                        node.Outputs["Edge"] = edgeImg;
                        break;
                    }

                    case "phase_congruency":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("PhaseCongruency: 缺少输入图像");
                        double threshold = double.TryParse(node.Params.GetValueOrDefault("threshold"), out var t) ? t : 0.45;
                        double noiseSigma = double.TryParse(node.Params.GetValueOrDefault("noiseSigma"), out var ns) ? ns : 0.12;
                        int blurKsize = int.TryParse(node.Params.GetValueOrDefault("blurKsize"), out var bk) ? bk : 3;
                        string debugDumpPrefix = node.Params.GetValueOrDefault("debugDumpPrefix", "") ?? "";
                        if (!string.IsNullOrWhiteSpace(debugDumpPrefix))
                            debugDumpPrefix = ResolveCompositeFlowPath(debugDumpPrefix.Trim(), compositeInnerFlowBaseDir);
                        var resultImgs = PhaseCongruencyEdgeImage(srcImg, threshold, noiseSigma, blurKsize, debugDumpPrefix);
                        node.Outputs["Response"] = resultImgs.Response;
                        var edgeImg = resultImgs.Edge;
                        node.Outputs["Edge"] = edgeImg;
                        var stats = AnalyzeBinaryImage(edgeImg);
                        node.ResultSummary = $"PC->Edge: {stats.WhitePixels} ({stats.WhiteRatio:P2})";
                        string phaseLog = $"[phase_congruency] th={threshold:F3}, noise={noiseSigma:F3}, blur={blurKsize}, white={stats.WhitePixels} ({stats.WhiteRatio:P2}), debug='{debugDumpPrefix}'";
                        AppendLog(phaseLog);
                        Console.WriteLine(phaseLog);
                        break;
                    }

                    case "freq_filter_binary":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("频域滤波二值化: 缺少输入图像");
                        string mode = node.Params.GetValueOrDefault("mode", "bandpass") ?? "bandpass";
                        double lowCut = double.TryParse(node.Params.GetValueOrDefault("lowCut"), out var lc) ? lc : 0.06;
                        double highCut = double.TryParse(node.Params.GetValueOrDefault("highCut"), out var hc) ? hc : 0.24;
                        double threshold = double.TryParse(node.Params.GetValueOrDefault("threshold"), out var t2) ? t2 : 0.48;
                        bool useOtsu = bool.TryParse(node.Params.GetValueOrDefault("useOtsu"), out var b2) ? b2 : true;
                        var result = FrequencyFilterToBinaryImage(srcImg, mode, lowCut, highCut, threshold, useOtsu);
                        node.Outputs["Filtered"] = result.Filtered;
                        node.Outputs["Binary"] = result.Binary;
                        var stats = AnalyzeBinaryImage(result.Binary);
                        node.ResultSummary = $"FreqBin: {stats.WhitePixels} ({stats.WhiteRatio:P2})";
                        break;
                    }

                    case "local_freq_sauvola_niblack":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("局部频域阈值: 缺少输入图像");
                        string method = node.Params.GetValueOrDefault("method", "sauvola") ?? "sauvola";
                        int windowSize = int.TryParse(node.Params.GetValueOrDefault("windowSize"), out var ws) ? ws : 25;
                        double k = double.TryParse(node.Params.GetValueOrDefault("k"), out var kk) ? kk : 0.32;
                        double r = double.TryParse(node.Params.GetValueOrDefault("R"), out var rr) ? rr : 0.5;
                        double lowCut = double.TryParse(node.Params.GetValueOrDefault("lowCut"), out var lc) ? lc : 0.04;
                        double highCut = double.TryParse(node.Params.GetValueOrDefault("highCut"), out var hc) ? hc : 0.28;
                        var result = LocalFreqSauvolaNiblackImage(srcImg, method, windowSize, k, r, lowCut, highCut);
                        node.Outputs["Filtered"] = result.Filtered;
                        node.Outputs["Binary"] = result.Binary;
                        var stats = AnalyzeBinaryImage(result.Binary);
                        node.ResultSummary = $"{method}: {stats.WhitePixels} ({stats.WhiteRatio:P2})";
                        break;
                    }

                    case "pre_filter":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("预滤波: 缺少输入图像");
                        string mode = node.Params.GetValueOrDefault("mode", "gaussian") ?? "gaussian";
                        int ksize = int.TryParse(node.Params.GetValueOrDefault("ksize"), out var ks) ? ks : 3;
                        var outImg = PreFilterImage(srcImg, mode, ksize);
                        node.Outputs["Out"] = outImg;
                        break;
                    }

                    case "nlmeans":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("NLMeans: 缺少输入图像");
                        double hStrength = double.TryParse(node.Params.GetValueOrDefault("h"), out var hs) ? hs : 12.0;
                        int searchWindow = int.TryParse(node.Params.GetValueOrDefault("searchWindow"), out var sw) ? sw : 11;
                        int templateWindow = int.TryParse(node.Params.GetValueOrDefault("templateWindow"), out var tw) ? tw : 3;
                        var outImg = NLMeansDenoiseImage(srcImg, hStrength, searchWindow, templateWindow);
                        node.Outputs["Out"] = outImg;
                        break;
                    }

                    case "dip_denoise":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("DIP去噪: 缺少输入图像");
                        string py = node.Params.GetValueOrDefault("pythonPath", "python") ?? "python";
                        string scriptRel = node.Params.GetValueOrDefault("scriptPath", DipDenoiseBridge.DefaultScriptRepoRelative)
                                           ?? DipDenoiseBridge.DefaultScriptRepoRelative;
                        string scriptAbs = SamOnnxSegmentation.ResolveModelPath(scriptRel, flowBaseDir);
                        int iterations = int.TryParse(node.Params.GetValueOrDefault("iterations"), out var it) ? it : 2400;
                        double lr = double.TryParse(
                            node.Params.GetValueOrDefault("learningRate"),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var lrVal)
                            ? lrVal
                            : 0.01;
                        double tv = double.TryParse(
                            node.Params.GetValueOrDefault("tvWeight"),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var tvVal)
                            ? tvVal
                            : 1e-6;
                        int maxSide = int.TryParse(node.Params.GetValueOrDefault("maxSide"), out var ms) ? ms : 0;
                        bool useGpu = string.Equals(
                            (node.Params.GetValueOrDefault("useGpu", "false") ?? "false").Trim(),
                            "true",
                            StringComparison.OrdinalIgnoreCase);
                        int timeoutSec = int.TryParse(node.Params.GetValueOrDefault("timeoutSec"), out var ts) ? ts : 600;
                        timeoutSec = Math.Max(30, timeoutSec);
                        var dipOut = DipDenoiseBridge.Run(
                            srcImg,
                            py.Trim(),
                            scriptAbs,
                            iterations,
                            lr,
                            tv,
                            maxSide,
                            useGpu,
                            timeoutSec * 1000);
                        node.Outputs["Out"] = dipOut;
                        break;
                    }

                    case "swin_transformer":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("SwinTransformer: 缺少输入图像");
                        string py = node.Params.GetValueOrDefault("pythonPath", "python") ?? "python";
                        string scriptRel = node.Params.GetValueOrDefault("scriptPath", SwinTransformerBridge.DefaultScriptRepoRelative)
                                             ?? SwinTransformerBridge.DefaultScriptRepoRelative;
                        string scriptAbs = SamOnnxSegmentation.ResolveModelPath(scriptRel, flowBaseDir);
                        string modelName = node.Params.GetValueOrDefault("modelName", "swin_tiny_patch4_window7_224")
                                           ?? "swin_tiny_patch4_window7_224";
                        bool enableClassification = string.Equals(
                            (node.Params.GetValueOrDefault("enableClassification", "true") ?? "true").Trim(),
                            "true",
                            StringComparison.OrdinalIgnoreCase);
                        bool enableEmbedding = string.Equals(
                            (node.Params.GetValueOrDefault("enableEmbedding", "true") ?? "true").Trim(),
                            "true",
                            StringComparison.OrdinalIgnoreCase);
                        bool enableSegmentHeatmap = string.Equals(
                            (node.Params.GetValueOrDefault("enableSegmentHeatmap", "true") ?? "true").Trim(),
                            "true",
                            StringComparison.OrdinalIgnoreCase);
                        int topK = int.TryParse(node.Params.GetValueOrDefault("topK"), out var tk) ? tk : 5;
                        if (enableClassification)
                            topK = Math.Clamp(topK, 1, 1000);
                        bool useGpu = string.Equals(
                            (node.Params.GetValueOrDefault("useGpu", "false") ?? "false").Trim(),
                            "true",
                            StringComparison.OrdinalIgnoreCase);
                        int timeoutSec = int.TryParse(node.Params.GetValueOrDefault("timeoutSec"), out var swTs) ? swTs : 300;
                        timeoutSec = Math.Max(30, timeoutSec);
                        var sw = SwinTransformerBridge.Run(
                            srcImg,
                            py.Trim(),
                            scriptAbs,
                            modelName.Trim(),
                            topK,
                            useGpu,
                            timeoutSec * 1000,
                            enableClassification,
                            enableEmbedding,
                            enableSegmentHeatmap);
                        node.Outputs["Out"] = sw.Passthrough;
                        node.Outputs["LabelsJson"] = sw.LabelsJson;
                        node.Outputs["EmbeddingJson"] = sw.EmbeddingJson;
                        node.Outputs["SegmentHeatmap"] = sw.SegmentHeatmap;
                        if (enableClassification && !string.IsNullOrEmpty(sw.LabelsJson))
                            node.ResultSummary = sw.LabelsJson.Length > 96 ? sw.LabelsJson.Substring(0, 96) + "…" : sw.LabelsJson;
                        else if (enableEmbedding && sw.EmbeddingJson.Length > 2)
                            node.ResultSummary = sw.EmbeddingJson.Length > 120 ? sw.EmbeddingJson.Substring(0, 120) + "…" : sw.EmbeddingJson;
                        else
                            node.ResultSummary = enableSegmentHeatmap ? "热力图已生成" : "OK";
                        break;
                    }

                    case "yolo_seg_infer":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("YOLO分割推理: 缺少输入图像");
                        string py = node.Params.GetValueOrDefault("pythonPath", "python") ?? "python";
                        string scriptRel = node.Params.GetValueOrDefault("scriptPath", YoloSegInferenceBridge.DefaultScriptRepoRelative)
                                           ?? YoloSegInferenceBridge.DefaultScriptRepoRelative;
                        string scriptAbs = SamOnnxSegmentation.ResolveModelPath(scriptRel, flowBaseDir);
                        string weightsRel = node.Params.GetValueOrDefault(
                                                "weightsPath",
                                                "yolo_data/runs/segment/train-2/weights/best.pt")
                                           ?? "yolo_data/runs/segment/train-2/weights/best.pt";
                        string weightsAbs = SamOnnxSegmentation.ResolveModelPath(weightsRel.Trim(), flowBaseDir);
                        double conf = double.TryParse(
                            node.Params.GetValueOrDefault("conf"),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var cf)
                            ? cf
                            : 0.25;
                        conf = Math.Clamp(conf, 0.01, 1.0);
                        int imgsz = int.TryParse(node.Params.GetValueOrDefault("imgsz"), out var isz) ? isz : 0;
                        imgsz = Math.Max(0, imgsz);
                        bool useGpu = string.Equals(
                            (node.Params.GetValueOrDefault("useGpu", "false") ?? "false").Trim(),
                            "true",
                            StringComparison.OrdinalIgnoreCase);
                        int timeoutSec = int.TryParse(node.Params.GetValueOrDefault("timeoutSec"), out var yTs) ? yTs : 120;
                        timeoutSec = Math.Max(15, timeoutSec);
                        bool visNoBoxes = string.Equals(
                            (node.Params.GetValueOrDefault("visNoBoxes", "false") ?? "false").Trim(),
                            "true",
                            StringComparison.OrdinalIgnoreCase);
                        var yr = YoloSegInferenceBridge.Run(
                            srcImg,
                            py.Trim(),
                            scriptAbs,
                            weightsAbs,
                            conf,
                            useGpu,
                            timeoutSec * 1000,
                            imgsz,
                            visNoBoxes);
                        node.Outputs["Out"] = yr.Passthrough;
                        node.Outputs["Vis"] = yr.Visualization;
                        node.Outputs["DetectJson"] = yr.DetectJson;
                        node.ResultSummary = yr.DetectJson.Length > 96 ? yr.DetectJson.Substring(0, 96) + "…" : yr.DetectJson;
                        break;
                    }

                    case "find_contours":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("查找轮廓: 缺少输入二值图像");
                        using var detector = new TrajectoryStepDetector();
                        detector.SetDarkBinary(inImg);
                        double minContourArea = -1.0;
                        var minAreaStr = node.Params.GetValueOrDefault("minContourArea");
                        if (!string.IsNullOrWhiteSpace(minAreaStr) &&
                            double.TryParse(
                                minAreaStr.Trim(),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var ma))
                            minContourArea = ma;
                        int count = detector.FindAndSortDarkContours(minContourArea);
                        var contourVis = detector.GetContourVis();
                        node.Outputs["Out"] = contourVis;
                        node.Outputs["Count"] = count;
                        var (cx, cy, cl, cc) = detector.ExportSortedContours();
                        node.Outputs["Contours"] = (cx, cy, cl, cc);
                        break;
                    }

                    case "apply_mask":
                    {
                        var srcImg = inputs["Image"] as CalibImage;
                        var maskImg = inputs["Mask"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("Mask应用: 缺少输入图像");
                        if (maskImg == null) throw new InvalidOperationException("Mask应用: 缺少Mask图像");
                        var resultImg = ImageOps.ApplyMask(srcImg, maskImg);
                        node.Outputs["Out"] = resultImg;
                        break;
                    }

                    case "create_mask":
                    {
                        int contourIdx = int.Parse(node.Params["contourIdx"]);
                        CalibImage maskImg;
                        var inImg = inputs["In"] as CalibImage;

                        if (inputs.TryGetValue("Contours", out var contourObj) &&
                            contourObj is ValueTuple<int[], int[], int[], int> contourData)
                        {
                            int w = inImg?.Width ?? CalibAPI.ImageWidth;
                            int h = inImg?.Height ?? CalibAPI.ImageHeight;
                            var (_, _, _, fc) = contourData;
                            if (fc <= 0)
                            {
                                maskImg = new CalibImage(w, h, 1);
                                node.Outputs["Mask"] = maskImg;
                                node.ResultSummary = "skip: empty contours → black Mask";
                                break;
                            }

                            maskImg = BuildMaskFromContours(contourData, contourIdx, w, h);
                        }
                        else
                        {
                            if (inImg == null) throw new InvalidOperationException("生成Mask: 缺少输入图像或Contours");
                            using var detector = new TrajectoryStepDetector();
                            detector.ConvertToGrayscale(inImg);
                            detector.PreprocessAndFindContours();
                            int ret = detector.CreateMaskFromLargestContour(contourIdx);
                            if (ret != 0)
                            {
                                maskImg = new CalibImage(inImg.Width, inImg.Height, 1);
                                node.Outputs["Mask"] = maskImg;
                                node.ResultSummary = "skip: no contour from image → black Mask";
                                break;
                            }

                            maskImg = detector.GetStepImage(3) ?? throw new InvalidOperationException("生成Mask: 获取输出失败");
                        }
                        node.Outputs["Mask"] = maskImg;
                        break;
                    }

                    case "detect_hollow":
                    {
                        var srcImg = (inputs.TryGetValue("Mask", out var maskObj) ? maskObj : null) as CalibImage
                                     ?? (inputs.TryGetValue("Image", out var imgObj) ? imgObj : null) as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("空洞检测: 缺少输入图像");
                        int hLow = int.Parse(node.Params["hollowGrayLow"]);
                        int hHigh = int.Parse(node.Params["hollowGrayHigh"]);
                        int targetH = int.Parse(node.Params["targetHollows"]);
                        int morphK = int.Parse(node.Params["morphKernelSize"]);
                        var result = CalibAPI.DetectHollowTrajectory(srcImg, morphKernelSize: morphK, targetHollows: targetH,
                            hollowGrayLow: hLow, hollowGrayHigh: hHigh);
                        node.Outputs["Count"] = result.Count;
                        var hollowImg = result.StepImages.Count > 3 ? result.StepImages[3] : null;
                        // Transfer ownership of one image to flow output, release the rest.
                        for (int i = 0; i < result.StepImages.Count; i++)
                        {
                            if (!ReferenceEquals(result.StepImages[i], hollowImg))
                                result.StepImages[i]?.Dispose();
                        }
                        node.Outputs["Hollow"] = hollowImg;
                        break;
                    }

                    case "detect_dark":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        var maskImg = inputs["Mask"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("暗条检测: 缺少输入图像");
                        if (maskImg == null) throw new InvalidOperationException("暗条检测: 缺少Mask图像");
                        int darkTh = int.Parse(node.Params["darkThreshold"]);
                        using var detector = new TrajectoryStepDetector();
                        detector.ConvertToGrayscale(srcImg);
                        detector.SetMask(maskImg);
                        detector.DetectDarkBars(darkTh, 0, 0);
                        var darkImg = detector.GetStepImage(4);
                        node.Outputs["Dark"] = darkImg;
                        break;
                    }

                    case "morphology":
                    {
                        var darkImg = inputs["In"] as CalibImage;
                        if (darkImg == null) throw new InvalidOperationException("形态学: 缺少输入图像");
                        int kernelSize = int.Parse(node.Params["kernelSize"]);
                        int blurKsize = int.Parse(node.Params["blurKsize"]);
                        double blurSigma = double.Parse(node.Params["blurSigma"]);
                        using var detector = new TrajectoryStepDetector();
                        detector.SetDarkBinary(darkImg);
                        detector.MorphologyCleanup(kernelSize, blurKsize, blurSigma);
                        var morphImg = detector.GetStepImage(4);
                        node.Outputs["Out"] = morphImg;
                        break;
                    }

                    case "expand_edge":
                    {
                        var darkImg = inputs["Dark"] as CalibImage;
                        var edgeImg = inputs["Edge"] as CalibImage;
                        if (darkImg == null) throw new InvalidOperationException("边界膨胀: 缺少Dark输入");
                        if (edgeImg == null) throw new InvalidOperationException("边界膨胀: 缺少Edge输入");
                        int expandDist = int.Parse(node.Params["expandDist"]);
                        using var detector = new TrajectoryStepDetector();
                        detector.SetDarkBinary(darkImg);
                        detector.SetGrayMat(edgeImg);
                        detector.CannyEdges();
                        detector.ExpandToEdgeBoundary(expandDist);
                        var expandImg = detector.GetStepImage(7);
                        node.Outputs["Out"] = expandImg;
                        break;
                    }

                    case "sort_contours":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("排序: 缺少输入图像");
                        using var detector = new TrajectoryStepDetector();
                        detector.SetDarkBinary(srcImg);
                        double minContourArea = -1.0;
                        var minAreaStr = node.Params.GetValueOrDefault("minContourArea");
                        if (!string.IsNullOrWhiteSpace(minAreaStr) &&
                            double.TryParse(
                                minAreaStr.Trim(),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var ma))
                            minContourArea = ma;
                        int count = detector.FindAndSortDarkContours(minContourArea);
                        node.Outputs["Count"] = count;
                        break;
                    }

                    case "sample":
                    {
                        if (!inputs.TryGetValue("Contours", out var contourObj))
                            throw new InvalidOperationException("采样: 缺少输入轮廓数据");
                        if (contourObj is not ValueTuple<int[], int[], int[], int> contourData)
                            throw new InvalidOperationException("采样: 轮廓数据格式错误");
                        var (flatX, flatY, contourLengths, contourCount) = contourData;
                        if (contourCount <= 0)
                        {
                            node.Outputs["Points"] = Array.Empty<Point2D>();
                            node.Outputs["BarIds"] = Array.Empty<int>();
                            node.ResultSummary = "skip: empty contours → empty Points";
                            break;
                        }

                        int targetBars = int.Parse(node.Params["targetBars"]);
                        int spacing = int.Parse(node.Params["spacing"]);
                        var (pts, barIds) = CalibAPI.SampleContoursFromPointsWithBarIds(flatX, flatY, contourLengths,
                            CalibAPI.ImageWidth, CalibAPI.ImageHeight, targetBars, (double)spacing);
                        node.Outputs["Points"] = pts;
                        node.Outputs["BarIds"] = barIds;
                        break;
                    }

                    case "contours_pixel_to_world":
                    {
                        if (!inputs.TryGetValue("Contours", out var cObj) || cObj is not ValueTuple<int[], int[], int[], int> pix)
                            throw new InvalidOperationException("轮廓像素→世界: 缺少像素 Contours");
                        var (ix, iy, lens, cnt) = pix;
                        if (cnt <= 0 || lens == null || ix == null || iy == null)
                        {
                            node.Outputs["ContoursWorld"] = (Array.Empty<double>(), Array.Empty<double>(), Array.Empty<int>(), 0);
                            node.ResultSummary = "skip: empty Contours → empty ContoursWorld";
                            break;
                        }

                        var map = ResolvePixelToWorldMapperForContours(inputs);
                        var world = TransformPixelContourTupleToWorldDoubleTuple(pix, map);
                        node.Outputs["ContoursWorld"] = world;
                        node.ResultSummary = $"条数={world.Item4} 点数={world.Item1?.Length ?? 0}";
                        break;
                    }

                    case "simplify_contours_to_corners":
                    {
                        var pts = inputs["In"] as Point2D[];
                        if (pts == null)
                            throw new InvalidOperationException("轮廓四角点: 缺少输入点列 In（未连接或非 Point2D[]）");
                        inputs.TryGetValue("BarIds", out var barObj);
                        var barIds = barObj as int[];

                        if (pts.Length == 0)
                        {
                            node.Outputs["Out"] = Array.Empty<Point2D>();
                            node.Outputs["OutBarIds"] = Array.Empty<int>();
                            node.ResultSummary = "skip: 0 pts → empty Out";
                            break;
                        }

                        // 按 BarIds 分段
                        var segments = new List<(int Start, int Len, int BarId)>();
                        if (barIds != null && barIds.Length == pts.Length)
                        {
                            int segStart = 0;
                            for (int i = 1; i < pts.Length; i++)
                            {
                                if (barIds[i] != barIds[i - 1])
                                {
                                    if (i - segStart > 0)
                                        segments.Add((segStart, i - segStart, barIds[segStart]));
                                    segStart = i;
                                }
                            }
                            if (pts.Length - segStart > 0)
                                segments.Add((segStart, pts.Length - segStart, barIds[segStart]));
                        }
                        else
                        {
                            segments.Add((0, pts.Length, 0));
                        }

                        var outPts = new List<Point2D>();
                        var outBarIds = new List<int>();

                        foreach (var seg in segments)
                        {
                            if (seg.Len < 4) continue;

                            // 1. 计算质心
                            double cx = 0, cy = 0;
                            for (int k = 0; k < seg.Len; k++)
                            {
                                cx += pts[seg.Start + k].X;
                                cy += pts[seg.Start + k].Y;
                            }
                            cx /= seg.Len;
                            cy /= seg.Len;

                            // 2. PCA 主方向
                            double mxx = 0, myy = 0, mxy = 0;
                            for (int k = 0; k < seg.Len; k++)
                            {
                                double dx = pts[seg.Start + k].X - cx;
                                double dy = pts[seg.Start + k].Y - cy;
                                mxx += dx * dx;
                                myy += dy * dy;
                                mxy += dx * dy;
                            }
                            double trace = mxx + myy;
                            double disc = Math.Sqrt(Math.Max(0, (mxx - myy) * (mxx - myy) / 4 + mxy * mxy));
                            double lambda1 = (trace / 2) + disc;
                            double vx, vy;
                            if (disc < 1e-10)
                            {
                                vx = 1; vy = 0;
                            }
                            else
                            {
                                double ratio = lambda1 - mxx;
                                if (Math.Abs(mxy) > Math.Abs(ratio))
                                {
                                    vy = ratio / mxy;
                                    double lenV = Math.Sqrt(1 + vy * vy);
                                    vx = 1 / lenV; vy /= lenV;
                                }
                                else
                                {
                                    vx = mxy / ratio;
                                    double lenV = Math.Sqrt(vx * vx + 1);
                                    vy = 1 / lenV; vx /= lenV;
                                }
                            }
                            if (vx < 0) { vx = -vx; vy = -vy; }
                            double nx = -vy, ny = vx; // 法线方向（垂直于主方向）

                            // 3. 计算射线与轮廓边界的交点
                            Point2D pLeft = new Point2D(cx, cy), pRight = new Point2D(cx, cy);
                            Point2D pTop = new Point2D(cx, cy), pBottom = new Point2D(cx, cy);
                            double maxDistRight = 0, maxDistLeft = 0, maxDistTop = 0, maxDistBottom = 0;

                            // 遍历轮廓边（相邻点之间的线段）
                            for (int i = 0; i < seg.Len; i++)
                            {
                                int j = (i + 1) % seg.Len;
                                var a = pts[seg.Start + i];
                                var b = pts[seg.Start + j];

                                // 线段方向
                                double segDx = b.X - a.X;
                                double segDy = b.Y - a.Y;

                                // 交点计算：射线 p = center + t * dir 与线段 ab 的交点
                                // 行列式: dir.x * segDy - dir.y * segDx
                                // t = (a - center) cross seg / (dir cross seg)
                                // 其中 cross(u,v) = u.x*v.y - u.y*v.x

                                // 主方向交点（左右）
                                double denom = vx * segDy - vy * segDx;
                                if (Math.Abs(denom) > 1e-10)
                                {
                                    double ax_cx = a.X - cx, ay_cy = a.Y - cy;
                                    // t: 射线参数，s: 线段参数
                                    double t = (ax_cx * segDy - ay_cy * segDx) / denom;
                                    double s = (ax_cx * vy - ay_cy * vx) / denom;

                                    if (s >= 0 && s <= 1)
                                    {
                                        if (t > maxDistRight)
                                        {
                                            maxDistRight = t;
                                            pRight = new Point2D(cx + t * vx, cy + t * vy);
                                        }
                                        if (-t > maxDistLeft)
                                        {
                                            maxDistLeft = -t;
                                            pLeft = new Point2D(cx + t * vx, cy + t * vy);
                                        }
                                    }
                                }

                                // 法线方向交点（上下）
                                denom = nx * segDy - ny * segDx;
                                if (Math.Abs(denom) > 1e-10)
                                {
                                    double ax_cx = a.X - cx, ay_cy = a.Y - cy;
                                    double t = (ax_cx * segDy - ay_cy * segDx) / denom;
                                    double s = (ax_cx * ny - ay_cy * nx) / denom;

                                    if (s >= 0 && s <= 1)
                                    {
                                        if (t > maxDistBottom)
                                        {
                                            maxDistBottom = t;
                                            pBottom = new Point2D(cx + t * nx, cy + t * ny);
                                        }
                                        if (-t > maxDistTop)
                                        {
                                            maxDistTop = -t;
                                            pTop = new Point2D(cx + t * nx, cy + t * ny);
                                        }
                                    }
                                }
                            }

                            // 4. 顺时针输出：左→上→右→下
                            outPts.Add(pLeft); outBarIds.Add(seg.BarId);
                            outPts.Add(pTop); outBarIds.Add(seg.BarId);
                            outPts.Add(pRight); outBarIds.Add(seg.BarId);
                            outPts.Add(pBottom); outBarIds.Add(seg.BarId);
                        }

                        node.Outputs["Out"] = outPts.ToArray();
                        node.Outputs["OutBarIds"] = outBarIds.ToArray();
                        node.ResultSummary = $"段数={segments.Count} 每段4点";
                        break;
                    }

                    case "contour_perpendicular_entry":
                    {
                        double retreatX = double.TryParse(
                            node.Params.GetValueOrDefault("retreatX")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var rx)
                            ? rx : 0;
                        double retreatY = double.TryParse(
                            node.Params.GetValueOrDefault("retreatY")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var ry)
                            ? ry : 0;
                        double retreatZ = double.TryParse(
                            node.Params.GetValueOrDefault("retreatZ", "0")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var rz)
                            ? rz : 0;
                        double planarZ = double.TryParse(
                            node.Params.GetValueOrDefault("planarZ", "0")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var pz)
                            ? pz : 0;
                        double verticalDist = double.TryParse(
                            node.Params.GetValueOrDefault("verticalDist", "30")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var vd)
                            ? vd : 30;
                        bool leadIn = !string.Equals(
                            (node.Params.GetValueOrDefault("leadIn", "true") ?? "true").Trim(),
                            "false", StringComparison.OrdinalIgnoreCase);
                        bool leadOut = !string.Equals(
                            (node.Params.GetValueOrDefault("leadOut", "true") ?? "true").Trim(),
                            "false", StringComparison.OrdinalIgnoreCase);
                        double transitSpacing = double.TryParse(
                            node.Params.GetValueOrDefault("transitSpacing")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var tsp)
                            ? tsp : 0;
                        string closeContour = (node.Params.GetValueOrDefault("closeContour", "false") ?? "false").Trim().ToLowerInvariant();

                        var retreat = new CalibPoint3D(retreatX, retreatY, retreatZ);

                        // 输入格式解析（优先级同 contours_to_weld_path）
                        List<CalibPoint3D[]> contourSegs3D;
                        List<int> contourBarIds;
                        string modeTag;
                        if (inputs.TryGetValue("ContoursBase3D", out var b3o) &&
                            b3o is ValueTuple<double[], double[], double[], int[], int> b3 &&
                            b3.Item5 > 0 && b3.Item1 != null && b3.Item2 != null && b3.Item3 != null && b3.Item4 != null)
                        {
                            modeTag = "基座3D条带";
                            contourSegs3D = ExplodeContourTripleToPolylines3D(b3.Item1, b3.Item2, b3.Item3, b3.Item4, b3.Item5);
                            contourBarIds = Enumerable.Range(0, contourSegs3D.Count).ToList();
                        }
                        else if (inputs.TryGetValue("ContoursWorld", out var wOb) &&
                            wOb is ValueTuple<double[], double[], int[], int> wt &&
                            wt.Item4 > 0 && wt.Item1 != null && wt.Item2 != null && wt.Item3 != null)
                        {
                            modeTag = "平面条带→基座Z";
                            var segs2 = ExplodeContourDoubleTupleToPolylines(wt.Item1, wt.Item2, wt.Item3, wt.Item4);
                            contourSegs3D = segs2.Select(s => LiftPoint2DToBase3D(s, planarZ)).ToList();
                            contourBarIds = Enumerable.Range(0, contourSegs3D.Count).ToList();
                        }
                        else if (inputs.TryGetValue("Contours", out var cOb) &&
                            cOb is ValueTuple<int[], int[], int[], int> it &&
                            it.Item4 > 0 && it.Item1 != null && it.Item2 != null && it.Item3 != null)
                        {
                            modeTag = "像素条带→Z=0";
                            var segs2 = ExplodeContourTupleToPolylines(it.Item1, it.Item2, it.Item3, it.Item4);
                            contourSegs3D = segs2.Select(s => LiftPoint2DToBase3D(s, 0)).ToList();
                            contourBarIds = Enumerable.Range(0, contourSegs3D.Count).ToList();
                        }
                        else if (inputs.TryGetValue("SamplePts", out var spOb) && spOb is CalibPoint3D[] sp && sp.Length > 0)
                        {
                            inputs.TryGetValue("BarIds", out var bidOb);
                            var barIds = bidOb as int[];
                            SplitSampledPointsToContourPolylinesWithBarIds3D(sp, barIds, out contourSegs3D, out contourBarIds);
                            modeTag = barIds != null && barIds.Length == sp.Length ? "采样点(基座3D)" : "采样点(基座3D·无BarIds)";
                        }
                        else
                        {
                            node.Outputs["Points"] = Array.Empty<CalibPoint3D>();
                            node.Outputs["BarIds"] = Array.Empty<int>();
                            node.ResultSummary = "skip: 未连接有效 ContoursBase3D / ContoursWorld / Contours / SamplePts";
                            break;
                        }

                        var zipped = ZipRemoveEmptyContourSegments3D(contourSegs3D, contourBarIds);
                        contourSegs3D = zipped.Segments;
                        contourBarIds = zipped.BarIds;
                        if (contourSegs3D.Count == 0)
                        {
                            node.Outputs["Points"] = Array.Empty<CalibPoint3D>();
                            node.Outputs["BarIds"] = Array.Empty<int>();
                            node.ResultSummary = "skip: no non-empty contour polylines";
                            break;
                        }

                        var path = new List<CalibPoint3D>();
                        var pathBarIds = new List<int>();

                        // 垂直方向由回退点Z和首轮廓点Z的关系决定
                        // 回退点在工件上方（Z >= 进入点Z）：垂直方向向上（+Z）
                        // 回退点在工件下方（Z < 进入点Z）：垂直方向向下（-Z）
                        double zSign = (retreatZ - contourSegs3D[0][0].Z) >= 0 ? 1 : -1;

                        CalibPoint3D retreatAbove = default; // 循环外声明，供 leadOut 使用
                        int lastBarId = contourBarIds.Count > 0 ? contourBarIds[0] : 0;

                        for (int i = 0; i < contourSegs3D.Count; i++)
                        {
                            var seg = contourSegs3D[i];
                            if (seg == null || seg.Length < 2) continue;

                            int barId = i < contourBarIds.Count ? contourBarIds[i] : i;
                            lastBarId = barId;

                            var pStart = seg[0];
                            var pEnd = seg[seg.Length - 1];

                            // 进入点正上方/正下方：沿Z轴偏移
                            var entryAbove = new CalibPoint3D(pStart.X, pStart.Y, pStart.Z + zSign * verticalDist);
                            // 回退点正上方/正下方：沿Z轴偏移
                            retreatAbove = new CalibPoint3D(retreat.X, retreat.Y, retreat.Z + zSign * verticalDist);

                            if (i == 0)
                            {
                                // 第一条轮廓
                                if (leadIn)
                                {
                                    // 进入段：回退点 → 进入点正上方/正下方 → 进入点（只保留轮廓点附近的垂直段）
                                    AppendLineTransit3DWithBarId(path, pathBarIds, retreat, entryAbove, transitSpacing, barId);
                                    AppendLineTransit3DWithBarId(path, pathBarIds, entryAbove, pStart, transitSpacing, barId);
                                }
                                else
                                {
                                    AppendDedupePoint3DWithBarId(path, pathBarIds, pStart, barId);
                                }
                            }
                            else
                            {
                                // 从上一条轮廓的退出点直接回退
                                AppendLineTransit3DWithBarId(path, pathBarIds, path[path.Count - 1], retreat, transitSpacing, barId);
                                // 进入段：回退点 → 进入点正上方/正下方 → 进入点
                                AppendLineTransit3DWithBarId(path, pathBarIds, retreat, entryAbove, transitSpacing, barId);
                                AppendLineTransit3DWithBarId(path, pathBarIds, entryAbove, pStart, transitSpacing, barId);
                            }

                            // 添加轮廓所有点
                            foreach (var p in seg)
                                AppendDedupePoint3DWithBarId(path, pathBarIds, p, barId);

                            // 退出延伸：轮廓终点 → 进入点（闭合）→ 进入点正上方/正下方
                            // 进出都连到同一个轮廓起点，形成闭合轨迹
                            if (PointDistance3D(pEnd, pStart) > 1e-9)
                                AppendLineTransit3DWithBarId(path, pathBarIds, path[path.Count - 1], pStart, transitSpacing, barId);
                            AppendLineTransit3DWithBarId(path, pathBarIds, path[path.Count - 1], entryAbove, transitSpacing, barId);
                        }

                        if (leadOut && path.Count > 0)
                        {
                            // 最后退出点正上方/正下方 → 回退点（只保留轮廓点附近的垂直段）
                            AppendLineTransit3DWithBarId(path, pathBarIds, path[path.Count - 1], retreat, transitSpacing, lastBarId);
                        }

                        if (pathBarIds.Count != path.Count)
                            throw new InvalidOperationException(
                                $"垂直入刀: 内部 BarIds 与点数不一致 ({pathBarIds.Count} vs {path.Count})");

                        node.Outputs["Points"] = path.ToArray();
                        node.Outputs["BarIds"] = pathBarIds.ToArray();
                        node.ResultSummary = $"{modeTag} · {path.Count} 点 · {contourSegs3D.Count} 条轮廓 · BarIds逐点";
                        break;
                    }

                    case "contours_to_weld_path":
                    {
                        double retreatX = double.TryParse(
                            node.Params.GetValueOrDefault("retreatX")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var rx)
                            ? rx
                            : 0;
                        double retreatY = double.TryParse(
                            node.Params.GetValueOrDefault("retreatY")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var ry)
                            ? ry
                            : 0;
                        double retreatZ = double.TryParse(
                            node.Params.GetValueOrDefault("retreatZ", "0")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var rz)
                            ? rz
                            : 0;
                        double planarZ = double.TryParse(
                            node.Params.GetValueOrDefault("planarZ", "0")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var pz)
                            ? pz
                            : 0;
                        bool leadIn = !string.Equals(
                            (node.Params.GetValueOrDefault("leadIn", "true") ?? "true").Trim(),
                            "false",
                            StringComparison.OrdinalIgnoreCase);
                        bool leadOut = !string.Equals(
                            (node.Params.GetValueOrDefault("leadOut", "true") ?? "true").Trim(),
                            "false",
                            StringComparison.OrdinalIgnoreCase);
                        double transitSpacing = double.TryParse(
                            node.Params.GetValueOrDefault("transitSpacing")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var tsp)
                            ? tsp
                            : 0;
                        string barSplit = (node.Params.GetValueOrDefault("barSplit", "auto") ?? "auto").Trim();
                        double segmentJoinMaxDistOverride = double.TryParse(
                            node.Params.GetValueOrDefault("segmentJoinMaxDist")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var sjm)
                            ? sjm
                            : 0;
                        bool sanitizeBarIds = !string.Equals(
                            (node.Params.GetValueOrDefault("sanitizeBarIds", "true") ?? "true").Trim(),
                            "false",
                            StringComparison.OrdinalIgnoreCase);

                        List<CalibPoint3D[]> segments;
                        List<int> segmentBarIds;
                        string modeTag;
                        if (inputs.TryGetValue("ContoursBase3D", out var b3o) &&
                            b3o is ValueTuple<double[], double[], double[], int[], int> b3 &&
                            b3.Item5 > 0 &&
                            b3.Item1 != null &&
                            b3.Item2 != null &&
                            b3.Item3 != null &&
                            b3.Item4 != null)
                        {
                            modeTag = "基座3D条带";
                            segments = ExplodeContourTripleToPolylines3D(b3.Item1, b3.Item2, b3.Item3, b3.Item4, b3.Item5);
                            segmentBarIds = Enumerable.Range(0, segments.Count).ToList();
                        }
                        else if (inputs.TryGetValue("ContoursWorld", out var wOb) &&
                            wOb is ValueTuple<double[], double[], int[], int> wt &&
                            wt.Item4 > 0 &&
                            wt.Item1 != null &&
                            wt.Item2 != null &&
                            wt.Item3 != null)
                        {
                            modeTag = "平面条带→基座Z";
                            var segs2 = ExplodeContourDoubleTupleToPolylines(wt.Item1, wt.Item2, wt.Item3, wt.Item4);
                            segments = segs2.Select(s => LiftPoint2DToBase3D(s, planarZ)).ToList();
                            segmentBarIds = Enumerable.Range(0, segments.Count).ToList();
                        }
                        else if (inputs.TryGetValue("Contours", out var cOb) &&
                            cOb is ValueTuple<int[], int[], int[], int> it &&
                            it.Item4 > 0 &&
                            it.Item1 != null &&
                            it.Item2 != null &&
                            it.Item3 != null)
                        {
                            modeTag = "像素条带→Z=0";
                            var segs2 = ExplodeContourTupleToPolylines(it.Item1, it.Item2, it.Item3, it.Item4);
                            segments = segs2.Select(s => LiftPoint2DToBase3D(s, 0)).ToList();
                            segmentBarIds = Enumerable.Range(0, segments.Count).ToList();
                        }
                        else if (inputs.TryGetValue("SamplePts", out var spOb) && spOb is CalibPoint3D[] sp && sp.Length > 0)
                        {
                            inputs.TryGetValue("BarIds", out var bidOb);
                            var barIds = bidOb as int[];
                            if (barIds != null && barIds.Length == sp.Length &&
                                sanitizeBarIds && !BarSplitIsStrictByBar(barSplit))
                                barIds = SanitizeBarIdsUnifyAlongShortSteps3D(sp, barIds, transitSpacing);
                            SplitSampledPointsToContourPolylinesWithBarIds3D(sp, barIds, out segments, out segmentBarIds);
                            modeTag = barIds != null && barIds.Length == sp.Length ? "采样点(基座3D)" : "采样点(基座3D·无BarIds)";
                        }
                        else
                        {
                            node.Outputs["Points"] = Array.Empty<CalibPoint3D>();
                            node.ResultSummary = "skip: 未连接有效 ContoursBase3D / ContoursWorld / Contours / SamplePts(CalibPoint3D[])";
                            break;
                        }

                        var retreat = new CalibPoint3D(retreatX, retreatY, retreatZ);
                        var zippedEmpty = ZipRemoveEmptyContourSegments3D(segments, segmentBarIds);
                        segments = zippedEmpty.Segments;
                        segmentBarIds = zippedEmpty.BarIds;
                        if (segments.Count == 0)
                        {
                            node.Outputs["Points"] = Array.Empty<CalibPoint3D>();
                            node.ResultSummary = "skip: no non-empty contour polylines";
                            break;
                        }

                        string closeContour = (node.Params.GetValueOrDefault("closeContour", "auto") ?? "auto").Trim();
                        var norm = NormalizeWeldContourSegmentsForRetreat3D(
                            segments,
                            segmentBarIds,
                            barSplit,
                            transitSpacing,
                            segmentJoinMaxDistOverride);
                        segments = norm.Segments;
                        segmentBarIds = norm.BarIds;
                        var segNormNote = norm.Note;
                        if (segments.Count == 0)
                        {
                            node.Outputs["Points"] = Array.Empty<CalibPoint3D>();
                            node.ResultSummary = "skip: no segments after barSplit normalize";
                            break;
                        }

                        var path = BuildWeldPathWithRetreatBetweenContours3D(
                            segments,
                            retreat,
                            leadIn,
                            leadOut,
                            transitSpacing,
                            closeContour);
                        node.Outputs["Points"] = path;
                        node.ResultSummary = $"{modeTag} · {path.Length} 点 · {segments.Count} 条轮廓{segNormNote ?? ""}";
                        break;
                    }

                    case "filter_contours":
                    {
                        if (!inputs.TryGetValue("Contours", out var contourObj))
                            throw new InvalidOperationException("轮廓筛选: 缺少输入轮廓数据");
                        if (contourObj is not ValueTuple<int[], int[], int[], int> contourData)
                            throw new InvalidOperationException("轮廓筛选: 轮廓数据格式错误");
                        if (contourData.Item4 <= 0)
                        {
                            node.Outputs["Contours"] = (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);
                            node.Outputs["Count"] = 0;
                            node.ResultSummary = "skip: empty contours";
                            break;
                        }

                        double ParseInv(string key, double defVal)
                        {
                            var s = node.Params.GetValueOrDefault(key);
                            if (string.IsNullOrWhiteSpace(s)) return defVal;
                            return double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                                ? v
                                : defVal;
                        }

                        double minArea = ParseInv("minArea", 8000);
                        double maxArea = ParseInv("maxArea", 4000000);
                        double minAspect = ParseInv("minAspect", 0.2);
                        double maxAspect = ParseInv("maxAspect", 5.0);
                        double minCircularity = ParseInv("minCircularity", 0.02);
                        double maxCircularity = ParseInv("maxCircularity", 1.0);
                        int targetCount = int.TryParse(node.Params.GetValueOrDefault("targetCount"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tc) ? tc : 15;
                        bool sortCy = string.Equals((node.Params.GetValueOrDefault("sortByCentroidY", "false") ?? "false").Trim(), "true", StringComparison.OrdinalIgnoreCase);

                        var filtered = FilterContoursByGeometry(
                            contourData, minArea, maxArea, minAspect, maxAspect, minCircularity, maxCircularity, targetCount, sortCy);

                        node.Outputs["Contours"] = filtered;
                        node.Outputs["Count"] = filtered.count;
                        node.ResultSummary = $"Filtered: {filtered.count}";
                        break;
                    }

                    case "match_contours":
                    {
                        if (!inputs.TryGetValue("Contours", out var contourObj))
                            throw new InvalidOperationException("形状匹配: 缺少输入轮廓数据");
                        if (contourObj is not ValueTuple<int[], int[], int[], int> contourData)
                            throw new InvalidOperationException("形状匹配: 轮廓数据格式错误");
                        if (contourData.Item4 <= 0)
                        {
                            node.Outputs["Contours"] = (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);
                            node.Outputs["Count"] = 0;
                            node.ResultSummary = "skip: empty contours";
                            break;
                        }

                        int templateIndex = int.Parse(node.Params["templateIndex"]);
                        double maxDistance = double.Parse(node.Params["maxDistance"]);
                        int samplePoints = int.Parse(node.Params["samplePoints"]);
                        int targetCount = int.Parse(node.Params["targetCount"]);

                        var matched = MatchContoursByTemplate(contourData, templateIndex, maxDistance, samplePoints, targetCount);
                        node.Outputs["Contours"] = matched;
                        node.Outputs["Count"] = matched.count;
                        node.ResultSummary = $"Matched: {matched.count}";
                        break;
                    }

                    case "shape_match_global":
                    {
                        var edgeImg = inputs["Edge"] as CalibImage;
                        if (edgeImg == null) throw new InvalidOperationException("全局形状匹配: 缺少边缘图输入");

                        int topN = int.Parse(node.Params["topN"]);
                        double minScore = double.Parse(node.Params["minScore"]);
                        double areaWeight = double.Parse(node.Params.GetValueOrDefault("areaWeight", "0.6"));
                        int step = int.Parse(node.Params["step"]);
                        int downsample = int.Parse(node.Params["downsample"]);
                        int maxTemplateSize = int.Parse(node.Params["maxTemplateSize"]);
                        int templateBars = int.Parse(node.Params["templateBars"]);
                        double templateConsensus = double.Parse(node.Params["templateConsensus"]);
                        int edgeMinComponent = int.Parse(node.Params["edgeMinComponent"]);
                        int edgeOpenRadius = int.Parse(node.Params["edgeOpenRadius"]);
                        string templatePath = (node.Params.GetValueOrDefault("templatePath", "") ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(templatePath))
                            templatePath = ResolveCompositeFlowPath(templatePath, compositeInnerFlowBaseDir);

                        using var edgeBmp = edgeImg.ToBitmap();
                        if (edgeBmp == null) throw new InvalidOperationException("全局形状匹配: 边缘图转换失败");

                        var edgeBin = BitmapToBinary(edgeBmp, 1);
                        if (edgeOpenRadius > 0)
                            edgeBin = DilateBinary(ErodeBinary(edgeBin, edgeOpenRadius), edgeOpenRadius);
                        edgeBin = RemoveSmallComponents(edgeBin, Math.Max(1, edgeMinComponent));
                        var edgeSmall = DownsampleBinary(edgeBin, Math.Max(1, downsample));

                        bool[,] templateBin;
                        if (inputs.TryGetValue("Template", out var tplObj) && tplObj is CalibImage tplImg)
                        {
                            using var tplBmp = tplImg.ToBitmap();
                            if (tplBmp == null) throw new InvalidOperationException("全局形状匹配: 模板图转换失败");
                            templateBin = BitmapToBinary(tplBmp, 1);
                            templateBin = DownsampleBinary(templateBin, Math.Max(1, downsample));
                        }
                        else if (!string.IsNullOrWhiteSpace(templatePath) && System.IO.File.Exists(templatePath))
                        {
                            using var tplBmp = new System.Drawing.Bitmap(templatePath);
                            templateBin = BitmapToBinary(tplBmp, 1);
                            templateBin = DownsampleBinary(templateBin, Math.Max(1, downsample));
                        }
                        else if (inputs.TryGetValue("Contours", out var cObj) && cObj is ValueTuple<int[], int[], int[], int> contourData)
                        {
                            templateBin = BuildFusedTemplateFromContours(contourData, templateBars, Math.Max(24, maxTemplateSize), templateConsensus);
                        }
                        else
                        {
                            var bbox = LargestConnectedBoundingBox(edgeSmall);
                            if (bbox == null) throw new InvalidOperationException("全局形状匹配: 未找到可用模板（边缘连通域为空）");
                            templateBin = CropBinary(edgeSmall, bbox.Value.X, bbox.Value.Y, bbox.Value.W, bbox.Value.H);
                        }

                        templateBin = ResizeBinary(templateBin, Math.Max(24, maxTemplateSize));
                        var ptsSmall = GlobalShapeMatchTopN(edgeSmall, templateBin, Math.Max(1, topN), Math.Max(0, Math.Min(1, minScore)), Math.Max(1, step), areaWeight);
                        int factor = Math.Max(1, downsample);
                        var points = ptsSmall.Select(p => new Point2D(p.X * factor, p.Y * factor)).ToArray();
                        var templatePolyline = TemplateToPolyline(templateBin, 256);
                        var matchedContours = BuildMatchedContoursFromTemplate(points, templatePolyline, factor);

                        node.Outputs["Contours"] = matchedContours;
                        node.Outputs["Count"] = matchedContours.Item4;
                        node.ResultSummary = $"TopNContours: {matchedContours.Item4}";
                        break;
                    }

                    case "fuse_contours_template":
                    {
                        if (!inputs.TryGetValue("Contours", out var contourObj))
                            throw new InvalidOperationException("融合模板: 缺少输入轮廓数据");
                        if (contourObj is not ValueTuple<int[], int[], int[], int> contourData)
                            throw new InvalidOperationException("融合模板: 轮廓数据格式错误");
                        int canvasSizeEarly = Math.Max(16, int.Parse(node.Params["canvasSize"]));
                        if (contourData.Item4 <= 0)
                        {
                            var emptyBin = new bool[canvasSizeEarly, canvasSizeEarly];
                            node.Outputs["Template"] = BinaryToCalibImage(emptyBin);
                            node.ResultSummary = "skip: empty contours → blank Template";
                            break;
                        }

                        int templateBars = int.Parse(node.Params["templateBars"]);
                        int canvasSize = int.Parse(node.Params["canvasSize"]);
                        double templateConsensus = double.Parse(node.Params["templateConsensus"]);
                        int samplePoints = int.Parse(node.Params["samplePoints"]);
                        string fusionMode = (node.Params.GetValueOrDefault("fusionMode", "mutual") ?? "mutual").Trim().ToLowerInvariant();
                        string centerlineMode = (node.Params.GetValueOrDefault("centerlineMode", "on") ?? "on").Trim().ToLowerInvariant();

                        bool[,] templateBin = fusionMode == "global_layout"
                            ? BuildFusedTemplateFromContours(contourData, templateBars, canvasSize, templateConsensus)
                            : BuildMutualTemplateFromContours(contourData, templateBars, canvasSize, samplePoints);
                        if (centerlineMode == "on")
                            templateBin = ExtractCenterline(templateBin);
                        var templateImg = BinaryToCalibImage(templateBin);
                        node.Outputs["Template"] = templateImg;
                        node.ResultSummary = $"Template: {canvasSize}x{canvasSize}, mode={fusionMode}, centerline={centerlineMode}";
                        break;
                    }

                    case "fit_shape":
                    {
                        var inPts = inputs["In"] as Point2D[];
                        if (inPts == null) throw new InvalidOperationException("拟合: 缺少输入点");
                        if (inPts.Length == 0)
                        {
                            node.Outputs["Out"] = Array.Empty<Point2D>();
                            node.Outputs["OutBarIds"] = Array.Empty<int>();
                            node.ResultSummary = "skip: 0 pts → empty Out";
                            break;
                        }

                        if (inPts.Length < 3)
                        {
                            node.Outputs["Out"] = inPts;
                            var bidEarly = inputs.TryGetValue("BarIds", out var boe) ? boe as int[] : null;
                            node.Outputs["OutBarIds"] = bidEarly != null && bidEarly.Length == inPts.Length ? bidEarly : Array.Empty<int>();
                            break;
                        }

                        string mode = (node.Params.GetValueOrDefault("mode", "hybrid") ?? "hybrid").Trim().ToLowerInvariant();
                        int windowRadius = 1;
                        if (node.Params.TryGetValue("windowRadius", out var wrText))
                            int.TryParse(wrText, out windowRadius);
                        if (windowRadius < 0) windowRadius = 0;

                        double epsilon = 2.0;
                        if (node.Params.TryGetValue("epsilon", out var epText))
                            double.TryParse(epText, out epsilon);
                        if (epsilon < 0) epsilon = 0;

                        double splitGapFactor = 3.0;
                        if (node.Params.TryGetValue("splitGapFactor", out var sgText))
                            double.TryParse(sgText, out splitGapFactor);
                        if (splitGapFactor < 1.2) splitGapFactor = 1.2;

                        int minRegionPoints = 16;
                        if (node.Params.TryGetValue("minRegionPoints", out var mrText))
                            int.TryParse(mrText, out minRegionPoints);
                        if (minRegionPoints < 3) minRegionPoints = 3;

                        var inputBarIds = inputs.TryGetValue("BarIds", out var barObj) ? barObj as int[] : null;

                        Point2D[] FitOneRegion(Point2D[] region)
                        {
                            if (region.Length < 3) return region;
                            switch (mode)
                            {
                                case "moving_avg":
                                    return SmoothPoints(region, windowRadius <= 0 ? 1 : windowRadius);
                                case "simplify":
                                {
                                    var simplified = SimplifyClosedPolyline(region, epsilon <= 0 ? 1.0 : epsilon);
                                    return ResampleClosedPolyline(simplified, region.Length);
                                }
                                case "hybrid":
                                default:
                                {
                                    var simplified = SimplifyClosedPolyline(region, epsilon <= 0 ? 1.0 : epsilon);
                                    var resampled = ResampleClosedPolyline(simplified, region.Length);
                                    return SmoothPoints(resampled, windowRadius <= 0 ? 1 : windowRadius);
                                }
                            }
                        }

                        Point2D[] fitPts;
                        int[] outBarIds;
                        var barRegions = SplitRegionsByBarIds(inPts, inputBarIds ?? Array.Empty<int>(), minRegionPoints);
                        if (barRegions.Count > 0)
                        {
                            var mergedPts = new List<Point2D>(inPts.Length);
                            var mergedIds = new List<int>(inPts.Length);
                            foreach (var (regionPts, regionIds) in barRegions)
                            {
                                var fitted = FitOneRegion(regionPts);
                                mergedPts.AddRange(fitted);
                                if (fitted.Length == regionIds.Length) mergedIds.AddRange(regionIds);
                                else mergedIds.AddRange(Enumerable.Repeat(regionIds.Length > 0 ? regionIds[0] : -1, fitted.Length));
                            }
                            fitPts = mergedPts.ToArray();
                            outBarIds = mergedIds.ToArray();
                        }
                        else
                        {
                            var regions = SplitIntoClosedRegions(inPts, splitGapFactor, minRegionPoints);
                            var merged = new List<Point2D>(inPts.Length);
                            foreach (var region in regions)
                                merged.AddRange(FitOneRegion(region));
                            fitPts = merged.Count == inPts.Length ? merged.ToArray() : FitOneRegion(inPts);
                            outBarIds = inputBarIds != null && inputBarIds.Length == fitPts.Length ? inputBarIds : Array.Empty<int>();
                        }
                        node.Outputs["Out"] = fitPts;
                        node.Outputs["OutBarIds"] = outBarIds;
                        break;
                    }

                    case "verify_mask":
                    {
                        var inPts = inputs["In"] as Point2D[];
                        if (inPts == null) throw new InvalidOperationException("验证: 缺少输入点");
                        if (inPts.Length == 0)
                        {
                            node.Outputs["Out"] = Array.Empty<Point2D>();
                            node.ResultSummary = "skip: 0 pts → empty Out";
                            break;
                        }

                        var verPts = FilterPointsInImage(inPts, CalibAPI.ImageWidth, CalibAPI.ImageHeight);
                        node.Outputs["Out"] = verPts;
                        break;
                    }

                    case "dedup":
                    {
                        var inPts = inputs["In"] as Point2D[];
                        if (inPts == null) throw new InvalidOperationException("去重: 缺少输入点");
                        if (inPts.Length == 0)
                        {
                            node.Outputs["Out"] = Array.Empty<Point2D>();
                            node.ResultSummary = "skip: 0 pts → empty Out";
                            break;
                        }

                        var dedupPts = inPts
                            .GroupBy(p => $"{Math.Round(p.X, 3)}_{Math.Round(p.Y, 3)}")
                            .Select(g => g.First())
                            .ToArray();
                        node.Outputs["Out"] = dedupPts;
                        break;
                    }

                    case "convert_output":
                    {
                        var inPts = inputs["In"] as Point2D[];
                        if (inPts == null) throw new InvalidOperationException("输出转换: 缺少输入点");
                        var result = new TrajectoryResult
                        {
                            Success = inPts.Length > 0,
                            Count = inPts.Length,
                            Points = inPts
                        };
                        node.Outputs["Result"] = result;
                        if (inPts.Length == 0)
                            node.ResultSummary = "skip: 0 pts → Result.Success=false";
                        break;
                    }

                    case "draw_color":
                    {
                        var result = inputs["Result"] as TrajectoryResult;
                        var colorImg = new CalibImage(CalibAPI.ImageWidth, CalibAPI.ImageHeight, 3);
                        if (result?.Points != null && result.Points.Length > 0)
                            CalibAPI.DrawTrajectoryColored(colorImg, result.Points, result.BarIds);
                        else
                            node.ResultSummary = "skip: empty trajectory → blank Image";
                        node.Outputs["Image"] = colorImg;
                        break;
                    }

                    case "detect_circles":
                    {
                        var circleImg = inputs["Image"] as CalibImage;
                        if (circleImg == null) throw new InvalidOperationException("检测圆点: 缺少输入图像");
                        var circles = CalibAPI.DetectCircles(circleImg);
                        node.Outputs["Points"] = circles;
                        break;
                    }

                    case "hough_circles":
                    {
                        var hImg = inputs["Image"] as CalibImage;
                        if (hImg == null) throw new InvalidOperationException("霍夫圆: 缺少输入图像 Image");
                        int blurK = int.TryParse(node.Params.GetValueOrDefault("blurKsize"), out var bk) ? bk : 9;
                        if (blurK >= 3 && (blurK & 1) == 0) blurK |= 1;
                        double hcDp = double.TryParse(node.Params.GetValueOrDefault("hcDp"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hdp) ? hdp : 1.2;
                        double hcMinDist = double.TryParse(node.Params.GetValueOrDefault("hcMinDist"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hmd) ? hmd : 40.0;
                        double hcP1 = double.TryParse(node.Params.GetValueOrDefault("hcParam1"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hp1) ? hp1 : 100.0;
                        double hcP2 = double.TryParse(node.Params.GetValueOrDefault("hcParam2"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hp2) ? hp2 : 30.0;
                        int hcMinR = int.TryParse(node.Params.GetValueOrDefault("hcMinRadius"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hminr) ? hminr : 5;
                        int hcMaxR = int.TryParse(node.Params.GetValueOrDefault("hcMaxRadius"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hmaxr) ? hmaxr : 200;
                        var overlay = CalibAPI.HoughCirclesOverlay(hImg, out var circlePts, out var circlesJson, blurK, hcDp, hcMinDist, hcP1, hcP2, hcMinR, hcMaxR);
                        node.Outputs["Out"] = overlay;
                        node.Outputs["CirclePoints"] = circlePts;
                        node.Outputs["CircleCount"] = circlePts.Length;
                        node.Outputs["CirclesJson"] = circlesJson;
                        node.ResultSummary = $"HoughCircles: {circlePts.Length}";
                        break;
                    }

                    case "hough_lines":
                    {
                        CalibImage? hImg = inputs.TryGetValue("Edge", out var edgeIn) ? edgeIn as CalibImage : null;
                        if (hImg == null && inputs.TryGetValue("Image", out var legacyImg))
                            hImg = legacyImg as CalibImage;
                        if (hImg == null) throw new InvalidOperationException("霍夫线段: 缺少边缘输入端口 Edge（或兼容旧连线 Image）");
                        double hlRho = double.TryParse(node.Params.GetValueOrDefault("hlRho"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hrh) ? hrh : 1.0;
                        double hlThetaDeg = double.TryParse(node.Params.GetValueOrDefault("hlThetaDeg"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hthd) ? hthd : 1.0;
                        int hlTh = int.TryParse(node.Params.GetValueOrDefault("hlThreshold"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hlt) ? hlt : 50;
                        double hlMinLen = double.TryParse(node.Params.GetValueOrDefault("hlMinLineLength"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hlml) ? hlml : 40.0;
                        double hlMaxGap = double.TryParse(node.Params.GetValueOrDefault("hlMaxLineGap"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hlmg) ? hlmg : 15.0;
                        int maxLines = int.TryParse(node.Params.GetValueOrDefault("maxLinesOut"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var mlx) ? mlx : 400;
                        int covHalfW = int.TryParse(node.Params.GetValueOrDefault("hlCoverageHalfWidthPx"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var chw) ? chw : 0;
                        if (covHalfW < 0) covHalfW = 0;
                        var overlay = CalibAPI.HoughLinesOverlay(hImg, out var linesJson, out var lineCount,
                            hlRho, hlThetaDeg, hlTh, hlMinLen, hlMaxGap, maxLines, covHalfW);
                        node.Outputs["Out"] = overlay;
                        node.Outputs["LinesJson"] = linesJson;
                        node.Outputs["LineCount"] = lineCount;
                        node.ResultSummary = $"HoughLines: n={lineCount}, json={linesJson.Length}B";
                        break;
                    }

                    case "lines_nms":
                    {
                        if (!inputs.TryGetValue("LinesJson", out var ljObj) || ljObj is not string linesJson)
                            throw new InvalidOperationException("线段非极大值抑制: 缺少 LinesJson");
                        double angleTol = double.TryParse(node.Params.GetValueOrDefault("angleTolDeg"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var atd) ? atd : 5.0;
                        double rhoTol = double.TryParse(node.Params.GetValueOrDefault("rhoTolPx"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rtp) ? rtp : 10.0;
                        var segs = ParseHoughLinesJsonToSegs(linesJson);
                        var filtered = HoughLinesJsonNonMaxSuppression(segs, angleTol, rhoTol);
                        node.Outputs["LinesJson"] = FormatHoughLinesJson(filtered);
                        node.Outputs["LineCount"] = filtered.Count;
                        node.ResultSummary = $"LinesNMS: n={filtered.Count}";
                        break;
                    }

                    case "lines_threshold":
                    {
                        if (!inputs.TryGetValue("LinesJson", out var ljObj2) || ljObj2 is not string linesJson2)
                            throw new InvalidOperationException("线段阈值筛选: 缺少 LinesJson");
                        double minLen = double.TryParse(node.Params.GetValueOrDefault("minLengthPx"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mn) ? mn : 0.0;
                        double maxLen = double.TryParse(node.Params.GetValueOrDefault("maxLengthPx"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mx) ? mx : 0.0;
                        var segs2 = ParseHoughLinesJsonToSegs(linesJson2);
                        var filtered2 = HoughLinesJsonLengthThreshold(segs2, minLen, maxLen);
                        node.Outputs["LinesJson"] = FormatHoughLinesJson(filtered2);
                        node.Outputs["LineCount"] = filtered2.Count;
                        node.ResultSummary = $"LinesThresh: n={filtered2.Count}";
                        break;
                    }

                    case "hough_runway":
                    {
                        var hImg = inputs["Image"] as CalibImage;
                        if (hImg == null) throw new InvalidOperationException("霍夫跑道形: 缺少输入图像 Image");
                        int blurK = int.TryParse(node.Params.GetValueOrDefault("blurKsize"), out var bk) ? bk : 9;
                        if (blurK >= 3 && (blurK & 1) == 0) blurK |= 1;
                        double c1 = double.TryParse(node.Params.GetValueOrDefault("cannyTh1"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ct1) ? ct1 : 50.0;
                        double c2 = double.TryParse(node.Params.GetValueOrDefault("cannyTh2"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ct2) ? ct2 : 150.0;
                        double hlRho = double.TryParse(node.Params.GetValueOrDefault("hlRho"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hrh) ? hrh : 1.0;
                        double hlThetaDeg = double.TryParse(node.Params.GetValueOrDefault("hlThetaDeg"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hthd) ? hthd : 1.0;
                        int hlTh = int.TryParse(node.Params.GetValueOrDefault("hlThreshold"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hlt) ? hlt : 50;
                        double hlMinLen = double.TryParse(node.Params.GetValueOrDefault("hlMinLineLength"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hlml) ? hlml : 40.0;
                        double hlMaxGap = double.TryParse(node.Params.GetValueOrDefault("hlMaxLineGap"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hlmg) ? hlmg : 15.0;
                        int maxLines = int.TryParse(node.Params.GetValueOrDefault("maxLinesOut"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var mlx) ? mlx : 400;
                        double rwAng = double.TryParse(node.Params.GetValueOrDefault("runwayAngleTolDeg"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rwa) ? rwa : 10.0;
                        int rwRho = int.TryParse(node.Params.GetValueOrDefault("runwayRhoBinPx"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var rwr) ? rwr : 25;
                        int rwStrips = int.TryParse(node.Params.GetValueOrDefault("runwayStripCount"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var rws) ? rws : 2;
                        int maxRw = int.TryParse(node.Params.GetValueOrDefault("maxRunwayLinesOut"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var rwm) ? rwm : 120;
                        string rwShape = (node.Params.GetValueOrDefault("runwayShape", "parallel") ?? "parallel").Trim();
                        int shapeMode = string.Equals(rwShape, "stadium", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                        double hcDp = double.TryParse(node.Params.GetValueOrDefault("hcDp"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hdp) ? hdp : 1.2;
                        double hcMinDist = double.TryParse(node.Params.GetValueOrDefault("hcMinDist"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hmd) ? hmd : 40.0;
                        double hcP1 = double.TryParse(node.Params.GetValueOrDefault("hcParam1"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hp1) ? hp1 : 100.0;
                        double hcP2 = double.TryParse(node.Params.GetValueOrDefault("hcParam2"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hp2) ? hp2 : 30.0;
                        int hcMinR = int.TryParse(node.Params.GetValueOrDefault("hcMinRadius"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hminr) ? hminr : 5;
                        int hcMaxR = int.TryParse(node.Params.GetValueOrDefault("hcMaxRadius"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hmaxr) ? hmaxr : 200;
                        string? linesJsonIn = null;
                        if (inputs.TryGetValue("LinesJson", out var ljObj) && ljObj is string ljStr && !string.IsNullOrWhiteSpace(ljStr))
                            linesJsonIn = ljStr;
                        string? circlesJsonIn = null;
                        if (inputs.TryGetValue("CirclesJson", out var cjObj) && cjObj is string cjStr && !string.IsNullOrWhiteSpace(cjStr))
                            circlesJsonIn = cjStr;
                        var overlay = CalibAPI.HoughRunwayOverlay(hImg, out var runwayLinesJson, out var runwayLineCount,
                            blurK, c1, c2, hlRho, hlThetaDeg, hlTh, hlMinLen, hlMaxGap, maxLines,
                            rwAng, rwRho, rwStrips, maxRw, shapeMode, hcDp, hcMinDist, hcP1, hcP2, hcMinR, hcMaxR,
                            linesJsonIn, circlesJsonIn);
                        node.Outputs["Out"] = overlay;
                        node.Outputs["RunwayLinesJson"] = runwayLinesJson;
                        node.Outputs["RunwayLineCount"] = runwayLineCount;
                        node.ResultSummary = shapeMode != 0
                            ? $"HoughRunway(stadium): prim={runwayLineCount}"
                            : $"HoughRunway: seg={runwayLineCount}";
                        break;
                    }

                    case "sam_onnx_segment":
                    {
                        var samImg = inputs["Image"] as CalibImage;
                        if (samImg == null) throw new InvalidOperationException("SAM 分割: 缺少输入图像 Image");
                        Point2D[]? promptPts = null;
                        if (inputs.TryGetValue("Points", out var ptObj) && ptObj is Point2D[] arr && arr.Length > 0)
                            promptPts = arr;
                        double fx = double.TryParse(node.Params.GetValueOrDefault("clickX"), out var cxx) ? cxx : 512;
                        double fy = double.TryParse(node.Params.GetValueOrDefault("clickY"), out var cyy) ? cyy : 512;
                        float th = float.TryParse(node.Params.GetValueOrDefault("maskThreshold"), out var thv) ? thv : 0f;
                        int maskMergeMax = int.TryParse(
                            node.Params.GetValueOrDefault("maskMergeMax"),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var mmx)
                            ? Math.Clamp(mmx, 1, FlowSamMaskMergeParamUpperBound)
                            : 4;
                        bool useGpu = bool.TryParse(node.Params.GetValueOrDefault("useGpu"), out var ug) && ug;
                        string enc = node.Params.GetValueOrDefault("encoderPath", "") ?? "";
                        string dec = node.Params.GetValueOrDefault("decoderPath", "") ?? "";
                        string encAbs = SamOnnxSegmentation.ResolveModelPath(string.IsNullOrWhiteSpace(enc) ? SamOnnxSegmentation.DefaultEncoderRepoRelative : enc, flowBaseDir);
                        string decAbs = SamOnnxSegmentation.ResolveModelPath(string.IsNullOrWhiteSpace(dec) ? SamOnnxSegmentation.DefaultDecoderRepoRelative : dec, flowBaseDir);

                        SamOnnxSegmentation.OrigBoxPrompt? boxOrig = null;
                        IReadOnlyList<Owlv2OnnxTextToBox.Owlv2Detection>? textDetections = null;
                        string groundingJson;
                        string textPrompt = (node.Params.GetValueOrDefault("textPrompt", "") ?? "").Trim();
                        if (textPrompt.Length > 0)
                        {
                            double tthr = double.TryParse(
                                node.Params.GetValueOrDefault("textThreshold"),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var tt)
                                ? tt
                                : 0.25;
                            int maxDet = int.TryParse(
                                node.Params.GetValueOrDefault("textMaxDetections"),
                                System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var md)
                                ? Math.Clamp(md, 1, FlowSamTextMaxDetectionsUpperBound)
                                : 16;
                            double nmsIou = double.TryParse(
                                node.Params.GetValueOrDefault("textNmsIou"),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var ni)
                                ? Math.Clamp(ni, 0.0, 1.0)
                                : 0.5;
                            string owlv2OnnxRel = (node.Params.GetValueOrDefault("owlv2OnnxPath", "") ?? "").Trim();
                            if (string.IsNullOrEmpty(owlv2OnnxRel))
                                owlv2OnnxRel = Owlv2OnnxTextToBox.DefaultOnnxRepoRelative;
                            string tokRel = (node.Params.GetValueOrDefault("owlv2TokenizerJson", "") ?? "").Trim();
                            if (string.IsNullOrEmpty(tokRel))
                                tokRel = Owlv2OnnxTextToBox.DefaultTokenizerJsonRelative;
                            string onnxAbs = SamOnnxSegmentation.ResolveModelPath(owlv2OnnxRel, flowBaseDir);
                            string tokenizerAbs = SamOnnxSegmentation.ResolveModelPath(tokRel, flowBaseDir);
                            bool rawQ = bool.TryParse(node.Params.GetValueOrDefault("textRawQuery"), out var trq) && trq;
                            try
                            {
                                textDetections = Owlv2OnnxTextToBox.QueryDetectionsOrThrow(
                                    samImg,
                                    textPrompt,
                                    tthr,
                                    onnxAbs,
                                    tokenizerAbs,
                                    rawQ,
                                    useGpu,
                                    maxDet,
                                    nmsIou);
                            }
                            catch (Exception ex)
                            {
                                void ShowGroundingAlert()
                                {
                                    MessageBox.Show(
                                        "文本 grounding 失败：\n\n" + ex.Message +
                                        "\n\n提示：请确认已放置 OWLv2 ONNX 与 CLIP tokenizer.json（见 models/onnx/README）；文本长度上限 16 token；" +
                                        "可缩短描述、开启「文本不加前缀」，或调低 textThreshold / 放宽 textNmsIou。",
                                        "SAM 图像分割",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Warning);
                                }

                                if (Dispatcher.CheckAccess())
                                    ShowGroundingAlert();
                                else
                                    Dispatcher.Invoke(ShowGroundingAlert);
                                throw new FlowExecutionGracefulStopException("文本 grounding 失败，流程已中止。", ex);
                            }

                            boxOrig = textDetections.Count == 1 ? textDetections[0].Box : null;
                            var first = textDetections[0];
                            groundingJson = JsonSerializer.Serialize(new
                            {
                                mode = "text",
                                query = textPrompt,
                                maxDetections = maxDet,
                                nmsIou,
                                detectionCount = textDetections.Count,
                                score = first.Score,
                                box = new { x1 = first.Box.X1, y1 = first.Box.Y1, x2 = first.Box.X2, y2 = first.Box.Y2 },
                                detections = textDetections.Select(d => new
                                {
                                    patchIndex = d.PatchIndex,
                                    score = d.Score,
                                    box = new { x1 = d.Box.X1, y1 = d.Box.Y1, x2 = d.Box.X2, y2 = d.Box.Y2 },
                                }).ToArray(),
                            });
                        }
                        else if (promptPts != null && promptPts.Length > 0)
                        {
                            groundingJson = JsonSerializer.Serialize(new { mode = "points", count = promptPts.Length });
                        }
                        else
                        {
                            groundingJson = JsonSerializer.Serialize(new { mode = "fallback_click", x = fx, y = fy });
                        }

                        SamOnnxSegmentation.Result seg;
                        if (textPrompt.Length > 0 && textDetections != null)
                        {
                            if (textDetections.Count == 1)
                                seg = SamOnnxSegmentation.Run(samImg, promptPts, fx, fy, encAbs, decAbs, th, useGpu, boxOrig);
                            else
                            {
                                var boxList = textDetections.Select(d => d.Box).ToList();
                                seg = SamOnnxSegmentation.RunMultiBox(
                                    samImg,
                                    boxList,
                                    encAbs,
                                    decAbs,
                                    th,
                                    useGpu,
                                    maxInstances: Math.Min(boxList.Count, maskMergeMax));
                            }
                        }
                        else
                            seg = SamOnnxSegmentation.Run(samImg, promptPts, fx, fy, encAbs, decAbs, th, useGpu, boxOrig);
                        node.Outputs["Mask"] = seg.Mask;
                        if (seg.Mask2 != null) node.Outputs["Mask2"] = seg.Mask2;
                        if (seg.Mask3 != null) node.Outputs["Mask3"] = seg.Mask3;
                        if (seg.Mask4 != null) node.Outputs["Mask4"] = seg.Mask4;
                        node.Outputs["MaskAll"] = SamOnnxSegmentation.MergeMaskOutputsUnion(seg, maskMergeMax);
                        node.Outputs["Vis"] = seg.Vis;
                        node.Outputs["GroundingJson"] = groundingJson;
                        node.ResultSummary = textPrompt.Length > 0 && textDetections != null
                            ? (textDetections.Count > 1
                                ? $"SAM 文本「{textPrompt}」OWLv2×{textDetections.Count}，解码×{seg.MaskUnionSources?.Count ?? 0}（MaskAll≤{maskMergeMax}），首实例 IoU≈{seg.IouPrediction:F3}"
                                : $"SAM 文本→框「{textPrompt}」，SAM 掩码候选×{seg.MaskCandidateCount}，最佳 IoU≈{seg.IouPrediction:F3}")
                            : $"SAM 候选×{seg.MaskCandidateCount}，最佳 IoU≈{seg.IouPrediction:F3}";
                        break;
                    }

                    case "chessboard_find_corners":
                    {
                        var chessImg = inputs["Image"] as CalibImage;
                        if (chessImg == null) throw new InvalidOperationException("棋盘格角点: 缺少输入图像");
                        int cols = int.TryParse(node.Params.GetValueOrDefault("cols"), out int cc) ? cc : 9;
                        int rows = int.TryParse(node.Params.GetValueOrDefault("rows"), out int rr) ? rr : 6;
                        bool refine = bool.TryParse(node.Params.GetValueOrDefault("refine"), out bool rv) ? rv : true;
                        bool fast = bool.TryParse(node.Params.GetValueOrDefault("fastCheck"), out bool fv) ? fv : true;
                        var cbPts = CalibAPI.FindChessboardCorners(chessImg, cols, rows, refine, fast);
                        node.Outputs["Points"] = cbPts;
                        node.Outputs["Found"] = cbPts.Length > 0;
                        var vis = CalibAPI.DuplicateImage(chessImg);
                        if (cbPts.Length > 0)
                            CalibAPI.DrawChessboardCorners(vis, cbPts, cols, rows);
                        node.Outputs["Vis"] = vis;
                        break;
                    }

                    case "chessboard_calibrate_intrinsics":
                    {
                        int colsI = int.TryParse(node.Params.GetValueOrDefault("cols"), out int ci) ? ci : 9;
                        int rowsI = int.TryParse(node.Params.GetValueOrDefault("rows"), out int ri) ? ri : 6;
                        double sqMm = double.TryParse(node.Params.GetValueOrDefault("squareSizeMm"), out double sqv) ? sqv : 25.0;
                        var resolved = ResolveChessboardCalibrationImagePaths(node, compositeInnerFlowBaseDir);
                        if (resolved.Count == 0)
                            throw new InvalidOperationException("棋盘格内参: 请设置 imageDirectory（如含 Image_*.bmp 的文件夹）或 imagePaths（分号分隔路径）");
                        string pathsJoined = string.Join(";", resolved);
                        var (intr, calJson) = CalibAPI.CalibrateCameraChessboard(pathsJoined, colsI, rowsI, sqMm);
                        node.Outputs["Intrinsics"] = intr;
                        node.Outputs["IntrinsicsJson"] = JsonSerializer.Serialize(intr, new JsonSerializerOptions { IncludeFields = true });
                        node.Outputs["CalibrationJson"] = calJson;
                        break;
                    }

                    case "chessboard_pixels_to_world":
                    {
                        var pts = inputs["Points"] as Point2D[];
                        if (pts == null)
                            throw new InvalidOperationException("棋盘像素→世界: 缺少像素点列 Points（轨迹或角点）");
                        if (pts.Length == 0)
                        {
                            node.Outputs["World"] = Array.Empty<Point2D>();
                            node.ResultSummary = "skip: 0 pts → empty World";
                            break;
                        }

                        if (!inputs.TryGetValue("CalibrationJson", out var cjObj) || cjObj is not string calJson || string.IsNullOrWhiteSpace(calJson))
                            throw new InvalidOperationException("棋盘像素→世界: 缺少 CalibrationJson（须为多视图标定输出的完整 JSON）");
                        int viewIdx = int.TryParse(node.Params.GetValueOrDefault("viewIndex"), out int vi) ? vi : 0;
                        var world = CalibAPI.PixelsToChessboardWorld(calJson, viewIdx, pts);
                        node.Outputs["World"] = world;
                        break;
                    }

                    case "calibration_correct_image":
                    {
                        if (inputs["Image"] is not CalibImage srcImg)
                            throw new InvalidOperationException("标定图像矫正: 缺少 Image");
                        bool undistort = ParseFlowBoolParam(node.Params, "enableUndistort");
                        bool perspective = ParseFlowBoolParam(node.Params, "enablePerspective");
                        if (!undistort && !perspective)
                        {
                            node.Outputs["Out"] = srcImg;
                            node.ResultSummary = "未启用矫正（透传）";
                            break;
                        }

                        var corrected = ApplyOptionalCameraCorrection(node, inputs, srcImg, compositeInnerFlowBaseDir);
                        node.Outputs["Out"] = corrected;
                        node.ResultSummary = undistort && perspective
                            ? "undistort + perspective"
                            : undistort
                                ? "undistort"
                                : "perspective";
                        break;
                    }

                    case "intrinsics_undistort_image":
                    {
                        var srcImg = inputs["Image"] as CalibImage;
                        if (srcImg == null)
                            throw new InvalidOperationException("内参畸变矫正: 缺少输入图像 Image");
                        double alpha = double.TryParse(node.Params.GetValueOrDefault("alpha"), out double av) ? av : -1.0;

                        CalibImage outImg;
                        if (inputs.TryGetValue("Intrinsics", out var intrObj) && intrObj is CameraIntrinsics intr)
                            outImg = CalibAPI.UndistortImage(srcImg, intr, alpha);
                        else
                        {
                            string calJson = ResolveChessboardCalibrationJson(node, inputs, compositeInnerFlowBaseDir, requireExtrinsics: false);
                            outImg = CalibAPI.UndistortImage(srcImg, calJson, alpha);
                        }
                        CalibAPI.EnsureNativeImageChannels(outImg);

                        node.Outputs["Out"] = outImg;
                        node.ResultSummary = $"undistort {srcImg.Width}x{srcImg.Height} alpha={alpha}";
                        break;
                    }

                    case "chessboard_perspective_warp_image":
                    {
                        var srcImg = inputs["Image"] as CalibImage;
                        if (srcImg == null)
                            throw new InvalidOperationException("棋盘透视展开: 缺少输入图像 Image");
                        string calJson = ResolveChessboardCalibrationJson(node, inputs, compositeInnerFlowBaseDir, requireExtrinsics: true);

                        int viewIdx = int.TryParse(node.Params.GetValueOrDefault("viewIndex"), out int vi) ? vi : 0;
                        int cols = int.TryParse(node.Params.GetValueOrDefault("cols"), out int cc) ? cc : 9;
                        int rows = int.TryParse(node.Params.GetValueOrDefault("rows"), out int rr) ? rr : 6;
                        double sqMm = double.TryParse(node.Params.GetValueOrDefault("squareSizeMm"), out double sqv) ? sqv : 25.0;
                        double pxPerMm = double.TryParse(node.Params.GetValueOrDefault("pxPerMm"), out double ppm) ? ppm : 1.0;
                        int perspMode = ParsePerspectiveOutputMode(node.Params);
                        int outScale = ParsePerspectiveOutputScale(node.Params);
                        bool assumeUnd = ParseAssumeUndistortedForWarp(node.Params, undistortEnabledInSameNode: false);

                        var outImg = CalibAPI.WarpToChessboardPlane(srcImg, calJson, viewIdx, cols, rows, sqMm, pxPerMm, perspMode, assumeUnd, outScale);
                        CalibAPI.EnsureNativeImageChannels(outImg);
                        node.Outputs["Out"] = outImg;
                        node.ResultSummary = $"perspective warp view={viewIdx} {srcImg.Width}x{srcImg.Height} → {outImg.Width}x{outImg.Height}";
                        break;
                    }

                    case "calibrate":
                    {
                        var calibImage = inputs.TryGetValue("Image", out var imgObj) ? imgObj as CalibImage : null;
                        var worldPts = ResolveCalibrateWorldPoints(node, compositeInnerFlowBaseDir);
                        bool manualPick = CalibrateUsesManualPixelPick(node);
                        bool needDialog = CalibrateNeedsCorrespondenceDialog(
                            node,
                            inputs.TryGetValue("ImagePts", out var ipObj) ? ipObj as Point2D[] : null,
                            worldPts.Length);
                        Point2D[] alignedImagePts = ResolveCalibrateAlignedImagePoints(
                            node, inputs, worldPts, calibImage, "标定");

                        // 标定计算与下游输出均使用配对后的像素（手选或确认后的 alignedImagePts[i] ↔ worldPts[i]）
                        var calResult = CalibAPI.CalibrateNinePoint(alignedImagePts, worldPts);
                        if (!calResult.Success)
                            throw new InvalidOperationException($"标定失败: {calResult.ErrorMessage}");
                        node.Outputs["Transform"] = calResult.Transform;
                        node.Outputs["ImagePts"] = alignedImagePts.ToArray();

                        string verifyRaw = node.Params.GetValueOrDefault("showVerifyPreview", "true") ?? "true";
                        bool showVerify = !string.Equals(verifyRaw.Trim(), "false", StringComparison.OrdinalIgnoreCase)
                            && verifyRaw.Trim() != "0";
                        if (showVerify && calibImage != null)
                        {
                            var (gridRows, gridCols) = CalibrationPointGrid.InferLayout(alignedImagePts.Length, worldPts);
                            ShowImagePreview(
                                calibImage,
                                alignedImagePts,
                                6,
                                null,
                                node.Id.ToString("D"),
                                $"九点标定验证 · {node.Def.DisplayName}",
                                null,
                                "grid",
                                null);
                        }

                        string errNote = calResult.AverageError > 0
                            ? $" avgErr={calResult.AverageError:F3}mm max={calResult.MaxError:F3}mm"
                            : "";
                        node.ResultSummary = needDialog
                            ? (manualPick
                                ? $"标定 OK（{alignedImagePts.Length} 对点，手选像素{errNote}）"
                                : $"标定 OK（{alignedImagePts.Length} 对点，图像确认{errNote}）")
                            : $"标定 OK（{alignedImagePts.Length} 对点{errNote}）";
                        break;
                    }

                    case "img_to_world":
                    {
                        var pixelPts = inputs.TryGetValue("Pixel", out var pxObj) ? pxObj as Point2D[] : null;
                        var transform = inputs.TryGetValue("Transform", out var trObj)
                            ? CoerceAffineTransform(trObj)
                            : null;
                        if (pixelPts == null || transform == null)
                            throw new InvalidOperationException(
                                "坐标转换: 缺少输入点或变换矩阵（请检查「读取标定结果」是否在子流程内输出 Transform，且 calibration_result.json 含 affine）");
                        if (pixelPts.Length == 0)
                        {
                            node.Outputs["World"] = Array.Empty<Point2D>();
                            node.ResultSummary = "skip: 0 pts → empty World";
                            break;
                        }

                        CalibAPI.SetTransform(transform.Value);
                        var worldPts2 = pixelPts.Select(p => CalibAPI.ImageToWorld(p, transform.Value)).ToArray();
                        node.Outputs["World"] = worldPts2;
                        break;
                    }

                    case "calibrate_homography":
                    {
                        var calibImageH = inputs.TryGetValue("Image", out var imgHObj) ? imgHObj as CalibImage : null;
                        bool mapsToWorldH = ParseHomographyTargetSpaceParam(node, defaultImage: true);
                        var targetPtsH = ResolveCalibrateWorldPointsForNode(node, inputs, compositeInnerFlowBaseDir);
                        if (targetPtsH.Length < 4)
                            throw new InvalidOperationException("透视标定: 至少需要4个目标坐标点");

                        bool manualPickH = CalibrateUsesManualPixelPick(node);
                        bool needDialogH = CalibrateNeedsCorrespondenceDialog(
                            node,
                            inputs.TryGetValue("ImagePts", out var ipHObj) ? ipHObj as Point2D[] : null,
                            targetPtsH.Length);
                        Point2D[] alignedImagePtsH = ResolveCalibrateAlignedImagePoints(
                            node, inputs, targetPtsH, calibImageH, "透视标定");

                        var h = FitHomography(alignedImagePtsH, targetPtsH);
                        h.TargetSpace = mapsToWorldH ? "world" : "image";
                        node.Outputs["H"] = h;
                        node.Outputs["ImagePts"] = alignedImagePtsH.ToArray();

                        var (avgErrH, maxErrH) = MeasureHomographyReprojection(alignedImagePtsH, targetPtsH, h);
                        string errUnitH = mapsToWorldH ? "mm" : "px";
                        string errNoteH = $" avgErr={avgErrH:F3}{errUnitH} max={maxErrH:F3}{errUnitH}";
                        string spaceNoteH = mapsToWorldH ? "world" : "image";

                        string verifyRawH = node.Params.GetValueOrDefault("showVerifyPreview", "true") ?? "true";
                        bool showVerifyH = !string.Equals(verifyRawH.Trim(), "false", StringComparison.OrdinalIgnoreCase)
                            && verifyRawH.Trim() != "0";
                        if (showVerifyH && calibImageH != null)
                        {
                            ShowImagePreview(
                                calibImageH,
                                alignedImagePtsH,
                                6,
                                null,
                                node.Id.ToString("D"),
                                $"透视标定验证 · {node.Def.DisplayName}",
                                null,
                                "grid",
                                null);
                        }

                        node.ResultSummary = needDialogH
                            ? (manualPickH
                                ? $"透视标定 OK [{spaceNoteH}]（{alignedImagePtsH.Length} 对，手选{errNoteH}）"
                                : $"透视标定 OK [{spaceNoteH}]（{alignedImagePtsH.Length} 对，确认{errNoteH}）")
                            : $"透视标定 OK [{spaceNoteH}]（{alignedImagePtsH.Length} 对{errNoteH}）";
                        break;
                    }

                    case "img_to_world_homography":
                    {
                        var pixelPts = inputs["Pixel"] as Point2D[];
                        if (!inputs.TryGetValue("H", out var hObj) || hObj is not HomographyTransform h)
                            throw new InvalidOperationException("坐标转换(H): 缺少H矩阵");
                        if (pixelPts == null)
                            throw new InvalidOperationException("坐标转换(H): 缺少输入点");
                        if (pixelPts.Length == 0)
                        {
                            node.Outputs["Mapped"] = Array.Empty<Point2D>();
                            node.Outputs["World"] = Array.Empty<Point2D>();
                            node.ResultSummary = "skip: 0 pts";
                            break;
                        }

                        bool mapsToWorldApply = ResolveHomographyApplyMapsToWorld(node, h);
                        var mapped = pixelPts.Select(p => ApplyHomography(p, h)).ToArray();
                        node.Outputs["Mapped"] = mapped;
                        if (mapsToWorldApply)
                        {
                            node.Outputs["World"] = mapped;
                            node.ResultSummary = $"world x{mapped.Length}";
                        }
                        else
                        {
                            node.Outputs["World"] = Array.Empty<Point2D>();
                            node.ResultSummary = $"image(px) x{mapped.Length} → 请接 Mapped 端口";
                        }
                        break;
                    }

                    case "calibrate_poly2d":
                    {
                        var imagePts = inputs["ImagePts"] as Point2D[];
                        var worldPts = inputs["WorldPts"] as Point2D[];
                        if (imagePts == null || worldPts == null)
                            throw new InvalidOperationException("Poly2D标定: 缺少图像点或世界坐标点");
                        if (imagePts.Length != worldPts.Length)
                            throw new InvalidOperationException("Poly2D标定: 图像点和世界点数量不一致");
                        if (imagePts.Length < 6)
                            throw new InvalidOperationException("Poly2D标定: 至少需要6个点");
                        var poly = FitPoly2D(imagePts, worldPts);
                        node.Outputs["Poly"] = poly;
                        break;
                    }

                    case "img_to_world_poly2d":
                    {
                        var pixelPts = inputs["Pixel"] as Point2D[];
                        if (!inputs.TryGetValue("Poly", out var polyObj) || polyObj is not Poly2DTransform poly)
                            throw new InvalidOperationException("坐标转换(Poly2D): 缺少Poly参数");
                        if (pixelPts == null)
                            throw new InvalidOperationException("坐标转换(Poly2D): 缺少输入点");
                        if (pixelPts.Length == 0)
                        {
                            node.Outputs["World"] = Array.Empty<Point2D>();
                            node.ResultSummary = "skip: 0 pts → empty World";
                            break;
                        }

                        node.Outputs["World"] = pixelPts.Select(p => ApplyPoly2D(p, poly)).ToArray();
                        break;
                    }

                    case "plane_to_base_handeye":
                    {
                        if (!inputs.TryGetValue("Points", out var plObj) || plObj is not Point2D[] planePts)
                            throw new InvalidOperationException("平面→基座(手眼): 缺少 Points（Point2D[]）。");
                        if (planePts.Length == 0)
                        {
                            node.Outputs["Points3D"] = Array.Empty<CalibPoint3D>();
                            node.ResultSummary = "skip: 0 pts → empty Points3D";
                            break;
                        }

                        if (!double.TryParse(
                                node.Params.GetValueOrDefault("planeZ", "0"),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var planeZ))
                            planeZ = 0.0;

                        string jsonText;
                        if (inputs.TryGetValue("HandEyeJson", out var hjObj) && hjObj is string hj && !string.IsNullOrWhiteSpace(hj))
                            jsonText = hj.Trim();
                        else
                        {
                            var pathParam = node.Params.GetValueOrDefault("filePath", "handeye_plane_to_base.json")?.Trim() ?? "handeye_plane_to_base.json";
                            var resolved = ResolveCompositeFlowPath(pathParam, compositeInnerFlowBaseDir);
                            if (!System.IO.File.Exists(resolved))
                                throw new System.IO.FileNotFoundException($"平面→基座(手眼): JSON 文件不存在: {resolved}");
                            jsonText = System.IO.File.ReadAllText(resolved, Encoding.UTF8);
                        }

                        double[] m16;
                        try
                        {
                            m16 = HandEyePlaneToBaseTransform.ParseBaseFromPlaneMatrix16(jsonText);
                        }
                        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is ArgumentException)
                        {
                            throw new InvalidOperationException($"平面→基座(手眼): 解析手眼 JSON 失败 — {ex.Message}", ex);
                        }

                        var out3 = new CalibPoint3D[planePts.Length];
                        for (int i = 0; i < planePts.Length; i++)
                        {
                            var p = planePts[i];
                            out3[i] = HandEyePlaneToBaseTransform.TransformPoint(m16, p.X, p.Y, planeZ);
                        }

                        node.Outputs["Points3D"] = out3;
                        int[]? barPass = null;
                        if (inputs.TryGetValue("BarIds", out var barIn) && barIn is int[] barIds)
                            barPass = barIds;
                        else if (inputs.TryGetValue("GroupBarIds", out var gbIn) && gbIn is int[] groupBarIds)
                            barPass = groupBarIds;
                        if (barPass != null)
                        {
                            if (barPass.Length != planePts.Length)
                                throw new InvalidOperationException(
                                    $"平面→基座(手眼): BarIds/GroupBarIds 长度 {barPass.Length} 与 Points {planePts.Length} 不一致。");
                            node.Outputs["BarIds"] = (int[])barPass.Clone();
                        }

                        string barNote = node.Outputs.ContainsKey("BarIds") ? ", BarIds 已透传" : "";
                        node.ResultSummary = $"{planePts.Length} pts → base (planeZ={planeZ}){barNote}";
                        break;
                    }

                    case "display_calibration":
                    {
                        string text;
                        if (inputs.TryGetValue("CalibrationJson", out var calJsonObj) && calJsonObj is string cjs && !string.IsNullOrWhiteSpace(cjs))
                        {
                            text = FormatCalibrationFullSummary(cjs);
                        }
                        else if (inputs.TryGetValue("Transform", out var tObj) && tObj is AffineTransform t)
                        {
                            text = $"[Affine]\n{t}";
                        }
                        else if (inputs.TryGetValue("H", out var hObj) && hObj is HomographyTransform h)
                        {
                            text = $"[Homography]\n{FormatHomography(h)}";
                        }
                        else if (inputs.TryGetValue("Poly", out var pObj) && pObj is Poly2DTransform p)
                        {
                            text = $"[Poly2D]\n{FormatPoly2D(p)}";
                        }
                        else if (inputs.TryGetValue("Intrinsics", out var intrObj) && intrObj is CameraIntrinsics ci)
                        {
                            text = $"[相机内参 · 标定计算求解]\n{ci}";
                        }
                        else
                        {
                            throw new InvalidOperationException("显示标定结果: 未检测到 CalibrationJson / Transform / H / Poly / Intrinsics 输入");
                        }

                        node.Outputs["Out"] = text;
                        node.ResultSummary = text.Replace("\n", " | ");
                        AppendLog(text);
                        Console.WriteLine(text);
                        StatusText.Dispatcher.Invoke(() => { StatusText.Text = text.Replace("\n", "  "); });
                        break;
                    }

                    case "save_calibration_result":
                    {
                        var dto = new CalibrationResultFileV1 { SchemaVersion = 1 };
                        bool any = false;
                        if (inputs.TryGetValue("CalibrationJson", out var cjObj) && cjObj is string cjStr && !string.IsNullOrWhiteSpace(cjStr))
                        {
                            dto.CalibrationJson = cjStr;
                            any = true;
                        }
                        if (inputs.TryGetValue("Transform", out var affObj) && affObj is AffineTransform aff)
                        {
                            dto.Affine = AffineCalibrationSaveV1.From(aff);
                            any = true;
                        }
                        if (inputs.TryGetValue("H", out var hObj) && hObj is HomographyTransform hh)
                        {
                            dto.Homography = HomographyCalibrationSaveV1.From(hh);
                            any = true;
                        }
                        if (inputs.TryGetValue("Poly", out var polyObj) && polyObj is Poly2DTransform pp)
                        {
                            dto.Poly2d = Poly2DCalibrationSaveV1.From(pp);
                            any = true;
                        }
                        if (inputs.TryGetValue("Intrinsics", out var intrObj) && intrObj is CameraIntrinsics intr)
                        {
                            dto.Intrinsics = IntrinsicsCalibrationSaveV1.From(intr);
                            any = true;
                        }
                        if (!any)
                            throw new InvalidOperationException("保存标定结果: 请至少连接 CalibrationJson / Transform / H / Poly / Intrinsics 之一");

                        var pathParam = node.Params.GetValueOrDefault("filePath", "calibration_result.json");
                        if (string.IsNullOrWhiteSpace(pathParam))
                            pathParam = "calibration_result.json";
                        var resolvedPath = ResolveCompositeFlowPath(pathParam, compositeInnerFlowBaseDir);
                        var dir = System.IO.Path.GetDirectoryName(resolvedPath);
                        if (!string.IsNullOrWhiteSpace(dir))
                            System.IO.Directory.CreateDirectory(dir);

                        string outJson = JsonSerializer.Serialize(dto, CalibrationResultFileJsonOptions);
                        System.IO.File.WriteAllText(resolvedPath, outJson, Encoding.UTF8);
                        node.ResultSummary = $"标定结果已保存: {System.IO.Path.GetFileName(resolvedPath)}";
                        break;
                    }

                    case "load_calibration_result":
                    {
                        string configuredPath = node.Params.GetValueOrDefault("filePath", "")?.Trim() ?? "";
                        string? resolvedPath = null;
                        if (!string.IsNullOrWhiteSpace(configuredPath))
                            resolvedPath = ResolveCompositeFlowPath(configuredPath, compositeInnerFlowBaseDir);

                        if (string.IsNullOrWhiteSpace(resolvedPath))
                        {
                            var dlg = new OpenFileDialog
                            {
                                Filter = "JSON|*.json|所有文件|*.*",
                                Title = "选择标定结果 JSON"
                            };
                            if (dlg.ShowDialog() != true)
                            {
                                node.ErrorMessage = "用户取消";
                                break;
                            }
                            resolvedPath = dlg.FileName;
                            node.Params["filePath"] = FormatPathForFlowParam(dlg.FileName, compositeInnerFlowBaseDir);
                        }
                        else if (!System.IO.File.Exists(resolvedPath))
                            throw new System.IO.FileNotFoundException(
                                $"读取标定结果: 文件不存在: {resolvedPath}" +
                                (string.IsNullOrWhiteSpace(compositeInnerFlowBaseDir)
                                    ? ""
                                    : $"（相对路径基准: {compositeInnerFlowBaseDir}）"));

                        string raw = System.IO.File.ReadAllText(resolvedPath, Encoding.UTF8);
                        if (raw.Length > 0 && raw[0] == '\uFEFF')
                            raw = raw[1..];

                        // 棋盘格标定直接落盘的 OpenCV 整包 JSON（无 schemaVersion 包装）
                        if (raw.Contains("\"extrinsicsPerView\"", StringComparison.Ordinal)
                            && !raw.Contains("\"schemaVersion\"", StringComparison.Ordinal))
                        {
                            if (ShouldEmitCalibrationOutput(node, "CalibrationJson", compositeInnerFlowBaseDir))
                                node.Outputs["CalibrationJson"] = CalibAPI.NormalizeChessboardCalibrationJson(raw);
                            if (inputs.TryGetValue("After", out var afterChess))
                                node.Outputs["Out"] = afterChess;
                            node.ResultSummary = $"棋盘标定 JSON: {System.IO.Path.GetFileName(resolvedPath)}";
                            break;
                        }

                        CalibrationResultFileV1? dto;
                        try
                        {
                            dto = JsonSerializer.Deserialize<CalibrationResultFileV1>(raw, CalibrationResultFileJsonOptions);
                        }
                        catch (JsonException ex)
                        {
                            throw new InvalidOperationException($"读取标定结果: JSON 解析失败 — {ex.Message}");
                        }
                        if (dto == null || dto.SchemaVersion < 1)
                            throw new InvalidOperationException("读取标定结果: 无效的 schemaVersion（需要 >= 1）");

                        if (ShouldEmitCalibrationOutput(node, "CalibrationJson", compositeInnerFlowBaseDir))
                        {
                            if (string.IsNullOrWhiteSpace(dto.CalibrationJson))
                                throw new InvalidOperationException("读取标定结果: 文件不含 calibrationJson，请去掉该输出的连线或更换文件");
                            node.Outputs["CalibrationJson"] = CalibAPI.NormalizeChessboardCalibrationJson(dto.CalibrationJson);
                        }
                        if (ShouldEmitCalibrationOutput(node, "Transform", compositeInnerFlowBaseDir))
                        {
                            if (dto.Affine == null)
                                throw new InvalidOperationException("读取标定结果: 文件不含 affine，请去掉 Transform 输出连线或更换文件");
                            node.Outputs["Transform"] = dto.Affine.ToAffine();
                        }
                        if (ShouldEmitCalibrationOutput(node, "H", compositeInnerFlowBaseDir))
                        {
                            if (dto.Homography == null)
                                throw new InvalidOperationException("读取标定结果: 文件不含 homography，请去掉 H 输出连线或更换文件");
                            node.Outputs["H"] = dto.Homography.ToHomography();
                        }
                        if (ShouldEmitCalibrationOutput(node, "Poly", compositeInnerFlowBaseDir))
                        {
                            if (dto.Poly2d == null)
                                throw new InvalidOperationException("读取标定结果: 文件不含 poly2d，请去掉 Poly 输出连线或更换文件");
                            node.Outputs["Poly"] = dto.Poly2d.ToPoly();
                        }
                        if (ShouldEmitCalibrationOutput(node, "Intrinsics", compositeInnerFlowBaseDir))
                        {
                            if (dto.Intrinsics == null)
                                throw new InvalidOperationException("读取标定结果: 文件不含 intrinsics，请去掉 Intrinsics 输出连线或更换文件");
                            node.Outputs["Intrinsics"] = dto.Intrinsics.ToIntrinsics();
                        }

                        if (inputs.TryGetValue("After", out var afterLoad))
                            node.Outputs["Out"] = afterLoad;
                        node.ResultSummary = $"标定结果已加载: {System.IO.Path.GetFileName(resolvedPath)}";
                        break;
                    }

                    case "display":
                    {
                        // 显示图像到预览窗口：可选背景图 Img，Image 作为前景层，Points/PointsList 透明叠加；Xld 单独叠加折线
                        var foregroundImg = (inputs.TryGetValue("Image", out var fgObj) ? fgObj : null) as CalibImage;
                        var backgroundImg = (inputs.TryGetValue("Img", out var bgObj) ? bgObj : null) as CalibImage;
                        if (foregroundImg == null && backgroundImg == null)
                            throw new InvalidOperationException("显示: 缺少输入图像(Image 或 Img)");
                        inputs.TryGetValue("Points", out var ptsObj);
                        Point2D[]? singlePts = ptsObj switch
                        {
                            Point2D[] p2 => p2,
                            CalibPoint3D[] p3 => p3.Select(p => new Point2D(p.X, p.Y)).ToArray(),
                            _ => null
                        };
                        inputs.TryGetValue("PointsList", out var ptsListObj);
                        inputs.TryGetValue("BarIds", out var barIdsObj);
                        int[]? explicitBarIds = barIdsObj as int[];
                        var (overlayPts, overlayBarIds) = MergeDisplayPointOverlays(singlePts, ptsListObj, explicitBarIds);
                        inputs.TryGetValue("Xld", out var xldObj);
                        var xldBundle = xldObj as HalconXldContourBundle;
                        int dotRadius = int.TryParse(node.Params.GetValueOrDefault("dotRadius"), out int r) ? r : 3;
                        string dispSlot = node.Id.ToString("D");
                        string dispTitle = $"{node.Def.DisplayName} [{node.Id.ToString("N")[..8]}]";
                        string pointLineJoin = (node.Params.GetValueOrDefault("pointLineJoin", "auto") ?? "auto").Trim();
                        ShowImagePreview(foregroundImg, overlayPts, dotRadius, backgroundImg, dispSlot, dispTitle, overlayBarIds, pointLineJoin, xldBundle);
                        int polylineCount = CoerceToPoint2DPolylineList(ptsListObj).Count + (singlePts != null && singlePts.Length > 0 ? 1 : 0);
                        node.ResultSummary = overlayPts == null
                            ? "显示图像"
                            : polylineCount > 1
                                ? $"显示 {overlayPts.Length} 点 · {polylineCount} 条折线"
                                : $"显示 {overlayPts.Length} 点";
                        break;
                    }

                    case "display_3d_trajectory":
                    {
                        CalibPoint3D[]? path3 = null;
                        if (inputs.TryGetValue("Points3D", out var p3o) && p3o is CalibPoint3D[] a3 && a3.Length > 0)
                            path3 = a3;
                        else if (inputs.TryGetValue("Points", out var p2o) && p2o is Point2D[] p2 && p2.Length > 0)
                        {
                            double zDef = double.TryParse(
                                node.Params.GetValueOrDefault("zDefault", "0")?.Trim(),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var zdefv)
                                ? zdefv
                                : 0;
                            inputs.TryGetValue("Z", out var zObj);
                            if (zObj is double[] zz && zz.Length == p2.Length)
                                path3 = p2.Select((t, i) => new CalibPoint3D(t.X, t.Y, zz[i])).ToArray();
                            else if (zObj is float[] zf && zf.Length == p2.Length)
                                path3 = p2.Select((t, i) => new CalibPoint3D(t.X, t.Y, zf[i])).ToArray();
                            else
                                path3 = p2.Select(t => new CalibPoint3D(t.X, t.Y, zDef)).ToArray();
                        }

                        if (path3 == null || path3.Length == 0)
                        {
                            node.Outputs["Out"] = Array.Empty<CalibPoint3D>();
                            node.ResultSummary = "skip: 无 Points3D 或 Points → 空 Out";
                            break;
                        }

                        double tubeDiameter = double.TryParse(
                            node.Params.GetValueOrDefault("tubeDiameter", "0.8")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var td)
                            ? td
                            : 0.8;
                        bool showGrid = !string.Equals(
                            (node.Params.GetValueOrDefault("showGrid", "true") ?? "true").Trim(),
                            "false",
                            StringComparison.OrdinalIgnoreCase);
                        double gridExtent = double.TryParse(
                            node.Params.GetValueOrDefault("gridExtent", "200")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var ge)
                            ? ge
                            : 200;
                        string slot3d = node.Id.ToString("D");
                        string title3d = $"{node.Def.DisplayName} [{node.Id.ToString("N")[..8]}]";
                        ShowTrajectory3DPreview(path3, slot3d, title3d, tubeDiameter, showGrid, gridExtent);
                        node.Outputs["Out"] = path3;
                        node.ResultSummary = $"3D 预览 · {path3.Length} 点";
                        break;
                    }

                    case "save_image":
                    {
                        var inputImg = TryResolveCalibImageInput(inputs)
                            ?? throw new InvalidOperationException("保存图像: 缺少输入图像");

                        var pathParam = node.Params.GetValueOrDefault("filePath", "flow_output.bmp");
                        if (string.IsNullOrWhiteSpace(pathParam))
                            pathParam = "flow_output.bmp";
                        var resolvedPath = ResolveCompositeFlowPath(pathParam, compositeInnerFlowBaseDir);
                        var dir = System.IO.Path.GetDirectoryName(resolvedPath);
                        if (!string.IsNullOrWhiteSpace(dir))
                            System.IO.Directory.CreateDirectory(dir);

                        var toSave = CalibAPI.DuplicateImage(inputImg);
                        var ok = CalibAPI.SaveImage(resolvedPath, toSave);
                        if (!ok) throw new InvalidOperationException($"保存图像失败: {resolvedPath}");

                        node.Outputs["Out"] = toSave;
                        node.ResultSummary = $"Saved: {System.IO.Path.GetFileName(resolvedPath)} ({toSave.Width}x{toSave.Height})";
                        break;
                    }

                    case "save_text":
                    {
                        if (!inputs.TryGetValue("Text", out var textObj) || textObj is not string text)
                            throw new InvalidOperationException("保存文本: 缺少 Text 输入");
                        var pathParam = node.Params.GetValueOrDefault("filePath", "flow_output.txt");
                        if (string.IsNullOrWhiteSpace(pathParam))
                            pathParam = "flow_output.txt";
                        var resolvedPath = ResolveCompositeFlowPath(pathParam, compositeInnerFlowBaseDir);
                        var dir = System.IO.Path.GetDirectoryName(resolvedPath);
                        if (!string.IsNullOrWhiteSpace(dir))
                            System.IO.Directory.CreateDirectory(dir);
                        System.IO.File.WriteAllText(resolvedPath, text, Encoding.UTF8);
                        node.Outputs["Out"] = text;
                        node.ResultSummary = $"Text saved: {System.IO.Path.GetFileName(resolvedPath)}";
                        break;
                    }

                    case "points_to_text":
                    {
                        var pts = inputs["Points"] as Point2D[];
                        if (pts == null)
                            throw new InvalidOperationException("点列转文本: 缺少 Points");
                        if (pts.Length == 0)
                        {
                            node.Outputs["Text"] = "";
                            node.ResultSummary = "skip: 0 pts → empty Text";
                            break;
                        }

                        var sep = (node.Params.GetValueOrDefault("lineSeparator", "lf") ?? "lf").Trim().ToLowerInvariant();
                        string nl = sep == "crlf" ? "\r\n" : "\n";
                        var sb = new System.Text.StringBuilder();
                        for (int i = 0; i < pts.Length; i++)
                        {
                            if (i > 0) sb.Append(nl);
                            sb.Append(pts[i].X.ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
                            sb.Append(',');
                            sb.Append(pts[i].Y.ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        string t = sb.ToString();
                        node.Outputs["Text"] = t;
                        node.ResultSummary = $"{pts.Length} points";
                        break;
                    }

                    case "polyline_simplify_dp":
                    {
                        var pts = inputs["In"] as Point2D[];
                        if (pts == null)
                            throw new InvalidOperationException("轮廓点简化: 缺少输入点列 In（未连接或非 Point2D[]）");
                        if (pts.Length == 0)
                        {
                            node.Outputs["Out"] = Array.Empty<Point2D>();
                            node.Outputs["OutBarIds"] = Array.Empty<int>();
                            node.ResultSummary = "skip: 0 pts → empty Out";
                            break;
                        }

                        double epsilon = 2.0;
                        if (node.Params.TryGetValue("epsilon", out var epsText) &&
                            !string.IsNullOrWhiteSpace(epsText) &&
                            double.TryParse(epsText.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ep))
                            epsilon = ep;
                        if (epsilon <= 0)
                            epsilon = 1e-6;
                        var closedRaw = (node.Params.GetValueOrDefault("closed", "true") ?? "true").Trim();
                        bool closed = !string.Equals(closedRaw, "false", StringComparison.OrdinalIgnoreCase)
                                      && !string.Equals(closedRaw, "0", StringComparison.OrdinalIgnoreCase);

                        int[]? barIn = null;
                        if (inputs.TryGetValue("GroupBarIds", out var gbObj) && gbObj is int[] gb && gb.Length == pts.Length)
                            barIn = gb;
                        if (barIn == null
                            && inputs.TryGetValue("MergedGroupBarIds", out var mgbObj)
                            && mgbObj is int[] mgb
                            && mgb.Length == pts.Length)
                            barIn = mgb;
                        if (barIn == null)
                        {
                            inputs.TryGetValue("BarIds", out var barObj);
                            barIn = barObj as int[];
                        }

                        if (barIn == null || barIn.Length != pts.Length)
                            barIn = null;

                        Point2D[] simplified;
                        int[] outBarIds;
                        if (barIn != null && barIn.Length == pts.Length)
                        {
                            SplitSampledPointsToContourPolylinesWithBarIds(pts, barIn, out var groups, out var segBarIds);
                            var outPts = new List<Point2D>();
                            var outIds = new List<int>();
                            for (int gi = 0; gi < groups.Count; gi++)
                            {
                                var seg = groups[gi];
                                if (seg == null || seg.Length == 0)
                                    continue;

                                int barId = gi < segBarIds.Count ? segBarIds[gi] : gi;
                                Point2D[] simpSeg;
                                if (seg.Length <= 2)
                                    simpSeg = seg;
                                else if (closed)
                                    simpSeg = SimplifyClosedPolyline(seg, epsilon);
                                else
                                    simpSeg = SimplifyOpenPolyline(seg.ToList(), epsilon).ToArray();
                                foreach (var p in simpSeg)
                                {
                                    outPts.Add(p);
                                    outIds.Add(barId);
                                }
                            }

                            simplified = outPts.ToArray();
                            outBarIds = outIds.ToArray();
                        }
                        else
                        {
                            if (pts.Length <= 2)
                                simplified = pts;
                            else if (closed)
                                simplified = SimplifyClosedPolyline(pts, epsilon);
                            else
                                simplified = SimplifyOpenPolyline(pts.ToList(), epsilon).ToArray();
                            outBarIds = new int[simplified.Length];
                        }

                        node.Outputs["Out"] = simplified;
                        node.Outputs["OutBarIds"] = outBarIds;
                        node.ResultSummary = $"{pts.Length} → {simplified.Length} pts, ε={epsilon:G}";
                        break;
                    }

                    case "polyline_uniform_offset":
                    {
                        double offsetDist = 5.0;
                        if (node.Params.TryGetValue("offsetDistance", out var offText) &&
                            !string.IsNullOrWhiteSpace(offText) &&
                            double.TryParse(offText.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var od))
                            offsetDist = od;

                        var closedRaw = (node.Params.GetValueOrDefault("closed", "true") ?? "true").Trim();
                        bool closed = !string.Equals(closedRaw, "false", StringComparison.OrdinalIgnoreCase)
                                      && !string.Equals(closedRaw, "0", StringComparison.OrdinalIgnoreCase);
                        string halconMode = node.Params.GetValueOrDefault("halconMode", "regression_normal") ?? "regression_normal";

#if HALCON_ENABLED
                        if (inputs.TryGetValue("Xld", out var xldIn) && xldIn is HalconXldContourBundle xb)
                        {
                            if (xb.ContourCount == 0)
                            {
                                node.Outputs["XldOut"] = new HalconXldContourBundle { Width = xb.Width, Height = xb.Height, Contours = new List<Point2D[]>() };
                                node.ResultSummary = "skip: empty Xld → empty XldOut";
                                break;
                            }

                            var xldOut = HalconFlowBridge.OffsetXldBundle(xb, offsetDist, halconMode);
                            node.Outputs["XldOut"] = xldOut;
                            node.ResultSummary = $"HALCON 外扩 d={offsetDist:G} → {xldOut.ContourCount} 条 XLD";
                            break;
                        }
#endif
                        int[]? barIn = null;
                        if (inputs.TryGetValue("Points3D", out var p3Obj) && p3Obj is CalibPoint3D[] p3 && p3.Length > 0)
                        {
                            var xy = new Point2D[p3.Length];
                            for (int i = 0; i < p3.Length; i++)
                                xy[i] = new Point2D(p3[i].X, p3[i].Y);
                            if (inputs.TryGetValue("GroupBarIds", out var gb3) && gb3 is int[] gbArr && gbArr.Length == p3.Length)
                                barIn = gbArr;
                            else if (inputs.TryGetValue("BarIds", out var b3) && b3 is int[] bArr && bArr.Length == p3.Length)
                                barIn = bArr;

                            OffsetPolylinesWithBarIds(xy, barIn, offsetDist, closed, out var off2d, out var offIds);
                            var off3d = new CalibPoint3D[off2d.Length];
                            for (int i = 0; i < off2d.Length; i++)
                            {
                                int j = Math.Min(i, p3.Length - 1);
                                off3d[i] = new CalibPoint3D(off2d[i].X, off2d[i].Y, p3[j].Z);
                            }

                            node.Outputs["Out3D"] = off3d;
                            node.Outputs["Out"] = off2d;
                            node.Outputs["OutBarIds"] = offIds;
                            node.ResultSummary = $"3D 外扩 d={offsetDist:G} · {p3.Length}→{off3d.Length} 点";
                            break;
                        }

                        var pts = inputs.GetValueOrDefault("In") as Point2D[];
                        if (pts == null)
                            throw new InvalidOperationException("轮廓/轨迹均匀外扩: 请连接 In(Point2D[])、Points3D 或 Xld");

                        if (pts.Length == 0)
                        {
                            node.Outputs["Out"] = Array.Empty<Point2D>();
                            node.Outputs["OutBarIds"] = Array.Empty<int>();
                            node.ResultSummary = "skip: 0 pts → empty Out";
                            break;
                        }

                        if (inputs.TryGetValue("GroupBarIds", out var gbObj) && gbObj is int[] gb && gb.Length == pts.Length)
                            barIn = gb;
                        else if (inputs.TryGetValue("BarIds", out var barObj) && barObj is int[] bi && bi.Length == pts.Length)
                            barIn = bi;

                        OffsetPolylinesWithBarIds(pts, barIn, offsetDist, closed, out var outPts, out var outBarIds);
                        node.Outputs["Out"] = outPts;
                        node.Outputs["OutBarIds"] = outBarIds;
                        node.ResultSummary = $"外扩 d={offsetDist:G} · {pts.Length}→{outPts.Length} 点";
                        break;
                    }

                    case "plc_read_weld_done":
                    {
                        _ = RequireFlowPlcD();
                        string flagReg = ResolveFlowPlcRegister(
                            node.Params.GetValueOrDefault("flagRegister"),
                            "WeldDoneFlag",
                            "D803L");
                        int bit = int.TryParse(node.Params.GetValueOrDefault("bit"), out var rb) ? rb : 0;
                        bool done = FlowReadPlcBit(flagReg, bit);
                        node.Outputs["Done"] = done;
                        node.ResultSummary = $"焊接完成 {flagReg}.bit{bit} = {(done ? "1(完成)" : "0")}";
                        break;
                    }

                    case "plc_wait_weld_done":
                    {
                        _ = RequireFlowPlcD();
                        string flagReg = ResolveFlowPlcRegister(
                            node.Params.GetValueOrDefault("flagRegister"),
                            "WeldDoneFlag",
                            "D803L");
                        int bit = int.TryParse(node.Params.GetValueOrDefault("bit"), out var wb) ? wb : 0;
                        int timeoutMs = int.TryParse(node.Params.GetValueOrDefault("timeoutMs"), out var tm) ? tm : 600_000;
                        int pollMs = int.TryParse(node.Params.GetValueOrDefault("pollIntervalMs"), out var pm) ? pm : 50;
                        bool clearAfter = !string.Equals(
                            node.Params.GetValueOrDefault("clearAfter", "true")?.Trim(),
                            "false",
                            StringComparison.OrdinalIgnoreCase)
                            && node.Params.GetValueOrDefault("clearAfter", "true") != "0";

                        bool ok = FlowWaitPlcBit(flagReg, bit, timeoutMs, pollMs);
                        if (!ok)
                            throw new InvalidOperationException(
                                $"监听焊接完成超时：{flagReg}.bit{bit} 在 {timeoutMs}ms 内未变为 1");

                        if (clearAfter)
                            FlowWritePlcBit(flagReg, bit, false);

                        node.Outputs["Done"] = true;
                        node.ResultSummary = $"焊接完成 {flagReg}.bit{bit}{(clearAfter ? "，已清 0" : "")}";
                        break;
                    }

                    case "plc_read_camera_capture":
                    {
                        _ = RequireFlowPlcD();
                        string sigReg = ResolveFlowPlcRegister(
                            node.Params.GetValueOrDefault("signalRegister"),
                            "CameraCaptureStart",
                            "D1800L");
                        int bit = int.TryParse(node.Params.GetValueOrDefault("bit"), out var bi) ? bi : 0;
                        bool triggered = FlowReadPlcBit(sigReg, bit);
                        node.Outputs["Triggered"] = triggered;
                        if (inputs.TryGetValue("After", out var afterRead))
                            node.Outputs["Out"] = afterRead;
                        node.ResultSummary = $"相机拍照信号 {sigReg}.bit{bit} = {(triggered ? "1" : "0")}";
                        break;
                    }

                    case "plc_wait_camera_capture":
                    {
                        _ = RequireFlowPlcD();
                        string sigReg = ResolveFlowPlcRegister(
                            node.Params.GetValueOrDefault("signalRegister"),
                            "CameraCaptureStart",
                            "D1800L");
                        int bit = int.TryParse(node.Params.GetValueOrDefault("bit"), out var wb) ? wb : 0;
                        int timeoutMs = int.TryParse(node.Params.GetValueOrDefault("timeoutMs"), out var tm) ? tm : 60_000;
                        int pollMs = int.TryParse(node.Params.GetValueOrDefault("pollIntervalMs"), out var pm) ? pm : 20;
                        bool clearAfter = string.Equals(
                            node.Params.GetValueOrDefault("clearAfter", "false")?.Trim(),
                            "true",
                            StringComparison.OrdinalIgnoreCase)
                            || node.Params.GetValueOrDefault("clearAfter") == "1";

                        bool ok = FlowWaitPlcBit(sigReg, bit, timeoutMs, pollMs);
                        if (!ok)
                            throw new InvalidOperationException(
                                $"监听相机拍照信号超时：{sigReg}.bit{bit} 在 {timeoutMs}ms 内未变为 1");

                        if (clearAfter)
                            FlowWritePlcBit(sigReg, bit, false);

                        node.Outputs["Triggered"] = true;
                        if (inputs.TryGetValue("After", out var afterWait))
                            node.Outputs["Out"] = afterWait;
                        node.ResultSummary = $"已收到拍照信号 {sigReg}.bit{bit}{(clearAfter ? "，已清 0" : "")}";
                        break;
                    }

                    case "plc_clear_weld_done":
                    {
                        _ = RequireFlowPlcD();
                        string flagReg = ResolveFlowPlcRegister(
                            node.Params.GetValueOrDefault("flagRegister"),
                            "WeldDoneFlag",
                            "D803L");
                        int bit = int.TryParse(node.Params.GetValueOrDefault("bit"), out var cb) ? cb : 0;
                        FlowWritePlcBit(flagReg, bit, false);
                        node.Outputs["Cleared"] = true;
                        node.ResultSummary = $"焊接完成标志 {flagReg}.bit{bit} 已清 0";
                        break;
                    }

                    case "plc_set_weld_done_to_plc":
                    {
                        _ = RequireFlowPlcD();
                        string flagReg = ResolveFlowPlcRegister(
                            node.Params.GetValueOrDefault("flagRegister"),
                            "WeldDoneHostFlag",
                            "D804L");
                        int bit = int.TryParse(node.Params.GetValueOrDefault("bit"), out var sb) ? sb : 0;
                        FlowWritePlcBit(flagReg, bit, true);
                        node.Outputs["Signaled"] = true;
                        node.ResultSummary = $"轨迹已下发通知 {flagReg}.bit{bit} = 1 (上位机→PLC)";
                        break;
                    }

                    case "plc_connect":
                    {
                        XinJETcpNet plc = EnsureFlowPlcConnectedFromNodeParams(node.Params);
                        node.Outputs["Connected"] = true;
                        string via = (_flowPlc != null && _flowPlcConnected) ? "Flow" : PlcXinjeSession.DescribeActive();
                        node.ResultSummary = $"PLC 已连接 ({via}, {plc.IpAddress}:{plc.Port})";
                        break;
                    }

                    case "plc_disconnect":
                    {
                        bool disconnected = DisconnectFlowOwnedPlc();
                        node.Outputs["Disconnected"] = disconnected;
                        node.ResultSummary = disconnected
                            ? "PLC 已断开（Flow 连接）"
                            : "未断开：当前无 Flow 连接（可能在用 PLC 页连接）";
                        break;
                    }

                    case "plc_pou_enable":
                    {
                        _ = RequireFlowPlcD();

                        bool enabled;
                        if (inputs.TryGetValue("Enable", out var enObj) && enObj is bool eb)
                            enabled = eb;
                        else
                        {
                            string enParam = (node.Params.GetValueOrDefault("enable", "ON") ?? "ON").Trim();
                            enabled = enParam.Equals("ON", StringComparison.OrdinalIgnoreCase)
                                || enParam == "1"
                                || enParam.Equals("true", StringComparison.OrdinalIgnoreCase);
                        }

                        string regLabel = node.Params.GetValueOrDefault("enableRegister", "D801L")?.Trim() ?? "D801L";
                        FlowWritePlcBit(regLabel, 0, enabled);

                        node.Outputs["Enabled"] = enabled;
                        node.ResultSummary = $"POU使能 {(enabled ? "ON" : "OFF")} → {regLabel}";
                        break;
                    }

                    case "plc_set_segment_count":
                    {
                        XinJETcpNet plcCount = RequireFlowPlcD();

                        int count;
                        if (inputs.TryGetValue("Count", out var countObj) && countObj != null)
                        {
                            count = countObj switch
                            {
                                int ci => ci,
                                long cl => (int)cl,
                                double cd => (int)Math.Round(cd),
                                float cf => (int)Math.Round(cf),
                                _ when int.TryParse(countObj.ToString(), System.Globalization.NumberStyles.Integer,
                                    System.Globalization.CultureInfo.InvariantCulture, out int parsed) => parsed,
                                _ => throw new InvalidOperationException(
                                    $"PLC 设置线段数量: Count 无法解析为整数 ({countObj.GetType().Name})")
                            };
                        }
                        else if (int.TryParse(node.Params.GetValueOrDefault("segmentCount"), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out int paramCount))
                        {
                            count = paramCount;
                        }
                        else
                            throw new InvalidOperationException("PLC 设置线段数量: 请连接 Count 输入或填写 segmentCount 参数");

                        if (count < 0)
                            throw new InvalidOperationException($"PLC 设置线段数量: 数量不能为负 ({count})");
                        if (count > short.MaxValue)
                            throw new InvalidOperationException($"PLC 设置线段数量: 超过 Int16 上限 ({count})");

                        string countReg = NormalizeFlowDAddress(
                            node.Params.GetValueOrDefault("countRegister", "D800") ?? "D800");
                        var wr = plcCount.Write(countReg, (short)count);
                        if (!wr.IsSuccess)
                            throw new InvalidOperationException($"PLC 设置线段数量失败(@{countReg}): {wr.Message}");

                        node.Outputs["Count"] = count;
                        node.ResultSummary = $"线段数量={count} → {countReg}";
                        break;
                    }

                    case "send_plc":
                    case "send_plc_point":
                    {
                        _ = RequireFlowPlcD();
                        bool sendAsPoint = node.Def.TypeId == "send_plc_point";
                        string sendPlcLogTag = sendAsPoint ? "send_plc_point" : "send_plc";
                        short gvarType = short.TryParse(
                            node.Params.GetValueOrDefault("gvarType"),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var gt)
                            ? gt : (short)1; // 默认 1=线段

                        string splitByBar = (node.Params.GetValueOrDefault("splitByBar", "none") ?? "none").Trim();
                        double zDefault = double.TryParse(
                            node.Params.GetValueOrDefault("zDefault", "0"),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var zDefVal)
                            ? zDefVal
                            : 0.0;
                        bool closePolyline = SendPlcBatchPlanner.ParseBoolParam(
                            node.Params.GetValueOrDefault("closePolyline", "true"), true);
                        bool asPolylineSegments = sendAsPoint
                            && SendPlcBatchPlanner.ParseBoolParam(
                                node.Params.GetValueOrDefault("asPolylineSegments", "false"), false);
                        var segmentMode = sendAsPoint && !asPolylineSegments
                            ? PlcGvarSegmentMode.PointDegenerate
                            : PlcGvarSegmentMode.Line;
                        if (!PlcGvarBuilder.TryResolveSendPlcGvar(
                                inputs,
                                node.Params.GetValueOrDefault("usePlcPageGvar"),
                                gvarType,
                                splitByBar,
                                out GVAR[] gvarItems,
                                out List<(int BarId, GVAR[] Gvars)>? barBatches,
                                out string gvarSource,
                                out string? resolveDiag,
                                segmentMode,
                                pointAt: "mid",
                                zDefault: zDefault,
                                closePolyline: closePolyline))
                        {
                            throw new InvalidOperationException(
                                $"{(sendAsPoint ? "发送PLC(每点一点)" : "发送PLC")}: {resolveDiag}");
                        }

                        if (string.Equals(splitByBar, "separate_batch", StringComparison.OrdinalIgnoreCase)
                            && barBatches != null
                            && barBatches.Count > 0)
                        {
                            int expectedBatches = int.TryParse(
                                node.Params.GetValueOrDefault("expectedBatchCount", "0"),
                                System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var expB)
                                ? expB
                                : 0;
                            string batchBarSummary = string.Join(
                                ", ",
                                barBatches.Select(b => $"BarId={b.BarId}×{b.Gvars.Length}段"));
                            AppendLog(
                                $"[{sendPlcLogTag}] separate_batch: {barBatches.Count} 批（{batchBarSummary}）| {gvarSource}");
                            if (expectedBatches > 0 && barBatches.Count != expectedBatches)
                            {
                                AppendLog(
                                    $"[{sendPlcLogTag}] 警告: 期望 {expectedBatches} 批，实际 {barBatches.Count} 批。"
                                    + " 批次数=逐点 BarId 种类数(每条焊道一批)，与找形个数无关；须用 GroupBarIds→OutBarIds。"
                                    + " 若复合内落格为 16/16 仍不足 16 批，说明 BarIds 未传到 send_plc 或条号不是 0～15。");
                                AppendCompositePipelineHintsForBatchMismatch(
                                    expectedBatches, barBatches.Count, inputs);
                            }
                        }

                        XinJETcpNet plcD = RequireFlowPlcD();
                        string plcVia = (_flowPlc != null && _flowPlcConnected) ? "Flow" : PlcXinjeSession.DescribeActive();

                        string countRegLabel = (node.Params.GetValueOrDefault("countRegister", "D800") ?? "D800").Trim();
                        string gvarRegLabel = (node.Params.GetValueOrDefault("gvarStartRegister") ?? "").Trim();
                        if (string.IsNullOrEmpty(gvarRegLabel))
                            gvarRegLabel = (node.Params.GetValueOrDefault("baseRegister", "D30000") ?? "D30000").Trim();
                        string countReg = NormalizeFlowDAddress(countRegLabel);
                        var flowCfg = LoadFlowPlcConfig();
                        string gvarStart = NormalizeFlowDAddress(
                            PlcXinjeHelper.ResolveGvarStartAddress(gvarRegLabel, flowCfg.GvarList?.StartAddress));
                        int maxCount = int.TryParse(
                            node.Params.GetValueOrDefault("maxSegmentCount", "1024"),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var mc)
                            ? mc : 1024;

                        bool skipCountWrite = (node.Params.GetValueOrDefault("skipCountWrite", "true") ?? "true")
                            .Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                            || node.Params.GetValueOrDefault("skipCountWrite", "true") == "1";
                        bool separateBatchMode = string.Equals(
                            splitByBar, "separate_batch", StringComparison.OrdinalIgnoreCase);
                        // 分批下发：每批必须写 D800=当批线段数（不受 skipCountWrite 影响）
                        bool writeCountPerBatch = separateBatchMode && barBatches != null && barBatches.Count > 0;
                        bool runDownstreamPerBatch = SendPlcBatchPlanner.ParseBoolParam(
                            node.Params.GetValueOrDefault("runDownstreamPerBatch"),
                            defaultValue: separateBatchMode && barBatches != null && barBatches.Count > 0);

                        bool stackBars = string.Equals(
                            (node.Params.GetValueOrDefault("barGvarLayout", "overwrite") ?? "overwrite").Trim(),
                            "stack",
                            StringComparison.OrdinalIgnoreCase);

                        List<(int BarId, int SegmentCount)>? batchMeta = null;
                        if (barBatches != null && barBatches.Count > 0)
                        {
                            batchMeta = barBatches
                                .Select(b => (b.BarId, b.Gvars.Length))
                                .ToList();
                        }

                        var plan = SendPlcBatchPlanner.BuildSteps(
                            separateBatchMode,
                            batchMeta,
                            gvarItems.Length,
                            writeCountPerBatch,
                            skipCountWrite,
                            signalHostAfterAllBatches: false);

                        string resultMsg;
                        int n;
                        int wordOff = 0;
                        var parts = new List<string>();
                        int execD800 = 0;
                        int execGvarBatch = 0;

                        bool downstreamRanInsideBatchLoop = false;
                        foreach (var step in plan)
                        {
                            switch (step.Kind)
                            {
                                case PlcSendStepKind.WriteSegmentCount:
                                    var wrCount = plcD.Write(countReg, (short)step.SegmentCount);
                                    execD800++;
                                    if (!wrCount.IsSuccess)
                                        throw new InvalidOperationException(
                                            $"发送PLC失败(BarId={step.BarId} 线段数 {step.SegmentCount}→{countReg}): {FormatFlowPlcOperateFailure(wrCount)}");
                                    break;

                                case PlcSendStepKind.WriteGvarBatch:
                                    if (barBatches != null && barBatches.Count > 0)
                                    {
                                        var (barId, batchGvars) = barBatches[step.BatchIndex];
                                        int nb = batchGvars.Length;
                                        if (nb > maxCount)
                                            throw new InvalidOperationException(
                                                $"发送PLC: BarId={barId} 线段数 {nb} 超过 maxSegmentCount={maxCount}");
                                        string addr = stackBars
                                            ? PlcXinjeHelper.OffsetDAddress(gvarStart, wordOff)
                                            : gvarStart;
                                        var wrGvarBatch = PlcGvarModbus.SendGvarList(plcD, addr, batchGvars, out int batchWords);
                                        if (!wrGvarBatch.IsSuccess)
                                            throw new InvalidOperationException(
                                                $"发送PLC失败(BarId={barId}, {nb} 条, {batchWords} 字 @{addr}): {PlcGvarModbus.FormatOperateFailure(wrGvarBatch)}");
                                        wordOff += nb * GVAR.WORD_COUNT;
                                        parts.Add($"bar{barId}:{nb}@{addr}");
                                        execGvarBatch++;
                                        if (runDownstreamPerBatch)
                                        {
                                            ExecuteSendPlcDownstreamChain(
                                                node, step.BatchIndex, barBatches.Count, barId, nb);
                                            downstreamRanInsideBatchLoop = true;
                                        }
                                    }
                                    else
                                    {
                                        n = gvarItems.Length;
                                        if (n > maxCount)
                                            throw new InvalidOperationException(
                                                $"发送PLC: GVAR 条数 {n} 超过上限 maxSegmentCount={maxCount}（来源={gvarSource}）");
                                        var wrGvar = PlcGvarModbus.SendGvarList(plcD, gvarStart, gvarItems, out int totalWords);
                                        if (!wrGvar.IsSuccess)
                                            throw new InvalidOperationException(
                                                $"发送PLC失败(GVAR {n} 条, {totalWords} 字 @{gvarStart}, 通道={plcVia}): {PlcGvarModbus.FormatOperateFailure(wrGvar)}");
                                        execGvarBatch++;
                                    }

                                    break;
                            }
                        }

                        int downstreamBatchCount = barBatches?.Count ?? 1;
                        int downstreamSegmentTotal = barBatches != null && barBatches.Count > 0
                            ? barBatches.Sum(b => b.Gvars.Length)
                            : gvarItems.Length;
                        if (!downstreamRanInsideBatchLoop)
                        {
                            var downChain = ResolveSendPlcDownstreamChain(node);
                            if (downChain.Count > 0)
                            {
                                int downBarId = barBatches != null && barBatches.Count > 0
                                    ? barBatches[0].BarId
                                    : 0;
                                ExecuteSendPlcDownstreamChain(
                                    node, 0, downstreamBatchCount, downBarId, downstreamSegmentTotal);
                            }
                        }

                        if (barBatches != null && barBatches.Count > 0)
                        {
                            n = barBatches.Sum(b => b.Gvars.Length);
                            resultMsg = writeCountPerBatch || !skipCountWrite
                                ? $"GVAR分批({barBatches.Count}): {string.Join("; ", parts)}, 共{n}段+各批{countRegLabel}为当批段数, type={gvarType}, src={gvarSource} ({plcVia})"
                                : $"GVAR分批({barBatches.Count}): {string.Join("; ", parts)}, 共{n}段, type={gvarType}, src={gvarSource} ({plcVia})";
                        }
                        else
                        {
                            n = gvarItems.Length;
                            resultMsg = skipCountWrite
                                ? $"GVAR: {n} 条 @{gvarStart}, type={gvarType}, src={gvarSource} ({plcVia})"
                                : $"GVAR: count={n}→{countReg}, {n} 条 @{gvarStart}, type={gvarType}, src={gvarSource} ({plcVia})";
                        }

                        int reportedBatches = execGvarBatch > 0 ? execGvarBatch : (gvarItems.Length > 0 ? 1 : 0);
                        string dispatchStats =
                            $"批{reportedBatches} D800×{execD800}";
                        int downChainN = ResolveSendPlcDownstreamChain(node).Count;
                        if (downChainN > 0)
                        {
                            dispatchStats += downstreamRanInsideBatchLoop
                                ? $" 下游×{downChainN}节点/批"
                                : $" 下游×{downChainN}节点";
                        }

                        string planSummary = $"计划批={plan.Count(s => s.Kind == PlcSendStepKind.WriteGvarBatch)}, 计划D800={plan.Count(s => s.Kind == PlcSendStepKind.WriteSegmentCount)}";
                        AppendLog($"[{sendPlcLogTag}] {dispatchStats} | {planSummary} | src={gvarSource}");
                        resultMsg += $" | {dispatchStats}";

                        node.Outputs["GvarSent"] = true;
                        StatusText.Dispatcher.Invoke(() => StatusText.Text = $"发送成功: {resultMsg}");
                        node.ResultSummary = resultMsg;
                        break;
                    }

                    case "light_connect":
                    {
                        ControllerLightFlowSession.ConnectFromNodeParams(node.Params);
                        node.Outputs["Connected"] = true;
                        var s = ControllerLightFlowSession.Session;
                        string detail = s.LastIp ?? (s.LastComPort.HasValue ? $"COM{s.LastComPort}" : "");
                        node.ResultSummary = $"光源已连接 {detail}";
                        break;
                    }

                    case "light_disconnect":
                    {
                        ControllerLightFlowSession.Disconnect();
                        node.Outputs["Disconnected"] = true;
                        node.ResultSummary = "光源已断开";
                        break;
                    }

                    case "light_set":
                    {
                        bool autoConnect = !string.Equals(
                            node.Params.GetValueOrDefault("autoConnect", "true")?.Trim(),
                            "false",
                            StringComparison.OrdinalIgnoreCase)
                            && node.Params.GetValueOrDefault("autoConnect", "true")?.Trim() != "0";

                        if (autoConnect)
                            ControllerLightFlowSession.EnsureConnected(node.Params);
                        else if (!ControllerLightFlowSession.IsConnected)
                            throw new InvalidOperationException("光源控制: 未连接。请先执行「光源连接」或开启 autoConnect。");

                        var light = ControllerLightFlowSession.Session;
                        string action = (node.Params.GetValueOrDefault("action", "brightness") ?? "brightness")
                            .Trim().ToLowerInvariant();
                        int channel = Math.Clamp(
                            int.TryParse(node.Params.GetValueOrDefault("channel"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch)
                                ? ch
                                : 1,
                            1,
                            64);
                        int value = ResolveLightControlValue(inputs, node.Params);
                        bool valueFromPort = inputs.ContainsKey("Value") && inputs["Value"] != null;

                        string summary;
                        switch (action)
                        {
                            case "brightness":
                            case "intensity":
                            case "亮度":
                                light.SetDigitalValue(channel, value);
                                summary = $"CH{channel} 亮度={value}" + (valueFromPort ? " (Value输入)" : "");
                                break;
                            case "strobe":
                            case "脉宽":
                                light.SetStrobeValue(channel, value);
                                summary = $"CH{channel} 脉宽={value}";
                                break;
                            case "intcycle":
                            case "内触发":
                                light.SetIntCycle(value);
                                summary = $"内触发周期={value}";
                                break;
                            case "trimode":
                            case "触发模式":
                                light.SetLightTriMode(value);
                                summary = $"触发模式={value}";
                                break;
                            case "lightstate":
                            case "常亮":
                                light.SetLightState(value);
                                summary = $"常亮/长灭={value}";
                                break;
                            case "keepalive":
                            case "心跳":
                                light.KeepAlive();
                                summary = "心跳 OK";
                                break;
                            case "multi":
                            case "channels":
                            case "多通道":
                            {
                                string spec = node.Params.GetValueOrDefault("channels", "") ?? "";
                                int n = ApplyLightChannelSpec(light, spec);
                                summary = $"多通道 x{n}";
                                break;
                            }
                            default:
                                throw new InvalidOperationException(
                                    $"光源控制: 未知 action='{action}'（brightness/strobe/intCycle/triMode/lightState/keepalive/multi）");
                        }

                        if (inputs.TryGetValue("After", out var afterLight))
                            node.Outputs["Out"] = afterLight;
                        node.Outputs["Ok"] = true;
                        node.ResultSummary = summary + (inputs.ContainsKey("After") ? "（After 已透传）" : "");
                        break;
                    }

                    case "if_gate":
                    {
                        if (!inputs.TryGetValue("Value", out var valObj) || valObj == null)
                            throw new InvalidOperationException("条件判断: 缺少 Value 输入");

                        double value;
                        if (valObj is bool vb) value = vb ? 1.0 : 0.0;
                        else if (valObj is int vi) value = vi;
                        else if (valObj is float vf) value = vf;
                        else if (valObj is double vd) value = vd;
                        else if (!double.TryParse(valObj.ToString(), out value))
                            throw new InvalidOperationException($"条件判断: Value 不是可比较数值/布尔 ({valObj.GetType().Name})");

                        string op = (node.Params.GetValueOrDefault("op", ">") ?? ">").Trim();
                        double threshold = double.TryParse(node.Params.GetValueOrDefault("threshold"), out var thv) ? thv : 0.0;
                        double epsilon = double.TryParse(node.Params.GetValueOrDefault("epsilon"), out var eps) ? Math.Max(1e-12, eps) : 1e-6;

                        bool pass = op switch
                        {
                            ">" => value > threshold,
                            ">=" => value >= threshold,
                            "<" => value < threshold,
                            "<=" => value <= threshold,
                            "==" => Math.Abs(value - threshold) <= epsilon,
                            "!=" => Math.Abs(value - threshold) > epsilon,
                            _ => throw new InvalidOperationException($"条件判断: 不支持的 op='{op}'")
                        };

                        node.Outputs["True"] = pass;
                        node.Outputs["False"] = !pass;
                        node.ResultSummary = $"if {value:G6} {op} {threshold:G6} => {pass}";
                        break;
                    }

                    case "route_true":
                    {
                        if (!inputs.TryGetValue("Condition", out var condObj) || condObj is not bool cond)
                            throw new InvalidOperationException("真分支放行: 缺少 Condition(bool)");
                        if (cond && inputs.TryGetValue("In", out var inObj))
                        {
                            node.Outputs["Out"] = inObj;
                            node.ResultSummary = "pass=true";
                        }
                        else
                        {
                            node.ResultSummary = "blocked";
                        }
                        break;
                    }

                    case "route_false":
                    {
                        if (!inputs.TryGetValue("Condition", out var condObj) || condObj is not bool cond)
                            throw new InvalidOperationException("假分支放行: 缺少 Condition(bool)");
                        if (!cond && inputs.TryGetValue("In", out var inObj))
                        {
                            node.Outputs["Out"] = inObj;
                            node.ResultSummary = "pass=false";
                        }
                        else
                        {
                            node.ResultSummary = "blocked";
                        }
                        break;
                    }

#if HALCON_ENABLED
                    case "halcon_rgb1_to_gray":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON 转灰度: 缺少 In");
                        HObject ho = HalconFlowBridge.CalibToHObject(inImg);
                        try
                        {
                            ho = HalconFlowBridge.EnsureGray(ho);
                            node.Outputs["Out"] = HalconFlowBridge.ToCalibGray(ho);
                            node.ResultSummary = $"HALCON gray {inImg.Width}x{inImg.Height}";
                        }
                        finally
                        {
                            ho.Dispose();
                        }
                        break;
                    }

                    case "halcon_threshold_bin":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON 阈值二值: 缺少 In");
                        double minG = double.TryParse(node.Params.GetValueOrDefault("minGray"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mn) ? mn : 128.0;
                        double maxG = double.TryParse(node.Params.GetValueOrDefault("maxGray"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mx) ? mx : 255.0;
                        node.Outputs["Out"] = HalconFlowBridge.ThresholdToCalibGray(inImg, minG, maxG);
                        node.ResultSummary = $"HALCON bin [{minG:G6},{maxG:G6}]";
                        break;
                    }

                    case "halcon_emphasize":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON Emphasize: 缺少 In");
                        int mw = int.TryParse(node.Params.GetValueOrDefault("maskWidth"), out var mwv) ? mwv : 7;
                        int mh = int.TryParse(node.Params.GetValueOrDefault("maskHeight"), out var mhv) ? mhv : 7;
                        double factor = double.TryParse(node.Params.GetValueOrDefault("factor"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fv) ? fv : 1.0;
                        mw = Math.Max(1, mw);
                        mh = Math.Max(1, mh);
                        node.Outputs["Out"] = HalconFlowBridge.EmphasizeToCalib(inImg, mw, mh, factor);
                        node.ResultSummary = $"HALCON emphasize {mw}x{mh}";
                        break;
                    }

                    case "halcon_gray_opening_rect":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON 灰度开运算: 缺少 In");
                        int mh = int.TryParse(node.Params.GetValueOrDefault("maskHeight"), out var mhv) ? mhv : 3;
                        int mw = int.TryParse(node.Params.GetValueOrDefault("maskWidth"), out var mwv) ? mwv : 3;
                        mh = Math.Max(1, mh);
                        mw = Math.Max(1, mw);
                        node.Outputs["Out"] = HalconFlowBridge.GrayOpeningRectToCalib(inImg, mh, mw);
                        node.ResultSummary = $"HALCON gray_opening {mh}x{mw}";
                        break;
                    }

                    case "halcon_scale_image":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON ScaleImage: 缺少 In");
                        double mult = double.TryParse(node.Params.GetValueOrDefault("mult"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 1.0;
                        double add = double.TryParse(node.Params.GetValueOrDefault("add"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : 0.0;
                        node.Outputs["Out"] = HalconFlowBridge.ScaleImageToCalib(inImg, mult, add);
                        node.ResultSummary = $"HALCON scale {mult:G4},{add:G4}";
                        break;
                    }

                    case "halcon_scale_image_max":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON ScaleImageMax: 缺少 In");
                        node.Outputs["Out"] = HalconFlowBridge.ScaleImageMaxToCalib(inImg);
                        node.ResultSummary = "HALCON scale_max";
                        break;
                    }

                    case "halcon_illuminate":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON Illuminate: 缺少 In");
                        int mw = int.TryParse(node.Params.GetValueOrDefault("maskWidth"), out var mwv) ? mwv : 41;
                        int mh = int.TryParse(node.Params.GetValueOrDefault("maskHeight"), out var mhv) ? mhv : 41;
                        double factor = double.TryParse(node.Params.GetValueOrDefault("factor"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fv) ? fv : 0.7;
                        node.Outputs["Out"] = HalconFlowBridge.IlluminateToCalib(inImg, mw, mh, factor);
                        node.ResultSummary = $"HALCON illuminate {mw}x{mh}";
                        break;
                    }

                    case "halcon_mean_image":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON MeanImage: 缺少 In");
                        int mw = int.TryParse(node.Params.GetValueOrDefault("maskWidth"), out var mwv) ? mwv : 15;
                        int mh = int.TryParse(node.Params.GetValueOrDefault("maskHeight"), out var mhv) ? mhv : 15;
                        node.Outputs["Out"] = HalconFlowBridge.MeanImageToCalib(inImg, mw, mh);
                        node.ResultSummary = $"HALCON mean {mw}x{mh}";
                        break;
                    }

                    case "halcon_gauss_filter":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON GaussFilter: 缺少 In");
                        int size = int.TryParse(node.Params.GetValueOrDefault("size"), out var sz) ? sz : 5;
                        node.Outputs["Out"] = HalconFlowBridge.GaussFilterToCalib(inImg, size);
                        node.ResultSummary = $"HALCON gauss {size}";
                        break;
                    }

                    case "halcon_gray_closing_rect":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON 灰度闭运算: 缺少 In");
                        int mh = int.TryParse(node.Params.GetValueOrDefault("maskHeight"), out var mhv) ? mhv : 5;
                        int mw = int.TryParse(node.Params.GetValueOrDefault("maskWidth"), out var mwv) ? mwv : 5;
                        node.Outputs["Out"] = HalconFlowBridge.GrayClosingRectToCalib(inImg, mh, mw);
                        node.ResultSummary = $"HALCON gray_closing {mh}x{mw}";
                        break;
                    }

                    case "halcon_binary_threshold":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON BinaryThreshold: 缺少 In");
                        string method = node.Params.GetValueOrDefault("method") ?? "max_separability";
                        string lightDark = node.Params.GetValueOrDefault("lightDark") ?? "dark";
                        node.Outputs["Out"] = HalconFlowBridge.BinaryThresholdToCalibGray(inImg, method.Trim(), lightDark.Trim());
                        node.ResultSummary = $"HALCON bin_auto {method}";
                        break;
                    }

                    case "halcon_dyn_threshold":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        var refImg = inputs["Ref"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON DynThreshold: 缺少 In");
                        if (refImg == null) throw new InvalidOperationException("HALCON DynThreshold: 缺少 Ref");
                        double offset = double.TryParse(node.Params.GetValueOrDefault("offset"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var off) ? off : 15.0;
                        string lightDark = node.Params.GetValueOrDefault("lightDark") ?? "dark";
                        node.Outputs["Out"] = HalconFlowBridge.DynThresholdToCalibGray(inImg, refImg, offset, lightDark.Trim());
                        node.ResultSummary = $"HALCON dyn_th off={offset:G4}";
                        break;
                    }

                    case "halcon_var_threshold":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON VarThreshold: 缺少 In");
                        int mw = int.TryParse(node.Params.GetValueOrDefault("maskWidth"), out var mwv) ? mwv : 15;
                        int mh = int.TryParse(node.Params.GetValueOrDefault("maskHeight"), out var mhv) ? mhv : 15;
                        double stdScale = double.TryParse(node.Params.GetValueOrDefault("stdDevScale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ss) ? ss : 0.2;
                        double absTh = double.TryParse(node.Params.GetValueOrDefault("absThreshold"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at) ? at : 40.0;
                        string lightDark = node.Params.GetValueOrDefault("lightDark") ?? "dark";
                        node.Outputs["Out"] = HalconFlowBridge.VarThresholdToCalibGray(inImg, mw, mh, stdScale, absTh, lightDark.Trim());
                        node.ResultSummary = $"HALCON var_th {mw}x{mh}";
                        break;
                    }

                    case "halcon_median_image":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON MedianImage: 缺少 In");
                        string maskType = node.Params.GetValueOrDefault("maskType") ?? "circle";
                        int radius = int.TryParse(node.Params.GetValueOrDefault("radius"), out var r) ? r : 3;
                        string margin = node.Params.GetValueOrDefault("margin") ?? "mirrored";
                        node.Outputs["Out"] = HalconFlowBridge.MedianImageToCalib(inImg, maskType.Trim(), radius, margin.Trim());
                        node.ResultSummary = $"HALCON median r={radius}";
                        break;
                    }

                    case "halcon_invert_image":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON InvertImage: 缺少 In");
                        node.Outputs["Out"] = HalconFlowBridge.InvertImageToCalib(inImg);
                        node.ResultSummary = "HALCON invert";
                        break;
                    }

                    case "halcon_abs_diff":
                    {
                        var a = inputs["In"] as CalibImage;
                        var b = inputs["In2"] as CalibImage;
                        if (a == null) throw new InvalidOperationException("HALCON AbsDiff: 缺少 In");
                        if (b == null) throw new InvalidOperationException("HALCON AbsDiff: 缺少 In2");
                        double mult = double.TryParse(node.Params.GetValueOrDefault("mult"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mv) ? mv : 1.0;
                        node.Outputs["Out"] = HalconFlowBridge.AbsDiffImageToCalib(a, b, mult);
                        node.ResultSummary = "HALCON abs_diff";
                        break;
                    }

                    case "halcon_sub_image":
                    {
                        var a = inputs["In"] as CalibImage;
                        var b = inputs["In2"] as CalibImage;
                        if (a == null) throw new InvalidOperationException("HALCON SubImage: 缺少 In");
                        if (b == null) throw new InvalidOperationException("HALCON SubImage: 缺少 In2");
                        double mult = double.TryParse(node.Params.GetValueOrDefault("mult"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mv) ? mv : 1.0;
                        double add = double.TryParse(node.Params.GetValueOrDefault("add"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var av) ? av : 0.0;
                        node.Outputs["Out"] = HalconFlowBridge.SubImageToCalib(a, b, mult, add);
                        node.ResultSummary = "HALCON sub_image";
                        break;
                    }

                    case "halcon_add_image":
                    {
                        var a = inputs["In"] as CalibImage;
                        var b = inputs["In2"] as CalibImage;
                        if (a == null) throw new InvalidOperationException("HALCON AddImage: 缺少 In");
                        if (b == null) throw new InvalidOperationException("HALCON AddImage: 缺少 In2");
                        double mult = double.TryParse(node.Params.GetValueOrDefault("mult"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mv) ? mv : 1.0;
                        double add = double.TryParse(node.Params.GetValueOrDefault("add"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var av) ? av : 0.0;
                        node.Outputs["Out"] = HalconFlowBridge.AddImageToCalib(a, b, mult, add);
                        node.ResultSummary = "HALCON add_image";
                        break;
                    }

                    case "halcon_mult_image":
                    {
                        var a = inputs["In"] as CalibImage;
                        var b = inputs["In2"] as CalibImage;
                        if (a == null) throw new InvalidOperationException("HALCON MultImage: 缺少 In");
                        if (b == null) throw new InvalidOperationException("HALCON MultImage: 缺少 In2");
                        double mult = double.TryParse(node.Params.GetValueOrDefault("mult"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mv) ? mv : 0.007843;
                        double add = double.TryParse(node.Params.GetValueOrDefault("add"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var av) ? av : 0.0;
                        node.Outputs["Out"] = HalconFlowBridge.MultImageToCalib(a, b, mult, add);
                        node.ResultSummary = "HALCON mult_image";
                        break;
                    }

                    case "halcon_min_image":
                    {
                        var a = inputs["In"] as CalibImage;
                        var b = inputs["In2"] as CalibImage;
                        if (a == null) throw new InvalidOperationException("HALCON MinImage: 缺少 In");
                        if (b == null) throw new InvalidOperationException("HALCON MinImage: 缺少 In2");
                        node.Outputs["Out"] = HalconFlowBridge.MinImageToCalib(a, b);
                        node.ResultSummary = "HALCON min_image";
                        break;
                    }

                    case "halcon_max_image":
                    {
                        var a = inputs["In"] as CalibImage;
                        var b = inputs["In2"] as CalibImage;
                        if (a == null) throw new InvalidOperationException("HALCON MaxImage: 缺少 In");
                        if (b == null) throw new InvalidOperationException("HALCON MaxImage: 缺少 In2");
                        node.Outputs["Out"] = HalconFlowBridge.MaxImageToCalib(a, b);
                        node.ResultSummary = "HALCON max_image";
                        break;
                    }

                    case "halcon_sobel_amp":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON SobelAmp: 缺少 In");
                        string ft = node.Params.GetValueOrDefault("filterType") ?? "sum_abs";
                        int size = int.TryParse(node.Params.GetValueOrDefault("size"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var sz) ? sz : 3;
                        node.Outputs["Out"] = HalconFlowBridge.SobelAmpToCalib(inImg, ft.Trim(), size);
                        node.ResultSummary = $"HALCON sobel {ft}";
                        break;
                    }

                    case "halcon_smooth_image":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON SmoothImage: 缺少 In");
                        string filter = node.Params.GetValueOrDefault("filter") ?? "gauss";
                        double alpha = double.TryParse(node.Params.GetValueOrDefault("alpha"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var al) ? al : 3.0;
                        node.Outputs["Out"] = HalconFlowBridge.SmoothImageToCalib(inImg, filter.Trim(), alpha);
                        node.ResultSummary = $"HALCON smooth {filter}";
                        break;
                    }

                    case "halcon_gray_erosion_rect":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON GrayErosionRect: 缺少 In");
                        int mh = int.TryParse(node.Params.GetValueOrDefault("maskHeight"), out var mhv) ? mhv : 3;
                        int mw = int.TryParse(node.Params.GetValueOrDefault("maskWidth"), out var mwv) ? mwv : 3;
                        node.Outputs["Out"] = HalconFlowBridge.GrayErosionRectToCalib(inImg, mh, mw);
                        node.ResultSummary = $"HALCON gray_erode {mh}x{mw}";
                        break;
                    }

                    case "halcon_gray_dilation_rect":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON GrayDilationRect: 缺少 In");
                        int mh = int.TryParse(node.Params.GetValueOrDefault("maskHeight"), out var mhv) ? mhv : 3;
                        int mw = int.TryParse(node.Params.GetValueOrDefault("maskWidth"), out var mwv) ? mwv : 3;
                        node.Outputs["Out"] = HalconFlowBridge.GrayDilationRectToCalib(inImg, mh, mw);
                        node.ResultSummary = $"HALCON gray_dilate {mh}x{mw}";
                        break;
                    }

                    case "halcon_auto_threshold":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON AutoThreshold: 缺少 In");
                        double sigma = double.TryParse(node.Params.GetValueOrDefault("sigma"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sg) ? sg : 2.0;
                        node.Outputs["Out"] = HalconFlowBridge.AutoThresholdToCalibGray(inImg, sigma);
                        node.ResultSummary = $"HALCON auto_th σ={sigma:G4}";
                        break;
                    }

                    case "halcon_binary_morph_rect":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON 矩形形态学: 缺少 In");
                        string op = node.Params.GetValueOrDefault("op") ?? "open";
                        int w = int.TryParse(node.Params.GetValueOrDefault("width"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var wv) ? wv : 3;
                        int h = int.TryParse(node.Params.GetValueOrDefault("height"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hv) ? hv : 3;
                        int it = int.TryParse(node.Params.GetValueOrDefault("iterations"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var itv) ? itv : 1;
                        node.Outputs["Out"] = HalconFlowBridge.BinaryMorphRect(inImg, op.Trim(), w, h, it);
                        node.ResultSummary = $"HALCON morph_rect {op}";
                        break;
                    }

                    case "halcon_binary_to_xld":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON 二值→XLD: 缺少 In");
                        double minG = double.TryParse(node.Params.GetValueOrDefault("minGray"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mn) ? mn : 1.0;
                        double maxG = double.TryParse(node.Params.GetValueOrDefault("maxGray"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mx) ? mx : 255.0;
                        string genMode = node.Params.GetValueOrDefault("genContourMode") ?? "border";
                        int minPts = int.TryParse(node.Params.GetValueOrDefault("minContourPoints"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var mp) ? mp : 3;
                        var bundle = HalconFlowBridge.XldContoursFromBinaryGray(inImg, minG, maxG, genMode.Trim(), minPts);
                        node.Outputs["Xld"] = bundle;
                        node.ResultSummary = $"HALCON XLD contours={bundle.ContourCount}";
                        break;
                    }

                    case "halcon_segment_xld":
                    {
                        if (!inputs.TryGetValue("Xld", out var bx) || bx is not HalconXldContourBundle bundleIn)
                            throw new InvalidOperationException("HALCON XLD 分格: 缺少 Xld");
                        string segMode = node.Params.GetValueOrDefault("mode") ?? "lines_circles";
                        int smooth = int.TryParse(node.Params.GetValueOrDefault("smoothCont"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var sm) ? sm : 5;
                        double d1 = double.TryParse(node.Params.GetValueOrDefault("maxLineDist1"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var md1) ? md1 : 4.0;
                        double d2 = double.TryParse(node.Params.GetValueOrDefault("maxLineDist2"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var md2) ? md2 : 2.0;
                        var bundleOut = HalconFlowBridge.SegmentXldBundle(bundleIn, segMode.Trim(), smooth, d1, d2);
                        node.Outputs["XldOut"] = bundleOut;
                        node.ResultSummary = $"HALCON seg XLD → {bundleOut.ContourCount}";
                        break;
                    }

                    case "halcon_largest_blob_mask":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON 最大连通域Mask: 缺少 In");
                        int gs = int.TryParse(node.Params.GetValueOrDefault("gaussSize"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var gsv) ? gsv : 9;
                        double minG = double.TryParse(node.Params.GetValueOrDefault("minGray"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mn) ? mn : 40.0;
                        double maxG = double.TryParse(node.Params.GetValueOrDefault("maxGray"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mx) ? mx : 255.0;
                        node.Outputs["Out"] = HalconFlowBridge.LargestBlobMaskFromGray(inImg, gs, minG, maxG);
                        node.ResultSummary = "HALCON largest_blob_mask";
                        break;
                    }

                    case "halcon_largest_contour_mask":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON 最大轮廓Mask: 缺少 In");
                        int gs = int.TryParse(node.Params.GetValueOrDefault("gaussSize"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var gsv) ? gsv : 9;
                        double minG = double.TryParse(node.Params.GetValueOrDefault("minGray"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mn) ? mn : 40.0;
                        double maxG = double.TryParse(node.Params.GetValueOrDefault("maxGray"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mx) ? mx : 255.0;
                        string genMode = node.Params.GetValueOrDefault("genContourMode") ?? "border";
                        string rankBy = node.Params.GetValueOrDefault("rankBy") ?? "area";
                        int minPts = int.TryParse(node.Params.GetValueOrDefault("minContourPoints"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var mp) ? mp : 3;
                        node.Outputs["Out"] = HalconFlowBridge.LargestContourMaskFromGray(inImg, gs, minG, maxG, genMode.Trim(), rankBy.Trim(), minPts);
                        node.ResultSummary = $"HALCON largest_contour_mask ({rankBy.Trim()})";
                        break;
                    }

                    case "halcon_local_contrast":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON 局部对比度: 缺少 In");
                        int iw = int.TryParse(node.Params.GetValueOrDefault("illumMaskWidth"), out var iwv) ? iwv : 41;
                        int ih = int.TryParse(node.Params.GetValueOrDefault("illumMaskHeight"), out var ihv) ? ihv : 41;
                        double ifac = double.TryParse(node.Params.GetValueOrDefault("illumFactor"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ifv) ? ifv : 0.7;
                        int ew = int.TryParse(node.Params.GetValueOrDefault("emphasizeWidth"), out var ewv) ? ewv : 7;
                        int eh = int.TryParse(node.Params.GetValueOrDefault("emphasizeHeight"), out var ehv) ? ehv : 7;
                        double ef = double.TryParse(node.Params.GetValueOrDefault("emphasizeFactor"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var efv) ? efv : 1.0;
                        node.Outputs["Out"] = HalconFlowBridge.LocalContrastEnhance(inImg, iw, ih, ifac, ew, eh, ef);
                        node.ResultSummary = "HALCON local_contrast";
                        break;
                    }

                    case "halcon_gray_mask":
                    {
                        var img = inputs["Image"] as CalibImage;
                        var mask = inputs["Mask"] as CalibImage;
                        if (img == null) throw new InvalidOperationException("HALCON GrayMask: 缺少 Image");
                        if (mask == null) throw new InvalidOperationException("HALCON GrayMask: 缺少 Mask");
                        double mmn = double.TryParse(node.Params.GetValueOrDefault("maskMin"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mn0) ? mn0 : 1.0;
                        double mmx = double.TryParse(node.Params.GetValueOrDefault("maskMax"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mx0) ? mx0 : 255.0;
                        node.Outputs["Out"] = HalconFlowBridge.GrayMaskApply(img, mask, mmn, mmx);
                        node.ResultSummary = "HALCON gray_mask";
                        break;
                    }

                    case "halcon_binary_morph_circle":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("HALCON 圆形态学: 缺少 In");
                        string op = node.Params.GetValueOrDefault("op") ?? "open";
                        double rad = double.TryParse(node.Params.GetValueOrDefault("radius"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rd) ? rd : 2.5;
                        int it = int.TryParse(node.Params.GetValueOrDefault("iterations"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var itv) ? itv : 2;
                        node.Outputs["Out"] = HalconFlowBridge.BinaryMorphCircle(inImg, op.Trim(), rad, it);
                        node.ResultSummary = $"HALCON morph_circle {op}";
                        break;
                    }

                    case "halcon_xld_sample_points":
                    {
                        if (!inputs.TryGetValue("Xld", out var sx) || sx is not HalconXldContourBundle xb)
                            throw new InvalidOperationException("HALCON XLD 采样点: 缺少 Xld");
                        double spacing = double.TryParse(node.Params.GetValueOrDefault("spacing"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sp) ? sp : 4.0;
                        int maxBars = int.TryParse(node.Params.GetValueOrDefault("maxBars"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var mb) ? mb : 16;
                        var orderRaw = (node.Params.GetValueOrDefault("contourOrder", "list") ?? "list").Trim();
                        bool sortByLength = string.Equals(orderRaw, "length_desc", StringComparison.OrdinalIgnoreCase);
                        var (pts, barIds) = HalconFlowBridge.SamplePointsFromXldBundle(xb, spacing, maxBars, sortByLength);
                        node.Outputs["Points"] = pts;
                        node.Outputs["BarIds"] = barIds;
                        node.ResultSummary = pts.Length == 0
                            ? "skip: empty XLD → empty Points"
                            : $"HALCON xld_pts 输入轮廓={xb.ContourCount} 点={pts.Length}";
                        break;
                    }

                    case "halcon_create_shape_model":
                    {
                        HalconXldContourBundle? xldBundle = null;
                        inputs.TryGetValue("Xld", out var xldObj);
                        xldBundle = xldObj as HalconXldContourBundle;
                        int numLevels = int.TryParse(node.Params.GetValueOrDefault("numLevels"), out var nl) ? nl : 4;
                        double angleStart = double.TryParse(node.Params.GetValueOrDefault("angleStart"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var asv) ? asv : -30.0;
                        double angleExtent = double.TryParse(node.Params.GetValueOrDefault("angleExtent"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var aev) ? aev : 60.0;
                        double angleStep = double.TryParse(node.Params.GetValueOrDefault("angleStep"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var apv) ? apv : 0.5;
                        string optimization = node.Params.GetValueOrDefault("optimization") ?? "auto";
                        string metric = node.Params.GetValueOrDefault("metric") ?? "use_polarity";
                        int contrast = int.TryParse(node.Params.GetValueOrDefault("contrast"), out var c) ? c : 30;
                        int minContrast = int.TryParse(node.Params.GetValueOrDefault("minContrast"), out var mc) ? mc : 5;
                        long modelId = HalconFlowBridge.CreateShapeModelFromXld(xldBundle, numLevels, angleStart, angleExtent, angleStep, optimization, metric, contrast, minContrast);
                        node.Outputs["ModelId"] = modelId;
                        node.ResultSummary = $"HALCON CreateShapeModel ModelId={modelId}";
                        break;
                    }

                    case "halcon_find_shape_model":
                    {
                        var findImg = inputs["In"] as CalibImage;
                        if (findImg == null) throw new InvalidOperationException("HALCON FindShapeModel: 缺少 In");
                        long modelId = HalconFlowBridge.ResolveRegisteredShapeModelId(Convert.ToInt64(inputs["ModelId"]));
                        if (modelId < 0)
                            throw new InvalidOperationException(
                                "HALCON FindShapeModel: ModelId 须为有效的形状模板(.shm)，勿接可变形模型或已释放的 ID");
                        double angleStart = double.TryParse(node.Params.GetValueOrDefault("angleStart"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var asv) ? asv : -30.0;
                        double angleExtent = double.TryParse(node.Params.GetValueOrDefault("angleExtent"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var aev) ? aev : 60.0;
                        double minScore = double.TryParse(node.Params.GetValueOrDefault("minScore"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : 0.5;
                        int numMatches = ResolveNumMatchesFromLattice(node, "numMatches");
                        double maxOverlap = double.TryParse(node.Params.GetValueOrDefault("maxOverlap"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mo) ? mo : 0.5;
                        string subPixel = node.Params.GetValueOrDefault("subPixel") ?? "interpolation";
                        int numLevels = int.TryParse(node.Params.GetValueOrDefault("numLevels"), out var nl) ? nl : 0;
                        double greediness = double.TryParse(node.Params.GetValueOrDefault("greediness"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : 0.9;
                        static double Pfind(IReadOnlyDictionary<string, string> p, string key, double def) =>
                            double.TryParse(p.GetValueOrDefault(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
                        var (rows, cols, angles, scores) = HalconFlowBridge.FindShapeModelWithFallback(
                            findImg, modelId, angleStart, angleExtent, minScore, numMatches, maxOverlap, subPixel, numLevels, greediness);
                        double endW = Pfind(node.Params, "endScoreWeight", HalconFlowBridge.DefaultEndScoreWeight);
                        if (endW > 0 && rows.Length > 0)
                        {
                            (_, _, _, scores) = HalconFlowBridge.ApplyShapeMatchEndScoreWeight(
                                findImg, modelId, rows, cols, angles, scores, endW,
                                Pfind(node.Params, "endArcFraction", HalconFlowBridge.DefaultEndArcFraction));
                        }
                        node.Outputs["Row"] = rows;
                        node.Outputs["Column"] = cols;
                        node.Outputs["Angle"] = angles;
                        node.Outputs["Score"] = scores;
                        node.ResultSummary = $"HALCON FindShapeModel 找到 {rows.Length} 个匹配";
                        break;
                    }

                    case "halcon_load_shape_model":
                    {
                        string configuredPath = node.Params.GetValueOrDefault("filePath", "")?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(configuredPath))
                            throw new InvalidOperationException("HALCON 加载形状模型: filePath 为空");
                        string resolvedPath = ResolveCompositeFlowPath(configuredPath, compositeInnerFlowBaseDir);
                        long modelId = HalconFlowBridge.LoadShapeModelFromFile(resolvedPath);
                        node.Outputs["ModelId"] = modelId;
                        node.ResultSummary = $"HALCON 已加载 .shm ModelId={modelId}";
                        break;
                    }

                    case "halcon_create_deformable_model":
                    {
                        inputs.TryGetValue("Xld", out var xldObj);
                        var xldBundle = xldObj as HalconXldContourBundle;
                        int numLevels = int.TryParse(node.Params.GetValueOrDefault("numLevels"), out var nl) ? nl : 4;
                        double angleStart = double.TryParse(node.Params.GetValueOrDefault("angleStart"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var asv) ? asv : -30.0;
                        double angleExtent = double.TryParse(node.Params.GetValueOrDefault("angleExtent"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var aev) ? aev : 60.0;
                        double angleStep = double.TryParse(node.Params.GetValueOrDefault("angleStep"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var apv) ? apv : 0.5;
                        string optimization = node.Params.GetValueOrDefault("optimization") ?? "auto";
                        string metric = node.Params.GetValueOrDefault("metric") ?? "ignore_local_polarity";
                        int minContrast = int.TryParse(node.Params.GetValueOrDefault("minContrast"), out var mc) ? mc : 5;
                        double scaleMin = double.TryParse(node.Params.GetValueOrDefault("scaleMin"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var smn) ? smn : 0.97;
                        double scaleMax = double.TryParse(node.Params.GetValueOrDefault("scaleMax"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var smx) ? smx : 1.03;
                        string deformableKind = (node.Params.GetValueOrDefault("deformableKind") ?? "local").Trim();
                        bool planar = deformableKind.Equals("planar", StringComparison.OrdinalIgnoreCase)
                            || deformableKind.Equals("planar_uncalib", StringComparison.OrdinalIgnoreCase)
                            || deformableKind.Equals("perspective", StringComparison.OrdinalIgnoreCase);
                        long modelId = HalconFlowBridge.CreateShapeModelFromXld(xldBundle, new HalconShapeModelCreateOptions
                        {
                            ModelKind = planar ? HalconShapeModelKind.PlanarDeformable : HalconShapeModelKind.Deformable,
                            SourceKind = HalconShapeModelSourceKind.ThresholdXld,
                            NumLevels = numLevels,
                            AngleStartDeg = angleStart,
                            AngleExtentDeg = angleExtent,
                            AngleStepDeg = angleStep,
                            Optimization = optimization,
                            Metric = metric,
                            MinContrast = minContrast,
                            ScaleMin = scaleMin,
                            ScaleMax = scaleMax
                        });
                        node.Outputs["ModelId"] = modelId;
                        node.ResultSummary = $"HALCON CreateDeformableModel ModelId={modelId}";
                        break;
                    }

                    case "halcon_load_deformable_model":
                    {
                        string configuredPath = node.Params.GetValueOrDefault("filePath", "")?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(configuredPath))
                            throw new InvalidOperationException("HALCON 加载可变形模型: filePath 为空");
                        string resolvedPath = ResolveCompositeFlowPath(configuredPath, compositeInnerFlowBaseDir);
                        long modelId = HalconFlowBridge.LoadDeformableModelFromFile(resolvedPath);
                        node.Outputs["ModelId"] = modelId;
                        string subtypeLabel = HalconFlowBridge.GetDeformableModelSubtypeLabel(modelId);
                        node.ResultSummary = $"HALCON 已加载 .dfm ({subtypeLabel}) ModelId={modelId}";
                        break;
                    }

                    case "halcon_coarse_shape_match":
                    {
                        var coarseImg = inputs["In"] as CalibImage;
                        if (coarseImg == null)
                            throw new InvalidOperationException("HALCON 粗定位: 缺少 In");
                        long modelId = HalconFlowBridge.ResolveRegisteredShapeModelId(Convert.ToInt64(inputs["ModelId"]));
                        if (modelId < 0)
                            throw new InvalidOperationException(
                                "HALCON 粗定位: ModelId 须为有效的形状模板(.shm)，请接 create/load_shape_model，勿接可变形模型或已释放的 ID");
                        static double Pc(IReadOnlyDictionary<string, string> p, string key, double def) =>
                            double.TryParse(p.GetValueOrDefault(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
                        static int Pic(IReadOnlyDictionary<string, string> p, string key, int def) =>
                            int.TryParse(p.GetValueOrDefault(key), out var v) ? v : def;
                        static bool Pbc(IReadOnlyDictionary<string, string> p, string key, bool def) =>
                            bool.TryParse(p.GetValueOrDefault(key), out var v) ? v : def;
                        static string Psc(IReadOnlyDictionary<string, string> p, string key, string def) =>
                            string.IsNullOrWhiteSpace(p.GetValueOrDefault(key)) ? def : p[key]!.Trim();
                        static double PcAlias(IReadOnlyDictionary<string, string> p, string primary, string legacy, double def)
                        {
                            if (double.TryParse(p.GetValueOrDefault(primary), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v1))
                                return v1;
                            if (double.TryParse(p.GetValueOrDefault(legacy), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v2))
                                return v2;
                            return def;
                        }

                        double coarseScaleMin = PcAlias(node.Params, "coarseScaleMin", "scaleMin", 1.0);
                        double coarseScaleMax = PcAlias(node.Params, "coarseScaleMax", "scaleMax", 1.0);
                        inputs.TryGetValue("CoarseAngle", out var caRefObj);
                        double angleRef = HalconFlowBridge.TryReadCoarseScalar(caRefObj, out double refAng) ? refAng : 0;
                        var (coarseAbsStart, coarseAbsExtent) = HalconFlowBridge.ResolveRelativeAngleRangeDeg(
                            angleRef,
                            Pc(node.Params, "angleStart", -30),
                            Pc(node.Params, "angleExtent", 60));
                        var (rows, cols, angles, scales, scores) = HalconFlowBridge.CoarseShapeMatch(
                            coarseImg, modelId,
                            coarseAbsStart,
                            coarseAbsExtent,
                            Pc(node.Params, "minScore", 0.4),
                            ResolveNumMatchesFromLattice(node, "numMatches"),
                            Pc(node.Params, "maxOverlap", 0.5),
                            Psc(node.Params, "subPixel", "none"),
                            Pic(node.Params, "numLevels", 0),
                            Pc(node.Params, "greediness", 0.85),
                            Pbc(node.Params, "allowRetry", false),
                            coarseScaleMin,
                            coarseScaleMax,
                            Pc(node.Params, "endScoreWeight", HalconFlowBridge.DefaultEndScoreWeight),
                            Pc(node.Params, "endArcFraction", HalconFlowBridge.DefaultEndArcFraction));
                        node.Outputs["Row"] = rows;
                        node.Outputs["Column"] = cols;
                        node.Outputs["Angle"] = angles;
                        node.Outputs["Scale"] = scales;
                        node.Outputs["Score"] = scores;
                        if (scales.Length > 0)
                            node.ResultSummary = $"HALCON 粗定位 {rows.Length} 个候选, 搜索缩放[{coarseScaleMin:F3},{coarseScaleMax:F3}] 命中[{scales.Min():F3},{scales.Max():F3}]";
                        else
                            node.ResultSummary = $"HALCON 粗定位 {rows.Length} 个候选, 搜索缩放[{coarseScaleMin:F3},{coarseScaleMax:F3}]";
                        break;
                    }

                    case "halcon_coarse_shape_reduce_domain":
                    {
#if HALCON_ENABLED
                        var batch = BuildCoarseShapeMaskBatchForNode(node, inputs);
                        if (batch.Count == 0)
                            throw new InvalidOperationException("HALCON 粗形状Mask: 无粗候选");

                        int candidateIndex = int.TryParse(
                            node.Params.GetValueOrDefault("candidateIndex", "0"),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out int ci)
                            ? ci
                            : 0;
                        if (candidateIndex < 0 || candidateIndex >= batch.Count)
                            throw new InvalidOperationException(
                                $"HALCON 粗形状Mask: candidateIndex={candidateIndex} 超出范围 [0,{batch.Count - 1}]");

                        SetCoarseShapeMaskRoundOutputs(node, batch, candidateIndex);
                        node.ResultSummary = $"粗形状填充Mask {candidateIndex + 1}/{batch.Count}";
                        break;
#else
                        throw new InvalidOperationException("HALCON 粗形状Mask: 需要 HALCON 支持编译");
#endif
                    }

                    case "halcon_reduce_domain_by_mask":
                    {
#if HALCON_ENABLED
                        var srcImg = inputs["Image"] as CalibImage;
                        if (srcImg == null)
                            throw new InvalidOperationException("HALCON Mask域内图: 缺少 Image");
                        if (inputs["Mask"] is not CalibImage maskImg)
                            throw new InvalidOperationException("HALCON Mask域内图: 缺少单张 Mask");

                        CalibImage domainOut = HalconFlowBridge.ReduceDomainByMask(srcImg, maskImg);
                        node.Outputs["Out"] = domainOut;
                        node.ResultSummary = $"reduce_domain 域内图 {srcImg.Width}×{srcImg.Height}";
                        break;
#else
                        throw new InvalidOperationException("HALCON Mask域内图: 需要 HALCON 支持编译");
#endif
                    }

                    case "halcon_fine_scaled_shape_match":
                    {
#if HALCON_ENABLED
                        if (inputs["In"] is not CalibImage scaledDomainImg)
                            throw new InvalidOperationException(
                                "HALCON 缩放形状精匹配: 缺少 In（接 halcon_reduce_domain_by_mask 的 Out）。");
                        long scaledModelId = HalconFlowBridge.ResolveRegisteredShapeModelId(
                            Convert.ToInt64(inputs["ModelId"]));
                        if (scaledModelId < 0)
                            throw new InvalidOperationException("HALCON 缩放形状精匹配: ModelId 无效或已释放（须为 ScaledShape .shm）");
                        CalibImage? scaledFullImg = inputs.TryGetValue("FullImage", out var sfiObj) ? sfiObj as CalibImage : null;
                        inputs.TryGetValue("CoarseRow", out var scrObj);
                        inputs.TryGetValue("CoarseColumn", out var sccObj);
                        inputs.TryGetValue("CoarseAngle", out var scaObj);
                        inputs.TryGetValue("CoarseScale", out var scsObj);
                        double coarseRowVal = 0, coarseColVal = 0, coarseAngleVal = 0, coarseScaleVal = 1.0;
                        bool fixRow = scrObj != null && HalconFlowBridge.TryReadCoarseScalar(scrObj, out coarseRowVal);
                        bool fixCol = sccObj != null && HalconFlowBridge.TryReadCoarseScalar(sccObj, out coarseColVal);
                        bool fixAngle = scaObj != null && HalconFlowBridge.TryReadCoarseScalar(scaObj, out coarseAngleVal);
                        bool fixScale = scsObj != null && HalconFlowBridge.TryReadCoarseScalar(scsObj, out coarseScaleVal);
                        var scaledFixedPose = new ScaledShapeFineFixedPose
                        {
                            FixRow = fixRow,
                            FixCol = fixCol,
                            FixAngle = fixAngle,
                            FixScale = fixScale,
                            Row = coarseRowVal,
                            Col = coarseColVal,
                            AngleDeg = coarseAngleVal,
                            Scale = coarseScaleVal
                        };
                        double scaledPoseAngle = fixAngle ? coarseAngleVal : 0;

                        static double Spf(IReadOnlyDictionary<string, string> p, string key, double def) =>
                            double.TryParse(p.GetValueOrDefault(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
                        static int Spfi(IReadOnlyDictionary<string, string> p, string key, int def) =>
                            int.TryParse(p.GetValueOrDefault(key), out var v) ? v : def;
                        static string Spfs(IReadOnlyDictionary<string, string> p, string key, string def) =>
                            string.IsNullOrWhiteSpace(p.GetValueOrDefault(key)) ? def : p[key]!.Trim();

                        var (scaledFineRelStart, scaledFineRelExtent) = HalconFlowBridge.ResolveFineRelativeAngleRangeFromParams(
                            node.Params, "fineAngleStart", "fineAngleExtent", "fineAngleMargin", 5);
                        var (scaledFineSearchCenter, scaledFineSearchMargin) = HalconFlowBridge.ResolveFineAngleSearchCenterMargin(
                            scaledPoseAngle, scaledFineRelStart, scaledFineRelExtent);

                        string shapeContourMode = Spfs(node.Params, "shapeContourMode", "first");
                        bool wantShapeContour = !string.Equals(shapeContourMode, "none", StringComparison.OrdinalIgnoreCase);

                        HalconCoarseFineMatchResult scaledFineResult = HalconFlowBridge.FineScaledShapeMatchOnDomainImage(
                            scaledDomainImg,
                            scaledModelId,
                            scaledFixedPose,
                            fixRow ? coarseRowVal : double.NaN,
                            fixCol ? coarseColVal : double.NaN,
                            scaledPoseAngle,
                            scaledFineSearchMargin,
                            Spf(node.Params, "fineMinScore", 0.45),
                            Spfi(node.Params, "fineNumLevels", 0),
                            Spf(node.Params, "fineGreediness", 0.75),
                            Spf(node.Params, "fineScaleMin", 0.97),
                            Spf(node.Params, "fineScaleMax", 1.03),
                            wantShapeContour,
                            Spf(node.Params, "roiMarginPx", 12),
                            Spf(node.Params, "maxRoiHalfPx", 120),
                            scaledFullImg,
                            Spf(node.Params, "fineEndScoreWeight", HalconFlowBridge.DefaultEndScoreWeight),
                            Spf(node.Params, "fineEndArcFraction", HalconFlowBridge.DefaultEndArcFraction),
                            angleSearchCenterDeg: scaledFineSearchCenter,
                            angleSearchMarginDeg: scaledFineSearchMargin);

                        scaledDomainImg.RefreshProperties();
                        var fixedDofParts = new List<string>(4);
                        if (fixRow) fixedDofParts.Add($"row={coarseRowVal:G4}");
                        if (fixCol) fixedDofParts.Add($"col={coarseColVal:G4}");
                        if (fixAngle) fixedDofParts.Add($"angle={coarseAngleVal:G4}°");
                        if (fixScale) fixedDofParts.Add($"scale={coarseScaleVal:G4}");
                        string fixedDofNote = fixedDofParts.Count > 0
                            ? $"固定[{string.Join(", ", fixedDofParts)}], "
                            : string.Empty;
                        AppendLog(
                            $"[HALCON] 缩放精匹配: 域内图 {scaledDomainImg.Width}x{scaledDomainImg.Height}, " +
                            fixedDofNote +
                            $"thread_num={HalconRuntimeSettings.LastParallelFindThreadNum}");

                        node.Outputs["Row"] = scaledFineResult.FineRows;
                        node.Outputs["Column"] = scaledFineResult.FineCols;
                        node.Outputs["Angle"] = scaledFineResult.FineAngles;
                        node.Outputs["Scale"] = scaledFineResult.FineScales;
                        node.Outputs["Score"] = scaledFineResult.FineScores;
                        if (scaledFineResult.DeformedXld != null)
                            node.Outputs["ShapeXld"] = scaledFineResult.DeformedXld;

                        node.ResultSummary = scaledFineResult.FineCount == 0
                            ? fixedDofParts.Count > 0
                                ? $"缩放精匹配无结果 ({string.Join(", ", fixedDofParts)})"
                                : "缩放精匹配无结果"
                            : fixedDofParts.Count > 0
                                ? $"缩放精 1 sc={scaledFineResult.FineScores[0]:F3} ({string.Join(", ", fixedDofParts)})"
                                : $"缩放精 1 sc={scaledFineResult.FineScores[0]:F3} scale={scaledFineResult.FineScales[0]:G4}";
                        break;
#else
                        throw new InvalidOperationException("HALCON 缩放形状精匹配: 需要 HALCON 支持编译");
#endif
                    }

                    case "halcon_fine_deformable_match":
                    {
#if HALCON_ENABLED
                        if (inputs["In"] is not CalibImage domainImg)
                            throw new InvalidOperationException(
                                "HALCON 可变形精匹配: 缺少 In（接 halcon_reduce_domain_by_mask 的 Out）。" +
                                "若在 flow_loop 中由列表驱动，请确认粗/Mask 本轮有候选且 InList 已正确连线。");
                        long deformId = HalconFlowBridge.ResolveRegisteredDeformableModelId(
                            Convert.ToInt64(inputs["DeformableModelId"]));
                        if (deformId < 0)
                            throw new InvalidOperationException("HALCON 可变形精匹配: DeformableModelId 无效或已释放");
                        long rigidId = ResolveFineMatchRigidModelId(inputs);
                        CalibImage? fullImg = inputs.TryGetValue("FullImage", out var fiObj) ? fiObj as CalibImage : null;
                        inputs.TryGetValue("CoarseRow", out var crObj);
                        inputs.TryGetValue("CoarseColumn", out var ccObj);
                        inputs.TryGetValue("CoarseAngle", out var caObj);
                        double anchorRow = HalconFlowBridge.TryReadCoarseScalar(crObj, out double ar) ? ar : double.NaN;
                        double anchorCol = HalconFlowBridge.TryReadCoarseScalar(ccObj, out double ac) ? ac : double.NaN;
                        bool hasCoarseAngle = HalconFlowBridge.TryReadCoarseScalar(caObj, out double anchorAng);
                        double poseAngle = hasCoarseAngle ? anchorAng : 0;

                        static double Pf(IReadOnlyDictionary<string, string> p, string key, double def) =>
                            double.TryParse(p.GetValueOrDefault(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
                        static int Pfi(IReadOnlyDictionary<string, string> p, string key, int def) =>
                            int.TryParse(p.GetValueOrDefault(key), out var v) ? v : def;
                        static bool Pfb(IReadOnlyDictionary<string, string> p, string key, bool def) =>
                            bool.TryParse(p.GetValueOrDefault(key), out var v) ? v : def;
                        static string Pfs(IReadOnlyDictionary<string, string> p, string key, string def) =>
                            string.IsNullOrWhiteSpace(p.GetValueOrDefault(key)) ? def : p[key]!.Trim();

                        var (fineRelStart, fineRelExtent) = HalconFlowBridge.ResolveFineRelativeAngleRangeFromParams(
                            node.Params, "fineAngleStart", "fineAngleExtent", "fineAngleMargin", 5);
                        var (fineSearchCenter, fineSearchMargin) = HalconFlowBridge.ResolveFineAngleSearchCenterMargin(
                            poseAngle, fineRelStart, fineRelExtent);

                        string contourMode = Pfs(node.Params, "deformedContourMode", "first");
                        bool wantDeformed = !string.Equals(contourMode, "none", StringComparison.OrdinalIgnoreCase);

                        HalconCoarseFineMatchResult fineResult = HalconFlowBridge.FineDeformableMatchOnDomainImage(
                            domainImg,
                            deformId,
                            anchorRow,
                            anchorCol,
                            poseAngle,
                            fineSearchMargin,
                            Pf(node.Params, "fineMinScore", 0.45),
                            Pfi(node.Params, "fineNumLevels", 0),
                            Pf(node.Params, "fineGreediness", 0.75),
                            Pf(node.Params, "fineScaleMin", 0.97),
                            Pf(node.Params, "fineScaleMax", 1.03),
                            wantDeformed,
                            Pfb(node.Params, "fineAllowFallback", false),
                            Pf(node.Params, "roiMarginPx", 12),
                            Pf(node.Params, "maxRoiHalfPx", 120),
                            fullImg,
                            rigidId,
                            Pf(node.Params, "fineEndScoreWeight", HalconFlowBridge.DefaultEndScoreWeight),
                            Pf(node.Params, "fineEndArcFraction", HalconFlowBridge.DefaultEndArcFraction),
                            angleSearchCenterDeg: fineSearchCenter,
                            angleSearchMarginDeg: fineSearchMargin);

                        domainImg.RefreshProperties();
                        AppendLog(
                            $"[HALCON] 精匹配: 域内图 {domainImg.Width}x{domainImg.Height}, " +
                            $"thread_num={HalconRuntimeSettings.LastParallelFindThreadNum}, " +
                            $"parallelize={HalconRuntimeSettings.LastParallelFindParallelize}");

                        node.Outputs["Row"] = fineResult.FineRows;
                        node.Outputs["Column"] = fineResult.FineCols;
                        node.Outputs["Angle"] = fineResult.FineAngles;
                        node.Outputs["Score"] = fineResult.FineScores;
                        if (fineResult.DeformedXld != null)
                            node.Outputs["DeformedXld"] = fineResult.DeformedXld;

                        node.ResultSummary = fineResult.FineCount == 0
                            ? hasCoarseAngle
                                ? $"精匹配无结果 (CoarseAngle={anchorAng:G}°)"
                                : "精匹配无结果"
                            : hasCoarseAngle
                                ? $"精 1 sc={fineResult.FineScores[0]:F3} coarseAng={anchorAng:G}°"
                                : $"精 1 个 sc={fineResult.FineScores[0]:F3}";
                        break;
#else
                        throw new InvalidOperationException("HALCON 可变形精匹配: 需要 HALCON 支持编译");
#endif
                    }

                    case "halcon_coarse_fine_shape_match":
                    {
                        var matchImg = inputs["In"] as CalibImage;
                        if (matchImg == null)
                            throw new InvalidOperationException("HALCON 粗精匹配: 缺少 In");
                        long rigidId = HalconFlowBridge.ResolveRegisteredShapeModelId(Convert.ToInt64(inputs["RigidModelId"]));
                        if (rigidId < 0)
                            throw new InvalidOperationException("HALCON 粗精匹配: RigidModelId 须为有效的形状模板(.shm)");
                        long deformId = HalconFlowBridge.ResolveRegisteredDeformableModelId(Convert.ToInt64(inputs["DeformableModelId"]));
                        if (deformId < 0)
                            throw new InvalidOperationException("HALCON 粗精匹配: DeformableModelId 须为有效的可变形模型(.dfm)");

                        static double P(IReadOnlyDictionary<string, string> p, string key, double def) =>
                            double.TryParse(p.GetValueOrDefault(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
                        static int Pi(IReadOnlyDictionary<string, string> p, string key, int def) =>
                            int.TryParse(p.GetValueOrDefault(key), out var v) ? v : def;

                        static bool Pb(IReadOnlyDictionary<string, string> p, string key, bool def) =>
                            bool.TryParse(p.GetValueOrDefault(key), out var v) ? v : def;
                        static string Ps(IReadOnlyDictionary<string, string> p, string key, string def) =>
                            string.IsNullOrWhiteSpace(p.GetValueOrDefault(key)) ? def : p[key]!.Trim();
                        static double Popt(IReadOnlyDictionary<string, string> p, string key, double fallback) =>
                            string.IsNullOrWhiteSpace(p.GetValueOrDefault(key))
                                ? fallback
                                : (double.TryParse(p[key], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback);
                        static double PAlias(IReadOnlyDictionary<string, string> p, string primary, string legacy, double def)
                        {
                            if (double.TryParse(p.GetValueOrDefault(primary), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v1))
                                return v1;
                            if (double.TryParse(p.GetValueOrDefault(legacy), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v2))
                                return v2;
                            return def;
                        }

                        double coarseEndW = P(node.Params, "endScoreWeight", HalconFlowBridge.DefaultEndScoreWeight);
                        double coarseEndArc = P(node.Params, "endArcFraction", HalconFlowBridge.DefaultEndArcFraction);
                        var (coarseAbsStart, coarseAbsExtent) = HalconFlowBridge.ResolveRelativeAngleRangeDeg(
                            0,
                            P(node.Params, "coarseAngleStart", -30),
                            P(node.Params, "coarseAngleExtent", 60));
                        var (fineRelStart, fineRelExtent) = HalconFlowBridge.ResolveFineRelativeAngleRangeFromParams(
                            node.Params, "fineAngleStart", "fineAngleExtent", "fineAngleMargin", 5);

                        var result = HalconFlowBridge.CoarseFineShapeMatch(
                            matchImg,
                            rigidId,
                            deformId,
                            coarseAbsStart,
                            coarseAbsExtent,
                            P(node.Params, "coarseMinScore", 0.4),
                            ResolveNumMatchesFromLattice(node, "coarseNumMatches"),
                            0.5,
                            Ps(node.Params, "coarseSubPixel", "none"),
                            Pi(node.Params, "coarseNumLevels", 0),
                            P(node.Params, "coarseGreediness", 0.85),
                            Pb(node.Params, "coarseAllowRetry", false),
                            PAlias(node.Params, "coarseScaleMin", "scaleMin", 1.0),
                            PAlias(node.Params, "coarseScaleMax", "scaleMax", 1.0),
                            P(node.Params, "fineAngleMargin", 5),
                            P(node.Params, "fineMinScore", 0.45),
                            Pi(node.Params, "fineNumLevels", 0),
                            P(node.Params, "fineGreediness", 0.75),
                            P(node.Params, "fineScaleMin", 0.97),
                            P(node.Params, "fineScaleMax", 1.03),
                            P(node.Params, "roiMarginPx", 20),
                            P(node.Params, "maxRoiHalfPx", 0),
                            Pi(node.Params, "maxFineMatches", 2),
                            Ps(node.Params, "deformedContourMode", "first"),
                            Pb(node.Params, "fineAllowFallback", false),
                            rigidContourFallback: true,
                            endScoreWeight: coarseEndW,
                            endArcFraction: coarseEndArc,
                            fineEndScoreWeight: Popt(node.Params, "fineEndScoreWeight", coarseEndW),
                            fineEndArcFraction: Popt(node.Params, "fineEndArcFraction", coarseEndArc),
                            fineAngleRelativeStartDeg: fineRelStart,
                            fineAngleRelativeExtentDeg: fineRelExtent);

                        node.Outputs["Row"] = result.FineRows;
                        node.Outputs["Column"] = result.FineCols;
                        node.Outputs["Angle"] = result.FineAngles;
                        node.Outputs["Score"] = result.FineScores;
                        node.Outputs["CoarseRow"] = result.CoarseRows;
                        node.Outputs["CoarseColumn"] = result.CoarseCols;
                        node.Outputs["CoarseAngle"] = result.CoarseAngles;
                        node.Outputs["CoarseScale"] = result.CoarseScales;
                        node.Outputs["CoarseScore"] = result.CoarseScores;
                        if (result.DeformedXld != null)
                            node.Outputs["DeformedXld"] = result.DeformedXld;

                        node.ResultSummary = result.CoarseCount == 0
                            ? "粗定位无结果"
                            : $"粗 {result.CoarseCount} → 精 {result.FineCount}";
                        break;
                    }
#endif

                    case "halcon_shape_match_centers":
                    {
                        double[] rows = inputs.TryGetValue("Row", out var rObj) && rObj is double[] ra ? ra : Array.Empty<double>();
                        double[] cols = inputs.TryGetValue("Column", out var cObj) && cObj is double[] ca ? ca : Array.Empty<double>();
                        int[]? gridRow = inputs.TryGetValue("GridRow", out var grObj) && grObj is int[] gra ? gra : null;
                        int[]? gridCol = inputs.TryGetValue("GridCol", out var gcObj) && gcObj is int[] gca ? gca : null;
                        int n = Math.Min(rows.Length, cols.Length);
                        string sortMode = (node.Params.GetValueOrDefault("sortMode", "yx") ?? "yx").Trim().ToLowerInvariant();

                        var order = SortShapeMatchCenterIndices(n, rows, cols, gridRow, gridCol, sortMode);

                        var pts = new Point2D[n];
                        for (int k = 0; k < n; k++)
                        {
                            int i = order[k];
                            pts[k] = new Point2D(cols[i], rows[i]);
                        }

                        node.Outputs["Points"] = pts;
                        node.ResultSummary = n == 0
                            ? "无匹配中心"
                            : $"匹配中心 {n} 点（排序 {sortMode}）";
                        break;
                    }

                    case "halcon_shape_match_grid_to_trajectory":
                    {
#if !HALCON_ENABLED
                        throw new NotSupportedException("HALCON 落格匹配轮廓→轨迹 需要启用 HALCON");
#else
                        if (!inputs.TryGetValue("ModelId", out var midTr) || midTr is not long modelIdTr)
                            throw new InvalidOperationException("落格匹配轮廓→轨迹: 请连接 ModelId（与 FindShapeModel 相同模板）");
                        double[] rows = inputs.TryGetValue("Row", out var rTr) && rTr is double[] raTr ? raTr : Array.Empty<double>();
                        double[] cols = inputs.TryGetValue("Column", out var cTr) && cTr is double[] caTr ? caTr : Array.Empty<double>();
                        inputs.TryGetValue("Angle", out var aTr);
                        var anglesTr = aTr as double[];
                        inputs.TryGetValue("GridRow", out var grTr);
                        inputs.TryGetValue("GridCol", out var gcTr);
                        var gridRow = grTr as int[];
                        var gridCol = gcTr as int[];
                        int contourLevel = int.TryParse(
                            node.Params.GetValueOrDefault("contourLevel", "1"),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var clTr)
                            ? clTr
                            : 1;
                        string contourMode = node.Params.GetValueOrDefault("contourMode", "outer") ?? "outer";
                        string connectOrder = node.Params.GetValueOrDefault("connectOrder", "col_major") ?? "col_major";
                        string barIdSource = node.Params.GetValueOrDefault("barIdSource", "per_match") ?? "per_match";
                        double closeTolPx = double.TryParse(
                            node.Params.GetValueOrDefault("closeTolPx", "0.5")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var ctTr)
                            ? ctTr
                            : 0.5;
                        double defaultZ = double.TryParse(
                            node.Params.GetValueOrDefault("defaultZ", "0")?.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var dzTr)
                            ? dzTr
                            : 0;
                        var (latticeGridRowsTr, latticeGridColsTr) = ResolveLatticeGridOptional(node);

                        var traj = HalconShapeMatchGridTrajectory.Build(
                            modelIdTr,
                            rows,
                            cols,
                            anglesTr,
                            gridRow,
                            gridCol,
                            contourLevel,
                            contourMode,
                            connectOrder,
                            barIdSource,
                            closeTolPx,
                            defaultZ,
                            latticeGridRowsTr,
                            latticeGridColsTr);
                        node.Outputs["Points"] = traj.Points;
                        node.Outputs["BarIds"] = traj.BarIds;
                        node.Outputs["GroupBarIds"] = traj.GroupBarIds;
                        node.Outputs["SamplePts"] = traj.SamplePts;
                        node.ResultSummary = traj.OrderSummary;
#endif
                        break;
                    }

                    case "halcon_chain_strip_pick_uv_grid":
                    {
                        double[] rows = inputs.TryGetValue("Row", out var rUv) && rUv is double[] raUv ? raUv : Array.Empty<double>();
                        double[] cols = inputs.TryGetValue("Column", out var cUv) && cUv is double[] caUv ? caUv : Array.Empty<double>();
                        inputs.TryGetValue("Angle", out var aUv);
                        double[]? anglesUv = aUv as double[];
                        inputs.TryGetValue("Score", out var sUv);
                        double[]? scoresUv = sUv as double[];

                        var (gridRowsUv, gridColsUv) = ResolveLatticeGrid(node, 8, 2);
                        double minScoreUv = double.TryParse(node.Params.GetValueOrDefault("minScoreKeep"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double msUv) ? msUv : 0;
                        double snapTolUv = double.TryParse(node.Params.GetValueOrDefault("snapTolerancePx"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double stUv) ? stUv : 0;

                        double latticeHintDeg = double.NaN;
                        if (inputs.TryGetValue("ChainBootstrapAngle", out var latObj) && latObj is double ld && !double.IsNaN(ld))
                            latticeHintDeg = ld;

                        int[]? bootIdx = inputs.TryGetValue("BootstrapPickIndices", out var bpiUv) && bpiUv is int[] bi && bi.Length > 0
                            ? bi
                            : null;

                        double pcaChainDeg = double.NaN;
                        if (inputs.TryGetValue("PcaChainAngle", out var pcaObj) && pcaObj is double pd && !double.IsNaN(pd))
                            pcaChainDeg = pd;

                        string? svgOutPath = null;
                        string svgParam = node.Params.GetValueOrDefault("uvProjectionSvg") ?? "";
                        if (!string.IsNullOrWhiteSpace(svgParam))
                            svgOutPath = ResolveCompositeFlowPath(svgParam, compositeInnerFlowBaseDir);

                        string diagTagUv = $"{node.Def.DisplayName}#{node.Id.ToString()[..8]}";
                        bool wantLogUv = HalconShapeMatchGridDiagnostics.ShouldLogForParam(
                            node.Params.GetValueOrDefault("debugLog", "auto"), MirrorErrorsToStderr);
                        if (wantLogUv && !HalconShapeMatchGridDiagnostics.IsEnabled)
                            HalconShapeMatchGridDiagnostics.Enable(mirrorConsole: MirrorErrorsToStderr, sessionName: diagTagUv);

                        var pickUv = HalconShapeMatchLatticePick.PickUvGridAfterBootstrap(
                            rows, cols, anglesUv, scoresUv,
                            gridRowsUv, gridColsUv, latticeHintDeg, bootIdx,
                            minScoreUv, snapTolUv,
                            wantLogUv ? diagTagUv : null, svgOutPath, pcaChainDeg);

                        node.Outputs["Row"] = pickUv.Rows;
                        node.Outputs["Column"] = pickUv.Cols;
                        node.Outputs["Angle"] = pickUv.Angles;
                        node.Outputs["Score"] = pickUv.Scores;
                        node.Outputs["GridRow"] = pickUv.GridRow;
                        node.Outputs["GridCol"] = pickUv.GridCol;
                        node.Outputs["LatticeAngle"] = pickUv.LatticeAngleDeg;
                        node.Outputs["ChainDirectionAngle"] = pickUv.ChainDirectionAngleDeg;
                        node.Outputs["ConsensusMatchAngle"] = pickUv.ConsensusMatchAngleDeg;
                        node.Outputs["ConsensusPickIndices"] = pickUv.KeptIndices;
                        node.Outputs["LatticeRows"] = pickUv.LatticeRows;
                        node.Outputs["LatticeCols"] = pickUv.LatticeCols;
                        node.Outputs["AxesSwapped"] = pickUv.AxesSwapped ? 1 : 0;
                        node.Outputs["TwoColumnDeltaU"] = pickUv.TwoColumnDeltaU;
                        node.Outputs["TwoColumnDeltaV"] = pickUv.TwoColumnDeltaV;
                        node.Outputs["TwoColumnDeltaSummary"] = pickUv.TwoColumnDeltaSummary ?? "";

                        int targetUv = gridRowsUv * gridColsUv;
                        string logHintUv = wantLogUv && !string.IsNullOrEmpty(HalconShapeMatchGridDiagnostics.LogFilePath)
                            ? " 详见.grid-filter.log" : "";
                        string deltaHint = !string.IsNullOrEmpty(pickUv.TwoColumnDeltaSummary)
                            ? $" {pickUv.TwoColumnDeltaSummary}" : "";
                        node.ResultSummary =
                            $"{pickUv.InputCount} 点 → {pickUv.KeptCount}/{targetUv} (u/v 落格, θ≈{pickUv.LatticeAngleDeg:F1}°){deltaHint}{logHintUv}";
                        break;
                    }

                    case "halcon_ransac_pick_shape_match_lattice":
                    {
                        double[] rows = inputs.TryGetValue("Row", out var rRs) && rRs is double[] raRs ? raRs : Array.Empty<double>();
                        double[] cols = inputs.TryGetValue("Column", out var cRs) && cRs is double[] caRs ? caRs : Array.Empty<double>();
                        inputs.TryGetValue("Angle", out var aRs);
                        double[]? anglesRs = aRs as double[];
                        inputs.TryGetValue("Score", out var sRs);
                        double[]? scoresRs = sRs as double[];

                        var (gridRowsRs, gridColsRs) = ResolveLatticeGrid(node, 8, 2);
                        double minScoreRs = double.TryParse(node.Params.GetValueOrDefault("minScoreKeep"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mskRs) ? mskRs : 0;
                        double snapTolRs = double.TryParse(node.Params.GetValueOrDefault("snapTolerancePx"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double stRs) ? stRs : 0;
                        int ransacIter = int.TryParse(node.Params.GetValueOrDefault("ransacIterations"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int riRs) ? riRs : 500;
                        double inlierSnap = double.TryParse(node.Params.GetValueOrDefault("inlierSnapFactor"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double isfRs) ? isfRs : 0.45;

                        string? svgOutRs = null;
                        string svgParamRs = node.Params.GetValueOrDefault("uvProjectionSvg") ?? "";
                        if (!string.IsNullOrWhiteSpace(svgParamRs))
                            svgOutRs = ResolveCompositeFlowPath(svgParamRs, compositeInnerFlowBaseDir);

                        string diagTagRs = $"{node.Def.DisplayName}#{node.Id.ToString()[..8]}";
                        bool wantLogRs = HalconShapeMatchGridDiagnostics.ShouldLogForParam(
                            node.Params.GetValueOrDefault("debugLog", "auto"), MirrorErrorsToStderr);
                        if (wantLogRs && !HalconShapeMatchGridDiagnostics.IsEnabled)
                            HalconShapeMatchGridDiagnostics.Enable(mirrorConsole: MirrorErrorsToStderr, sessionName: diagTagRs);

                        var pickRs = HalconShapeMatchLatticePick.PickRansac(
                            rows, cols, anglesRs, scoresRs,
                            gridRowsRs, gridColsRs,
                            minScoreRs, snapTolRs,
                            wantLogRs ? diagTagRs : null, svgOutRs,
                            ransacIter, inlierSnap);

                        node.Outputs["Row"] = pickRs.Rows;
                        node.Outputs["Column"] = pickRs.Cols;
                        node.Outputs["Angle"] = pickRs.Angles;
                        node.Outputs["Score"] = pickRs.Scores;
                        node.Outputs["GridRow"] = pickRs.GridRow;
                        node.Outputs["GridCol"] = pickRs.GridCol;
                        node.Outputs["LatticeAngle"] = pickRs.LatticeAngleDeg;
                        node.Outputs["ChainDirectionAngle"] = pickRs.ChainDirectionAngleDeg;
                        node.Outputs["ConsensusMatchAngle"] = pickRs.ConsensusMatchAngleDeg;
                        node.Outputs["ConsensusPickIndices"] = pickRs.KeptIndices;
                        node.Outputs["LatticeRows"] = pickRs.LatticeRows;
                        node.Outputs["LatticeCols"] = pickRs.LatticeCols;
                        node.Outputs["AxesSwapped"] = pickRs.AxesSwapped ? 1 : 0;
                        node.Outputs["TwoColumnDeltaU"] = pickRs.TwoColumnDeltaU;
                        node.Outputs["TwoColumnDeltaV"] = pickRs.TwoColumnDeltaV;
                        node.Outputs["TwoColumnDeltaSummary"] = pickRs.TwoColumnDeltaSummary ?? "";

                        int targetRs = gridRowsRs * gridColsRs;
                        string logHintRs = wantLogRs && !string.IsNullOrEmpty(HalconShapeMatchGridDiagnostics.LogFilePath)
                            ? " 详见.grid-filter.log" : "";
                        string deltaHintRs = !string.IsNullOrEmpty(pickRs.TwoColumnDeltaSummary)
                            ? $" {pickRs.TwoColumnDeltaSummary}" : "";
                        node.ResultSummary =
                            $"{pickRs.InputCount} 点 → {pickRs.KeptCount}/{targetRs} (RANSAC, θ≈{pickRs.LatticeAngleDeg:F1}°){deltaHintRs}{logHintRs}";
                        break;
                    }

                    case "halcon_pick_shape_match_lattice":
                    {
                        double[] rows = inputs.TryGetValue("Row", out var rPk) && rPk is double[] raPk ? raPk : Array.Empty<double>();
                        double[] cols = inputs.TryGetValue("Column", out var cPk) && cPk is double[] caPk ? caPk : Array.Empty<double>();
                        inputs.TryGetValue("Angle", out var aPk);
                        double[]? anglesPk = aPk as double[];
                        inputs.TryGetValue("Score", out var sPk);
                        double[]? scoresPk = sPk as double[];

                        var (gridRowsPk, gridColsPk) = ResolveLatticeGrid(node, 8, 2);
                        double minScorePk = double.TryParse(node.Params.GetValueOrDefault("minScoreKeep"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mskPk) ? mskPk : 0;

                        string diagTagPk = $"{node.Def.DisplayName}#{node.Id.ToString()[..8]}";
                        bool wantLogPk = HalconShapeMatchGridDiagnostics.ShouldLogForParam(
                            node.Params.GetValueOrDefault("debugLog", "auto"), MirrorErrorsToStderr);
                        if (wantLogPk && !HalconShapeMatchGridDiagnostics.IsEnabled)
                            HalconShapeMatchGridDiagnostics.Enable(mirrorConsole: MirrorErrorsToStderr, sessionName: diagTagPk);

                        var pickResult = HalconShapeMatchLatticePick.Pick(
                            rows, cols, anglesPk, scoresPk,
                            gridRowsPk, gridColsPk,
                            minScorePk,
                            wantLogPk ? diagTagPk : null);

                        node.Outputs["Row"] = pickResult.Rows;
                        node.Outputs["Column"] = pickResult.Cols;
                        node.Outputs["Angle"] = pickResult.Angles;
                        node.Outputs["Score"] = pickResult.Scores;
                        node.Outputs["GridRow"] = pickResult.GridRow;
                        node.Outputs["GridCol"] = pickResult.GridCol;
                        node.Outputs["LatticeAngle"] = pickResult.LatticeAngleDeg;
                        node.Outputs["ChainDirectionAngle"] = pickResult.ChainDirectionAngleDeg;
                        node.Outputs["ConsensusMatchAngle"] = pickResult.ConsensusMatchAngleDeg;
                        node.Outputs["ConsensusPickIndices"] = pickResult.KeptIndices;
                        node.Outputs["LatticeRows"] = pickResult.LatticeRows;
                        node.Outputs["LatticeCols"] = pickResult.LatticeCols;
                        node.Outputs["AxesSwapped"] = pickResult.AxesSwapped ? 1 : 0;

                        int target = gridRowsPk * gridColsPk;
                        node.ResultSummary =
                            $"{pickResult.InputCount} 点 → {pickResult.KeptCount}/{target} 模板 " +
                            $"({pickResult.LatticeRows}×{pickResult.LatticeCols} 物理格, 链向≈{pickResult.ChainDirectionAngleDeg:F1}°)";
                        break;
                    }

                    case "halcon_chain_strip_bootstrap":
                    case "halcon_chain_strip_orient":
                    case "halcon_chain_strip_pick_col0":
                    case "halcon_chain_strip_fill":
                    case "halcon_chain_strip_pick_fill":
                    {
                        double[] rows = inputs.TryGetValue("Row", out var rSt) && rSt is double[] raSt ? raSt : Array.Empty<double>();
                        double[] cols = inputs.TryGetValue("Column", out var cSt) && cSt is double[] caSt ? caSt : Array.Empty<double>();
                        inputs.TryGetValue("Angle", out var aSt);
                        double[]? anglesSt = aSt as double[];
                        inputs.TryGetValue("Score", out var sSt);
                        double[]? scoresSt = sSt as double[];

                        var (gridRowsSt, gridColsSt) = ResolveLatticeGrid(node, 8, 2);

                        string diagTagSt = $"{node.Def.DisplayName}#{node.Id.ToString()[..8]}";
                        bool wantLogSt = HalconShapeMatchGridDiagnostics.ShouldLogForParam(
                            node.Params.GetValueOrDefault("debugLog", "auto"), MirrorErrorsToStderr);
                        if (wantLogSt && !HalconShapeMatchGridDiagnostics.IsEnabled)
                            HalconShapeMatchGridDiagnostics.Enable(mirrorConsole: MirrorErrorsToStderr, sessionName: diagTagSt);
                        string? logTag = wantLogSt ? diagTagSt : null;

                        switch (node.Def.TypeId)
                        {
                            case "halcon_chain_strip_bootstrap":
                            {
                                var boot = HalconShapeMatchLatticeStrip.Bootstrap(
                                    rows, cols, anglesSt, scoresSt, gridRowsSt, gridColsSt, logTag);
                                node.Outputs["BootstrapPickIndices"] = boot.BootstrapPickIndices;
                                node.Outputs["ChainBootstrapAngle"] = boot.LatticeAngleDeg;
                                node.Outputs["LatticeAngle"] = boot.LatticeAngleDeg;
                                node.Outputs["PcaChainAngle"] = boot.PcaAxisAngleDeg;
                                node.Outputs["VAxisImageAngle"] = boot.VAxisImageAngleDeg;
                                node.Outputs["ConsensusMatchAngle"] = boot.ConsensusMatchAngleDeg;
                                node.Outputs["MatchConcentration"] = boot.MatchConcentration;
                                node.ResultSummary =
                                    $"{boot.InputCount} 点 → 格网θ≈{boot.LatticeAngleDeg:F1}°, v≈{boot.VAxisImageAngleDeg:F1}° (PCA链向≈{boot.PcaAxisAngleDeg:F1}°, 模板≈{boot.ConsensusMatchAngleDeg:F1}°)";
                                break;
                            }
                            case "halcon_chain_strip_orient":
                            {
                                HalconLatticeStripBootstrapResult? bootIn = null;
                                if (inputs.TryGetValue("BootstrapPickIndices", out var bpi) && bpi is int[] bootIdx && bootIdx.Length > 0)
                                {
                                    var fullBoot = HalconShapeMatchLatticeStrip.Bootstrap(
                                        rows, cols, anglesSt, scoresSt, gridRowsSt, gridColsSt, logTag);
                                    double latticeDeg = inputs.TryGetValue("ChainBootstrapAngle", out var cba) && cba is double cd && !double.IsNaN(cd)
                                        ? cd
                                        : fullBoot.LatticeAngleDeg;
                                    bootIn = new HalconLatticeStripBootstrapResult
                                    {
                                        InputCount = fullBoot.InputCount,
                                        BootstrapPickIndices = bootIdx,
                                        LatticeAngleDeg = latticeDeg,
                                        VAxisImageAngleDeg = fullBoot.VAxisImageAngleDeg,
                                        PcaAxisAngleDeg = fullBoot.PcaAxisAngleDeg,
                                        ConsensusMatchAngleDeg = fullBoot.ConsensusMatchAngleDeg,
                                        MatchConcentration = fullBoot.MatchConcentration
                                    };
                                }

                                var ctx = HalconShapeMatchLatticeStrip.BuildOrient(
                                    rows, cols, anglesSt, scoresSt, gridRowsSt, gridColsSt, bootIn, logTag);
                                if (ctx == null)
                                {
                                    node.ResultSummary = "定向失败（格距）";
                                    break;
                                }

                                node.Outputs["StripContext"] = ctx;
                                node.Outputs["LatticeAngle"] = ctx.LatticeAngleDeg;
                                node.Outputs["PcaAxisAngle"] = ctx.PcaAxisDeg;
                                node.Outputs["PitchRow"] = ctx.PitchV;
                                node.Outputs["PitchCol"] = ctx.PitchU;
                                node.Outputs["LatticeRows"] = ctx.EffRows;
                                node.Outputs["LatticeCols"] = ctx.EffCols;
                                node.Outputs["AxesSwapped"] = ctx.AxesSwapped ? 1 : 0;
                                node.ResultSummary =
                                    $"{ctx.InputCount} 点 → PCA≈{ctx.PcaAxisDeg:F1}° 格网θ≈{ctx.LatticeAngleDeg:F1}°, " +
                                    $"{ctx.EffRows}×{ctx.EffCols}, pitchV×pitchU={ctx.PitchV:F0}×{ctx.PitchU:F0}";
                                break;
                            }
                            case "halcon_chain_strip_pick_col0":
                            {
                                if (!inputs.TryGetValue("StripContext", out var ctxIn) || ctxIn is not HalconLatticeStripContext ctx0)
                                {
                                    node.ResultSummary = "缺少 StripContext";
                                    break;
                                }

                                var ctx1 = HalconShapeMatchLatticeStrip.PickColumn0(ctx0, rows, cols, scoresSt, logTag);
                                double chainDeg = ctx1.LatticeAngleDeg;
                                if (double.IsNaN(chainDeg))
                                    chainDeg = ctx1.PcaAxisDeg;
                                node.Outputs["StripContext"] = ctx1;
                                node.Outputs["Column0PickIndices"] = ctx1.Column0PickIndices;
                                node.Outputs["ChainDirectionAngle"] = chainDeg;
                                node.ResultSummary =
                                    $"列0 N={ctx1.Column0PickIndices.Length}, 格网θ≈{chainDeg:F1}° (PCA≈{ctx1.PcaAxisDeg:F1}°)";
                                break;
                            }
                            case "halcon_chain_strip_fill":
                            {
                                if (!inputs.TryGetValue("StripContext", out var ctxFillIn) || ctxFillIn is not HalconLatticeStripContext ctxFill)
                                {
                                    node.ResultSummary = "缺少 StripContext";
                                    break;
                                }

                                var fill = HalconShapeMatchLatticeStrip.FillStrip(
                                    ctxFill, rows, cols, anglesSt, scoresSt, 0, logTag);
                                SetChainStripFillOutputs(node, fill);
                                int nC0f = fill.GridCol.Count(ic => ic == 0);
                                int nC1f = fill.GridCol.Count(ic => ic == 1);
                                string logHintF = wantLogSt && !string.IsNullOrEmpty(HalconShapeMatchGridDiagnostics.LogFilePath)
                                    ? " 详见.grid-filter.log" : "";
                                node.ResultSummary =
                                    $"{fill.InputCount} 点 → 显示={fill.ConsensusPickIndices.Length}(列0={nC0f},列1={nC1f}){logHintF}";
                                break;
                            }
                            case "halcon_chain_strip_pick_fill":
                            {
                                if (!inputs.TryGetValue("StripContext", out var ctxPfIn) || ctxPfIn is not HalconLatticeStripContext ctxPf)
                                {
                                    node.ResultSummary = "缺少 StripContext";
                                    break;
                                }

                                var (ctxPfOut, fillPf) = HalconShapeMatchLatticeStrip.PickColumn0AndFill(
                                    ctxPf, rows, cols, anglesSt, scoresSt, 0, logTag);
                                double chainDegPf = ctxPfOut.LatticeAngleDeg;
                                if (double.IsNaN(chainDegPf))
                                    chainDegPf = ctxPfOut.PcaAxisDeg;
                                node.Outputs["StripContext"] = ctxPfOut;
                                node.Outputs["Column0PickIndices"] = ctxPfOut.Column0PickIndices;
                                SetChainStripFillOutputs(node, fillPf);
                                node.Outputs["ChainDirectionAngle"] = chainDegPf;

                                int nC0Pf = fillPf.GridCol.Count(ic => ic == 0);
                                int nC1Pf = fillPf.GridCol.Count(ic => ic == 1);
                                string logHintPf = wantLogSt && !string.IsNullOrEmpty(HalconShapeMatchGridDiagnostics.LogFilePath)
                                    ? " 详见.grid-filter.log" : "";
                                node.ResultSummary =
                                    $"列0 N={ctxPfOut.Column0PickIndices.Length}, θ≈{chainDegPf:F1}° → 显示={fillPf.ConsensusPickIndices.Length}(列0={nC0Pf},列1={nC1Pf}){logHintPf}";
                                break;
                            }
                        }
                        break;
                    }

                    case "halcon_estimate_shape_match_chain":
                    {
                        double[] rows = inputs.TryGetValue("Row", out var rCh) && rCh is double[] raCh ? raCh : Array.Empty<double>();
                        double[] cols = inputs.TryGetValue("Column", out var cCh) && cCh is double[] caCh ? caCh : Array.Empty<double>();
                        inputs.TryGetValue("Angle", out var aCh);
                        double[]? anglesCh = aCh as double[];
                        inputs.TryGetValue("Score", out var sCh);
                        double[]? scoresCh = sCh as double[];

                        var (gridRowsCh, gridColsCh) = ResolveLatticeGrid(node, 8, 2);

                        string diagTagCh = $"{node.Def.DisplayName}#{node.Id.ToString()[..8]}";
                        bool wantLogCh = HalconShapeMatchGridDiagnostics.ShouldLogForParam(
                            node.Params.GetValueOrDefault("debugLog", "auto"), MirrorErrorsToStderr);
                        if (wantLogCh && !HalconShapeMatchGridDiagnostics.IsEnabled)
                            HalconShapeMatchGridDiagnostics.Enable(mirrorConsole: MirrorErrorsToStderr, sessionName: diagTagCh);

                        var chainResult = HalconShapeMatchChainDirection.Estimate(
                            rows, cols, anglesCh, scoresCh,
                            gridRowsCh, gridColsCh,
                            wantLogCh ? diagTagCh : null);

                        node.Outputs["ChainDirectionAngle"] = chainResult.ChainDirectionAngleDeg;
                        node.Outputs["ConsensusMatchAngle"] = chainResult.ConsensusMatchAngleDeg;
                        node.Outputs["MatchConcentration"] = chainResult.MatchConcentration;
                        node.Outputs["ConsensusPickIndices"] = chainResult.ConsensusPickIndices;
                        node.Outputs["GridRow"] = chainResult.GridRow;
                        node.Outputs["GridCol"] = chainResult.GridCol;
                        node.Outputs["CollinearRow"] = chainResult.Rows;
                        node.Outputs["CollinearColumn"] = chainResult.Cols;

                        int nC0 = chainResult.GridCol.Count(ic => ic == 0);
                        int nC1 = chainResult.GridCol.Count(ic => ic == 1);
                        string angCh = !double.IsNaN(chainResult.ConsensusMatchAngleDeg)
                            ? $", 模板角≈{chainResult.ConsensusMatchAngleDeg:F1}°"
                            : "";
                        string dirCh = !double.IsNaN(chainResult.ChainDirectionAngleDeg)
                            ? $"链向≈{chainResult.ChainDirectionAngleDeg:F1}°"
                            : "无链向";
                        string logHint = wantLogCh && !string.IsNullOrEmpty(HalconShapeMatchGridDiagnostics.LogFilePath)
                            ? " 详见.grid-filter.log"
                            : "";
                        node.ResultSummary =
                            $"{chainResult.InputCount} 点 → {dirCh}, 显示={chainResult.ConsensusPickIndices.Length}(列0={nC0},列1={nC1}){angCh}{logHint}";
                        break;
                    }

                    case "halcon_fit_shape_match_lattice":
                    {
                        double[] rows = inputs.TryGetValue("Row", out var rIn0) && rIn0 is double[] ra0 ? ra0 : Array.Empty<double>();
                        double[] cols = inputs.TryGetValue("Column", out var cIn0) && cIn0 is double[] ca0 ? ca0 : Array.Empty<double>();
                        inputs.TryGetValue("Angle", out var aIn0);
                        double[]? angles0 = aIn0 as double[];
                        inputs.TryGetValue("Score", out var sIn0);
                        double[]? scores0 = sIn0 as double[];

                        var (gridRows0, gridCols0) = ResolveLatticeGrid(node, 3, 3);
                        double pitchRow0 = double.TryParse(node.Params.GetValueOrDefault("pitchRow"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pr0) ? pr0 : 0;
                        double pitchCol0 = double.TryParse(node.Params.GetValueOrDefault("pitchCol"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pc0) ? pc0 : 0;
                        string angleRaw0 = (node.Params.GetValueOrDefault("gridAngleDeg", "auto") ?? "auto").Trim();
                        double? gridAngle0 = string.Equals(angleRaw0, "auto", StringComparison.OrdinalIgnoreCase)
                            ? null
                            : double.TryParse(angleRaw0, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gad0)
                                ? gad0
                                : null;
                        double snapTol0 = double.TryParse(node.Params.GetValueOrDefault("snapTolerancePx"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double st0) ? st0 : 0;

                        double latticeIn = TryGetDoubleInput(inputs, "LatticeAngle");
                        if (double.IsNaN(latticeIn))
                            latticeIn = TryGetDoubleInput(inputs, "ChainDirectionAngle");
                        double consensusIn = TryGetDoubleInput(inputs, "ConsensusMatchAngle");
                        int[]? pickIn = inputs.TryGetValue("ConsensusPickIndices", out var pickObj) && pickObj is int[] pickArr && pickArr.Length > 0
                            ? pickArr
                            : null;
                        HalconShapeMatchLatticeFitOptions? chainOpts = null;
                        if (!double.IsNaN(latticeIn) || !double.IsNaN(consensusIn) || pickIn != null)
                        {
                            chainOpts = new HalconShapeMatchLatticeFitOptions
                            {
                                ChainDirectionAngleDeg = double.IsNaN(latticeIn) ? null : latticeIn,
                                ConsensusMatchAngleDeg = double.IsNaN(consensusIn) ? null : consensusIn,
                                ConsensusPickIndices = pickIn
                            };
                        }

                        string diagTag0 = $"{node.Def.DisplayName}#{node.Id.ToString()[..8]}";
                        bool wantLog0 = HalconShapeMatchGridDiagnostics.ShouldLogForParam(
                            node.Params.GetValueOrDefault("debugLog", "auto"), MirrorErrorsToStderr);
                        if (wantLog0 && !HalconShapeMatchGridDiagnostics.IsEnabled)
                            HalconShapeMatchGridDiagnostics.Enable(mirrorConsole: MirrorErrorsToStderr, sessionName: diagTag0);

                        var fitResult = HalconShapeMatchLatticeFit.Fit(
                            rows, cols, angles0, scores0,
                            gridRows0, gridCols0,
                            pitchRow0, pitchCol0,
                            gridAngle0,
                            snapTol0,
                            wantLog0 ? diagTag0 : null,
                            chainOpts);

                        node.Outputs["ColCenterU"] = fitResult.ColCenterU;
                        node.Outputs["RowCenterV"] = fitResult.RowCenterV;
                        node.Outputs["LatticeAngle"] = fitResult.EstimatedAngleDeg;
                        node.Outputs["ConsensusMatchAngle"] = fitResult.ConsensusMatchAngleDeg;
                        node.Outputs["ChainDirectionAngle"] = fitResult.ChainDirectionAngleDeg;
                        node.Outputs["PitchRow"] = fitResult.PitchRow;
                        node.Outputs["PitchCol"] = fitResult.PitchCol;
                        node.Outputs["AxesSwapped"] = fitResult.AxesSwapped ? 1 : 0;
                        node.Outputs["SnapU"] = fitResult.SnapU;
                        node.Outputs["SnapV"] = fitResult.SnapV;
                        node.Outputs["LatticeRows"] = fitResult.LatticeRows;
                        node.Outputs["LatticeCols"] = fitResult.LatticeCols;
                        node.Outputs["CellRow"] = fitResult.CellRow;
                        node.Outputs["CellCol"] = fitResult.CellCol;
                        node.Outputs["CellFound"] = fitResult.CellFound;
                        node.Outputs["CellAngle"] = fitResult.CellAngle;
                        node.Outputs["PointGridRow"] = fitResult.PointGridRow;
                        node.Outputs["PointGridCol"] = fitResult.PointGridCol;

                        string angNote0 = !double.IsNaN(fitResult.ConsensusMatchAngleDeg)
                            ? $", 模板角≈{fitResult.ConsensusMatchAngleDeg:F1}°"
                            : "";
                        string chainNote0 = !double.IsNaN(fitResult.ChainDirectionAngleDeg)
                            ? $", 链向≈{fitResult.ChainDirectionAngleDeg:F1}°"
                            : "";
                        node.ResultSummary =
                            $"阵列聚类 {fitResult.InputCount} 点 → {fitResult.LatticeRows}×{fitResult.LatticeCols} " +
                            $"(θu≈{fitResult.EstimatedAngleDeg:F1}°, 间距≈{fitResult.PitchRow:F1}×{fitResult.PitchCol:F1}px{chainNote0}{angNote0})";
                        break;
                    }

                    case "halcon_filter_shape_match_grid":
                    {
                        double[] rows = inputs.TryGetValue("Row", out var rIn) && rIn is double[] ra ? ra : Array.Empty<double>();
                        double[] cols = inputs.TryGetValue("Column", out var cIn) && cIn is double[] ca ? ca : Array.Empty<double>();
                        inputs.TryGetValue("Angle", out var aIn);
                        double[]? anglesIn = aIn as double[];
                        inputs.TryGetValue("Score", out var sIn);
                        double[]? scoresIn = sIn as double[];

                        var (gridRows, gridCols) = ResolveLatticeGrid(node, 3, 3);
                        double pitchRow = double.TryParse(node.Params.GetValueOrDefault("pitchRow"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pr) ? pr : 0;
                        double pitchCol = double.TryParse(node.Params.GetValueOrDefault("pitchCol"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pc) ? pc : 0;
                        string angleRaw = (node.Params.GetValueOrDefault("gridAngleDeg", "auto") ?? "auto").Trim();
                        double? gridAngle = string.Equals(angleRaw, "auto", StringComparison.OrdinalIgnoreCase)
                            ? null
                            : double.TryParse(angleRaw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gad)
                                ? gad
                                : null;
                        double snapTol = double.TryParse(node.Params.GetValueOrDefault("snapTolerancePx"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double st) ? st : 0;
                        double pitchTol = double.TryParse(node.Params.GetValueOrDefault("pitchToleranceRatio"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pt) ? pt : 0.2;
                        int minVotes = int.TryParse(node.Params.GetValueOrDefault("minNeighborVotes"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int mv) ? mv : 0;
                        double minScoreKeep = double.TryParse(node.Params.GetValueOrDefault("minScoreKeep"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double msk) ? msk : 0;
                        double maxAngleDev = double.TryParse(node.Params.GetValueOrDefault("maxAngleDeviationDeg"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mad) ? mad : 0;

                        string diagTag = $"{node.Def.DisplayName}#{node.Id.ToString()[..8]}";
                        bool wantGridLog = HalconShapeMatchGridDiagnostics.ShouldLogForParam(
                            node.Params.GetValueOrDefault("debugLog", "auto"), MirrorErrorsToStderr);
                        if (wantGridLog && !HalconShapeMatchGridDiagnostics.IsEnabled)
                            HalconShapeMatchGridDiagnostics.Enable(mirrorConsole: MirrorErrorsToStderr, sessionName: diagTag);

                        if (wantGridLog)
                        {
                            HalconShapeMatchGridDiagnostics.LogFindInput(diagTag, rows.Length, scoresIn);
                            string? logPath = HalconShapeMatchGridDiagnostics.LogFilePath;
                            if (!string.IsNullOrEmpty(logPath))
                                AppendLog($"[GridFilter] 诊断日志 → {logPath}");
                        }

                        HalconShapeMatchGridFilter.LatticeFitResult? precomputed = null;
                        if (inputs.TryGetValue("ColCenterU", out var ccuObj) && ccuObj is double[] ccu && ccu.Length > 0
                            && inputs.TryGetValue("RowCenterV", out var rcvObj) && rcvObj is double[] rcv && rcv.Length > 0)
                        {
                            double gridAngForImport = TryGetDoubleInput(inputs, "ChainDirectionAngle");
                            if (double.IsNaN(gridAngForImport))
                                gridAngForImport = TryGetDoubleInput(inputs, "LatticeAngle");
                            double impPitchCol = TryGetDoubleInput(inputs, "PitchCol");
                            double impPitchRow = TryGetDoubleInput(inputs, "PitchRow");
                            if (impPitchCol <= 0) impPitchCol = pitchCol;
                            if (impPitchRow <= 0) impPitchRow = pitchRow;
                            double consensusIn = double.NaN;
                            if (inputs.TryGetValue("ConsensusMatchAngle", out var cmaObj) && cmaObj != null)
                                consensusIn = TryGetDoubleInput(inputs, "ConsensusMatchAngle");
                            precomputed = HalconShapeMatchGridFilter.TryImportLatticeFit(
                                rows, cols, anglesIn, scoresIn,
                                gridRows, gridCols,
                                ccu, rcv,
                                impPitchCol, impPitchRow,
                                gridAngForImport,
                                TryGetIntInput(inputs, "AxesSwapped"),
                                TryGetDoubleInput(inputs, "SnapU"),
                                TryGetDoubleInput(inputs, "SnapV"),
                                consensusIn);
                        }

                        var filtered = HalconShapeMatchGridFilter.Filter(
                            rows, cols, anglesIn, scoresIn,
                            gridRows, gridCols,
                            pitchRow, pitchCol,
                            gridAngle,
                            snapTol,
                            pitchTol,
                            minVotes,
                            minScoreKeep,
                            maxAngleDev,
                            wantGridLog ? diagTag : null,
                            precomputed);

                        node.Outputs["Row"] = filtered.Rows;
                        node.Outputs["Column"] = filtered.Cols;
                        node.Outputs["Angle"] = filtered.Angles;
                        node.Outputs["Score"] = filtered.Scores;
                        node.Outputs["GridRow"] = filtered.GridRow;
                        node.Outputs["GridCol"] = filtered.GridCol;
                        node.Outputs["LatticeRows"] = filtered.LatticeRows;
                        node.Outputs["LatticeCols"] = filtered.LatticeCols;
                        node.Outputs["CellRow"] = filtered.CellRow;
                        node.Outputs["CellCol"] = filtered.CellCol;
                        node.Outputs["CellFound"] = filtered.CellFound;
                        node.Outputs["PitchRow"] = filtered.EstimatedPitchRow;
                        node.Outputs["PitchCol"] = filtered.EstimatedPitchCol;
                        node.Outputs["LatticeAngle"] = filtered.EstimatedAngleDeg;
                        node.Outputs["ChainDirectionAngle"] = filtered.ChainDirectionAngleDeg;
                        node.Outputs["ColCenterU"] = filtered.ColCenterU;
                        node.Outputs["RowCenterV"] = filtered.RowCenterV;
                        node.Outputs["CellAngle"] = filtered.CellAngleDeg;
                        int missingCells = filtered.CellFound.Count(f => !f);
                        string swapNote = filtered.AxesSwapped ? ", 行列轴已对调" : "";
                        string gridSample = filtered.GridRow.Length > 0
                            ? $" 格[{filtered.GridRow[0]},{filtered.GridCol[0]}]…"
                            : "";
                        string matchAngNote = !double.IsNaN(filtered.ConsensusMatchAngleDeg)
                            ? $", 匹配角≈{filtered.ConsensusMatchAngleDeg:F1}°"
                            : "";
                        node.ResultSummary =
                            $"阵列过滤 {filtered.InputCount}→{filtered.Rows.Length} " +
                            $"(阵列 {gridRows}×{gridCols}, 缺失{missingCells}格, 间距≈{filtered.EstimatedPitchRow:F1}×{filtered.EstimatedPitchCol:F1}px{matchAngNote}{swapNote}{gridSample})";
                        break;
                    }

                    case "halcon_mask_image_by_shape_match":
                    {
#if HALCON_ENABLED
                        var srcImg = inputs["Image"] as CalibImage;
                        if (srcImg == null)
                            throw new InvalidOperationException("HALCON 形状匹配 Mask: 缺少 Image");
                        double[] rows = inputs.TryGetValue("Row", out var rowObj) && rowObj is double[] ra
                            ? ra
                            : Array.Empty<double>();
                        double[] cols = inputs.TryGetValue("Column", out var colObj) && colObj is double[] ca
                            ? ca
                            : Array.Empty<double>();
                        inputs.TryGetValue("Angle", out var angObj);
                        double[]? angles = angObj as double[];
                        if (!inputs.TryGetValue("ModelId", out var midObj))
                            throw new InvalidOperationException("HALCON 形状匹配 Mask: 缺少 ModelId");

                        long modelId = Convert.ToInt64(midObj);
                        int contourLevel = int.TryParse(
                            node.Params.GetValueOrDefault("contourLevel"),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out int cl)
                            ? cl
                            : 1;
                        double insetPx = double.TryParse(
                            node.Params.GetValueOrDefault("insetPx"),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double ins)
                            ? ins
                            : 0;
                        double maskMin = double.TryParse(
                            node.Params.GetValueOrDefault("maskMin"),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double mn)
                            ? mn
                            : 1;
                        double maskMax = double.TryParse(
                            node.Params.GetValueOrDefault("maskMax"),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double mx)
                            ? mx
                            : 255;
                        bool preserveColor = !string.Equals(
                            node.Params.GetValueOrDefault("preserveColor"),
                            "false",
                            StringComparison.OrdinalIgnoreCase);
                        bool keepInsideMask = HalconFlowBridge.ParseMaskRegionKeepInside(
                            node.Params.GetValueOrDefault("maskRegionMode", "保留mask区域"));
                        int maxMatchCount = int.TryParse(
                            node.Params.GetValueOrDefault("maxMatchCount", "0"),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out int mmc)
                            ? Math.Max(0, mmc)
                            : 0;

                        var masked = HalconFlowBridge.MaskCalibImageByShapeMatch(
                            srcImg,
                            modelId,
                            rows,
                            cols,
                            angles,
                            contourLevel,
                            insetPx,
                            maskMin,
                            maskMax,
                            preserveColor,
                            keepInsideMask,
                            maxMatchCount);

                        node.Outputs["Out"] = masked.MaskedImage;
                        node.Outputs["Mask"] = masked.Mask;
                        string modeLabel = keepInsideMask ? "保留区域" : "去掉区域";
                        string countLabel = maxMatchCount > 0
                            ? $"用{masked.MatchCount}/{masked.InputMatchCount}匹配"
                            : $"匹配{masked.MatchCount}";
                        node.ResultSummary =
                            $"形状匹配 Mask {modeLabel} {countLabel} 有效区域{masked.ValidRegionCount} " +
                            $"→ {masked.MaskedImage.Width}×{masked.MaskedImage.Height}";
                        break;
#else
                        throw new InvalidOperationException("HALCON 形状匹配 Mask: 需要 HALCON 支持编译");
#endif
                    }

                    case "halcon_filter_shape_match_inside_region":
                    {
#if HALCON_ENABLED
                        double[] regionRows = inputs.TryGetValue("RegionRow", out var rr) && rr is double[] rra
                            ? rra
                            : Array.Empty<double>();
                        double[] regionCols = inputs.TryGetValue("RegionColumn", out var rc) && rc is double[] rca
                            ? rca
                            : Array.Empty<double>();
                        inputs.TryGetValue("RegionAngle", out var raObj);
                        double[]? regionAngles = raObj as double[];

                        double[] queryRows = inputs.TryGetValue("Row", out var qr) && qr is double[] qra
                            ? qra
                            : Array.Empty<double>();
                        double[] queryCols = inputs.TryGetValue("Column", out var qc) && qc is double[] qca
                            ? qca
                            : Array.Empty<double>();
                        inputs.TryGetValue("Angle", out var qaObj);
                        double[]? queryAngles = qaObj as double[];
                        inputs.TryGetValue("Score", out var qsObj);
                        double[]? queryScores = qsObj as double[];

                        if (!inputs.TryGetValue("RegionModelId", out var rmidObj))
                            throw new InvalidOperationException("HALCON 区域内过滤: 缺少 RegionModelId");
                        if (!inputs.TryGetValue("ModelId", out var qmidObj))
                            throw new InvalidOperationException("HALCON 区域内过滤: 缺少 ModelId（待过滤模板）");

                        long regionModelId = Convert.ToInt64(rmidObj);
                        long queryModelId = Convert.ToInt64(qmidObj);

                        int contourLevel = int.TryParse(
                            node.Params.GetValueOrDefault("contourLevel"),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out int cl)
                            ? cl
                            : 1;
                        double insetPx = double.TryParse(
                            node.Params.GetValueOrDefault("insetPx"),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double ins)
                            ? ins
                            : 2;
                        double minSepPx = double.TryParse(
                            node.Params.GetValueOrDefault("minSeparationPx"),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double msep)
                            ? msep
                            : 0;

                        var filtered = HalconShapeMatchRegionFilter.Filter(
                            regionModelId,
                            regionRows,
                            regionCols,
                            regionAngles,
                            queryModelId,
                            queryRows,
                            queryCols,
                            queryAngles,
                            queryScores,
                            contourLevel,
                            insetPx,
                            minSepPx);

                        node.Outputs["Row"] = filtered.Rows;
                        node.Outputs["Column"] = filtered.Columns;
                        node.Outputs["Angle"] = filtered.Angles;
                        node.Outputs["Score"] = filtered.Scores;
                        node.Outputs["KeptIndices"] = filtered.KeptIndices;
                        node.Outputs["RegionMatchCount"] = filtered.RegionMatchCount;
                        node.Outputs["ValidRegionCount"] = filtered.ValidRegionCount;
                        string sepNote = filtered.TouchFilterEnabled
                            ? $"间距≥{filtered.MinSeparationPx:F1}px"
                            : "未启用间距互斥";
                        string regionNote = filtered.ValidRegionCount > 0
                            ? $"有效区域{filtered.ValidRegionCount}/{filtered.RegionMatchCount}" +
                              (filtered.UsedHalconRegion ? "(HALCON)" : "(多边形)")
                            : "有效区域0";
                        string diagNote = string.IsNullOrWhiteSpace(filtered.RegionDiagnostic)
                            ? ""
                            : $" {filtered.RegionDiagnostic}";
                        node.ResultSummary =
                            $"区域内过滤 {filtered.InputCount}→{filtered.Rows.Length} " +
                            $"(区域内{filtered.InsideRegionCount}, {regionNote}, {sepNote}){diagNote}";
                        if (filtered.RegionMatchCount == 0 || filtered.ValidRegionCount == 0)
                            AppendLog($"[区域内过滤] {node.ResultSummary}", MirrorErrorsToStderr);
                        break;
#else
                        throw new InvalidOperationException("HALCON 区域内过滤: 需要 HALCON 支持编译");
#endif
                    }

                    case "halcon_display_shape_match":
                    {
                        var matchImg = inputs["In"] as CalibImage;
                        if (matchImg == null)
                            throw new InvalidOperationException("HALCON 显示形状匹配: 缺少 In");
                        if (!inputs.TryGetValue("ModelId", out var midObj))
                            throw new InvalidOperationException("HALCON 显示形状匹配: 缺少 ModelId");
                        long modelId = Convert.ToInt64(midObj);
                        static double[] ReadSeries(IReadOnlyDictionary<string, object?> src, string key)
                        {
                            if (!src.TryGetValue(key, out var obj) || obj == null)
                                return Array.Empty<double>();
                            try
                            {
                                return CoerceCoarseShapeSeries(obj, key);
                            }
                            catch
                            {
                                if (HalconFlowBridge.TryReadCoarseScalar(obj, out double scalar))
                                    return new[] { scalar };
                                return Array.Empty<double>();
                            }
                        }

                        double[] rows = ReadSeries(inputs, "Row");
                        double[] cols = ReadSeries(inputs, "Column");
                        double[] angles = ReadSeries(inputs, "Angle");
                        double[] scales = ReadSeries(inputs, "Scale");
                        double[] scores = ReadSeries(inputs, "Score");
                        int[]? pickIndices = inputs.TryGetValue("ConsensusPickIndices", out var pickObj) && pickObj is int[] pickArr && pickArr.Length > 0
                            ? pickArr
                            : null;
                        int totalMatches = Math.Min(rows.Length, cols.Length);
                        string dispDiagTag = $"{node.Def.DisplayName}#{node.Id.ToString()[..8]}";
                        int[]? gridColForLog = inputs.TryGetValue("GridCol", out var gcLog) && gcLog is int[] gca ? gca : null;
                        if (pickIndices != null)
                        {
                            if (HalconShapeMatchGridDiagnostics.ShouldLogForParam(
                                    node.Params.GetValueOrDefault("debugLog", "auto"), MirrorErrorsToStderr)
                                && !HalconShapeMatchGridDiagnostics.IsEnabled)
                                HalconShapeMatchGridDiagnostics.Enable(mirrorConsole: MirrorErrorsToStderr, sessionName: dispDiagTag);

                            // u/v 落格已输出筛选后的 Row（长度=16）；若仍用 Find 下标去 Subset 26 点会越界，只剩 1～2 个能显示
                            bool rowsAlreadyFromPick = pickIndices.Length > 0
                                && rows.Length == pickIndices.Length
                                && rows.Length < totalMatches;
                            if (!rowsAlreadyFromPick)
                            {
                                SubsetShapeMatchesByIndices(rows, cols, angles, scales, scores, pickIndices,
                                    out rows, out cols, out angles, out scales, out scores);
                            }

                            HalconShapeMatchGridFilter.LogDisplayPickSubset(
                                dispDiagTag, rows, cols, scores, pickIndices, gridColForLog, totalMatches);
                        }
                        int contourLevel = int.TryParse(node.Params.GetValueOrDefault("contourLevel"), out int cl) && cl > 0 ? cl : 1;
                        float crossHalf = float.TryParse(node.Params.GetValueOrDefault("crossHalf"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ch) ? ch : 14f;
                        float strokeWidth = float.TryParse(node.Params.GetValueOrDefault("strokeWidth"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sw) ? sw : 2.5f;
                        bool drawScores = !string.Equals(node.Params.GetValueOrDefault("drawScores", "true")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
                        string dispSlot = node.Id.ToString("D");
                        string dispTitle = $"{node.Def.DisplayName} [{node.Id.ToString("N")[..8]}]";
                        ShowShapeMatchPreview(matchImg, modelId, rows, cols, angles, scales, scores,
                            dispSlot, dispTitle, contourLevel, crossHalf, strokeWidth, drawScores);
                        node.Outputs["Out"] = matchImg;
                        int n = Math.Min(rows.Length, cols.Length);
                        string colNote = "";
                        if (pickIndices != null && inputs.TryGetValue("GridCol", out var gcObj) && gcObj is int[] gridCol && gridCol.Length == n)
                        {
                            int c0 = gridCol.Count(ic => ic == 0);
                            int c1 = gridCol.Count(ic => ic == 1);
                            colNote = $", 列0={c0} 列1={c1}";
                        }
                        node.ResultSummary = n == 0
                            ? "无匹配可显示"
                            : pickIndices != null
                                ? $"已显示 {n}/{totalMatches} 个匹配{colNote}"
                                : $"已显示 {n} 个匹配";
                        if (scales.Length > 0)
                        {
                            double sMin = scales.Min();
                            double sMax = scales.Max();
                            node.ResultSummary += $", 缩放[{sMin:F3},{sMax:F3}]";
                        }
                        break;
                    }

                    case "composite_bind_in":
                    {
                        var extInputs = GetCompositeBindInInputsForExecute(compositeExternalInputsForBindIn);
                        if (extInputs == null)
                            throw new InvalidOperationException(
                                "组合绑定入 仅用于组合算子子流程：在父流程中运行组合算子，或在本子流程工具栏设置「子流程调试图」后 F5 独立调试。");
                        string ext = node.Params.GetValueOrDefault("externalPort", "In")?.Trim() ?? "In";
                        if (!extInputs.TryGetValue(ext, out var v))
                        {
                            if (string.Equals(ext, "In", StringComparison.OrdinalIgnoreCase))
                                extInputs.TryGetValue("Image", out v);
                            if (v == null)
                                extInputs.TryGetValue("Img", out v);
                        }
                        node.Outputs["Out"] = v;
                        node.ResultSummary = v != null
                            ? $"{ext}→子图 ({DescribeFlowValueBrief(v)})"
                            : $"{ext}→子图 (无数据)";
                        break;
                    }

                    case "composite_bind_out":
                    {
                        node.Outputs["Out"] = inputs.GetValueOrDefault("In");
                        string ext = node.Params.GetValueOrDefault("externalPort", "Out")?.Trim() ?? "Out";
                        node.ResultSummary = $"子图→{ext}";
                        break;
                    }

                    case "composite":
                    {
                        ExecuteCompositeSubFlow(node, inputs, compositeInnerFlowBaseDir);
                        break;
                    }

                    default:
#if !HALCON_ENABLED
                        if ((node.Def.TypeId ?? "").StartsWith("halcon_", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(
                                "流程包含 HALCON 算子，但当前 exe 未启用 HALCON（编译时未找到 HalconDotNet.dll）。请在本机「重新生成」项目：确认 HALCON 安装根目录下有 bin\\dotnetXX\\HalconDotNet.dll；勿让环境变量 HalconRoot 指向无 bin 的旧路径（可与 HALCONROOT 不一致时用 dotnet build -p:HalconRoot=正确根目录）。不需要 HALCON 时请从流程中移除相应节点。");
#endif
                        throw new InvalidOperationException($"未知算子: {node.Def.TypeId}");
                }

                node.Executed = true;
                SetNodeStatus(node, false);
                if (node.ResultSummary == null)
                    UpdateNodeSummary(node);
                else
                    ApplyResultSummaryTextToNodeVisual(node, node.ResultSummary);
            }
            catch (FlowExecutionGracefulStopException ex)
            {
                node.ErrorMessage = ex.InnerException != null
                    ? $"{ex.Message} {ex.InnerException.Message}"
                    : ex.Message;
                node.Executed = false;
                SetNodeStatus(node, false, true);
                throw;
            }
            catch (Exception ex)
            {
                node.ErrorMessage = ex.Message;
                node.Executed = false;
                SetNodeStatus(node, false, true);
                throw;
            }
        }

        /// <summary>
        /// 弹窗编辑算子参数
        /// </summary>
        private void EditNodeParams(FlowNode node)
        {
            var editableParams = node.Def.Params;
            if (editableParams.Count == 0) return;

            bool compositeUi = node.Def.TypeId == "composite";

            const int paramLabelWidth = 128;
            int inputFieldWidth = compositeUi ? 400 : 260;

            var win = new Window
            {
                Title = $"{node.Def.DisplayName} - 算子配置面板",
                Width = compositeUi ? 680 : 520,
                Height = compositeUi ? Math.Min(420 + editableParams.Count * 72, 720) : Math.Min(80 + editableParams.Count * 78, 620),
                MinWidth = compositeUi ? 540 : 440,
                MinHeight = compositeUi ? 360 : 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.CanResizeWithGrip
            };

            var root = new DockPanel();

            var fieldsPanel = new StackPanel { Margin = new Thickness(12, 12, 12, 8) };
            var inputs = new Control[editableParams.Count];

            if (!string.IsNullOrWhiteSpace(node.Def.Description))
            {
                fieldsPanel.Children.Add(new TextBlock
                {
                    Text = node.Def.Description.Trim(),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                });
            }

            for (int i = 0; i < editableParams.Count; i++)
            {
                var param = editableParams[i];

                var paramBlock = new StackPanel { Margin = new Thickness(0, 6, 0, 10) };
                var row = new DockPanel();

                var label = new TextBlock
                {
                    Text = param.DisplayName,
                    Width = paramLabelWidth,
                    VerticalAlignment = VerticalAlignment.Top,
                    FontWeight = FontWeights.Medium,
                    TextWrapping = TextWrapping.Wrap
                };
                if (!string.IsNullOrWhiteSpace(param.Description))
                    ToolTipService.SetToolTip(label, param.Description.Trim());

                string currentValue = node.Params.GetValueOrDefault(param.Name, param.DefaultValue);
                Control input;
                if (param.Options != null && param.Options.Count > 0)
                {
                    var cb = new ComboBox
                    {
                        MinWidth = inputFieldWidth,
                        Width = inputFieldWidth,
                        VerticalAlignment = VerticalAlignment.Center,
                        ItemsSource = param.Options
                    };
                    cb.SelectedItem = param.Options.Contains(currentValue) ? currentValue : param.DefaultValue;
                    input = cb;
                }
                else if (param.Name == "worldPoints" && NodeUsesCalibrateWorldPointParams(node))
                {
                    input = new TextBox
                    {
                        Width = 460,
                        MinHeight = 72,
                        Text = currentValue,
                        TextWrapping = TextWrapping.Wrap,
                        AcceptsReturn = false,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalAlignment = VerticalAlignment.Top,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11
                    };
                }
                else if (param.Name == "stepValues" && node.Def.TypeId == "flow_loop")
                {
                    input = new TextBox
                    {
                        Width = 460,
                        MinHeight = 56,
                        Text = currentValue,
                        TextWrapping = TextWrapping.Wrap,
                        AcceptsReturn = true,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalAlignment = VerticalAlignment.Top,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11
                    };
                }
                else if (compositeUi && (param.Name == "innerFlowJson" || param.Name == "bindingsJson"))
                {
                    input = new TextBox
                    {
                        Width = 460,
                        MinHeight = param.Name == "innerFlowJson" ? 140 : 96,
                        Text = currentValue,
                        TextWrapping = TextWrapping.Wrap,
                        AcceptsReturn = true,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalAlignment = VerticalAlignment.Top,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11
                    };
                }
                else
                {
                    var tb = new TextBox
                    {
                        MinWidth = inputFieldWidth,
                        Width = inputFieldWidth,
                        Text = currentValue,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    input = tb;
                }
                inputs[i] = input;

                Button? browseBtn = null;
                if (param.Name == "filePath")
                {
                    browseBtn = new Button
                    {
                        Content = "...",
                        Width = 28,
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    browseBtn.Click += (_, _) =>
                    {
                        var ofd = new OpenFileDialog
                        {
                            Title = "选择文件路径",
                            Filter = node.Def.TypeId == "load_image"
                                ? "图像文件|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*"
                                : "所有文件|*.*"
                        };
                        if (ofd.ShowDialog() == true && input is TextBox pathBox)
                            pathBox.Text = FormatPathForFlowParam(ofd.FileName);
                    };
                }
                else if (param.Name == "calibrationJsonFile"
                    && node.Def.TypeId is "calibration_correct_image"
                        or "intrinsics_undistort_image" or "chessboard_perspective_warp_image")
                {
                    browseBtn = new Button
                    {
                        Content = "...",
                        Width = 28,
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    browseBtn.Click += (_, _) =>
                    {
                        var ofd = new OpenFileDialog
                        {
                            Title = "选择标定 JSON",
                            Filter = "JSON|*.json|所有文件|*.*"
                        };
                        if (ofd.ShowDialog() == true && input is TextBox jsonBox)
                            jsonBox.Text = FormatPathForFlowParam(ofd.FileName);
                    };
                }
                else if ((param.Name == "directory" && node.Def.TypeId == "load_image_dir")
                    || (param.Name == "imageDirectory" && node.Def.TypeId == "chessboard_calibrate_intrinsics"))
                {
                    browseBtn = new Button
                    {
                        Content = "...",
                        Width = 28,
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    browseBtn.Click += (_, _) =>
                    {
                        using var fbd = new System.Windows.Forms.FolderBrowserDialog
                        {
                            Description = "选择图像目录",
                            UseDescriptionForTitle = true
                        };
                        if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK && input is TextBox dirBox)
                            dirBox.Text = FormatPathForFlowParam(fbd.SelectedPath);
                    };
                }
                else if (param.Name == "innerFlowPath")
                {
                    browseBtn = new Button
                    {
                        Content = "...",
                        Width = 28,
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    browseBtn.Click += (_, _) =>
                    {
                        var ofd = new OpenFileDialog
                        {
                            Title = "选择子流程 flow.json",
                            Filter = "流程文件|*.flow.json|所有文件|*.*"
                        };
                        if (ofd.ShowDialog() == true && input is TextBox pathBox)
                            pathBox.Text = FormatPathForFlowParam(ofd.FileName);
                    };
                }
                else if (param.Name == "worldPointsFile" && NodeUsesCalibrateWorldPointParams(node))
                {
                    browseBtn = new Button
                    {
                        Content = "...",
                        Width = 28,
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    browseBtn.Click += (_, _) =>
                    {
                        var ofd = new OpenFileDialog
                        {
                            Title = "选择世界坐标文本（点列转文本）",
                            Filter = "文本|*.txt;*.csv|所有文件|*.*"
                        };
                        if (ofd.ShowDialog() == true && input is TextBox pathBox)
                            pathBox.Text = FormatPathForFlowParam(ofd.FileName);
                    };
                }

                DockPanel.SetDock(label, Dock.Left);
                DockPanel.SetDock(input, Dock.Left);
                row.Children.Add(label);
                row.Children.Add(input);
                if (browseBtn != null)
                {
                    DockPanel.SetDock(browseBtn, Dock.Left);
                    row.Children.Add(browseBtn);
                }
                row.LastChildFill = false;
                paramBlock.Children.Add(row);

                if (!string.IsNullOrWhiteSpace(param.Description))
                {
                    var tip = new TextBlock
                    {
                        Text = param.Description.Trim(),
                        Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                        FontSize = 10,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(paramLabelWidth, 4, 0, 0)
                    };
                    ToolTipService.SetToolTip(tip, param.Description.Trim());
                    paramBlock.Children.Add(tip);
                }

                fieldsPanel.Children.Add(paramBlock);
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = fieldsPanel
            };

            // 底部固定：保存 / 取消（避免内容过高时按钮被挤出可视区域）
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 8, 12, 12)
            };
            string okLabel = compositeUi ? "保存" : "确定";
            var btnOk = new Button { Content = okLabel, Width = 88, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var btnCancel = new Button { Content = "取消", Width = 80, IsCancel = true };

            btnOk.Click += (_, _) =>
            {
                string? oldDynamicPortSchema = NodeUsesDynamicPorts(node)
                    ? BuildCurrentNodeVisualPortSchemaSignature(node)
                    : null;
                PushFlowUndoSnapshotBeforeChange();
                for (int i = 0; i < editableParams.Count; i++)
                {
                    var paramDef = editableParams[i];
                    string value = inputs[i] switch
                    {
                        ComboBox cb => cb.SelectedItem?.ToString() ?? paramDef.DefaultValue,
                        TextBox tb => tb.Text,
                        _ => paramDef.DefaultValue
                    };
                    if (IsFlowRelativePathParam(paramDef.Name)
                        && !string.IsNullOrWhiteSpace(value)
                        && System.IO.Path.IsPathRooted(value.Trim()))
                        value = FormatPathForFlowParam(value);
                    node.Params[paramDef.Name] = value;
                }
                if (NodeUsesDynamicPorts(node))
                {
                    string newDynamicPortSchema = BuildDesiredNodePortSchemaSignature(node);
                    if (!string.Equals(oldDynamicPortSchema, newDynamicPortSchema, StringComparison.Ordinal))
                        RebuildNodeVisualAndReconnect(node);
                    else if (string.Equals(node.Def.TypeId, "composite", StringComparison.Ordinal))
                        RefreshCompositeNodeCaption(node);
                }
                win.DialogResult = true;
                win.Close();
            };
            btnCancel.Click += (_, _) =>
            {
                win.DialogResult = false;
                win.Close();
            };

            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            root.Children.Add(btnPanel);
            root.Children.Add(scroll);

            win.Content = root;
            win.ShowDialog();
        }

        /// <summary>
        /// 更新节点上的结果摘要文本
        /// </summary>
        private static void ApplyResultSummaryTextToNodeVisual(FlowNode node, string? summary)
        {
            if (node.Visual is not Border border)
                return;
            border.Dispatcher.Invoke(() =>
            {
                if (border.Child is StackPanel panel)
                {
                    foreach (var child in panel.Children)
                    {
                        if (child is TextBlock tb && (string)tb.Tag == "SummaryText")
                        {
                            tb.Text = summary ?? "";
                            break;
                        }
                    }
                }
            });
        }

        private void UpdateNodeSummary(FlowNode node)
        {
            // 构建摘要
            string? summary = null;

            foreach (var kv in node.Outputs)
            {
                if (kv.Value == null) continue;

                if (kv.Value is Point2D[] pts)
                {
                    summary = $"{kv.Key}: {pts.Length} 个点";
                    break;
                }
                else if (kv.Value is int count)
                {
                    summary = $"{kv.Key}: {count}";
                    break;
                }
                else if (kv.Value is CalibImage ci)
                {
                    summary = $"{kv.Key}: {ci.Width}x{ci.Height}";
                }
            }

            node.ResultSummary = summary;
            ApplyResultSummaryTextToNodeVisual(node, summary);
        }


        /// <summary>
        /// 弹窗输入九点世界坐标（X,Y 两个 TextBox × 9行）
        /// </summary>
        private Point2D[]? InputWorldCoordinates()
        {
            var points = new Point2D[9];
            bool confirmed = false;

            var win = new Window
            {
                Title = "输入九点标定的世界坐标",
                Width = 500,
                Height = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(10) };

            // 标题说明
            sp.Children.Add(new TextBlock
            {
                Text = "请输入 9 个标定点的世界坐标（X, Y）:",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var txts = new TextBox[9, 2];
            // 表头
            var headerPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            headerPanel.Children.Add(new TextBlock { Text = "序号", Width = 50, FontWeight = FontWeights.Bold });
            headerPanel.Children.Add(new TextBlock { Text = "X", Width = 180, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center });
            headerPanel.Children.Add(new TextBlock { Text = "Y", Width = 180, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center });
            sp.Children.Add(headerPanel);

            for (int i = 0; i < 9; i++)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new TextBlock { Text = $"  {i + 1}", Width = 50, VerticalAlignment = VerticalAlignment.Center });
                var tbx = new TextBox { Width = 180, Text = "0" };
                var tby = new TextBox { Width = 180, Text = "0" };
                txts[i, 0] = tbx;
                txts[i, 1] = tby;
                row.Children.Add(tbx);
                row.Children.Add(tby);
                sp.Children.Add(row);
            }

            // 按钮区
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var btnOk = new Button { Content = "确定", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
            var btnCancel = new Button { Content = "取消", Width = 80 };

            btnOk.Click += (_, _) =>
            {
                for (int i = 0; i < 9; i++)
                {
                    if (!double.TryParse(txts[i, 0].Text, out double x) || !double.TryParse(txts[i, 1].Text, out double y))
                    {
                        MessageBox.Show($"第 {i + 1} 个点的坐标格式不正确，请输入数值", "输入错误",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    points[i] = new Point2D(x, y);
                }
                confirmed = true;
                win.Close();
            };
            btnCancel.Click += (_, _) => win.Close();

            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            sp.Children.Add(btnPanel);

            win.Content = sp;
            win.ShowDialog();

            return confirmed ? points : null;
        }

        /// <summary>
        /// 与同节点点列端口配套的条号（Points+BarIds、Out+OutBarIds 等），用于预览时避免跨轮廓连线。
        /// </summary>
        private static int[]? TryGetBarIdsForPointPort(FlowNode node, string pointPortName, int pointCount)
        {
            if (pointCount <= 0)
                return null;
            if (string.Equals(pointPortName, "Points", StringComparison.Ordinal))
            {
                if (node.Outputs.TryGetValue("BarIds", out var b) && b is int[] ids && ids.Length == pointCount)
                    return ids;
                return null;
            }

            if (string.Equals(pointPortName, "Out", StringComparison.Ordinal))
            {
                if (node.Outputs.TryGetValue("OutBarIds", out var ob) && ob is int[] oids && oids.Length == pointCount)
                    return oids;
                if (node.Outputs.TryGetValue("BarIds", out var b2) && b2 is int[] id2 && id2.Length == pointCount)
                    return id2;
            }

            return null;
        }

        /// <summary>BarIds 中是否出现至少两种条号（多段轮廓）。</summary>
        private static bool BarIdsHaveMultipleRuns(int[]? barIds)
        {
            if (barIds == null || barIds.Length < 2) return false;
            int v0 = barIds[0];
            for (int i = 1; i < barIds.Length; i++)
            {
                if (barIds[i] != v0) return true;
            }

            return false;
        }

        /// <summary>连线/右键预览：焊道输出强制整条折线，避免误用 BarIds 被分段。</summary>
        private static string? DefaultPointLineJoinModeForPreview(string? nodeTypeId, string pointPortName)
        {
            if (string.Equals(nodeTypeId, "contours_to_weld_path", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pointPortName, "Points", StringComparison.Ordinal))
                return "single";
            if (string.Equals(pointPortName, "World", StringComparison.Ordinal) &&
                (string.Equals(nodeTypeId, "img_to_world", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(nodeTypeId, "img_to_world_homography", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(nodeTypeId, "img_to_world_poly2d", StringComparison.OrdinalIgnoreCase)))
                return "single";
            return null;
        }

        /// <summary>
        /// 点列折线是否按 BarIds 分段绘制。
        /// mode: auto=仅当 BarIds 等长且含多种条号时分段；single=强制整条折线；bars=BarIds 等长即分段。
        /// </summary>
        private static bool ShouldUseBarSplitForPointOverlay(int[]? barIds, int pointCount, string? mode)
        {
            if (pointCount < 2) return false;
            bool idsOk = barIds != null && barIds.Length == pointCount;
            var m = (mode ?? "auto").Trim().ToLowerInvariant();
            if (m == "single" || m == "one" || m == "polyline")
                return false;
            if (m == "bars" || m == "per_bar" || m == "split" || m == "true")
                return idsOk;
            return idsOk && BarIdsHaveMultipleRuns(barIds);
        }

        /// <summary>
        /// 将 Point2D[] 栅格化为 BMP 后走图像预览（缩放/平移与图像预览一致）。坐标 Y 轴向上为「数学正向」。
        /// </summary>
        private void ShowPointsPolylinePreview(Point2D[] pts, string? titlePrefix, int[]? barIds = null, string? pointLineJoinMode = null)
        {
            if (pts == null) return;
            try
            {
                using var bmp = RenderPoint2DArrayToBitmap(pts, 920, 680, 28, barIds, pointLineJoinMode);
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"flow_pts_preview_{Guid.NewGuid():N}.bmp");
                bmp.Save(tmp, System.Drawing.Imaging.ImageFormat.Bmp);
                try
                {
                    var ci = CalibImage.Load(tmp);
                    try
                    {
                        ShowImagePreview(ci, null, 0, null, LivePreviewSingletonSlotKey, titlePrefix ?? "点位轨迹");
                    }
                    finally
                    {
                        ci.Dispose();
                    }
                }
                finally
                {
                    try { System.IO.File.Delete(tmp); } catch { /* ignored */ }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法生成点位预览: {ex.Message}", "点位预览", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static System.Drawing.Bitmap RenderPoint2DArrayToBitmap(
            Point2D[] pts,
            int targetW,
            int targetH,
            int padPx,
            int[]? barIds = null,
            string? pointLineJoinMode = null)
        {
            targetW = Math.Max(120, targetW);
            targetH = Math.Max(120, targetH);
            padPx = Math.Max(8, padPx);
            if (pts == null || pts.Length == 0)
            {
                var empty = new System.Drawing.Bitmap(420, 140, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g0 = System.Drawing.Graphics.FromImage(empty))
                {
                    g0.Clear(System.Drawing.Color.FromArgb(32, 32, 36));
                    using var f = new System.Drawing.Font("Segoe UI", 12f);
                    g0.DrawString("0 个点（无轨迹可绘制）", f, System.Drawing.Brushes.Gray, padPx, 48f);
                }
                return empty;
            }

            double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
            double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            double bw = Math.Max(maxX - minX, 1e-9);
            double bh = Math.Max(maxY - minY, 1e-9);
            double mx = Math.Max(bw, bh) * 0.06 + 1e-6;
            minX -= mx;
            maxX += mx;
            minY -= mx;
            maxY += mx;
            bw = maxX - minX;
            bh = maxY - minY;

            double scale = Math.Min((targetW - 2.0 * padPx) / bw, (targetH - 2.0 * padPx) / bh);
            int bmpW = (int)Math.Ceiling(bw * scale + 2 * padPx);
            int bmpH = (int)Math.Ceiling(bh * scale + 2 * padPx);
            bmpW = Math.Clamp(bmpW, 160, 1600);
            bmpH = Math.Clamp(bmpH, 160, 1200);

            var bmp = new System.Drawing.Bitmap(bmpW, bmpH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.FromArgb(28, 28, 32));
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

                System.Drawing.PointF Map(Point2D p)
                {
                    float x = (float)(padPx + (p.X - minX) * scale);
                    float y = (float)(padPx + (maxY - p.Y) * scale);
                    return new System.Drawing.PointF(x, y);
                }

                if (pts.Length >= 2)
                {
                    using var linePen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(230, 0, 200, 255), 2f);
                    linePen.StartCap = linePen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    linePen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    bool useBarSplit = ShouldUseBarSplitForPointOverlay(barIds, pts.Length, pointLineJoinMode);
                    if (useBarSplit)
                    {
                        int segStart = 0;
                        for (int i = 1; i <= pts.Length; i++)
                        {
                            if (i == pts.Length || barIds![i] != barIds[i - 1])
                            {
                                int segLen = i - segStart;
                                if (segLen >= 2)
                                {
                                    var arr = new System.Drawing.PointF[segLen];
                                    for (int k = 0; k < segLen; k++)
                                        arr[k] = Map(pts[segStart + k]);
                                    DrawPointOverlaySegment(g, linePen, arr);
                                }

                                segStart = i;
                            }
                        }
                    }
                    else
                    {
                        var arr = pts.Select(Map).ToArray();
                        DrawPointOverlaySegment(g, linePen, arr);
                    }
                }

                using var vtxPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(220, 255, 220, 80), 1.2f);
                float r = pts.Length > 200 ? 2f : 3.5f;
                foreach (var p in pts)
                {
                    var c = Map(p);
                    g.DrawEllipse(vtxPen, c.X - r, c.Y - r, r * 2f, r * 2f);
                }

                using var font = new System.Drawing.Font("Segoe UI", 9f);
                string cap = $"N={pts.Length}  X[{minX + mx:F2},{maxX - mx:F2}]  Y[{minY + mx:F2},{maxY - mx:F2}]";
                if (ShouldUseBarSplitForPointOverlay(barIds, pts.Length, pointLineJoinMode))
                    cap += " · 分包";
                g.DrawString(cap, font, System.Drawing.Brushes.Gainsboro, padPx, 4f);
            }

            return bmp;
        }

        /// <summary>绘制点列折线段；≥3 点时用 DrawPolygon 自动闭合首尾。</summary>
        private static void DrawPointOverlaySegment(
            System.Drawing.Graphics g,
            System.Drawing.Pen pen,
            System.Drawing.PointF[] arr)
        {
            if (arr.Length < 2)
                return;
            if (arr.Length >= 3)
                g.DrawPolygon(pen, arr);
            else
                g.DrawLines(pen, arr);
        }

        /// <summary>HALCON FindShapeModel 专用预览：模板轮廓按位姿叠加、十字、得分。</summary>
        private static int TryGetIntInput(Dictionary<string, object?> inputs, string key)
        {
            if (!inputs.TryGetValue(key, out var v) || v == null) return 0;
            return v switch
            {
                int i => i,
                long l => (int)l,
                double d => (int)d,
                string s when int.TryParse(s, out int p) => p,
                _ => 0
            };
        }

        private static double TryGetDoubleInput(Dictionary<string, object?> inputs, string key)
        {
            if (!inputs.TryGetValue(key, out var v) || v == null) return 0;
            return v switch
            {
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double p) => p,
                _ => 0
            };
        }

        private static void SetChainStripFillOutputs(FlowNode node, HalconShapeMatchChainDirectionResult fill)
        {
            node.Outputs["ChainDirectionAngle"] = fill.ChainDirectionAngleDeg;
            node.Outputs["ConsensusMatchAngle"] = fill.ConsensusMatchAngleDeg;
            node.Outputs["MatchConcentration"] = fill.MatchConcentration;
            node.Outputs["ConsensusPickIndices"] = fill.ConsensusPickIndices;
            node.Outputs["GridRow"] = fill.GridRow;
            node.Outputs["GridCol"] = fill.GridCol;
            node.Outputs["CollinearRow"] = fill.Rows;
            node.Outputs["CollinearColumn"] = fill.Cols;
        }

        /// <summary>按 FindShapeModel 原始下标筛选要显示的匹配（ConsensusPickIndices）。</summary>
        private static void SubsetShapeMatchesByIndices(
            double[] rows,
            double[] cols,
            double[] angles,
            double[] scales,
            double[] scores,
            int[] pickIndices,
            out double[] outRows,
            out double[] outCols,
            out double[] outAngles,
            out double[] outScales,
            out double[] outScores)
        {
            int n = Math.Min(rows.Length, cols.Length);
            var or = new List<double>();
            var oc = new List<double>();
            var oa = new List<double>();
            var oz = new List<double>();
            var os = new List<double>();
            foreach (int i in pickIndices)
            {
                if (i < 0 || i >= n)
                    continue;
                or.Add(rows[i]);
                oc.Add(cols[i]);
                if (angles.Length > i)
                    oa.Add(angles[i]);
                if (scales.Length > i)
                    oz.Add(scales[i]);
                if (scores.Length > i)
                    os.Add(scores[i]);
            }
            outRows = or.ToArray();
            outCols = oc.ToArray();
            outAngles = oa.ToArray();
            outScales = oz.ToArray();
            outScores = os.ToArray();
        }

        private void ShowShapeMatchPreview(
            CalibImage img,
            long modelId,
            double[] rows,
            double[] cols,
            double[] angles,
            double[] scales,
            double[] scores,
            string previewSlotKey,
            string titlePrefix,
            int contourLevel,
            float crossHalf,
            float strokeWidth,
            bool drawScores)
        {
            int n = Math.Min(rows.Length, cols.Length);
            string title = titlePrefix + (n > 0 ? $" · {n} 匹配" : " · 无匹配");
#if HALCON_ENABLED
            ShowImagePreview(
                img,
                null,
                0,
                null,
                previewSlotKey,
                title,
                null,
                null,
                null,
                (drawBmp) => HalconShapeMatchVisualizer.DrawOnBitmap(
                    drawBmp, modelId, rows, cols, angles, scales, scores,
                    contourLevel, crossHalf, strokeWidth, drawScores));
#else
            if (n > 0)
            {
                var pts = new Point2D[n];
                for (int i = 0; i < n; i++)
                    pts[i] = new Point2D(cols[i], rows[i]);
                ShowImagePreview(img, pts, 6, null, previewSlotKey, title + " (无HALCON，仅中心点)");
            }
            else
                ShowImagePreview(img, null, 0, null, previewSlotKey, title);
#endif
        }

        /// <summary>
        /// 显示图像预览窗口，支持缩放和平移，可选叠加点位
        /// 滚轮缩放；右键按住拖拽平移（轻微移动仍可弹出菜单）；右键菜单「保存图像」或 F 适应窗口，1 重置100%
        /// </summary>
        /// <param name="previewSlotKey">每个槽位独立窗口；默认 <see cref="LivePreviewSingletonSlotKey"/> 供连线预览等共用。</param>
        /// <param name="titlePrefix">窗口标题前缀（例如「显示图像 [节点短Guid]」）；空则用默认「图像预览」。</param>
        private void ShowImagePreview(
            CalibImage? img,
            Point2D[]? overlayPoints = null,
            int dotRadius = 3,
            CalibImage? backgroundImg = null,
            string? previewSlotKey = null,
            string? titlePrefix = null,
            int[]? overlayBarIds = null,
            string? overlayPointLineJoinMode = null,
            HalconXldContourBundle? xldOverlay = null,
            Action<System.Drawing.Bitmap>? customBitmapDraw = null)
        {
            var baseSource = backgroundImg ?? img;
            if (baseSource == null) return;
            var bmp = baseSource.ToBitmap();
            if (bmp == null) return;

            // 如果有前景图且存在背景图，使用半透明叠加前景
            if (backgroundImg != null && img != null)
            {
                using var fgBmpRaw = img.ToBitmap();
                if (fgBmpRaw != null)
                {
                    var drawBmp = new System.Drawing.Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    using (var gTemp = System.Drawing.Graphics.FromImage(drawBmp))
                    {
                        gTemp.DrawImage(bmp, 0, 0);
                        var attr = new System.Drawing.Imaging.ImageAttributes();
                        var matrix = new System.Drawing.Imaging.ColorMatrix
                        {
                            Matrix33 = 0.45f // 前景半透明
                        };
                        attr.SetColorMatrix(matrix, System.Drawing.Imaging.ColorMatrixFlag.Default, System.Drawing.Imaging.ColorAdjustType.Bitmap);
                        gTemp.DrawImage(fgBmpRaw,
                            new System.Drawing.Rectangle(0, 0, drawBmp.Width, drawBmp.Height),
                            0, 0, fgBmpRaw.Width, fgBmpRaw.Height,
                            System.Drawing.GraphicsUnit.Pixel, attr);
                    }
                    bmp.Dispose();
                    bmp = drawBmp;
                }
            }

            bool drawPts = overlayPoints != null && overlayPoints.Length > 0;
            bool drawXld = xldOverlay != null && xldOverlay.ContourCount > 0;
            bool drawCustom = customBitmapDraw != null;
            if (drawPts || drawXld || drawCustom)
            {
                // Graphics.FromImage 不支持索引像素格式，先转为 24bppRgb
                var drawBmp = new System.Drawing.Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var gTemp = System.Drawing.Graphics.FromImage(drawBmp))
                {
                    gTemp.DrawImage(bmp, 0, 0);
                }
                bmp.Dispose();
                bmp = drawBmp;

                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    if (drawPts && overlayPoints != null)
                    {
                        if (overlayPoints.Length >= 2)
                        {
                            using var linePen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(200, 0, 200, 255), 1.8f);
                            linePen.StartCap = linePen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                            linePen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                            bool useBarSplit = ShouldUseBarSplitForPointOverlay(overlayBarIds, overlayPoints.Length, overlayPointLineJoinMode);
                            if (useBarSplit)
                            {
                                int segStart = 0;
                                for (int i = 1; i <= overlayPoints.Length; i++)
                                {
                                    if (i == overlayPoints.Length || overlayBarIds![i] != overlayBarIds[i - 1])
                                    {
                                        int segLen = i - segStart;
                                        if (segLen >= 2)
                                        {
                                            var gdiSeg = new System.Drawing.PointF[segLen];
                                            for (int k = 0; k < segLen; k++)
                                            {
                                                var p = overlayPoints[segStart + k];
                                                gdiSeg[k] = new System.Drawing.PointF((float)p.X, (float)p.Y);
                                            }

                                            DrawPointOverlaySegment(g, linePen, gdiSeg);
                                        }

                                        segStart = i;
                                    }
                                }
                            }
                            else
                            {
                                var gdiPts = overlayPoints.Select(p => new System.Drawing.PointF((float)p.X, (float)p.Y)).ToArray();
                                DrawPointOverlaySegment(g, linePen, gdiPts);
                            }
                        }

                        using var pointPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(140, 0, 255, 0), 1.2f);
                        for (int i = 0; i < overlayPoints.Length; i++)
                        {
                            var p = overlayPoints[i];
                            int cx = (int)p.X, cy = (int)p.Y;
                            int r = dotRadius;
                            g.DrawEllipse(pointPen, cx - r, cy - r, r * 2, r * 2);
                            g.DrawLine(pointPen, cx - r - 1, cy, cx + r + 1, cy);
                            g.DrawLine(pointPen, cx, cy - r - 1, cx, cy + r + 1);
                        }
                    }

                    if (drawXld && xldOverlay != null)
                    {
                        using var xldPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(220, 255, 140, 0), 1.5f);
                        foreach (var contour in xldOverlay.Contours)
                        {
                            if (contour == null || contour.Length < 2)
                                continue;
                            for (int i = 0; i < contour.Length - 1; i++)
                            {
                                g.DrawLine(xldPen,
                                    (float)contour[i].X, (float)contour[i].Y,
                                    (float)contour[i + 1].X, (float)contour[i + 1].Y);
                            }
                        }
                    }

                    customBitmapDraw?.Invoke(bmp);
                }
            }

            IntPtr hBitmap = bmp.GetHbitmap();
            var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(baseSource.Width, baseSource.Height));
            bitmapSource.Freeze();
            DeleteObject(hBitmap);
            bmp.Dispose();

            string previewTitle = !string.IsNullOrEmpty(titlePrefix) ? titlePrefix : "图像预览";
            if (overlayPoints != null && overlayPoints.Length > 0)
                previewTitle += $" ({overlayPoints.Length} 个点)";
            if (overlayPoints != null && overlayPoints.Length > 0 &&
                ShouldUseBarSplitForPointOverlay(overlayBarIds, overlayPoints.Length, overlayPointLineJoinMode))
                previewTitle += " · 分包连线";
            if (xldOverlay != null && xldOverlay.ContourCount > 0)
                previewTitle += $" | XLD {xldOverlay.ContourCount}";
            int imgW = baseSource.Width;
            int imgH = baseSource.Height;
            string slotKey = string.IsNullOrEmpty(previewSlotKey) ? LivePreviewSingletonSlotKey : previewSlotKey!;

            void OpenOrUpdateLivePreviewOnUiThread()
            {
                if (_livePreviewBySlot.TryGetValue(slotKey, out var existingSlot))
                {
                    if (existingSlot.Window.IsLoaded)
                    {
                        existingSlot.Window.Title = previewTitle;
                        existingSlot.ImageCtrl.Source = bitmapSource;
                        existingSlot.Window.Show();
                        try
                        {
                            existingSlot.Window.Activate();
                        }
                        catch
                        {
                            // ignored
                        }

                        return;
                    }

                    _livePreviewBySlot.Remove(slotKey);
                }

                var win = new Window
                {
                    Title = previewTitle,
                    Width = Math.Min(imgW + 40, 1400),
                    Height = Math.Min(imgH + 60, 950),
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = Window.GetWindow(this),
                    Background = SystemColors.ControlDarkDarkBrush
                };

                // ---- 缩放/平移核心 ----
                var scaleTransform = new ScaleTransform(1.0, 1.0);
                var translateTransform = new TranslateTransform(0, 0);
                var transformGroup = new TransformGroup();
                transformGroup.Children.Add(scaleTransform);
                transformGroup.Children.Add(translateTransform);

                // 外层：剪裁区域（防止图像超出窗口）
                var border = new Border { ClipToBounds = true };
                // 内层：承载图像，应用变换
                var canvas = new System.Windows.Controls.Canvas { RenderTransform = transformGroup, RenderTransformOrigin = new Point(0, 0) };
                var imageCtrl = new System.Windows.Controls.Image { Source = bitmapSource, Width = imgW, Height = imgH };
                canvas.Children.Add(imageCtrl);
                border.Child = canvas;

                // 缩放比例标签
                var zoomText = new TextBlock
                {
                    Text = "100%",
                    Foreground = Brushes.White,
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(8, 4, 8, 4),
                    Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top
                };

                var rootPanel = new Grid();
                rootPanel.Children.Add(border);
                rootPanel.Children.Add(zoomText);

                win.Content = rootPanel;

                // ---- 右键拖拽平移（超过阈值才捕获，单击保留 ContextMenu）----
                bool rightPanAwait = false;
                Point rightPanDownPos = default;
                bool rightPanning = false;
                Point rightPanGrabPos = default;
                double rightPanTx0 = 0, rightPanTy0 = 0;

                var previewContextMenu = new ContextMenu();
                var miFit = new MenuItem { Header = "适应窗口" };
                miFit.Click += (_, _) => FitToView();
                var mi100 = new MenuItem { Header = "实际大小 (100%)" };
                mi100.Click += (_, _) =>
                {
                    scaleTransform.ScaleX = 1.0;
                    scaleTransform.ScaleY = 1.0;
                    translateTransform.X = 0;
                    translateTransform.Y = 0;
                    zoomText.Text = "100%";
                };
                previewContextMenu.Items.Add(miFit);
                previewContextMenu.Items.Add(mi100);
                previewContextMenu.Items.Add(new Separator());
                var miSave = new MenuItem { Header = "保存图像..." };
                miSave.Click += (_, _) =>
                {
                    if (imageCtrl.Source is not System.Windows.Media.Imaging.BitmapSource bs)
                        return;
                    var dlg = new SaveFileDialog
                    {
                        Title = "保存预览图像",
                        Filter = "PNG 图像|*.png|JPEG 图像|*.jpg;*.jpeg|BMP 图像|*.bmp",
                        DefaultExt = ".png",
                        FileName = "preview.png"
                    };
                    if (dlg.ShowDialog() != true) return;
                    try
                    {
                        System.Windows.Media.Imaging.BitmapEncoder enc;
                        string ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
                        switch (ext)
                        {
                            case ".jpg":
                            case ".jpeg":
                                enc = new System.Windows.Media.Imaging.JpegBitmapEncoder();
                                break;
                            case ".bmp":
                                enc = new System.Windows.Media.Imaging.BmpBitmapEncoder();
                                break;
                            default:
                                enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                break;
                        }
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bs));
                        using (var fs = System.IO.File.Create(dlg.FileName))
                            enc.Save(fs);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"保存失败: {ex.Message}", "图像预览", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                };
                previewContextMenu.Items.Add(miSave);
                border.ContextMenu = previewContextMenu;

                // ---- 鼠标滚轮缩放（以鼠标位置为中心） ----
                border.MouseWheel += (_, e) =>
                {
                    var pos = e.GetPosition(border);
                    double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
                    double newScale = scaleTransform.ScaleX * factor;
                    newScale = Math.Max(0.05, Math.Min(newScale, 50.0));

                    translateTransform.X = pos.X - (pos.X - translateTransform.X) * (newScale / scaleTransform.ScaleX);
                    translateTransform.Y = pos.Y - (pos.Y - translateTransform.Y) * (newScale / scaleTransform.ScaleY);
                    scaleTransform.ScaleX = newScale;
                    scaleTransform.ScaleY = newScale;

                    zoomText.Text = $"{(int)(newScale * 100)}%";
                };

                border.PreviewMouseRightButtonDown += (_, e) =>
                {
                    rightPanAwait = true;
                    rightPanDownPos = e.GetPosition(border);
                    rightPanning = false;
                };
                border.PreviewMouseMove += (_, e) =>
                {
                    if (!rightPanAwait || e.RightButton != MouseButtonState.Pressed)
                        return;

                    var cur = e.GetPosition(border);
                    if (!rightPanning)
                    {
                        double rdx = cur.X - rightPanDownPos.X;
                        double rdy = cur.Y - rightPanDownPos.Y;
                        if (rdx * rdx + rdy * rdy < 36)
                            return;
                        rightPanning = true;
                        rightPanGrabPos = cur;
                        rightPanTx0 = translateTransform.X;
                        rightPanTy0 = translateTransform.Y;
                        border.CaptureMouse();
                        border.Cursor = Cursors.ScrollAll;
                        e.Handled = true;
                        return;
                    }

                    translateTransform.X = rightPanTx0 + (cur.X - rightPanGrabPos.X);
                    translateTransform.Y = rightPanTy0 + (cur.Y - rightPanGrabPos.Y);
                    e.Handled = true;
                };
                border.PreviewMouseRightButtonUp += (_, e) =>
                {
                    if (rightPanning)
                    {
                        border.ReleaseMouseCapture();
                        border.Cursor = null;
                        rightPanning = false;
                        e.Handled = true;
                    }

                    rightPanAwait = false;
                };

                void FitToView()
                {
                    double scaleX = border.ActualWidth / imgW;
                    double scaleY = border.ActualHeight / imgH;
                    double fitScale = Math.Min(scaleX, scaleY) * 0.95;
                    fitScale = Math.Max(fitScale, 0.05);
                    scaleTransform.ScaleX = fitScale;
                    scaleTransform.ScaleY = fitScale;
                    double offsetX = (border.ActualWidth - imgW * fitScale) / 2;
                    double offsetY = (border.ActualHeight - imgH * fitScale) / 2;
                    translateTransform.X = offsetX;
                    translateTransform.Y = offsetY;
                    zoomText.Text = $"{(int)(fitScale * 100)}%";
                }

                // 键盘快捷键
                win.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.F)
                    {
                        FitToView();
                        e.Handled = true;
                    }
                    else if (e.Key == Key.D1 || e.Key == Key.NumPad1)
                    {
                        scaleTransform.ScaleX = 1.0;
                        scaleTransform.ScaleY = 1.0;
                        translateTransform.X = 0;
                        translateTransform.Y = 0;
                        zoomText.Text = "100%";
                        e.Handled = true;
                    }
                    else if (e.Key == Key.OemPlus || e.Key == Key.Add)
                    {
                        double newScale = Math.Min(scaleTransform.ScaleX * 1.2, 50.0);
                        double cx = border.ActualWidth / 2, cy = border.ActualHeight / 2;
                        translateTransform.X = cx - (cx - translateTransform.X) * (newScale / scaleTransform.ScaleX);
                        translateTransform.Y = cy - (cy - translateTransform.Y) * (newScale / scaleTransform.ScaleY);
                        scaleTransform.ScaleX = scaleTransform.ScaleY = newScale;
                        zoomText.Text = $"{(int)(newScale * 100)}%";
                        e.Handled = true;
                    }
                    else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
                    {
                        double newScale = Math.Max(scaleTransform.ScaleX / 1.2, 0.05);
                        double cx = border.ActualWidth / 2, cy = border.ActualHeight / 2;
                        translateTransform.X = cx - (cx - translateTransform.X) * (newScale / scaleTransform.ScaleX);
                        translateTransform.Y = cy - (cy - translateTransform.Y) * (newScale / scaleTransform.ScaleY);
                        scaleTransform.ScaleX = scaleTransform.ScaleY = newScale;
                        zoomText.Text = $"{(int)(newScale * 100)}%";
                        e.Handled = true;
                    }
                };

                // 窗口打开后自适应
                win.ContentRendered += (_, _) => FitToView();
                win.Closed += (_, _) => { _livePreviewBySlot.Remove(slotKey); };

                _livePreviewBySlot[slotKey] = new LivePreviewSlot { Window = win, ImageCtrl = imageCtrl };
                win.Show();
            }

            if (Dispatcher.CheckAccess())
                OpenOrUpdateLivePreviewOnUiThread();
            else
                Dispatcher.Invoke(OpenOrUpdateLivePreviewOnUiThread);
        }

        private void ShowTrajectory3DPreview(
            CalibPoint3D[] path,
            string slotKey,
            string title,
            double tubeDiameter,
            bool showGrid,
            double gridExtent)
        {
            void OpenOrUpdate()
            {
                if (_livePreview3dBySlot.TryGetValue(slotKey, out var existing) && existing.IsLoaded)
                {
                    existing.Title = title;
                    existing.ApplyTrajectory(path, tubeDiameter, showGrid, gridExtent);
                    existing.Show();
                    try
                    {
                        existing.Activate();
                    }
                    catch
                    {
                        // ignored
                    }

                    return;
                }

                if (_livePreview3dBySlot.TryGetValue(slotKey, out var stale))
                {
                    _livePreview3dBySlot.Remove(slotKey);
                    try
                    {
                        stale.Close();
                    }
                    catch
                    {
                        // ignored
                    }
                }

                var win = new Trajectory3DPreviewWindow { Owner = Window.GetWindow(this) };
                win.Title = title;
                win.ApplyTrajectory(path, tubeDiameter, showGrid, gridExtent);
                win.Closed += (_, _) => { _livePreview3dBySlot.Remove(slotKey); };
                _livePreview3dBySlot[slotKey] = win;
                win.Show();
            }

            if (Dispatcher.CheckAccess())
                OpenOrUpdate();
            else
                Dispatcher.Invoke(OpenOrUpdate);
        }

        private void ShowContoursPreview(ValueTuple<int[], int[], int[], int> contourData, CalibImage? baseImg = null)
        {
            var (flatX, flatY, lengths, contourCount) = contourData;
            if (contourCount <= 0 || flatX == null || flatY == null || lengths == null)
            {
                MessageBox.Show("Contours 为空，无法预览。", "轮廓预览", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            System.Drawing.Bitmap bmp;
            if (baseImg != null)
            {
                bmp = baseImg.ToBitmap() ?? new System.Drawing.Bitmap(Math.Max(1, CalibAPI.ImageWidth), Math.Max(1, CalibAPI.ImageHeight), System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            }
            else
            {
                int maxX = flatX.Length > 0 ? flatX.Max() : 0;
                int maxY = flatY.Length > 0 ? flatY.Max() : 0;
                int w = Math.Max(1, maxX + 20);
                int h = Math.Max(1, maxY + 20);
                bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using var g0 = System.Drawing.Graphics.FromImage(bmp);
                g0.Clear(System.Drawing.Color.Black);
            }

            // Graphics.FromImage 不支持索引像素格式，统一转 24bpp 以避免崩溃
            if (bmp.PixelFormat != System.Drawing.Imaging.PixelFormat.Format24bppRgb)
            {
                var converted = new System.Drawing.Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g0 = System.Drawing.Graphics.FromImage(converted))
                    g0.DrawImage(bmp, 0, 0);
                bmp.Dispose();
                bmp = converted;
            }

            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int offset = 0;
                for (int ci = 0; ci < contourCount && ci < lengths.Length; ci++)
                {
                    int len = lengths[ci];
                    if (len < 2 || offset + len > flatX.Length || offset + len > flatY.Length)
                    {
                        offset += Math.Max(0, len);
                        continue;
                    }

                    var pts = new System.Drawing.Point[len + 1];
                    for (int i = 0; i < len; i++)
                        pts[i] = new System.Drawing.Point(flatX[offset + i], flatY[offset + i]);
                    pts[len] = pts[0];

                    var color = System.Drawing.Color.FromArgb(220, (ci * 53) % 256, (ci * 97 + 80) % 256, (ci * 151 + 40) % 256);
                    using var pen = new System.Drawing.Pen(color, 1.4f);
                    g.DrawLines(pen, pts);
                    offset += len;
                }
            }

            IntPtr hBitmap = bmp.GetHbitmap();
            var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(bmp.Width, bmp.Height));

            var win = new Window
            {
                Title = $"轮廓预览 ({contourCount} 条)",
                Width = Math.Min(bmp.Width + 40, 1400),
                Height = Math.Min(bmp.Height + 60, 950),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = SystemColors.ControlDarkDarkBrush
            };
            var imageCtrl = new System.Windows.Controls.Image
            {
                Source = bitmapSource,
                Width = bmp.Width,
                Height = bmp.Height,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var hostGrid = new Grid();
            hostGrid.Children.Add(imageCtrl);
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = hostGrid
            };
            win.Content = scroll;
            win.Closed += (_, _) =>
            {
                DeleteObject(hBitmap);
                bmp.Dispose();
            };
            win.Show();
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        public async System.Threading.Tasks.Task<bool> RunAllAsync(bool clearLog = true, bool preferNativeEngine = false)
        {
            if (_isRunInProgress)
            {
                AppendLog("[WARN] 已有执行在进行中");
                return false;
            }

            _isRunInProgress = true;
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = new System.Threading.CancellationTokenSource();
            if (StopRunButton != null) StopRunButton.IsEnabled = true;
            StatusText.Text = "运行中...";
            StatusText.Foreground = new SolidColorBrush(Colors.Orange);
            if (clearLog) LogBox.Text = "";
            if (IsStandaloneDebugActive)
                AppendLog("========== 子流程独立调试模式 ==========");
            AppendLog("========== 开始执行 ==========");

            // 清除所有节点的执行状态
            foreach (var n in _nodes)
            {
                n.Executed = false;
                n.ErrorMessage = null;
                n.Outputs.Clear();
                n.LastCompositeRun = null;
                SetNodeStatus(n, false);
            }

            try
            {
                ThrowIfExecutionCancelled();
                if (preferNativeEngine && IsStandaloneDebugActive)
                {
                    AppendLog("[WARN] 子流程独立调试仅支持托管「运行」，Native 已改用托管执行");
                    preferNativeEngine = false;
                }

                BeginStandaloneDebugRunScope();
                if (preferNativeEngine)
                {
                    // C++ 原生流程引擎：整段 Run 在线程池执行，避免长时间占用 UI 线程导致窗口卡死、日志不刷新
                    if (TraceEnginePathToConsole)
                        TryTraceEnginePathToConsole("[FlowRunner] 尝试 NativeFlowEngine（C++ 调度）…");
                    var flowData = BuildCurrentFlowData();
                    var flowJson = JsonSerializer.Serialize(flowData, new JsonSerializerOptions { WriteIndented = false });
                    await System.Threading.Tasks.Task.Yield();
                    ThrowIfExecutionCancelled();

                    AppendLog("[NATIVE] 正在后台线程执行 NativeFlowEngine（窗口应保持响应）…");
                    await System.Threading.Tasks.Task.Delay(1);

                    string? nativeFlowRootDir = null;
                    if (!string.IsNullOrEmpty(CurrentFlowFilePath))
                    {
                        var d = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(CurrentFlowFilePath));
                        if (!string.IsNullOrEmpty(d)) nativeFlowRootDir = d;
                    }

                    var token = _runCts?.Token ?? System.Threading.CancellationToken.None;
                    FlowEngineRunResult run = await System.Threading.Tasks.Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        using var engine = new NativeFlowEngine();
                        engine.LoadFromJson(flowJson, nativeFlowRootDir);
                        token.ThrowIfCancellationRequested();
                        return engine.Run();
                    }, token);

                    if (run.Success)
                    {
                        foreach (var n in _nodes)
                        {
                            n.Executed = true;
                            n.ErrorMessage = null;
                            SetNodeStatus(n, false);
                        }
                        StatusText.Text = $"执行完成: {run.ExecutedNodes}/{run.TotalNodes} 个节点成功";
                        StatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
                        AppendLog($"[NATIVE] 执行成功: {run.ExecutedNodes}/{run.TotalNodes}");
                        if (TraceEnginePathToConsole)
                            TryTraceEnginePathToConsole($"[FlowRunner] 全程由 NativeFlowEngine 完成（{run.ExecutedNodes}/{run.TotalNodes} 节点），未回退托管。");
                        if (!string.IsNullOrWhiteSpace(run.ReportJson))
                            AppendLog($"[NATIVE] Report: {run.ReportJson}");
                        if (_nodes.Any(n => n.Def.TypeId == "composite"))
                            await RefreshCompositeSnapshotsAfterNativeAsync();
                        AppendLog(
                            "[NATIVE] 说明: C++ 引擎一次性调度，此日志区不会出现「每个算子一行」的逐步输出；逐算子耗时在控制台 stderr 的 [FlowNative][Timing]。若需要界面里逐步日志，请点「运行」（托管）或对节点右键「执行到此节点（含上游）」。");
                        AppendLog("========== 执行完成 ==========");
                        return true;
                    }

                    StatusText.Text = $"Native执行失败，回退托管: {run.ErrorMessage}";
                    StatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
                    AppendLog($"[NATIVE][ERROR] {run.ErrorMessage}", true);
                    if (TraceEnginePathToConsole)
                        TryTraceEnginePathToConsole("[FlowRunner] NativeFlowEngine 未成功，将回退到托管引擎（逐节点，通常更慢）…");
                    if (!string.IsNullOrWhiteSpace(run.ReportJson))
                        AppendLog($"[NATIVE] Report: {run.ReportJson}");
                    AppendLog("[NATIVE] 回退托管执行...");
                    return await RunAllManagedFallbackAsync();
                }

                // 交互式 UI 默认走托管执行：保证 display/连线点击查看数据可用
                return await RunAllManagedFallbackAsync();
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "执行已停止";
                StatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
                AppendLog("[STOP] 用户中断执行");
                AppendLog("========== 执行中止 ==========");
                return false;
            }
            catch (Exception ex)
            {
                AppendLog($"[WARN] 执行异常，回退到托管执行: {ex.Message}");
                if (preferNativeEngine && TraceEnginePathToConsole)
                    TryTraceEnginePathToConsole($"[FlowRunner] NativeFlowEngine 异常（{ex.Message}），回退托管…");
                return await RunAllManagedFallbackAsync();
            }
            finally
            {
                string? dumpPath = Environment.GetEnvironmentVariable("FLOW_RUN_LOG_PATH");
                if (!string.IsNullOrWhiteSpace(dumpPath))
                {
                    try
                    {
                        string text = GetFlowExecutionLogText();
                        System.IO.File.WriteAllText(dumpPath, text, Encoding.UTF8);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                EndStandaloneDebugRunScope();
                _isRunInProgress = false;
                if (StopRunButton != null) StopRunButton.IsEnabled = false;
            }
        }

        /// <summary>Native 不展开组合子图；补跑各组合算子及其上游托管链，生成 LastCompositeRun 供双击查看变量。</summary>
        private async System.Threading.Tasks.Task RefreshCompositeSnapshotsAfterNativeAsync()
        {
            var composites = _nodes.Where(n => n.Def.TypeId == "composite").ToList();
            if (composites.Count == 0)
                return;

            AppendLog($"[NATIVE→composite] 为 {composites.Count} 个组合算子补跑托管链以生成子流程变量快照…");
            var sorted = TopologicalSort();
            if (sorted.Count != _nodes.Count)
            {
                AppendLog("[NATIVE→composite] 跳过：主流程存在循环依赖", true);
                return;
            }

            foreach (var comp in composites)
            {
                ThrowIfExecutionCancelled();
                var chain = sorted.Where(n => CollectPredecessorsIncludingSelf(comp).Contains(n)).ToList();
                try
                {
                    foreach (var n in chain)
                    {
                        ThrowIfExecutionCancelled();
                        await ExecuteNodeForRunAsync(n, "COMP-SNAP");
                    }

                    AppendLog($"  -> {comp.Def.DisplayName} 子流程变量快照已更新");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppendLog($"  -> {comp.Def.DisplayName} 快照失败: {ex.Message}", true);
                }
            }
        }

        private async System.Threading.Tasks.Task<bool> RunAllManagedFallbackAsync()
        {
            _skipFlowRunNodeIds = new HashSet<Guid>();
            _sendPlcDownstreamChainCache = null;
            try
            {
                if (TraceEnginePathToConsole)
                    TryTraceEnginePathToConsole("[FlowRunner] 托管引擎执行中（C# 逐节点调度）…");
                var sorted = TopologicalSort();
                if (sorted.Count != _nodes.Count)
                {
                    StatusText.Text = "错误: 检测到循环依赖!";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog("[ERROR] 检测到循环依赖，无法执行!", true);
                    return false;
                }

                var perFrameLoops = sorted
                    .Where(n => n.Def.TypeId == "camera_loop" &&
                                string.Equals((n.Params.GetValueOrDefault("mode", "last_only") ?? "last_only").Trim(),
                                    "per_frame", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var dirEachLoops = sorted
                    .Where(n => n.Def.TypeId == "load_image_dir" &&
                                string.Equals((n.Params.GetValueOrDefault("mode", "each") ?? "each").Trim(),
                                    "each", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var flowLoops = sorted.Where(n => n.Def.TypeId == "flow_loop").ToList();

                var maskEachNodes = sorted
                    .Where(n => n.Def.TypeId == "halcon_coarse_shape_reduce_domain" &&
                                GetDownstreamNodes(n).Count > 1 &&
                                !string.Equals(n.Params.GetValueOrDefault("loopEmit", "true"), "false",
                                    StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (perFrameLoops.Count > 1)
                {
                    StatusText.Text = "执行失败: 当前仅支持一个 per_frame camera_loop 节点";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog("[ERROR] 检测到多个 per_frame camera_loop，当前版本仅支持一个", true);
                    return false;
                }

                if (dirEachLoops.Count > 1)
                {
                    StatusText.Text = "执行失败: 当前仅支持一个「遍历目录」加载图像目录节点 (mode=each)";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog("[ERROR] 检测到多个 load_image_dir(mode=each)，当前版本仅支持一个", true);
                    return false;
                }

                if (dirEachLoops.Count == 1 && perFrameLoops.Count == 1)
                {
                    StatusText.Text = "执行失败: load_image_dir(遍历) 不可与 per_frame camera_loop 同时使用";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog("[ERROR] 不能同时使用 load_image_dir(mode=each) 与 camera_loop(per_frame)", true);
                    return false;
                }

                if (flowLoops.Count > 0 && (perFrameLoops.Count == 1 || dirEachLoops.Count == 1))
                {
                    StatusText.Text = "执行失败: 「循环」不可与相机循环(per_frame)或目录遍历同时使用";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog("[ERROR] flow_loop 与 camera_loop(per_frame)/load_image_dir(each) 互斥", true);
                    return false;
                }

                if (maskEachNodes.Count > 1)
                {
                    StatusText.Text = "执行失败: 当前仅支持一个「粗形状Mask循环」节点";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog("[ERROR] 检测到多个 halcon_coarse_shape_reduce_domain(loopEmit=true) 且带下游", true);
                    return false;
                }

                if (maskEachNodes.Count == 1 &&
                    (flowLoops.Count > 0 || perFrameLoops.Count == 1 || dirEachLoops.Count == 1))
                {
                    StatusText.Text = "执行失败: 粗形状Mask循环不可与 flow_loop / 相机循环 / 目录遍历同时使用";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog("[ERROR] halcon_coarse_shape_reduce_domain 循环与 flow_loop/camera_loop/load_image_dir 互斥", true);
                    return false;
                }

#if HALCON_ENABLED
                if (maskEachNodes.Count == 1)
                    return await RunCoarseShapeMaskEachLoopAsync(sorted, maskEachNodes[0]);
#endif

                if (dirEachLoops.Count == 1)
                {
                    var loopNode = dirEachLoops[0];
                    var downstream = GetDownstreamNodes(loopNode);
                    var preNodes = sorted.Where(n => !downstream.Contains(n)).ToList();
                    var postNodes = sorted.Where(n => downstream.Contains(n) && n != loopNode).ToList();

                    AppendLog($"检测到 load_image_dir(遍历): {loopNode.Def.DisplayName}，前置 {preNodes.Count} 节点，下游 {postNodes.Count} 节点");

                    int successCountPre = 0;
                    int errorCountPre = 0;
                    for (int i = 0; i < preNodes.Count; i++)
                    {
                        ThrowIfExecutionCancelled();
                        var node = preNodes[i];
                        if (_skipFlowRunNodeIds != null && _skipFlowRunNodeIds.Contains(node.Id))
                        {
                            AppendLog($"[LOOP-PRE] 跳过: {node.Def.DisplayName}");
                            continue;
                        }
                        if (_skipFlowRunNodeIds != null && _skipFlowRunNodeIds.Contains(node.Id))
                        {
                            AppendLog($"[PRE] 跳过: {node.Def.DisplayName}");
                            continue;
                        }
                        StatusText.Text = $"执行前置 [{i + 1}/{preNodes.Count}] {node.Def.DisplayName}...";
                        AppendLog($"[PRE {i + 1}/{preNodes.Count}] 执行: {node.Def.DisplayName}");
                        await System.Threading.Tasks.Task.Yield();
                        try
                        {
                            await ExecuteNodeForRunAsync(node, "PRE");
                            successCountPre++;
                        }
                        catch (FlowExecutionGracefulStopException ex)
                        {
                            MarkDownstreamNodesSkippedFrom(node, ex.Message, "PRE-STOP");
                            continue;
                        }
                        catch (Exception ex)
                        {
                            errorCountPre++;
                            AppendLog($"[PRE][ERROR] {node.Def.DisplayName}: {ex.Message}", true);
                            continue;
                        }
                    }

                    if (!TryResolveLoadImageDirectory(loopNode, null, out var resolvedDir))
                        throw new InvalidOperationException("加载图像目录: 未选择文件夹");

                    string extSpec = loopNode.Params.GetValueOrDefault("extensions", ".bmp;.png;.jpg;.jpeg;.tif;.tiff")
                                     ?? ".bmp;.png;.jpg;.jpeg;.tif;.tiff";
                    var paths = CollectSortedImagePathsFromDirectory(resolvedDir, extSpec);
                    if (paths.Count == 0)
                        throw new InvalidOperationException($"加载图像目录(遍历): 无匹配图像 ({resolvedDir})，扩展名: {extSpec}");

                    int okImages = 0;
                    int errorCountPost = 0;
                    for (int ii = 0; ii < paths.Count; ii++)
                    {
                        ThrowIfExecutionCancelled();
                        string path = paths[ii];
                        CalibImage? frame = null;
                        try
                        {
                            frame = CalibAPI.LoadImage(path);
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"[IMG {ii + 1}/{paths.Count}] 读取失败: {path} — {ex.Message}", true);
                            continue;
                        }

                        okImages++;
                        loopNode.Outputs.Clear();
                        loopNode.Outputs["Image"] = frame;
                        loopNode.Outputs["Count"] = paths.Count;
                        loopNode.Outputs["Path"] = path;
                        loopNode.Executed = true;
                        loopNode.ErrorMessage = null;
                        SetNodeStatus(loopNode, false);
                        loopNode.ResultSummary = $"each {ii + 1}/{paths.Count} {System.IO.Path.GetFileName(path)}";
                        UpdateNodeSummary(loopNode);

                        AppendLog($"[IMG {ii + 1}/{paths.Count}] 开始 {path}");
                        for (int j = 0; j < postNodes.Count; j++)
                        {
                            ThrowIfExecutionCancelled();
                            var node = postNodes[j];
                            if (_skipFlowRunNodeIds != null && _skipFlowRunNodeIds.Contains(node.Id))
                            {
                                AppendLog($"[IMG] 跳过: {node.Def.DisplayName}");
                                continue;
                            }
                            node.Outputs.Clear();
                            node.ErrorMessage = null;
                            node.Executed = false;
                            StatusText.Text = $"Img[{ii + 1}/{paths.Count}] 执行 [{j + 1}/{postNodes.Count}] {node.Def.DisplayName}...";
                            await System.Threading.Tasks.Task.Yield();
                            try
                            {
                                await ExecuteNodeForRunAsync(node, $"I{ii + 1}");
                            }
                            catch (FlowExecutionGracefulStopException ex)
                            {
                                MarkDownstreamNodesSkippedFrom(node, ex.Message, "IMG-STOP");
                                continue;
                            }
                            catch (Exception ex)
                            {
                                errorCountPost++;
                                AppendLog($"[IMG][ERROR] {node.Def.DisplayName}: {ex.Message}", true);
                                continue;
                            }
                        }
                    }

                    if (okImages <= 0)
                        throw new InvalidOperationException("加载图像目录(遍历): 未能成功解码任何图像");

                    StatusText.Text = $"目录遍历完成: 前置 {successCountPre}/{preNodes.Count}" +
                                      (errorCountPre > 0 ? $"(错误{errorCountPre})" : "") +
                                      $", 有效图 {okImages}/{paths.Count}" +
                                      (errorCountPost > 0 ? $", 下游错误 {errorCountPost}" : "");
                    StatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
                    AppendLog($"========== load_image_dir(遍历) 完成: images={okImages}/{paths.Count} ==========");
                    return true;
                }

                if (perFrameLoops.Count == 1)
                {
                    var loopNode = perFrameLoops[0];
                    var downstream = GetDownstreamNodes(loopNode);
                    var preNodes = sorted.Where(n => !downstream.Contains(n)).ToList();
                    var postNodes = sorted.Where(n => downstream.Contains(n) && n != loopNode).ToList();

                    AppendLog($"检测到 per_frame: {loopNode.Def.DisplayName}，前置 {preNodes.Count} 节点，下游 {postNodes.Count} 节点");

                    int successCountPre = 0;
                    int errorCountPre = 0;
                    for (int i = 0; i < preNodes.Count; i++)
                    {
                        ThrowIfExecutionCancelled();
                        var node = preNodes[i];
                        if (_skipFlowRunNodeIds != null && _skipFlowRunNodeIds.Contains(node.Id))
                        {
                            AppendLog($"[PRE] 跳过: {node.Def.DisplayName}");
                            continue;
                        }
                        StatusText.Text = $"执行前置 [{i + 1}/{preNodes.Count}] {node.Def.DisplayName}...";
                        AppendLog($"[PRE {i + 1}/{preNodes.Count}] 执行: {node.Def.DisplayName}");
                        await System.Threading.Tasks.Task.Yield();
                        try
                        {
                            await ExecuteNodeForRunAsync(node, "PRE");
                            successCountPre++;
                        }
                        catch (FlowExecutionGracefulStopException ex)
                        {
                            MarkDownstreamNodesSkippedFrom(node, ex.Message, "PRE-STOP");
                            continue;
                        }
                        catch (Exception ex)
                        {
                            errorCountPre++;
                            AppendLog($"[PRE][ERROR] {node.Def.DisplayName}: {ex.Message}", true);
                            continue;
                        }
                    }

                    int deviceIndex = int.TryParse(loopNode.Params.GetValueOrDefault("deviceIndex"), out var di) ? di : 0;
                    int frameCount = int.TryParse(loopNode.Params.GetValueOrDefault("frameCount"), out var fc) ? fc : 10;
                    int intervalMs = int.TryParse(loopNode.Params.GetValueOrDefault("intervalMs"), out var im) ? im : 100;
                    int targetWidth = int.TryParse(loopNode.Params.GetValueOrDefault("targetWidth"), out var tw) ? tw : 0;
                    int targetHeight = int.TryParse(loopNode.Params.GetValueOrDefault("targetHeight"), out var th) ? th : 0;

                    frameCount = Math.Max(1, frameCount);
                    int okFrames = 0;
                    int errorCountPost = 0;
                    using var cam = new CameraService();
                    if (!cam.ConnectByIndex(deviceIndex))
                        throw new InvalidOperationException($"相机连接失败，deviceIndex={deviceIndex}");

                    for (int fi = 0; fi < frameCount; fi++)
                    {
                        ThrowIfExecutionCancelled();
                        var frame = cam.GrabOneFrame(targetWidth, targetHeight);
                        if (frame == null)
                        {
                            AppendLog($"[FRAME {fi + 1}/{frameCount}] 抓帧失败: {cam.LastError ?? "未知错误"}", true);
                            if (intervalMs > 0 && fi < frameCount - 1)
                                await System.Threading.Tasks.Task.Delay(intervalMs, _runCts?.Token ?? System.Threading.CancellationToken.None);
                            continue;
                        }
                        okFrames++;
                        loopNode.Outputs.Clear();
                        loopNode.Outputs["Image"] = frame;
                        loopNode.Outputs["Count"] = okFrames;
                        loopNode.Executed = true;
                        loopNode.ErrorMessage = null;
                        SetNodeStatus(loopNode, false);
                        loopNode.ResultSummary = $"per_frame {okFrames}/{frameCount}";
                        UpdateNodeSummary(loopNode);

                        AppendLog($"[FRAME {fi + 1}/{frameCount}] 开始");
                        for (int j = 0; j < postNodes.Count; j++)
                        {
                            ThrowIfExecutionCancelled();
                            var node = postNodes[j];
                            if (_skipFlowRunNodeIds != null && _skipFlowRunNodeIds.Contains(node.Id))
                            {
                                AppendLog($"[FRAME] 跳过: {node.Def.DisplayName}");
                                continue;
                            }
                            node.Outputs.Clear();
                            node.ErrorMessage = null;
                            node.Executed = false;
                            StatusText.Text = $"Frame[{fi + 1}/{frameCount}] 执行 [{j + 1}/{postNodes.Count}] {node.Def.DisplayName}...";
                            await System.Threading.Tasks.Task.Yield();
                            try
                            {
                                await ExecuteNodeForRunAsync(node, $"F{fi + 1}");
                            }
                            catch (FlowExecutionGracefulStopException ex)
                            {
                                MarkDownstreamNodesSkippedFrom(node, ex.Message, "FRAME-STOP");
                                continue;
                            }
                            catch (Exception ex)
                            {
                                errorCountPost++;
                                AppendLog($"[FRAME][ERROR] {node.Def.DisplayName}: {ex.Message}", true);
                                continue;
                            }
                        }

                        if (intervalMs > 0 && fi < frameCount - 1)
                            await System.Threading.Tasks.Task.Delay(intervalMs, _runCts?.Token ?? System.Threading.CancellationToken.None);
                    }

                    if (okFrames <= 0)
                        throw new InvalidOperationException("per_frame 未抓到任何有效帧");

                    StatusText.Text = $"per_frame 执行完成: 前置 {successCountPre}/{preNodes.Count}" +
                                      (errorCountPre > 0 ? $"(错误{errorCountPre})" : "") +
                                      $", 有效帧 {okFrames}/{frameCount}" +
                                      (errorCountPost > 0 ? $", 下游错误 {errorCountPost}" : "");
                    StatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
                    AppendLog($"========== per_frame 执行完成: frames={okFrames}/{frameCount} ==========");
                    return true;
                }

                if (flowLoops.Count > 0)
                {
                    var loopSummaries = new List<string>();
                    var executedNodeIds = new HashSet<Guid>();
                    var loopDownstreamMap = flowLoops.ToDictionary(
                        l => l,
                        l =>
                        {
                            var ds = GetDownstreamNodes(l);
                            ds.Remove(l);
                            return ds;
                        });
                    var rootLoops = flowLoops
                        .Where(l => !flowLoops.Any(other => other != l && loopDownstreamMap[other].Contains(l)))
                        .ToList();
                    if (rootLoops.Count == 0)
                        rootLoops = flowLoops;
                    rootLoops = sorted.Where(rootLoops.Contains).ToList();

                    foreach (var loopNode in rootLoops)
                    {
                        string doneRounds = await ExecuteManagedFlowLoopAsync(loopNode, sorted, executedNodeIds, "ROOT");
                        loopSummaries.Add($"{loopNode.Def.DisplayName}: {doneRounds}");
                    }

                    int residualSuccess = 0;
                    int residualError = 0;
                    foreach (var node in sorted)
                    {
                        if (string.Equals(node.Def.TypeId, "flow_loop", StringComparison.Ordinal))
                            continue;
                        if (executedNodeIds.Contains(node.Id))
                            continue;
                        if (_skipFlowRunNodeIds.Contains(node.Id))
                        {
                            residualSuccess++;
                            AppendLog($"[RESIDUAL] 跳过: {node.Def.DisplayName}（已在批内下游执行）");
                            executedNodeIds.Add(node.Id);
                            continue;
                        }

                        ThrowIfExecutionCancelled();
                        StatusText.Text = $"执行后续 [{residualSuccess + residualError + 1}] {node.Def.DisplayName}...";
                        AppendLog($"[RESIDUAL] 执行: {node.Def.DisplayName}");
                        await System.Threading.Tasks.Task.Yield();
                        try
                        {
                            await ExecuteNodeForRunAsync(node, "LOOP-RESIDUAL");
                            residualSuccess++;
                            executedNodeIds.Add(node.Id);
                        }
                        catch (FlowExecutionGracefulStopException ex)
                        {
                            MarkDownstreamNodesSkippedFrom(node, ex.Message, "LOOP-RESIDUAL-STOP");
                            executedNodeIds.Add(node.Id);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            residualError++;
                            AppendLog($"[RESIDUAL][ERROR] {node.Def.DisplayName}: {ex.Message}", true);
                            continue;
                        }
                    }

                    if (loopSummaries.Count > 1)
                        AppendLog($"========== 多 flow_loop 完成: {string.Join(" | ", loopSummaries)} ==========");
                    if (residualSuccess > 0 || residualError > 0)
                        AppendLog($"========== loop 后续执行: success={residualSuccess}, error={residualError} ==========");

                    bool loopExecOk = residualError == 0;
                    StatusText.Text = loopExecOk
                        ? $"循环执行完成: {string.Join(" | ", loopSummaries)}"
                        : $"循环执行完成(含错误): {string.Join(" | ", loopSummaries)}，后续错误 {residualError}";
                    StatusText.Foreground = new SolidColorBrush(loopExecOk ? Colors.LightGreen : Colors.Orange);
                    return loopExecOk;
                }

                AppendLog($"共 {sorted.Count} 个节点待执行");
                int successCount = 0;
                int errorCount = 0;
                int stopSkipCount = 0;
                for (int i = 0; i < sorted.Count; i++)
                {
                    ThrowIfExecutionCancelled();
                    var node = sorted[i];
                    if (_skipFlowRunNodeIds.Contains(node.Id))
                    {
                        successCount++;
                        AppendLog($"[{i + 1}/{sorted.Count}] 跳过: {node.Def.DisplayName}（已在 send_plc 分批下游执行）");
                        continue;
                    }

                    StatusText.Text = $"执行 [{i + 1}/{sorted.Count}] {node.Def.DisplayName}...";
                    AppendLog($"[{i + 1}/{sorted.Count}] 执行: {node.Def.DisplayName}");
                    await System.Threading.Tasks.Task.Yield();
                    try
                    {
                        await ExecuteNodeForRunAsync(node);
                        successCount++;
                        AppendLog($"  -> OK: {node.Def.DisplayName}");
                    }
                    catch (FlowExecutionGracefulStopException ex)
                    {
                        AppendLog($"  -> [STOP] {node.Def.DisplayName}: {ex.Message}", true);
                        if (ex.InnerException != null)
                            AppendLog($"     {ex.InnerException.Message}", true);
                        stopSkipCount += MarkDownstreamNodesSkippedFrom(node, ex.Message, "NODE-STOP");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"  -> [ERROR] {node.Def.DisplayName}: {ex.Message}", true);
                        if (MirrorErrorsToStderr && ex.InnerException != null)
                            AppendLog($"     Inner: {ex.InnerException}", true);
                        if (MirrorErrorsToStderr)
                        {
                            try { Console.Error.WriteLine(ex.ToString()); } catch { /* ignored */ }
                        }
                        errorCount++;
                        continue;
                    }
                }
                bool ok = errorCount == 0;
                StatusText.Text = ok
                    ? $"执行完成(托管回退): 成功 {successCount}, 跳过 {stopSkipCount}, 总计 {sorted.Count}"
                    : $"执行完成(托管回退): 成功 {successCount}, 错误 {errorCount}, 跳过 {stopSkipCount}, 总计 {sorted.Count}";
                StatusText.Foreground = new SolidColorBrush(ok ? Colors.LightGreen : Colors.Orange);
                AppendLog(ok
                    ? $"========== 执行完成(托管回退): {successCount}/{sorted.Count} =========="
                    : $"========== 执行完成(托管回退): success={successCount}, errors={errorCount}, total={sorted.Count} ==========");
                return ok;
            }
            catch (FlowExecutionGracefulStopException ex)
            {
                StatusText.Text = ex.Message;
                StatusText.Foreground = new SolidColorBrush(Colors.Orange);
                AppendLog($"[STOP] {ex.Message}", true);
                if (ex.InnerException != null)
                    AppendLog($"  {ex.InnerException.Message}", true);
                AppendLog("========== 执行中止 ==========");
                return false;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"执行失败: {ex.Message}";
                StatusText.Foreground = new SolidColorBrush(Colors.Red);
                AppendLog($"[ERROR] 托管回退执行失败: {ex.Message}", true);
                if (MirrorErrorsToStderr && ex.InnerException != null)
                    AppendLog($"  Inner: {ex.InnerException}", true);
                if (MirrorErrorsToStderr)
                {
                    try { Console.Error.WriteLine(ex.ToString()); } catch { /* ignored */ }
                }
                AppendLog("========== 执行中止 ==========");
                return false;
            }
            finally
            {
                _skipFlowRunNodeIds = null;
                _sendPlcDownstreamChainCache = null;
            }
        }

        /// <summary>
        /// 仅执行到达指定节点所需的子图（拓扑序），日志格式与全量托管运行一致（含 [Timing]）。
        /// </summary>
        public async System.Threading.Tasks.Task<bool> RunUpstreamToNodeAsync(FlowNode target)
        {
            if (_isRunInProgress)
            {
                AppendLog("[WARN] 已有执行在进行中，忽略「执行到此节点」");
                return false;
            }

            _isRunInProgress = true;
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = new System.Threading.CancellationTokenSource();
            if (StopRunButton != null) StopRunButton.IsEnabled = true;
            StatusText.Text = "运行中(到此节点)...";
            StatusText.Foreground = new SolidColorBrush(Colors.Orange);
            AppendLog($"========== 执行到此节点: {target.Def.DisplayName} ==========");

            try
            {
                BeginStandaloneDebugRunScope();
                ThrowIfExecutionCancelled();
                var sortedFull = TopologicalSort();
                if (sortedFull.Count != _nodes.Count)
                {
                    StatusText.Text = "错误: 检测到循环依赖!";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog("[ERROR] 检测到循环依赖，无法执行!", true);
                    return false;
                }

                var need = CollectPredecessorsIncludingSelf(target);
                var chain = sortedFull.Where(need.Contains).ToList();
                AppendLog($"链内共 {chain.Count} 个节点（含本节点及全部上游）");

                int successCount = 0;
                for (int i = 0; i < chain.Count; i++)
                {
                    ThrowIfExecutionCancelled();
                    var node = chain[i];
                    StatusText.Text = $"到此节点 [{i + 1}/{chain.Count}] {node.Def.DisplayName}...";
                    AppendLog($"[{i + 1}/{chain.Count}] 执行: {node.Def.DisplayName}");
                    await System.Threading.Tasks.Task.Yield();
                    try
                    {
                        await ExecuteNodeForRunAsync(node, "STEP");
                        successCount++;
                        AppendLog($"  -> OK: {node.Def.DisplayName}");
                    }
                    catch (FlowExecutionGracefulStopException ex)
                    {
                        AppendLog($"  -> [STOP] {node.Def.DisplayName}: {ex.Message}", true);
                        if (ex.InnerException != null)
                            AppendLog($"     {ex.InnerException.Message}", true);
                        StatusText.Text = ex.Message;
                        StatusText.Foreground = new SolidColorBrush(Colors.Orange);
                        AppendLog("========== 执行中止 ==========");
                        return false;
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"  -> [ERROR] {node.Def.DisplayName}: {ex.Message}", true);
                        if (MirrorErrorsToStderr && ex.InnerException != null)
                            AppendLog($"     Inner: {ex.InnerException}", true);
                        if (MirrorErrorsToStderr)
                        {
                            try { Console.Error.WriteLine(ex.ToString()); } catch { /* ignored */ }
                        }
                        StatusText.Text = $"执行失败: {node.Def.DisplayName} - {ex.Message}";
                        StatusText.Foreground = new SolidColorBrush(Colors.Red);
                        AppendLog("========== 执行中止 ==========");
                        return false;
                    }
                }

                StatusText.Text = $"到此节点完成: {successCount}/{chain.Count}";
                StatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
                AppendLog($"========== 到此节点执行完成: {successCount}/{chain.Count} ==========");
                return true;
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "执行已停止";
                StatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
                AppendLog("[STOP] 用户中断执行");
                AppendLog("========== 执行中止 ==========");
                return false;
            }
            finally
            {
                EndStandaloneDebugRunScope();
                _isRunInProgress = false;
                if (StopRunButton != null) StopRunButton.IsEnabled = false;
            }
        }

        private async void RunAll_Click(object sender, RoutedEventArgs e)
        {
            await RunAllAsync(clearLog: true, preferNativeEngine: false);
        }

        private async void RunAllNative_Click(object sender, RoutedEventArgs e)
        {
            await RunAllAsync(clearLog: true, preferNativeEngine: true);
        }

        private bool ValidateDryRunRequiredInputs(IReadOnlyList<FlowNode> sorted, out List<string> errors)
        {
            errors = new List<string>();
            foreach (var node in sorted)
            {
                if (!IsNodeEnabled(node))
                    continue;
                var ports = GetNodePortDefinitions(node);
                foreach (var port in ports.Where(p => p.Direction == PortDirection.Input && !p.IsOptional))
                {
                    bool wired = _connections.Any(c =>
                        c.ToPort.Owner == node &&
                        string.Equals(c.ToPort.Definition.Name, port.Name, StringComparison.Ordinal));
                    if (wired)
                        continue;

                    errors.Add($"{node.Def.DisplayName}.{port.Name} 未连接");
                }
            }

            return errors.Count == 0;
        }

        private static string FormatNodeList(IReadOnlyList<FlowNode> nodes)
            => nodes.Count == 0
                ? "(无)"
                : string.Join(" -> ", nodes.Select(n => n.Def.DisplayName));

        private async System.Threading.Tasks.Task<bool> RunDryRunAsync(bool clearLog = true)
        {
            if (_isRunInProgress)
            {
                AppendLog("[WARN] 已有执行在进行中");
                return false;
            }

            if (clearLog) LogBox.Text = "";
            AppendLog("========== 干跑开始（不执行算子） ==========");
            await System.Threading.Tasks.Task.Yield();

            try
            {
                var sorted = TopologicalSort();
                if (sorted.Count != _nodes.Count)
                {
                    StatusText.Text = "干跑失败: 检测到循环依赖";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog("[ERROR] 检测到循环依赖，无法给出执行顺序", true);
                    return false;
                }

                if (!ValidateDryRunRequiredInputs(sorted, out var wireErrors))
                {
                    StatusText.Text = $"干跑失败: 必填输入未连接 ({wireErrors.Count})";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog($"[ERROR] 必填输入未连接，共 {wireErrors.Count} 处：", true);
                    foreach (var e in wireErrors)
                        AppendLog($"  - {e}", true);
                    return false;
                }

                var perFrameLoops = sorted
                    .Where(n => n.Def.TypeId == "camera_loop" &&
                                string.Equals((n.Params.GetValueOrDefault("mode", "last_only") ?? "last_only").Trim(),
                                    "per_frame", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var dirEachLoops = sorted
                    .Where(n => n.Def.TypeId == "load_image_dir" &&
                                string.Equals((n.Params.GetValueOrDefault("mode", "each") ?? "each").Trim(),
                                    "each", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var flowLoops = sorted.Where(n => n.Def.TypeId == "flow_loop").ToList();
                var maskEachNodes = sorted
                    .Where(n => n.Def.TypeId == "halcon_coarse_shape_reduce_domain" &&
                                GetDownstreamNodes(n).Count > 1 &&
                                !string.Equals(n.Params.GetValueOrDefault("loopEmit", "true"), "false",
                                    StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (perFrameLoops.Count > 1 || dirEachLoops.Count > 1 || maskEachNodes.Count > 1)
                {
                    StatusText.Text = "干跑失败: 存在多个互斥循环入口";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog("[ERROR] 干跑失败：检测到多个同类循环入口节点", true);
                    return false;
                }

                if ((dirEachLoops.Count == 1 && perFrameLoops.Count == 1)
                    || (flowLoops.Count > 0 && (perFrameLoops.Count == 1 || dirEachLoops.Count == 1))
                    || (maskEachNodes.Count == 1 && (flowLoops.Count > 0 || perFrameLoops.Count == 1 || dirEachLoops.Count == 1)))
                {
                    StatusText.Text = "干跑失败: 循环模式互斥配置冲突";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog("[ERROR] 干跑失败：flow_loop / per_frame / load_image_dir(each) / mask-loop 存在互斥冲突", true);
                    return false;
                }

                if (maskEachNodes.Count == 1)
                {
                    var loopNode = maskEachNodes[0];
                    var downstream = GetDownstreamNodes(loopNode);
                    var preNodes = sorted.Where(n => !downstream.Contains(n)).ToList();
                    var postNodes = GetOrderedDownstreamOfNode(loopNode);
                    var postLoopDeferred = ComputePostLoopDeferredNodes(postNodes);
                    var perRoundNodes = postNodes.Where(n => !postLoopDeferred.Contains(n)).ToList();
                    var deferred = postNodes.Where(postLoopDeferred.Contains).ToList();
                    AppendLog($"[DRY] 检测到粗形状Mask循环: {loopNode.Def.DisplayName}");
                    AppendLog($"[DRY] 前置顺序: {FormatNodeList(preNodes)}");
                    AppendLog($"[DRY] 每轮顺序: {FormatNodeList(perRoundNodes)}");
                    if (deferred.Count > 0)
                        AppendLog($"[DRY] 循环后顺序: {FormatNodeList(deferred)}");
                }
                else if (dirEachLoops.Count == 1 || perFrameLoops.Count == 1)
                {
                    var loopNode = dirEachLoops.Count == 1 ? dirEachLoops[0] : perFrameLoops[0];
                    var downstream = GetDownstreamNodes(loopNode);
                    var preNodes = sorted.Where(n => !downstream.Contains(n)).ToList();
                    var postNodes = sorted.Where(n => downstream.Contains(n) && n != loopNode).ToList();
                    AppendLog($"[DRY] 检测到帧/目录循环: {loopNode.Def.DisplayName}");
                    AppendLog($"[DRY] 前置顺序: {FormatNodeList(preNodes)}");
                    AppendLog($"[DRY] 每轮顺序: {FormatNodeList(postNodes)}");
                }
                else if (flowLoops.Count > 0)
                {
                    foreach (var loopNode in flowLoops)
                    {
                        var downstream = GetDownstreamNodes(loopNode);
                        var preNodes = sorted
                            .Where(n => !downstream.Contains(n)
                                        && !string.Equals(n.Def.TypeId, "flow_loop", StringComparison.Ordinal))
                            .ToList();
                        var plan = GetOrderedLoopLocalPlan(loopNode, sorted);
                        var postNodes = plan.PostNodes
                            .Where(n => !string.Equals(n.Def.TypeId, "flow_loop", StringComparison.Ordinal))
                            .ToList();
                        var postLoopDeferred = ComputePostLoopDeferredNodes(postNodes);
                        var perRoundNodes = postNodes.Where(n => !postLoopDeferred.Contains(n)).ToList();
                        var deferred = postNodes.Where(postLoopDeferred.Contains).ToList();
                        AppendLog($"[DRY] 检测到 flow_loop: {loopNode.Def.DisplayName}");
                        AppendLog($"[DRY] 前置顺序: {FormatNodeList(preNodes)}");
                        AppendLog($"[DRY] 每轮顺序: {FormatNodeList(perRoundNodes)}");
                        if (deferred.Count > 0)
                            AppendLog($"[DRY] 循环后顺序: {FormatNodeList(deferred)}");
                    }
                }
                else
                {
                    AppendLog($"[DRY] 线性执行顺序: {FormatNodeList(sorted)}");
                }

                StatusText.Text = $"干跑通过: 共 {_nodes.Count} 节点，{_connections.Count} 连线";
                StatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
                AppendLog("========== 干跑完成（未实际执行） ==========");
                return true;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"干跑失败: {ex.Message}";
                StatusText.Foreground = new SolidColorBrush(Colors.Red);
                AppendLog($"[ERROR] 干跑异常: {ex.Message}", true);
                return false;
            }
        }

        private async void DryRun_Click(object sender, RoutedEventArgs e)
        {
            await RunDryRunAsync(clearLog: true);
        }

        private void StopRun_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunInProgress && _runCts != null && !_runCts.IsCancellationRequested)
            {
                _runCts.Cancel();
                AppendLog("[STOP] 已请求停止...");
                StatusText.Text = "正在停止...";
                StatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
            }
        }
    }
}
