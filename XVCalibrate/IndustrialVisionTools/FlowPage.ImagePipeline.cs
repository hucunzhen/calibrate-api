using System;
using System.Collections.Generic;
using System.Linq;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage
    {
        /// <summary>预处理算子统一写出 Out/Image，便于下游 save_image、display 等按任一图像端口取数。</summary>
        private static void PublishCalibImageOutputs(FlowNode node, CalibImage image)
        {
            node.Outputs["Out"] = image;
            node.Outputs["Image"] = image;
        }

        /// <summary>严格按连线的上游输出端口名取值，不做 Out/Image/In 互换（避免 save 到错图）。</summary>
        private static object? GetStrictUpstreamOutputValue(FlowNode fromNode, string fromPortName)
        {
            if (fromNode.Outputs.TryGetValue(fromPortName, out var direct))
                return direct;
            return null;
        }

        /// <summary>
        /// 仅在算子自身输入字典内按 In/Image 别名解析（如 image_rotate 的 In 与 image_flip）。
        /// 不扫描 Out/Img，也不回退到其它上游端口。
        /// </summary>
        private static CalibImage? TryResolveCalibImageInput(IReadOnlyDictionary<string, object?>? inputs)
        {
            if (inputs == null || inputs.Count == 0)
                return null;

            foreach (string key in new[] { "Image", "In" })
            {
                if (inputs.TryGetValue(key, out var obj) && obj is CalibImage img)
                    return img;
            }

            return null;
        }

        /// <summary>save_image：只接受 Image（或旧版 In）端口的连线值。</summary>
        private CalibImage? ResolveSaveImageInput(FlowNode node, IReadOnlyDictionary<string, object?> inputs)
        {
            if (inputs.TryGetValue("Image", out var imgObj) && imgObj is CalibImage img)
                return img;
            if (inputs.TryGetValue("In", out imgObj) && imgObj is CalibImage imgIn)
                return imgIn;

            var wired = GetInputData(node, "Image") as CalibImage;
            if (wired != null)
                return wired;
            return GetInputData(node, "In") as CalibImage;
        }

        private string? DescribeWiredInputSource(FlowNode node, string portName)
        {
            var inPort = node.PortVisuals.FirstOrDefault(pv =>
                pv.Definition.Direction == PortDirection.Input &&
                string.Equals(pv.Definition.Name, portName, StringComparison.Ordinal));
            if (inPort == null)
                return null;

            var conn = _connections.FirstOrDefault(c => c.ToPort == inPort);
            if (conn == null)
                return null;

            var from = conn.FromPort.Owner;
            return $"{from.Def.DisplayName}.{conn.FromPort.Definition.Name}";
        }

        /// <summary>
        /// 保存 CalibImage，行 0 = 图像顶部（与流程预览 ToBitmap 一致）。
        /// GDI+ 写 BMP/PNG/JPEG 避免原生 SaveBMP bottom-up 导致上下颠倒。
        /// </summary>
        private static bool SaveCalibImageToFile(CalibImage image, string path) => image.Save(path);
    }
}
