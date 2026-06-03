// 组合算子子流程：独立调试模式（composite_bind_in/out 在无父组合时注入测试输入）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CalibOperatorPInvoke;
using Microsoft.Win32;

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage : UserControl
    {
        private const string MetaStandaloneDebugImage = "standaloneDebugImagePath";
        private const string MetaStandaloneDebugCalibJson = "standaloneDebugCalibrationJson";

        private readonly Dictionary<string, string> _flowMeta = new(StringComparer.Ordinal);
        private Dictionary<string, object?>? _standaloneCompositeDebugInputs;

        /// <summary>含 composite_bind_in/out 且无顶层 composite 算子时视为可独立调试的子流程。</summary>
        public bool IsCompositeSubflowDocument =>
            _nodes.Any(n => n.Def.TypeId is "composite_bind_in" or "composite_bind_out")
            && !_nodes.Any(n => n.Def.TypeId == "composite");

        public bool IsStandaloneDebugActive => IsCompositeSubflowDocument;

        private void ApplyFlowMetaFromData(FlowData data)
        {
            _flowMeta.Clear();
            if (data.Meta == null) return;
            foreach (var kv in data.Meta)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    _flowMeta[kv.Key.Trim()] = kv.Value ?? "";
            }
        }

        private void MergeFlowMetaInto(FlowData data)
        {
            if (_flowMeta.Count == 0)
            {
                data.Meta = null;
                return;
            }

            data.Meta = new Dictionary<string, string>(_flowMeta, StringComparer.Ordinal);
        }

        private string GetFlowMeta(string key) =>
            _flowMeta.TryGetValue(key, out var v) ? v : "";

        private void SetFlowMeta(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                _flowMeta.Remove(key);
            else
                _flowMeta[key] = value.Trim();
        }

        private void UpdateStandaloneDebugUi()
        {
            if (StandaloneDebugPanel == null) return;
            bool show = IsCompositeSubflowDocument;
            StandaloneDebugPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && StandaloneDebugImageBox != null)
                StandaloneDebugImageBox.Text = GetFlowMeta(MetaStandaloneDebugImage);
        }

        private string? GetStandaloneDebugImagePathConfigured()
        {
            string path = GetFlowMeta(MetaStandaloneDebugImage).Trim();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        private void PersistStandaloneDebugImageFromUi()
        {
            if (StandaloneDebugImageBox == null) return;
            SetFlowMeta(MetaStandaloneDebugImage, StandaloneDebugImageBox.Text.Trim());
        }

        private void StandaloneDebugBrowseImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "子流程调试 — 选择测试图像",
                Filter = "图像文件|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*"
            };
            string? guess = GetStandaloneDebugImagePathConfigured();
            if (!string.IsNullOrWhiteSpace(guess))
            {
                try
                {
                    string resolved = ResolveCompositeFlowPath(guess, null);
                    dlg.InitialDirectory = System.IO.Path.GetDirectoryName(resolved);
                    dlg.FileName = System.IO.Path.GetFileName(resolved);
                }
                catch
                {
                    // ignored
                }
            }

            if (dlg.ShowDialog() != true) return;
            string stored = FormatPathForFlowParam(dlg.FileName);
            SetFlowMeta(MetaStandaloneDebugImage, stored);
            if (StandaloneDebugImageBox != null)
                StandaloneDebugImageBox.Text = stored;
            SaveToolbarDefaultsToStore();
        }

        /// <summary>运行前在 UI 线程准备；返回 null 表示非子流程调试或无需注入。</summary>
        private Dictionary<string, object?>? TryBuildStandaloneCompositeDebugInputs(bool promptIfMissing)
        {
            if (!IsStandaloneDebugActive)
                return null;

            PersistStandaloneDebugImageFromUi();
            string? pathParam = GetStandaloneDebugImagePathConfigured();
            if (string.IsNullOrWhiteSpace(pathParam))
            {
                if (!promptIfMissing)
                    throw new InvalidOperationException(
                        "子流程独立调试: 请在工具栏填写「调试图像」路径，或点击 … 选择文件。");

                var dlg = new OpenFileDialog
                {
                    Title = "子流程独立调试 — 选择测试图像",
                    Filter = "图像文件|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*"
                };
                if (dlg.ShowDialog() != true)
                    throw new FlowExecutionGracefulStopException("子流程独立调试: 未选择测试图像。");

                pathParam = FormatPathForFlowParam(dlg.FileName);
                SetFlowMeta(MetaStandaloneDebugImage, pathParam);
                if (StandaloneDebugImageBox != null)
                    StandaloneDebugImageBox.Text = pathParam;
            }

            string resolvedPath = ResolveCompositeFlowPath(pathParam, null);
            if (!System.IO.File.Exists(resolvedPath))
                throw new System.IO.FileNotFoundException($"子流程独立调试: 测试图像不存在: {resolvedPath}");

            CalibImage image = CalibAPI.LoadImage(resolvedPath);
            var externalPorts = _nodes
                .Where(n => n.Def.TypeId == "composite_bind_in")
                .Select(n => n.Params.GetValueOrDefault("externalPort", "In")?.Trim() ?? "In")
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (externalPorts.Count == 0)
                externalPorts.Add("In");

            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (string ext in externalPorts)
            {
                if (!dict.ContainsKey(ext))
                    dict[ext] = image;
            }

            if (!dict.ContainsKey("In"))
                dict["In"] = image;
            dict["Image"] = image;
            dict["Img"] = image;

            AppendLog($"[子流程调试] 已注入测试图: {System.IO.Path.GetFileName(resolvedPath)} → 父端口 [{string.Join(", ", externalPorts)}]");
            return dict;
        }

        private void BeginStandaloneDebugRunScope()
        {
            _standaloneCompositeDebugInputs = null;
            if (!IsStandaloneDebugActive) return;
            _standaloneCompositeDebugInputs = TryBuildStandaloneCompositeDebugInputs(promptIfMissing: true);
        }

        private void EndStandaloneDebugRunScope()
        {
            _standaloneCompositeDebugInputs = null;
        }

        private Dictionary<string, object?>? GetCompositeBindInInputsForExecute(
            Dictionary<string, object?>? compositeExternalInputsForBindIn) =>
            compositeExternalInputsForBindIn ?? _standaloneCompositeDebugInputs;

        /// <summary>从组合算子在新标签打开子流程文件。</summary>
        private void OpenCompositeInnerFlowInNewTab(FlowNode compositeNode)
        {
            string path = compositeNode.Params.GetValueOrDefault("innerFlowPath", "")?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show("请先在组合算子中设置「子流程文件」(innerFlowPath)。", "打开子流程",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string resolved = ResolveCompositeFlowPath(path, null);
            if (!System.IO.File.Exists(resolved))
            {
                MessageBox.Show($"子流程文件不存在:\n{resolved}", "打开子流程",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (TryLoadFlowInNewTab?.Invoke(resolved) == true)
            {
                StatusText.Text = $"已在新标签打开子流程: {System.IO.Path.GetFileName(resolved)}";
                return;
            }

            LoadFlowFromFile(resolved, showErrorDialog: true);
        }

        private static string DescribeFlowValueBrief(object? value)
        {
            if (value == null) return "null";
            if (value is CalibImage img)
                return $"CalibImage {img.Width}x{img.Height}";
            if (value is Array arr)
                return $"{arr.GetType().GetElementType()?.Name ?? "Array"}[{arr.Length}]";
            string s = value.ToString() ?? "";
            return s.Length > 48 ? s[..45] + "…" : s;
        }
    }
}
