using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalibOperatorCLI_Example
{
    /// <summary>形状模板页 ROI 快照（.xvroi.json），支持全部绘制模式。</summary>
    internal sealed class RoiSnapshotDocument
    {
        public const int CurrentVersion = 2;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>mm：世界坐标(mm)；px：图像像素（旧版）。缺省为空，由 <see cref="RoiSnapshotFileIo"/> 按 Version 推断。</summary>
        public string? CoordinateUnit { get; set; }

        /// <summary>保存时使用的九点标定 JSON 路径（仅供参考）。</summary>
        public string? NinePointCalibrationPath { get; set; }

        public string? ImagePath { get; set; }

        public int ImageWidth { get; set; }

        public int ImageHeight { get; set; }

        /// <summary>none | rect | rotatedrect | circle | polygon | ring | opentrajectories</summary>
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

        public List<HalconShapeModelRoiPathDto> OpenTrajectoryPaths { get; set; } = new();
        public List<string> OpenTrajectoryNames { get; set; } = new();
        public HalconShapeModelRoiPathDto? OpenTrajectoryDraft { get; set; }
        public List<HalconShapeModelRoiConnectorDto> OpenTrajectoryConnectors { get; set; } = new();
    }

    internal static class RoiSnapshotFileIo
    {
        public const string FileFilter =
            "ROI 快照 (*.xvroi.json)|*.xvroi.json|开放轨迹旧版 (*.xvopenpaths.json)|*.xvopenpaths.json|JSON (*.json)|*.json|所有文件 (*.*)|*.*";

        private static readonly JsonSerializerOptions ReadOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonSerializerOptions WriteOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static void Save(string filePath, RoiSnapshotDocument doc)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("文件路径无效", nameof(filePath));
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            doc.Version = RoiSnapshotDocument.CurrentVersion;
            doc.CoordinateUnit = RoiSnapshotCoordinateTransform.UnitMillimeter;
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(filePath, JsonSerializer.Serialize(doc, WriteOpts));
        }

        public static RoiSnapshotDocument Load(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("ROI 文件不存在", filePath);

            string json = File.ReadAllText(filePath);
            using JsonDocument probe = JsonDocument.Parse(json);
            if (probe.RootElement.TryGetProperty("RoiMode", out _))
            {
                RoiSnapshotDocument? doc = JsonSerializer.Deserialize<RoiSnapshotDocument>(json, ReadOpts);
                if (doc == null)
                    throw new InvalidOperationException("ROI 文件格式无效或为空");
                NormalizeLists(doc);
                return doc;
            }

            OpenTrajectoryDocument? legacy = JsonSerializer.Deserialize<OpenTrajectoryDocument>(json, ReadOpts);
            if (legacy == null)
                throw new InvalidOperationException("ROI 文件格式无效或为空");
            return FromLegacyOpenTrajectory(legacy);
        }

        public static RoiSnapshotDocument FromSettings(
            HalconShapeModelUiSettings s,
            string? imagePath,
            int imageWidth,
            int imageHeight,
            string coordinateUnit = RoiSnapshotCoordinateTransform.UnitMillimeter,
            string? ninePointCalibrationPath = null)
        {
            return new RoiSnapshotDocument
            {
                ImagePath = string.IsNullOrWhiteSpace(imagePath) ? null : imagePath,
                ImageWidth = imageWidth,
                ImageHeight = imageHeight,
                CoordinateUnit = string.IsNullOrWhiteSpace(coordinateUnit)
                    ? RoiSnapshotCoordinateTransform.UnitMillimeter
                    : coordinateUnit,
                NinePointCalibrationPath = string.IsNullOrWhiteSpace(ninePointCalibrationPath)
                    ? null
                    : ninePointCalibrationPath,
                RoiMode = s.RoiMode ?? "none",
                RoiRectX = s.RoiRectX,
                RoiRectY = s.RoiRectY,
                RoiRectW = s.RoiRectW,
                RoiRectH = s.RoiRectH,
                RotRectCenterX = s.RotRectCenterX,
                RotRectCenterY = s.RotRectCenterY,
                RotRectWidth = s.RotRectWidth,
                RotRectHeight = s.RotRectHeight,
                RotRectAngleDeg = s.RotRectAngleDeg,
                CircleCenterX = s.CircleCenterX,
                CircleCenterY = s.CircleCenterY,
                CircleRadius = s.CircleRadius,
                PolygonPath = ClonePathDto(s.PolygonPath),
                RingOuterPath = ClonePathDto(s.RingOuterPath),
                RingInnerPath = ClonePathDto(s.RingInnerPath),
                OpenTrajectoryPaths = s.OpenTrajectoryPaths?.ToList() ?? new List<HalconShapeModelRoiPathDto>(),
                OpenTrajectoryNames = s.OpenTrajectoryNames?.ToList() ?? new List<string>(),
                OpenTrajectoryDraft = ClonePathDto(s.OpenTrajectoryDraft),
                OpenTrajectoryConnectors = s.OpenTrajectoryConnectors?.ToList()
                                         ?? new List<HalconShapeModelRoiConnectorDto>()
            };
        }

        public static HalconShapeModelUiSettings ToSettings(RoiSnapshotDocument doc)
        {
            return new HalconShapeModelUiSettings
            {
                RoiMode = doc.RoiMode ?? "none",
                RoiRectX = doc.RoiRectX,
                RoiRectY = doc.RoiRectY,
                RoiRectW = doc.RoiRectW,
                RoiRectH = doc.RoiRectH,
                RotRectCenterX = doc.RotRectCenterX,
                RotRectCenterY = doc.RotRectCenterY,
                RotRectWidth = doc.RotRectWidth,
                RotRectHeight = doc.RotRectHeight,
                RotRectAngleDeg = doc.RotRectAngleDeg,
                CircleCenterX = doc.CircleCenterX,
                CircleCenterY = doc.CircleCenterY,
                CircleRadius = doc.CircleRadius,
                PolygonPath = ClonePathDto(doc.PolygonPath),
                RingOuterPath = ClonePathDto(doc.RingOuterPath),
                RingInnerPath = ClonePathDto(doc.RingInnerPath),
                OpenTrajectoryPaths = doc.OpenTrajectoryPaths?.ToList() ?? new List<HalconShapeModelRoiPathDto>(),
                OpenTrajectoryNames = doc.OpenTrajectoryNames?.ToList() ?? new List<string>(),
                OpenTrajectoryDraft = ClonePathDto(doc.OpenTrajectoryDraft),
                OpenTrajectoryConnectors = doc.OpenTrajectoryConnectors?.ToList()
                                             ?? new List<HalconShapeModelRoiConnectorDto>()
            };
        }

        private static RoiSnapshotDocument FromLegacyOpenTrajectory(OpenTrajectoryDocument legacy)
        {
            var doc = new RoiSnapshotDocument
            {
                RoiMode = "opentrajectories",
                CoordinateUnit = RoiSnapshotCoordinateTransform.UnitPixel,
                ImagePath = legacy.ImagePath,
                ImageWidth = legacy.ImageWidth,
                ImageHeight = legacy.ImageHeight,
                OpenTrajectoryDraft = ClonePathDto(legacy.Draft),
                OpenTrajectoryConnectors = legacy.Connectors?.ToList()
                                           ?? new List<HalconShapeModelRoiConnectorDto>()
            };

            if (legacy.Trajectories != null)
            {
                int i = 0;
                foreach (OpenTrajectoryEntryDto? entry in legacy.Trajectories)
                {
                    if (entry?.Path == null || entry.Path.VertexXs.Count < 2)
                        continue;
                    doc.OpenTrajectoryPaths.Add(entry.Path);
                    doc.OpenTrajectoryNames.Add(string.IsNullOrWhiteSpace(entry.Name) ? $"轨迹 {i + 1}" : entry.Name.Trim());
                    i++;
                }
            }

            return doc;
        }

        private static void NormalizeLists(RoiSnapshotDocument doc)
        {
            // v1 及更早快照始终为像素；缺失 CoordinateUnit 时不得沿用类默认值误判为 mm。
            if (doc.Version < RoiSnapshotDocument.CurrentVersion)
                doc.CoordinateUnit = RoiSnapshotCoordinateTransform.UnitPixel;
            else if (string.IsNullOrWhiteSpace(doc.CoordinateUnit))
                doc.CoordinateUnit = RoiSnapshotCoordinateTransform.UnitMillimeter;
            doc.OpenTrajectoryPaths ??= new List<HalconShapeModelRoiPathDto>();
            doc.OpenTrajectoryNames ??= new List<string>();
            doc.OpenTrajectoryConnectors ??= new List<HalconShapeModelRoiConnectorDto>();
        }

        private static HalconShapeModelRoiPathDto? ClonePathDto(HalconShapeModelRoiPathDto? src)
        {
            if (src == null || src.VertexXs.Count == 0)
                return null;

            var dst = new HalconShapeModelRoiPathDto { IsClosed = src.IsClosed };
            dst.VertexXs.AddRange(src.VertexXs);
            dst.VertexYs.AddRange(src.VertexYs);
            dst.EdgeKinds.AddRange(src.EdgeKinds);
            dst.ArcViaXs.AddRange(src.ArcViaXs);
            dst.ArcViaYs.AddRange(src.ArcViaYs);
            return dst;
        }
    }
}
