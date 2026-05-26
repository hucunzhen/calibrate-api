// 组合算子：运行后双击查看子流程内各节点端口变量。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CalibOperatorPInvoke;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage
    {
        public sealed class CompositeInnerNodeSnapshot
        {
            public Guid NodeId { get; init; }
            public string DisplayName { get; init; } = "";
            public string TypeId { get; init; } = "";
            public bool Executed { get; init; }
            public string? ErrorMessage { get; init; }
            public string? ResultSummary { get; init; }
            public Dictionary<string, object?> Outputs { get; init; } = new();
        }

        public sealed class CompositeRunSnapshot
        {
            public DateTime CapturedAt { get; init; }
            public string? InnerFlowLabel { get; init; }
            public Dictionary<string, object?> ParentInputs { get; init; } = new();
            public List<CompositeInnerNodeSnapshot> InnerNodes { get; init; } = new();
        }

        private static CompositeRunSnapshot CaptureCompositeRunSnapshot(
            IReadOnlyList<FlowNode> sortedInner,
            Dictionary<string, object?> compositeInputs,
            string? innerFlowLabel)
        {
            return new CompositeRunSnapshot
            {
                CapturedAt = DateTime.Now,
                InnerFlowLabel = innerFlowLabel,
                ParentInputs = new Dictionary<string, object?>(compositeInputs),
                InnerNodes = sortedInner.Select(inner => new CompositeInnerNodeSnapshot
                {
                    NodeId = inner.Id,
                    DisplayName = inner.Def.DisplayName,
                    TypeId = inner.Def.TypeId,
                    Executed = inner.Executed,
                    ErrorMessage = inner.ErrorMessage,
                    ResultSummary = inner.ResultSummary,
                    Outputs = new Dictionary<string, object?>(inner.Outputs)
                }).ToList()
            };
        }

        private static readonly string[] CompositeContourPipelineTypeIds =
        {
            "halcon_find_shape_model",
            "halcon_mask_image_by_shape_match",
            "halcon_chain_strip_bootstrap",
            "halcon_chain_strip_pick_uv_grid",
            "halcon_shape_match_grid_to_trajectory",
            "polyline_simplify_dp",
            "plane_to_base_handeye"
        };

        private void LogCompositeContourPipelineDigest(FlowNode compositeNode)
        {
            var snap = compositeNode.LastCompositeRun;
            if (snap == null || snap.InnerNodes.Count == 0)
                return;

            var parts = new List<string>();
            int findIdx = 0;
            foreach (var inner in snap.InnerNodes)
            {
                if (!CompositeContourPipelineTypeIds.Contains(inner.TypeId, StringComparer.Ordinal))
                    continue;

                string label = inner.DisplayName;
                string summary = inner.ResultSummary ?? "";
                switch (inner.TypeId)
                {
                    case "halcon_find_shape_model":
                        findIdx++;
                        label = findIdx == 1 ? "外形找形" : "内找形";
                        break;
                    case "halcon_mask_image_by_shape_match":
                        label = "Mask";
                        break;
                    case "halcon_chain_strip_pick_uv_grid":
                        label = "u/v落格";
                        break;
                    case "halcon_shape_match_grid_to_trajectory":
                        label = "轨迹";
                        break;
                    case "polyline_simplify_dp":
                        label = "简化";
                        break;
                    case "plane_to_base_handeye":
                        label = "手眼";
                        break;
                }

                string extra = DescribeCompositePipelineOutputExtra(inner);
                string line = string.IsNullOrEmpty(summary)
                    ? $"{label}{extra}"
                    : $"{label}={summary}{extra}";
                parts.Add(line);
            }

            if (parts.Count == 0)
                return;

            string tag = string.IsNullOrWhiteSpace(snap.InnerFlowLabel)
                ? compositeNode.Def.DisplayName
                : snap.InnerFlowLabel;
            AppendLog($"[composite/{tag}] " + string.Join(" | ", parts));
        }

        private static string DescribeCompositePipelineOutputExtra(CompositeInnerNodeSnapshot inner)
        {
            switch (inner.TypeId)
            {
                case "halcon_find_shape_model":
                    if (inner.Outputs.TryGetValue("Row", out var r) && r is double[] ra)
                        return $" (Row={ra.Length})";
                    break;
                case "halcon_chain_strip_pick_uv_grid":
                    if (inner.Outputs.TryGetValue("Row", out var pr) && pr is double[] pra)
                        return $" (Row={pra.Length})";
                    break;
                case "halcon_shape_match_grid_to_trajectory":
                    if (inner.Outputs.TryGetValue("GroupBarIds", out var gb) && gb is int[] gba && gba.Length > 0)
                        return $" (GroupBarId种类={gba.Distinct().Count()}, 点={gba.Length})";
                    break;
                case "polyline_simplify_dp":
                    if (inner.Outputs.TryGetValue("OutBarIds", out var ob) && ob is int[] oba && oba.Length > 0)
                        return $" (OutBarId种类={oba.Distinct().Count()}, 点={oba.Length})";
                    break;
                case "plane_to_base_handeye":
                    if (inner.Outputs.TryGetValue("BarIds", out var bb) && bb is int[] bba && bba.Length > 0)
                        return $" (BarId种类={bba.Distinct().Count()}, 点={bba.Length})";
                    break;
            }

            return "";
        }

        private void AppendCompositePipelineHintsForBatchMismatch(
            int expectedBatches,
            int actualBatches,
            Dictionary<string, object?> sendPlcInputs)
        {
            if (sendPlcInputs.TryGetValue("BarIds", out var barObj) && barObj is int[] barIds && barIds.Length > 0)
            {
                int uniq = barIds.Distinct().Count();
                AppendLog(
                    $"[send_plc] 输入 BarIds: 点数={barIds.Length}, 逐点种类={uniq}, 连续分段批数={actualBatches}");
                if (uniq != actualBatches)
                    AppendLog(
                        $"[send_plc] 异常: BarId 种类={uniq} 与批次数={actualBatches} 不一致（应相等）。");
                else if (uniq < expectedBatches)
                    AppendLog(
                        $"[send_plc] 根因推断: 到达 send_plc 的焊道条号仅 {uniq} 种（批次数=条号种类数），与期望 {expectedBatches} 不符。"
                        + " 复合内「找形」个数≠批次数；请查 u/v落格 KeptCount、轨迹 GroupBarIds 种类、Out2 是否接 OutBarIds。");
            }

            var composite = _nodes
                .Where(n => n.Def.TypeId == "composite" && n.LastCompositeRun != null)
                .OrderByDescending(n => n.LastCompositeRun!.CapturedAt)
                .FirstOrDefault();
            if (composite == null)
            {
                AppendLog("[send_plc] 未找到已运行的组合算子快照；请先运行含 genCountorMod 的复合节点。");
                return;
            }

            LogCompositeContourPipelineDigest(composite);
            AppendLog("[send_plc] 可双击组合算子打开「子流程变量」核对各步 Row/GroupBarIds 长度。");
        }

        private void ShowCompositeVariablesWindow(FlowNode compositeNode)
        {
            var snap = compositeNode.LastCompositeRun;
            string sub = FormatCompositeNodeSubtitleStatic(compositeNode);
            string title = string.IsNullOrWhiteSpace(sub)
                ? $"{compositeNode.Def.DisplayName} · 子流程变量"
                : $"{compositeNode.Def.DisplayName} · {sub}";

            var owner = Window.GetWindow(this);
            var win = new Window
            {
                Title = title,
                Width = 920,
                Height = 560,
                MinWidth = 720,
                MinHeight = 400,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26))
            };
            if (owner != null)
                win.Owner = owner;

            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var hint = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            if (snap == null)
                hint.Text = "尚无子流程变量快照。请先点工具栏「运行」(托管) 或「Native」(会自动补跑组合子图)；"
                    + "也可右键「查看子流程变量」。双击标题/正文区域均可打开本窗口。";
            else
                hint.Text = $"快照时间: {snap.CapturedAt:yyyy-MM-dd HH:mm:ss}  |  子节点 {snap.InnerNodes.Count} 个"
                    + (string.IsNullOrWhiteSpace(snap.InnerFlowLabel) ? "" : $"  |  {snap.InnerFlowLabel}");
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var nodeList = new ListBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
                DisplayMemberPath = "Label"
            };
            Grid.SetColumn(nodeList, 0);

            var portList = new ListBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
                DisplayMemberPath = "PortName"
            };
            Grid.SetColumn(portList, 1);

            var detail = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(detail, 2);

            body.Children.Add(nodeList);
            body.Children.Add(portList);
            body.Children.Add(detail);
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var btnPreview = new Button { Content = "预览选中端口", Width = 120, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
            var btnClose = new Button { Content = "关闭", Width = 72, Height = 28, IsDefault = true };
            btnClose.Click += (_, _) => win.Close();
            btnRow.Children.Add(btnPreview);
            btnRow.Children.Add(btnClose);
            Grid.SetRow(btnRow, 2);
            root.Children.Add(btnRow);

            win.Content = root;

            var parentSection = new CompositeVarListItem
            {
                Label = "【父级输入】",
                IsParentInputs = true,
                ParentInputs = snap?.ParentInputs ?? new Dictionary<string, object?>()
            };
            var parentOutSection = new CompositeVarListItem
            {
                Label = "【组合输出】",
                IsParentOutputs = true,
                ParentOutputs = new Dictionary<string, object?>(compositeNode.Outputs)
            };

            var items = new List<CompositeVarListItem> { parentSection, parentOutSection };
            if (snap != null)
            {
                foreach (var inner in snap.InnerNodes)
                {
                    items.Add(new CompositeVarListItem
                    {
                        Label = inner.Executed
                            ? (string.IsNullOrWhiteSpace(inner.ErrorMessage) ? inner.DisplayName : $"{inner.DisplayName} !")
                            : $"{inner.DisplayName} (未执行)",
                        Inner = inner
                    });
                }
            }

            nodeList.ItemsSource = items;
            if (items.Count > 2)
                nodeList.SelectedIndex = 2;
            else if (items.Count > 0)
                nodeList.SelectedIndex = 0;

            void RefreshPorts()
            {
                portList.ItemsSource = null;
                detail.Text = "";
                btnPreview.IsEnabled = false;
                if (nodeList.SelectedItem is not CompositeVarListItem sel)
                    return;

                if (sel.IsParentInputs)
                {
                    var ports = sel.ParentInputs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                    portList.ItemsSource = ports.Select(p => new CompositeVarPortItem { PortName = p, Data = sel.ParentInputs[p] }).ToList();
                    detail.Text = BuildCompositeParentInputsText(sel.ParentInputs);
                    return;
                }

                if (sel.IsParentOutputs)
                {
                    var ports = sel.ParentOutputs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                    portList.ItemsSource = ports.Select(p => new CompositeVarPortItem { PortName = p, Data = sel.ParentOutputs[p] }).ToList();
                    detail.Text = BuildCompositeParentOutputsText(compositeNode, sel.ParentOutputs);
                    return;
                }

                if (sel.Inner == null)
                    return;

                var inner = sel.Inner;
                var sbHead = new StringBuilder();
                sbHead.AppendLine($"节点: {inner.DisplayName} ({inner.TypeId})");
                sbHead.AppendLine($"Id: {inner.NodeId}");
                sbHead.AppendLine($"已执行: {inner.Executed}");
                if (!string.IsNullOrWhiteSpace(inner.ErrorMessage))
                    sbHead.AppendLine($"错误: {inner.ErrorMessage}");
                if (!string.IsNullOrWhiteSpace(inner.ResultSummary))
                    sbHead.AppendLine($"摘要: {inner.ResultSummary}");
                sbHead.AppendLine();
                detail.Text = sbHead.ToString();

                var portItems = inner.Outputs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new CompositeVarPortItem { PortName = p, Data = inner.Outputs[p], Inner = inner })
                    .ToList();
                portList.ItemsSource = portItems;
                if (portItems.Count > 0)
                    portList.SelectedIndex = 0;
            }

            void RefreshDetail()
            {
                if (nodeList.SelectedItem is not CompositeVarListItem sel)
                    return;

                if (portList.SelectedItem is CompositeVarPortItem port)
                {
                    var sb = new StringBuilder();
                    if (sel.Inner != null)
                    {
                        sb.AppendLine($"节点: {sel.Inner.DisplayName}");
                        sb.AppendLine($"端口: {port.PortName}");
                    }
                    else if (sel.IsParentInputs)
                        sb.AppendLine($"父组合输入: {port.PortName}");
                    else if (sel.IsParentOutputs)
                        sb.AppendLine($"组合输出: {port.PortName}");
                    sb.AppendLine($"类型: {port.Data?.GetType().Name ?? "(null)"}");
                    sb.AppendLine();
                    sb.Append(FormatFlowPortValue(port.Data));
                    detail.Text = sb.ToString();
                    btnPreview.IsEnabled = port.Data != null && CanPreviewFlowPortValue(port.Data);
                    return;
                }

                RefreshPorts();
            }

            nodeList.SelectionChanged += (_, _) => RefreshPorts();
            portList.SelectionChanged += (_, _) => RefreshDetail();

            btnPreview.Click += (_, _) =>
            {
                if (portList.SelectedItem is not CompositeVarPortItem port || port.Data == null)
                    return;
                string nodeName = port.Inner?.DisplayName ?? compositeNode.Def.DisplayName;
                PreviewFlowPortValue(port.Data, nodeName, port.PortName, port.Inner);
            };

            win.ShowDialog();
        }

        private sealed class CompositeVarListItem
        {
            public string Label { get; init; } = "";
            public CompositeInnerNodeSnapshot? Inner { get; init; }
            public bool IsParentInputs { get; init; }
            public bool IsParentOutputs { get; init; }
            public Dictionary<string, object?> ParentInputs { get; init; } = new();
            public Dictionary<string, object?> ParentOutputs { get; init; } = new();
        }

        private sealed class CompositeVarPortItem
        {
            public string PortName { get; init; } = "";
            public object? Data { get; init; }
            public CompositeInnerNodeSnapshot? Inner { get; init; }
        }

        private static string BuildCompositeParentInputsText(Dictionary<string, object?> inputs)
        {
            var sb = new StringBuilder();
            sb.AppendLine("父组合算子传入子流程的输入端口:");
            sb.AppendLine();
            foreach (var kv in inputs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"--- {kv.Key} ---");
                sb.AppendLine($"类型: {kv.Value?.GetType().Name ?? "(null)"}");
                sb.Append(FormatFlowPortValue(kv.Value));
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private static string BuildCompositeParentOutputsText(FlowNode compositeNode, Dictionary<string, object?> outputs)
        {
            var sb = new StringBuilder();
            sb.AppendLine("组合算子对外输出（映射自子节点）:");
            sb.AppendLine();
            foreach (var kv in outputs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"--- {kv.Key} ---");
                sb.AppendLine($"类型: {kv.Value?.GetType().Name ?? "(null)"}");
                sb.Append(FormatFlowPortValue(kv.Value));
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(compositeNode.ResultSummary))
                sb.AppendLine($"摘要: {compositeNode.ResultSummary}");
            return sb.ToString().TrimEnd();
        }

        private static bool CanPreviewFlowPortValue(object data) =>
            data is CalibImage or Point2D[] or CalibPoint3D[];

        private void PreviewFlowPortValue(object data, string nodeName, string portName, CompositeInnerNodeSnapshot? inner)
        {
            if (data is CalibImage img)
            {
                ShowImagePreview(img);
                return;
            }

            if (data is Point2D[] pts)
            {
                CalibImage? baseImg = null;
                int[]? barSnap = null;
                string? join = null;
                if (inner != null)
                {
                    baseImg = inner.Outputs.Values.OfType<CalibImage>().FirstOrDefault();
                    var barIds = TryGetBarIdsForPointPortFromOutputs(inner.Outputs, inner.TypeId, portName, pts.Length);
                    barSnap = barIds == null ? null : (int[])barIds.Clone();
                    join = DefaultPointLineJoinModeForPreview(inner.TypeId, portName);
                }

                if (baseImg != null)
                    ShowImagePreview(baseImg, pts, 3, null, null, null, barSnap, join, null);
                else
                    ShowPointsPolylinePreview(pts, $"{nodeName} · {portName}", barSnap, join);
                return;
            }

            if (data is CalibPoint3D[] pts3)
            {
                CalibImage? baseImg = null;
                int[]? barSnap = null;
                string? join = null;
                if (inner != null)
                {
                    baseImg = inner.Outputs.Values.OfType<CalibImage>().FirstOrDefault();
                    var barIds = TryGetBarIdsForPointPortFromOutputs(inner.Outputs, inner.TypeId, portName, pts3.Length);
                    barSnap = barIds == null ? null : (int[])barIds.Clone();
                    join = DefaultPointLineJoinModeForPreview(inner.TypeId, portName);
                }

                var xy = pts3.Select(p => new Point2D(p.X, p.Y)).ToArray();
                if (baseImg != null)
                    ShowImagePreview(baseImg, xy, 3, null, null, null, barSnap, join, null);
                else
                    ShowPointsPolylinePreview(xy, $"{nodeName} · {portName} (XY)", barSnap, join);
            }
        }

        private static int[]? TryGetBarIdsForPointPortFromOutputs(
            Dictionary<string, object?> outputs,
            string typeId,
            string portName,
            int pointCount)
        {
            foreach (var key in new[] { "BarIds", "GroupBarIds", "barIds" })
            {
                if (outputs.TryGetValue(key, out var v) && v is int[] bars && bars.Length == pointCount)
                    return bars;
            }
            return null;
        }

        private static string FormatFlowPortValue(object? data)
        {
            if (data == null)
                return "(null)";

            var sb = new StringBuilder();
            switch (data)
            {
                case CalibImage img:
                    sb.AppendLine($"图像: {img.Width}×{img.Height}, 通道={img.Channels}");
                    break;
                case int n:
                    sb.AppendLine($"值: {n}");
                    break;
                case double d:
                    sb.AppendLine($"值: {d:G9}");
                    break;
                case string s:
                    sb.AppendLine(s.Length > 2000 ? s[..2000] + "…" : s);
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
                    break;
                case ValueTuple<double[], double[], int[], int> cw:
                    sb.AppendLine($"ContoursWorld (条数): {cw.Item4}");
                    sb.AppendLine($"Total Points: {cw.Item1?.Length ?? 0}");
                    break;
                case ValueTuple<double[], double[], double[], int[], int> c3:
                    sb.AppendLine($"ContoursBase3D (条数): {c3.Item5}");
                    sb.AppendLine($"Total Points: {c3.Item1?.Length ?? 0}");
                    break;
                case Point2D[] pp:
                    sb.AppendLine($"点数: {pp.Length}");
                    if (pp.Length > 0)
                    {
                        sb.AppendLine($"首点: ({pp[0].X:F4}, {pp[0].Y:F4})");
                        if (pp.Length > 1)
                            sb.AppendLine($"末点: ({pp[^1].X:F4}, {pp[^1].Y:F4})");
                    }
                    break;
                case CalibPoint3D[] p3:
                    sb.AppendLine($"点数(基座3D): {p3.Length}");
                    if (p3.Length > 0)
                    {
                        sb.AppendLine($"首点: ({p3[0].X:F4}, {p3[0].Y:F4}, {p3[0].Z:F4})");
                        if (p3.Length > 1)
                            sb.AppendLine($"末点: ({p3[^1].X:F4}, {p3[^1].Y:F4}, {p3[^1].Z:F4})");
                    }
                    break;
                default:
                    var text = data.ToString() ?? "(无可显示内容)";
                    sb.AppendLine(text.Length > 4000 ? text[..4000] + "…" : text);
                    break;
            }

            return sb.ToString().TrimEnd();
        }
    }
}
