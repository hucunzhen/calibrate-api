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

    public sealed class FlowLoopRoundPickDisplayChoice
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public override string ToString() => string.IsNullOrWhiteSpace(Label) ? Key : Label;
    }

    public sealed class FlowLoopRoundPickItem
    {
        public int Index { get; init; }
        public string Title { get; init; } = "";
        public string Details { get; init; } = "";
        public BitmapSource? Preview { get; init; }
        public IReadOnlyDictionary<string, BitmapSource?> PreviewsByKey { get; init; } =
            new Dictionary<string, BitmapSource?>();
        public IReadOnlyDictionary<string, string> DetailsByKey { get; init; } =
            new Dictionary<string, string>();
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
        private readonly Image[] _previewImages;
        private readonly TextBlock[] _detailBlocks;
        private readonly IReadOnlyList<FlowLoopRoundPickItem> _items;

        public FlowLoopRoundPickChoice Choice { get; private set; } = FlowLoopRoundPickChoice.FinishLoop;

        public int[] SelectedIndices { get; private set; } = Array.Empty<int>();

        public FlowLoopRoundPickDialog(
            IReadOnlyList<FlowLoopRoundPickItem> items,
            string? loopTitle,
            IReadOnlyList<FlowLoopRoundPickDisplayChoice>? displayChoices = null,
            string? preferredDisplayKey = null,
            bool preselectAll = true)
        {
            InitializeComponent();
            _items = items ?? Array.Empty<FlowLoopRoundPickItem>();
            int n = _items.Count;
            if (!string.IsNullOrWhiteSpace(loopTitle))
                Title = $"循环「{loopTitle}」：选择要执行的轮次";
            TxtHint.Text = $"共 {n} 轮。勾选后点「执行所选」或「全部执行」，跑完会再次弹出，可重复勾选同一 Index。点「完成循环」才结束选轮，继续后面的节点。";

            BindDisplayChoices(displayChoices, preferredDisplayKey);

            _boxes = new CheckBox[n];
            _previewImages = new Image[n];
            _detailBlocks = new TextBlock[n];
            for (int i = 0; i < n; i++)
            {
                var item = _items[i];
                var cb = new CheckBox
                {
                    IsChecked = preselectAll,
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
                _detailBlocks[i] = detail;
                var textCol = new StackPanel();
                textCol.Children.Add(title);
                textCol.Children.Add(detail);

                var row = new DockPanel();
                DockPanel.SetDock(cb, Dock.Left);
                row.Children.Add(cb);
                var img = new Image
                {
                    Source = item.Preview,
                    Stretch = Stretch.Uniform,
                    MaxWidth = 132,
                    MaxHeight = 96,
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    SnapsToDevicePixels = true,
                    Visibility = item.Preview != null ? Visibility.Visible : Visibility.Collapsed
                };
                _previewImages[i] = img;
                DockPanel.SetDock(img, Dock.Left);
                row.Children.Add(img);

                row.Children.Add(textCol);

                var border = new Border
                {
                    Child = row,
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 8),
                    CornerRadius = new CornerRadius(4),
                    Background = preselectAll ? CardCheckedBg : CardIdleBg,
                    BorderBrush = preselectAll ? CardCheckedBorder : CardIdleBorder,
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

            ApplySelectedDisplayInput();
        }

        private void BindDisplayChoices(
            IReadOnlyList<FlowLoopRoundPickDisplayChoice>? displayChoices,
            string? preferredDisplayKey)
        {
            if (displayChoices == null || displayChoices.Count == 0)
            {
                DisplayInputRow.Visibility = Visibility.Collapsed;
                return;
            }

            DisplayInputRow.Visibility = Visibility.Visible;
            CmbDisplayInput.ItemsSource = displayChoices;
            FlowLoopRoundPickDisplayChoice? selected = null;
            if (!string.IsNullOrWhiteSpace(preferredDisplayKey))
            {
                foreach (var c in displayChoices)
                {
                    if (string.Equals(c.Key, preferredDisplayKey, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = c;
                        break;
                    }
                }
            }

            CmbDisplayInput.SelectedItem = selected ?? displayChoices[0];
            CmbDisplayInput.SelectionChanged += (_, _) => ApplySelectedDisplayInput();
        }

        private void ApplySelectedDisplayInput()
        {
            if (_previewImages == null || _detailBlocks == null)
                return;

            string key = "";
            bool showAll = true;
            if (CmbDisplayInput.SelectedItem is FlowLoopRoundPickDisplayChoice choice)
            {
                key = choice.Key ?? "";
                showAll = string.IsNullOrWhiteSpace(key);
            }

            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                BitmapSource? src = null;
                string details;
                if (showAll)
                {
                    details = item.Details;
                    src = item.Preview;
                    if (src == null && item.PreviewsByKey != null)
                    {
                        foreach (var kv in item.PreviewsByKey)
                        {
                            if (kv.Value != null)
                            {
                                src = kv.Value;
                                break;
                            }
                        }
                    }
                }
                else
                {
                    if (item.DetailsByKey == null || !item.DetailsByKey.TryGetValue(key, out details!)
                        || string.IsNullOrWhiteSpace(details))
                        details = "(本轮无此输入)";
                    if (item.PreviewsByKey != null)
                        item.PreviewsByKey.TryGetValue(key, out src);
                }

                if (i < _detailBlocks.Length && _detailBlocks[i] != null)
                    _detailBlocks[i].Text = details;
                if (i < _previewImages.Length && _previewImages[i] != null)
                {
                    _previewImages[i].Source = src;
                    _previewImages[i].Visibility = src != null ? Visibility.Visible : Visibility.Collapsed;
                }
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
                MessageBox.Show("请至少勾选一轮，或点「完成循环」结束选轮。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
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
