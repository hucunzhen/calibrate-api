using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 在图像上点击检测点，与世界坐标配置逐行建立对应关系。
    /// </summary>
    public sealed class NinePointCorrespondenceDialog : Window
    {
        private const double HitRadiusPx = 18;
        private const double MinScale = 0.05;
        private const double MaxScale = 20;

        private readonly CalibImage _image;
        private readonly Point2D[] _imagePts;
        private readonly Point2D[] _worldPts;
        private readonly int _n;

        private readonly int[] _imageIndexForWorld;
        private readonly ListBox _worldList;
        private readonly TextBlock _hintText;
        private readonly Canvas _overlay;
        private readonly Border _viewHost;
        private readonly ScaleTransform _viewScale = new ScaleTransform();
        private readonly TranslateTransform _viewTranslate = new TranslateTransform();

        private int _activeWorldIndex;
        private double _scale = 1;
        private double _offsetX;
        private double _offsetY;
        private bool _isPanning;
        private Point _panStart;
        private Point _lastPanOffset;

        public Point2D[]? ResultImagePoints { get; private set; }

        public NinePointCorrespondenceDialog(CalibImage image, Point2D[] imagePts, Point2D[] worldPts, Window? owner)
        {
            _image = image ?? throw new ArgumentNullException(nameof(image));
            _imagePts = imagePts ?? throw new ArgumentNullException(nameof(imagePts));
            _worldPts = worldPts ?? throw new ArgumentNullException(nameof(worldPts));
            _n = worldPts.Length;
            if (_imagePts.Length != _n)
                throw new ArgumentException($"图像点 {_imagePts.Length} 个与世界点 {_n} 个数量须一致");

            _imageIndexForWorld = Enumerable.Repeat(-1, _n).ToArray();

            Title = "确认标定点对应（点击图像）";
            Width = 1100;
            Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = owner;
            Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a));

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左侧：世界点列表与说明
            var left = new DockPanel { Margin = new Thickness(10), Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)) };
            Grid.SetColumn(left, 0);

            _hintText = new TextBlock
            {
                Text = BuildHintText(),
                Foreground = Brushes.Gainsboro,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 8, 8, 8)
            };
            DockPanel.SetDock(_hintText, Dock.Top);
            left.Children.Add(_hintText);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 0, 8, 8)
            };
            var btnAuto = new Button { Content = "YX 自动", Width = 72, Margin = new Thickness(0, 0, 6, 0) };
            var btnClear = new Button { Content = "清除", Width = 56, Margin = new Thickness(0, 0, 6, 0) };
            btnAuto.Click += (_, _) => { ApplyYxAutoMapping(); RefreshAll(); };
            btnClear.Click += (_, _) => { ClearAssignments(); RefreshAll(); };
            btnRow.Children.Add(btnAuto);
            btnRow.Children.Add(btnClear);
            DockPanel.SetDock(btnRow, Dock.Bottom);
            left.Children.Add(btnRow);

            _worldList = new ListBox
            {
                Margin = new Thickness(8, 0, 8, 8),
                Background = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            for (int i = 0; i < _n; i++)
                _worldList.Items.Add(FormatWorldListItem(i));
            _worldList.SelectionChanged += (_, _) =>
            {
                if (_worldList.SelectedIndex >= 0)
                    _activeWorldIndex = _worldList.SelectedIndex;
                UpdateHint();
            };
            left.Children.Add(_worldList);

            // 右侧：图像 + 叠加
            var right = new DockPanel { Margin = new Thickness(0, 10, 10, 10) };
            Grid.SetColumn(right, 1);

            var bottomBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var btnOk = new Button { Content = "确定标定", Width = 96, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var btnCancel = new Button { Content = "取消", Width = 72, IsCancel = true };
            btnOk.Click += OnConfirm;
            btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
            bottomBtns.Children.Add(btnOk);
            bottomBtns.Children.Add(btnCancel);
            DockPanel.SetDock(bottomBtns, Dock.Bottom);
            right.Children.Add(bottomBtns);

            _viewHost = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
                ClipToBounds = true,
                Focusable = true
            };
            _viewHost.MouseLeftButtonDown += ViewHost_MouseLeftButtonDown;
            _viewHost.MouseMove += ViewHost_MouseMove;
            _viewHost.MouseLeftButtonUp += ViewHost_MouseLeftButtonUp;
            _viewHost.MouseWheel += ViewHost_MouseWheel;
            _viewHost.PreviewMouseRightButtonDown += ViewHost_PreviewMouseRightButtonDown;
            _viewHost.PreviewMouseRightButtonUp += ViewHost_PreviewMouseRightButtonUp;

            var canvasRoot = new Canvas
            {
                Width = _image.Width,
                Height = _image.Height,
                Background = Brushes.Transparent,
                RenderTransform = new TransformGroup
                {
                    Children = new TransformCollection { _viewScale, _viewTranslate }
                }
            };

            var wpfImage = new Image
            {
                Source = CalibImageToBitmapSource(_image),
                Width = _image.Width,
                Height = _image.Height,
                Stretch = Stretch.None,
                IsHitTestVisible = false
            };
            canvasRoot.Children.Add(wpfImage);

            _overlay = new Canvas
            {
                Width = _image.Width,
                Height = _image.Height,
                IsHitTestVisible = false,
                Background = Brushes.Transparent
            };
            canvasRoot.Children.Add(_overlay);

            _viewHost.Child = canvasRoot;
            right.Children.Add(_viewHost);

            root.Children.Add(left);
            root.Children.Add(right);
            Content = root;

            Loaded += (_, _) =>
            {
                _worldList.SelectedIndex = 0;
                _activeWorldIndex = 0;
                ApplyYxAutoMapping();
                FitImageToView();
                RefreshAll();
            };
        }

        private string BuildHintText() =>
            "左侧选中世界点，再在右侧图像上点击对应的检测圆心。\n" +
            "绿色=已配对，黄色=当前行，灰色=未配对。滚轮缩放，右键拖动平移。";

        private void UpdateHint()
        {
            int w = _activeWorldIndex;
            if (w < 0 || w >= _n) return;
            var wp = _worldPts[w];
            string assign = _imageIndexForWorld[w] >= 0
                ? $"已对应图像点 {_imageIndexForWorld[w] + 1}"
                : "未选择图像点";
            _hintText.Text = $"当前：世界点 {w + 1}  ({wp.X:F2}, {wp.Y:F2}) mm\n{assign}\n\n{BuildHintText()}";
        }

        private string FormatWorldListItem(int i)
        {
            int img = _imageIndexForWorld[i];
            string status = img >= 0
                ? $"→ 图{img + 1} ({_imagePts[img].X:F0},{_imagePts[img].Y:F0})"
                : "→ 未选";
            return $"{i + 1}. 世界 ({_worldPts[i].X:F1}, {_worldPts[i].Y:F1})  {status}";
        }

        private void RefreshWorldList()
        {
            int sel = _worldList.SelectedIndex;
            for (int i = 0; i < _n; i++)
                _worldList.Items[i] = FormatWorldListItem(i);
            if (sel >= 0 && sel < _n)
                _worldList.SelectedIndex = sel;
            UpdateHint();
        }

        private void RefreshOverlay()
        {
            _overlay.Children.Clear();
            for (int j = 0; j < _imagePts.Length; j++)
            {
                int assignedWorld = Array.IndexOf(_imageIndexForWorld, j);
                bool isAssigned = assignedWorld >= 0;
                bool isActiveTarget = isAssigned && assignedWorld == _activeWorldIndex;

                Brush stroke = isActiveTarget ? Brushes.Yellow : isAssigned ? Brushes.Lime : Brushes.DeepSkyBlue;
                double r = isActiveTarget ? 10 : 8;

                var ell = new Ellipse
                {
                    Width = r * 2,
                    Height = r * 2,
                    Stroke = stroke,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(60, 0, 255, 0))
                };
                Canvas.SetLeft(ell, _imagePts[j].X - r);
                Canvas.SetTop(ell, _imagePts[j].Y - r);
                _overlay.Children.Add(ell);

                var label = new TextBlock
                {
                    Text = (j + 1).ToString(CultureInfo.InvariantCulture),
                    Foreground = stroke,
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0))
                };
                Canvas.SetLeft(label, _imagePts[j].X + r + 2);
                Canvas.SetTop(label, _imagePts[j].Y - 8);
                _overlay.Children.Add(label);

                if (isAssigned)
                {
                    var tag = new TextBlock
                    {
                        Text = $"W{assignedWorld + 1}",
                        Foreground = Brushes.Lime,
                        FontSize = 10
                    };
                    Canvas.SetLeft(tag, _imagePts[j].X - r);
                    Canvas.SetTop(tag, _imagePts[j].Y - r - 14);
                    _overlay.Children.Add(tag);
                }
            }
        }

        private void RefreshAll()
        {
            RefreshWorldList();
            RefreshOverlay();
        }

        private void ClearAssignments()
        {
            for (int i = 0; i < _n; i++)
                _imageIndexForWorld[i] = -1;
        }

        private void ApplyYxAutoMapping()
        {
            int[] worldOrder = SortIndicesByYx(_worldPts);
            int[] imageOrder = SortIndicesByYx(_imagePts);
            ClearAssignments();
            for (int k = 0; k < _n; k++)
                _imageIndexForWorld[worldOrder[k]] = imageOrder[k];
        }

        private static int[] SortIndicesByYx(IReadOnlyList<Point2D> pts)
        {
            var order = Enumerable.Range(0, pts.Count).ToArray();
            Array.Sort(order, (a, b) =>
            {
                int cmp = pts[a].Y.CompareTo(pts[b].Y);
                return cmp != 0 ? cmp : pts[a].X.CompareTo(pts[b].X);
            });
            return order;
        }

        private Point GetImagePointFromMouse(MouseEventArgs e)
        {
            var p = e.GetPosition(_viewHost);
            double x = (p.X - _offsetX) / _scale;
            double y = (p.Y - _offsetY) / _scale;
            return new Point(
                Math.Max(0, Math.Min(_image.Width - 1, x)),
                Math.Max(0, Math.Min(_image.Height - 1, y)));
        }

        private int HitTestImagePoint(Point imgPt)
        {
            int best = -1;
            double bestD2 = HitRadiusPx * HitRadiusPx;
            for (int j = 0; j < _imagePts.Length; j++)
            {
                double dx = imgPt.X - _imagePts[j].X;
                double dy = imgPt.Y - _imagePts[j].Y;
                double d2 = dx * dx + dy * dy;
                if (d2 <= bestD2)
                {
                    bestD2 = d2;
                    best = j;
                }
            }

            return best;
        }

        private void AssignImageToActiveWorld(int imageIdx)
        {
            if (imageIdx < 0 || imageIdx >= _n || _activeWorldIndex < 0 || _activeWorldIndex >= _n)
                return;

            for (int i = 0; i < _n; i++)
            {
                if (i != _activeWorldIndex && _imageIndexForWorld[i] == imageIdx)
                    _imageIndexForWorld[i] = -1;
            }

            _imageIndexForWorld[_activeWorldIndex] = imageIdx;

            int next = _activeWorldIndex;
            while (next < _n - 1 && _imageIndexForWorld[next + 1] >= 0)
                next++;
            if (next < _n && _imageIndexForWorld[next] < 0)
                _worldList.SelectedIndex = next;
            else
            {
                next = Array.FindIndex(_imageIndexForWorld, x => x < 0);
                if (next >= 0)
                    _worldList.SelectedIndex = next;
            }

            _activeWorldIndex = _worldList.SelectedIndex >= 0 ? _worldList.SelectedIndex : _activeWorldIndex;
        }

        private void ViewHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var imgPt = GetImagePointFromMouse(e);
            int hit = HitTestImagePoint(imgPt);
            if (hit < 0)
            {
                MessageBox.Show(this, "请点击检测点圆心附近。", "选点", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AssignImageToActiveWorld(hit);
            RefreshAll();
            _viewHost.Focus();
        }

        private void ViewHost_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            var current = e.GetPosition(_viewHost);
            _offsetX = _lastPanOffset.X + (current.X - _panStart.X);
            _offsetY = _lastPanOffset.Y + (current.Y - _panStart.Y);
            ApplyTransform();
        }

        private void ViewHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _viewHost.ReleaseMouseCapture();
        }

        private void ViewHost_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isPanning = true;
            _panStart = e.GetPosition(_viewHost);
            _lastPanOffset = new Point(_offsetX, _offsetY);
            _viewHost.CaptureMouse();
            e.Handled = true;
        }

        private void ViewHost_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                _viewHost.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void ViewHost_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var mouseCanvas = e.GetPosition(_viewHost);
            double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            double newScale = Math.Max(MinScale, Math.Min(MaxScale, _scale * factor));
            if (Math.Abs(newScale - _scale) < 0.001) return;

            double parentX = _scale * mouseCanvas.X + _offsetX;
            double parentY = _scale * mouseCanvas.Y + _offsetY;
            _scale = newScale;
            _offsetX = parentX - newScale * mouseCanvas.X;
            _offsetY = parentY - newScale * mouseCanvas.Y;
            ApplyTransform();
        }

        private void FitImageToView()
        {
            double viewW = _viewHost.ActualWidth > 1 ? _viewHost.ActualWidth : 700;
            double viewH = _viewHost.ActualHeight > 1 ? _viewHost.ActualHeight : 500;
            double fit = Math.Min(viewW / _image.Width, viewH / _image.Height) * 0.95;
            fit = Math.Max(MinScale, Math.Min(MaxScale, fit));
            _scale = fit;
            _offsetX = (viewW - _image.Width * _scale) / 2;
            _offsetY = (viewH - _image.Height * _scale) / 2;
            ApplyTransform();
        }

        private void ApplyTransform()
        {
            _viewScale.ScaleX = _scale;
            _viewScale.ScaleY = _scale;
            _viewTranslate.X = _offsetX;
            _viewTranslate.Y = _offsetY;
        }

        private void OnConfirm(object? sender, RoutedEventArgs e)
        {
            for (int i = 0; i < _n; i++)
            {
                if (_imageIndexForWorld[i] < 0)
                {
                    MessageBox.Show(this, $"世界点 {i + 1} 尚未在图像上指定对应检测点。", "对应关系",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    _worldList.SelectedIndex = i;
                    return;
                }
            }

            var used = new HashSet<int>();
            for (int i = 0; i < _n; i++)
            {
                if (!used.Add(_imageIndexForWorld[i]))
                {
                    MessageBox.Show(this, "同一图像检测点不能对应多个世界点。", "对应关系",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var result = new Point2D[_n];
            for (int i = 0; i < _n; i++)
                result[i] = _imagePts[_imageIndexForWorld[i]];
            ResultImagePoints = result;
            DialogResult = true;
            Close();
        }

        private static BitmapSource CalibImageToBitmapSource(CalibImage img)
        {
            using var bmp = img.ToBitmap();
            using var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
    }
}
