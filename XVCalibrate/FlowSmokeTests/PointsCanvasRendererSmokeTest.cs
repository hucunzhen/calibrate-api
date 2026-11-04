using System;
using System.Drawing;
using CalibOperatorCLI_Example;
using CalibOperatorPInvoke;

namespace FlowSmokeTests
{
    internal static class PointsCanvasRendererSmokeTest
    {
        public static int Run()
        {
            if (Math.Abs(PointsCanvasRenderer.NiceStep(100) - 20) > 1e-9)
            {
                Console.Error.WriteLine($"FAIL: NiceStep(100)={PointsCanvasRenderer.NiceStep(100)}");
                return 1;
            }

            if (PointsCanvasRenderer.FormatTick(0, 20) != "0")
            {
                Console.Error.WriteLine($"FAIL: FormatTick 0 => {PointsCanvasRenderer.FormatTick(0, 20)}");
                return 1;
            }

            var pts = new[]
            {
                new Point2D(360, 10),
                new Point2D(420, 10),
                new Point2D(420, 80),
                new Point2D(360, 80)
            };
            using var bmp = PointsCanvasRenderer.Render(
                pts, null, null, "single", null, 0.1f, 0, null, 800, 600);
            if (bmp.Width < 400 || bmp.Height < 300)
            {
                Console.Error.WriteLine($"FAIL: canvas {bmp.Width}x{bmp.Height}");
                return 1;
            }

            var twoBars = new[]
            {
                new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 6),
                new Point2D(40, 0), new Point2D(50, 0), new Point2D(50, 6)
            };
            var barIds = new[] { 0, 0, 0, 7, 7, 7 };
            using var labeled = PointsCanvasRenderer.Render(
                twoBars, barIds, null, "bars", null, 1.2f, 0, null, 800, 600, drawBarIdLabels: true);
            if (labeled.Width < 200 || labeled.Height < 160)
            {
                Console.Error.WriteLine($"FAIL: bar-id canvas {labeled.Width}x{labeled.Height}");
                return 1;
            }

            Console.WriteLine($"OK points canvas {bmp.Width}x{bmp.Height} NiceStep(100)=20 barIds={labeled.Width}x{labeled.Height}");
            return 0;
        }
    }
}
