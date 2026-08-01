using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GdiBitmap = System.Drawing.Bitmap;
using GdiColor = System.Drawing.Color;
using GdiGraphics = System.Drawing.Graphics;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 在图像上建立世界坐标与像素坐标的对应：手动选点，或点击附近匹配检测点。
    /// </summary>
    public sealed class NinePointCorrespondenceDialog : Window
    {
        private const double HitRadiusPx = 18;
        private const double MinScale = 0.05;
        private const double MaxScale = 20;
        private const double NudgeStepPx = 1.0;
        private const double NudgeStepFinePx = 0.1;
        private const double NudgeStepCoarsePx = 10.0;

        private readonly CalibImage _image;
        private readonly Point2D[] _referenceImagePts;
        private readonly Point2D[] _worldPts;
        private readonly Point2D[] _probeWorldPts;
        private readonly int _gridRows;
        private readonly int _gridCols;
        private readonly bool _useProximityMatching;
        private readonly int _n;
        private readonly bool _manualPixelPick;
        private readonly Point2D?[] _pixelForWorld;

        private readonly ListBox _worldList;
        private readonly TextBlock _hintText;
        private readonly TextBlock _txtLiveCal;
        private readonly Image _gridLayer;
        private readonly TextBox _txtPixelX;
        private readonly TextBox _txtPixelY;
        private readonly StackPanel _pnlFineTune;
        private readonly Canvas _overlay;
        private readonly Border _viewHost;
        private readonly ScaleTransform _viewScale = new ScaleTransform();
        private readonly TranslateTransform _viewTranslate = new TranslateTransform();

        private int _activeWorldIndex;
        private double _scale = 1;
        private double _offsetX;
        private double _offsetY;
        private bool _isPanning;
        private bool _isDraggingPoint;
        private int _dragWorldIndex = -1;
        private Point _panStart;
        private Point _lastPanOffset;

        public Point2D[]? ResultImagePoints { get; private set; }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        /// <param name="referenceImagePts">检测点（可选）；手选模式下仅作参考显示与按序自动初值。</param>
        /// <param name="manualPixelPick">true=在图像任意位置点击取像素；false=须点在检测圆心附近。</param>
        /// <param name="probeWorldPts">探针世界点（可选）；不参与标定，实时反算并叠加显示为 P1…Pn。</param>
        public NinePointCorrespondenceDialog(
            CalibImage image,
            Point2D[]? referenceImagePts,
            Point2D[] worldPts,
            Window? owner,
            bool manualPixelPick = true,
            Point2D[]? probeWorldPts = null,
            int gridRows = 0,
            int gridCols = 0,
            bool useProximityMatching = false)
        {
            _image = image ?? throw new ArgumentNullException(nameof(image));
            _referenceImagePts = referenceImagePts ?? Array.Empty<Point2D>();
            _worldPts = worldPts ?? throw new ArgumentNullException(nameof(worldPts));
            _probeWorldPts = probeWorldPts ?? Array.Empty<Point2D>();
            _useProximityMatching = useProximityMatching;
            (_gridRows, _gridCols) = gridRows > 0 && gridCols > 0
                ? (gridRows, gridCols)
                : CalibrationPointGrid.InferLayout(worldPts.Length, worldPts);
            _n = worldPts.Length;
            if (_n < 4)
                throw new ArgumentException("标定至少需要 4 对世界/像素点");

            _manualPixelPick = manualPixelPick;
            _pixelForWorld = new Point2D?[_n];

            string gridTitle = CalibrationGridLayout.FormatDialogTitle(_gridRows, _gridCols);
            Title = manualPixelPick ? $"{gridTitle} — 手动选点" : $"{gridTitle} — 匹配检测点";
            Width = 1100;
            Height = 760;
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

            _txtLiveCal = new TextBlock
            {
                Text = "配对完成后将实时计算标定误差并叠加网格坐标系。",
                Foreground = Brushes.PaleGoldenrod,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(8, 0, 8, 8)
            };
            DockPanel.SetDock(_txtLiveCal, Dock.Top);
            left.Children.Add(_txtLiveCal);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 0, 8, 8)
            };
            var btnAuto = new Button { Content = "分行分列", Width = 72, Margin = new Thickness(0, 0, 6, 0) };
            var btnClear = new Button { Content = "清除", Width = 56, Margin = new Thickness(0, 0, 6, 0) };
            var btnUndo = new Button { Content = "撤销", Width = 56, Margin = new Thickness(0, 0, 6, 0) };
            btnAuto.Click += (_, _) => { ApplyBlXyAutoMapping(); RefreshAll(); };
            btnClear.Click += (_, _) => { ClearAssignments(); RefreshAll(); };
            btnUndo.Click += (_, _) => { UndoActiveWorld(); RefreshAll(); };
            btnRow.Children.Add(btnAuto);
            btnRow.Children.Add(btnClear);
            btnRow.Children.Add(btnUndo);
            DockPanel.SetDock(btnRow, Dock.Bottom);
            left.Children.Add(btnRow);

            _pnlFineTune = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };
            DockPanel.SetDock(_pnlFineTune, Dock.Bottom);
            left.Children.Add(_pnlFineTune);

            _pnlFineTune.Children.Add(new TextBlock
            {
                Text = "像素微调（选中已配对的点）",
                Foreground = Brushes.Silver,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4)
            });

            var xyRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            xyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            xyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            xyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            xyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            xyRow.RowDefinitions.Add(new RowDefinition());
            xyRow.RowDefinitions.Add(new RowDefinition());

            var lblX = new TextBlock { Text = "X", Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lblX, 0);
            Grid.SetColumn(lblX, 0);
            _txtPixelX = new TextBox { Height = 24, Margin = new Thickness(4, 0, 8, 4) };
            Grid.SetRow(_txtPixelX, 0);
            Grid.SetColumn(_txtPixelX, 1);
            var lblY = new TextBlock { Text = "Y", Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lblY, 0);
            Grid.SetColumn(lblY, 2);
            _txtPixelY = new TextBox { Height = 24, Margin = new Thickness(4, 0, 0, 4) };
            Grid.SetRow(_txtPixelY, 0);
            Grid.SetColumn(_txtPixelY, 3);
            xyRow.Children.Add(lblX);
            xyRow.Children.Add(_txtPixelX);
            xyRow.Children.Add(lblY);
            xyRow.Children.Add(_txtPixelY);
            _pnlFineTune.Children.Add(xyRow);

            void ApplyPixelFromTextBoxes()
            {
                if (_activeWorldIndex < 0 || _activeWorldIndex >= _n
                    || _pixelForWorld[_activeWorldIndex] == null)
                    return;
                if (!double.TryParse(_txtPixelX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                    || !double.TryParse(_txtPixelY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                    return;
                SetWorldPixel(_activeWorldIndex, x, y, refreshUi: true);
            }

            _txtPixelX.LostFocus += (_, _) => ApplyPixelFromTextBoxes();
            _txtPixelY.LostFocus += (_, _) => ApplyPixelFromTextBoxes();
            _txtPixelX.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { ApplyPixelFromTextBoxes(); e.Handled = true; }
            };
            _txtPixelY.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { ApplyPixelFromTextBoxes(); e.Handled = true; }
            };

            var nudgeRow1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
            var btnUp = new Button { Content = "↑", Width = 36, Height = 28, Margin = new Thickness(2) };
            btnUp.Click += (_, _) => NudgeActivePixel(0, -NudgeStepPx);
            nudgeRow1.Children.Add(btnUp);
            _pnlFineTune.Children.Add(nudgeRow1);

            var nudgeRow2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
            var btnLeft = new Button { Content = "←", Width = 36, Height = 28, Margin = new Thickness(2) };
            var btnDown = new Button { Content = "↓", Width = 36, Height = 28, Margin = new Thickness(2) };
            var btnRight = new Button { Content = "→", Width = 36, Height = 28, Margin = new Thickness(2) };
            btnLeft.Click += (_, _) => NudgeActivePixel(-NudgeStepPx, 0);
            btnDown.Click += (_, _) => NudgeActivePixel(0, NudgeStepPx);
            btnRight.Click += (_, _) => NudgeActivePixel(NudgeStepPx, 0);
            nudgeRow2.Children.Add(btnLeft);
            nudgeRow2.Children.Add(btnDown);
            nudgeRow2.Children.Add(btnRight);
            _pnlFineTune.Children.Add(nudgeRow2);

            _pnlFineTune.Children.Add(new TextBlock
            {
                Text = "方向键 ±1 px · Shift ±10 · Ctrl ±0.1\n可拖拽黄色当前点",
                Foreground = Brushes.Gray,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });

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
                RefreshFineTuneFields();
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
            var btnOk = new Button { Content = "确认完成", Width = 96, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
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
            _viewHost.KeyDown += ViewHost_KeyDown;
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

            _gridLayer = new Image
            {
                Width = _image.Width,
                Height = _image.Height,
                Stretch = Stretch.None,
                IsHitTestVisible = false
            };
            canvasRoot.Children.Add(_gridLayer);

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
                    ApplyBlXyAutoMapping();
                FitImageToView();
                RefreshAll();
            };
        }

        private string BuildHintText() =>
            _manualPixelPick
                ? "左侧选中世界点，在图像上左键点击取该点的像素坐标。\n" +
                  "已选点可拖拽或方向键微调；至少 4 对后实时显示标定网格与误差。\n" +
                  "浅蓝圆=检测参考；绿=已选；黄=当前。"
                : "左侧选中世界点，在图像上点击检测圆心附近。\n" +
                  "已配对点可拖拽或方向键微调；至少 4 对后实时显示标定网格与误差。\n" +
                  "默认按世界点分行分列匹配检测点（允许小幅偏差）。绿=已选，黄=当前，灰蓝=未配对；粉=P 探针反算点。确认无误后单击「确认完成」。";

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
            RefreshFineTuneFields();
            RefreshLiveCalibration();
            RefreshOverlay();
        }

        private int CountAssignedPoints()
        {
            int count = 0;
            for (int i = 0; i < _n; i++)
            {
                if (_pixelForWorld[i] != null)
                    count++;
            }

            return count;
        }

        private bool TryBuildCalibrationPairs(out Point2D[] imagePts, out Point2D[] worldPts, out bool allAssigned)
        {
            allAssigned = CountAssignedPoints() == _n;
            if (allAssigned)
            {
                imagePts = new Point2D[_n];
                for (int i = 0; i < _n; i++)
                    imagePts[i] = _pixelForWorld[i]!.Value;
                worldPts = _worldPts;
                return true;
            }

            var img = new List<Point2D>();
            var wld = new List<Point2D>();
            for (int i = 0; i < _n; i++)
            {
                if (_pixelForWorld[i] is Point2D p)
                {
                    img.Add(p);
                    wld.Add(_worldPts[i]);
                }
            }

            if (img.Count < 4)
            {
                imagePts = Array.Empty<Point2D>();
                worldPts = Array.Empty<Point2D>();
                return false;
            }

            imagePts = img.ToArray();
            worldPts = wld.ToArray();
            return true;
        }

        private void RefreshLiveCalibration()
        {
            int assigned = CountAssignedPoints();
            if (assigned < 4)
            {
                _gridLayer.Source = null;
                _txtLiveCal.Text = assigned == 0
                    ? "配对完成后将实时计算标定误差并叠加网格坐标系。"
                    : $"已配对 {assigned}/{_n}，至少 4 对后可预览标定。";
                _txtLiveCal.Foreground = Brushes.PaleGoldenrod;
                return;
            }

            if (!TryBuildCalibrationPairs(out var imagePts, out var worldPts, out bool allAssigned))
            {
                _gridLayer.Source = null;
                _txtLiveCal.Text = "点数不足，无法预览标定。";
                return;
            }

            var calResult = CalibAPI.CalibrateNinePoint(imagePts, worldPts);
            if (!calResult.Success)
            {
                _gridLayer.Source = null;
                _txtLiveCal.Text = $"标定计算失败：{calResult.ErrorMessage}";
                _txtLiveCal.Foreground = Brushes.OrangeRed;
                return;
            }

            var report = NinePointCalibrationQuality.Analyze(
                calResult.Transform,
                imagePts,
                worldPts,
                calResult.AverageError,
                calResult.MaxError);
            string brief = NinePointCalibrationQuality.BuildBriefSummary(report);
            string probeHint = _probeWorldPts.Length > 0 ? $" · 探针 {_probeWorldPts.Length} 点已反算" : "";
            _txtLiveCal.Text = allAssigned
                ? $"实时标定 · {brief}{probeHint}\n确认无误后单击「确认完成」。"
                : $"实时预览（{assigned}/{_n} 对）· {brief}{probeHint}";
            _txtLiveCal.Foreground = report.Passed ? Brushes.LightGreen : Brushes.Salmon;

            RefreshGridLayer(calResult.Transform, allAssigned ? _worldPts : worldPts,
                allAssigned ? BuildFullAlignedImagePoints() : imagePts);
        }

        private Point2D[] BuildFullAlignedImagePoints()
        {
            var pts = new Point2D[_n];
            for (int i = 0; i < _n; i++)
                pts[i] = _pixelForWorld[i]!.Value;
            return pts;
        }

        private void RefreshGridLayer(in AffineTransform transform, Point2D[] worldPts, Point2D[] imagePts)
        {
            var overlayBmp = new GdiBitmap(_image.Width, _image.Height, GdiPixelFormat.Format32bppArgb);
            using (var g = GdiGraphics.FromImage(overlayBmp))
            {
                g.Clear(GdiColor.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                AffineWorldGridOverlay.DrawOnGraphics(
                    g, transform, worldPts, _image.Width, _image.Height, imagePts);
                AffineWorldGridOverlay.DrawProbeWorldPointsOnGraphics(g, transform, _probeWorldPts);
            }

            IntPtr hBitmap = overlayBmp.GetHbitmap();
            try
            {
                var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmapSource.Freeze();
                _gridLayer.Source = bitmapSource;
            }
            finally
            {
                DeleteObject(hBitmap);
                overlayBmp.Dispose();
            }
        }

        private void RefreshFineTuneFields()
        {
            if (_activeWorldIndex < 0 || _activeWorldIndex >= _n
                || _pixelForWorld[_activeWorldIndex] is not Point2D p)
            {
                _pnlFineTune.IsEnabled = false;
                _txtPixelX.Text = "";
                _txtPixelY.Text = "";
                return;
            }

            _pnlFineTune.IsEnabled = true;
            _txtPixelX.Text = p.X.ToString("F2", CultureInfo.InvariantCulture);
            _txtPixelY.Text = p.Y.ToString("F2", CultureInfo.InvariantCulture);
        }

        private static Point2D ClampPixel(CalibImage image, double x, double y) =>
            new Point2D(
                Math.Max(0, Math.Min(image.Width - 1, x)),
                Math.Max(0, Math.Min(image.Height - 1, y)));

        private void SetWorldPixel(int worldIndex, double x, double y, bool refreshUi)
        {
            if (worldIndex < 0 || worldIndex >= _n)
                return;

            _pixelForWorld[worldIndex] = ClampPixel(_image, x, y);
            if (refreshUi)
                RefreshAll();
        }

        private void NudgeActivePixel(double dx, double dy)
        {
            if (_activeWorldIndex < 0 || _activeWorldIndex >= _n
                || _pixelForWorld[_activeWorldIndex] is not Point2D p)
                return;

            SetWorldPixel(_activeWorldIndex, p.X + dx, p.Y + dy, refreshUi: true);
            _viewHost.Focus();
        }

        private double ResolveNudgeStep(KeyEventArgs e)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                return NudgeStepFinePx;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                return NudgeStepCoarsePx;
            return NudgeStepPx;
        }

        private void ViewHost_KeyDown(object sender, KeyEventArgs e)
        {
            if (_activeWorldIndex < 0 || _pixelForWorld[_activeWorldIndex] == null)
                return;

            double step = ResolveNudgeStep(e);
            switch (e.Key)
            {
                case Key.Left:
                    NudgeActivePixel(-step, 0);
                    e.Handled = true;
                    break;
                case Key.Right:
                    NudgeActivePixel(step, 0);
                    e.Handled = true;
                    break;
                case Key.Up:
                    NudgeActivePixel(0, -step);
                    e.Handled = true;
                    break;
                case Key.Down:
                    NudgeActivePixel(0, step);
                    e.Handled = true;
                    break;
            }
        }

        private int HitTestAssignedPoint(Point imgPt, bool preferActive = true)
        {
            int best = -1;
            double bestD2 = HitRadiusPx * HitRadiusPx;

            if (preferActive && _activeWorldIndex >= 0 && _activeWorldIndex < _n
                && _pixelForWorld[_activeWorldIndex] is Point2D ap)
            {
                double dx = imgPt.X - ap.X;
                double dy = imgPt.Y - ap.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 <= bestD2)
                    return _activeWorldIndex;
            }

            for (int i = 0; i < _n; i++)
            {
                if (_pixelForWorld[i] is not Point2D p)
                    continue;
                double dx = imgPt.X - p.X;
                double dy = imgPt.Y - p.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 <= bestD2)
                {
                    bestD2 = d2;
                    best = i;
                }
            }

            return best;
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

        /// <summary>
        /// 世界点与像素点分行、分列匹配（允许小幅检测偏差）；失败时回退 bl_xy 图像排序。
        /// </summary>
        private void ApplyBlXyAutoMapping()
        {
            if (_referenceImagePts.Length != _n)
            {
                MessageBox.Show(this,
                    $"分行分列需要 {_n} 个检测参考点，当前为 {_referenceImagePts.Length} 个。请手动选点或调整检测输出。",
                    "分行分列", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ClearAssignments();
            int[] imageForWorld = NinePointGridCorrespondenceMatcher.Match(
                _worldPts, _referenceImagePts, _gridRows, _gridCols, _useProximityMatching);
            for (int i = 0; i < _n; i++)
                _pixelForWorld[i] = _referenceImagePts[imageForWorld[i]];
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
            int assignedHit = HitTestAssignedPoint(imgPt, preferActive: true);
            if (assignedHit >= 0)
            {
                _worldList.SelectedIndex = assignedHit;
                _activeWorldIndex = assignedHit;
                _isDraggingPoint = true;
                _dragWorldIndex = assignedHit;
                _viewHost.CaptureMouse();
                SetWorldPixel(_dragWorldIndex, imgPt.X, imgPt.Y, refreshUi: true);
                _viewHost.Focus();
                e.Handled = true;
                return;
            }

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
            if (_isDraggingPoint && _dragWorldIndex >= 0)
            {
                var imgPt = GetImagePointFromMouse(e);
                SetWorldPixel(_dragWorldIndex, imgPt.X, imgPt.Y, refreshUi: true);
                return;
            }

            if (!_isPanning) return;
            var current = e.GetPosition(_viewHost);
            _offsetX = _lastPanOffset.X + (current.X - _panStart.X);
            _offsetY = _lastPanOffset.Y + (current.Y - _panStart.Y);
            ApplyTransform();
        }

        private void ViewHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingPoint)
            {
                _isDraggingPoint = false;
                _dragWorldIndex = -1;
                _viewHost.ReleaseMouseCapture();
                return;
            }

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

            if (!ValidateCorrespondenceBeforeConfirm(out string? blockReason))
            {
                MessageBox.Show(this, blockReason, "标定", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!ConfirmIfOrderLikelyWrong())
                return;

            var result = new Point2D[_n];
            for (int i = 0; i < _n; i++)
                result[i] = _pixelForWorld[i]!.Value;
            ResultImagePoints = result;
            DialogResult = true;
            Close();
        }

        private bool ValidateCorrespondenceBeforeConfirm(out string? reason)
        {
            reason = null;
            var seen = new HashSet<(int X, int Y)>();
            for (int i = 0; i < _n; i++)
            {
                var p = _pixelForWorld[i]!.Value;
                var key = ((int)Math.Round(p.X), (int)Math.Round(p.Y));
                if (!seen.Add(key))
                {
                    reason = $"多个世界点指向同一像素位置 ({p.X:F0}, {p.Y:F0})，请重新分配。";
                    return false;
                }
            }

            return true;
        }

        /// <summary>与 bl_xy 按序自动对比，顺序明显不一致时二次确认，避免误标后界面长时间无响应。</summary>
        private bool ConfirmIfOrderLikelyWrong()
        {
            if (_referenceImagePts.Length != _n)
                return true;

            int[] imageForWorld = NinePointGridCorrespondenceMatcher.Match(
                _worldPts, _referenceImagePts, _gridRows, _gridCols, _useProximityMatching);

            int mismatches = 0;
            for (int i = 0; i < _n; i++)
            {
                var expected = _referenceImagePts[imageForWorld[i]];
                var actual = _pixelForWorld[i]!.Value;
                if (Math.Abs(expected.X - actual.X) > 1.5 || Math.Abs(expected.Y - actual.Y) > 1.5)
                    mismatches++;
            }

            if (mismatches < 2)
                return true;

            var answer = MessageBox.Show(this,
                $"检测到 {mismatches}/{_n} 个点的对应顺序与「按序自动」(分行分列) 不一致。\n" +
                "若顺序选错，标定误差会很大且后续对话框可能阻塞界面。\n\n" +
                "建议选择「否」后使用「分行分列」或逐点重新配对。\n\n仍要确定标定吗？",
                "顺序可能错误",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            return answer == MessageBoxResult.Yes;
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
