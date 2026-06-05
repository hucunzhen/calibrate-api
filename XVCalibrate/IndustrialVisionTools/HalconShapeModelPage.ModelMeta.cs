#if HALCON_ENABLED
using System;
using System.Windows.Controls;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    public partial class HalconShapeModelPage
    {
        private HalconShapeModelMetaDocument? _lastModelCreateMeta;

        private HalconShapeModelMetaDocument CaptureModelCreateMeta(
            HalconShapeModelCreateOptions opt,
            double minGray,
            double maxGray,
            HalconXldContourBundle? xldBundle,
            string? resolvedMetric)
        {
            return new HalconShapeModelMetaDocument
            {
                CreatedUtc = DateTime.UtcNow,
                Epoch = _modelEpoch,
                MinGray = minGray,
                MaxGray = maxGray,
                ResolvedMetric = resolvedMetric,
                CreateOptions = CloneCreateOptions(opt)
            };
        }

        private static HalconShapeModelCreateOptions CloneCreateOptions(HalconShapeModelCreateOptions src) =>
            new()
            {
                ModelKind = src.ModelKind,
                SourceKind = src.SourceKind,
                NumLevels = src.NumLevels,
                AngleStartDeg = src.AngleStartDeg,
                AngleExtentDeg = src.AngleExtentDeg,
                AngleStepDeg = src.AngleStepDeg,
                Optimization = src.Optimization,
                Metric = src.Metric,
                Contrast = src.Contrast,
                MinContrast = src.MinContrast,
                ScaleMin = src.ScaleMin,
                ScaleMax = src.ScaleMax,
                ScaleStep = src.ScaleStep,
                DeformableSubtype = src.DeformableSubtype,
                GenContourMode = src.GenContourMode,
                MinContourPoints = src.MinContourPoints,
                LargestContourOnly = src.LargestContourOnly,
                EdgeAlpha = src.EdgeAlpha,
                EdgeLow = src.EdgeLow,
                EdgeHigh = src.EdgeHigh
            };

        private string BuildDefaultExportFileName()
        {
            HalconShapeModelCreateOptions opt = _lastModelCreateMeta?.CreateOptions ?? ReadCreateOptionsFromUi();
            double minGray = _lastModelCreateMeta?.MinGray
                             ?? (double.TryParse(TxtMinGray.Text, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out double mg) ? mg : 0);
            double maxGray = _lastModelCreateMeta?.MaxGray
                             ?? (double.TryParse(TxtMaxGray.Text, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out double xg) ? xg : 255);
            return HalconShapeModelFileNameCodec.BuildFileName(
                opt,
                _modelKind,
                minGray,
                maxGray,
                _lastModelCreateMeta?.ResolvedMetric);
        }

        private void ApplyParsedFileName(HalconShapeModelParsedFileName parsed)
        {
            ApplyCreateOptionsToUi(parsed.CreateOptions);
            SetText(TxtMinGray, parsed.MinGray.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SetText(TxtMaxGray, parsed.MaxGray.ToString(System.Globalization.CultureInfo.InvariantCulture));
            UpdateCreatePanelsVisibility();
            RefreshMeasureSummaryFromUi();
        }

        private void ApplyCreateOptionsToUi(HalconShapeModelCreateOptions opt)
        {
            SelectComboByTag(CmbModelKind, opt.ModelKind.ToString());
            SelectComboByTag(CmbTemplateSource, opt.SourceKind.ToString());
            SelectComboByTag(CmbGenContourMode, opt.GenContourMode);
            SelectComboByTag(CmbOptimization, opt.Optimization);
            SelectComboByTag(CmbMetric, opt.Metric);

            SetText(TxtNumLevels, opt.NumLevels.ToString());
            SetText(TxtAngleStart, opt.AngleStartDeg.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
            SetText(TxtAngleExtent, opt.AngleExtentDeg.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
            SetText(TxtAngleStep, opt.AngleStepDeg.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
            SetText(TxtContrast, string.IsNullOrWhiteSpace(opt.Contrast) ? "auto" : opt.Contrast);
            SetText(TxtMinContrast, opt.MinContrast.ToString());
            SetText(TxtScaleMin, opt.ScaleMin.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
            SetText(TxtScaleMax, opt.ScaleMax.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
            SetText(TxtScaleStep, opt.ScaleStep.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
            SetText(TxtMinContourPoints, opt.MinContourPoints.ToString());
            if (ChkLargestContourOnly != null)
                ChkLargestContourOnly.IsChecked = opt.LargestContourOnly;
            SetText(TxtEdgeAlpha, opt.EdgeAlpha.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
            SetText(TxtEdgeLow, opt.EdgeLow.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
            SetText(TxtEdgeHigh, opt.EdgeHigh.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void SetText(TextBox? box, string value)
        {
            if (box != null)
                box.Text = value;
        }

        private static void SelectComboByTag(ComboBox? combo, string? tag)
        {
            if (combo == null || string.IsNullOrWhiteSpace(tag))
                return;

            foreach (object item in combo.Items)
            {
                if (item is ComboBoxItem cbi
                    && cbi.Tag is string itemTag
                    && string.Equals(itemTag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = cbi;
                    return;
                }
            }
        }

        private string? ResolveMetricUsedForCreate(
            HalconShapeModelCreateOptions opt,
            HalconXldContourBundle? xldBundle,
            bool fromImage)
        {
            if (fromImage)
                return opt.Metric;

            return HalconFlowBridge.ResolveShapeModelMetricForCreate(xldBundle, _nativeXldForCreate, opt);
        }

        private void ClearModelCreateMeta() => _lastModelCreateMeta = null;
    }
}
#endif
