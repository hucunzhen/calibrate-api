using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CalibOperatorCLI_Example
{
    public partial class OperatorProductionPage : Page
    {
        private readonly MainWindow _shell;
        private readonly DispatcherTimer _statusTimer;

        public OperatorProductionPage(MainWindow shell)
        {
            _shell = shell;
            InitializeComponent();
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statusTimer.Tick += (_, _) => RefreshRunStatus();
            Loaded += (_, _) =>
            {
                RefreshCardUi();
                _statusTimer.Start();
            };
            Unloaded += (_, _) => _statusTimer.Stop();
            AppSession.Current.RoleChanged += OnRoleChanged;
            Unloaded += (_, _) => AppSession.Current.RoleChanged -= OnRoleChanged;
        }

        private void OnRoleChanged() => Dispatcher.Invoke(RefreshCardUi);

        public void RefreshCardUi()
        {
            TxtRoleHint.Text = $"当前用户：{AppSession.Current.RoleDisplayName}";

            string? recipe = _shell.GetSelectedRecipeName();
            if (string.IsNullOrWhiteSpace(recipe))
            {
                TxtProductName.Text = "未选择配方";
                TxtRecipeLine.Text = "请由工程师在「流程编排」中选择配方，或配置 flows 根目录。";
                TxtChangeoverStatus.Text = "换型状态：—";
                TxtChangeoverStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xB5));
                return;
            }

            var card = ProductRecipeCard.LoadForRecipe(recipe);
            TxtProductName.Text = string.IsNullOrWhiteSpace(card.DisplayName) ? recipe : card.DisplayName;
            TxtRecipeLine.Text = $"配方目录：{recipe}  ·  主流程：{card.MainFlowFile}  ·  版本：{(string.IsNullOrWhiteSpace(card.PublishedVersion) ? "—" : card.PublishedVersion)}";

            if (card.ReadyForOperatorProduction)
            {
                TxtChangeoverStatus.Text = "换型状态：已验收，允许量产";
                TxtChangeoverStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x3D, 0xAA, 0x6D));
            }
            else
            {
                TxtChangeoverStatus.Text = "换型状态：未完成 — 请工艺/视觉完成换型向导";
                TxtChangeoverStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xA2, 0x3C));
            }

            BtnChangeoverWizard.IsEnabled = AppRolePermissions.CanEditChangeover(AppSession.Current.Role);
        }

        private void RefreshRunStatus()
        {
            var fp = _shell.FlowHost.ActiveFlowOrFirst();
            if (fp == null)
            {
                TxtRunStatus.Text = "未加载流程";
                BtnStopVision.IsEnabled = false;
                return;
            }

            BtnStopVision.IsEnabled = fp.IsRunInProgress;
            if (fp.IsRunInProgress)
                TxtRunStatus.Text = "视觉流程运行中…";
        }

        private bool EnsureProductionAllowed()
        {
            string? recipe = _shell.GetSelectedRecipeName();
            if (string.IsNullOrWhiteSpace(recipe))
            {
                MessageBox.Show("未选择配方。", "量产", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var card = ProductRecipeCard.LoadForRecipe(recipe);
            if (!card.ReadyForOperatorProduction)
            {
                MessageBox.Show(
                    "当前配方尚未完成换型验收。\n请工艺/视觉工程师在「换型向导」中勾选阶段 0–6 并允许量产。",
                    "无法启动",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private async void BtnStartVision_Click(object sender, RoutedEventArgs e)
        {
            if (!AppRolePermissions.CanRunProduction(AppSession.Current.Role))
                return;

            if (!EnsureProductionAllowed())
                return;

            BtnStartVision.IsEnabled = false;
            try
            {
                TxtRunStatus.Text = "正在启动视觉主流程…";
                bool ok = await _shell.RunSelectedRecipeMainFlowAsync(preferNativeEngine: true);
                TxtRunStatus.Text = ok ? "本轮视觉流程已完成。" : "视觉流程未完成或已停止。";
            }
            catch (Exception ex)
            {
                TxtRunStatus.Text = "错误: " + ex.Message;
            }
            finally
            {
                BtnStartVision.IsEnabled = true;
                RefreshRunStatus();
            }
        }

        private void BtnStopVision_Click(object sender, RoutedEventArgs e)
        {
            _shell.FlowHost.StopActiveFlow();
            TxtRunStatus.Text = "已请求停止…";
        }

        private void BtnLoadMainFlow_Click(object sender, RoutedEventArgs e)
        {
            if (!_shell.FlowHost.TryLoadSelectedRecipeMainFlow(showErrors: true))
                return;
            TxtRunStatus.Text = "已加载主流程。";
        }

        private void BtnChangeoverWizard_Click(object sender, RoutedEventArgs e)
        {
            string? recipe = _shell.GetSelectedRecipeName();
            if (string.IsNullOrWhiteSpace(recipe))
            {
                MessageBox.Show("请先选择配方。", "换型向导", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool readOnly = !AppRolePermissions.CanEditChangeover(AppSession.Current.Role);
            var dlg = new ChangeoverWizardDialog(recipe, readOnly) { Owner = _shell };
            if (dlg.ShowDialog() == true)
                RefreshCardUi();
        }

        private void BtnPlcStatus_Click(object sender, RoutedEventArgs e) => _shell.NavigateToPlcReadOnlySummary();

        private void BtnResetHint_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "量产复位请按现场 SOP 操作 PLC。\n操作员勿擅自进入手动/标定模式。\n异常请联系视觉工程师。",
                "复位提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
