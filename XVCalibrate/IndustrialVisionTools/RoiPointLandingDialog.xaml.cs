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

        /// <summary>与 ROI 内存相同：有九点时为 mm，否则为图像像素。</summary>
        public double ColumnPx { get; private set; }
        public double RowPx { get; private set; }

        public RoiPointLandingDialog()
        {
            InitializeComponent();
        }

        /// <param name="col">路径坐标：有九点时为 mm，否则为像素。不要先换成叠图像素。</param>
        /// <param name="row">路径坐标：有九点时为 mm，否则为像素。</param>
        public void Initialize(
            double col,
            double row,
            AffineTransform? affine,
            int imageWidth,
            int imageHeight,
            string? hint = null)
        {
            _affine = affine;
            _imageWidth = imageWidth;
            _imageHeight = imageHeight;
            ColumnPx = col;
            RowPx = row;

            bool hasAffine = affine is AffineTransform;
            string fmt = hasAffine ? "F4" : "F2";
            TxtColPx.Text = ColumnPx.ToString(fmt, CultureInfo.InvariantCulture);
            TxtRowPx.Text = RowPx.ToString(fmt, CultureInfo.InvariantCulture);

            if (LblCoordTitle != null)
                LblCoordTitle.Text = hasAffine ? "ROI 坐标（mm）" : "图像坐标（像素）";
            if (LblCol != null)
                LblCol.ToolTip = hasAffine ? "X（mm）" : "列坐标（像素）";
            if (LblRow != null)
                LblRow.ToolTip = hasAffine ? "Y（mm）" : "行坐标（像素）";

            if (!string.IsNullOrWhiteSpace(hint))
                TxtHint.Text = hint;
            else
            {
                TxtHint.Text = hasAffine
                    ? "可修改落点 X、Y（mm，与 ROI 相同）。像素只用于叠图。"
                    : "可修改落点列、行坐标（图像像素）。";
            }

            RefreshWorldHint();
        }

        private void RefreshWorldHint()
        {
            if (_affine is AffineTransform)
            {
                TxtWorldHint.Text = $"坐标为 mm（与 ROI 相同）；确定时不从像素反算。当前 ({ColumnPx:F4},{RowPx:F4}) mm";
            }
            else
                TxtWorldHint.Text = "未加载九点标定文件，仅编辑像素坐标。";
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParse(TxtColPx, out double col) || !TryParse(TxtRowPx, out double row))
            {
                MessageBox.Show(this,
                    _affine is AffineTransform ? "请输入有效的 X、Y（mm）。" : "请输入有效的列 X、行 Y（数值）。",
                    "落点坐标",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_affine is not AffineTransform && _imageWidth > 0 && _imageHeight > 0)
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
