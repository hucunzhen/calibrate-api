using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 从产线部署指南所列正式流程中收集已使用的算子 TypeId，供工具箱「扩展」分组。
    /// </summary>
    internal static class FormalFlowOperatorCatalog
    {
        public const string ExtendedToolboxCategory = "扩展";

        /// <summary>相对 flows 根目录的正式流程入口；composite.innerFlowPath 会递归展开。</summary>
        private static readonly string[] FormalEntryFlowRelativePaths =
        {
            "chessboard/chessboard_intrinsics_from_dir.flow.json",
            "halcon/caliSendContour.flow.json",
            "halcon/caliNinePoint.flow.json",
            "halcon/main.flow.json",
            "halcon/findCircle.flow.json",
            "halcon/grayPreprocess.flow.json",
            "halcon/binPreprocess.flow.json",
            "halcon/contourPreprocess.flow.json",
            "halcon/genMask.flow.json",
            "v4/main.flow.json",
            "v4/calibSendContour.flow.json",
            "v4/caliNinePoint.flow.json",
            "v4/chessboard_intrinsics_from_dir.flow.json",
            "v4/halcon_coarse_shape_match.flow.json",
            "v4/halcon_fine_shape_match.flow.json",
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static HashSet<string>? _cachedUsedTypeIds;

        public static IReadOnlySet<string> GetUsedTypeIds()
        {
            if (_cachedUsedTypeIds != null)
                return _cachedUsedTypeIds;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? root = FlowRecipeCatalog.TryFindFlowsRootDirectory();
            if (root != null)
            {
                var visitedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string rel in FormalEntryFlowRelativePaths)
                {
                    string path = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
                    ScanFlowFile(path, visitedFiles, set);
                }
            }

            _cachedUsedTypeIds = set;
            return set;
        }

        public static void InvalidateCache() => _cachedUsedTypeIds = null;

        private static void ScanFlowFile(string flowPath, HashSet<string> visitedFiles, HashSet<string> typeIds)
        {
            if (string.IsNullOrWhiteSpace(flowPath) || !File.Exists(flowPath))
                return;

            string full = Path.GetFullPath(flowPath);
            if (!visitedFiles.Add(full))
                return;

            FlowScanDocument? doc;
            try
            {
                doc = JsonSerializer.Deserialize<FlowScanDocument>(File.ReadAllText(full), JsonOptions);
            }
            catch
            {
                return;
            }

            if (doc?.Nodes == null)
                return;

            string flowDir = Path.GetDirectoryName(full) ?? "";
            CollectNodes(doc.Nodes, flowDir, visitedFiles, typeIds);
        }

        private static void CollectNodes(
            List<FlowScanNode> nodes,
            string relativeBaseDir,
            HashSet<string> visitedFiles,
            HashSet<string> typeIds)
        {
            foreach (var node in nodes)
            {
                if (!string.IsNullOrWhiteSpace(node.TypeId))
                    typeIds.Add(node.TypeId.Trim());

                if (!string.Equals(node.TypeId, "composite", StringComparison.OrdinalIgnoreCase))
                    continue;

                var p = node.Params;
                if (p == null)
                    continue;

                if (p.TryGetValue("innerFlowPath", out var innerPath) && !string.IsNullOrWhiteSpace(innerPath))
                {
                    string resolved = ResolveFlowPath(innerPath.Trim(), relativeBaseDir);
                    ScanFlowFile(resolved, visitedFiles, typeIds);
                }

                if (p.TryGetValue("innerFlowJson", out var innerJson) && !string.IsNullOrWhiteSpace(innerJson))
                    ScanInlineFlowJson(innerJson, relativeBaseDir, visitedFiles, typeIds);
            }
        }

        private static void ScanInlineFlowJson(
            string innerJson,
            string relativeBaseDir,
            HashSet<string> visitedFiles,
            HashSet<string> typeIds)
        {
            FlowScanDocument? doc;
            try
            {
                doc = JsonSerializer.Deserialize<FlowScanDocument>(innerJson, JsonOptions);
            }
            catch
            {
                return;
            }

            if (doc?.Nodes == null)
                return;

            CollectNodes(doc.Nodes, relativeBaseDir, visitedFiles, typeIds);
        }

        private static string ResolveFlowPath(string path, string relativeBaseDirectory)
        {
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            if (!string.IsNullOrEmpty(relativeBaseDirectory))
                return Path.GetFullPath(Path.Combine(relativeBaseDirectory, path));
            return Path.GetFullPath(path);
        }

        private sealed class FlowScanDocument
        {
            public List<FlowScanNode>? Nodes { get; set; }
        }

        private sealed class FlowScanNode
        {
            public string? TypeId { get; set; }
            public Dictionary<string, string>? Params { get; set; }
        }
    }
}
