using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// ROI 路径直线/圆弧相切衔接。
    /// 直线→圆：圆心在切点法线上，圆心到直线的垂足 = 切点（路径末顶点）；弧上点 = 鼠标在切圆上的投影。
    /// 圆→直线：直线过弧端切点，圆心到直线的垂足 = 切点；直线端点 = 鼠标在切线上的投影。
    /// </summary>
    public static class RoiContourPathTangent
    {
        private const int ArcTangentSampleSegments = 24;
        private const double TangentAlignMinDot = 0.985;
        private const double FootTolerancePx = 2.0;

        public static bool TryGetIncomingTravelTangent(RoiContourPath path, out double tx, out double ty) =>
            TryGetLastEdgeTravelTangent(path, atEndVertex: true, out tx, out ty);

        public static bool TryGetOutgoingTravelTangent(RoiContourPath path, out double tx, out double ty) =>
            TryGetLastEdgeTravelTangent(path, atEndVertex: true, out tx, out ty);

        /// <summary>圆弧→直线：将 pick 投影到过弧端切点的切线上，保证圆心垂足为切点。</summary>
        public static bool TryProjectTangentLineEndAfterArc(
            RoiContourPath path,
            Point pick,
            double minStepPx,
            out Point lineEnd)
        {
            lineEnd = pick;
            if (!TryGetLastArcEdge(path, out Point arcStart, out Point via, out Point junction))
                return false;

            if (!TryGetArcOutgoingTangentFromCircle(arcStart, via, junction, out double tx, out double ty, out Point center))
                return false;

            if (!CenterFootOnLineIsJunction(junction, junction, tx, ty, center))
            {
                tx = -tx;
                ty = -ty;
            }

            if (!CenterFootOnLineIsJunction(junction, junction, tx, ty, center))
                return false;

            return TryProjectPointOntoTangentRay(junction, tx, ty, pick, minStepPx, out lineEnd);
        }

        /// <summary>直线→圆弧时，上一顶点（直线段起点）。</summary>
        public static bool TryGetIncomingLineAnchor(RoiContourPath path, out Point anchor)
        {
            anchor = default;
            if (path.VertexCount < 2 || path.EdgeKinds.Count == 0)
                return false;
            int ei = path.EdgeKinds.Count - 1;
            if (path.EdgeKinds[ei] != RoiEdgeKind.Line)
                return false;
            anchor = path.Vertices[ei];
            return true;
        }

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

            if (atEndVertex
                && TryGetArcOutgoingTangentFromCircle(a, via, b, out tx, out ty, out _))
                return true;

            return TryGetArcTravelTangentFromSamples(a, via, b, atEndVertex, out tx, out ty);
        }

        private static bool TryGetLastArcEdge(
            RoiContourPath path,
            out Point arcStart,
            out Point via,
            out Point junction)
        {
            arcStart = via = junction = default;
            int ei = path.EdgeKinds.Count - 1;
            if (ei < 0 || ei + 1 >= path.VertexCount || path.EdgeKinds[ei] != RoiEdgeKind.Arc)
                return false;
            if (ei >= path.ArcVia.Count || path.ArcVia[ei] is not Point v)
                return false;

            arcStart = path.Vertices[ei];
            junction = path.Vertices[ei + 1];
            via = v;
            return true;
        }

        /// <summary>弧端切向：垂直于圆心→弧端半径，方向与 SampleArc 走向一致。</summary>
        private static bool TryGetArcOutgoingTangentFromCircle(
            Point arcStart,
            Point via,
            Point junction,
            out double tx,
            out double ty,
            out Point center)
        {
            tx = ty = 0;
            center = default;
            if (!RoiContourPath.TryCircleFromThreePoints(arcStart, via, junction, out center, out _))
                return false;

            double rx = junction.X - center.X;
            double ry = junction.Y - center.Y;
            double ccwX = -ry;
            double ccwY = rx;
            double cwX = ry;
            double cwY = -rx;

            if (!TryGetArcTravelTangentFromSamples(arcStart, via, junction, atEndVertex: true, out double sx, out double sy))
            {
                if (!Normalize(ref ccwX, ref ccwY))
                    return false;
                tx = ccwX;
                ty = ccwY;
                return true;
            }

            if (Dot(ccwX, ccwY, sx, sy) >= Dot(cwX, cwY, sx, sy))
            {
                if (!Normalize(ref ccwX, ref ccwY))
                    return false;
                tx = ccwX;
                ty = ccwY;
                return true;
            }

            if (!Normalize(ref cwX, ref cwY))
                return false;
            tx = cwX;
            ty = cwY;
            return true;
        }

        public static double TangentSideSign(double tangentX, double tangentY, Point origin, Point probe)
        {
            if (!Normalize(ref tangentX, ref tangentY))
                return 0;
            double vx = probe.X - origin.X;
            double vy = probe.Y - origin.Y;
            return tangentX * vy - tangentY * vx;
        }

        /// <summary>圆心在直线上的垂足是否为切点 junction。</summary>
        public static bool CenterFootOnLineIsJunction(
            Point lineAnchor,
            Point junction,
            double tangentX,
            double tangentY,
            Point center)
        {
            if (!Normalize(ref tangentX, ref tangentY))
                return false;

            double lam = Dot(center.X - lineAnchor.X, center.Y - lineAnchor.Y, tangentX, tangentY);
            var foot = new Point(
                lineAnchor.X + lam * tangentX,
                lineAnchor.Y + lam * tangentY);
            return Dist(foot, junction) <= FootTolerancePx;
        }

        public static bool TryComputeTangentArcBothSides(
            Point junction,
            double tangentX,
            double tangentY,
            Point end,
            Point mouseOnArc,
            Point? lineAnchor,
            out Point viaLeft,
            out Point viaRight,
            out bool hasLeft,
            out bool hasRight)
        {
            viaLeft = viaRight = default;
            hasLeft = hasRight = false;
            if (!Normalize(ref tangentX, ref tangentY) || Dist(junction, end) < 1e-3)
                return false;

            var nLeft = new Vector(-tangentY, tangentX);
            var nRight = new Vector(tangentY, -tangentX);
            hasLeft = TryBuildViaOnTangentCircle(
                junction, end, nLeft, tangentX, tangentY, mouseOnArc, lineAnchor, out viaLeft);
            hasRight = TryBuildViaOnTangentCircle(
                junction, end, nRight, tangentX, tangentY, mouseOnArc, lineAnchor, out viaRight);
            return hasLeft || hasRight;
        }

        public static bool TryComputeArcViaTangentAtStart(
            Point junction,
            double tangentX,
            double tangentY,
            Point end,
            Point mouseOnArc,
            Point? lineAnchor,
            bool? forceLeft,
            out Point via)
        {
            via = default;
            if (!TryComputeTangentArcBothSides(
                    junction, tangentX, tangentY, end, mouseOnArc, lineAnchor,
                    out Point viaL, out Point viaR, out bool hasL, out bool hasR))
                return false;

            if (forceLeft == true && hasL)
                return TryFinalizeVia(junction, tangentX, tangentY, end, lineAnchor, viaL, out via);

            if (forceLeft == false && hasR)
                return TryFinalizeVia(junction, tangentX, tangentY, end, lineAnchor, viaR, out via);

            if (hasL && !hasR)
                return TryFinalizeVia(junction, tangentX, tangentY, end, lineAnchor, viaL, out via);

            if (!hasL && hasR)
                return TryFinalizeVia(junction, tangentX, tangentY, end, lineAnchor, viaR, out via);

            bool pickLeft = TangentSideSign(tangentX, tangentY, junction, mouseOnArc) >= 0;
            Point primary = pickLeft ? viaL : viaR;
            Point alternate = pickLeft ? viaR : viaL;
            if (TryFinalizeVia(junction, tangentX, tangentY, end, lineAnchor, primary, out via))
                return true;

            return TryFinalizeVia(junction, tangentX, tangentY, end, lineAnchor, alternate, out via);
        }

        public static bool TryComputeArcViaTangentAtStartFromPath(
            RoiContourPath path,
            Point junction,
            Point end,
            Point mouseOnArc,
            bool? forceLeft,
            out Point via)
        {
            via = default;
            if (!TryGetIncomingTravelTangent(path, out double tx, out double ty))
                return false;

            Point? anchor = TryGetIncomingLineAnchor(path, out Point a) ? a : null;
            return TryComputeArcViaTangentAtStart(
                junction, tx, ty, end, mouseOnArc, anchor, forceLeft, out via);
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

        private static bool TryFinalizeVia(
            Point junction,
            double tangentX,
            double tangentY,
            Point end,
            Point? lineAnchor,
            Point candidate,
            out Point via)
        {
            via = candidate;
            if (!RoiContourPath.IsValidArcVia(junction, via, end))
                return false;

            if (!ArcDepartureAlignsTangent(junction, via, end, tangentX, tangentY))
                return false;

            if (lineAnchor is Point a
                && RoiContourPath.TryCircleFromThreePoints(junction, via, end, out Point c, out _)
                && !CenterFootOnLineIsJunction(a, junction, tangentX, tangentY, c))
                return false;

            return true;
        }

        private static bool TryBuildViaOnTangentCircle(
            Point junction,
            Point end,
            Vector normal,
            double tangentX,
            double tangentY,
            Point mouseOnArc,
            Point? lineAnchor,
            out Point via)
        {
            via = default;
            if (!TryCircleCenterFromTangentNormal(junction, end, normal, out Point center, out double radius))
                return false;

            if (lineAnchor is Point anchor
                && !CenterFootOnLineIsJunction(anchor, junction, tangentX, tangentY, center))
                return false;

            via = ProjectPointOntoCircle(center, radius, mouseOnArc);
            if (!RoiContourPath.IsValidArcVia(junction, via, end))
                return false;

            return ArcDepartureAlignsTangent(junction, via, end, tangentX, tangentY);
        }

        private static Point ProjectPointOntoCircle(Point center, double radius, Point pick)
        {
            double ang = Math.Atan2(pick.Y - center.Y, pick.X - center.X);
            return new Point(
                center.X + radius * Math.Cos(ang),
                center.Y + radius * Math.Sin(ang));
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

        /// <summary>
        /// 过 junction、end 且于 junction 与直线相切：圆心在 junction 的法线上。
        /// |junction-center| = |end-center|。
        /// </summary>
        private static bool TryCircleCenterFromTangentNormal(
            Point junction,
            Point end,
            Vector unitNormal,
            out Point center,
            out double radius)
        {
            center = default;
            radius = 0;
            double bx = end.X - junction.X;
            double by = end.Y - junction.Y;
            double dot = unitNormal.X * bx + unitNormal.Y * by;
            if (Math.Abs(dot) < 1e-9)
                return false;

            double s = (bx * bx + by * by) / (2.0 * dot);
            center = new Point(junction.X + s * unitNormal.X, junction.Y + s * unitNormal.Y);
            double dx = junction.X - center.X;
            double dy = junction.Y - center.Y;
            radius = Math.Sqrt(dx * dx + dy * dy);
            return radius > 1e-6;
        }

        private static double Dot(double ax, double ay, double bx, double by) => ax * bx + ay * by;

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
