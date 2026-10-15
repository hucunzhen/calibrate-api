using System;

namespace CalibOperatorCLI_Example
{
    /// <summary>当前登录角色（进程内单例）。</summary>
    public sealed class AppSession
    {
        public static AppSession Current { get; } = new();

        private AppSession()
        {
            Role = AppSessionUiSettings.ReadPersistedRole();
        }

        public AppUserRole Role { get; private set; }

        public string RoleDisplayName => AppRolePermissions.DisplayName(Role);

        public event Action? RoleChanged;

        public bool TrySignIn(AppUserRole role, string password, out string error)
        {
            error = "";
            var store = AppRoleCredentialStore.Load();
            if (!store.Verify(role, password ?? ""))
            {
                error = "密码错误。";
                return false;
            }

            SetRole(role);
            return true;
        }

        public bool TryChangePassword(AppUserRole role, string currentPassword, string newPassword, out string error) =>
            AppRoleCredentialStore.Load().TryChangePassword(role, currentPassword ?? "", newPassword ?? "", out error);

        /// <summary>回到操作员（无需密码，用于交班）。</summary>
        public void SignOutToOperator() => SetRole(AppUserRole.Operator);

        private void SetRole(AppUserRole role)
        {
            bool changed = Role != role;
            Role = role;
            AppSessionUiSettings.WritePersistedRole(role);
            if (changed)
                RoleChanged?.Invoke();
        }
    }
}
