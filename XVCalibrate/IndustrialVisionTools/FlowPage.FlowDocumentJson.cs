// 流程页分文件：流程编排 JSON 序列化用 DTO（FlowData / 组合绑定等）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        // JSON 序列化模型
        // ================================================================

        private class FlowData
        {
            public List<FlowNodeData> Nodes { get; set; } = new();
            public List<FlowConnData> Connections { get; set; } = new();

            /// <summary>可选元数据，如子流程独立调试路径、流程级阵列行列 latticeGridRows/Cols。</summary>
            public Dictionary<string, string>? Meta { get; set; }
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

        private class CompositeBindingsSpec
        {
            public List<CompositeIoBind>? Inputs { get; set; }
            public List<CompositeIoBind>? Outputs { get; set; }
        }

        private class CompositeIoBind
        {
            public string? External { get; set; }
            public string? NodeId { get; set; }
            public string? Port { get; set; }
        }
    }
}
