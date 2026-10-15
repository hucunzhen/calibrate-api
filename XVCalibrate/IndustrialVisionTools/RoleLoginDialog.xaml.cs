using System.Linq;
using System.Windows;

namespace CalibOperatorCLI_Example
{
    public partial class RoleLoginDialog : Window
    {
        public AppUserRole SelectedRole { get; private set; } = AppUserRole.Operator;

        public RoleLoginDialog()
        {
            InitializeComponent();
            foreach (AppUserRole r in new[] { AppUserRole.Operator, AppUserRole.Process, AppUserRole.Vision, AppUserRole.Admin })
                CmbRole.Items.Add(new RoleItem(r));
            CmbRole.SelectedIndex = 0;
            UpdatePasswordHint();
        }

        private void CmbRole_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            TxtPassword.Clear();
            UpdatePasswordHint();
        }

        private void TxtPassword_PasswordChanged(object sender, RoutedEventArgs e) => UpdatePasswordHint();

        private void UpdatePasswordHint()
        {
            AppUserRole role = CmbRole.SelectedItem is RoleItem item ? item.Role : AppUserRole.Operator;
            TxtPasswordHint.Text = AppRoleCredentialStore.FactoryDefaultPasswordHint(role);
            TxtPasswordHint.Visibility = string.IsNullOrEmpty(TxtPassword.Password)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            TxtError.Visibility = Visibility.Collapsed;
            if (CmbRole.SelectedItem is not RoleItem item)
                return;

            string pwd = TxtPassword.Password;
            if (!AppSession.Current.TrySignIn(item.Role, pwd, out string error))
            {
                TxtError.Text = error;
                TxtError.Visibility = Visibility.Visible;
                return;
            }

            SelectedRole = item.Role;
            DialogResult = true;
        }

        private void ChangePassword_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new RoleChangePasswordDialog { Owner = this };
            dlg.ShowDialog();
        }

        private sealed class RoleItem
        {
            public RoleItem(AppUserRole role) => Role = role;
            public AppUserRole Role { get; }
            public override string ToString() => AppRolePermissions.DisplayName(Role);
        }
    }
}
