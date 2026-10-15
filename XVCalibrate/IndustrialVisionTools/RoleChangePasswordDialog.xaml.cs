using System.Windows;

namespace CalibOperatorCLI_Example
{
    public partial class RoleChangePasswordDialog : Window
    {
        public RoleChangePasswordDialog()
        {
            InitializeComponent();
            foreach (AppUserRole r in new[] { AppUserRole.Operator, AppUserRole.Process, AppUserRole.Vision, AppUserRole.Admin })
                CmbRole.Items.Add(new RoleItem(r));
            CmbRole.SelectedIndex = 0;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            TxtError.Visibility = Visibility.Collapsed;
            if (CmbRole.SelectedItem is not RoleItem item)
                return;

            if (TxtNew.Password != TxtConfirm.Password)
            {
                ShowError("两次输入的新密码不一致。");
                return;
            }

            if (!AppSession.Current.TryChangePassword(item.Role, TxtCurrent.Password, TxtNew.Password, out string error))
            {
                ShowError(error);
                return;
            }

            DialogResult = true;
        }

        private void ShowError(string message)
        {
            TxtError.Text = message;
            TxtError.Visibility = Visibility.Visible;
        }

        private sealed class RoleItem
        {
            public RoleItem(AppUserRole role) => Role = role;
            public AppUserRole Role { get; }
            public override string ToString() => AppRolePermissions.DisplayName(Role);
        }
    }
}
