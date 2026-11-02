using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// ROI 路径直线/圆弧相切衔接。
    /// 直线→圆：以切点为原点、进线为轴分象限（Q1=90°，Q2=180°，Q3=270°，Q4=另一侧90°）。
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

        /// <summary>直线/圆弧/连接线之后接下一段直线：将 pick 投影到进线切向射线上。</summary>
        public static bool TryProjectTangentLineEnd(
            RoiContourPath path,
            Point pick,
            double minStepPx,
            out Point lineEnd)
        {
            lineEnd = pick;
            if (path == null || path.VertexCount < 1)
                return false;

            if (TryGetLastArcEdge(path, out Point arcStart, out Point via, out Point junction))
            {
                if (!TryGetArcOutgoingTangentFromCircle(arcStart, via, junction, out double atx, out double aty, out Point center))
                    return false;

                if (!CenterFootOnLineIsJunction(junction, junction, atx, aty, center))
                {
                    atx = -atx;
                    aty = -aty;
                }

                if (!CenterFootOnLineIsJunction(junction, junction, atx, aty, center))
                    return false;

                return TryProjectPointOntoTangentRay(junction, atx, aty, pick, minStepPx, out lineEnd);
            }

            if (!TryGetIncomingTravelTangent(path, out double tx, out double ty))
                return false;

            Point start = path.Vertices[^1];
            return TryProjectPointOntoTangentRay(start, tx, ty, pick, minStepPx, out lineEnd);
        }

        /// <summary>圆弧→直线：将 pick 投影到过弧端切点的切线上，保证圆心垂足为切点。</summary>
        public static bool TryProjectTangentLineEndAfterArc(
            RoiContourPath path,
            Point pick,
            double minStepPx,
            out Point lineEnd) =>
            TryProjectTangentLineEnd(path, pick, minStepPx, out lineEnd);

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

        /// <summary>
        /// 绘制过程中：以切点为原点、进线为 +X、进线左侧为 +Y。
        /// 一象限 90°、二象限 180°、三象限 270°、四象限另一侧 90°。半径为到切线的距离。
        /// </summary>
        public static bool TryComputeTangentArcWithLiveRadius(
            Point junction,
            double tangentX,
            double tangentY,
            Point mouse,
            bool? forceLeft,
            Point? lineAnchor,
            out Point end,
            out Point via,
            out double radiusPx)
        {
            end = via = default;
            radiusPx = 0;
            if (!Normalize(ref tangentX, ref tangentY) || Dist(junction, mouse) < 1.0)
                return false;

            double along = Dot(mouse.X - junction.X, mouse.Y - junction.Y, tangentX, tangentY);
            double left = TangentSideSign(tangentX, tangentY, junction, mouse);
            bool mirror = forceLeft == false;
            if (mirror)
                left = -left;

            ResolveTangentQuadrant(along, left, out double sweepDeg, out bool bulgePositiveY);
            bool pickLeft = mirror ? !bulgePositiveY : bulgePositiveY;

            double nx = pickLeft ? -tangentY : tangentY;
            double ny = pickLeft ? tangentX : -tangentX;
            if (!Normalize(ref nx, ref ny))
                return false;

            radiusPx = Math.Abs(left);
            if (radiusPx < 1e-6)
                return false;

            double sweepRad = sweepDeg * Math.PI / 180.0;
            var center = new Point(junction.X + nx * radiusPx, junction.Y + ny * radiusPx);
            if (lineAnchor is Point anchor
                && !CenterFootOnLineIsJunction(anchor, junction, tangentX, tangentY, center))
                return false;

            double a0 = Math.Atan2(junction.Y - center.Y, junction.X - center.X);
            double signed = pickLeft ? sweepRad : -sweepRad;
            end = new Point(
                center.X + radiusPx * Math.Cos(a0 + signed),
                center.Y + radiusPx * Math.Sin(a0 + signed));
            via = new Point(
                center.X + radiusPx * Math.Cos(a0 + signed * 0.5),
                center.Y + radiusPx * Math.Sin(a0 + signed * 0.5));

            if (Dist(junction, end) < 1e-3)
                return false;

            return RoiContourPath.IsValidArcVia(junction, via, end)
                && ArcDepartureAlignsTangent(junction, via, end, tangentX, tangentY);
        }

        /// <summary>
        /// 切点为原点、进线为 +X、左侧为 +Y：Q1→90°，Q2→180°，Q3→270°，Q4→另一侧 90°。
        /// </summary>
        public static void ResolveTangentQuadrant(
            double alongTangent,
            double leftOfTangent,
            out double sweepDeg,
            out bool bulgePositiveY)
        {
            bool forward = alongTangent >= 0;
            bool left = leftOfTangent >= 0;
            if (forward && left)
            {
                sweepDeg = 90;
                bulgePositiveY = true;
            }
            else if (!forward && left)
            {
                sweepDeg = 180;
                bulgePositiveY = true;
            }
            else if (!forward && !left)
            {
                sweepDeg = 270;
                bulgePositiveY = true;
            }
            else
            {
                sweepDeg = 90;
                bulgePositiveY = false;
            }
        }

        /// <summary>顶点处的进线方向（闭合路径取上一段；开放路径仅 index&gt;0）。</summary>
        public static bool TryGetIncomingTravelTangentAtVertex(
            RoiContourPath path,
            int vertexIndex,
            out double tx,
            out double ty)
        {
            tx = ty = 0;
            int n = path.VertexCount;
            if (n < 2 || vertexIndex < 0 || vertexIndex >= n)
                return false;

            int ei;
            if (path.IsClosed)
                ei = (vertexIndex - 1 + n) % n;
            else
            {
                if (vertexIndex == 0)
                    return false;
                ei = vertexIndex - 1;
            }

            if (ei < 0 || ei >= path.EdgeKinds.Count || ei + 1 >= n)
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

            return TryGetArcOutgoingTangentFromCircle(a, via, b, out tx, out ty, out _);
        }

        public static bool TryGetLineAnchorBeforeVertex(RoiContourPath path, int vertexIndex, out Point anchor)
        {
            anchor = default;
            int n = path.VertexCount;
            if (n < 2 || vertexIndex <= 0)
                return false;

            int ei = path.IsClosed ? (vertexIndex - 1 + n) % n : vertexIndex - 1;
            if (ei < 0 || ei >= path.EdgeKinds.Count || path.EdgeKinds[ei] != RoiEdgeKind.Line)
                return false;

            anchor = path.Vertices[ei];
            return true;
        }

        /// <summary>固定切点与弧终点，按相切半径（法向距离）重算弧上点。</summary>
        public static bool TryAdjustTangentArcByRadius(
            Point junction,
            double tangentX,
            double tangentY,
            Point end,
            double radiusPx,
            bool? forceLeft,
            Point? lineAnchor,
            Point viaSideHint,
            out Point via)
        {
            via = default;
            if (!Normalize(ref tangentX, ref tangentY))
                return false;

            double nlx = -tangentY;
            double nly = tangentX;
            double nrx = tangentY;
            double nry = -tangentX;
            if (!Normalize(ref nlx, ref nly) || !Normalize(ref nrx, ref nry))
                return false;

            bool pickLeft = forceLeft == true
                || (forceLeft != false && TangentSideSign(tangentX, tangentY, junction, viaSideHint) >= 0);
            double nx = pickLeft ? nlx : nrx;
            double ny = pickLeft ? nly : nry;

            radiusPx = Math.Max(2.0, radiusPx);
            var center = new Point(junction.X + nx * radiusPx, junction.Y + ny * radiusPx);

            if (lineAnchor is Point anchor
                && !CenterFootOnLineIsJunction(anchor, junction, tangentX, tangentY, center))
                return false;

            if (!TryPickViaAtMidSweep(junction, end, center, radiusPx, tangentX, tangentY, out via))
                return false;

            return RoiContourPath.IsValidArcVia(junction, via, end)
                && ArcDepartureAlignsTangent(junction, via, end, tangentX, tangentY);
        }

        /// <summary>固定起点与圆心角，按半径缩放圆弧（终点与弧上点绕起点移动）。</summary>
        public static bool TrySetArcEdgeRadius(RoiContourPath path, int edgeIndex, double radiusPx, out string error)
        {
            error = "";
            int edges = RoiContourPath.GetEdgeCount(path);
            if (path.VertexCount < 2 || edges <= 0)
            {
                error = "路径段数不足";
                return false;
            }

            if (edgeIndex < 0 || edgeIndex >= edges
                || edgeIndex >= path.EdgeKinds.Count
                || path.EdgeKinds[edgeIndex] != RoiEdgeKind.Arc)
            {
                error = "请选择圆弧段";
                return false;
            }

            if (edgeIndex >= path.ArcVia.Count || path.ArcVia[edgeIndex] is not Point via)
            {
                error = "该圆弧段缺少弧上点";
                return false;
            }

            RoiContourPath.GetEdgeEndpoints(path, edgeIndex, out Point start, out Point end);
            if (!RoiContourPath.TryResizeArcKeepSweepFromStart(start, via, end, radiusPx, out Point newVia, out Point newEnd))
            {
                error = "无法按原圆心角重算圆弧，请检查半径";
                return false;
            }

            return RoiContourPath.TryWriteArcEdge(path, edgeIndex, newEnd, newVia, out error);
        }

        /// <summary>从路径末点（直线→圆弧相切）根据当前鼠标生成整段圆弧。</summary>
        public static bool TryBuildTangentArcFromPath(
            RoiContourPath path,
            Point mouse,
            bool? forceLeft,
            out Point end,
            out Point via)
        {
            end = via = default;
            if (path.VertexCount < 2 || path.EdgeKinds.Count == 0)
                return false;

            Point junction = path.Vertices[^1];
            if (!TryGetIncomingTravelTangent(path, out double tx, out double ty))
                return false;

            Point? anchor = TryGetIncomingLineAnchor(path, out Point a) ? a : null;
            return TryComputeTangentArcWithLiveRadius(
                junction, tx, ty, mouse, forceLeft, anchor, out end, out via, out _);
        }

        public static bool TryEstimateTangentArcParamsFromMouse(
            RoiContourPath path,
            Point directionHint,
            bool? forceLeft,
            out double radiusPx,
            out double sweepDeg)
        {
            radiusPx = 0;
            sweepDeg = 0;
            if (!TryBuildTangentArcFromPath(path, directionHint, forceLeft, out Point end, out Point via))
                return false;

            Point junction = path.Vertices[^1];
            if (!RoiContourPath.TryCircleFromThreePoints(junction, via, end, out Point center, out radiusPx))
                return false;

            return TryMeasureArcSweepDegrees(junction, via, end, center, out sweepDeg);
        }

        public static bool TryBuildTangentArcFromRadiusAndSweep(
            RoiContourPath path,
            Point directionHint,
            double radiusPx,
            double sweepDeg,
            bool? forceLeft,
            out Point end,
            out Point via)
        {
            end = via = default;
            if (path.VertexCount < 2 || path.EdgeKinds.Count == 0)
                return false;

            Point junction = path.Vertices[^1];
            if (!TryGetIncomingTravelTangent(path, out double tx, out double ty))
                return false;

            Point? anchor = TryGetIncomingLineAnchor(path, out Point a) ? a : null;
            return TryComputeTangentArcWithRadiusAndSweep(
                junction, tx, ty, directionHint, radiusPx, sweepDeg, forceLeft, anchor, out end, out via);
        }

        public static bool TryComputeTangentArcWithRadiusAndSweep(
            Point junction,
            double tangentX,
            double tangentY,
            Point directionHint,
            double radiusPx,
            double sweepDeg,
            bool? forceLeft,
            Point? lineAnchor,
            out Point end,
            out Point via)
        {
            end = via = default;
            if (!Normalize(ref tangentX, ref tangentY) || sweepDeg < 0.1)
                return false;

            double nlx = -tangentY;
            double nly = tangentX;
            double nrx = tangentY;
            double nry = -tangentX;
            if (!Normalize(ref nlx, ref nly) || !Normalize(ref nrx, ref nry))
                return false;

            double along = Dot(directionHint.X - junction.X, directionHint.Y - junction.Y, tangentX, tangentY);
            double left = TangentSideSign(tangentX, tangentY, junction, directionHint);
            bool mirror = forceLeft == false;
            if (mirror)
                left = -left;
            ResolveTangentQuadrant(along, left, out _, out bool bulgePositiveY);
            bool pickLeft = mirror ? !bulgePositiveY : bulgePositiveY;
            double nx = pickLeft ? nlx : nrx;
            double ny = pickLeft ? nly : nry;

            radiusPx = Math.Max(1e-3, radiusPx);
            var center = new Point(junction.X + nx * radiusPx, junction.Y + ny * radiusPx);

            if (lineAnchor is Point anchor
                && !CenterFootOnLineIsJunction(anchor, junction, tangentX, tangentY, center))
                return false;

            double aJunction = Math.Atan2(junction.Y - center.Y, junction.X - center.X);
            double sweepRad = Math.Abs(sweepDeg) * Math.PI / 180.0;
            const double minSweep = 0.08;
            if (sweepRad < minSweep)
                sweepRad = minSweep;
            if (pickLeft)
                sweepRad = Math.Abs(sweepRad);
            else
                sweepRad = -Math.Abs(sweepRad);

            double aEnd = aJunction + sweepRad;
            end = new Point(
                center.X + radiusPx * Math.Cos(aEnd),
                center.Y + radiusPx * Math.Sin(aEnd));

            if (Dist(junction, end) < 1e-3)
                return false;

            double aMid = aJunction + sweepRad * 0.5;
            via = new Point(
                center.X + radiusPx * Math.Cos(aMid),
                center.Y + radiusPx * Math.Sin(aMid));

            if (!RoiContourPath.IsValidArcVia(junction, via, end))
                return false;

            if (ArcDepartureAlignsTangent(junction, via, end, tangentX, tangentY))
                return true;

            sweepRad = -sweepRad;
            aEnd = aJunction + sweepRad;
            end = new Point(
                center.X + radiusPx * Math.Cos(aEnd),
                center.Y + radiusPx * Math.Sin(aEnd));
            aMid = aJunction + sweepRad * 0.5;
            via = new Point(
                center.X + radiusPx * Math.Cos(aMid),
                center.Y + radiusPx * Math.Sin(aMid));

            return RoiContourPath.IsValidArcVia(junction, via, end)
                && ArcDepartureAlignsTangent(junction, via, end, tangentX, tangentY);
        }

        private static bool TryMeasureArcSweepDegrees(
            Point start,
            Point via,
            Point end,
            Point center,
            out double sweepDeg)
        {
            sweepDeg = 0;
            double a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
            double av = Math.Atan2(via.Y - center.Y, via.X - center.X);
            double a1 = Math.Atan2(end.Y - center.Y, end.X - center.X);
            double sweepCcw = NormalizeSweepRad(a0, a1, ccw: true);
            double tv = NormalizeAngleRad(av - a0);
            bool viaOnCcw = tv <= sweepCcw + 1e-9;
            double sweep = viaOnCcw ? sweepCcw : NormalizeSweepRad(a0, a1, ccw: false);
            sweepDeg = Math.Abs(sweep) * 180.0 / Math.PI;
            return sweepDeg > 0.1;
        }

        private static double NormalizeAngleRad(double a)
        {
            while (a < 0)
                a += 2 * Math.PI;
            while (a >= 2 * Math.PI)
                a -= 2 * Math.PI;
            return a;
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
            double rlen = Math.Sqrt(rx * rx + ry * ry);
            if (rlen < 1e-9)
                return false;
            return Math.Abs(Dot(rx, ry, tangentX, tangentY)) <= 0.05 * rlen;
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

            if (!TryPickViaAtMidSweep(junction, end, center, radius, tangentX, tangentY, out via))
                return false;

            if (!RoiContourPath.IsValidArcVia(junction, via, end))
                return false;

            return ArcDepartureAlignsTangent(junction, via, end, tangentX, tangentY);
        }

        /// <summary>
        /// 在已定切圆上取弧中点作 via。via 贴在起点附近时，SampleArc 会把劣弧判成 270° 优弧。
        /// </summary>
        private static bool TryPickViaAtMidSweep(
            Point junction,
            Point end,
            Point center,
            double radius,
            double tangentX,
            double tangentY,
            out Point via)
        {
            via = default;
            if (!TryPickSweepWithTangentDeparture(
                    junction, end, center, radius, tangentX, tangentY, out double sweep))
                return false;

            double a0 = Math.Atan2(junction.Y - center.Y, junction.X - center.X);
            double viaAng = a0 + sweep * 0.5;
            via = new Point(
                center.X + radius * Math.Cos(viaAng),
                center.Y + radius * Math.Sin(viaAng));
            return Dist(junction, via) > 1e-6 && Dist(via, end) > 1e-6;
        }

        private static bool TryPickSweepWithTangentDeparture(
            Point junction,
            Point end,
            Point center,
            double radius,
            double tangentX,
            double tangentY,
            out double sweep)
        {
            sweep = 0;
            double a0 = Math.Atan2(junction.Y - center.Y, junction.X - center.X);
            double sweepCcw = NormalizeSweepRad(a0, Math.Atan2(end.Y - center.Y, end.X - center.X), ccw: true);
            double sweepCw = NormalizeSweepRad(a0, Math.Atan2(end.Y - center.Y, end.X - center.X), ccw: false);

            double best = 0;
            bool found = false;
            foreach (double candidate in new[] { sweepCcw, sweepCw })
            {
                if (Math.Abs(candidate) < 1e-9)
                    continue;

                // 先在弧中点试走向，避免 via 靠近起点时误判成优弧
                double testAng = a0 + candidate * 0.5;
                var testVia = new Point(
                    center.X + radius * Math.Cos(testAng),
                    center.Y + radius * Math.Sin(testAng));

                if (!ArcDepartureAlignsTangent(junction, testVia, end, tangentX, tangentY))
                    continue;

                if (!found || Math.Abs(candidate) < Math.Abs(best) - 1e-9)
                {
                    best = candidate;
                    found = true;
                }
            }

            if (!found)
                return false;

            sweep = best;
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
