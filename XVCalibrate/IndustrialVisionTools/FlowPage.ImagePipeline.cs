using System;
using System.Collections.Generic;
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

        /// <summary>从上游节点输出中取值；若首选端口为空则尝试 Out/Image/In。</summary>
        private static object? GetUpstreamOutputValue(FlowNode fromNode, string preferredPortName)
        {
            if (fromNode.Outputs.TryGetValue(preferredPortName, out var direct) && direct != null)
                return direct;

            foreach (string alt in new[] { "Out", "Image", "In", "Img" })
            {
                if (string.Equals(alt, preferredPortName, StringComparison.Ordinal))
                    continue;
                if (fromNode.Outputs.TryGetValue(alt, out var o) && o is CalibImage)
                    return o;
            }

            return fromNode.Outputs.GetValueOrDefault(preferredPortName);
        }

        /// <summary>按常见端口名解析 CalibImage 输入（Image / In / Out / Img）。</summary>
        private static CalibImage? TryResolveCalibImageInput(IReadOnlyDictionary<string, object?>? inputs)
        {
            if (inputs == null || inputs.Count == 0)
                return null;

            foreach (string key in new[] { "Image", "In", "Out", "Img" })
            {
                if (inputs.TryGetValue(key, out var obj) && obj is CalibImage img)
                    return img;
            }

            foreach (var kv in inputs)
            {
                if (kv.Value is CalibImage img)
                    return img;
            }

            return null;
        }
    }
}
