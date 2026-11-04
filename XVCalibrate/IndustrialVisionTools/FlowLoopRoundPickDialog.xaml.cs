using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CalibOperatorCLI_Example
{
    public enum FlowLoopRoundPickChoice
    {
        FinishLoop,
        RunSelected,
        RunAll
    }

    public sealed class FlowLoopRoundPickItem
    {
        public int Index { get; init; }
        public string Title { get; init; } = "";
        public string Details { get; init; } = "";
        public BitmapSource? Preview { get; init; }
    }

    public partial class FlowLoopRoundPickDialog : Window
    {
        private static readonly SolidColorBrush CardIdleBg = new(Color.FromRgb(0x2D, 0x2D, 0x30));
        private static readonly SolidColorBrush CardCheckedBg = new(Color.FromRgb(0x3A, 0x42, 0x4A));
        private static readonly SolidColorBrush CardIdleBorder = new(Color.FromRgb(0x3F, 0x3F, 0x46));
        private static readonly SolidColorBrush CardCheckedBorder = new(Color.FromRgb(0x4F, 0xC3, 0xF7));
        private static readonly SolidColorBrush DetailFg = new(Color.FromRgb(0xC8, 0xC8, 0xC8));
        private static readonly SolidColorBrush TitleFg = new(Color.FromRgb(0xF0, 0xF0, 0xF0));

        private readonly CheckBox[] _boxes;

        public FlowLoopRoundPickChoice Choice { get; private set; } = FlowLoopRoundPickChoice.FinishLoop;

        public int[] SelectedIndices { get; private set; } = Array.Empty<int>();

        public FlowLoopRoundPickDialog(IReadOnlyList<FlowLoopRoundPickItem> items, string? loopTitle)
        {
            InitializeComponent();
            int n = items?.Count ?? 0;
            if (!string.IsNullOrWhiteSpace(loopTitle))
                Title = $"循环「{loopTitle}」：选择要执行的轮次";
            TxtHint.Text = $"共 {n} 轮。每项显示该轮列表端口数据（图像缩略图、数值等）。勾选要执行的 Index（从 0 起，与循环 Index / BarId 相同）。「完成循环」跳过全部轮次，继续循环后的节点。";

            _boxes = new CheckBox[n];
            for (int i = 0; i < n; i++)
            {
                var item = items![i];
                var cb = new CheckBox
                {
                    IsChecked = true,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 10, 0),
                    Foreground = Brushes.White
                };
                _boxes[i] = cb;

                var title = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(item.Title) ? $"Index {item.Index}" : item.Title,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = TitleFg,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                var detail = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(item.Details) ? "(本轮无列表数据)" : item.Details,
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                    FontSize = 12.5,
                    Foreground = DetailFg,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18
                };
                var textCol = new StackPanel();
                textCol.Children.Add(title);
                textCol.Children.Add(detail);

                var row = new DockPanel();
                DockPanel.SetDock(cb, Dock.Left);
                row.Children.Add(cb);
                if (item.Preview != null)
                {
                    var img = new Image
                    {
                        Source = item.Preview,
                        Stretch = Stretch.Uniform,
                        MaxWidth = 132,
                        MaxHeight = 96,
                        Margin = new Thickness(0, 0, 12, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                        SnapsToDevicePixels = true
                    };
                    DockPanel.SetDock(img, Dock.Left);
                    row.Children.Add(img);
                }

                row.Children.Add(textCol);

                var border = new Border
                {
                    Child = row,
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 8),
                    CornerRadius = new CornerRadius(4),
                    Background = CardCheckedBg,
                    BorderBrush = CardCheckedBorder,
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                cb.Checked += (_, _) => ApplyCardChrome(border, true);
                cb.Unchecked += (_, _) => ApplyCardChrome(border, false);
                border.MouseLeftButtonUp += (_, e) =>
                {
                    if (e.OriginalSource is CheckBox)
                        return;
                    cb.IsChecked = cb.IsChecked != true;
                    e.Handled = true;
                };
                RoundHost.Children.Add(border);
            }
        }

        private static void ApplyCardChrome(Border border, bool checkedOn)
        {
            border.Background = checkedOn ? CardCheckedBg : CardIdleBg;
            border.BorderBrush = checkedOn ? CardCheckedBorder : CardIdleBorder;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var cb in _boxes)
                cb.IsChecked = true;
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var cb in _boxes)
                cb.IsChecked = false;
        }

        private void RunSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = CollectSelected();
            if (selected.Count == 0)
            {
                MessageBox.Show("请至少勾选一轮，或点「完成循环」跳过全部轮次。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SelectedIndices = selected.ToArray();
            Choice = selected.Count == _boxes.Length
                ? FlowLoopRoundPickChoice.RunAll
                : FlowLoopRoundPickChoice.RunSelected;
            DialogResult = true;
        }

        private void RunAll_Click(object sender, RoutedEventArgs e)
        {
            Choice = FlowLoopRoundPickChoice.RunAll;
            SelectedIndices = CollectAll();
            DialogResult = true;
        }

        private void Finish_Click(object sender, RoutedEventArgs e)
        {
            Choice = FlowLoopRoundPickChoice.FinishLoop;
            SelectedIndices = Array.Empty<int>();
            DialogResult = false;
        }

        private List<int> CollectSelected()
        {
            var list = new List<int>(_boxes.Length);
            for (int i = 0; i < _boxes.Length; i++)
            {
                if (_boxes[i].IsChecked == true)
                    list.Add(i);
            }

            return list;
        }

        private int[] CollectAll()
        {
            var all = new int[_boxes.Length];
            for (int i = 0; i < all.Length; i++)
                all[i] = i;
            return all;
        }
    }
}
