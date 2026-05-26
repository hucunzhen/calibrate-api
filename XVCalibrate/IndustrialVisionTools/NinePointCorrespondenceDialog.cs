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
    /// 在图像上建立世界坐标与像素坐标的对应：手选像素，或点击附近匹配检测点。
    /// </summary>
    public sealed class NinePointCorrespondenceDialog : Window
    {
        private const double HitRadiusPx = 18;
        private const double MinScale = 0.05;
        private const double MaxScale = 20;

        private readonly CalibImage _image;
        private readonly Point2D[] _referenceImagePts;
        private readonly Point2D[] _worldPts;
        private readonly int _n;
        private readonly bool _manualPixelPick;
        private readonly Point2D?[] _pixelForWorld;

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

        /// <param name="referenceImagePts">检测点（可选）；手选模式下仅作参考显示与 YX 自动初值。</param>
        /// <param name="manualPixelPick">true=在图像任意位置点击取像素；false=须点在检测圆心附近。</param>
        public NinePointCorrespondenceDialog(
            CalibImage image,
            Point2D[]? referenceImagePts,
            Point2D[] worldPts,
            Window? owner,
            bool manualPixelPick = true)
        {
            _image = image ?? throw new ArgumentNullException(nameof(image));
            _referenceImagePts = referenceImagePts ?? Array.Empty<Point2D>();
            _worldPts = worldPts ?? throw new ArgumentNullException(nameof(worldPts));
            _n = worldPts.Length;
            if (_n < 4)
                throw new ArgumentException("标定至少需要 4 对世界/像素点");

            _manualPixelPick = manualPixelPick;
            _pixelForWorld = new Point2D?[_n];

            Title = manualPixelPick ? "九点标定 — 手选像素点" : "九点标定 — 匹配检测点";
            Width = 1100;
            Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = owner;
            Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a));

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

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
            var btnUndo = new Button { Content = "撤销", Width = 56, Margin = new Thickness(0, 0, 6, 0) };
            btnAuto.Click += (_, _) => { ApplyYxAutoMapping(); RefreshAll(); };
            btnClear.Click += (_, _) => { ClearAssignments(); RefreshAll(); };
            btnUndo.Click += (_, _) => { UndoActiveWorld(); RefreshAll(); };
            btnRow.Children.Add(btnAuto);
            btnRow.Children.Add(btnClear);
            btnRow.Children.Add(btnUndo);
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
                RefreshOverlay();
            };
            left.Children.Add(_worldList);

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
                if (!_manualPixelPick && _referenceImagePts.Length == _n)
                    ApplyYxAutoMapping();
                FitImageToView();
                RefreshAll();
            };
        }

        private string BuildHintText() =>
            _manualPixelPick
                ? "左侧选中世界点，在图像上左键点击取该点的像素坐标。\n" +
                  "浅蓝圆=检测参考点（若有）；绿=已手选；黄=当前行。滚轮缩放，右键拖动平移。"
                : "左侧选中世界点，在图像上点击检测圆心附近。\n" +
                  "绿=已配对，黄=当前行，灰蓝=未配对检测点。滚轮缩放，右键拖动平移。";

        private void UpdateHint()
        {
            int w = _activeWorldIndex;
            if (w < 0 || w >= _n) return;
            var wp = _worldPts[w];
            string assign = _pixelForWorld[w] is Point2D p
                ? $"像素 ({p.X:F1}, {p.Y:F1})"
                : "未选像素";
            _hintText.Text = $"当前：世界点 {w + 1}  ({wp.X:F2}, {wp.Y:F2}) mm\n{assign}\n\n{BuildHintText()}";
        }

        private string FormatWorldListItem(int i)
        {
            string status = _pixelForWorld[i] is Point2D p
                ? $"→ ({p.X:F0}, {p.Y:F0}) px"
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

            if (_manualPixelPick && _referenceImagePts.Length > 0)
            {
                for (int j = 0; j < _referenceImagePts.Length; j++)
                {
                    DrawMarker(_referenceImagePts[j], 6, Brushes.DodgerBlue,
                        new SolidColorBrush(Color.FromArgb(40, 30, 144, 255)),
                        (j + 1).ToString(CultureInfo.InvariantCulture), 10, false);
                }
            }
            else if (!_manualPixelPick)
            {
                for (int j = 0; j < _referenceImagePts.Length; j++)
                {
                    int assignedWorld = FindWorldIndexForPixel(j);
                    bool isAssigned = assignedWorld >= 0;
                    bool isActiveTarget = isAssigned && assignedWorld == _activeWorldIndex;
                    Brush stroke = isActiveTarget ? Brushes.Yellow : isAssigned ? Brushes.Lime : Brushes.DeepSkyBlue;
                    DrawMarker(_referenceImagePts[j], isActiveTarget ? 10 : 8, stroke,
                        new SolidColorBrush(Color.FromArgb(60, 0, 255, 0)),
                        (j + 1).ToString(CultureInfo.InvariantCulture), 11, isAssigned, assignedWorld);
                }
            }

            for (int i = 0; i < _n; i++)
            {
                if (_pixelForWorld[i] is not Point2D px)
                    continue;
                bool isActive = i == _activeWorldIndex;
                DrawMarker(px, isActive ? 10 : 8, isActive ? Brushes.Yellow : Brushes.Lime,
                    new SolidColorBrush(Color.FromArgb(90, 0, 255, 0)),
                    $"W{i + 1}", 11, true, i);
            }
        }

        private void DrawMarker(
            Point2D pt,
            double r,
            Brush stroke,
            Brush fill,
            string label,
            double fontSize,
            bool showWorldTag,
            int worldIndex = -1)
        {
            var ell = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Stroke = stroke,
                StrokeThickness = 2,
                Fill = fill
            };
            Canvas.SetLeft(ell, pt.X - r);
            Canvas.SetTop(ell, pt.Y - r);
            _overlay.Children.Add(ell);

            var tb = new TextBlock
            {
                Text = label,
                Foreground = stroke,
                FontWeight = FontWeights.Bold,
                FontSize = fontSize,
                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0))
            };
            Canvas.SetLeft(tb, pt.X + r + 2);
            Canvas.SetTop(tb, pt.Y - 8);
            _overlay.Children.Add(tb);

            if (showWorldTag && worldIndex >= 0)
            {
                var tag = new TextBlock
                {
                    Text = $"W{worldIndex + 1}",
                    Foreground = Brushes.Lime,
                    FontSize = 10
                };
                Canvas.SetLeft(tag, pt.X - r);
                Canvas.SetTop(tag, pt.Y - r - 14);
                _overlay.Children.Add(tag);
            }
        }

        private int FindWorldIndexForPixel(int referenceIndex)
        {
            if (_manualPixelPick || referenceIndex < 0 || referenceIndex >= _referenceImagePts.Length)
                return -1;
            var target = _referenceImagePts[referenceIndex];
            for (int i = 0; i < _n; i++)
            {
                if (_pixelForWorld[i] is Point2D p
                    && Math.Abs(p.X - target.X) < 0.5
                    && Math.Abs(p.Y - target.Y) < 0.5)
                    return i;
            }

            return -1;
        }

        private void RefreshAll()
        {
            RefreshWorldList();
            RefreshOverlay();
        }

        private void ClearAssignments()
        {
            for (int i = 0; i < _n; i++)
                _pixelForWorld[i] = null;
        }

        private void UndoActiveWorld()
        {
            if (_activeWorldIndex >= 0 && _activeWorldIndex < _n)
                _pixelForWorld[_activeWorldIndex] = null;
        }

        private void ApplyYxAutoMapping()
        {
            if (_referenceImagePts.Length != _n)
            {
                MessageBox.Show(this,
                    $"YX 自动需要 {_n} 个检测参考点，当前为 {_referenceImagePts.Length} 个。请手选或调整检测输出。",
                    "YX 自动", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int[] worldOrder = SortIndicesByYx(_worldPts);
            int[] imageOrder = SortIndicesByYx(_referenceImagePts);
            ClearAssignments();
            for (int k = 0; k < _n; k++)
                _pixelForWorld[worldOrder[k]] = _referenceImagePts[imageOrder[k]];
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

        private int HitTestReferencePoint(Point imgPt)
        {
            if (_referenceImagePts.Length == 0)
                return -1;

            int best = -1;
            double bestD2 = HitRadiusPx * HitRadiusPx;
            for (int j = 0; j < _referenceImagePts.Length; j++)
            {
                double dx = imgPt.X - _referenceImagePts[j].X;
                double dy = imgPt.Y - _referenceImagePts[j].Y;
                double d2 = dx * dx + dy * dy;
                if (d2 <= bestD2)
                {
                    bestD2 = d2;
                    best = j;
                }
            }

            return best;
        }

        private void AssignPixelToActiveWorld(Point2D pixel)
        {
            if (_activeWorldIndex < 0 || _activeWorldIndex >= _n)
                return;

            _pixelForWorld[_activeWorldIndex] = pixel;
            AdvanceToNextUnassigned();
        }

        private void AssignReferenceIndexToActiveWorld(int referenceIdx)
        {
            if (referenceIdx < 0 || referenceIdx >= _referenceImagePts.Length)
                return;
            AssignPixelToActiveWorld(_referenceImagePts[referenceIdx]);
        }

        private void AdvanceToNextUnassigned()
        {
            int next = _activeWorldIndex;
            while (next < _n - 1 && _pixelForWorld[next + 1] != null)
                next++;
            if (next < _n && _pixelForWorld[next] == null)
                _worldList.SelectedIndex = next;
            else
            {
                next = Array.FindIndex(_pixelForWorld, x => x == null);
                if (next >= 0)
                    _worldList.SelectedIndex = next;
            }

            _activeWorldIndex = _worldList.SelectedIndex >= 0 ? _worldList.SelectedIndex : _activeWorldIndex;
        }

        private void ViewHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var imgPt = GetImagePointFromMouse(e);
            if (_manualPixelPick)
            {
                AssignPixelToActiveWorld(new Point2D(imgPt.X, imgPt.Y));
                RefreshAll();
                _viewHost.Focus();
                return;
            }

            int hit = HitTestReferencePoint(imgPt);
            if (hit < 0)
            {
                MessageBox.Show(this, "请点击检测点圆心附近。", "选点", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AssignReferenceIndexToActiveWorld(hit);
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
                if (_pixelForWorld[i] == null)
                {
                    MessageBox.Show(this, $"世界点 {i + 1} 尚未指定像素坐标。", "标定",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    _worldList.SelectedIndex = i;
                    return;
                }
            }

            var result = new Point2D[_n];
            for (int i = 0; i < _n; i++)
                result[i] = _pixelForWorld[i]!.Value;
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
