using System;
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
using HslCommunication.ModBus;
#if HALCON_ENABLED
using HalconDotNet;
#endif

// 流程页分文件：算子目录 FlowPage.OperatorCatalog.cs；画布模型 FlowPage.CanvasModels.cs；
// 组态 JSON DTO FlowPage.FlowDocumentJson.cs；标定几何 FlowPage.CalibrationGeometry.cs。

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage : UserControl
    {
        public string? CurrentFlowFilePath { get; private set; }
        public event Action<string?>? FlowLoaded;

        /// <summary>若宿主支持多标签，返回 true 表示已在其它标签打开路径；否则走当前页加载。</summary>
        public Func<string, bool>? TryLoadFlowInNewTab { get; set; }

        /// <summary>
        /// CLI <c>--flow</c> 自动执行时为 true：流程日志里标记为错误的行同时写入标准错误输出。
        /// </summary>
        public bool MirrorErrorsToStderr { get; set; }

        /// <summary>
        /// CLI 后台跑 Flow 时为 true：将「实际使用的引擎路径」简要写入标准输出，便于确认 Native 是否成功或未回退托管。
        /// </summary>
        public bool TraceEnginePathToConsole { get; set; }


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
        private ModbusTcpNet? _flowPlc;
        private bool _flowPlcConnected;

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
        /// OpenFileDialog 须在 UI 线程；其余算子在专用 STA 后台线程执行：避免阻塞 UI，且满足 System.Drawing/GDI+（SAM 等）对 STA 的要求——若用线程池 MTA 易出现卡住且 CPU 空闲。
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

            return false;
        }

        private async System.Threading.Tasks.Task ExecuteNodeForRunAsync(FlowNode node, string? timingScope = null)
        {
            ThrowIfExecutionCancelled();
            var sw = Stopwatch.StartNew();
            try
            {
                if (ExecuteNodeRequiresUiDispatcher(node))
                {
                    await System.Threading.Tasks.Task.Yield();
                    ExecuteNode(node);
                    return;
                }

                await System.Threading.Tasks.Task.Factory.StartNew(
                    () =>
                    {
                        ThrowIfExecutionCancelled();
                        ExecuteNode(node);
                    },
                    _runCts?.Token ?? System.Threading.CancellationToken.None,
                    System.Threading.Tasks.TaskCreationOptions.None,
                    FlowStaTaskScheduler.Default);
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

        private void CreateNodeVisual(FlowNode node)
        {
            var def = node.Def;
            double w = def.DefaultWidth;
            int inputCount = def.Ports.Count(p => p.Direction == PortDirection.Input);
            int outputCount = def.Ports.Count(p => p.Direction == PortDirection.Output);
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

            var menuRunTo = new MenuItem { Header = "执行到此节点（含上游）" };
            menuRunTo.Click += (_, _) => { _ = RunUpstreamToNodeAsync(node); };

            border.ContextMenu = new ContextMenu { Items = { menuCopy, menuPaste, new Separator(), menuParams, menuRunTo, menuDelete } };

            var panel = new StackPanel();

            // 标题栏
            var titleBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x37, 0x37, 0x3D)),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Padding = new Thickness(8, 4, 8, 4)
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

            foreach (var portDef in def.Ports)
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
            int inputCount = def.Ports.Count(p => p.Direction == PortDirection.Input);
            int outputCount = def.Ports.Count(p => p.Direction == PortDirection.Output);
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

        private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                if (sender is FrameworkElement fe2 && fe2.Tag is FlowNode node2)
                {
                    if (node2.Def.Params.Count > 0)
                        EditNodeParams(node2);
                }
                return;
            }

            if (sender is FrameworkElement fe && fe.Tag is FlowNode node)
            {
                _isDraggingNode = true;
                _dragNode = node;
                _dragStart = e.GetPosition(FlowCanvas);
                _nodeStartPos = new Point(Canvas.GetLeft(node.Visual), Canvas.GetTop(node.Visual));
                _pendingDragUndoSnapshotJson = _suppressFlowUndoRecording ? null : SerializeFlowSnapshotCompact();
                fe.CaptureMouse();
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
            return false;
        }

        // ================================================================
        // 连线管理
        // ================================================================

        private void CreateConnection(PortVisual from, PortVisual to)
        {
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

            // 点位类型：如果上游同节点有图像，叠加点位预览；否则显示摘要
            if (data is Point2D[] pts)
            {
                var baseImg = conn.FromPort.Owner.Outputs.Values.OfType<CalibImage>().FirstOrDefault();
                if (baseImg != null)
                {
                    ShowImagePreview(baseImg, pts, 3);
                }
                else
                {
                    MessageBox.Show(
                        $"点位数量：{pts.Length}\n示例首点：{(pts.Length > 0 ? $"({pts[0].X:F2}, {pts[0].Y:F2})" : "N/A")}",
                        "连线数据 - 点位",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
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

            switch (data)
            {
                case int n:
                    sb.AppendLine($"值: {n}");
                    break;
                case AffineTransform t:
                    sb.AppendLine($"X = {t.A:F6}*x + {t.B:F6}*y + {t.C:F6}");
                    sb.AppendLine($"Y = {t.D:F6}*x + {t.E:F6}*y + {t.F:F6}");
                    break;
                case TrajectoryResult tr:
                    sb.AppendLine($"Success: {tr.Success}");
                    sb.AppendLine($"Count: {tr.Count}");
                    if (tr.Points != null && tr.Points.Length > 0)
                        sb.AppendLine($"First Point: ({tr.Points[0].X:F2}, {tr.Points[0].Y:F2})");
                    break;
                case ValueTuple<int[], int[], int[], int> contours:
                    sb.AppendLine($"Contours: {contours.Item4}");
                    sb.AppendLine($"Total Points: {contours.Item1?.Length ?? 0}");
                    if (contours.Item3 != null && contours.Item3.Length > 0)
                        sb.AppendLine($"First Contour Length: {contours.Item3[0]}");
                    break;
                default:
                    sb.AppendLine(data.ToString() ?? "(无可显示内容)");
                    break;
            }

            return sb.ToString();
        }

        // ================================================================
        // 工具栏按钮
        // ================================================================

        // ================================================================
        // 保存 / 加载组态
        // ================================================================

        private void SaveFlow_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "组态文件|*.flow.json|所有文件|*.*",
                DefaultExt = ".flow.json",
                Title = "保存组态"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var data = BuildCurrentFlowData();
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(data, options);
                System.IO.File.WriteAllText(dlg.FileName, json);

                CurrentFlowFilePath = System.IO.Path.GetFullPath(dlg.FileName);
                FlowLoaded?.Invoke(CurrentFlowFilePath);
                StatusText.Text = $"已保存: {System.IO.Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

            return new FlowData { Nodes = nodes, Connections = connections };
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

                if (fn.Def.TypeId == "composite")
                    RefreshCompositeNodeCaption(fn);

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
                if (node.Def.TypeId == "composite")
                    RefreshCompositeNodeCaption(node);

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
                StatusText.Text = $"已加载: {System.IO.Path.GetFileName(filePath)} ({data.Nodes.Count} 节点, {data.Connections.Count} 连线)";
                return true;
            }
            catch (Exception ex)
            {
                if (showErrorDialog)
                    MessageBox.Show($"加载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = $"加载失败: {ex.Message}";
                AppendLog($"[ERROR] 加载组态失败: {ex.Message}", true);
                return false;
            }
        }

        private void LoadFlow_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "组态文件|*.flow.json|所有文件|*.*",
                DefaultExt = ".flow.json",
                Title = "加载组态"
            };
            if (dlg.ShowDialog() != true) return;
            LoadFlowFromFile(dlg.FileName, showErrorDialog: true);
        }

        private void LoadFlowNewTab_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "组态文件|*.flow.json|所有文件|*.*",
                DefaultExt = ".flow.json",
                Title = "在新标签打开组态"
            };
            if (dlg.ShowDialog() != true) return;
            if (TryLoadFlowInNewTab?.Invoke(dlg.FileName) == true)
                return;
            LoadFlowFromFile(dlg.FileName, showErrorDialog: true);
        }

        /// <summary>清空画布并重置路径（保留单标签时用于「关闭」语义）。</summary>
        public void ResetToEmptyDocument()
        {
            _suppressFlowUndoRecording = true;
            try
            {
                ClearCanvasCore();
                CurrentFlowFilePath = null;
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

            foreach (var n in _nodes) inDegree[n.Id] = 0;
            foreach (var c in _connections) inDegree[c.ToPort.Owner.Id]++;

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

            return conn.FromPort.Owner.Outputs.GetValueOrDefault(conn.FromPort.Definition.Name);
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
                    inputs[pv.Definition.Name] = conn.FromPort.Owner.Outputs.GetValueOrDefault(conn.FromPort.Definition.Name);
            }
            return inputs;
        }

        /// <summary>旧版霍夫线段输入端口名为 Image，现改为 Edge；加载组态时自动映射。</summary>
        private static string NormalizeHoughLinesInputPort(FlowNode toNode, string? toPort)
        {
            if (toNode.Def.TypeId == "hough_lines" && string.Equals(toPort, "Image", StringComparison.Ordinal))
                return "Edge";
            return toPort ?? "";
        }

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
            return simplified.ToArray();
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
                    string prefix = debugDumpPrefix.Trim();
                    if (!System.IO.Path.IsPathRooted(prefix))
                        prefix = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, prefix));
                    else
                        prefix = System.IO.Path.GetFullPath(prefix);
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

        /// <param name="relativeBaseDirectory">嵌套组合算子时传入当前子流程 .flow.json 所在目录；顶层为 null 则用 CurrentFlowFilePath 目录。</param>
        private string ResolveCompositeFlowPath(string path, string? relativeBaseDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            path = path.Trim();
            if (System.IO.Path.IsPathRooted(path))
                return System.IO.Path.GetFullPath(path);
            if (!string.IsNullOrWhiteSpace(relativeBaseDirectory))
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(relativeBaseDirectory.Trim(), path));
            if (!string.IsNullOrEmpty(CurrentFlowFilePath))
            {
                var dir = System.IO.Path.GetDirectoryName(CurrentFlowFilePath);
                if (!string.IsNullOrEmpty(dir))
                    return System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, path));
            }
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
                inputs[e.ToPort] = fromNode.Outputs.GetValueOrDefault(e.FromPort);
            }
            return inputs;
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

            foreach (var extDef in compositeNode.Def.Ports.Where(p => p.Direction == PortDirection.Output))
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

        /// <param name="innerFlowResolveBaseDir">当前组合嵌套在上层子流程内时，为其 innerFlowPath 相对路径提供基准目录（通常为上层子流程 .flow.json 所在文件夹）。</param>
        private void ExecuteCompositeSubFlow(FlowNode compositeNode, Dictionary<string, object?> compositeInputs, string? innerFlowResolveBaseDir = null)
        {
            string path = compositeNode.Params.GetValueOrDefault("innerFlowPath", "")?.Trim() ?? "";
            string embedded = compositeNode.Params.GetValueOrDefault("innerFlowJson", "") ?? "";
            string bindRaw = compositeNode.Params.GetValueOrDefault("bindingsJson", "") ?? "";

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

            foreach (var inner in sorted)
            {
                var innerInputs = BuildInnerInputsFromEdges(inner.Id, edges, idMap);
                MergeCompositeExternalInputs(inner, innerInputs, compositeInputs, spec);
                bool innerIsSource = !edges.Any(e => e.ToId == inner.Id);
                if (!strictCompositeInputBinding)
                    AutoFillUnboundCompositeInnerInputs(inner, innerInputs, compositeInputs, innerIsSource);
                var swInner = Stopwatch.StartNew();
                ExecuteNode(inner, innerInputs, compositeInputs, baseDirForNestedComposites);
                swInner.Stop();
                LogOperatorTiming(inner, swInner.Elapsed.TotalMilliseconds, "composite");
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
                throw new InvalidOperationException("世界坐标参数为空，请填写 points");
            var parts = raw.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var points = new List<Point2D>(parts.Length);
            foreach (var p in parts)
            {
                var xy = p.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (xy.Length != 2 ||
                    !double.TryParse(xy[0].Trim(), out var x) ||
                    !double.TryParse(xy[1].Trim(), out var y))
                    throw new InvalidOperationException($"世界坐标格式错误: '{p}'，应为 x,y");
                points.Add(new Point2D(x, y));
            }
            if (points.Count < 4)
                throw new InvalidOperationException("世界坐标点数量不足，至少需要4个点");
            return points.ToArray();
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

        /// <summary>解析「加载图像目录」的根路径；相对路径相对 exe。目录为空时弹出选文件夹；取消则返回 false。</summary>
        private static bool TryResolveLoadImageDirectory(FlowNode node, out string resolvedDir)
        {
            resolvedDir = "";
            string configuredDir = node.Params.GetValueOrDefault("directory", "")?.Trim() ?? "";
            resolvedDir = configuredDir;
            if (!string.IsNullOrWhiteSpace(configuredDir) && !System.IO.Path.IsPathRooted(configuredDir))
                resolvedDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredDir));

            if (!string.IsNullOrWhiteSpace(resolvedDir))
                return true;

            using var fbd = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择图像所在目录",
                UseDescriptionForTitle = true
            };
            if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return false;
            resolvedDir = fbd.SelectedPath;
            return true;
        }

        private void ExecuteNode(FlowNode node, Dictionary<string, object?>? explicitInputs = null, Dictionary<string, object?>? compositeExternalInputsForBindIn = null, string? compositeInnerFlowBaseDir = null)
        {
            var inputs = explicitInputs ?? GetNodeInputs(node);
            node.Outputs.Clear();
            node.ErrorMessage = null;
            node.ResultSummary = null;
            SetNodeStatus(node, true);

            try
            {
                switch (node.Def.TypeId)
                {
                    case "load_image":
                    {
                        string configuredPath = node.Params.GetValueOrDefault("filePath", "")?.Trim() ?? "";
                        string resolvedPath = configuredPath;
                        if (!string.IsNullOrWhiteSpace(configuredPath) && !System.IO.Path.IsPathRooted(configuredPath))
                            resolvedPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath));

                        if (!string.IsNullOrWhiteSpace(resolvedPath))
                        {
                            if (!System.IO.File.Exists(resolvedPath))
                                throw new System.IO.FileNotFoundException($"加载图像失败，文件不存在: {resolvedPath}");
                            var img = CalibAPI.LoadImage(resolvedPath);
                            node.Outputs["Image"] = img;
                            break;
                        }

                        var dlg = new OpenFileDialog
                        {
                            Filter = "图像文件|*.bmp;*.png;*.jpg;*.tif|所有文件|*.*",
                            Title = "选择图像文件"
                        };
                        if (dlg.ShowDialog() == true)
                        {
                            var img = CalibAPI.LoadImage(dlg.FileName);
                            node.Outputs["Image"] = img;
                        }
                        else
                        {
                            node.ErrorMessage = "用户取消";
                        }
                        break;
                    }

                    case "load_image_dir":
                    {
                        if (!TryResolveLoadImageDirectory(node, out var resolvedDir))
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
                        string launcherAbs = SamOnnxSegmentation.ResolveModelPath(launcherRel);
                        string repoRoot = node.Params.GetValueOrDefault("jitRepoRoot", "")?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(repoRoot))
                            throw new InvalidOperationException("JiT采样: 请填写 jitRepoRoot（just-image-transformer 克隆路径）");
                        string cfgYaml = node.Params.GetValueOrDefault("configYaml", JitSampleBridge.DefaultConfigYamlRelative)
                                         ?? JitSampleBridge.DefaultConfigYamlRelative;
                        string ckRel = node.Params.GetValueOrDefault("checkpointPath", "")?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(ckRel))
                            throw new InvalidOperationException("JiT采样: 请填写 checkpointPath（model.npz 或含 model.npz 的 .zip）");
                        string ckAbs = SamOnnxSegmentation.ResolveModelPath(ckRel);
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
                                repoRoot,
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
                        node.Outputs["Image"] = img;
                        node.ResultSummary = $"Snap OK: dev={deviceIndex}";
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

                    case "world_coords":
                    {
                        string raw = node.Params.GetValueOrDefault("points", "") ?? "";
                        var coords = ParseWorldPointsParam(raw);
                        node.Outputs["Points"] = coords;
                        node.ResultSummary = $"Points: {coords.Length}";
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
                        string scriptAbs = SamOnnxSegmentation.ResolveModelPath(scriptRel);
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
                        string scriptAbs = SamOnnxSegmentation.ResolveModelPath(scriptRel);
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
                        string scriptAbs = SamOnnxSegmentation.ResolveModelPath(scriptRel);
                        string weightsRel = node.Params.GetValueOrDefault(
                                                "weightsPath",
                                                "yolo_data/runs/segment/train-2/weights/best.pt")
                                           ?? "yolo_data/runs/segment/train-2/weights/best.pt";
                        string weightsAbs = SamOnnxSegmentation.ResolveModelPath(weightsRel.Trim());
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
                        string encAbs = SamOnnxSegmentation.ResolveModelPath(string.IsNullOrWhiteSpace(enc) ? SamOnnxSegmentation.DefaultEncoderRepoRelative : enc);
                        string decAbs = SamOnnxSegmentation.ResolveModelPath(string.IsNullOrWhiteSpace(dec) ? SamOnnxSegmentation.DefaultDecoderRepoRelative : dec);

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
                            string onnxAbs = SamOnnxSegmentation.ResolveModelPath(owlv2OnnxRel);
                            string tokenizerAbs = SamOnnxSegmentation.ResolveModelPath(tokRel);
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
                        var rawPaths = node.Params.GetValueOrDefault("imagePaths", "");
                        int colsI = int.TryParse(node.Params.GetValueOrDefault("cols"), out int ci) ? ci : 9;
                        int rowsI = int.TryParse(node.Params.GetValueOrDefault("rows"), out int ri) ? ri : 6;
                        double sqMm = double.TryParse(node.Params.GetValueOrDefault("squareSizeMm"), out double sqv) ? sqv : 25.0;
                        var segments = rawPaths.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                        var resolved = new List<string>();
                        foreach (var seg in segments)
                        {
                            var p = seg.Trim();
                            if (string.IsNullOrEmpty(p)) continue;
                            var full = System.IO.Path.IsPathRooted(p)
                                ? System.IO.Path.GetFullPath(p)
                                : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, p));
                            resolved.Add(full);
                        }
                        if (resolved.Count == 0)
                            throw new InvalidOperationException("棋盘格内参: imagePaths 为空（使用分号分隔多张图路径）");
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

                    case "calibrate":
                    {
                        var imagePts = inputs["ImagePts"] as Point2D[];
                        var worldPts = inputs["WorldPts"] as Point2D[];
                        if (imagePts == null || worldPts == null)
                            throw new InvalidOperationException("标定: 缺少图像点或世界坐标点");
                        if (imagePts.Length != worldPts.Length)
                            throw new InvalidOperationException("标定: 图像点和世界点数量不一致");
                        var calResult = CalibAPI.CalibrateNinePoint(imagePts, worldPts);
                        if (!calResult.Success)
                            throw new InvalidOperationException($"标定失败: {calResult.ErrorMessage}");
                        node.Outputs["Transform"] = calResult.Transform;
                        break;
                    }

                    case "img_to_world":
                    {
                        var pixelPts = inputs["Pixel"] as Point2D[];
                        var transform = inputs["Transform"] as AffineTransform?;
                        if (pixelPts == null || transform == null)
                            throw new InvalidOperationException("坐标转换: 缺少输入点或变换矩阵");
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
                        var imagePts = inputs["ImagePts"] as Point2D[];
                        var worldPts = inputs["WorldPts"] as Point2D[];
                        if (imagePts == null || worldPts == null)
                            throw new InvalidOperationException("透视标定: 缺少图像点或世界坐标点");
                        if (imagePts.Length != worldPts.Length)
                            throw new InvalidOperationException("透视标定: 图像点和世界点数量不一致");
                        if (imagePts.Length < 4)
                            throw new InvalidOperationException("透视标定: 至少需要4个点");
                        var h = FitHomography(imagePts, worldPts);
                        node.Outputs["H"] = h;
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
                            node.Outputs["World"] = Array.Empty<Point2D>();
                            node.ResultSummary = "skip: 0 pts → empty World";
                            break;
                        }

                        node.Outputs["World"] = pixelPts.Select(p => ApplyHomography(p, h)).ToArray();
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
                        var resolvedPath = System.IO.Path.IsPathRooted(pathParam)
                            ? System.IO.Path.GetFullPath(pathParam)
                            : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, pathParam));
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
                        string resolvedPath = configuredPath;
                        if (!string.IsNullOrWhiteSpace(configuredPath) && !System.IO.Path.IsPathRooted(configuredPath))
                            resolvedPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath));

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
                        }
                        else if (!System.IO.File.Exists(resolvedPath))
                            throw new System.IO.FileNotFoundException($"读取标定结果: 文件不存在: {resolvedPath}");

                        string raw = System.IO.File.ReadAllText(resolvedPath, Encoding.UTF8);
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

                        if (IsFlowOutputPortWired(node, "CalibrationJson"))
                        {
                            if (string.IsNullOrWhiteSpace(dto.CalibrationJson))
                                throw new InvalidOperationException("读取标定结果: 文件不含 calibrationJson，请去掉该输出的连线或更换文件");
                            node.Outputs["CalibrationJson"] = dto.CalibrationJson;
                        }
                        if (IsFlowOutputPortWired(node, "Transform"))
                        {
                            if (dto.Affine == null)
                                throw new InvalidOperationException("读取标定结果: 文件不含 affine，请去掉 Transform 输出连线或更换文件");
                            node.Outputs["Transform"] = dto.Affine.ToAffine();
                        }
                        if (IsFlowOutputPortWired(node, "H"))
                        {
                            if (dto.Homography == null)
                                throw new InvalidOperationException("读取标定结果: 文件不含 homography，请去掉 H 输出连线或更换文件");
                            node.Outputs["H"] = dto.Homography.ToHomography();
                        }
                        if (IsFlowOutputPortWired(node, "Poly"))
                        {
                            if (dto.Poly2d == null)
                                throw new InvalidOperationException("读取标定结果: 文件不含 poly2d，请去掉 Poly 输出连线或更换文件");
                            node.Outputs["Poly"] = dto.Poly2d.ToPoly();
                        }
                        if (IsFlowOutputPortWired(node, "Intrinsics"))
                        {
                            if (dto.Intrinsics == null)
                                throw new InvalidOperationException("读取标定结果: 文件不含 intrinsics，请去掉 Intrinsics 输出连线或更换文件");
                            node.Outputs["Intrinsics"] = dto.Intrinsics.ToIntrinsics();
                        }

                        node.ResultSummary = $"标定结果已加载: {System.IO.Path.GetFileName(resolvedPath)}";
                        break;
                    }

                    case "display":
                    {
                        // 显示图像到预览窗口：可选背景图 Img，Image 作为前景层，Points 透明叠加；Xld 单独叠加折线
                        var foregroundImg = (inputs.TryGetValue("Image", out var fgObj) ? fgObj : null) as CalibImage;
                        var backgroundImg = (inputs.TryGetValue("Img", out var bgObj) ? bgObj : null) as CalibImage;
                        if (foregroundImg == null && backgroundImg == null)
                            throw new InvalidOperationException("显示: 缺少输入图像(Image 或 Img)");
                        inputs.TryGetValue("Points", out var ptsObj);
                        Point2D[]? overlayPts = ptsObj as Point2D[];
                        inputs.TryGetValue("Xld", out var xldObj);
                        var xldBundle = xldObj as HalconXldContourBundle;
                        int dotRadius = int.TryParse(node.Params.GetValueOrDefault("dotRadius"), out int r) ? r : 3;
                        string dispSlot = node.Id.ToString("D");
                        string dispTitle = $"{node.Def.DisplayName} [{node.Id.ToString("N")[..8]}]";
                        ShowImagePreview(foregroundImg, overlayPts, dotRadius, backgroundImg, dispSlot, dispTitle, xldBundle);
                        break;
                    }

                    case "save_image":
                    {
                        var inputImg = inputs["Image"] as CalibImage;
                        if (inputImg == null) throw new InvalidOperationException("保存图像: 缺少输入图像");

                        var pathParam = node.Params.GetValueOrDefault("filePath", "flow_output.bmp");
                        if (string.IsNullOrWhiteSpace(pathParam))
                            pathParam = "flow_output.bmp";
                        var resolvedPath = System.IO.Path.IsPathRooted(pathParam)
                            ? System.IO.Path.GetFullPath(pathParam)
                            : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, pathParam));
                        var dir = System.IO.Path.GetDirectoryName(resolvedPath);
                        if (!string.IsNullOrWhiteSpace(dir))
                            System.IO.Directory.CreateDirectory(dir);

                        var ok = CalibAPI.SaveImage(resolvedPath, inputImg);
                        if (!ok) throw new InvalidOperationException($"保存图像失败: {resolvedPath}");

                        node.Outputs["Out"] = inputImg;
                        node.ResultSummary = $"Saved: {System.IO.Path.GetFileName(resolvedPath)}";
                        break;
                    }

                    case "save_text":
                    {
                        if (!inputs.TryGetValue("Text", out var textObj) || textObj is not string text)
                            throw new InvalidOperationException("保存文本: 缺少 Text 输入");
                        var pathParam = node.Params.GetValueOrDefault("filePath", "flow_output.txt");
                        if (string.IsNullOrWhiteSpace(pathParam))
                            pathParam = "flow_output.txt";
                        var resolvedPath = System.IO.Path.IsPathRooted(pathParam)
                            ? System.IO.Path.GetFullPath(pathParam)
                            : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, pathParam));
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

                        Point2D[] simplified;
                        if (pts.Length <= 2)
                            simplified = pts;
                        else if (closed)
                            simplified = SimplifyClosedPolyline(pts, epsilon);
                        else
                            simplified = SimplifyOpenPolyline(pts.ToList(), epsilon).ToArray();

                        node.Outputs["Out"] = simplified;
                        node.ResultSummary = $"{pts.Length} → {simplified.Length} pts, ε={epsilon:G}";
                        break;
                    }

                    case "send_plc":
                    {
                        var pts = inputs["Points"] as Point2D[];
                        if (pts == null || pts.Length == 0)
                            throw new InvalidOperationException("发送PLC: 缺少有效的点位数据");
                        if (!_flowPlcConnected || _flowPlc == null)
                            throw new InvalidOperationException("发送PLC: PLC 未连接，请先执行 PLC连接 算子");
                        // v1：先打通链路，写入点数到寄存器0；后续可扩展完整轨迹协议
                        var wr = _flowPlc.Write("0", (short)pts.Length);
                        if (!wr.IsSuccess)
                            throw new InvalidOperationException($"发送PLC失败: {wr.Message}");
                        StatusText.Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = $"PLC发送成功: 点数={pts.Length}";
                        });
                        node.ResultSummary = $"PLC sent {pts.Length} pts";
                        break;
                    }

                    case "plc_connect":
                    {
                        string ip = (node.Params.GetValueOrDefault("ip", "192.168.6.6") ?? "192.168.6.6").Trim();
                        int port = int.TryParse(node.Params.GetValueOrDefault("port"), out var p) ? p : 502;
                        byte station = byte.TryParse(node.Params.GetValueOrDefault("station"), out var s) ? s : (byte)1;

                        if (_flowPlc != null)
                        {
                            try { _flowPlc.ConnectClose(); } catch { }
                            _flowPlc = null;
                        }

                        _flowPlc = new ModbusTcpNet(ip, port, station);
                        var conn = _flowPlc.ConnectServer();
                        if (!conn.IsSuccess)
                            throw new InvalidOperationException($"PLC连接失败: {conn.Message}");

                        _flowPlcConnected = true;
                        node.Outputs["Connected"] = true;
                        node.ResultSummary = $"Connected {ip}:{port} st={station}";
                        break;
                    }

                    case "plc_disconnect":
                    {
                        if (_flowPlc != null)
                        {
                            try { _flowPlc.ConnectClose(); } catch { }
                            _flowPlc = null;
                        }
                        _flowPlcConnected = false;
                        node.Outputs["Disconnected"] = true;
                        node.ResultSummary = "PLC disconnected";
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
                        var pts = HalconFlowBridge.SamplePointsFromXldBundle(xb, spacing, maxBars);
                        node.Outputs["Points"] = pts;
                        node.ResultSummary = pts.Length == 0 ? "skip: empty XLD → empty Points" : $"HALCON xld_pts {pts.Length}";
                        break;
                    }
