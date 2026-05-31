// 流程级 Meta 阵列行列：HALCON 落格/轨迹算子继承 latticeGridRows / latticeGridCols。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage
    {
        private const string MetaLatticeGridRows = "latticeGridRows";
        private const string MetaLatticeGridCols = "latticeGridCols";

        internal const string LatticeGridMetaHint = "留空或 0 时使用流程 Meta（工具栏「阵列行列」）";
        internal const string NumMatchesLatticeHint = "0 或未填 = Meta 阵列行×列（默认 8×2=16）";

        private int GetFlowMetaInt(string key, int fallback)
        {
            if (_flowMeta.TryGetValue(key, out var raw)
                && !string.IsNullOrWhiteSpace(raw)
                && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                && v > 0)
                return v;
            return fallback;
        }

        private static bool TryParseLatticeGridParam(string? raw, out int value)
        {
            value = 0;
            return !string.IsNullOrWhiteSpace(raw)
                   && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static string? GetLatticeNodeParam(FlowNode node, string key) =>
            node.Params.TryGetValue(key, out var v) ? v : null;

        /// <summary>节点 param &gt; 0 优先；否则 Flow Meta；否则算子默认。</summary>
        private (int rows, int cols) ResolveLatticeGrid(FlowNode node, int defaultRows, int defaultCols)
        {
            bool hasRows = TryParseLatticeGridParam(GetLatticeNodeParam(node, "gridRows"), out int pr);
            bool hasCols = TryParseLatticeGridParam(GetLatticeNodeParam(node, "gridCols"), out int pc);

            int rows = hasRows && pr > 0 ? pr : GetFlowMetaInt(MetaLatticeGridRows, defaultRows);
            int cols = hasCols && pc > 0 ? pc : GetFlowMetaInt(MetaLatticeGridCols, defaultCols);
            if (rows <= 0) rows = defaultRows;
            if (cols <= 0) cols = defaultCols;
            return (rows, cols);
        }

        /// <summary>轨迹拼接等：显式 0 表示从 GridRow/Col 推断；未填 param 时继承 Meta（可为 0）。</summary>
        private (int rows, int cols) ResolveLatticeGridOptional(FlowNode node)
        {
            bool hasRows = TryParseLatticeGridParam(GetLatticeNodeParam(node, "gridRows"), out int pr);
            bool hasCols = TryParseLatticeGridParam(GetLatticeNodeParam(node, "gridCols"), out int pc);

            int rows = hasRows ? pr : GetFlowMetaInt(MetaLatticeGridRows, 0);
            int cols = hasCols ? pc : GetFlowMetaInt(MetaLatticeGridCols, 0);
            return (rows, cols);
        }

        private (int rows, int cols) ResolveLatticeGridFromFlow(int defaultRows, int defaultCols)
        {
            int rows = GetFlowMetaInt(MetaLatticeGridRows, defaultRows);
            int cols = GetFlowMetaInt(MetaLatticeGridCols, defaultCols);
            if (rows <= 0) rows = defaultRows;
            if (cols <= 0) cols = defaultCols;
            return (rows, cols);
        }

        /// <summary>numMatches / coarseNumMatches：param &gt; 0 优先；否则 Meta 行×列。</summary>
        private int ResolveNumMatchesFromLattice(
            FlowNode node,
            string paramName,
            int defaultLatticeRows = 8,
            int defaultLatticeCols = 2)
        {
            if (TryParseLatticeGridParam(GetLatticeNodeParam(node, paramName), out int nm) && nm > 0)
                return nm;
            var (rows, cols) = ResolveLatticeGridFromFlow(defaultLatticeRows, defaultLatticeCols);
            return rows * cols;
        }

        private void UpdateLatticeConfigUi()
        {
            if (LatticeGridRowsBox == null || LatticeGridColsBox == null) return;
            LatticeGridRowsBox.Text = GetFlowMeta(MetaLatticeGridRows);
            LatticeGridColsBox.Text = GetFlowMeta(MetaLatticeGridCols);
        }

        private void PersistLatticeConfigFromUi()
        {
            if (LatticeGridRowsBox == null || LatticeGridColsBox == null) return;
            SetFlowMeta(MetaLatticeGridRows, LatticeGridRowsBox.Text.Trim());
            SetFlowMeta(MetaLatticeGridCols, LatticeGridColsBox.Text.Trim());
        }

        private void LatticeGridConfig_LostFocus(object sender, RoutedEventArgs e) =>
            PersistLatticeConfigFromUi();
    }
}
