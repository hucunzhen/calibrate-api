// 阵列行列：工具栏 Meta 为准；焊接/原点等仍只读算子 Params。

using System;
using System.Globalization;
using System.Windows;

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage
    {
        private const string MetaLatticeGridRows = "latticeGridRows";
        private const string MetaLatticeGridCols = "latticeGridCols";

        internal const string LatticeGridMetaHint = "以工具栏「阵列行列」为准；算子 gridRows/gridCols 仅在工具栏未设时作后备";
        internal const string LatticeOriginMetaHint = "格 (0,0) 在图像上的角（默认左上）。固定编号，避免链向翻 180° 时从左上跳到右下。";
        internal const string LatticeWeldOrderMetaHint = "焊接/落格遍历顺序（默认列优先）。与落格原点配合：先定 (0,0)，再按此序走格。";
        internal const string LatticeWeldIntervalMetaHint = "间隔 N：在基础顺序上每隔 N 取一点，共 N 遍。1=不分遍。是否隔遍反向见 weldIntervalReverse。";
        internal const string LatticeWeldIntervalReverseMetaHint = "间隔多遍时：true=第 2、4…遍反向；false=每遍同向。";
        internal const string NumMatchesLatticeHint = "0 或未填 = 工具栏阵列行×列（默认 8×2=16）";

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

        /// <summary>工具栏 Meta &gt; 0 优先；否则节点 param；否则算子默认。</summary>
        private (int rows, int cols) ResolveLatticeGrid(FlowNode node, int defaultRows, int defaultCols)
        {
            int metaRows = GetFlowMetaInt(MetaLatticeGridRows, 0);
            int metaCols = GetFlowMetaInt(MetaLatticeGridCols, 0);
            bool hasRows = TryParseLatticeGridParam(GetLatticeNodeParam(node, "gridRows"), out int pr);
            bool hasCols = TryParseLatticeGridParam(GetLatticeNodeParam(node, "gridCols"), out int pc);

            int rows = metaRows > 0 ? metaRows : (hasRows && pr > 0 ? pr : defaultRows);
            int cols = metaCols > 0 ? metaCols : (hasCols && pc > 0 ? pc : defaultCols);
            if (rows <= 0) rows = defaultRows;
            if (cols <= 0) cols = defaultCols;
            return (rows, cols);
        }

        /// <summary>轨迹等：工具栏 Meta 优先；否则节点（0=从 GridRow/Col 推断）。</summary>
        private (int rows, int cols) ResolveLatticeGridOptional(FlowNode node)
        {
            int metaRows = GetFlowMetaInt(MetaLatticeGridRows, 0);
            int metaCols = GetFlowMetaInt(MetaLatticeGridCols, 0);
            bool hasRows = TryParseLatticeGridParam(GetLatticeNodeParam(node, "gridRows"), out int pr);
            bool hasCols = TryParseLatticeGridParam(GetLatticeNodeParam(node, "gridCols"), out int pc);

            int rows = metaRows > 0 ? metaRows : (hasRows ? pr : 0);
            int cols = metaCols > 0 ? metaCols : (hasCols ? pc : 0);
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

        private LatticeOriginCorner ResolveLatticeOrigin(FlowNode node) =>
            HalconShapeMatchGridFilter.ParseLatticeOriginCorner(GetLatticeNodeParam(node, "originCorner"));

        private LatticeWeldOrder ResolveLatticeWeldOrder(FlowNode node)
        {
            string? raw = GetLatticeNodeParam(node, "weldOrder");
            if (string.IsNullOrWhiteSpace(raw))
                raw = GetLatticeNodeParam(node, "connectOrder");
            return HalconShapeMatchGridFilter.ParseLatticeWeldOrder(raw);
        }

        private string ResolveLatticeWeldOrderString(FlowNode node) =>
            HalconShapeMatchGridFilter.FormatLatticeWeldOrder(ResolveLatticeWeldOrder(node));

        private int ResolveLatticeWeldInterval(FlowNode node)
        {
            string? raw = GetLatticeNodeParam(node, "weldInterval");
            if (!int.TryParse((raw ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
                n = 1;
            return Math.Clamp(n, 1, 64);
        }

        private bool ResolveLatticeWeldIntervalReverse(FlowNode node) =>
            ParseLatticeBool(GetLatticeNodeParam(node, "weldIntervalReverse"), defaultValue: true);

        private static bool ParseLatticeBool(string? raw, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return defaultValue;
            var t = raw.Trim();
            if (string.Equals(t, "true", StringComparison.OrdinalIgnoreCase)
                || t == "1"
                || string.Equals(t, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "on", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(t, "false", StringComparison.OrdinalIgnoreCase)
                || t == "0"
                || string.Equals(t, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "off", StringComparison.OrdinalIgnoreCase))
                return false;
            return defaultValue;
        }

        /// <summary>numMatches：param &gt; 0 优先；否则工具栏行×列。</summary>
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
            if (string.IsNullOrWhiteSpace(LatticeGridRowsBox.Text))
                LatticeGridRowsBox.Text = "8";
            if (string.IsNullOrWhiteSpace(LatticeGridColsBox.Text))
                LatticeGridColsBox.Text = "2";
        }

        private void PersistLatticeConfigFromUi()
        {
            if (LatticeGridRowsBox == null || LatticeGridColsBox == null) return;
            string rowsRaw = LatticeGridRowsBox.Text.Trim();
            string colsRaw = LatticeGridColsBox.Text.Trim();
            if (!int.TryParse(rowsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rows) || rows < 1)
                rows = 8;
            if (!int.TryParse(colsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cols) || cols < 1)
                cols = 2;
            LatticeGridRowsBox.Text = rows.ToString(CultureInfo.InvariantCulture);
            LatticeGridColsBox.Text = cols.ToString(CultureInfo.InvariantCulture);
            SetFlowMeta(MetaLatticeGridRows, LatticeGridRowsBox.Text);
            SetFlowMeta(MetaLatticeGridCols, LatticeGridColsBox.Text);
        }

        private void LatticeGridConfig_LostFocus(object sender, RoutedEventArgs e)
        {
            PersistLatticeConfigFromUi();
            SaveToolbarDefaultsToStore();
        }
    }
}
