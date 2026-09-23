namespace CalibOperatorCLI_Example
{
    /// <summary>激光焊产线软件角色（权限由 <see cref="AppRolePermissions"/> 定义）。</summary>
    public enum AppUserRole
    {
        Operator = 0,
        Process = 1,
        Vision = 2,
        Admin = 3,
    }

    internal static class AppRolePermissions
    {
        public static string DisplayName(AppUserRole role) => role switch
        {
            AppUserRole.Operator => "操作员",
            AppUserRole.Process => "工艺",
            AppUserRole.Vision => "视觉",
            AppUserRole.Admin => "管理员",
            _ => role.ToString(),
        };

        public static bool CanRunProduction(AppUserRole role) => true;

        public static bool CanOpenFlowEditor(AppUserRole role) =>
            role is AppUserRole.Vision or AppUserRole.Admin;

        public static bool CanOpenShapeTemplate(AppUserRole role) =>
            role is AppUserRole.Vision or AppUserRole.Admin;

        public static bool CanOpenPlcManual(AppUserRole role) =>
            role is AppUserRole.Process or AppUserRole.Vision or AppUserRole.Admin;

        public static bool CanManageRecipes(AppUserRole role) =>
            role is AppUserRole.Process or AppUserRole.Vision or AppUserRole.Admin;

        public static bool CanEditChangeover(AppUserRole role) =>
            role is AppUserRole.Process or AppUserRole.Vision or AppUserRole.Admin;

        public static bool CanOpenAdvancedTools(AppUserRole role) =>
            role is AppUserRole.Vision or AppUserRole.Admin;

        public static bool CanOpenLightController(AppUserRole role) =>
            role is AppUserRole.Vision or AppUserRole.Admin;

        public static bool CanConfigureRoles(AppUserRole role) => role == AppUserRole.Admin;
    }
}
