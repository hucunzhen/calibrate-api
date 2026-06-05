using System.Collections.Generic;

namespace CalibOperatorCLI_Example
{
    /// <summary>旧版开放轨迹专用文件格式，仍用于迁移加载。</summary>
    internal sealed class OpenTrajectoryEntryDto
    {
        public string Name { get; set; } = "";

        public HalconShapeModelRoiPathDto Path { get; set; } = new();
    }

    internal sealed class OpenTrajectoryDocument
    {
        public int Version { get; set; } = 1;

        public string? ImagePath { get; set; }

        public int ImageWidth { get; set; }

        public int ImageHeight { get; set; }

        public List<OpenTrajectoryEntryDto> Trajectories { get; set; } = new();

        public HalconShapeModelRoiPathDto? Draft { get; set; }

        public List<HalconShapeModelRoiConnectorDto> Connectors { get; set; } = new();
    }

    internal sealed class OpenTrajectoryEntry
    {
        public string Name { get; set; } = "";

        public RoiContourPath Path { get; set; } = new();
    }
}
