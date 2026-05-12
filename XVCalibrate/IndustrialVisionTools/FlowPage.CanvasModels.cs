// 流程页分文件：画布节点 / 端口 / 工具箱分组 / 连线模型。

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

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage : UserControl
    {
        // ================================================================
        // 画布上的节点模型
        // ================================================================

        /// <summary>
        /// 画布上已放置的节点实例
        /// </summary>
        public class FlowNode
        {
            public Guid Id { get; }
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

            /// <summary>组合算子标题下显示子流程文件名（仅 UI）</summary>
            public TextBlock? CompositeCaptionText { get; set; }

            public FlowNode(OperatorDef def, double x, double y, Guid? fixedId = null)
            {
                Id = fixedId ?? Guid.NewGuid();
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

        public class ToolboxGroup
        {
            public string Name { get; set; } = "";
            public bool IsExpanded { get; set; } = true;
            public ObservableCollection<OperatorDef> Operators { get; } = new ObservableCollection<OperatorDef>();
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

    }
}
