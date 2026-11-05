using System;
using CalibOperatorCLI_Example;
using CalibOperatorPInvoke;

namespace FlowSmokeTests
{
    internal static class WorldTrajectoryOffsetSmokeTest
    {
        public static int Run()
        {
            var pts = new[]
            {
                new Point2D(0, 10),
                new Point2D(8, 10),
                new Point2D(0, 0),
                new Point2D(8, 0)
            };
            var bars = new[] { 1, 1, 2, 2 };
            var table = WorldTrajectoryOffset.ParseOffsetByBar("1:0,0.3; 2:0,-0.2");
            var r = WorldTrajectoryOffset.Apply(pts, bars, table);
            if (r.AppliedBarCount != 2)
            {
                Console.Error.WriteLine($"FAIL: applied bars {r.AppliedBarCount}");
                return 1;
            }

            if (Math.Abs(r.Points[0].Y - 10.3) > 1e-9 || Math.Abs(r.Points[1].Y - 10.3) > 1e-9)
            {
                Console.Error.WriteLine($"FAIL: bar1 Y {r.Points[0].Y},{r.Points[1].Y}");
                return 1;
            }

            if (Math.Abs(r.Points[2].Y + 0.2) > 1e-9 || Math.Abs(r.Points[3].Y + 0.2) > 1e-9)
            {
                Console.Error.WriteLine($"FAIL: bar2 Y {r.Points[2].Y},{r.Points[3].Y}");
                return 1;
            }

            var loopOne = WorldTrajectoryOffset.Apply(
                pts,
                new[] { 0, 0, 0, 0 },
                WorldTrajectoryOffset.ParseOffsetByBar("7:0,1"),
                roundBarId: 7);
            if (Math.Abs(loopOne.Points[0].Y - 11) > 1e-9 || loopOne.BarIds[0] != 7)
            {
                Console.Error.WriteLine($"FAIL: loop Index=7 should offset Y+1, got Y={loopOne.Points[0].Y} bar={loopOne.BarIds[0]}");
                return 1;
            }

            var missing = WorldTrajectoryOffset.Apply(pts, new[] { 7, 7, 7, 7 }, table);
            if (Math.Abs(missing.Points[0].Y - 10) > 1e-9)
            {
                Console.Error.WriteLine("FAIL: unknown bar must not offset");
                return 1;
            }

            var range = WorldTrajectoryOffset.ParseOffsetByBar("0-9:0,0.3\n10-19:0,-0.2 # bottom");
            if (!range.TryGetValue(3, out var top) || Math.Abs(top.Dy - 0.3) > 1e-9)
            {
                Console.Error.WriteLine("FAIL: range 0-9");
                return 1;
            }

            if (!range.TryGetValue(10, out var bot) || Math.Abs(bot.Dy + 0.2) > 1e-9)
            {
                Console.Error.WriteLine("FAIL: range 10-19");
                return 1;
            }

            var line = new[] { new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 4) };
            var tailed = WorldTrajectoryOffset.ApplyTailDistance(line, null, 2);
            double tailLen = Math.Sqrt(
                (tailed[2].X - tailed[1].X) * (tailed[2].X - tailed[1].X)
                + (tailed[2].Y - tailed[1].Y) * (tailed[2].Y - tailed[1].Y));
            if (Math.Abs(tailLen - 2) > 1e-9 || Math.Abs(tailed[2].X - 10) > 1e-9 || Math.Abs(tailed[2].Y - 2) > 1e-9)
            {
                Console.Error.WriteLine($"FAIL: tailDist 2 got last=({tailed[2].X},{tailed[2].Y}) len={tailLen}");
                return 1;
            }

            var twoBars = new[]
            {
                new Point2D(0, 0), new Point2D(5, 0),
                new Point2D(0, 10), new Point2D(5, 10)
            };
            var twoTailed = WorldTrajectoryOffset.ApplyTailDistance(twoBars, new[] { 0, 0, 1, 1 }, 1);
            if (Math.Abs(twoTailed[1].X - 1) > 1e-9 || Math.Abs(twoTailed[3].X - 1) > 1e-9)
            {
                Console.Error.WriteLine($"FAIL: per-bar tail {twoTailed[1].X},{twoTailed[3].X}");
                return 1;
            }

            Console.WriteLine("OK world traj offset by BarId (loop Index=7 Y+1), tailDist last segment");
            return 0;
        }
    }
}
