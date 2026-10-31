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
            in AffineTransform t,
            bool preserveAngle = true)
        {
            var mm = CloneSettingsShell(px);

            var topLeft = HalconGeometryContourBuilder.ImagePixelToWorldMm(px.RoiRectX, px.RoiRectY, t, preserveAngle);
            (double rectWMm, double rectHMm) = HalconGeometryContourBuilder.PixelSizeToWorldMm(
                px.RoiRectW, px.RoiRectH, px.RoiRectX, px.RoiRectY, t, preserveAngle);
            mm.RoiRectX = topLeft.X;
            mm.RoiRectY = topLeft.Y;
            mm.RoiRectW = rectWMm;
            mm.RoiRectH = rectHMm;

            var rotCenter = HalconGeometryContourBuilder.ImagePixelToWorldMm(px.RotRectCenterX, px.RotRectCenterY, t, preserveAngle);
            (double rotWMm, double rotHMm) = HalconGeometryContourBuilder.PixelSizeToWorldMm(
                px.RotRectWidth, px.RotRectHeight, px.RotRectCenterX, px.RotRectCenterY, t, preserveAngle);
            mm.RotRectCenterX = rotCenter.X;
            mm.RotRectCenterY = rotCenter.Y;
            mm.RotRectWidth = rotWMm;
            mm.RotRectHeight = rotHMm;
            mm.RotRectAngleDeg = px.RotRectAngleDeg;

            var circleCenter = HalconGeometryContourBuilder.ImagePixelToWorldMm(px.CircleCenterX, px.CircleCenterY, t, preserveAngle);
            mm.CircleCenterX = circleCenter.X;
            mm.CircleCenterY = circleCenter.Y;
            mm.CircleRadius = HalconGeometryContourBuilder.PixelRadiusToWorldMm(
                px.CircleRadius, px.CircleCenterX, px.CircleCenterY, t, preserveAngle);

            mm.PolygonPath = ConvertPathPixelToMillimeter(px.PolygonPath, t, preserveAngle);
            mm.RingOuterPath = ConvertPathPixelToMillimeter(px.RingOuterPath, t, preserveAngle);
            mm.RingInnerPath = ConvertPathPixelToMillimeter(px.RingInnerPath, t, preserveAngle);
            mm.OpenTrajectoryDraft = ConvertPathPixelToMillimeter(px.OpenTrajectoryDraft, t, preserveAngle);

            mm.OpenTrajectoryPaths = new List<HalconShapeModelRoiPathDto>();
            if (px.OpenTrajectoryPaths != null)
            {
                foreach (HalconShapeModelRoiPathDto? path in px.OpenTrajectoryPaths)
                {
                    if (ConvertPathPixelToMillimeter(path, t, preserveAngle) is HalconShapeModelRoiPathDto converted)
                        mm.OpenTrajectoryPaths.Add(converted);
                }
            }

            mm.OpenTrajectoryNames = px.OpenTrajectoryNames?.ToList() ?? new List<string>();
            mm.OpenTrajectoryConnectors = ConvertConnectorsPixelToMillimeter(px.OpenTrajectoryConnectors, t, preserveAngle);
            mm.OpenTrajectoryUndo = ConvertHistoryListPixelToMillimeter(px.OpenTrajectoryUndo, t, preserveAngle);
            mm.OpenTrajectoryRedo = ConvertHistoryListPixelToMillimeter(px.OpenTrajectoryRedo, t, preserveAngle);
            return mm;
        }

        public static HalconShapeModelUiSettings ConvertMillimeterSettingsToPixels(
            HalconShapeModelUiSettings mm,
            in AffineTransform t,
            bool preserveAngle = true)
        {
            var px = CloneSettingsShell(mm);

            var topLeft = HalconGeometryContourBuilder.WorldMmToImagePixel(mm.RoiRectX, mm.RoiRectY, t, preserveAngle);
            px.RoiRectX = topLeft.X;
            px.RoiRectY = topLeft.Y;
            px.RoiRectW = HalconGeometryContourBuilder.WorldMmWidthToPixelWidth(
                mm.RoiRectW, topLeft.X, topLeft.Y, t, preserveAngle);
            px.RoiRectH = HalconGeometryContourBuilder.WorldMmHeightToPixelHeight(
                mm.RoiRectH, topLeft.X, topLeft.Y, t, preserveAngle);

            var rotCenter = HalconGeometryContourBuilder.WorldMmToImagePixel(mm.RotRectCenterX, mm.RotRectCenterY, t, preserveAngle);
            px.RotRectCenterX = rotCenter.X;
            px.RotRectCenterY = rotCenter.Y;
            px.RotRectWidth = HalconGeometryContourBuilder.WorldMmWidthToPixelWidth(
                mm.RotRectWidth, rotCenter.X, rotCenter.Y, t, preserveAngle);
            px.RotRectHeight = HalconGeometryContourBuilder.WorldMmHeightToPixelHeight(
                mm.RotRectHeight, rotCenter.X, rotCenter.Y, t, preserveAngle);
            px.RotRectAngleDeg = mm.RotRectAngleDeg;

            var circleCenter = HalconGeometryContourBuilder.WorldMmToImagePixel(mm.CircleCenterX, mm.CircleCenterY, t, preserveAngle);
            px.CircleCenterX = circleCenter.X;
            px.CircleCenterY = circleCenter.Y;
            px.CircleRadius = HalconGeometryContourBuilder.WorldMmRadiusToPixelRadius(
                mm.CircleRadius, circleCenter.X, circleCenter.Y, t, preserveAngle);

            px.PolygonPath = ConvertPathMillimeterToPixel(mm.PolygonPath, t, preserveAngle);
            px.RingOuterPath = ConvertPathMillimeterToPixel(mm.RingOuterPath, t, preserveAngle);
            px.RingInnerPath = ConvertPathMillimeterToPixel(mm.RingInnerPath, t, preserveAngle);
            px.OpenTrajectoryDraft = ConvertPathMillimeterToPixel(mm.OpenTrajectoryDraft, t, preserveAngle);

            px.OpenTrajectoryPaths = new List<HalconShapeModelRoiPathDto>();
            if (mm.OpenTrajectoryPaths != null)
            {
                foreach (HalconShapeModelRoiPathDto? path in mm.OpenTrajectoryPaths)
                {
                    if (ConvertPathMillimeterToPixel(path, t, preserveAngle) is HalconShapeModelRoiPathDto converted)
                        px.OpenTrajectoryPaths.Add(converted);
                }
            }

            px.OpenTrajectoryNames = mm.OpenTrajectoryNames?.ToList() ?? new List<string>();
            px.OpenTrajectoryConnectors = ConvertConnectorsMillimeterToPixel(mm.OpenTrajectoryConnectors, t, preserveAngle);
            px.OpenTrajectoryUndo = ConvertHistoryListMillimeterToPixel(mm.OpenTrajectoryUndo, t, preserveAngle);
            px.OpenTrajectoryRedo = ConvertHistoryListMillimeterToPixel(mm.OpenTrajectoryRedo, t, preserveAngle);
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
            in AffineTransform t, bool preserveAngle)
        {
            if (src == null || src.VertexXs.Count == 0)
                return null;

            var dst = new HalconShapeModelRoiPathDto { IsClosed = src.IsClosed };
            for (int i = 0; i < src.VertexXs.Count; i++)
            {
                Point2D w = HalconGeometryContourBuilder.ImagePixelToWorldMm(src.VertexXs[i], src.VertexYs[i], t, preserveAngle);
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
                    Point2D w = HalconGeometryContourBuilder.ImagePixelToWorldMm(col, row, t, preserveAngle);
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
            in AffineTransform t, bool preserveAngle)
        {
            if (src == null || src.VertexXs.Count == 0)
                return null;

            var dst = new HalconShapeModelRoiPathDto { IsClosed = src.IsClosed };
            for (int i = 0; i < src.VertexXs.Count; i++)
            {
                Point2D p = HalconGeometryContourBuilder.WorldMmToImagePixel(src.VertexXs[i], src.VertexYs[i], t, preserveAngle);
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
                    Point2D p = HalconGeometryContourBuilder.WorldMmToImagePixel(x, y, t, preserveAngle);
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
            in AffineTransform t, bool preserveAngle)
        {
            var list = new List<HalconShapeModelRoiConnectorDto>();
            if (src == null)
                return list;

            foreach (HalconShapeModelRoiConnectorDto? c in src)
            {
                if (c == null)
                    continue;
                Point2D s = HalconGeometryContourBuilder.ImagePixelToWorldMm(c.StartX, c.StartY, t, preserveAngle);
                Point2D e = HalconGeometryContourBuilder.ImagePixelToWorldMm(c.EndX, c.EndY, t, preserveAngle);
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
            in AffineTransform t, bool preserveAngle)
        {
            var list = new List<HalconShapeModelRoiConnectorDto>();
            if (src == null)
                return list;

            foreach (HalconShapeModelRoiConnectorDto? c in src)
            {
                if (c == null)
                    continue;
                Point2D s = HalconGeometryContourBuilder.WorldMmToImagePixel(c.StartX, c.StartY, t, preserveAngle);
                Point2D e = HalconGeometryContourBuilder.WorldMmToImagePixel(c.EndX, c.EndY, t, preserveAngle);
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

        private static List<OpenTrajHistoryItemDto> ConvertHistoryListPixelToMillimeter(
            List<OpenTrajHistoryItemDto>? src,
            in AffineTransform t, bool preserveAngle)
        {
            var list = new List<OpenTrajHistoryItemDto>();
            if (src == null)
                return list;
            foreach (OpenTrajHistoryItemDto? item in src)
            {
                if (item == null)
                    continue;
                list.Add(ConvertHistoryItemPixelToMillimeter(item, t, preserveAngle));
            }

            return list;
        }

        private static List<OpenTrajHistoryItemDto> ConvertHistoryListMillimeterToPixel(
            List<OpenTrajHistoryItemDto>? src,
            in AffineTransform t, bool preserveAngle)
        {
            var list = new List<OpenTrajHistoryItemDto>();
            if (src == null)
                return list;
            foreach (OpenTrajHistoryItemDto? item in src)
            {
                if (item == null)
                    continue;
                list.Add(ConvertHistoryItemMillimeterToPixel(item, t, preserveAngle));
            }

            return list;
        }

        private static OpenTrajHistoryItemDto ConvertHistoryItemPixelToMillimeter(
            OpenTrajHistoryItemDto src,
            in AffineTransform t, bool preserveAngle)
        {
            OpenTrajHistoryItemDto dst = RoiSnapshotFileIo.CloneHistoryItem(src);
            Point2D v = HalconGeometryContourBuilder.ImagePixelToWorldMm(src.VertexX, src.VertexY, t, preserveAngle);
            dst.VertexX = v.X;
            dst.VertexY = v.Y;
            if (src.ArcViaX is double avx && src.ArcViaY is double avy)
            {
                Point2D via = HalconGeometryContourBuilder.ImagePixelToWorldMm(avx, avy, t, preserveAngle);
                dst.ArcViaX = via.X;
                dst.ArcViaY = via.Y;
            }

            if (src.Connector != null)
            {
                var connList = ConvertConnectorsPixelToMillimeter(
                    new List<HalconShapeModelRoiConnectorDto> { src.Connector }, t, preserveAngle);
                dst.Connector = connList.Count > 0 ? connList[0] : null;
            }

            dst.AutoFinishedPath = ConvertPathPixelToMillimeter(src.AutoFinishedPath, t, preserveAngle);
            dst.AutoFinishedPreviousPath = ConvertPathPixelToMillimeter(src.AutoFinishedPreviousPath, t, preserveAngle);
            dst.FinishedPath = ConvertPathPixelToMillimeter(src.FinishedPath, t, preserveAngle);
            dst.FinishPreviousPath = ConvertPathPixelToMillimeter(src.FinishPreviousPath, t, preserveAngle);
            ConvertSceneFieldsPixelToMillimeter(src, dst, t, preserveAngle);

            if (src.ClearedSingletonX is double sx && src.ClearedSingletonY is double sy)
            {
                Point2D s = HalconGeometryContourBuilder.ImagePixelToWorldMm(sx, sy, t, preserveAngle);
                dst.ClearedSingletonX = s.X;
                dst.ClearedSingletonY = s.Y;
            }

            Point2D resume = HalconGeometryContourBuilder.ImagePixelToWorldMm(src.ResumeVertexX, src.ResumeVertexY, t, preserveAngle);
            dst.ResumeVertexX = resume.X;
            dst.ResumeVertexY = resume.Y;
            return dst;
        }

        private static OpenTrajHistoryItemDto ConvertHistoryItemMillimeterToPixel(
            OpenTrajHistoryItemDto src,
            in AffineTransform t, bool preserveAngle)
        {
            OpenTrajHistoryItemDto dst = RoiSnapshotFileIo.CloneHistoryItem(src);
            Point2D v = HalconGeometryContourBuilder.WorldMmToImagePixel(src.VertexX, src.VertexY, t, preserveAngle);
            dst.VertexX = v.X;
            dst.VertexY = v.Y;
            if (src.ArcViaX is double avx && src.ArcViaY is double avy)
            {
                Point2D via = HalconGeometryContourBuilder.WorldMmToImagePixel(avx, avy, t, preserveAngle);
                dst.ArcViaX = via.X;
                dst.ArcViaY = via.Y;
            }

            if (src.Connector != null)
            {
                var connList = ConvertConnectorsMillimeterToPixel(
                    new List<HalconShapeModelRoiConnectorDto> { src.Connector }, t, preserveAngle);
                dst.Connector = connList.Count > 0 ? connList[0] : null;
            }

            dst.AutoFinishedPath = ConvertPathMillimeterToPixel(src.AutoFinishedPath, t, preserveAngle);
            dst.AutoFinishedPreviousPath = ConvertPathMillimeterToPixel(src.AutoFinishedPreviousPath, t, preserveAngle);
            dst.FinishedPath = ConvertPathMillimeterToPixel(src.FinishedPath, t, preserveAngle);
            dst.FinishPreviousPath = ConvertPathMillimeterToPixel(src.FinishPreviousPath, t, preserveAngle);
            ConvertSceneFieldsMillimeterToPixel(src, dst, t, preserveAngle);

            if (src.ClearedSingletonX is double sx && src.ClearedSingletonY is double sy)
            {
                Point2D s = HalconGeometryContourBuilder.WorldMmToImagePixel(sx, sy, t, preserveAngle);
                dst.ClearedSingletonX = s.X;
                dst.ClearedSingletonY = s.Y;
            }

            Point2D resume = HalconGeometryContourBuilder.WorldMmToImagePixel(src.ResumeVertexX, src.ResumeVertexY, t, preserveAngle);
            dst.ResumeVertexX = resume.X;
            dst.ResumeVertexY = resume.Y;
            return dst;
        }

        private static void ConvertSceneFieldsPixelToMillimeter(
            OpenTrajHistoryItemDto src, OpenTrajHistoryItemDto dst, in AffineTransform t, bool preserveAngle)
        {
            dst.SceneBeforeDraft = ConvertPathPixelToMillimeter(src.SceneBeforeDraft, t, preserveAngle);
            dst.SceneAfterDraft = ConvertPathPixelToMillimeter(src.SceneAfterDraft, t, preserveAngle);
            dst.SceneBeforePaths = ConvertPathListPixelToMillimeter(src.SceneBeforePaths, t, preserveAngle);
            dst.SceneAfterPaths = ConvertPathListPixelToMillimeter(src.SceneAfterPaths, t, preserveAngle);
            dst.SceneBeforeConnectors = ConvertConnectorsPixelToMillimeter(src.SceneBeforeConnectors, t, preserveAngle);
            dst.SceneAfterConnectors = ConvertConnectorsPixelToMillimeter(src.SceneAfterConnectors, t, preserveAngle);
        }

        private static void ConvertSceneFieldsMillimeterToPixel(
            OpenTrajHistoryItemDto src, OpenTrajHistoryItemDto dst, in AffineTransform t, bool preserveAngle)
        {
            dst.SceneBeforeDraft = ConvertPathMillimeterToPixel(src.SceneBeforeDraft, t, preserveAngle);
            dst.SceneAfterDraft = ConvertPathMillimeterToPixel(src.SceneAfterDraft, t, preserveAngle);
            dst.SceneBeforePaths = ConvertPathListMillimeterToPixel(src.SceneBeforePaths, t, preserveAngle);
            dst.SceneAfterPaths = ConvertPathListMillimeterToPixel(src.SceneAfterPaths, t, preserveAngle);
            dst.SceneBeforeConnectors = ConvertConnectorsMillimeterToPixel(src.SceneBeforeConnectors, t, preserveAngle);
            dst.SceneAfterConnectors = ConvertConnectorsMillimeterToPixel(src.SceneAfterConnectors, t, preserveAngle);
        }

        private static List<HalconShapeModelRoiPathDto>? ConvertPathListPixelToMillimeter(
            List<HalconShapeModelRoiPathDto>? src, in AffineTransform t, bool preserveAngle)
        {
            if (src == null)
                return null;
            var list = new List<HalconShapeModelRoiPathDto>(src.Count);
            foreach (HalconShapeModelRoiPathDto? p in src)
            {
                if (ConvertPathPixelToMillimeter(p, t, preserveAngle) is HalconShapeModelRoiPathDto c)
                    list.Add(c);
            }

            return list;
        }

        private static List<HalconShapeModelRoiPathDto>? ConvertPathListMillimeterToPixel(
            List<HalconShapeModelRoiPathDto>? src, in AffineTransform t, bool preserveAngle)
        {
            if (src == null)
                return null;
            var list = new List<HalconShapeModelRoiPathDto>(src.Count);
            foreach (HalconShapeModelRoiPathDto? p in src)
            {
                if (ConvertPathMillimeterToPixel(p, t, preserveAngle) is HalconShapeModelRoiPathDto c)
                    list.Add(c);
            }

            return list;
        }
    }
}
