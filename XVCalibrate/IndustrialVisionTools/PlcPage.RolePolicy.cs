namespace CalibOperatorCLI_Example
{
    public partial class PlcPage
    {
        public void ApplyRolePolicy(AppUserRole role)
        {
            bool manual = AppRolePermissions.CanOpenPlcManual(role);
            if (TabPlcManualRun != null)
                TabPlcManualRun.IsEnabled = manual;
        }
    }
}
