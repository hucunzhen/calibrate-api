using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Linq;
using Microsoft.Win32;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage : Page
    {
        // ================================================================
        // 算子定义模型
        // ================================================================

        /// <summary>
        /// 端口方向
        /// </summary>
        public enum PortDirection { Input, Output }

        /// <summary>
        /// 端口定义
        /// </summary>
        public class PortDef
        {
            public string Name { get; set; }
            public PortDirection Direction { get; set; }
            public Type DataType { get; set; }
            public string ColorHex { get; set; }  // 端口颜色
        }

        /// <summary>
        /// 算子参数定义
        /// </summary>
        public class OperatorParam
        {
            public string Name { get; set; }           // 参数名
            public string DisplayName { get; set; }    // 显示名
            public string DefaultValue { get; set; }   // 默认值（字符串）
            public string Description { get; set; }    // 参数说明
        }

        /// <summary>
        /// 算子类型定义（静态注册表）
        /// </summary>
        public class OperatorDef
        {
            public string TypeId { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string Category { get; set; }
            public List<PortDef> Ports { get; set; } = new List<PortDef>();
            public List<OperatorParam> Params { get; set; } = new List<OperatorParam>();
            public double DefaultWidth { get; set; } = 180;
            public double DefaultHeight { get; set; } = 80;
        }

        /// <summary>
        /// 可用算子注册表
        /// </summary>
        public static readonly List<OperatorDef> OperatorRegistry = new List<OperatorDef>
        {
            new OperatorDef
            {
                TypeId = "load_image",
                DisplayName = "加载图像",
                Description = "从文件或相机加载图像",
                Category = "输入",
                Ports = { new PortDef { Name = "Image", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" } }
            },
            new OperatorDef
            {
                TypeId = "grayscale",
                DisplayName = "灰度化",
                Description = "Step1: 图像转灰度",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "clahe",
                DisplayName = "CLAHE",
                Description = "对比度受限自适应直方图均衡化",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "clipLimit", DisplayName = "对比度限制", DefaultValue = "3.0", Description = "CLAHE clipLimit (1.0~4.0)" },
                    new OperatorParam { Name = "tileSize", DisplayName = "分块大小", DefaultValue = "8", Description = "网格大小 (像素)" }
                }
            },
            new OperatorDef
            {
                TypeId = "canny",
                DisplayName = "Canny边缘检测",
                Description = "Step1.5: Canny/Sobel 边缘检测",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Edge", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "binarize",
                DisplayName = "二值化",
                Description = "预处理(Otsu二值化)输出二值图",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "blurSize", DisplayName = "模糊核大小", DefaultValue = "7", Description = "高斯模糊核 (奇数)" },
                    new OperatorParam { Name = "morphSize", DisplayName = "形态学核大小", DefaultValue = "5", Description = "形态学操作核 (奇数)" }
                }
            },
            new OperatorDef
            {
                TypeId = "gray_range_binary",
                DisplayName = "灰度范围二值化",
                Description = "按指定灰度区间[low,high]二值化",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "grayLow", DisplayName = "灰度下限", DefaultValue = "5", Description = "灰度范围下界 (0~255)" },
                    new OperatorParam { Name = "grayHigh", DisplayName = "灰度上限", DefaultValue = "50", Description = "灰度范围上界 (0~255)" }
                }
            },
            new OperatorDef
            {
                TypeId = "find_contours",
                DisplayName = "查找轮廓",
                Description = "从二值图中提取暗条轮廓、排序并输出可视化图和轮廓数据",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF9800" },
                    new PortDef { Name = "Contours", Direction = PortDirection.Output, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0" }
                }
            },
            new OperatorDef
            {
                TypeId = "apply_mask",
                DisplayName = "Mask应用",
                Description = "用Mask处理原图，保留Mask区域内像素",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Mask", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "create_mask",
                DisplayName = "生成Mask",
                Description = "Step2.5: 按轮廓索引填充内部生成Mask（-1=自动选最大）",
                Category = "检测",
                Params =
                {
                    new OperatorParam { Name = "contourIdx", DisplayName = "轮廓索引", DefaultValue = "-1", Description = "轮廓索引 (-1=自动选最大)" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Mask", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "detect_hollow",
                DisplayName = "空洞检测",
                Description = "Step3b: mask内按灰度范围检测空洞",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "Mask", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF9800" },
                    new PortDef { Name = "Hollow", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "hollowGrayLow", DisplayName = "空洞灰度下限", DefaultValue = "5", Description = "空洞灰度下界 (0~255)" },
                    new OperatorParam { Name = "hollowGrayHigh", DisplayName = "空洞灰度上限", DefaultValue = "50", Description = "空洞灰度上界 (0~255)" },
                    new OperatorParam { Name = "targetHollows", DisplayName = "目标空洞数", DefaultValue = "16", Description = "期望检测的空洞数量" },
                    new OperatorParam { Name = "morphKernelSize", DisplayName = "形态学核大小", DefaultValue = "5", Description = "形态学核大小 (奇数)" }
                }
            },
            new OperatorDef
            {
                TypeId = "detect_dark",
                DisplayName = "暗条检测",
                Description = "Step4: 检测暗条二值化",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Mask", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Dark", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "darkThreshold", DisplayName = "暗条阈值", DefaultValue = "50", Description = "暗条灰度阈值" }
                }
            },
            new OperatorDef
            {
                TypeId = "morphology",
                DisplayName = "形态学清理",
                Description = "Step5: Morphology清理",
                Category = "后处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "kernelSize", DisplayName = "核大小", DefaultValue = "5", Description = "形态学核大小 (奇数)" },
                    new OperatorParam { Name = "blurKsize", DisplayName = "模糊核大小", DefaultValue = "9", Description = "高斯模糊核大小 (奇数)" },
                    new OperatorParam { Name = "blurSigma", DisplayName = "模糊Sigma", DefaultValue = "2.0", Description = "高斯模糊Sigma" }
                }
            },
            new OperatorDef
            {
                TypeId = "sort_contours",
                DisplayName = "排序轮廓",
                Description = "Step6: 找暗条轮廓并排序",
                Category = "后处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "sample",
                DisplayName = "采样",
                Description = "使用上游轮廓数据进行等弧长采样",
                Category = "输出",
                Ports =
                {
                    new PortDef { Name = "Contours", Direction = PortDirection.Input, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                },
                Params =
                {
                    new OperatorParam { Name = "targetBars", DisplayName = "目标轮廓数", DefaultValue = "16", Description = "按面积取前N个轮廓" },
                    new OperatorParam { Name = "spacing", DisplayName = "采样间距", DefaultValue = "3", Description = "等弧长采样间距 (像素)" }
                }
            },
            new OperatorDef
            {
                TypeId = "fit_shape",
                DisplayName = "形状拟合",
                Description = "Step7.5: Stadium拟合/曲率去噪",
                Category = "输出",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "verify_mask",
                DisplayName = "Mask验证",
                Description = "Step8: 通过mask验证轨迹",
                Category = "验证",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "dedup",
                DisplayName = "去重排序",
                Description = "Step9: 去重+排序",
                Category = "验证",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "convert_output",
                DisplayName = "输出转换",
                Description = "Step10: 转换为输出格式",
                Category = "输出",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Result", Direction = PortDirection.Output, DataType = typeof(TrajectoryResult), ColorHex = "#9C27B0" }
                }
            },
            new OperatorDef
            {
                TypeId = "draw_color",
                DisplayName = "绘制彩色轨迹",
                Description = "Step11: 绘制彩色结果",
                Category = "可视化",
                Ports =
                {
                    new PortDef { Name = "Result", Direction = PortDirection.Input, DataType = typeof(TrajectoryResult), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Image", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "display",
                DisplayName = "显示图像",
                Description = "在窗口中显示图像，可选叠加点位",
                Category = "可视化",
                Params =
                {
                    new OperatorParam { Name = "dotRadius", DisplayName = "点位半径", DefaultValue = "3", Description = "叠加点位的圆点半径 (像素)" }
                },
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Points", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "world_coords",
                DisplayName = "世界坐标",
                Description = "手动输入九点标定的世界坐标",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "calibrate",
                DisplayName = "九点标定",
                Description = "标定像素坐标→世界坐标",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "ImagePts", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "WorldPts", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Transform", Direction = PortDirection.Output, DataType = typeof(AffineTransform), ColorHex = "#E91E63" }
                }
            },
            new OperatorDef
            {
                TypeId = "img_to_world",
                DisplayName = "坐标转换",
                Description = "像素坐标→世界坐标",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "Pixel", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Transform", Direction = PortDirection.Input, DataType = typeof(AffineTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "World", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "send_plc",
                DisplayName = "发送PLC",
                Description = "将轨迹发送到PLC",
                Category = "输出",
                Ports =
                {
                    new PortDef { Name = "Points", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "detect_circles",
                DisplayName = "检测圆点",
                Description = "检测标定圆点位置",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "expand_edge",
                DisplayName = "边界膨胀",
                Description = "Step5.5: 沿边缘膨胀",
                Category = "后处理",
                Ports =
                {
                    new PortDef { Name = "Dark", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Edge", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "expandDist", DisplayName = "膨胀距离", DefaultValue = "15", Description = "边缘膨胀距离 (像素)" }
                }
            },
        };

        // ================================================================
        // 画布上的节点模型
        // ================================================================

        /// <summary>
        /// 画布上已放置的节点实例
        /// </summary>
        public class FlowNode
        {
            public Guid Id { get; } = Guid.NewGuid();
            public OperatorDef Def { get; }
            public double X { get; set; }
            public double Y { get; set; }

            // UI 元素（由 FlowPage 创建）
            public FrameworkElement? Visual { get; set; }
            public List<PortVisual> PortVisuals { get; } = new List<PortVisual>();

            // 执行结果数据（端口名 → 数据对象）
            public Dictionary<string, object?> Outputs { get; } = new Dictionary<string, object?>();
            public bool Executed { get; set; }
            public string? ErrorMessage { get; set; }

            // 算子参数（参数名 → 当前值）
            public Dictionary<string, string> Params { get; } = new Dictionary<string, string>();

            // 结果摘要文本
            public string? ResultSummary { get; set; }

            public FlowNode(OperatorDef def, double x, double y)
            {
                Def = def;
                X = x;
                Y = y;
                // 初始化参数为默认值
                foreach (var p in def.Params)
                    Params[p.Name] = p.DefaultValue;
            }
        }

        /// <summary>
        /// 端口可视元素（一个小圆圈 + 在节点上的位置）
        /// </summary>
        public class PortVisual
        {
            public PortDef Definition { get; }
            public FlowNode Owner { get; }
            public Ellipse Ellipse { get; }
            public Point Center { get; set; }  // 画布坐标系下的中心点

            public PortVisual(PortDef def, FlowNode owner, Ellipse ellipse)
            {
                Definition = def;
                Owner = owner;
                Ellipse = ellipse;
            }
        }

        /// <summary>
        /// 连线
        /// </summary>
        public class FlowConnection
        {
            public Guid Id { get; } = Guid.NewGuid();
            public PortVisual FromPort { get; }
            public PortVisual ToPort { get; }
            public Path? PathVisual { get; set; }

            public FlowConnection(PortVisual from, PortVisual to)
            {
                FromPort = from;
                ToPort = to;
            }
        }

        // ================================================================
        // 页面状态
        // ================================================================

        private readonly List<FlowNode> _nodes = new List<FlowNode>();
        private readonly List<FlowConnection> _connections = new List<FlowConnection>();
        private readonly ObservableCollection<OperatorDef> _toolboxItems = new ObservableCollection<OperatorDef>();

        // 拖拽状态
        private bool _isDraggingNode;
        private FlowNode? _dragNode;
        private Point _dragStart;
        private Point _nodeStartPos;

        // 连线状态
        private PortVisual? _connectingFromPort;
        private Path? _tempConnectionPath;

        private int _nodeCounter;

        public FlowPage()
        {
            InitializeComponent();
            InitializeToolbox();
        }

        private void InitializeToolbox()
        {
            // 按分类排序
            var sorted = OperatorRegistry.OrderBy(o => o.Category).ThenBy(o => o.DisplayName).ToList();
            foreach (var op in sorted)
                _toolboxItems.Add(op);
            OperatorToolbox.ItemsSource = _toolboxItems;
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
            AddNode(def, pos.X - def.DefaultWidth / 2, pos.Y - def.DefaultHeight / 2);
            e.Handled = true;
        }

        // ================================================================
        // 节点创建
        // ================================================================

        private FlowNode AddNode(OperatorDef def, double x, double y)
        {
            var node = new FlowNode(def, x, y);
            _nodes.Add(node);
            CreateNodeVisual(node);
            return node;
        }

        private void CreateNodeVisual(FlowNode node)
        {
            var def = node.Def;
            double w = def.DefaultWidth;
            double h = def.DefaultHeight;

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
            var menuDelete = new MenuItem { Header = "删除算子" };
            menuDelete.Click += (s, e) => DeleteNode(node);

            var menuParams = new MenuItem { Header = "参数设置" };
            menuParams.Click += (s, e) => EditNodeParams(node);

            border.ContextMenu = new ContextMenu { Items = { menuParams, menuDelete } };

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
            titleBorder.Child = titleText;
            panel.Children.Add(titleBorder);

            // 端口区域
            var portsGrid = new Grid { Margin = new Thickness(6, 4, 6, 4) };
            portsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            portsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int inputIdx = 0;
            int outputIdx = 0;
            int rowCount = Math.Max(
                def.Ports.Count(p => p.Direction == PortDirection.Input),
                def.Ports.Count(p => p.Direction == PortDirection.Output));
            if (rowCount == 0) rowCount = 1;

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

            // 初始化端口位置
            UpdatePortPositions(node);
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
                _dragNode.Visual.ReleaseMouseCapture();
                _isDraggingNode = false;
                _dragNode = null;
            }

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
            // 相同数据类型
            if (from.Definition.DataType != to.Definition.DataType) return false;
            // 不重复连线
            if (_connections.Any(c =>
                (c.FromPort == from && c.ToPort == to) ||
                (c.FromPort == to && c.ToPort == from))) return false;
            // 目标端口只能有一条输入连线
            if (to.Definition.Direction == PortDirection.Input &&
                _connections.Any(c => c.ToPort == to)) return false;

            return true;
        }

        // ================================================================
        // 连线管理
        // ================================================================

        private void CreateConnection(PortVisual from, PortVisual to)
        {
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
                _connections.Remove(conn);
                if (conn.PathVisual != null)
                    FlowCanvas.Children.Remove(conn.PathVisual);
                e.Handled = true;
            }
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

                var data = new FlowData { Nodes = nodes, Connections = connections };
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(data, options);
                System.IO.File.WriteAllText(dlg.FileName, json);

                StatusText.Text = $"已保存: {System.IO.Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

            try
            {
                var json = System.IO.File.ReadAllText(dlg.FileName);
                var data = JsonSerializer.Deserialize<FlowData>(json);
                if (data == null) throw new Exception("文件内容为空");

                // 先清空画布
                ClearCanvas_Click(sender, e);

                // 按 Id 查找算子定义
                var defLookup = OperatorRegistry.ToDictionary(d => d.TypeId);

                // 第一遍：创建所有节点
                var nodeLookup = new Dictionary<string, FlowNode>();
                foreach (var nd in data.Nodes)
                {
                    if (!defLookup.TryGetValue(nd.TypeId, out var def))
                        throw new Exception($"未知算子类型: {nd.TypeId}");
                    var node = AddNode(def, nd.X, nd.Y);
                    // 恢复参数
                    if (nd.Params != null)
                    {
                        foreach (var kv in nd.Params)
                        {
                            if (node.Params.ContainsKey(kv.Key))
                                node.Params[kv.Key] = kv.Value;
                        }
                    }
                    nodeLookup[nd.Id] = node;
                }

                // 第二遍：建立连线
                foreach (var cd in data.Connections)
                {
                    if (!nodeLookup.TryGetValue(cd.FromNodeId, out var fromNode))
                        continue;
                    if (!nodeLookup.TryGetValue(cd.ToNodeId, out var toNode))
                        continue;

                    var fromPort = fromNode.PortVisuals.FirstOrDefault(p =>
                        p.Definition.Name == cd.FromPort && p.Definition.Direction == PortDirection.Output);
                    var toPort = toNode.PortVisuals.FirstOrDefault(p =>
                        p.Definition.Name == cd.ToPort && p.Definition.Direction == PortDirection.Input);

                    if (fromPort != null && toPort != null && CanConnect(fromPort, toPort))
                        CreateConnection(fromPort, toPort);
                }

                StatusText.Text = $"已加载: {System.IO.Path.GetFileName(dlg.FileName)} ({data.Nodes.Count} 节点, {data.Connections.Count} 连线)";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ================================================================
        // JSON 序列化模型
        // ================================================================

        private class FlowData
        {
            public List<FlowNodeData> Nodes { get; set; } = new();
            public List<FlowConnData> Connections { get; set; } = new();
        }

        private class FlowNodeData
        {
            public string Id { get; set; } = "";
            public string TypeId { get; set; } = "";
            public double X { get; set; }
            public double Y { get; set; }
            public Dictionary<string, string>? Params { get; set; }
        }

        private class FlowConnData
        {
            public string FromNodeId { get; set; } = "";
            public string FromPort { get; set; } = "";
            public string ToNodeId { get; set; } = "";
            public string ToPort { get; set; } = "";
        }

        private void ClearCanvas_Click(object sender, RoutedEventArgs e)
        {
            // 删除所有连线
            foreach (var conn in _connections)
            {
                if (conn.PathVisual != null)
                    FlowCanvas.Children.Remove(conn.PathVisual);
            }
            _connections.Clear();

            // 删除所有节点
            foreach (var node in _nodes)
            {
                if (node.Visual != null)
                    FlowCanvas.Children.Remove(node.Visual);
            }
            _nodes.Clear();

            _nodeCounter = 0;
            StatusText.Text = "画布已清空";
        }

        private void DeleteNode(FlowNode node)
        {
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

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogBox.Text = "";
        }

        private TrajectoryStepDetector? _stepDetector;

        private void ExecuteNode(FlowNode node)
        {
            var inputs = GetNodeInputs(node);
            node.Outputs.Clear();
            node.ErrorMessage = null;
            SetNodeStatus(node, true);

            try
            {
                switch (node.Def.TypeId)
                {
                    case "load_image":
                    {
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

                    case "world_coords":
                    {
                        var coords = InputWorldCoordinates();
                        if (coords != null)
                            node.Outputs["Points"] = coords;
                        else
                            node.ErrorMessage = "用户取消";
                        break;
                    }

                    case "grayscale":
                    {
                        // 需要 TrajectoryStepDetector 上下文
                        _stepDetector?.Dispose();
                        _stepDetector = new TrajectoryStepDetector();
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("灰度化: 缺少输入图像");
                        _stepDetector.ConvertToGrayscale(srcImg);
                        var gray = _stepDetector.GetStepImage(1);
                        node.Outputs["Out"] = gray;
                        break;
                    }

                    case "clahe":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("CLAHE: 请先执行灰度化");
                        double clipLimit = double.Parse(node.Params["clipLimit"]);
                        int tileSize = int.Parse(node.Params["tileSize"]);
                        _stepDetector.ApplyCLAHE(clipLimit, tileSize);
                        var claheImg = _stepDetector.GetStepImage(1);
                        node.Outputs["Out"] = claheImg;
                        break;
                    }

                    case "binarize":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("二值化: 请先执行灰度化");
                        int blurSize = int.Parse(node.Params["blurSize"]);
                        int morphSize = int.Parse(node.Params["morphSize"]);
                        _stepDetector.PreprocessAndFindContours(blurSize, morphSize, false);
                        var binImg = _stepDetector.GetStepImage(2);
                        node.Outputs["Out"] = binImg;
                        break;
                    }

                    case "gray_range_binary":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("灰度范围二值化: 请先执行灰度化");
                        int grayLow = int.Parse(node.Params["grayLow"]);
                        int grayHigh = int.Parse(node.Params["grayHigh"]);
                        _stepDetector.GrayRangeBinary(grayLow, grayHigh);
                        var grayBinImg = _stepDetector.GetStepImage(2);
                        node.Outputs["Out"] = grayBinImg;
                        break;
                    }

                    case "canny":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("Canny: 请先执行灰度化");
                        _stepDetector.CannyEdges(0, 0, 0); // 全部自适应
                        var edgeImg = _stepDetector.GetStepImage(6);
                        node.Outputs["Edge"] = edgeImg;
                        break;
                    }

                    case "find_contours":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("查找轮廓: 请先执行灰度化");
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg != null)
                        {
                            _stepDetector.SetDarkBinary(inImg);
                            _stepDetector.SetMask(inImg);
                        }
                        int count = _stepDetector.FindAndSortDarkContours();
                        var contourVis = _stepDetector.GetContourVis();
                        node.Outputs["Out"] = contourVis;
                        node.Outputs["Count"] = count;
                        var (cx, cy, cl, cc) = _stepDetector.ExportSortedContours();
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
                        if (_stepDetector == null) throw new InvalidOperationException("生成Mask: 请先执行查找轮廓");
                        int contourIdx = int.Parse(node.Params["contourIdx"]);
                        int ret = _stepDetector.CreateMaskFromLargestContour(contourIdx);
                        if (ret != 0) throw new InvalidOperationException("生成Mask: 轮廓为空或查找失败");
                        var maskImg = _stepDetector.GetStepImage(8);  // 二值mask
                        node.Outputs["Mask"] = maskImg;
                        break;
                    }

                    case "detect_hollow":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("空洞检测: 请先执行灰度化");
                        int hLow = int.Parse(node.Params["hollowGrayLow"]);
                        int hHigh = int.Parse(node.Params["hollowGrayHigh"]);
                        int targetH = int.Parse(node.Params["targetHollows"]);
                        int morphK = int.Parse(node.Params["morphKernelSize"]);
                        int hollowCount = _stepDetector.DetectHollow(hLow, hHigh, targetH, morphK);
                        var hollowImg = _stepDetector.GetStepImage(4);
                        node.Outputs["Count"] = hollowCount;
                        node.Outputs["Hollow"] = hollowImg;
                        break;
                    }

                    case "detect_dark":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("暗条检测: 请先执行生成Mask");
                        int darkTh = int.Parse(node.Params["darkThreshold"]);
                        _stepDetector.DetectDarkBars(darkTh, 0, 0);
                        var darkImg = _stepDetector.GetStepImage(4);
                        node.Outputs["Dark"] = darkImg;
                        break;
                    }

                    case "morphology":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("形态学: 请先执行暗条检测");
                        var darkImg = inputs["In"] as CalibImage;
                        if (darkImg != null)
                            _stepDetector.SetDarkBinary(darkImg);
                        int kernelSize = int.Parse(node.Params["kernelSize"]);
                        int blurKsize = int.Parse(node.Params["blurKsize"]);
                        double blurSigma = double.Parse(node.Params["blurSigma"]);
                        _stepDetector.MorphologyCleanup(kernelSize, blurKsize, blurSigma);
                        var morphImg = _stepDetector.GetStepImage(4);
                        node.Outputs["Out"] = morphImg;
                        break;
                    }

                    case "expand_edge":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("边界膨胀: 请先执行 Canny");
                        int expandDist = int.Parse(node.Params["expandDist"]);
                        _stepDetector.ExpandToEdgeBoundary(expandDist);
                        var expandImg = _stepDetector.GetStepImage(7);
                        node.Outputs["Out"] = expandImg;
                        break;
                    }

                    case "sort_contours":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("排序: 请先执行形态学");
                        int count = _stepDetector.FindAndSortDarkContours();
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
                            throw new InvalidOperationException("采样: 轮廓为空，请先执行查找轮廓");
                        int targetBars = int.Parse(node.Params["targetBars"]);
                        int spacing = int.Parse(node.Params["spacing"]);
                        var pts = CalibAPI.SampleContoursFromPoints(flatX, flatY, contourLengths,
                            CalibAPI.ImageWidth, CalibAPI.ImageHeight, targetBars, (double)spacing);
                        node.Outputs["Points"] = pts;
                        break;
                    }

                    case "fit_shape":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("拟合: 请先执行采样");
                        _stepDetector.FitShape(0, 0.0);
                        var fitPts = _stepDetector.GetPoints();
                        node.Outputs["Out"] = fitPts;
                        break;
                    }

                    case "verify_mask":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("验证: 请先执行拟合");
                        _stepDetector.VerifyByMask();
                        var verPts = _stepDetector.GetPoints();
                        node.Outputs["Out"] = verPts;
                        break;
                    }

                    case "dedup":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("去重: 请先执行验证");
                        _stepDetector.DeduplicateAndSort();
                        var dedupPts = _stepDetector.GetPoints();
                        node.Outputs["Out"] = dedupPts;
                        break;
                    }

                    case "convert_output":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("输出转换: 请先执行去重");
                        var result = _stepDetector.ConvertToOutput();
                        node.Outputs["Result"] = result;
                        break;
                    }

                    case "draw_color":
                    {
                        if (_stepDetector == null) throw new InvalidOperationException("绘制彩色: 请先执行输出转换");
                        var imgWidth = CalibAPI.ImageWidth;
                        var imgHeight = CalibAPI.ImageHeight;
                        var colorImg = _stepDetector.DrawColoredTrajectory(imgWidth, imgHeight);
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
                        CalibAPI.SetTransform(transform.Value);
                        var worldPts2 = pixelPts.Select(p => CalibAPI.ImageToWorld(p, transform.Value)).ToArray();
                        node.Outputs["World"] = worldPts2;
                        break;
                    }

                    case "display":
                    {
                        // 显示图像到预览窗口，可选叠加点位
                        var displayImg = inputs["Image"] as CalibImage;
                        if (displayImg == null) throw new InvalidOperationException("显示: 缺少输入图像");
                        inputs.TryGetValue("Points", out var ptsObj);
                        Point2D[]? overlayPts = ptsObj as Point2D[];
                        int dotRadius = int.TryParse(node.Params.GetValueOrDefault("dotRadius"), out int r) ? r : 3;
                        ShowImagePreview(displayImg, overlayPts, dotRadius);
                        break;
                    }

                    case "send_plc":
                    {
                        var pts = inputs["Points"] as Point2D[];
                        if (pts == null || pts.Length == 0)
                            throw new InvalidOperationException("发送PLC: 缺少有效的点位数据");
                        // TODO: 接入 PlcPage 的发送逻辑
                        StatusText.Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = $"轨迹共 {pts.Length} 个点 (PLC发送待接入)";
                        });
                        break;
                    }

                    default:
                        throw new InvalidOperationException($"未知算子: {node.Def.TypeId}");
                }

                node.Executed = true;
                SetNodeStatus(node, false);
                UpdateNodeSummary(node);
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

            var win = new Window
            {
                Title = $"{node.Def.DisplayName} - 参数设置",
                Width = 420,
                Height = Math.Min(60 + node.Def.Params.Count * 60, 500),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(12) };

            var txts = new TextBox[node.Def.Params.Count];

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

                var tb = new TextBox
                {
                    Width = 200,
                    Text = node.Params.GetValueOrDefault(param.Name, param.DefaultValue),
                    VerticalAlignment = VerticalAlignment.Center
                };
                txts[i] = tb;

                var tip = new TextBlock
                {
                    Text = param.Description,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontSize = 9,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };

                row.Children.Add(label);
                row.Children.Add(tb);
                row.Children.Add(tip);
                sp.Children.Add(row);
            }

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var btnOk = new Button { Content = "确定", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
            var btnCancel = new Button { Content = "取消", Width = 80 };

            btnOk.Click += (_, _) =>
            {
                for (int i = 0; i < node.Def.Params.Count; i++)
                    node.Params[node.Def.Params[i].Name] = txts[i].Text;
                win.Close();
            };
            btnCancel.Click += (_, _) => win.Close();

            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            sp.Children.Add(btnPanel);

            win.Content = sp;
            win.ShowDialog();
        }

        /// <summary>
        /// 更新节点上的结果摘要文本
        /// </summary>
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

            // 更新 UI
            if (node.Visual is Border border)
            {
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
        /// 滚轮缩放，左键拖拽平移，右键/F 适应窗口，1 重置100%
        /// </summary>
        private void ShowImagePreview(CalibImage img, Point2D[]? overlayPoints = null, int dotRadius = 3)
        {
            var bmp = img.ToBitmap();
            if (bmp == null) return;

            // 如果有叠加点位，在 bitmap 上画点
            if (overlayPoints != null && overlayPoints.Length > 0)
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
                    for (int i = 0; i < overlayPoints.Length; i++)
                    {
                        var p = overlayPoints[i];
                        int cx = (int)p.X, cy = (int)p.Y;
                        int r = dotRadius;
                        g.DrawEllipse(System.Drawing.Pens.Lime, cx - r, cy - r, r * 2, r * 2);
                        g.DrawLine(System.Drawing.Pens.Lime, cx - r - 1, cy, cx + r + 1, cy);
                        g.DrawLine(System.Drawing.Pens.Lime, cx, cy - r - 1, cx, cy + r + 1);
                    }
                }
            }

            var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(img.Width, img.Height));

            var win = new Window
            {
                Title = overlayPoints != null ? $"图像预览 ({overlayPoints.Length} 个点)" : "图像预览",
                Width = Math.Min(img.Width + 40, 1400),
                Height = Math.Min(img.Height + 60, 950),
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
            var imageCtrl = new System.Windows.Controls.Image { Source = bitmapSource, Width = img.Width, Height = img.Height };
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

            // ---- 状态 ----
            bool isPanning = false;
            Point panStart = default;
            double panOffsetX0 = 0, panOffsetY0 = 0;

            // ---- 鼠标滚轮缩放（以鼠标位置为中心） ----
            border.MouseWheel += (_, e) =>
            {
                var pos = e.GetPosition(border);
                double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
                double newScale = scaleTransform.ScaleX * factor;
                newScale = Math.Max(0.05, Math.Min(newScale, 50.0));

                // 以鼠标位置为锚点缩放
                translateTransform.X = pos.X - (pos.X - translateTransform.X) * (newScale / scaleTransform.ScaleX);
                translateTransform.Y = pos.Y - (pos.Y - translateTransform.Y) * (newScale / scaleTransform.ScaleY);
                scaleTransform.ScaleX = newScale;
                scaleTransform.ScaleY = newScale;

                zoomText.Text = $"{(int)(newScale * 100)}%";
            };

            // ---- 左键拖拽平移 ----
            border.MouseLeftButtonDown += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    isPanning = true;
                    panStart = e.GetPosition(border);
                    panOffsetX0 = translateTransform.X;
                    panOffsetY0 = translateTransform.Y;
                    border.Cursor = Cursors.ScrollAll;
                    border.CaptureMouse();
                    e.Handled = true;
                }
            };
            border.MouseMove += (_, e) =>
            {
                if (isPanning)
                {
                    var cur = e.GetPosition(border);
                    translateTransform.X = panOffsetX0 + (cur.X - panStart.X);
                    translateTransform.Y = panOffsetY0 + (cur.Y - panStart.Y);
                }
            };
            border.MouseLeftButtonUp += (_, e) =>
            {
                if (isPanning)
                {
                    isPanning = false;
                    border.ReleaseMouseCapture();
                    border.Cursor = null;
                }
            };

            // ---- 中键/右键双击 适应窗口 ----
            void FitToView()
            {
                double scaleX = border.ActualWidth / img.Width;
                double scaleY = border.ActualHeight / img.Height;
                double fitScale = Math.Min(scaleX, scaleY) * 0.95;
                fitScale = Math.Max(fitScale, 0.05);
                scaleTransform.ScaleX = fitScale;
                scaleTransform.ScaleY = fitScale;
                double offsetX = (border.ActualWidth - img.Width * fitScale) / 2;
                double offsetY = (border.ActualHeight - img.Height * fitScale) / 2;
                translateTransform.X = offsetX;
                translateTransform.Y = offsetY;
                zoomText.Text = $"{(int)(fitScale * 100)}%";
            }

            border.MouseRightButtonDown += (_, e) => { e.Handled = true; };

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

            win.Show();
        }

        private async void RunAll_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "运行中...";
            StatusText.Foreground = new SolidColorBrush(Colors.Orange);
            LogBox.Text = "";
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
                var sorted = TopologicalSort();

                // 检查是否有循环依赖
                if (sorted.Count != _nodes.Count)
                {
                    StatusText.Text = "错误: 检测到循环依赖!";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog("[ERROR] 检测到循环依赖，无法执行!", true);
                    return;
                }

                AppendLog($"共 {sorted.Count} 个节点待执行");

                // 按拓扑顺序逐个执行
                int successCount = 0;
                for (int i = 0; i < sorted.Count; i++)
                {
                    var node = sorted[i];
                    StatusText.Text = $"执行 [{i + 1}/{sorted.Count}] {node.Def.DisplayName}...";
                    AppendLog($"[{i + 1}/{sorted.Count}] 执行: {node.Def.DisplayName}");

                    // 让 UI 有机会刷新
                    await System.Threading.Tasks.Task.Yield();

                    try
                    {
                        ExecuteNode(node);
                        successCount++;
                        AppendLog($"  -> OK: {node.Def.DisplayName}");
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"  -> [ERROR] {node.Def.DisplayName}: {ex.Message}", true);
                        StatusText.Text = $"执行失败: {node.Def.DisplayName} - {ex.Message}";
                        StatusText.Foreground = new SolidColorBrush(Colors.Red);
                        AppendLog("========== 执行中止 ==========");
                        return;
                    }
                }

                // 释放 step detector
                _stepDetector?.Dispose();
                _stepDetector = null;

                StatusText.Text = $"执行完成: {successCount}/{sorted.Count} 个节点成功";
                StatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
                AppendLog($"========== 执行完成: {successCount}/{sorted.Count} 成功 ==========");
            }
            catch (Exception ex)
            {
                StatusText.Text = $"执行失败: {ex.Message}";
                StatusText.Foreground = new SolidColorBrush(Colors.Red);
                AppendLog($"[ERROR] 未预期异常: {ex.Message}", true);
                AppendLog("========== 执行中止 ==========");
            }
        }
    }
}
