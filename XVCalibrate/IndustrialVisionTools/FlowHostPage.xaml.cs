using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 流程编排多标签宿主：每个标签一个 <see cref="FlowPage"/>，可同时编辑多个 .flow.json。
    /// </summary>
    public sealed partial class FlowHostPage : Page
    {
        private bool _suppressSessionNotify;

        public FlowHostPage()
        {
            InitializeComponent();
            FlowTabs.SelectionChanged += (_, _) =>
            {
                if (!_suppressSessionNotify)
                    OpenTabsChanged?.Invoke();
            };
        }

        /// <summary>当前选中的流程编排页。</summary>
        public FlowPage? ActiveFlowPage => (FlowTabs.SelectedItem as TabItem)?.Content as FlowPage;

        /// <summary>任一标签加载或保存路径变更时通知（路径为 null 表示清空为未命名）。</summary>
        public event Action<string?>? FlowLoaded;

        /// <summary>标签集合或当前选中标签变更（用于持久化打开的文件列表）。</summary>
        public event Action? OpenTabsChanged;

        public readonly record struct FlowTabsSessionSnapshot(IReadOnlyList<string?> Tabs, int ActiveIndex);

        public FlowPage? ActiveFlowOrFirst()
        {
            if (ActiveFlowPage != null)
                return ActiveFlowPage;
            if (FlowTabs.Items.Count > 0 && FlowTabs.Items[0] is TabItem ti0 && ti0.Content is FlowPage fp0)
                return fp0;
            return null;
        }

        /// <summary>启动时恢复上次打开的全部流程标签；无记录时保留一个空白标签。</summary>
        public void RestoreOpenFlows(IReadOnlyList<string?>? tabPaths, int activeIndex)
        {
            _suppressSessionNotify = true;
            try
            {
                FlowTabs.Items.Clear();
                var paths = tabPaths?.ToList() ?? new List<string?>();
                if (paths.Count == 0)
                {
                    AddEmptyTab(switchToSelected: true);
                    return;
                }

                int loaded = 0;
                foreach (string? raw in paths)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        AddEmptyTab(switchToSelected: false);
                        loaded++;
                        continue;
                    }

                    string full = Path.GetFullPath(raw.Trim());
                    if (!File.Exists(full))
                        continue;

                    var ti = AddEmptyTab(switchToSelected: false);
                    if (ti.Content is FlowPage fp)
                        fp.LoadFlowFromFile(full, showErrorDialog: false);
                    loaded++;
                }

                if (loaded == 0)
                {
                    AddEmptyTab(switchToSelected: true);
                    return;
                }

                int idx = Math.Clamp(activeIndex, 0, FlowTabs.Items.Count - 1);
                FlowTabs.SelectedIndex = idx;
            }
            finally
            {
                _suppressSessionNotify = false;
            }
        }

        public FlowTabsSessionSnapshot GetSessionSnapshot()
        {
            var tabs = new List<string?>();
            foreach (TabItem ti in FlowTabs.Items)
            {
                if (ti.Content is FlowPage fp)
                {
                    tabs.Add(string.IsNullOrWhiteSpace(fp.CurrentFlowFilePath)
                        ? null
                        : Path.GetFullPath(fp.CurrentFlowFilePath.Trim()));
                }
            }

            int activeIndex = 0;
            if (FlowTabs.SelectedItem is TabItem sel)
                activeIndex = FlowTabs.Items.IndexOf(sel);
            if (activeIndex < 0)
                activeIndex = 0;
            return new FlowTabsSessionSnapshot(tabs, activeIndex);
        }

        /// <summary>打开或切换到新标签并加载指定文件。</summary>
        public void OpenFlowInNewTab(string filePath)
        {
            var ti = AddEmptyTab(switchToSelected: true);
            if (ti.Content is FlowPage fp)
                fp.LoadFlowFromFile(filePath, showErrorDialog: true);
        }

        private TabItem AddEmptyTab(bool switchToSelected)
        {
            var ti = new TabItem();
            var fp = WireFlowPage(ti);
            ti.Content = fp;
            SetTabHeader(ti, null);
            FlowTabs.Items.Add(ti);
            if (switchToSelected)
                FlowTabs.SelectedItem = ti;
            return ti;
        }

        private FlowPage WireFlowPage(TabItem ownerTab)
        {
            var fp = new FlowPage();
            fp.Loaded += (_, _) => fp.ApplyToolbarDefaultsFromStore();
            fp.FlowLoaded += path =>
            {
                SetTabHeader(ownerTab, path);
                if (!_suppressSessionNotify)
                    OpenTabsChanged?.Invoke();
                if (ReferenceEquals(FlowTabs.SelectedItem, ownerTab))
                    FlowLoaded?.Invoke(path);
            };
            fp.TryLoadFlowInNewTab = path =>
            {
                OpenFlowInNewTab(path);
                return true;
            };
            fp.RequestNewEmptyFlowTab = () =>
            {
                AddEmptyTab(switchToSelected: true);
                OpenTabsChanged?.Invoke();
                return true;
            };
            return fp;
        }

        private void SetTabHeader(TabItem ti, string? flowPath)
        {
            string name = string.IsNullOrWhiteSpace(flowPath)
                ? "未命名"
                : Path.GetFileName(flowPath.Trim());

            var headerPanel = new DockPanel { LastChildFill = true };

            var closeBtn = new Button
            {
                Content = "×",
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "关闭标签",
                Cursor = System.Windows.Input.Cursors.Hand
            };
            closeBtn.Click += (_, e) =>
            {
                e.Handled = true;
                this.CloseTab(ti);
            };
            DockPanel.SetDock(closeBtn, Dock.Right);

            var titleTb = new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold
            };

            headerPanel.Children.Add(closeBtn);
            headerPanel.Children.Add(titleTb);

            ti.Header = headerPanel;
        }

        private void CloseTab(TabItem ti)
        {
            if (!FlowTabs.Items.Contains(ti))
                return;

            if (FlowTabs.Items.Count <= 1)
            {
                if (ti.Content is FlowPage fp)
                    fp.ResetToEmptyDocument();
                return;
            }

            int idx = FlowTabs.Items.IndexOf(ti);
            bool wasSelected = ReferenceEquals(FlowTabs.SelectedItem, ti);
            FlowTabs.Items.Remove(ti);

            if (wasSelected && FlowTabs.Items.Count > 0)
            {
                int newIdx = Math.Min(Math.Max(0, idx), FlowTabs.Items.Count - 1);
                FlowTabs.SelectedIndex = newIdx;
            }

            OpenTabsChanged?.Invoke();
        }
    }
}
