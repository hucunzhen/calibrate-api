using System;

namespace CalibOperatorCLI_Example
{
    /// <summary>当前登录角色落盘（%AppData%/IndustrialVisionTools/）。不存密码。</summary>
    internal sealed class AppSessionUiSettings
    {
        public const string FileName = "app_session_ui.json";

        /// <summary><see cref="AppUserRole"/> 枚举名。</summary>
        public string Role { get; set; } = nameof(AppUserRole.Operator);

        public static AppSessionUiSettings Load() => AppUiSettingsStore.Load<AppSessionUiSettings>(FileName);

        public void Save() => AppUiSettingsStore.Save(FileName, this);

        public static AppUserRole ReadPersistedRole()
        {
            string raw = Load().Role?.Trim() ?? "";
            if (Enum.TryParse(raw, ignoreCase: true, out AppUserRole role)
                && Enum.IsDefined(typeof(AppUserRole), role))
                return role;

            return AppUserRole.Operator;
        }

        public static void WritePersistedRole(AppUserRole role)
        {
            new AppSessionUiSettings { Role = role.ToString() }.Save();
        }
    }
}
