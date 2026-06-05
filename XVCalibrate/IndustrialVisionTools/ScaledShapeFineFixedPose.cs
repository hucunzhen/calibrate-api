#if HALCON_ENABLED
namespace CalibOperatorCLI_Example
{
    /// <summary>缩放形状精匹配：已接入的 Coarse* 端口视为固定 DOF，不再搜索。</summary>
    public readonly struct ScaledShapeFineFixedPose
    {
        public bool FixRow { get; init; }
        public bool FixCol { get; init; }
        public bool FixAngle { get; init; }
        public bool FixScale { get; init; }
        public double Row { get; init; }
        public double Col { get; init; }
        public double AngleDeg { get; init; }
        public double Scale { get; init; }

        public bool PositionFixed => FixRow && FixCol;
        public bool AllGeometryFixed => FixRow && FixCol && FixAngle;
        public bool AllFixed => AllGeometryFixed && FixScale;
    }
}
#endif