#endif

                    case "composite_bind_in":
                    {
                        if (compositeExternalInputsForBindIn == null)
                            throw new InvalidOperationException("组合绑定入 仅用于组合算子子流程：填写父输入端口名，将 Out 连到子算子输入");
                        string ext = node.Params.GetValueOrDefault("externalPort", "In")?.Trim() ?? "In";
                        compositeExternalInputsForBindIn.TryGetValue(ext, out var v);
                        node.Outputs["Out"] = v;
                        node.ResultSummary = $"{ext}→子图";
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
            if (node.Def.Params.Count == 0) return;

            bool compositeUi = node.Def.TypeId == "composite";

            var win = new Window
            {
                Title = $"{node.Def.DisplayName} - 算子配置面板",
                Width = compositeUi ? 640 : 420,
                Height = compositeUi ? Math.Min(420 + node.Def.Params.Count * 56, 680) : Math.Min(60 + node.Def.Params.Count * 60, 500),
                MinWidth = compositeUi ? 520 : 380,
                MinHeight = compositeUi ? 360 : 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = compositeUi ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize
            };

            var root = new DockPanel();

            var fieldsPanel = new StackPanel { Margin = new Thickness(12, 12, 12, 8) };
            var inputs = new Control[node.Def.Params.Count];

            for (int i = 0; i < node.Def.Params.Count; i++)
            {
                var param = node.Def.Params[i];

                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };

                var label = new TextBlock
                {
                    Text = param.DisplayName,
                    Width = 120,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.Medium
                };

                string currentValue = node.Params.GetValueOrDefault(param.Name, param.DefaultValue);
                Control input;
                if (param.Options != null && param.Options.Count > 0)
                {
                    var cb = new ComboBox
                    {
                        Width = 200,
                        VerticalAlignment = VerticalAlignment.Center,
                        ItemsSource = param.Options
                    };
                    cb.SelectedItem = param.Options.Contains(currentValue) ? currentValue : param.DefaultValue;
                    input = cb;
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
                        Width = compositeUi ? 380 : 200,
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
                            pathBox.Text = ofd.FileName;
                    };
                }
                else if (param.Name == "directory" && node.Def.TypeId == "load_image_dir")
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
                            dirBox.Text = fbd.SelectedPath;
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
                            Filter = "组态文件|*.flow.json|所有文件|*.*"
                        };
                        if (ofd.ShowDialog() == true && input is TextBox pathBox)
                            pathBox.Text = ofd.FileName;
                    };
                }

                var tip = new TextBlock
                {
                    Text = param.Description,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontSize = 9,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };

                row.Children.Add(label);
                row.Children.Add(input);
                if (browseBtn != null) row.Children.Add(browseBtn);
                row.Children.Add(tip);
                fieldsPanel.Children.Add(row);
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
                PushFlowUndoSnapshotBeforeChange();
                for (int i = 0; i < node.Def.Params.Count; i++)
                {
                    string value = inputs[i] switch
                    {
                        ComboBox cb => cb.SelectedItem?.ToString() ?? node.Def.Params[i].DefaultValue,
                        TextBox tb => tb.Text,
                        _ => node.Def.Params[i].DefaultValue
                    };
                    node.Params[node.Def.Params[i].Name] = value;
                }
                if (node.Def.TypeId == "composite")
                    RefreshCompositeNodeCaption(node);
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
            HalconXldContourBundle? xldOverlay = null)
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
            if (drawPts || drawXld)
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
            AppendLog("========== 开始执行 ==========");

            // 清除所有节点的执行状态
            foreach (var n in _nodes)
            {
                n.Executed = false;
                n.ErrorMessage = null;
                n.Outputs.Clear();
                SetNodeStatus(n, false);
            }

            try
            {
                ThrowIfExecutionCancelled();
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
                _isRunInProgress = false;
                if (StopRunButton != null) StopRunButton.IsEnabled = false;
            }
        }

        private async System.Threading.Tasks.Task<bool> RunAllManagedFallbackAsync()
        {
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

                if (dirEachLoops.Count == 1)
                {
                    var loopNode = dirEachLoops[0];
                    var downstream = GetDownstreamNodes(loopNode);
                    var preNodes = sorted.Where(n => !downstream.Contains(n)).ToList();
                    var postNodes = sorted.Where(n => downstream.Contains(n) && n != loopNode).ToList();

                    AppendLog($"检测到 load_image_dir(遍历): {loopNode.Def.DisplayName}，前置 {preNodes.Count} 节点，下游 {postNodes.Count} 节点");

                    int successCountPre = 0;
                    for (int i = 0; i < preNodes.Count; i++)
                    {
                        ThrowIfExecutionCancelled();
                        var node = preNodes[i];
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
                            AppendLog($"[PRE][STOP] {node.Def.DisplayName}: {ex.Message}", true);
                            if (ex.InnerException != null)
                                AppendLog($"  {ex.InnerException.Message}", true);
                            StatusText.Text = ex.Message;
                            StatusText.Foreground = new SolidColorBrush(Colors.Orange);
                            AppendLog("========== 执行中止 ==========");
                            return false;
                        }
                    }

                    if (!TryResolveLoadImageDirectory(loopNode, out var resolvedDir))
                        throw new InvalidOperationException("加载图像目录: 未选择文件夹");

                    string extSpec = loopNode.Params.GetValueOrDefault("extensions", ".bmp;.png;.jpg;.jpeg;.tif;.tiff")
                                     ?? ".bmp;.png;.jpg;.jpeg;.tif;.tiff";
                    var paths = CollectSortedImagePathsFromDirectory(resolvedDir, extSpec);
                    if (paths.Count == 0)
                        throw new InvalidOperationException($"加载图像目录(遍历): 无匹配图像 ({resolvedDir})，扩展名: {extSpec}");

                    int okImages = 0;
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
                                AppendLog($"[IMG][STOP] {node.Def.DisplayName}: {ex.Message}", true);
                                if (ex.InnerException != null)
                                    AppendLog($"  {ex.InnerException.Message}", true);
                                StatusText.Text = ex.Message;
                                StatusText.Foreground = new SolidColorBrush(Colors.Orange);
                                AppendLog("========== 执行中止 ==========");
                                return false;
                            }
                        }
                    }

                    if (okImages <= 0)
                        throw new InvalidOperationException("加载图像目录(遍历): 未能成功解码任何图像");

                    StatusText.Text = $"目录遍历完成: 前置 {successCountPre}/{preNodes.Count}, 有效图 {okImages}/{paths.Count}";
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
                    for (int i = 0; i < preNodes.Count; i++)
                    {
                    ThrowIfExecutionCancelled();
                        var node = preNodes[i];
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
                            AppendLog($"[PRE][STOP] {node.Def.DisplayName}: {ex.Message}", true);
                            if (ex.InnerException != null)
                                AppendLog($"  {ex.InnerException.Message}", true);
                            StatusText.Text = ex.Message;
                            StatusText.Foreground = new SolidColorBrush(Colors.Orange);
                            AppendLog("========== 执行中止 ==========");
                            return false;
                        }
                    }

                    int deviceIndex = int.TryParse(loopNode.Params.GetValueOrDefault("deviceIndex"), out var di) ? di : 0;
                    int frameCount = int.TryParse(loopNode.Params.GetValueOrDefault("frameCount"), out var fc) ? fc : 10;
                    int intervalMs = int.TryParse(loopNode.Params.GetValueOrDefault("intervalMs"), out var im) ? im : 100;
                    int targetWidth = int.TryParse(loopNode.Params.GetValueOrDefault("targetWidth"), out var tw) ? tw : 0;
                    int targetHeight = int.TryParse(loopNode.Params.GetValueOrDefault("targetHeight"), out var th) ? th : 0;

                    frameCount = Math.Max(1, frameCount);
                    int okFrames = 0;
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
                                AppendLog($"[FRAME][STOP] {node.Def.DisplayName}: {ex.Message}", true);
                                if (ex.InnerException != null)
                                    AppendLog($"  {ex.InnerException.Message}", true);
                                StatusText.Text = ex.Message;
                                StatusText.Foreground = new SolidColorBrush(Colors.Orange);
                                AppendLog("========== 执行中止 ==========");
                                return false;
                            }
                        }

                        if (intervalMs > 0 && fi < frameCount - 1)
                            await System.Threading.Tasks.Task.Delay(intervalMs, _runCts?.Token ?? System.Threading.CancellationToken.None);
                    }

                    if (okFrames <= 0)
                        throw new InvalidOperationException("per_frame 未抓到任何有效帧");

                    StatusText.Text = $"per_frame 执行完成: 前置 {successCountPre}/{preNodes.Count}, 有效帧 {okFrames}/{frameCount}";
                    StatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
                    AppendLog($"========== per_frame 执行完成: frames={okFrames}/{frameCount} ==========");
                    return true;
                }

                AppendLog($"共 {sorted.Count} 个节点待执行");
                int successCount = 0;
                for (int i = 0; i < sorted.Count; i++)
                {
                    ThrowIfExecutionCancelled();
                    var node = sorted[i];
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
                StatusText.Text = $"执行完成(托管回退): {successCount}/{sorted.Count} 个节点成功";
                StatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
                AppendLog($"========== 执行完成(托管回退): {successCount}/{sorted.Count} ==========");
                return true;
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
