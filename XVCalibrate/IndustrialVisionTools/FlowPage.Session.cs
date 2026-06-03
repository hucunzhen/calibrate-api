using System;

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage
    {
        internal void SaveToolbarDefaultsToStore()
        {
            PersistLatticeConfigFromUi();
            PersistStandaloneDebugImageFromUi();

            string rows = GetFlowMeta(MetaLatticeGridRows);
            string cols = GetFlowMeta(MetaLatticeGridCols);
            if (string.IsNullOrWhiteSpace(rows) && LatticeGridRowsBox != null)
                rows = LatticeGridRowsBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(cols) && LatticeGridColsBox != null)
                cols = LatticeGridColsBox.Text.Trim();

            var s = new FlowPageToolbarUiSettings
            {
                LatticeGridRows = string.IsNullOrWhiteSpace(rows) ? "8" : rows,
                LatticeGridCols = string.IsNullOrWhiteSpace(cols) ? "2" : cols,
                StandaloneDebugImagePath = GetFlowMeta(MetaStandaloneDebugImage)
            };
            s.Save();
        }

        internal void ApplyToolbarDefaultsFromStore()
        {
            var s = FlowPageToolbarUiSettings.Load();
            if (string.IsNullOrWhiteSpace(GetFlowMeta(MetaLatticeGridRows))
                && !string.IsNullOrWhiteSpace(s.LatticeGridRows))
                SetFlowMeta(MetaLatticeGridRows, s.LatticeGridRows);
            if (string.IsNullOrWhiteSpace(GetFlowMeta(MetaLatticeGridCols))
                && !string.IsNullOrWhiteSpace(s.LatticeGridCols))
                SetFlowMeta(MetaLatticeGridCols, s.LatticeGridCols);
            if (string.IsNullOrWhiteSpace(GetFlowMeta(MetaStandaloneDebugImage))
                && !string.IsNullOrWhiteSpace(s.StandaloneDebugImagePath))
                SetFlowMeta(MetaStandaloneDebugImage, s.StandaloneDebugImagePath);

            UpdateLatticeConfigUi();
            UpdateStandaloneDebugUi();
        }
    }
}
