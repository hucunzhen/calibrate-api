using System;

namespace CalibOperatorCLI_Example
{
    /// <summary>当前登录角色（进程内单例）。</summary>
    public sealed class AppSession
    {
        public static AppSession Current { get; } = new();

        private AppSession() { }

        public AppUserRole Role { get; private set; } = AppUserRole.Operator;

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

            Role = role;
            RoleChanged?.Invoke();
            return true;
        }

        /// <summary>回到操作员（无需密码，用于交班）。</summary>
        public void SignOutToOperator()
        {
            if (Role == AppUserRole.Operator)
                return;
            Role = AppUserRole.Operator;
            RoleChanged?.Invoke();
        }
    }
}
