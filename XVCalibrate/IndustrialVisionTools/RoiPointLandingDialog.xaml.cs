using System;

using System.Globalization;

using System.Windows;

using CalibOperatorPInvoke;



namespace CalibOperatorCLI_Example

{

    public partial class RoiPointLandingDialog : Window

    {

        private AffineTransform? _affine;

        private int _imageWidth;

        private int _imageHeight;



        public double ColumnPx { get; private set; }

        public double RowPx { get; private set; }



        public RoiPointLandingDialog()

        {

            InitializeComponent();

        }



        public void Initialize(

            double colPx,

            double rowPx,

            AffineTransform? affine,

            int imageWidth,

            int imageHeight,

            string? hint = null)

        {

            _affine = affine;

            _imageWidth = imageWidth;

            _imageHeight = imageHeight;

            ColumnPx = colPx;

            RowPx = rowPx;



            if (!string.IsNullOrWhiteSpace(hint))

                TxtHint.Text = hint;



            TxtColPx.Text = ColumnPx.ToString("F2", CultureInfo.InvariantCulture);

            TxtRowPx.Text = RowPx.ToString("F2", CultureInfo.InvariantCulture);

            RefreshWorldHint();

        }



        private void RefreshWorldHint()

        {

            if (_affine is AffineTransform t)

            {

                TxtWorldHint.Text =

                    $"世界坐标约 {HalconGeometryContourBuilder.FormatWorldPointMm(ColumnPx, RowPx, t)}";

            }

            else

                TxtWorldHint.Text = "未加载九点标定 JSON，仅编辑像素坐标。";

        }



        private void BtnOk_Click(object sender, RoutedEventArgs e)

        {

            if (!TryParse(TxtColPx, out double col) || !TryParse(TxtRowPx, out double row))

            {

                MessageBox.Show(this, "请输入有效的列 X、行 Y（数值）。", "落点坐标",

                    MessageBoxButton.OK, MessageBoxImage.Warning);

                return;

            }



            if (_imageWidth > 0 && _imageHeight > 0)

            {

                col = Math.Clamp(col, 0, _imageWidth - 1e-6);

                row = Math.Clamp(row, 0, _imageHeight - 1e-6);

            }



            ColumnPx = col;

            RowPx = row;

            DialogResult = true;

        }



        private static bool TryParse(System.Windows.Controls.TextBox box, out double v) =>

            double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    }

}

