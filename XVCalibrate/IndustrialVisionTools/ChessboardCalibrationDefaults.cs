namespace CalibOperatorCLI_Example
{
    /// <summary>棋盘格标定/透视矫正的默认物理规格与透视输出缩放。</summary>
    internal static class ChessboardCalibrationDefaults
    {
        public const int InnerCornerCols = 11;
        public const int InnerCornerRows = 8;
        public const double SquareSizeMm = 5.0;
        /// <summary>透视展开 metric 模式：输出图像中每毫米对应的像素数（像素/毫米）。</summary>
        public const double PxPerMm = 32.0;

        public const string PerspectiveOutputFrame = "plane";
        public const string PerspectiveOutputScale = "board_pixels";
        public const string AssumeUndistorted = "true";
    }
}
