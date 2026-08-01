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
        private bool _suppressRecipeChange;

        public FlowHostPage()
        {
            InitializeComponent();
            FlowTabs.SelectionChanged += (_, _) =>
            {
                if (!_suppressSessionNotify)
                {
                    OpenTabsChanged?.Invoke();
                    SyncRecipeFromActiveTab();
                }
                ActiveFlowPage?.ScheduleConnectionGeometryRefresh();
            };
            Loaded += (_, _) => ActiveFlowPage?.ScheduleConnectionGeometryRefresh();
        }

        /// <summary>当前选中的流程编排页。</summary>
        public FlowPage? ActiveFlowPage => (FlowTabs.SelectedItem as TabItem)?.Content as FlowPage;

        /// <summary>流程页入视觉树后刷新连线几何（默认加载时画布可能尚未量尺寸）。</summary>
        public void RefreshActiveFlowConnections() =>
            ActiveFlowOrFirst()?.ScheduleConnectionGeometryRefresh();

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
                SyncRecipeFromActiveTab();
                ActiveFlowPage?.ScheduleConnectionGeometryRefresh();
            }
            finally
            {
                _suppressSessionNotify = false;
            }
        }

        /// <summary>扫描 flows/ 子目录并填充配方下拉框。</summary>
        public void InitializeRecipes() => RefreshRecipeCombo(selectFromSettings: true);

        public void SyncRecipeFromActiveTab()
        {
            string? path = ActiveFlowPage?.CurrentFlowFilePath;
            string? detected = FlowRecipeCatalog.TryDetectRecipeNameFromFlowPath(path);
            if (string.IsNullOrEmpty(detected))
                return;

            _suppressRecipeChange = true;
            try
            {
                if (CmbRecipe.Items.Cast<object>().Any(i => string.Equals(i as string, detected, StringComparison.OrdinalIgnoreCase)))
                    CmbRecipe.SelectedItem = CmbRecipe.Items.Cast<object>()
                        .First(i => string.Equals(i as string, detected, StringComparison.OrdinalIgnoreCase));
                UpdateRecipePathHint(detected);
            }
            finally
            {
                _suppressRecipeChange = false;
            }
        }

        private void RefreshRecipeCombo(bool selectFromSettings)
        {
            var recipes = FlowRecipeCatalog.ListRecipes();
            string? selectName = selectFromSettings
                ? FlowRecipeCatalog.ResolveSelectedRecipeName(recipes)
                : CmbRecipe.SelectedItem as string;

            _suppressRecipeChange = true;
            try
            {
                CmbRecipe.Items.Clear();
                foreach (var r in recipes)
                    CmbRecipe.Items.Add(r.Name);

                if (recipes.Count == 0)
                {
                    UpdateFlowsRootHint(null);
                    CmbRecipe.IsEnabled = false;
                    BtnOpenRecipeMain.IsEnabled = false;
                    BtnOpenNinePointCalib.IsEnabled = false;
                    BtnOpenChessboardCalib.IsEnabled = false;
                    BtnCopyRecipe.IsEnabled = false;
                    BtnRenameRecipe.IsEnabled = false;
                    BtnDeleteRecipe.IsEnabled = false;
                    return;
                }

                CmbRecipe.IsEnabled = true;
                BtnOpenRecipeMain.IsEnabled = true;
                BtnOpenNinePointCalib.IsEnabled = true;
                BtnOpenChessboardCalib.IsEnabled = true;
                BtnCopyRecipe.IsEnabled = true;
                BtnRenameRecipe.IsEnabled = true;
                BtnDeleteRecipe.IsEnabled = true;

                string pick = !string.IsNullOrEmpty(selectName)
                    && recipes.Any(r => string.Equals(r.Name, selectName, StringComparison.OrdinalIgnoreCase))
                    ? recipes.First(r => string.Equals(r.Name, selectName, StringComparison.OrdinalIgnoreCase)).Name
                    : recipes[0].Name;

                CmbRecipe.SelectedItem = pick;
                UpdateRecipePathHint(pick);
            }
            finally
            {
                _suppressRecipeChange = false;
            }
        }

        private void UpdateRecipePathHint(string? recipeName)
        {
            UpdateFlowsRootHint(recipeName);
        }

        private void UpdateFlowsRootHint(string? recipeName)
        {
            string? root = FlowRecipeCatalog.TryFindFlowsRootDirectory();
            if (root == null)
            {
                TxtRecipeHint.Text = "未设置配方根目录，请点击「目录…」选择";
                return;
            }

            if (string.IsNullOrWhiteSpace(recipeName))
            {
                TxtRecipeHint.Text = $"根目录: {root}（未发现含 main.flow.json 的子文件夹）";
                return;
            }

            string? recipeDir = FlowRecipeCatalog.TryGetRecipeDirectory(recipeName);
            TxtRecipeHint.Text = recipeDir == null
                ? $"根目录: {root}"
                : $"{root}  →  {recipeDir}";
        }

        private void BtnSelectFlowsRoot_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择配方根目录（其下每个子文件夹为一个配方，如 v1、v2）",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            string? current = FlowRecipeCatalog.TryFindFlowsRootDirectory()
                ?? FlowRecipeCatalog.TryAutoDiscoverFlowsRootDirectory();
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                dlg.SelectedPath = current;

            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK
                || string.IsNullOrWhiteSpace(dlg.SelectedPath))
                return;

            if (!FlowRecipeCatalog.TrySetFlowsRootDirectory(dlg.SelectedPath, out string error))
            {
                MessageBox.Show(error, "配方根目录", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshRecipeCombo(selectFromSettings: true);
        }

        private void PersistSelectedRecipe(string recipeName)
        {
            var settings = FlowRecipeUiSettings.Load();
            settings.SelectedRecipe = recipeName;
            settings.Save();
        }

        private void LoadSelectedRecipeMainFlow(bool showErrors) =>
            LoadSelectedRecipeFlow(
                FlowRecipeCatalog.TryGetMainFlowPath,
                FlowRecipeCatalog.DefaultMainFlowFileName,
                showErrors);

        private void LoadSelectedRecipeNinePointCalibFlow(bool showErrors) =>
            LoadSelectedRecipeFlow(
                FlowRecipeCatalog.TryGetNinePointCalibFlowPath,
                $"{FlowRecipeCatalog.NinePointCalibFlowFileName} / {FlowRecipeCatalog.NinePointCalibFlowFileNameAlt}",
                showErrors);

        private void LoadSelectedRecipeChessboardCalibFlow(bool showErrors) =>
            LoadSelectedRecipeFlow(
                FlowRecipeCatalog.TryGetChessboardIntrinsicsFlowPath,
                FlowRecipeCatalog.ChessboardIntrinsicsFlowFileName,
                showErrors);

        private void LoadSelectedRecipeFlow(Func<string, string?> pathResolver, string flowLabel, bool showErrors)
        {
            string? name = CmbRecipe.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(name))
                return;

            string? flowPath = pathResolver(name);
            if (flowPath == null)
            {
                if (showErrors)
                {
                    MessageBox.Show(
                        $"未找到配方「{name}」的流程文件（{flowLabel}）。",
                        "配方",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                return;
            }

            LoadFlowPathInActiveTab(flowPath, showErrors);
        }

        private void LoadFlowPathInActiveTab(string flowPath, bool showErrors)
        {
            var fp = ActiveFlowOrFirst();
            if (fp == null)
            {
                OpenFlowInNewTab(flowPath);
                return;
            }

            fp.LoadFlowFromFile(flowPath, showErrorDialog: showErrors);
        }

        private void CmbRecipe_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressRecipeChange)
                return;

            string? name = CmbRecipe.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(name))
                return;

            PersistSelectedRecipe(name);
            UpdateRecipePathHint(name);
            LoadSelectedRecipeMainFlow(showErrors: true);
        }

        private void BtnOpenRecipeMain_Click(object sender, RoutedEventArgs e) =>
            LoadSelectedRecipeMainFlow(showErrors: true);

        private void BtnOpenNinePointCalib_Click(object sender, RoutedEventArgs e) =>
            LoadSelectedRecipeNinePointCalibFlow(showErrors: true);

        private void BtnOpenChessboardCalib_Click(object sender, RoutedEventArgs e) =>
            LoadSelectedRecipeChessboardCalibFlow(showErrors: true);

        private void BtnRefreshRecipes_Click(object sender, RoutedEventArgs e) =>
            RefreshRecipeCombo(selectFromSettings: false);

        private void BtnCopyRecipe_Click(object sender, RoutedEventArgs e)
        {
            string? sourceName = CmbRecipe.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(sourceName))
                return;

            string suggested = FlowRecipeCatalog.SuggestCopyRecipeName(sourceName);
            var dlg = new RecipeNameInputDialog($"复制配方「{sourceName}」为：", suggested)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() != true)
                return;

            var result = FlowRecipeCatalog.TryCopyRecipe(sourceName, dlg.RecipeName);
            if (!result.Success)
            {
                MessageBox.Show(result.Error ?? "复制失败。", "复制配方", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            RefreshRecipeCombo(selectFromSettings: false);
            SelectRecipeByName(result.RecipeName!, loadMainFlow: true);
        }

        private void BtnRenameRecipe_Click(object sender, RoutedEventArgs e)
        {
            string? oldName = CmbRecipe.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(oldName))
                return;

            string? oldDir = FlowRecipeCatalog.TryGetRecipeDirectory(oldName);
            if (oldDir == null)
            {
                MessageBox.Show($"未找到配方「{oldName}」。", "重命名配方", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new RecipeNameInputDialog($"重命名配方「{oldName}」为：", oldName, renameFromName: oldName)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() != true)
                return;

            string newName = dlg.RecipeName;
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                return;

            var result = FlowRecipeCatalog.TryRenameRecipe(oldName, newName);
            if (!result.Success)
            {
                MessageBox.Show(result.Error ?? "重命名失败。", "重命名配方", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string? newDir = FlowRecipeCatalog.TryGetRecipeDirectory(newName);
            if (newDir != null)
                RemapOpenTabsForRecipeRename(oldDir, newDir);

            RefreshRecipeCombo(selectFromSettings: false);
            SelectRecipeByName(newName, loadMainFlow: false);
            OpenTabsChanged?.Invoke();
        }

        private void RemapOpenTabsForRecipeRename(string oldRecipeDirectory, string newRecipeDirectory)
        {
            foreach (TabItem ti in FlowTabs.Items)
            {
                if (ti.Content is FlowPage fp)
                    fp.RemapFlowFilePathForRecipeRename(oldRecipeDirectory, newRecipeDirectory);
            }
        }

        private void BtnDeleteRecipe_Click(object sender, RoutedEventArgs e)
        {
            string? name = CmbRecipe.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(name))
                return;

            string? recipeDir = FlowRecipeCatalog.TryGetRecipeDirectory(name);
            if (recipeDir == null)
            {
                MessageBox.Show($"未找到配方「{name}」。", "删除配方", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int openTabCount = CountOpenTabsUnderRecipeDirectory(recipeDir);
            string message = openTabCount > 0
                ? $"确定删除配方「{name}」及其目录下全部文件？\n当前有 {openTabCount} 个标签正在使用该配方下的流程，删除后将关闭或清空这些标签。\n此操作不可撤销。"
                : $"确定删除配方「{name}」及其目录下全部文件？\n此操作不可撤销。";

            if (MessageBox.Show(message, "删除配方", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            CloseTabsUnderRecipeDirectory(recipeDir);

            var result = FlowRecipeCatalog.TryDeleteRecipe(name);
            if (!result.Success)
            {
                MessageBox.Show(result.Error ?? "删除失败。", "删除配方", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            RefreshRecipeCombo(selectFromSettings: true);
            if (CmbRecipe.SelectedItem is string selected)
            {
                PersistSelectedRecipe(selected);
                LoadSelectedRecipeMainFlow(showErrors: false);
            }
        }

        private void SelectRecipeByName(string recipeName, bool loadMainFlow)
        {
            _suppressRecipeChange = true;
            try
            {
                object? item = CmbRecipe.Items.Cast<object>()
                    .FirstOrDefault(i => string.Equals(i as string, recipeName, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                    CmbRecipe.SelectedItem = item;
            }
            finally
            {
                _suppressRecipeChange = false;
            }

            PersistSelectedRecipe(recipeName);
            UpdateRecipePathHint(recipeName);
            if (loadMainFlow)
                LoadSelectedRecipeMainFlow(showErrors: true);
        }

        private int CountOpenTabsUnderRecipeDirectory(string recipeDirectory)
        {
            int count = 0;
            foreach (TabItem ti in FlowTabs.Items)
            {
                if (ti.Content is FlowPage fp
                    && FlowRecipeCatalog.IsPathUnderRecipeDirectory(fp.CurrentFlowFilePath, recipeDirectory))
                    count++;
            }

            return count;
        }

        private void CloseTabsUnderRecipeDirectory(string recipeDirectory)
        {
            var toClose = new List<TabItem>();
            foreach (TabItem ti in FlowTabs.Items)
            {
                if (ti.Content is FlowPage fp
                    && FlowRecipeCatalog.IsPathUnderRecipeDirectory(fp.CurrentFlowFilePath, recipeDirectory))
                    toClose.Add(ti);
            }

            if (toClose.Count == 0)
                return;

            _suppressSessionNotify = true;
            try
            {
                foreach (TabItem ti in toClose)
                {
                    if (FlowTabs.Items.Count <= 1)
                    {
                        if (ti.Content is FlowPage fp)
                            fp.ResetToEmptyDocument();
                        break;
                    }

                    CloseTab(ti);
                }
            }
            finally
            {
                _suppressSessionNotify = false;
                OpenTabsChanged?.Invoke();
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
                {
                    FlowLoaded?.Invoke(path);
                    SyncRecipeFromActiveTab();
                }
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

            string tabToolTip = string.IsNullOrWhiteSpace(flowPath)
                ? "尚未保存的流程"
                : Path.GetFullPath(flowPath.Trim());

            var headerPanel = new DockPanel { LastChildFill = true, ToolTip = tabToolTip };

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
                FontWeight = FontWeights.Bold,
                ToolTip = tabToolTip
            };

            headerPanel.Children.Add(closeBtn);
            headerPanel.Children.Add(titleTb);

            ti.Header = headerPanel;
            ti.ToolTip = tabToolTip;
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
