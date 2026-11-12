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
            if (tailed.Points.Length != 4
                || Math.Abs(tailed.Points[2].X - 10) > 1e-9 || Math.Abs(tailed.Points[2].Y - 2) > 1e-9
                || Math.Abs(tailed.Points[3].X - 10) > 1e-9 || Math.Abs(tailed.Points[3].Y - 4) > 1e-9)
            {
                Console.Error.WriteLine(
                    $"FAIL: tail split expected (10,2)+(10,4), n={tailed.Points.Length} p2=({tailed.Points[Math.Min(2, tailed.Points.Length - 1)].X},{tailed.Points[Math.Min(2, tailed.Points.Length - 1)].Y})");
                return 1;
            }

            double tailLen = Math.Sqrt(
                (tailed.Points[3].X - tailed.Points[2].X) * (tailed.Points[3].X - tailed.Points[2].X)
                + (tailed.Points[3].Y - tailed.Points[2].Y) * (tailed.Points[3].Y - tailed.Points[2].Y));
            double keptLen = Math.Sqrt(
                (tailed.Points[2].X - tailed.Points[1].X) * (tailed.Points[2].X - tailed.Points[1].X)
                + (tailed.Points[2].Y - tailed.Points[1].Y) * (tailed.Points[2].Y - tailed.Points[1].Y));
            if (Math.Abs(tailLen - 2) > 1e-9 || Math.Abs(keptLen - 2) > 1e-9)
            {
                Console.Error.WriteLine($"FAIL: tail split lens kept={keptLen} tail={tailLen}");
                return 1;
            }

            var twoBars = new[]
            {
                new Point2D(0, 0), new Point2D(5, 0),
                new Point2D(0, 10), new Point2D(5, 10)
            };
            var twoTailed = WorldTrajectoryOffset.ApplyTailDistance(twoBars, new[] { 0, 0, 1, 1 }, 1);
            if (twoTailed.Points.Length != 6
                || Math.Abs(twoTailed.Points[1].X - 4) > 1e-9 || Math.Abs(twoTailed.Points[2].X - 5) > 1e-9
                || Math.Abs(twoTailed.Points[4].X - 4) > 1e-9 || Math.Abs(twoTailed.Points[5].X - 5) > 1e-9)
            {
                Console.Error.WriteLine($"FAIL: per-bar tail split n={twoTailed.Points.Length}");
                return 1;
            }

            var tooShort = WorldTrajectoryOffset.ApplyTailDistance(
                new[] { new Point2D(0, 0), new Point2D(1, 0) }, null, 2);
            if (tooShort.Points.Length != 2 || Math.Abs(tooShort.Points[1].X - 1) > 1e-9)
            {
                Console.Error.WriteLine("FAIL: short last segment must stay unsplit");
                return 1;
            }

            var openRect = new[]
            {
                new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10)
            };
            var closedOpen = WorldTrajectoryOffset.ApplyClose(openRect, null, true);
            if (closedOpen.Points.Length != 5
                || Math.Abs(closedOpen.Points[4].X) > 1e-9 || Math.Abs(closedOpen.Points[4].Y) > 1e-9
                || closedOpen.CloseCount != 1)
            {
                Console.Error.WriteLine($"FAIL: close open rect n={closedOpen.Points.Length}");
                return 1;
            }

            var skipClose = WorldTrajectoryOffset.ApplyClose(openRect, null, false);
            if (skipClose.Points.Length != 4)
            {
                Console.Error.WriteLine("FAIL: closePolyline=false must keep open");
                return 1;
            }

            var alreadyClosedRect = new[]
            {
                new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10), new Point2D(0, 0)
            };
            var closeAgain = WorldTrajectoryOffset.ApplyClose(alreadyClosedRect, null, true);
            if (closeAgain.Points.Length != 5)
            {
                Console.Error.WriteLine("FAIL: already-closed must not duplicate start");
                return 1;
            }

            var openOverrun = WorldTrajectoryOffset.ApplyOverrun(openRect, null, 2);
            if (openOverrun.Points.Length != 4)
            {
                Console.Error.WriteLine("FAIL: overrun on open polyline must no-op until closed");
                return 1;
            }

            var overrun = WorldTrajectoryOffset.ApplyOverrun(closedOpen.Points, closedOpen.BarIds, 2);
            if (overrun.Points.Length != 6
                || Math.Abs(overrun.Points[4].X) > 1e-9 || Math.Abs(overrun.Points[4].Y) > 1e-9
                || Math.Abs(overrun.Points[5].X) > 1e-9 || Math.Abs(overrun.Points[5].Y + 2) > 1e-9)
            {
                Console.Error.WriteLine(
                    $"FAIL: close then overrun n={overrun.Points.Length} last=({overrun.Points[^1].X},{overrun.Points[^1].Y})");
                return 1;
            }

            var overrunClosed = WorldTrajectoryOffset.ApplyOverrun(alreadyClosedRect, null, 2);
            if (overrunClosed.Points.Length != 6
                || Math.Abs(overrunClosed.Points[5].X) > 1e-9 || Math.Abs(overrunClosed.Points[5].Y + 2) > 1e-9)
            {
                Console.Error.WriteLine($"FAIL: already-closed overrun n={overrunClosed.Points.Length}");
                return 1;
            }

            var twoBarPts = new[]
            {
                new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 5), new Point2D(0, 5),
                new Point2D(0, 10), new Point2D(10, 10), new Point2D(10, 15), new Point2D(0, 15)
            };
            var twoBarIds = new[] { 0, 0, 0, 0, 1, 1, 1, 1 };
            var twoBarClosed = WorldTrajectoryOffset.ApplyClose(twoBarPts, twoBarIds, true);
            var twoBarOverrun = WorldTrajectoryOffset.ApplyOverrun(twoBarClosed.Points, twoBarClosed.BarIds, 1);
            if (twoBarOverrun.Points.Length != 12
                || Math.Abs(twoBarOverrun.Points[5].X) > 1e-9 || Math.Abs(twoBarOverrun.Points[5].Y + 1) > 1e-9
                || Math.Abs(twoBarOverrun.Points[11].X) > 1e-9 || Math.Abs(twoBarOverrun.Points[11].Y - 9) > 1e-9)
            {
                Console.Error.WriteLine($"FAIL: per-bar close+overrun n={twoBarOverrun.Points.Length}");
                return 1;
            }

            var openPair = WorldTrajectoryOffset.ApplyClose(
                new[] { new Point2D(0, 0), new Point2D(5, 0) }, null, true);
            if (openPair.Points.Length != 2)
            {
                Console.Error.WriteLine("FAIL: 2-point open polyline must not close");
                return 1;
            }

            var thenTail = WorldTrajectoryOffset.ApplyCloseOverrunTail(openRect, null, true, 2, 0.5);
            if (thenTail.Points.Length != 7
                || Math.Abs(thenTail.Points[5].X) > 1e-9 || Math.Abs(thenTail.Points[5].Y + 1.5) > 1e-9
                || Math.Abs(thenTail.Points[6].X) > 1e-9 || Math.Abs(thenTail.Points[6].Y + 2) > 1e-9)
            {
                Console.Error.WriteLine(
                    $"FAIL: close-overrun-tail n={thenTail.Points.Length} p5=({thenTail.Points[Math.Min(5, thenTail.Points.Length - 1)].X},{thenTail.Points[Math.Min(5, thenTail.Points.Length - 1)].Y})");
                return 1;
            }

            var closeThenTail = WorldTrajectoryOffset.ApplyCloseOverrunTail(openRect, null, true, 0, 2);
            if (closeThenTail.Points.Length != 6
                || Math.Abs(closeThenTail.Points[4].X) > 1e-9 || Math.Abs(closeThenTail.Points[4].Y - 2) > 1e-9
                || Math.Abs(closeThenTail.Points[5].X) > 1e-9 || Math.Abs(closeThenTail.Points[5].Y) > 1e-9)
            {
                Console.Error.WriteLine($"FAIL: close then tail split n={closeThenTail.Points.Length}");
                return 1;
            }

            // 超程2、收尾3：末段必须仍是3（跨过超程段再往前拆）。
            var overrun2Tail3 = WorldTrajectoryOffset.ApplyCloseOverrunTail(openRect, null, true, 2, 3);
            if (overrun2Tail3.SplitCount != 1)
            {
                Console.Error.WriteLine("FAIL: overrun2/tail3 must split");
                return 1;
            }

            double o2t3Len = Math.Sqrt(
                (overrun2Tail3.Points[^1].X - overrun2Tail3.Points[^2].X)
                * (overrun2Tail3.Points[^1].X - overrun2Tail3.Points[^2].X)
                + (overrun2Tail3.Points[^1].Y - overrun2Tail3.Points[^2].Y)
                * (overrun2Tail3.Points[^1].Y - overrun2Tail3.Points[^2].Y));
            if (Math.Abs(o2t3Len - 3) > 1e-9)
            {
                Console.Error.WriteLine($"FAIL: overrun2/tail3 last segment {o2t3Len} != 3");
                return 1;
            }

            // 超程长于收尾：拆在超程段内。
            var overrunThenTail = WorldTrajectoryOffset.ApplyCloseOverrunTail(openRect, null, true, 3, 1);
            if (overrunThenTail.SplitCount != 1)
            {
                Console.Error.WriteLine("FAIL: overrun3/tail1 must split");
                return 1;
            }

            double overrunTailLen = Math.Sqrt(
                (overrunThenTail.Points[^1].X - overrunThenTail.Points[^2].X)
                * (overrunThenTail.Points[^1].X - overrunThenTail.Points[^2].X)
                + (overrunThenTail.Points[^1].Y - overrunThenTail.Points[^2].Y)
                * (overrunThenTail.Points[^1].Y - overrunThenTail.Points[^2].Y));
            if (Math.Abs(overrunTailLen - 1) > 1e-9)
            {
                Console.Error.WriteLine($"FAIL: overrun last segment {overrunTailLen} != 1");
                return 1;
            }

            Console.WriteLine("OK world traj offset by BarId (loop Index=7 Y+1), close then overrun then tailDist split");
            return 0;
        }
    }
}
