using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 组态多标签宿主：每个标签一个 <see cref="FlowPage"/>，可同时编辑多个 .flow.json。
    /// </summary>
    public sealed partial class FlowHostPage : Page
    {
        public FlowHostPage()
        {
            InitializeComponent();
            AddEmptyTab(switchToSelected: true);
        }

        private void NewTabButton_Click(object sender, RoutedEventArgs e) =>
            AddEmptyTab(switchToSelected: true);

        /// <summary>当前选中的组态页。</summary>
        public FlowPage? ActiveFlowPage => (FlowTabs.SelectedItem as TabItem)?.Content as FlowPage;

        /// <summary>任一标签加载或保存路径变更时通知（路径为 null 表示清空为未命名）。</summary>
        public event Action<string?>? FlowLoaded;

        public FlowPage? ActiveFlowOrFirst()
        {
            if (ActiveFlowPage != null)
                return ActiveFlowPage;
            if (FlowTabs.Items.Count > 0 && FlowTabs.Items[0] is TabItem ti0 && ti0.Content is FlowPage fp0)
                return fp0;
            return null;
        }

        /// <summary>打开或切换到新标签并加载指定文件。</summary>
        public void OpenFlowInNewTab(string filePath)
        {
            var ti = AddEmptyTab(switchToSelected: true);
            if (ti.Content is FlowPage fp)
                fp.LoadFlowFromFile(filePath, showErrorDialog: true);
        }

        /// <summary>与原先单页逻辑一致：磁盘上有上次路径且当前活动页不是该文件时，加载到活动标签。</summary>
        public void TryAutoLoadLastFlowIfApplicable(string? lastPathCandidate)
        {
            if (string.IsNullOrWhiteSpace(lastPathCandidate) || !File.Exists(lastPathCandidate))
                return;
            string full = Path.GetFullPath(lastPathCandidate.Trim());
            var fp = ActiveFlowPage;
            if (fp == null)
                return;
            string current = fp.CurrentFlowFilePath ?? string.Empty;
            if (string.Equals(full, current, StringComparison.OrdinalIgnoreCase))
                return;
            fp.LoadFlowFromFile(full, showErrorDialog: false);
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
            fp.FlowLoaded += path =>
            {
                FlowLoaded?.Invoke(path);
                SetTabHeader(ownerTab, path);
            };
            fp.TryLoadFlowInNewTab = path =>
            {
                OpenFlowInNewTab(path);
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
        }
    }
}
