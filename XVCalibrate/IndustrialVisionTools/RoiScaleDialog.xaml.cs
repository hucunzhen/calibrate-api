using System;
using System.Globalization;
using System.Windows;

namespace CalibOperatorCLI_Example
{
    public partial class RoiScaleDialog : Window
    {
        public double Factor { get; private set; } = 1;

        public RoiScaleDialog()
        {
            InitializeComponent();
        }

        public void Initialize(double lastFactor, bool usesMillimeters)
        {
            Factor = lastFactor > 1e-9 ? lastFactor : 1;
            TxtFactor.Text = Factor.ToString("G6", CultureInfo.InvariantCulture);
            TxtUnitHint.Text = usesMillimeters
                ? "有九点标定时按 mm 几何缩放，中心位置不变。"
                : "当前按图像像素几何缩放，中心位置不变。";
            TxtFactor.SelectAll();
            TxtFactor.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseFactor(TxtFactor.Text, out double factor, out string? error))
            {
                MessageBox.Show(error, "缩放 ROI", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtFactor.Focus();
                TxtFactor.SelectAll();
                return;
            }

            Factor = factor;
            DialogResult = true;
        }

        internal static bool TryParseFactor(string? text, out double factor, out string? error)
        {
            factor = 1;
            error = null;
            string raw = (text ?? "").Trim();
            if (raw.Length == 0)
            {
                error = "请输入倍率，例如 1.02 或 102%。";
                return false;
            }

            bool percent = raw.EndsWith("%", StringComparison.Ordinal);
            if (percent)
                raw = raw[..^1].Trim();

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && !double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                error = "倍率无法解析。请用 1.02 或 102%。";
                return false;
            }

            factor = percent ? value / 100.0 : value;
            if (!(factor > 0) || double.IsNaN(factor) || double.IsInfinity(factor))
            {
                error = "倍率必须是大于 0 的有限数。";
                return false;
            }

            if (Math.Abs(factor - 1) < 1e-12)
            {
                error = "倍率为 1，ROI 不会变化。";
                return false;
            }

            return true;
        }
    }
}
