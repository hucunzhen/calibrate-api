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

        private sealed class RoleItem
        {
            public RoleItem(AppUserRole role) => Role = role;
            public AppUserRole Role { get; }
            public override string ToString() => AppRolePermissions.DisplayName(Role);
        }
    }
}
