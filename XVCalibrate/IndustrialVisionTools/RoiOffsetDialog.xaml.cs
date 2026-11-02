using System;
using System.Globalization;
using System.Windows;

namespace CalibOperatorCLI_Example
{
    public partial class RoiOffsetDialog : Window
    {
        public double Distance { get; private set; }

        public bool OffsetLeft { get; private set; } = true;

        public RoiOffsetDialog()
        {
            InitializeComponent();
        }

        public void Initialize(double lastDistance, bool usesMillimeters)
        {
            Distance = Math.Abs(lastDistance) > 1e-12 ? Math.Abs(lastDistance) : (usesMillimeters ? 1 : 10);
            TxtDistance.Text = Distance.ToString("G6", CultureInfo.InvariantCulture);
            TxtUnitHint.Text = usesMillimeters
                ? "距离按 mm。正值沿轨迹前进方向的左侧或右侧等距偏移。"
                : "当前无九点标定，距离按图像像素。";
            TxtDistance.SelectAll();
            TxtDistance.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseDistance(TxtDistance.Text, out double dist, out string? error))
            {
                MessageBox.Show(error, "偏移轨迹", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtDistance.Focus();
                TxtDistance.SelectAll();
                return;
            }

            Distance = dist;
            OffsetLeft = RbLeft.IsChecked == true;
            DialogResult = true;
        }

        private static bool TryParseDistance(string text, out double dist, out string? error)
        {
            dist = 0;
            error = null;
            if (string.IsNullOrWhiteSpace(text)
                || !double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out dist)
                    && !double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out dist))
            {
                error = "请输入有效的偏移距离。";
                return false;
            }

            if (dist <= 0 || double.IsNaN(dist) || double.IsInfinity(dist))
            {
                error = "偏移距离必须大于 0。";
                return false;
            }

            return true;
        }
    }
}
