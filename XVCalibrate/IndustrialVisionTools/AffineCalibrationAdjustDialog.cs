using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Drawing.Drawing2D;
using GdiBitmap = System.Drawing.Bitmap;
using GdiColor = System.Drawing.Color;
using GdiGraphics = System.Drawing.Graphics;
using GdiPen = System.Drawing.Pen;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 弹窗按钮式微调九点仿射标定（世界 mm 平移 / 绕 pivot 缩放），右侧实时预览网格坐标系。
    /// </summary>
    public sealed class AffineCalibrationAdjustDialog : Window
    {
        private const double MinViewScale = 0.05;
        private const double MaxViewScale = 20;

        private readonly AffineTransform _source;
        private readonly CalibImage? _previewImage;
        private readonly Point2D[]? _worldPts;
        private readonly Point2D[]? _imagePts;
        private double _offsetX;
        private double _offsetY;
        private double _scaleX;
        private double _scaleY;
        private double _pivotX;
        private double _pivotY;

        private readonly Border _viewHost;
        private readonly System.Windows.Controls.Image _imgPreview;
        private readonly ScaleTransform _viewScaleTransform = new ScaleTransform();
        private readonly TranslateTransform _viewTranslate = new TranslateTransform();
        private double _viewZoom = 1;
        private double _viewPanX;
        private double _viewPanY;
        private bool _isPanning;
        private Point _panStart;
        private Point _lastPanOffset;
        private readonly TextBlock _txtSource;
        private readonly TextBlock _txtCurrent;
        private readonly TextBlock _txtDelta;
        private readonly TextBox _txtStepMm;
        private readonly TextBox _txtScaleStepPct;
        private readonly TextBox _txtPivotX;
        private readonly TextBox _txtPivotY;
        private readonly TextBox _txtOffsetX;
        private readonly TextBox _txtOffsetY;
        private readonly TextBox _txtScaleX;
        private readonly TextBox _txtScaleY;

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        public AffineTransform ResultTransform => AffineTransformAdjust.Apply(
            _source, _offsetX, _offsetY, _scaleX, _scaleY, _pivotX, _pivotY);

        public (double OffsetX, double OffsetY, double ScaleX, double ScaleY, double PivotX, double PivotY) AdjustmentState
            => (_offsetX, _offsetY, _scaleX, _scaleY, _pivotX, _pivotY);

        public AffineCalibrationAdjustDialog(
            AffineTransform source,
            Window? owner,
            double initialOffsetX = 0,
            double initialOffsetY = 0,
            double initialScaleX = 1,
            double initialScaleY = 1,
            double pivotWorldX = 0,
            double pivotWorldY = 0,
            double nudgeStepMm = 0.5,
            double scaleStepPercent = 0.1,
            CalibImage? previewImage = null,
            Point2D[]? worldPts = null,
            Point2D[]? imagePts = null)
        {
            _source = source;
            _previewImage = previewImage;
            _worldPts = worldPts;
            _imagePts = imagePts;
            _offsetX = initialOffsetX;
            _offsetY = initialOffsetY;
            _scaleX = initialScaleX;
            _scaleY = initialScaleY;
            _pivotX = pivotWorldX;
            _pivotY = pivotWorldY;

            Title = "微调九点标定";
            Width = 920;
            Height = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = owner;
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2a, 0x2a, 0x2a));
            KeyDown += Window_KeyDown;

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var hint = new TextBlock
            {
                Text = "在世界坐标 (mm) 下整体平移或绕 pivot 缩放。右侧实时显示网格坐标系（X 红 / Y 绿 / O 中心）。Y+ 向上，X+ 向右。",
                Foreground = System.Windows.Media.Brushes.Gainsboro,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(left, 0);
            body.Children.Add(left);

            left.Children.Add(SectionTitle("平移步长 (mm)"));
            _txtStepMm = NumericBox(nudgeStepMm.ToString("G4", CultureInfo.InvariantCulture));
            left.Children.Add(_txtStepMm);

            left.Children.Add(SectionTitle("方向（可连点）"));
            left.Children.Add(BuildNudgePad());

            left.Children.Add(SectionTitle("缩放步长 (%)"));
            _txtScaleStepPct = NumericBox(scaleStepPercent.ToString("G4", CultureInfo.InvariantCulture));
            left.Children.Add(_txtScaleStepPct);

            var scaleRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
            scaleRow.Children.Add(MakeButton("X+", () => NudgeScale(axisX: true, increase: true), 52));
            scaleRow.Children.Add(MakeButton("X-", () => NudgeScale(axisX: true, increase: false), 52));
            scaleRow.Children.Add(MakeButton("Y+", () => NudgeScale(axisX: false, increase: true), 52));
            scaleRow.Children.Add(MakeButton("Y-", () => NudgeScale(axisX: false, increase: false), 52));
            scaleRow.Children.Add(MakeButton("等比+", () => NudgeScaleUniform(increase: true), 60));
            scaleRow.Children.Add(MakeButton("等比-", () => NudgeScaleUniform(increase: false), 60));
            left.Children.Add(scaleRow);

            left.Children.Add(SectionTitle("缩放中心 pivot (mm)"));
            var pivotRow = new Grid();
            pivotRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            pivotRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pivotRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            pivotRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lblPx = Label("X");
            Grid.SetColumn(lblPx, 0);
            _txtPivotX = NumericBox(pivotWorldX.ToString("G4", CultureInfo.InvariantCulture));
            Grid.SetColumn(_txtPivotX, 1);
            var lblPy = Label("Y");
            Grid.SetColumn(lblPy, 2);
            _txtPivotY = NumericBox(pivotWorldY.ToString("G4", CultureInfo.InvariantCulture));
            Grid.SetColumn(_txtPivotY, 3);
            pivotRow.Children.Add(lblPx);
            pivotRow.Children.Add(_txtPivotX);
            pivotRow.Children.Add(lblPy);
            pivotRow.Children.Add(_txtPivotY);
            left.Children.Add(pivotRow);

            left.Children.Add(SectionTitle("累计偏移 / 缩放（可手输）"));
            var offRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            offRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            offRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            offRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            offRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lblDx = Label("ΔX");
            Grid.SetColumn(lblDx, 0);
            _txtOffsetX = NumericBox(_offsetX.ToString("G6", CultureInfo.InvariantCulture));
            Grid.SetColumn(_txtOffsetX, 1);
            var lblDy = Label("ΔY");
            Grid.SetColumn(lblDy, 2);
            _txtOffsetY = NumericBox(_offsetY.ToString("G6", CultureInfo.InvariantCulture));
            Grid.SetColumn(_txtOffsetY, 3);
            offRow.Children.Add(lblDx);
            offRow.Children.Add(_txtOffsetX);
            offRow.Children.Add(lblDy);
            offRow.Children.Add(_txtOffsetY);
            left.Children.Add(offRow);

            var scRow = new Grid();
            scRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            scRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            scRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            scRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lblSx = Label("SX");
            Grid.SetColumn(lblSx, 0);
            _txtScaleX = NumericBox(_scaleX.ToString("G8", CultureInfo.InvariantCulture));
            Grid.SetColumn(_txtScaleX, 1);
            var lblSy = Label("SY");
            Grid.SetColumn(lblSy, 2);
            _txtScaleY = NumericBox(_scaleY.ToString("G8", CultureInfo.InvariantCulture));
            Grid.SetColumn(_txtScaleY, 3);
            scRow.Children.Add(lblSx);
            scRow.Children.Add(_txtScaleX);
            scRow.Children.Add(lblSy);
            scRow.Children.Add(_txtScaleY);
            left.Children.Add(scRow);

            _txtOffsetX.LostFocus += (_, _) => ApplyManualFields();
            _txtOffsetY.LostFocus += (_, _) => ApplyManualFields();
            _txtScaleX.LostFocus += (_, _) => ApplyManualFields();
            _txtScaleY.LostFocus += (_, _) => ApplyManualFields();
            _txtPivotX.LostFocus += (_, _) => ApplyManualFields();
            _txtPivotY.LostFocus += (_, _) => ApplyManualFields();

            var btnReset = MakeButton("重置", ResetAdjustments, 72);
            btnReset.Margin = new Thickness(0, 8, 0, 0);
            left.Children.Add(btnReset);

            left.Children.Add(new TextBlock
            {
                Text = "快捷键：方向键平移 · Shift×10 · Ctrl×0.1\n预览：滚轮缩放 · 左键/右键拖动平移",
                Foreground = System.Windows.Media.Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(right, 1);
            body.Children.Add(right);

            _viewHost = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x12, 0x12, 0x12)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Focusable = true,
                Margin = new Thickness(0, 0, 0, 8)
            };
            _viewHost.MouseWheel += ViewHost_MouseWheel;
            _viewHost.MouseLeftButtonDown += ViewHost_MouseLeftButtonDown;
            _viewHost.MouseLeftButtonUp += ViewHost_MouseLeftButtonUp;
            _viewHost.PreviewMouseRightButtonDown += ViewHost_PreviewMouseRightButtonDown;
            _viewHost.PreviewMouseRightButtonUp += ViewHost_PreviewMouseRightButtonUp;
            _viewHost.MouseMove += ViewHost_MouseMove;

            _imgPreview = new System.Windows.Controls.Image
            {
                Stretch = Stretch.None,
                IsHitTestVisible = false
            };

            var canvasRoot = new Canvas
            {
                Background = System.Windows.Media.Brushes.Transparent,
                RenderTransform = new TransformGroup
                {
                    Children = new TransformCollection { _viewScaleTransform, _viewTranslate }
                }
            };
            canvasRoot.Children.Add(_imgPreview);
            _viewHost.Child = canvasRoot;
            Grid.SetRow(_viewHost, 0);
            right.Children.Add(_viewHost);

            _txtSource = CreateInfoTextBlock();
            _txtSource.Text = _source.ToString();
            _txtDelta = CreateInfoTextBlock();
            _txtCurrent = CreateInfoTextBlock();
            var infoPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            infoPanel.Children.Add(SectionTitle("Transform"));
            infoPanel.Children.Add(WrapInfo(_txtDelta));
            infoPanel.Children.Add(WrapInfo(_txtCurrent));
            Grid.SetRow(infoPanel, 1);
            right.Children.Add(infoPanel);

            var bottom = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var btnOk = new Button { Content = "确定", Width = 88, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var btnCancel = new Button { Content = "取消", Width = 72, IsCancel = true };
            btnOk.Click += (_, _) =>
            {
                ApplyManualFields();
                if (_scaleX <= 0 || _scaleY <= 0)
                {
                    MessageBox.Show(this, "scaleX/scaleY 须为正数。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                DialogResult = true;
                Close();
            };
            btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
            bottom.Children.Add(btnOk);
            bottom.Children.Add(btnCancel);
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);

            Content = root;
            Loaded += (_, _) =>
            {
                RefreshPreview();
                Dispatcher.BeginInvoke(FitImageToView, System.Windows.Threading.DispatcherPriority.Loaded);
            };
        }

        private void BeginPan(Point start)
        {
            _isPanning = true;
            _panStart = start;
            _lastPanOffset = new Point(_viewPanX, _viewPanY);
            _viewHost.CaptureMouse();
        }

        private void EndPan()
        {
            if (!_isPanning)
                return;
            _isPanning = false;
            _viewHost.ReleaseMouseCapture();
        }

        private void ViewHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BeginPan(e.GetPosition(_viewHost));
            e.Handled = true;
        }

        private void ViewHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndPan();
        }

        private void ViewHost_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning)
                return;
            var current = e.GetPosition(_viewHost);
            _viewPanX = _lastPanOffset.X + (current.X - _panStart.X);
            _viewPanY = _lastPanOffset.Y + (current.Y - _panStart.Y);
            ApplyViewTransform();
        }

        private void ViewHost_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            BeginPan(e.GetPosition(_viewHost));
            e.Handled = true;
        }

        private void ViewHost_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndPan();
            e.Handled = true;
        }

        private void ViewHost_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var mouseCanvas = e.GetPosition(_viewHost);
            double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            double newScale = Math.Max(MinViewScale, Math.Min(MaxViewScale, _viewZoom * factor));
            if (Math.Abs(newScale - _viewZoom) < 0.001)
                return;

            double parentX = _viewZoom * mouseCanvas.X + _viewPanX;
            double parentY = _viewZoom * mouseCanvas.Y + _viewPanY;
            _viewZoom = newScale;
            _viewPanX = parentX - newScale * mouseCanvas.X;
            _viewPanY = parentY - newScale * mouseCanvas.Y;
            ApplyViewTransform();
        }

        private void FitImageToView()
        {
            if (_previewImage == null || _imgPreview.Source == null)
                return;

            double viewW = _viewHost.ActualWidth > 1 ? _viewHost.ActualWidth : 520;
            double viewH = _viewHost.ActualHeight > 1 ? _viewHost.ActualHeight : 400;
            double imgW = _previewImage.Width;
            double imgH = _previewImage.Height;
            if (imgW < 1 || imgH < 1)
                return;

            double fit = Math.Min(viewW / imgW, viewH / imgH) * 0.95;
            fit = Math.Max(MinViewScale, Math.Min(MaxViewScale, fit));
            _viewZoom = fit;
            _viewPanX = (viewW - imgW * _viewZoom) / 2;
            _viewPanY = (viewH - imgH * _viewZoom) / 2;
            ApplyViewTransform();
        }

        private void ApplyViewTransform()
        {
            _viewScaleTransform.ScaleX = _viewZoom;
            _viewScaleTransform.ScaleY = _viewZoom;
            _viewTranslate.X = _viewPanX;
            _viewTranslate.Y = _viewPanY;
        }

        private Grid BuildNudgePad()
        {
            var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 10) };
            for (int i = 0; i < 3; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            void Place(int row, int col, string label, Action action)
            {
                var btn = MakeButton(label, action, 44);
                Grid.SetRow(btn, row);
                Grid.SetColumn(btn, col);
                grid.Children.Add(btn);
            }

            Place(0, 1, "↑", () => NudgeOffset(0, ReadStepMm()));
            Place(1, 0, "←", () => NudgeOffset(-ReadStepMm(), 0));
            Place(1, 1, "·", () => { });
            if (grid.Children[^1] is Button centerBtn)
            {
                centerBtn.IsEnabled = false;
                centerBtn.Opacity = 0.35;
            }
            Place(1, 2, "→", () => NudgeOffset(ReadStepMm(), 0));
            Place(2, 1, "↓", () => NudgeOffset(0, -ReadStepMm()));
            return grid;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            double step = ReadStepMm();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                step *= 10;
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                step *= 0.1;

            switch (e.Key)
            {
                case Key.Up:
                    NudgeOffset(0, step);
                    e.Handled = true;
                    break;
                case Key.Down:
                    NudgeOffset(0, -step);
                    e.Handled = true;
                    break;
                case Key.Left:
                    NudgeOffset(-step, 0);
                    e.Handled = true;
                    break;
                case Key.Right:
                    NudgeOffset(step, 0);
                    e.Handled = true;
                    break;
            }
        }

        private void NudgeOffset(double dx, double dy)
        {
            ApplyManualFields();
            _offsetX += dx;
            _offsetY += dy;
            RefreshPreview();
        }

        private void NudgeScale(bool axisX, bool increase)
        {
            ApplyManualFields();
            double factor = ReadScaleFactor(increase);
            if (axisX)
                _scaleX *= factor;
            else
                _scaleY *= factor;
            RefreshPreview();
        }

        private void NudgeScaleUniform(bool increase)
        {
            ApplyManualFields();
            double factor = ReadScaleFactor(increase);
            _scaleX *= factor;
            _scaleY *= factor;
            RefreshPreview();
        }

        private void ApplyManualFields()
        {
            if (TryParseField(_txtOffsetX, out double ox)) _offsetX = ox;
            if (TryParseField(_txtOffsetY, out double oy)) _offsetY = oy;
            if (TryParseField(_txtScaleX, out double sx) && sx > 0) _scaleX = sx;
            if (TryParseField(_txtScaleY, out double sy) && sy > 0) _scaleY = sy;
            if (TryParseField(_txtPivotX, out double px)) _pivotX = px;
            if (TryParseField(_txtPivotY, out double py)) _pivotY = py;
            RefreshPreview();
        }

        private void ResetAdjustments()
        {
            _offsetX = 0;
            _offsetY = 0;
            _scaleX = 1;
            _scaleY = 1;
            RefreshPreview();
        }

        private double ReadStepMm()
        {
            if (TryParseField(_txtStepMm, out double v) && v > 0)
                return v;
            return 0.5;
        }

        private double ReadScaleFactor(bool increase)
        {
            double pct = TryParseField(_txtScaleStepPct, out double v) && v > 0 ? v : 0.1;
            double delta = pct / 100.0;
            return increase ? 1.0 + delta : 1.0 / (1.0 + delta);
        }

        private void RefreshPreview()
        {
            _txtOffsetX.Text = _offsetX.ToString("G6", CultureInfo.InvariantCulture);
            _txtOffsetY.Text = _offsetY.ToString("G6", CultureInfo.InvariantCulture);
            _txtScaleX.Text = _scaleX.ToString("G8", CultureInfo.InvariantCulture);
            _txtScaleY.Text = _scaleY.ToString("G8", CultureInfo.InvariantCulture);
            _txtPivotX.Text = _pivotX.ToString("G4", CultureInfo.InvariantCulture);
            _txtPivotY.Text = _pivotY.ToString("G4", CultureInfo.InvariantCulture);

            _txtDelta.Text =
                $"原始 Transform:\n{_source}\n\n" +
                $"累计: ΔX={_offsetX:G4} mm  ΔY={_offsetY:G4} mm\n" +
                $"scaleX={_scaleX:G6}  scaleY={_scaleY:G6}\n" +
                $"pivot=({_pivotX:G4}, {_pivotY:G4}) mm";

            _txtCurrent.Text = ResultTransform.ToString();

            RefreshPreviewImage();
        }

        private void RefreshPreviewImage()
        {
            if (_previewImage == null || _worldPts == null || _worldPts.Length == 0)
            {
                _imgPreview.Source = null;
                return;
            }

            using var baseBmp = _previewImage.ToBitmap();
            if (baseBmp == null)
            {
                _imgPreview.Source = null;
                return;
            }

            var drawBmp = new GdiBitmap(baseBmp.Width, baseBmp.Height, GdiPixelFormat.Format24bppRgb);
            using (var gTemp = GdiGraphics.FromImage(drawBmp))
                gTemp.DrawImage(baseBmp, 0, 0);

            using (var g = GdiGraphics.FromImage(drawBmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (_imagePts != null)
                {
                    using var ptPen = new GdiPen(GdiColor.FromArgb(160, 0, 220, 255), 1.4f);
                    foreach (var p in _imagePts)
                        g.DrawEllipse(ptPen, (float)p.X - 3, (float)p.Y - 3, 6, 6);
                }

                AffineWorldGridOverlay.DrawOnGraphics(
                    g, ResultTransform, _worldPts, drawBmp.Width, drawBmp.Height, _imagePts);
            }

            IntPtr hBitmap = drawBmp.GetHbitmap();
            try
            {
                var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmapSource.Freeze();
                _imgPreview.Source = bitmapSource;
                _imgPreview.Width = drawBmp.Width;
                _imgPreview.Height = drawBmp.Height;
                if (_viewHost.Child is Canvas canvas)
                {
                    canvas.Width = drawBmp.Width;
                    canvas.Height = drawBmp.Height;
                }

                ApplyViewTransform();
            }
            finally
            {
                DeleteObject(hBitmap);
                drawBmp.Dispose();
            }
        }

        private static bool TryParseField(TextBox box, out double value) =>
            double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static TextBlock SectionTitle(string text) =>
            new TextBlock
            {
                Text = text,
                Foreground = System.Windows.Media.Brushes.Silver,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 4)
            };

        private static TextBlock Label(string text) =>
            new TextBlock
            {
                Text = text,
                Foreground = System.Windows.Media.Brushes.Gainsboro,
                VerticalAlignment = VerticalAlignment.Center
            };

        private static TextBox NumericBox(string initial) =>
            new TextBox
            {
                Text = initial,
                Height = 26,
                Margin = new Thickness(4, 0, 4, 0),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x28, 0x28, 0x28)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55))
            };

        private static TextBlock CreateInfoTextBlock() =>
            new TextBlock
            {
                Foreground = System.Windows.Media.Brushes.Gainsboro,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };

        private static Border WrapInfo(TextBlock text) =>
            new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1e, 0x1e, 0x1e)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = text
            };

        private Button MakeButton(string content, Action onClick, double width = 44)
        {
            var btn = new Button
            {
                Content = content,
                Width = width,
                Height = 32,
                Margin = new Thickness(3),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3d, 0x3d, 0x3d)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66))
            };
            btn.Click += (_, _) => onClick();
            return btn;
        }
    }
}
