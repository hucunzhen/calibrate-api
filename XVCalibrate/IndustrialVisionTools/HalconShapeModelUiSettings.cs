using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CalibOperatorCLI_Example
{
    internal sealed class HalconShapeModelRoiPathDto
    {
        public List<double> VertexXs { get; set; } = new();
        public List<double> VertexYs { get; set; } = new();
        public List<string> EdgeKinds { get; set; } = new();
        public List<double?> ArcViaXs { get; set; } = new();
        public List<double?> ArcViaYs { get; set; } = new();
        public bool IsClosed { get; set; }
    }

    internal sealed class HalconShapeModelUiSettings
    {
        public string ImagePath { get; set; } = "";
        public string CalibrationJsonPath { get; set; } = "";
        /// <summary>none | rect | circle | polygon | ring</summary>
        public string RoiMode { get; set; } = "none";

        public double RoiRectX { get; set; }
        public double RoiRectY { get; set; }
        public double RoiRectW { get; set; }
        public double RoiRectH { get; set; }

        public double CircleCenterX { get; set; }
        public double CircleCenterY { get; set; }
        public double CircleRadius { get; set; }

        public HalconShapeModelRoiPathDto? PolygonPath { get; set; }
        public HalconShapeModelRoiPathDto? RingOuterPath { get; set; }
        public HalconShapeModelRoiPathDto? RingInnerPath { get; set; }

        private static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppProduct.AppDataFolderName,
            "halcon_shape_model_ui.json");

        public static string HintPath => ConfigPath;

        public static HalconShapeModelUiSettings Load()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                    return new HalconShapeModelUiSettings();
                string json = File.ReadAllText(ConfigPath);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<HalconShapeModelUiSettings>(json, opts)
                       ?? new HalconShapeModelUiSettings();
            }
            catch
            {
                return new HalconShapeModelUiSettings();
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
                // 非关键
            }
        }
    }
}
