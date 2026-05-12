using System;
using System.IO;
using System.Text.Json;

namespace CalibOperatorCLI_Example
{
    internal sealed class HalconDlSegUiSettings
    {
        public string DatasetRoot { get; set; } = "";
        public string ExportRoot { get; set; } = "";
        public string ClassNames { get; set; } = "object";
        public string TrainPretrainedHdl { get; set; } = "";
        public string TrainOutputHdl { get; set; } = "";
        public string TrainEpochs { get; set; } = "5";
        public string TrainBatch { get; set; } = "2";
        public string TrainLr { get; set; } = "0.001";
        public int TrainRuntimeIndex { get; set; }

        /// <summary>true：训练 image 使用 real [0,1]；false：byte（默认，与推理路径一致）。</summary>
        public bool TrainImageNormalizeReal01 { get; set; }
        public string ModelHdl { get; set; } = "";
        public string TestImage { get; set; } = "";

        private static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppProduct.AppDataFolderName,
            "halcon_dl_seg_ui.json");

        public static HalconDlSegUiSettings Load()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                    return new HalconDlSegUiSettings();
                string json = File.ReadAllText(ConfigPath);
                var s = JsonSerializer.Deserialize<HalconDlSegUiSettings>(json);
                return s ?? new HalconDlSegUiSettings();
            }
            catch
            {
                return new HalconDlSegUiSettings();
            }
        }

        public void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, opts));
            }
            catch
            {
                /* 非关键 */
            }
        }
    }
}
