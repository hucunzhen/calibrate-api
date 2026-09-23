using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CalibOperatorCLI_Example
{
    internal sealed class AppRoleCredentialStore
    {
        private const string FileName = "app_role_credentials.json";

        public Dictionary<string, string> PasswordSha256Hex { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static AppRoleCredentialStore Load()
        {
            var loaded = AppUiSettingsStore.Load<AppRoleCredentialStore>(FileName);
            if (loaded.PasswordSha256Hex.Count == 0)
                loaded.EnsureFactoryDefaults();
            return loaded;
        }

        public void Save() => AppUiSettingsStore.Save(FileName, this);

        public void EnsureFactoryDefaults()
        {
            SetPassword(AppUserRole.Operator, "");
            SetPassword(AppUserRole.Process, "process");
            SetPassword(AppUserRole.Vision, "vision");
            SetPassword(AppUserRole.Admin, "admin");
            Save();
        }

        public bool Verify(AppUserRole role, string password)
        {
            string key = role.ToString();
            if (!PasswordSha256Hex.TryGetValue(key, out string? wantHex))
                return false;

            if (string.IsNullOrEmpty(wantHex))
                return string.IsNullOrEmpty(password);

            return string.Equals(wantHex, Hash(password), StringComparison.OrdinalIgnoreCase);
        }

        public void SetPassword(AppUserRole role, string password)
        {
            PasswordSha256Hex[role.ToString()] = string.IsNullOrEmpty(password) ? "" : Hash(password);
        }

        private static string Hash(string password)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password ?? ""));
            return Convert.ToHexString(bytes);
        }
    }
}
