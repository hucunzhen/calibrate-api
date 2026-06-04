using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace CalibOperatorCLI_Example
{
    /// <summary>ROI 路径直线/圆弧相切衔接几何。</summary>
    public static class RoiContourPathTangent
    {
        private const int ArcTangentSampleSegments = 24;
        private const double TangentAlignMinDot = 0.985;

        /// <summary>沿最后一段边进入末顶点时的前进切向（列=X，行=Y）。</summary>
        public static bool TryGetIncomingTravelTangent(RoiContourPath path, out double tx, out double ty) =>
            TryGetLastEdgeTravelTangent(path, atEndVertex: true, out tx, out ty);

        /// <summary>从末顶点沿路径继续前进的切向（与进入末点的切向一致）。</summary>
        public static bool TryGetOutgoingTravelTangent(RoiContourPath path, out double tx, out double ty) =>
            TryGetLastEdgeTravelTangent(path, atEndVertex: true, out tx, out ty);

        private static bool TryGetLastEdgeTravelTangent(
            RoiContourPath path,
            bool atEndVertex,
            out double tx,
            out double ty)
        {
            tx = ty = 0;
            int n = path.VertexCount;
            if (n < 2 || path.EdgeKinds.Count == 0)
                return false;

            int ei = path.EdgeKinds.Count - 1;
            if (ei < 0 || ei + 1 >= n)
                return false;

            Point a = path.Vertices[ei];
            Point b = path.Vertices[ei + 1];
            if (path.EdgeKinds[ei] == RoiEdgeKind.Line)
            {
                tx = b.X - a.X;
                ty = b.Y - a.Y;
                return Normalize(ref tx, ref ty);
            }

            if (ei >= path.ArcVia.Count || path.ArcVia[ei] is not Point via)
                return false;

            return TryGetArcTravelTangentFromSamples(a, via, b, atEndVertex, out tx, out ty);
        }

        /// <summary>沿切向行进时，probe 在 origin 的左侧为正值、右侧为负值。</summary>
        public static double TangentSideSign(double tangentX, double tangentY, Point origin, Point probe)
        {
            if (!Normalize(ref tangentX, ref tangentY))
                return 0;
            double vx = probe.X - origin.X;
            double vy = probe.Y - origin.Y;
            return tangentX * vy - tangentY * vx;
        }

        public static bool TryComputeTangentArcBothSides(
            Point start,
            double tangentX,
            double tangentY,
            Point end,
            out Point viaLeft,
            out Point viaRight,
            out bool hasLeft,
            out bool hasRight)
        {
            viaLeft = viaRight = default;
            hasLeft = hasRight = false;
            if (!Normalize(ref tangentX, ref tangentY) || Dist(start, end) < 1e-3)
                return false;

            var nLeft = new Vector(-tangentY, tangentX);
            var nRight = new Vector(tangentY, -tangentX);
            hasLeft = TryBuildViaOnTangentCircle(start, end, nLeft, tangentX, tangentY, out viaLeft);
            hasRight = TryBuildViaOnTangentCircle(start, end, nRight, tangentX, tangentY, out viaRight);
            return hasLeft || hasRight;
        }

        public static bool TryComputeArcViaTangentAtStart(
            Point start,
            double tangentX,
            double tangentY,
            Point end,
            Point sideHint,
            bool? forceLeft,
            out Point via)
        {
            via = default;
            if (!TryComputeTangentArcBothSides(start, tangentX, tangentY, end, out Point viaL, out Point viaR, out bool hasL, out bool hasR))
                return false;

            if (forceLeft == true && hasL)
            {
                via = viaL;
                return true;
            }

            if (forceLeft == false && hasR)
            {
                via = viaR;
                return true;
            }

            if (hasL && !hasR)
            {
                via = viaL;
                return true;
            }

            if (!hasL && hasR)
            {
                via = viaR;
                return true;
            }

            bool pickLeft = TangentSideSign(tangentX, tangentY, start, sideHint) >= 0;
            via = pickLeft ? viaL : viaR;
            if (RoiContourPath.IsValidArcVia(start, via, end)
                && ArcDepartureAlignsTangent(start, via, end, tangentX, tangentY))
                return true;

            Point alt = pickLeft ? viaR : viaL;
            if ((pickLeft ? hasR : hasL)
                && RoiContourPath.IsValidArcVia(start, alt, end)
                && ArcDepartureAlignsTangent(start, alt, end, tangentX, tangentY))
            {
                via = alt;
                return true;
            }

            return false;
        }

        public static bool TryProjectPointOntoTangentRay(
            Point origin,
            double tangentX,
            double tangentY,
            Point pick,
            double minStepPx,
            out Point onRay)
        {
            onRay = origin;
            if (!Normalize(ref tangentX, ref tangentY))
                return false;

            double vx = pick.X - origin.X;
            double vy = pick.Y - origin.Y;
            double t = vx * tangentX + vy * tangentY;
            if (t < minStepPx)
                t = minStepPx;

            onRay = new Point(origin.X + t * tangentX, origin.Y + t * tangentY);
            return true;
        }

        /// <summary>验证圆弧在 start 处切向与 (tx,ty) 同向；圆心应在 start 的法线方向上。</summary>
        public static bool ArcDepartureAlignsTangent(
            Point start,
            Point via,
            Point end,
            double tangentX,
            double tangentY)
        {
            if (!Normalize(ref tangentX, ref tangentY))
                return false;

            List<Point> pts = RoiContourPath.SampleArc(start, via, end, ArcTangentSampleSegments).ToList();
            if (pts.Count < 2)
                return false;

            double dx = pts[1].X - start.X;
            double dy = pts[1].Y - start.Y;
            if (!Normalize(ref dx, ref dy))
                return false;

            if (Dot(dx, dy, tangentX, tangentY) < TangentAlignMinDot)
                return false;

            if (!RoiContourPath.TryCircleFromThreePoints(start, via, end, out Point center, out _))
                return false;

            double rx = start.X - center.X;
            double ry = start.Y - center.Y;
            return Math.Abs(Dot(rx, ry, tangentX, tangentY)) < 0.05;
        }

        private static bool TryBuildViaOnTangentCircle(
            Point start,
            Point end,
            Vector normal,
            double tangentX,
            double tangentY,
            out Point via)
        {
            via = default;
            if (!TryCircleCenterFromTangentNormal(start, end, normal, out Point center, out double radius))
                return false;

            double a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
            double a1 = Math.Atan2(end.Y - center.Y, end.X - center.X);
            double sweepCcw = NormalizeSweepRad(a0, a1, ccw: true);
            double sweepCw = NormalizeSweepRad(a0, a1, ccw: false);

            Point? best = null;
            double bestDot = -2;
            foreach (double sweep in new[] { sweepCcw, sweepCw })
            {
                if (Math.Abs(sweep) < 1e-9)
                    continue;

                double amid = a0 + sweep * 0.5;
                var candidate = new Point(
                    center.X + radius * Math.Cos(amid),
                    center.Y + radius * Math.Sin(amid));
                if (!RoiContourPath.IsValidArcVia(start, candidate, end))
                    continue;

                List<Point> pts = RoiContourPath.SampleArc(start, candidate, end, ArcTangentSampleSegments).ToList();
                if (pts.Count < 2)
                    continue;

                double dx = pts[1].X - start.X;
                double dy = pts[1].Y - start.Y;
                if (!Normalize(ref dx, ref dy))
                    continue;

                double d = Dot(dx, dy, tangentX, tangentY);
                if (d > bestDot)
                {
                    bestDot = d;
                    best = candidate;
                }
            }

            if (best is not Point chosen || bestDot < TangentAlignMinDot)
                return false;

            via = chosen;
            return true;
        }

        private static bool TryGetArcTravelTangentFromSamples(
            Point start,
            Point via,
            Point end,
            bool atEndVertex,
            out double tx,
            out double ty)
        {
            tx = ty = 0;
            List<Point> pts = RoiContourPath.SampleArc(start, via, end, ArcTangentSampleSegments).ToList();
            if (pts.Count < 2)
                return false;

            if (atEndVertex)
            {
                Point prev = pts[pts.Count - 2];
                tx = end.X - prev.X;
                ty = end.Y - prev.Y;
            }
            else
            {
                tx = pts[1].X - start.X;
                ty = pts[1].Y - start.Y;
            }

            return Normalize(ref tx, ref ty);
        }

        private static bool TryCircleCenterFromTangentNormal(
            Point start,
            Point end,
            Vector unitNormal,
            out Point center,
            out double radius)
        {
            center = default;
            radius = 0;
            double bx = end.X - start.X;
            double by = end.Y - start.Y;
            double dot = unitNormal.X * bx + unitNormal.Y * by;
            if (Math.Abs(dot) < 1e-9)
                return false;

            double s = -(bx * bx + by * by) / (2.0 * dot);
            center = new Point(start.X + s * unitNormal.X, start.Y + s * unitNormal.Y);
            double dx = start.X - center.X;
            double dy = start.Y - center.Y;
            radius = Math.Sqrt(dx * dx + dy * dy);
            return radius > 1e-6;
        }

        private static double Dot(double ax, double ay, double bx, double by) => ax * bx + ay * by;

        private static double NormalizeSweepRad(double a0, double a1, bool ccw)
        {
            double diff = a1 - a0;
            if (ccw)
            {
                while (diff <= 0) diff += 2 * Math.PI;
                while (diff > 2 * Math.PI) diff -= 2 * Math.PI;
            }
            else
            {
                while (diff >= 0) diff -= 2 * Math.PI;
                while (diff < -2 * Math.PI) diff += 2 * Math.PI;
            }

            return diff;
        }

        private static bool Normalize(ref double x, ref double y)
        {
            double len = Math.Sqrt(x * x + y * y);
            if (len < 1e-9)
                return false;
            x /= len;
            y /= len;
            return true;
        }

        private static double Dist(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private readonly struct Vector
        {
            public readonly double X;
            public readonly double Y;
            public Vector(double x, double y) { X = x; Y = y; }
        }
    }
}
