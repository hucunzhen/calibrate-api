using System;
using System.IO;
using System.Text.Json;

namespace CalibOperatorCLI_Example
{
    /// <summary>各页面 UI 配置 JSON 存于 %AppData%/IndustrialVisionTools/。</summary>
    internal static class AppUiSettingsStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static string PathFor(string fileName) =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppProduct.AppDataFolderName,
                fileName);

        public static T Load<T>(string fileName) where T : new()
        {
            try
            {
                string path = PathFor(fileName);
                if (!File.Exists(path))
                    return new T();
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), opts) ?? new T();
            }
            catch
            {
                return new T();
            }
        }

        public static void Save<T>(string fileName, T data)
        {
            try
            {
                string path = PathFor(fileName);
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOpts));
            }
            catch
            {
                // 非关键
            }
        }
    }
}
