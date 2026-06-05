using System;
using System.Collections.Generic;
using System.Linq;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    internal static class RoiSnapshotCoordinateTransform
    {
        public const string UnitMillimeter = "mm";
        public const string UnitPixel = "px";

        public static bool IsMillimeterDocument(RoiSnapshotDocument doc) =>
            doc.Version >= RoiSnapshotDocument.CurrentVersion
            && string.Equals(doc.CoordinateUnit, UnitMillimeter, StringComparison.OrdinalIgnoreCase);

        public static HalconShapeModelUiSettings ConvertPixelSettingsToMillimeters(
            HalconShapeModelUiSettings px,
            in AffineTransform t)
        {
            var mm = CloneSettingsShell(px);

            var topLeft = HalconGeometryContourBuilder.ImagePixelToWorldMm(px.RoiRectX, px.RoiRectY, t);
            (double rectWMm, double rectHMm) = HalconGeometryContourBuilder.PixelSizeToWorldMm(
                px.RoiRectW, px.RoiRectH, px.RoiRectX, px.RoiRectY, t);
            mm.RoiRectX = topLeft.X;
            mm.RoiRectY = topLeft.Y;
            mm.RoiRectW = rectWMm;
            mm.RoiRectH = rectHMm;

            var rotCenter = HalconGeometryContourBuilder.ImagePixelToWorldMm(px.RotRectCenterX, px.RotRectCenterY, t);
            (double rotWMm, double rotHMm) = HalconGeometryContourBuilder.PixelSizeToWorldMm(
                px.RotRectWidth, px.RotRectHeight, px.RotRectCenterX, px.RotRectCenterY, t);
            mm.RotRectCenterX = rotCenter.X;
            mm.RotRectCenterY = rotCenter.Y;
            mm.RotRectWidth = rotWMm;
            mm.RotRectHeight = rotHMm;
            mm.RotRectAngleDeg = px.RotRectAngleDeg;

            var circleCenter = HalconGeometryContourBuilder.ImagePixelToWorldMm(px.CircleCenterX, px.CircleCenterY, t);
            mm.CircleCenterX = circleCenter.X;
            mm.CircleCenterY = circleCenter.Y;
            mm.CircleRadius = HalconGeometryContourBuilder.PixelRadiusToWorldMm(
                px.CircleRadius, px.CircleCenterX, px.CircleCenterY, t);

            mm.PolygonPath = ConvertPathPixelToMillimeter(px.PolygonPath, t);
            mm.RingOuterPath = ConvertPathPixelToMillimeter(px.RingOuterPath, t);
            mm.RingInnerPath = ConvertPathPixelToMillimeter(px.RingInnerPath, t);
            mm.OpenTrajectoryDraft = ConvertPathPixelToMillimeter(px.OpenTrajectoryDraft, t);

            mm.OpenTrajectoryPaths = new List<HalconShapeModelRoiPathDto>();
            if (px.OpenTrajectoryPaths != null)
            {
                foreach (HalconShapeModelRoiPathDto? path in px.OpenTrajectoryPaths)
                {
                    if (ConvertPathPixelToMillimeter(path, t) is HalconShapeModelRoiPathDto converted)
                        mm.OpenTrajectoryPaths.Add(converted);
                }
            }

            mm.OpenTrajectoryNames = px.OpenTrajectoryNames?.ToList() ?? new List<string>();
            mm.OpenTrajectoryConnectors = ConvertConnectorsPixelToMillimeter(px.OpenTrajectoryConnectors, t);
            return mm;
        }

        public static HalconShapeModelUiSettings ConvertMillimeterSettingsToPixels(
            HalconShapeModelUiSettings mm,
            in AffineTransform t)
        {
            var px = CloneSettingsShell(mm);

            var topLeft = HalconGeometryContourBuilder.WorldMmToImagePixel(mm.RoiRectX, mm.RoiRectY, t);
            px.RoiRectX = topLeft.X;
            px.RoiRectY = topLeft.Y;
            px.RoiRectW = HalconGeometryContourBuilder.WorldMmWidthToPixelWidth(
                mm.RoiRectW, topLeft.X, topLeft.Y, t);
            px.RoiRectH = HalconGeometryContourBuilder.WorldMmHeightToPixelHeight(
                mm.RoiRectH, topLeft.X, topLeft.Y, t);

            var rotCenter = HalconGeometryContourBuilder.WorldMmToImagePixel(mm.RotRectCenterX, mm.RotRectCenterY, t);
            px.RotRectCenterX = rotCenter.X;
            px.RotRectCenterY = rotCenter.Y;
            px.RotRectWidth = HalconGeometryContourBuilder.WorldMmWidthToPixelWidth(
                mm.RotRectWidth, rotCenter.X, rotCenter.Y, t);
            px.RotRectHeight = HalconGeometryContourBuilder.WorldMmHeightToPixelHeight(
                mm.RotRectHeight, rotCenter.X, rotCenter.Y, t);
            px.RotRectAngleDeg = mm.RotRectAngleDeg;

            var circleCenter = HalconGeometryContourBuilder.WorldMmToImagePixel(mm.CircleCenterX, mm.CircleCenterY, t);
            px.CircleCenterX = circleCenter.X;
            px.CircleCenterY = circleCenter.Y;
            px.CircleRadius = HalconGeometryContourBuilder.WorldMmRadiusToPixelRadius(
                mm.CircleRadius, circleCenter.X, circleCenter.Y, t);

            px.PolygonPath = ConvertPathMillimeterToPixel(mm.PolygonPath, t);
            px.RingOuterPath = ConvertPathMillimeterToPixel(mm.RingOuterPath, t);
            px.RingInnerPath = ConvertPathMillimeterToPixel(mm.RingInnerPath, t);
            px.OpenTrajectoryDraft = ConvertPathMillimeterToPixel(mm.OpenTrajectoryDraft, t);

            px.OpenTrajectoryPaths = new List<HalconShapeModelRoiPathDto>();
            if (mm.OpenTrajectoryPaths != null)
            {
                foreach (HalconShapeModelRoiPathDto? path in mm.OpenTrajectoryPaths)
                {
                    if (ConvertPathMillimeterToPixel(path, t) is HalconShapeModelRoiPathDto converted)
                        px.OpenTrajectoryPaths.Add(converted);
                }
            }

            px.OpenTrajectoryNames = mm.OpenTrajectoryNames?.ToList() ?? new List<string>();
            px.OpenTrajectoryConnectors = ConvertConnectorsMillimeterToPixel(mm.OpenTrajectoryConnectors, t);
            return px;
        }

        private static HalconShapeModelUiSettings CloneSettingsShell(HalconShapeModelUiSettings src) =>
            new()
            {
                RoiMode = src.RoiMode ?? "none",
                OpenTrajectoryNames = src.OpenTrajectoryNames?.ToList() ?? new List<string>()
            };

        private static HalconShapeModelRoiPathDto? ConvertPathPixelToMillimeter(
            HalconShapeModelRoiPathDto? src,
            in AffineTransform t)
        {
            if (src == null || src.VertexXs.Count == 0)
                return null;

            var dst = new HalconShapeModelRoiPathDto { IsClosed = src.IsClosed };
            for (int i = 0; i < src.VertexXs.Count; i++)
            {
                Point2D w = HalconGeometryContourBuilder.ImagePixelToWorldMm(src.VertexXs[i], src.VertexYs[i], t);
                dst.VertexXs.Add(w.X);
                dst.VertexYs.Add(w.Y);
            }

            dst.EdgeKinds.AddRange(src.EdgeKinds);
            for (int i = 0; i < src.ArcViaXs.Count; i++)
            {
                double? vx = src.ArcViaXs[i];
                double? vy = i < src.ArcViaYs.Count ? src.ArcViaYs[i] : null;
                if (vx is double col && vy is double row)
                {
                    Point2D w = HalconGeometryContourBuilder.ImagePixelToWorldMm(col, row, t);
                    dst.ArcViaXs.Add(w.X);
                    dst.ArcViaYs.Add(w.Y);
                }
                else
                {
                    dst.ArcViaXs.Add(null);
                    dst.ArcViaYs.Add(null);
                }
            }

            return dst;
        }

        private static HalconShapeModelRoiPathDto? ConvertPathMillimeterToPixel(
            HalconShapeModelRoiPathDto? src,
            in AffineTransform t)
        {
            if (src == null || src.VertexXs.Count == 0)
                return null;

            var dst = new HalconShapeModelRoiPathDto { IsClosed = src.IsClosed };
            for (int i = 0; i < src.VertexXs.Count; i++)
            {
                Point2D p = HalconGeometryContourBuilder.WorldMmToImagePixel(src.VertexXs[i], src.VertexYs[i], t);
                dst.VertexXs.Add(p.X);
                dst.VertexYs.Add(p.Y);
            }

            dst.EdgeKinds.AddRange(src.EdgeKinds);
            for (int i = 0; i < src.ArcViaXs.Count; i++)
            {
                double? wx = src.ArcViaXs[i];
                double? wy = i < src.ArcViaYs.Count ? src.ArcViaYs[i] : null;
                if (wx is double x && wy is double y)
                {
                    Point2D p = HalconGeometryContourBuilder.WorldMmToImagePixel(x, y, t);
                    dst.ArcViaXs.Add(p.X);
                    dst.ArcViaYs.Add(p.Y);
                }
                else
                {
                    dst.ArcViaXs.Add(null);
                    dst.ArcViaYs.Add(null);
                }
            }

            return dst;
        }

        private static List<HalconShapeModelRoiConnectorDto> ConvertConnectorsPixelToMillimeter(
            List<HalconShapeModelRoiConnectorDto>? src,
            in AffineTransform t)
        {
            var list = new List<HalconShapeModelRoiConnectorDto>();
            if (src == null)
                return list;

            foreach (HalconShapeModelRoiConnectorDto? c in src)
            {
                if (c == null)
                    continue;
                Point2D s = HalconGeometryContourBuilder.ImagePixelToWorldMm(c.StartX, c.StartY, t);
                Point2D e = HalconGeometryContourBuilder.ImagePixelToWorldMm(c.EndX, c.EndY, t);
                list.Add(new HalconShapeModelRoiConnectorDto
                {
                    StartX = s.X,
                    StartY = s.Y,
                    EndX = e.X,
                    EndY = e.Y
                });
            }

            return list;
        }

        private static List<HalconShapeModelRoiConnectorDto> ConvertConnectorsMillimeterToPixel(
            List<HalconShapeModelRoiConnectorDto>? src,
            in AffineTransform t)
        {
            var list = new List<HalconShapeModelRoiConnectorDto>();
            if (src == null)
                return list;

            foreach (HalconShapeModelRoiConnectorDto? c in src)
            {
                if (c == null)
                    continue;
                Point2D s = HalconGeometryContourBuilder.WorldMmToImagePixel(c.StartX, c.StartY, t);
                Point2D e = HalconGeometryContourBuilder.WorldMmToImagePixel(c.EndX, c.EndY, t);
                list.Add(new HalconShapeModelRoiConnectorDto
                {
                    StartX = s.X,
                    StartY = s.Y,
                    EndX = e.X,
                    EndY = e.Y
                });
            }

            return list;
        }
    }
}
