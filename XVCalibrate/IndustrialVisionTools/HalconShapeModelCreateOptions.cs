namespace CalibOperatorCLI_Example
{
    /// <summary>形状模板页 / Flow 持有的 HALCON 模型族。</summary>
    public enum HalconFlowModelKind
    {
        Shape,
        Deformable
    }
}

#if HALCON_ENABLED
namespace CalibOperatorCLI_Example
{
    /// <summary>形状模板创建：模型类型。</summary>
    public enum HalconShapeModelKind
    {
        Shape,
        ScaledShape,
        /// <summary>局部可变形模板（CreateLocalDeformableModel/Xld），适合局部拉伸/褶皱。</summary>
        Deformable,
        /// <summary>平面未标定可变形（CreatePlanarUncalibDeformableModel/Xld），适合透视形变（梯形、斜视角平面件）。</summary>
        PlanarDeformable
    }

    /// <summary>可变形模板子类型（与 .dfm 文件一一对应，混用会导致匹配失败）。</summary>
    public enum HalconDeformableModelSubtype
    {
        Local,
        PlanarUncalib
    }

    /// <summary>形状模板创建：轮廓/图像来源。</summary>
    public enum HalconShapeModelSourceKind
    {
        /// <summary>阈值二值化 → GenContourRegionXld</summary>
        ThresholdXld,
        /// <summary>EdgesSubPix (Canny) → XLD，含 edge_direction</summary>
        EdgesXld,
        /// <summary>手绘闭合 ROI 边线几何 + 向内/向外梯度方向（edge_direction）</summary>
        PolygonXld,
        /// <summary>矩形 ROI 内灰度图 → create_shape_model</summary>
        ImageRectangle,
        /// <summary>多边形域内灰度图 → create_shape_model</summary>
        ImagePolygon,
        /// <summary>由测量/ROI 参数生成几何轮廓 XLD，可手调宽高角后建模板。</summary>
        GeometryXld
    }

    /// <summary>HALCON create_shape_model / create_shape_model_xld / scaled 变体共用参数。</summary>
    public sealed class HalconShapeModelCreateOptions
    {
        public HalconShapeModelKind ModelKind { get; set; } = HalconShapeModelKind.Shape;
        public HalconShapeModelSourceKind SourceKind { get; set; } = HalconShapeModelSourceKind.ThresholdXld;

        /// <summary>金字塔层数；0 表示 auto。</summary>
        public int NumLevels { get; set; } = 4;
        public double AngleStartDeg { get; set; } = -30;
        public double AngleExtentDeg { get; set; } = 60;
        /// <summary>角度步长(度)；0 表示 auto。</summary>
        public double AngleStepDeg { get; set; } = 0.5;
        public string Optimization { get; set; } = "auto";
        public string Metric { get; set; } = "ignore_local_polarity";
        /// <summary>图像模型 Contrast；null/空/auto 表示 HALCON auto。</summary>
        public string? Contrast { get; set; } = "auto";
        public int MinContrast { get; set; } = 10;

        /// <summary>仅 scaled 模型：比例范围与步长（步长 0=auto）。</summary>
        public double ScaleMin { get; set; } = 0.9;
        public double ScaleMax { get; set; } = 1.1;
        public double ScaleStep { get; set; } = 0;

        /// <summary>仅 ModelKind 为 Deformable/PlanarDeformable 时有效。</summary>
        public HalconDeformableModelSubtype DeformableSubtype { get; set; } = HalconDeformableModelSubtype.Local;

        // —— 轮廓提取 ——
        public string GenContourMode { get; set; } = "border";
        public int MinContourPoints { get; set; } = 10;
        public bool LargestContourOnly { get; set; }

        // —— EdgesSubPix ——
        public double EdgeAlpha { get; set; } = 1;
        public double EdgeLow { get; set; } = 20;
        public double EdgeHigh { get; set; } = 40;
    }
}
#endif
