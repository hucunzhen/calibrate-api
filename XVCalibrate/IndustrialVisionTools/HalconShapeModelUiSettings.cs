using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CalibOperatorCLI_Example
{
    internal sealed class HalconShapeModelRoiConnectorDto
    {
        public double StartX { get; set; }
        public double StartY { get; set; }
        public double EndX { get; set; }
        public double EndY { get; set; }
    }

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

        /// <summary>九点标定结果 calibration_result.json（含 affine，用于几何测量 mm 显示）。</summary>
        public string NinePointCalibrationPath { get; set; } = "";
        /// <summary>none | rect | rotatedrect | circle | polygon | ring</summary>
        public string RoiMode { get; set; } = "none";

        public double RoiRectX { get; set; }
        public double RoiRectY { get; set; }
        public double RoiRectW { get; set; }
        public double RoiRectH { get; set; }

        public double RotRectCenterX { get; set; }
        public double RotRectCenterY { get; set; }
        public double RotRectWidth { get; set; }
        public double RotRectHeight { get; set; }
        public double RotRectAngleDeg { get; set; }

        public double CircleCenterX { get; set; }
        public double CircleCenterY { get; set; }
        public double CircleRadius { get; set; }

        public HalconShapeModelRoiPathDto? PolygonPath { get; set; }
        public HalconShapeModelRoiPathDto? RingOuterPath { get; set; }
        public HalconShapeModelRoiPathDto? RingInnerPath { get; set; }

        /// <summary>已完成的多条开放轨迹（不闭合）。</summary>
        public List<HalconShapeModelRoiPathDto> OpenTrajectoryPaths { get; set; } = new();

        /// <summary>与 <see cref="OpenTrajectoryPaths"/> 同序的显示名称。</summary>
        public List<string> OpenTrajectoryNames { get; set; } = new();

        /// <summary>上次保存/加载的 ROI 快照文件路径。</summary>
        public string LastRoiFile { get; set; } = "";

        /// <summary>旧会话字段，读取时回退到 <see cref="LastRoiFile"/>。</summary>
        public string LastOpenTrajectoryFile { get; set; } = "";

        /// <summary>当前正在绘制的开放轨迹草稿。</summary>
        public HalconShapeModelRoiPathDto? OpenTrajectoryDraft { get; set; }

        /// <summary>开放轨迹之间的连接线（不参与模板）。</summary>
        public List<HalconShapeModelRoiConnectorDto> OpenTrajectoryConnectors { get; set; } = new();

        /// <summary>相机矫正：内参去畸变。</summary>
        public bool EnableUndistort { get; set; }

        public string UndistortAlpha { get; set; } = "-1";

        /// <summary>相机矫正：棋盘平面透视。</summary>
        public bool EnablePerspective { get; set; }

        public int CalibViewIndex { get; set; }

        public int CalibBoardCols { get; set; } = ChessboardCalibrationDefaults.InnerCornerCols;

        public int CalibBoardRows { get; set; } = ChessboardCalibrationDefaults.InnerCornerRows;

        public string CalibSquareSizeMm { get; set; } = ChessboardCalibrationDefaults.SquareSizeMm.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public string CalibPxPerMm { get; set; } = "32";

        /// <summary>board | local | plane，对应透视输出范围 ComboBox Tag。</summary>
        public string PerspectiveOutputFrame { get; set; } = ChessboardCalibrationDefaults.PerspectiveOutputFrame;

        /// <summary>矫正后图像顺时针旋转角度(°)，0/90/180/270 或任意角度。</summary>
        public double PostCorrectRotateDeg { get; set; }

        /// <summary>非 90° 整数倍旋转时扩大画布以容纳整图。</summary>
        public bool RotateExpandCanvas { get; set; }

        /// <summary>海康相机枚举索引（摄像头取一帧）。</summary>
        public string CameraDeviceIndex { get; set; } = "0";

        /// <summary>模板创建/匹配等侧栏控件（按 x:Name 序列化）。</summary>
        public Dictionary<string, string> Controls { get; set; } = new();

        private static readonly string[] PersistControlNames =
        {
            "CmbTemplateSource", "CmbModelKind", "CmbGenContourMode", "TxtMinContourPoints", "ChkLargestContourOnly",
            "CmbTrimMode", "TxtTrimEpsilon", "TxtTrimEndsPx", "TxtTrimMinLength", "ChkTrimClosed",
            "RbGradientOutward", "RbGradientInward", "CmbRingInnerGradientMode",
            "RbInnerGradientOutward", "RbInnerGradientInward", "RbNextSegmentLine", "RbNextSegmentArc", "ChkTangentJoin",
            "TxtEdgeAlpha", "TxtEdgeLow", "TxtEdgeHigh", "TxtScaleMin", "TxtScaleMax", "TxtScaleStep",
            "TxtMinGray", "TxtMaxGray", "TxtPolygonCloseDist", "TxtCameraDeviceIndex",
            "TxtNumLevels", "TxtAngleStart", "TxtAngleExtent", "TxtAngleStep", "TxtContrast", "TxtMinContrast",
            "CmbOptimization", "CmbMetric",
            "TxtFindNumMatches", "TxtFindMinScore", "TxtFindGreediness", "TxtFindNumLevels", "TxtFindMaxOverlap",
            "ChkFindApplyGridFilter", "TxtFindGridRows", "TxtFindGridCols", "TxtFindMinScoreKeep",
            "TxtFindGridAngleDeg", "TxtFindMaxAngleDev", "ChkFindAutoRetry"
        };

        internal static IReadOnlyList<string> PersistControlNameList => PersistControlNames;

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
