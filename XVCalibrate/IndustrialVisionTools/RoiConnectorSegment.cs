using System.Windows;

namespace CalibOperatorCLI_Example
{
    /// <summary>连接两条轨迹的直线，不参与轮廓/模板采样。</summary>
    public sealed class RoiConnectorSegment
    {
        public double StartX { get; init; }
        public double StartY { get; init; }
        public double EndX { get; init; }
        public double EndY { get; init; }

        public Point Start => new Point(StartX, StartY);
        public Point End => new Point(EndX, EndY);

        public RoiConnectorSegment(double startX, double startY, double endX, double endY)
        {
            StartX = startX;
            StartY = startY;
            EndX = endX;
            EndY = endY;
        }

        public RoiConnectorSegment(Point start, Point end)
            : this(start.X, start.Y, end.X, end.Y)
        {
        }
    }
}
