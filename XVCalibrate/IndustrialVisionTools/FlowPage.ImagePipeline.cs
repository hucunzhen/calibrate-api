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

        /// <summary>
        /// 微调九点标定：从 Transform 上游节点推断 ImagePts / 世界点 / 预览图（无需额外连线）。
        /// </summary>
        private (Point2D[]? WorldPts, Point2D[]? ImagePts, CalibImage? Image) TryInferAffineAdjustContext(
            FlowNode adjustNode,
            string? flowBaseDir)
        {
            var transformPort = adjustNode.PortVisuals.FirstOrDefault(pv =>
                pv.Definition.Direction == PortDirection.Input &&
                string.Equals(pv.Definition.Name, "Transform", StringComparison.Ordinal));
            if (transformPort == null)
                return (null, null, null);

            var conn = _connections.FirstOrDefault(c => c.ToPort == transformPort);
            if (conn == null)
                return (null, null, null);

            var src = conn.FromPort.Owner;
            Point2D[]? imagePts = null;
            if (src.Outputs.TryGetValue("ImagePts", out var ipObj) && ipObj is Point2D[] ip && ip.Length > 0)
                imagePts = ip;

            Point2D[]? worldPts = null;
            CalibImage? image = null;
            if (string.Equals(src.Def.TypeId, "calibrate", StringComparison.Ordinal))
            {
                var srcInputs = GetNodeInputs(src);
                worldPts = ResolveCalibrateWorldPointsForNode(src, srcInputs, flowBaseDir);
                if (src.Outputs.TryGetValue("Image", out var imgObj) && imgObj is CalibImage ci)
                    image = ci;
                if (image == null)
                    image = GetInputData(src, "Image") as CalibImage;
            }
            else
            {
                if (src.Outputs.TryGetValue("WorldPts", out var wpObj) && wpObj is Point2D[] wp && wp.Length > 0)
                    worldPts = wp;
            }

            return (worldPts, imagePts, image);
        }
    }
}
