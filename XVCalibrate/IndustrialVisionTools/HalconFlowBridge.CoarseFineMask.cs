#if HALCON_ENABLED
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using CalibOperatorPInvoke;
using HalconDotNet;

namespace CalibOperatorCLI_Example
{
    internal static partial class HalconFlowBridge
    {
        private static readonly object HalconModelIdLock = new();
        private static long _halconModelNextId = 1;

        private static long AllocateHalconModelId()
        {
            lock (HalconModelIdLock)
                return _halconModelNextId++;
        }

        /// <summary>新版 HalconDotNet 句柄不可再当 long 用；用 id 持有 <see cref="HShapeModel"/> 生命周期。</summary>
        private static class HalconShapeModelRegistry
        {
            private static readonly object Lock = new();
            private static readonly Dictionary<long, HShapeModel> Models = new();
            private static readonly Dictionary<long, HalconFlowModelKind> ModelKinds = new();

            public static long Register(HShapeModel model)
            {
                lock (Lock)
                {
                    long id = AllocateHalconModelId();
                    Models[id] = model;
                    ModelKinds[id] = HalconFlowModelKind.Shape;
                    return id;
                }
            }

            public static bool TryGet(long id, out HShapeModel model)
            {
                lock (Lock)
                    return Models.TryGetValue(id, out model!);
            }

            public static HShapeModel Get(long id)
            {
                lock (Lock)
                {
                    if (!Models.TryGetValue(id, out HShapeModel? model))
                        throw new InvalidOperationException($"形状模型 ModelId={id} 不存在或已释放");
                    return model;
                }
            }

            public static void Release(long id)
            {
                lock (Lock)
                {
                    if (!Models.Remove(id, out HShapeModel? model))
                        return;
                    ModelKinds.Remove(id);
                    model.Dispose();
                }
            }

            public static bool TryGetKind(long id, out HalconFlowModelKind kind)
            {
                lock (Lock)
                    return ModelKinds.TryGetValue(id, out kind);
            }
        }

        private static class HalconDeformableModelRegistry
        {
            private static readonly object Lock = new();
            private static readonly Dictionary<long, HDeformableModel> Models = new();
            private static readonly Dictionary<long, HalconFlowModelKind> ModelKinds = new();
            private static readonly Dictionary<long, HalconDeformableModelSubtype> Subtypes = new();

            public static long Register(HDeformableModel model, HalconDeformableModelSubtype subtype = HalconDeformableModelSubtype.Local)
            {
                lock (Lock)
                {
                    long id = AllocateHalconModelId();
                    Models[id] = model;
                    ModelKinds[id] = HalconFlowModelKind.Deformable;
                    Subtypes[id] = subtype;
                    return id;
                }
            }

            public static HalconDeformableModelSubtype GetSubtype(long id)
            {
                lock (Lock)
                {
                    if (!Subtypes.TryGetValue(id, out HalconDeformableModelSubtype subtype))
                        throw new InvalidOperationException($"可变形模型 ModelId={id} 不存在或已释放");
                    return subtype;
                }
            }

            public static HalconDeformableModelSubtype TryDetectSubtype(HDeformableModel model)
            {
                try
                {
                    HTuple value = model.GetDeformableModelParams("model_type");
                    if (value == null || value.Length == 0)
                        return HalconDeformableModelSubtype.Local;
                    string t = value[0].S.ToLowerInvariant();
                    if (t.Contains("planar") && t.Contains("uncalib"))
                        return HalconDeformableModelSubtype.PlanarUncalib;
                    if (t.Contains("planar"))
                        return HalconDeformableModelSubtype.PlanarUncalib;
                }
                catch
                {
                    // 旧版 HALCON 可能无 model_type
                }
                return HalconDeformableModelSubtype.Local;
            }

            public static bool TryGet(long id, out HDeformableModel model)
            {
                lock (Lock)
                    return Models.TryGetValue(id, out model!);
            }

            public static HDeformableModel Get(long id)
            {
                lock (Lock)
                {
                    if (!Models.TryGetValue(id, out HDeformableModel? model))
                        throw new InvalidOperationException($"可变形模型 ModelId={id} 不存在或已释放");
                    return model;
                }
            }

            public static void Release(long id)
            {
                lock (Lock)
                {
                    if (!Models.Remove(id, out HDeformableModel? model))
                        return;
                    ModelKinds.Remove(id);
                    Subtypes.Remove(id);
                    model.Dispose();
                }
            }

            public static bool TryGetKind(long id, out HalconFlowModelKind kind)
            {
                lock (Lock)
                    return ModelKinds.TryGetValue(id, out kind);
            }
        }
        private static HalconDeformableModelSubtype ResolveDeformableSubtype(HalconShapeModelCreateOptions opt) =>
            opt.ModelKind == HalconShapeModelKind.PlanarDeformable
                ? HalconDeformableModelSubtype.PlanarUncalib
                : opt.DeformableSubtype;

        private static HObject SelectContoursNearReferenceXld(HObject edgesXld, HObject refXld, double maxDistPx, int minContourPoints)
        {
            HOperatorSet.GenEmptyObj(out HObject acc);
            int n = edgesXld.CountObj();
            for (int i = 1; i <= n; i++)
            {
                HObject one = edgesXld.SelectObj(i);
                try
                {
                    int len = ContourXldToPointArray(one).Length;
                    if (len < minContourPoints)
                        continue;

                    HOperatorSet.DistanceContoursXld(one, refXld, out HObject distContour, "point_to_segment");
                    try
                    {
                        HOperatorSet.GetContourAttribXld(distContour, "distance", out HTuple dist);
                        if (dist == null || dist.Length == 0)
                            continue;

                        double dMin = dist.TupleMin().D;
                        if (dMin > maxDistPx)
                            continue;
                    }
                    finally
                    {
                        distContour.Dispose();
                    }

                    HOperatorSet.ConcatObj(acc, one, out HObject merged);
                    acc.Dispose();
                    acc = merged;
                }
                finally
                {
                    one.Dispose();
                }
            }

            return acc;
        }

        /// <summary>
        /// 按参考闭合路径各处的期望梯度方向，用 <c>segment_contour_attrib_xld</c> 保留匹配的 Canny 子轮廓（保留 edge_direction）。
        /// </summary>
        public static HObject FilterEdgesXldByReferencePolarity(
            HObject edgesXld,
            IReadOnlyList<Point2D> referenceDense,
            RoiGradientPolarity polarity,
            double angleToleranceDeg = 28)
        {
            if (edgesXld == null || !edgesXld.IsInitialized() || edgesXld.CountObj() == 0)
                throw new ArgumentException("边缘轮廓无效", nameof(edgesXld));
            if (referenceDense == null || referenceDense.Count < 3)
                throw new ArgumentException("参考路径至少需要 3 个点", nameof(referenceDense));

            bool outward = polarity == RoiGradientPolarity.Outward;
            double tol = Math.Max(5.0, angleToleranceDeg) * Math.PI / 180.0;

            HOperatorSet.GenEmptyObj(out HObject acc);
            int added = 0;
            int step = Math.Max(1, referenceDense.Count / 28);

            for (int i = 0; i < referenceDense.Count; i += step)
            {
                double desired = ComputeDesiredGradientAngleRad(referenceDense, i, outward);
                double minA = desired - tol;
                double maxA = desired + tol;

                HOperatorSet.SegmentContourAttribXld(
                    edgesXld, out HObject part, "edge_direction", "and", minA, maxA);
                try
                {
                    if (!part.IsInitialized() || part.CountObj() == 0)
                        continue;

                    HOperatorSet.ConcatObj(acc, part, out HObject merged);
                    acc.Dispose();
                    acc = merged;
                    added++;
                }
                finally
                {
                    part.Dispose();
                }
            }

            if (added == 0)
            {
                acc.Dispose();
                throw new InvalidOperationException(
                    "按向内/向外梯度筛选后无剩余边缘，请调整 Canny 阈值、边带宽度，或切换梯度方向。");
            }

            return acc;
        }

        public static HalconXldContourBundle BundleFromXldContours(
            HObject xld,
            int imageWidth,
            int imageHeight,
            int minContourPoints)
        {
            minContourPoints = Math.Max(2, minContourPoints);
            var outList = new List<Point2D[]>();
            int n = xld.CountObj();
            for (int i = 1; i <= n; i++)
            {
                HObject one = xld.SelectObj(i);
                try
                {
                    Point2D[] pts = ContourXldToPointArray(one);
                    if (pts.Length >= minContourPoints)
                        outList.Add(pts);
                }
                finally
                {
                    one.Dispose();
                }
            }

            return new HalconXldContourBundle
            {
                Width = imageWidth,
                Height = imageHeight,
                Contours = outList
            };
        }

        /// <summary>
        /// 直接使用手绘闭合边线几何，按向内/向外在合成阶跃图上提取亚像素边（位置=ROI 线，edge_direction=设定极性）。
        /// </summary>
        public static (HalconXldContourBundle Bundle, HObject NativeXld) BuildRoiBoundaryXldWithGradientDirection(
            int imageWidth,
            int imageHeight,
            IReadOnlyList<Point2D> boundaryDense,
            RoiGradientPolarity polarity,
            int minContourPoints)
        {
            if (imageWidth <= 0 || imageHeight <= 0)
                throw new ArgumentException("图像尺寸无效");
            if (boundaryDense == null || boundaryDense.Count < 3)
                throw new ArgumentException("闭合边线至少需要 3 个点", nameof(boundaryDense));

            minContourPoints = Math.Max(2, minContourPoints);
            Point2D[] closed = EnsureClosedContourPoints(boundaryDense.ToArray(), forceClose: true);
            if (closed.Length < minContourPoints)
                throw new InvalidOperationException($"闭合边线采样点不足（{closed.Length}），请增加顶点或圆弧段。");

            var rows = closed.Select(p => p.Y).ToArray();
            var cols = closed.Select(p => p.X).ToArray();

            HObject reg = GenRegionPolygonFilled(closed.ToList());
            HOperatorSet.GenContourPolygonXld(out HObject refXld, new HTuple(rows), new HTuple(cols));
            try
            {
                bool outward = polarity == RoiGradientPolarity.Outward;
                HOperatorSet.GenImageConst(out HObject syn, "byte", imageWidth, imageHeight);
                try
                {
                    if (outward)
                    {
                        HOperatorSet.InvertImage(syn, out HObject whiteBg);
                        syn.Dispose();
                        syn = whiteBg;
                        HOperatorSet.OverpaintRegion(syn, reg, new HTuple(0), "fill");
                    }
                    else
                    {
                        HOperatorSet.OverpaintRegion(syn, reg, new HTuple(255), "fill");
                    }

                    HOperatorSet.EdgesSubPix(syn, out HObject edges, "canny", 1, 5, 15);
                    HObject filtered = SelectContoursNearReferenceXld(edges, refXld, 2.0, minContourPoints);
                    edges.Dispose();

                    if (!filtered.IsInitialized() || filtered.CountObj() == 0)
                    {
                        filtered.Dispose();
                        throw new InvalidOperationException(
                            "未能从手绘边线生成带方向的轮廓，请确认 ROI 已闭合且位于图像范围内。");
                    }

                    var bundle = BundleFromXldContours(filtered, imageWidth, imageHeight, minContourPoints);
                    return (bundle, filtered);
                }
                finally
                {
                    syn.Dispose();
                }
            }
            finally
            {
                reg.Dispose();
                refXld.Dispose();
            }
        }

        /// <summary>
        /// 环形 ROI：外圈、内圈边线分别生成带方向的 XLD，再合并（用于 PolygonXld）。
        /// </summary>
        public static (HalconXldContourBundle Bundle, HObject NativeXld) BuildRingRoiBoundaryXldWithGradientDirection(
            int imageWidth,
            int imageHeight,
            IReadOnlyList<Point2D> outerBoundaryDense,
            IReadOnlyList<Point2D> innerBoundaryDense,
            RoiGradientPolarity outerPolarity,
            RoiGradientPolarity innerPolarity,
            int minContourPoints)
        {
            if (outerBoundaryDense == null || outerBoundaryDense.Count < 3)
                throw new ArgumentException("外圈边线至少需要 3 个点", nameof(outerBoundaryDense));
            if (innerBoundaryDense == null || innerBoundaryDense.Count < 3)
                throw new ArgumentException("内圈边线至少需要 3 个点", nameof(innerBoundaryDense));

            var (outerBundle, outerXld) = BuildRoiBoundaryXldWithGradientDirection(
                imageWidth, imageHeight, outerBoundaryDense, outerPolarity, minContourPoints);
            try
            {
                var (innerBundle, innerXld) = BuildRoiBoundaryXldWithGradientDirection(
                    imageWidth, imageHeight, innerBoundaryDense, innerPolarity, minContourPoints);
                try
                {
                    HOperatorSet.ConcatObj(outerXld, innerXld, out HObject merged);
                    outerXld.Dispose();
                    outerXld = merged;

                    var contours = new List<Point2D[]>();
                    if (outerBundle.Contours != null)
                        contours.AddRange(outerBundle.Contours);
                    if (innerBundle.Contours != null)
                        contours.AddRange(innerBundle.Contours);

                    var bundle = new HalconXldContourBundle
                    {
                        Width = imageWidth,
                        Height = imageHeight,
                        Contours = contours
                    };
                    return (bundle, outerXld);
                }
                finally
                {
                    innerXld.Dispose();
                }
            }
            catch
            {
                outerXld.Dispose();
                throw;
            }
        }

        private static double ComputeDesiredGradientAngleRad(IReadOnlyList<Point2D> poly, int idx, bool gradientOutward)
        {
            int n = poly.Count;
            int im = (idx - 1 + n) % n;
            int ip = (idx + 1) % n;
            double dcol = poly[ip].X - poly[im].X;
            double drow = poly[ip].Y - poly[im].Y;
            double len = Math.Sqrt(dcol * dcol + drow * drow);
            if (len < 1e-9)
                return 0;

            dcol /= len;
            drow /= len;
            double n1c = -drow;
            double n1r = dcol;
            double n2c = drow;
            double n2r = -dcol;

            var test = new Point2D(poly[idx].X + n2c * 3, poly[idx].Y + n2r * 3);
            bool n2IsOut = !IsPointInsideClosedPoly(test.X, test.Y, poly);
            double outC = n2IsOut ? n2c : n1c;
            double outR = n2IsOut ? n2r : n1r;
            if (!gradientOutward)
            {
                outC = -outC;
                outR = -outR;
            }

            return Math.Atan2(outR, outC);
        }

        private static bool IsPointInsideClosedPoly(double x, double y, IReadOnlyList<Point2D> poly)
        {
            if (poly.Count < 3)
                return false;

            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double yi = poly[i].Y;
                double yj = poly[j].Y;
                double xi = poly[i].X;
                double xj = poly[j].X;
                if ((yi > y) != (yj > y) &&
                    x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi)
                    inside = !inside;
            }

            return inside;
        }
        public static long CreateShapeModel(
            CalibImage? image,
            HalconXldContourBundle? xldBundle,
            HObject? region,
            HalconShapeModelCreateOptions opt,
            HObject? nativeXldWithDirection = null)
        {
            if (opt == null) throw new ArgumentNullException(nameof(opt));

            if (opt.ModelKind is HalconShapeModelKind.Deformable or HalconShapeModelKind.PlanarDeformable)
            {
                bool fromImageDef = opt.SourceKind is HalconShapeModelSourceKind.ImageRectangle
                    or HalconShapeModelSourceKind.ImagePolygon;
                if (fromImageDef)
                {
                    if (image == null) throw new ArgumentNullException(nameof(image));
                    if (region == null || !region.IsInitialized())
                        throw new InvalidOperationException("可变形图像模板需要有效的 ROI 区域");
                    return CreateDeformableModelFromImageRegion(image, region, opt);
                }

                if (xldBundle == null || xldBundle.Contours == null || xldBundle.Contours.Count == 0)
                    throw new InvalidOperationException("XLD 轮廓为空");
                return CreateDeformableModelFromXld(xldBundle, opt);
            }

            bool fromImage = opt.SourceKind is HalconShapeModelSourceKind.ImageRectangle
                or HalconShapeModelSourceKind.ImagePolygon;

            if (fromImage)
            {
                if (image == null) throw new ArgumentNullException(nameof(image));
                if (region == null || !region.IsInitialized())
                    throw new InvalidOperationException("图像模板模式需要有效的 ROI 区域");
                return CreateShapeModelFromImageRegion(image, region, opt);
            }

            if (xldBundle == null || xldBundle.Contours == null || xldBundle.Contours.Count == 0)
                throw new InvalidOperationException("XLD 轮廓为空");

            return CreateShapeModelFromXld(xldBundle, opt, nativeXldWithDirection);
        }

        public static HalconFlowModelKind GetModelKind(long modelId)
        {
            if (HalconDeformableModelRegistry.TryGetKind(modelId, out HalconFlowModelKind def))
                return def;
            if (HalconShapeModelRegistry.TryGetKind(modelId, out HalconFlowModelKind shape))
                return shape;
            throw new InvalidOperationException($"ModelId={modelId} 不存在或已释放");
        }

        /// <summary>模型 id 是否仍在注册表中（未被 ClearModel 释放）。</summary>
        public static bool TryGetRegisteredModelKind(long modelId, out HalconFlowModelKind kind)
        {
            kind = default;
            if (modelId < 0)
                return false;
            if (HalconDeformableModelRegistry.TryGetKind(modelId, out kind))
                return true;
            return HalconShapeModelRegistry.TryGetKind(modelId, out kind);
        }

        /// <summary>若 id 在形状模型注册表中则返回该 id，否则 -1。</summary>
        public static long ResolveRegisteredShapeModelId(long modelId)
        {
            if (modelId < 0)
                return -1;
            return HalconShapeModelRegistry.TryGetKind(modelId, out _)
                ? modelId
                : -1;
        }

        /// <summary>若 id 在可变形模型注册表中则返回该 id，否则 -1。</summary>
        public static long ResolveRegisteredDeformableModelId(long modelId)
        {
            if (modelId < 0)
                return -1;
            return HalconDeformableModelRegistry.TryGetKind(modelId, out _)
                ? modelId
                : -1;
        }

        public static void ClearModel(long modelId)
        {
            if (modelId < 0) return;
            if (HalconDeformableModelRegistry.TryGetKind(modelId, out _))
            {
                HalconDeformableModelRegistry.Release(modelId);
                return;
            }

            ClearShapeModel(modelId);
        }

        /// <summary>CreateShapeModel：基于 XLD 轮廓（兼容旧参数）。</summary>
        public static long CreateShapeModelFromXld(
            HalconXldContourBundle? xldBundle,
            int numLevels,
            double angleStartDeg,
            double angleExtentDeg,
            double angleStepDeg,
            string optimization,
            string metric,
            int contrast,
            int minContrast)
        {
            return CreateShapeModelFromXld(xldBundle, new HalconShapeModelCreateOptions
            {
                ModelKind = HalconShapeModelKind.Shape,
                SourceKind = HalconShapeModelSourceKind.ThresholdXld,
                NumLevels = numLevels,
                AngleStartDeg = angleStartDeg,
                AngleExtentDeg = angleExtentDeg,
                AngleStepDeg = angleStepDeg,
                Optimization = optimization,
                Metric = metric,
                Contrast = contrast > 0 ? contrast.ToString(CultureInfo.InvariantCulture) : "auto",
                MinContrast = minContrast
            });
        }

        /// <summary>CreateShapeModel / CreateScaledShapeModel：基于 XLD。</summary>
        /// <param name="nativeXldWithDirection">EdgesSubPix 等带 edge_direction 的轮廓；若提供则勿 Dispose 直至创建完成（仍由调用方持有生命周期）。</param>
        public static long CreateShapeModelFromXld(
            HalconXldContourBundle? xldBundle,
            HalconShapeModelCreateOptions opt,
            HObject? nativeXldWithDirection = null)
        {
            HObject conts = new HObject();
            bool disposeConts = true;
            try
            {
                if (nativeXldWithDirection != null && nativeXldWithDirection.IsInitialized() &&
                    nativeXldWithDirection.CountObj() > 0)
                {
                    conts = nativeXldWithDirection;
                    disposeConts = false;
                }
                else
                {
                    if (xldBundle?.Contours == null || xldBundle.Contours.Count == 0)
                        throw new InvalidOperationException("XLD轮廓为空");

                    List<Point2D[]> contours = xldBundle.Contours
                        .Where(c => c != null && c.Length >= 2)
                        .ToList();
                    if (contours.Count == 0)
                        throw new InvalidOperationException($"没有有效轮廓（共 {xldBundle.Contours.Count} 条）");

                    conts = XldBundleToHObject(contours);
                    disposeConts = true;
                }

                if (!conts.IsInitialized() || conts.CountObj() == 0)
                    throw new InvalidOperationException("轮廓对象无效/为空");

                string metricResolved = ResolveShapeModelXldMetric(conts, opt.Metric);
                double angleStartRad = opt.AngleStartDeg * Math.PI / 180.0;
                double angleExtentRad = opt.AngleExtentDeg * Math.PI / 180.0;
                HTuple numLevels = ToNumLevelsTuple(opt.NumLevels);
                HTuple angleStep = ToAngleStepTuple(opt.AngleStepDeg);
                HTuple optimization = new HTuple(string.IsNullOrWhiteSpace(opt.Optimization) ? "auto" : opt.Optimization);
                int minContrastVal = ResolveMinContrast(opt);

                var xld = new HXLDCont(conts);
                var shapeModel = new HShapeModel();
                try
                {
                    if (opt.ModelKind == HalconShapeModelKind.ScaledShape)
                    {
                        shapeModel.CreateScaledShapeModelXld(
                            xld,
                            numLevels,
                            angleStartRad,
                            angleExtentRad,
                            angleStep,
                            opt.ScaleMin,
                            opt.ScaleMax,
                            ToScaleStepTuple(opt.ScaleStep),
                            optimization,
                            metricResolved,
                            minContrastVal);
                    }
                    else
                    {
                        shapeModel.CreateShapeModelXld(
                            xld,
                            numLevels,
                            angleStartRad,
                            angleExtentRad,
                            angleStep,
                            optimization,
                            metricResolved,
                            minContrastVal);
                    }
                }
                catch
                {
                    shapeModel.Dispose();
                    throw;
                }

                return HalconShapeModelRegistry.Register(shapeModel);
            }
            finally
            {
                if (disposeConts)
                    conts.Dispose();
            }
        }

        /// <summary>CreateShapeModel / CreateScaledShapeModel：ROI 内灰度图。</summary>
        public static long CreateShapeModelFromImageRegion(
            CalibImage image,
            HObject region,
            HalconShapeModelCreateOptions opt)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (region == null || !region.IsInitialized())
                throw new ArgumentException("区域无效", nameof(region));

            HObject ho = CalibToHObject(image);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.ReduceDomain(ho, region, out HObject reduced);
                ho.Dispose();
                ho = reduced;

                using var hImg = new HImage(ho);
                double angleStartRad = opt.AngleStartDeg * Math.PI / 180.0;
                double angleExtentRad = opt.AngleExtentDeg * Math.PI / 180.0;
                HTuple numLevels = ToNumLevelsTuple(opt.NumLevels);
                HTuple angleStep = ToAngleStepTuple(opt.AngleStepDeg);
                HTuple optimization = new HTuple(string.IsNullOrWhiteSpace(opt.Optimization) ? "auto" : opt.Optimization);
                string metric = string.IsNullOrWhiteSpace(opt.Metric) ? "use_polarity" : opt.Metric;
                HTuple contrast = ToContrastTuple(opt.Contrast, 0);
                HTuple minContrast = new HTuple(ResolveMinContrast(opt));

                var shapeModel = new HShapeModel();
                try
                {
                    if (opt.ModelKind == HalconShapeModelKind.ScaledShape)
                    {
                        shapeModel.CreateScaledShapeModel(
                            hImg,
                            numLevels,
                            angleStartRad,
                            angleExtentRad,
                            angleStep,
                            opt.ScaleMin,
                            opt.ScaleMax,
                            ToScaleStepTuple(opt.ScaleStep),
                            optimization,
                            metric,
                            contrast,
                            minContrast);
                    }
                    else
                    {
                        shapeModel.CreateShapeModel(
                            hImg,
                            numLevels,
                            angleStartRad,
                            angleExtentRad,
                            angleStep,
                            optimization,
                            metric,
                            contrast,
                            minContrast);
                    }
                }
                catch
                {
                    shapeModel.Dispose();
                    throw;
                }

                return HalconShapeModelRegistry.Register(shapeModel);
            }
            finally
            {
                ho.Dispose();
            }
        }

        private static double ResolveDeformableAngleStepRad(double angleStepDeg) =>
            angleStepDeg > 0 ? angleStepDeg * Math.PI / 180.0 : 0.0;

        private static double ResolveDeformableScaleStep(double scaleStep) =>
            scaleStep > 0 ? scaleStep : 0.0;

        private static List<Point2D[]> ParseXldContArrayToContourList(HXLDCont? xldCont, int expectedCount)
        {
            var result = new List<Point2D[]>(Math.Max(0, expectedCount));
            if (xldCont == null || !xldCont.IsInitialized())
            {
                while (result.Count < expectedCount)
                    result.Add(Array.Empty<Point2D>());
                return result;
            }

            int n = xldCont.CountObj();
            for (int i = 1; i <= n; i++)
            {
                HObject one = xldCont.SelectObj(i);
                try
                {
                    Point2D[] pts = ContourXldToPointArray(one);
                    result.Add(pts.Length >= 2 ? pts : Array.Empty<Point2D>());
                }
                finally
                {
                    one.Dispose();
                }
            }

            while (result.Count < expectedCount)
                result.Add(Array.Empty<Point2D>());
            return result;
        }

        /// <summary>可变形模板（轮廓）：局部 或 平面未标定（透视）。</summary>
        public static long CreateDeformableModelFromXld(HalconXldContourBundle? xldBundle, HalconShapeModelCreateOptions opt)
        {
            HalconDeformableModelSubtype subtype = ResolveDeformableSubtype(opt);
            HObject conts = new HObject();
            try
            {
                if (xldBundle?.Contours == null || xldBundle.Contours.Count == 0)
                    throw new InvalidOperationException("XLD轮廓为空");

                List<Point2D[]> contours = xldBundle.Contours
                    .Where(c => c != null && c.Length >= 2)
                    .ToList();
                if (contours.Count == 0)
                    throw new InvalidOperationException($"没有有效轮廓（共 {xldBundle.Contours.Count} 条）");

                conts = XldBundleToHObject(contours);
                if (!conts.IsInitialized() || conts.CountObj() == 0)
                    throw new InvalidOperationException("轮廓对象无效/为空");

                string metricResolved = ResolveShapeModelXldMetric(conts, opt.Metric);
                double angleStartRad = opt.AngleStartDeg * Math.PI / 180.0;
                double angleExtentRad = opt.AngleExtentDeg * Math.PI / 180.0;
                int minContrastVal = ResolveMinContrast(opt);
                string optimization = string.IsNullOrWhiteSpace(opt.Optimization) ? "auto" : opt.Optimization.Trim();

                var xld = new HXLDCont(conts);
                var model = new HDeformableModel();
                try
                {
                    if (subtype == HalconDeformableModelSubtype.PlanarUncalib)
                    {
                        model.CreatePlanarUncalibDeformableModelXld(
                            xld,
                            ToNumLevelsTuple(opt.NumLevels),
                            new HTuple(angleStartRad),
                            new HTuple(angleExtentRad),
                            ToAngleStepTuple(opt.AngleStepDeg),
                            opt.ScaleMin,
                            new HTuple(opt.ScaleMax),
                            ToScaleStepTuple(opt.ScaleStep),
                            opt.ScaleMin,
                            new HTuple(opt.ScaleMax),
                            ToScaleStepTuple(opt.ScaleStep),
                            new HTuple(optimization),
                            new HTuple(metricResolved),
                            new HTuple(minContrastVal),
                            new HTuple(),
                            new HTuple());
                    }
                    else
                    {
                        model.CreateLocalDeformableModelXld(
                            xld,
                            ToNumLevelsTuple(opt.NumLevels),
                            new HTuple(angleStartRad),
                            new HTuple(angleExtentRad),
                            ToAngleStepTuple(opt.AngleStepDeg),
                            opt.ScaleMin,
                            new HTuple(opt.ScaleMax),
                            ToScaleStepTuple(opt.ScaleStep),
                            opt.ScaleMin,
                            new HTuple(opt.ScaleMax),
                            ToScaleStepTuple(opt.ScaleStep),
                            new HTuple(optimization),
                            new HTuple(metricResolved),
                            new HTuple(minContrastVal),
                            new HTuple(),
                            new HTuple());
                    }
                }
                catch
                {
                    model.Dispose();
                    throw;
                }

                return HalconDeformableModelRegistry.Register(model, subtype);
            }
            finally
            {
                conts.Dispose();
            }
        }

        /// <summary>可变形模板（ROI 灰度）：局部 或 平面未标定（透视）。</summary>
        public static long CreateDeformableModelFromImageRegion(
            CalibImage image,
            HObject region,
            HalconShapeModelCreateOptions opt)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (region == null || !region.IsInitialized())
                throw new ArgumentException("区域无效", nameof(region));

            HalconDeformableModelSubtype subtype = ResolveDeformableSubtype(opt);
            HObject ho = CalibToHObject(image);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.ReduceDomain(ho, region, out HObject reduced);
                ho.Dispose();
                ho = reduced;

                using var hImg = new HImage(ho);
                double angleStartRad = opt.AngleStartDeg * Math.PI / 180.0;
                double angleExtentRad = opt.AngleExtentDeg * Math.PI / 180.0;
                string optimization = string.IsNullOrWhiteSpace(opt.Optimization) ? "auto" : opt.Optimization.Trim();
                string metric = string.IsNullOrWhiteSpace(opt.Metric) ? "use_polarity" : opt.Metric.Trim();
                HTuple contrast = ToContrastTuple(opt.Contrast, 0);
                int minContrastVal = ResolveMinContrast(opt);

                var model = new HDeformableModel();
                try
                {
                    if (subtype == HalconDeformableModelSubtype.PlanarUncalib)
                    {
                        model.CreatePlanarUncalibDeformableModel(
                            hImg,
                            ToNumLevelsTuple(opt.NumLevels),
                            new HTuple(angleStartRad),
                            new HTuple(angleExtentRad),
                            ToAngleStepTuple(opt.AngleStepDeg),
                            opt.ScaleMin,
                            new HTuple(opt.ScaleMax),
                            ToScaleStepTuple(opt.ScaleStep),
                            opt.ScaleMin,
                            new HTuple(opt.ScaleMax),
                            ToScaleStepTuple(opt.ScaleStep),
                            new HTuple(optimization),
                            new HTuple(metric),
                            contrast,
                            new HTuple(minContrastVal),
                            new HTuple(),
                            new HTuple());
                    }
                    else
                    {
                        model.CreateLocalDeformableModel(
                            hImg,
                            ToNumLevelsTuple(opt.NumLevels),
                            new HTuple(angleStartRad),
                            new HTuple(angleExtentRad),
                            ToAngleStepTuple(opt.AngleStepDeg),
                            opt.ScaleMin,
                            new HTuple(opt.ScaleMax),
                            ToScaleStepTuple(opt.ScaleStep),
                            opt.ScaleMin,
                            new HTuple(opt.ScaleMax),
                            ToScaleStepTuple(opt.ScaleStep),
                            new HTuple(optimization),
                            new HTuple(metric),
                            contrast,
                            new HTuple(minContrastVal),
                            new HTuple(),
                            new HTuple());
                    }
                }
                catch
                {
                    model.Dispose();
                    throw;
                }

                return HalconDeformableModelRegistry.Register(model, subtype);
            }
            finally
            {
                ho.Dispose();
            }
        }

        private static (double refRow, double refCol) GetDeformableModelReferencePoint(HDeformableModel model)
        {
            model.GetDeformableModelOrigin(out double row, out double col);
            return (row, col);
        }

        private static HTuple SelectPlanarHomMat(HTuple homMat2D, int matchIndex)
        {
            if (homMat2D == null || homMat2D.Length == 0)
                return new HTuple();
            if (homMat2D.Length <= 9)
                return homMat2D;
            int start = matchIndex * 9;
            return homMat2D.TupleSelectRange(start, start + 8);
        }

        private static (double row, double col) PoseFromPlanarHomMat(HDeformableModel model, HTuple homMat)
        {
            var (refRow, refCol) = GetDeformableModelReferencePoint(model);
            HOperatorSet.ProjectiveTransPoint2d(homMat, refRow, refCol, 1.0, out HTuple row, out HTuple col, out HTuple _);
            return (row[0].D, col[0].D);
        }

        private static Point2D[]? ProjectDeformableContourWithHomMat(HDeformableModel model, HTuple homMat, int level = 1)
        {
            using HXLDCont modelXld = model.GetDeformableModelContours(level);
            if (!modelXld.IsInitialized() || modelXld.CountObj() == 0)
                return null;
            HOperatorSet.ProjectiveTransContourXld(modelXld, out HObject trans, homMat);
            try
            {
                var list = ParseXldContArrayToContourList(new HXLDCont(trans), 1);
                return list.Count > 0 && list[0].Length >= 2 ? list[0] : null;
            }
            finally
            {
                trans.Dispose();
            }
        }

        private static void FindPlanarUncalibOnImage(
            HImage hImg,
            HDeformableModel model,
            double angleStartRad,
            double angleExtentRad,
            double scaleRMin,
            double scaleRMax,
            double scaleCMin,
            double scaleCMax,
            double minScore,
            int numMatches,
            double maxOverlap,
            int numLevels,
            double greediness,
            out HTuple homMat2D,
            out HTuple score)
        {
            HOperatorSet.FindPlanarUncalibDeformableModel(
                hImg,
                model,
                angleStartRad,
                angleExtentRad,
                scaleRMin,
                scaleRMax,
                scaleCMin,
                scaleCMax,
                minScore,
                numMatches,
                maxOverlap,
                numLevels,
                greediness,
                new HTuple(),
                new HTuple(),
                out homMat2D,
                out score);
        }

        /// <summary>FindLocalDeformableModel / FindPlanarUncalibDeformableModel；返回变形轮廓（图像坐标）。</summary>
        public static (double[] rows, double[] cols, double[] scores, List<Point2D[]> deformedContoursPerMatch) FindDeformableModel(
            CalibImage inImg,
            long modelId,
            double angleStartDeg,
            double angleExtentDeg,
            double minScore,
            int numMatches,
            double maxOverlap,
            int numLevels,
            double greediness,
            HalconShapeModelCreateOptions? scaleOpt = null)
        {
            if (HalconDeformableModelRegistry.GetSubtype(modelId) == HalconDeformableModelSubtype.PlanarUncalib)
            {
                return FindPlanarUncalibDeformableModel(
                    inImg, modelId, angleStartDeg, angleExtentDeg, minScore, numMatches, maxOverlap, numLevels, greediness, scaleOpt);
            }

            HDeformableModel model = HalconDeformableModelRegistry.Get(modelId);
            HObject hoImage = CalibToHObject(inImg);
            HImage hImg = new HImage(hoImage);
            try
            {
                double scaleMin = scaleOpt?.ScaleMin ?? 0.9;
                double scaleMax = scaleOpt?.ScaleMax ?? 1.1;
                double angleStartRad = angleStartDeg * Math.PI / 180.0;
                double angleExtentRad = angleExtentDeg * Math.PI / 180.0;
                int matchCount = numMatches <= 0 ? 1 : numMatches;

                HTuple score, row, column;
                int findLevels = numLevels > 0 ? numLevels : 4;
                model.FindLocalDeformableModel(
                    hImg,
                    out HImage? vectorField,
                    out HXLDCont? deformedContours,
                    angleStartRad,
                    angleExtentRad,
                    scaleMin,
                    scaleMax,
                    scaleMin,
                    scaleMax,
                    minScore,
                    matchCount,
                    maxOverlap,
                    findLevels,
                    greediness,
                    new HTuple("deformed_contours"),
                    new HTuple(),
                    new HTuple(),
                    out score,
                    out row,
                    out column);

                vectorField?.Dispose();

                int n = score.Length;
                var rows = new double[n];
                var cols = new double[n];
                var scores = new double[n];
                for (int i = 0; i < n; i++)
                {
                    rows[i] = row[i].D;
                    cols[i] = column[i].D;
                    scores[i] = score[i].D;
                }

                var deformedList = ParseXldContArrayToContourList(deformedContours, n);
                deformedContours?.Dispose();
                return (rows, cols, scores, deformedList);
            }
            finally
            {
                hImg.Dispose();
                hoImage.Dispose();
            }
        }

        /// <summary>FindPlanarUncalibDeformableModel：透视形变（平面未标定），返回位姿与投影轮廓。</summary>
        public static (double[] rows, double[] cols, double[] scores, List<Point2D[]> deformedContoursPerMatch) FindPlanarUncalibDeformableModel(
            CalibImage inImg,
            long modelId,
            double angleStartDeg,
            double angleExtentDeg,
            double minScore,
            int numMatches,
            double maxOverlap,
            int numLevels,
            double greediness,
            HalconShapeModelCreateOptions? scaleOpt = null)
        {
            HDeformableModel model = HalconDeformableModelRegistry.Get(modelId);
            HObject hoImage = CalibToHObject(inImg);
            HImage hImg = new HImage(EnsureGray(hoImage));
            try
            {
                double scaleRMin = scaleOpt?.ScaleMin ?? 0.9;
                double scaleRMax = scaleOpt?.ScaleMax ?? 1.1;
                double scaleCMin = scaleRMin;
                double scaleCMax = scaleRMax;
                double angleStartRad = angleStartDeg * Math.PI / 180.0;
                double angleExtentRad = angleExtentDeg * Math.PI / 180.0;
                int matchCount = numMatches <= 0 ? 1 : numMatches;
                int findLevels = numLevels > 0 ? numLevels : ResolveDeformableFindNumLevels(modelId, 0);

                FindPlanarUncalibOnImage(
                    hImg, model, angleStartRad, angleExtentRad,
                    scaleRMin, scaleRMax, scaleCMin, scaleCMax,
                    minScore, matchCount, maxOverlap, findLevels, greediness,
                    out HTuple homMat2D, out HTuple score);

                int n = score.Length;
                var rows = new double[n];
                var cols = new double[n];
                var scores = new double[n];
                var deformedList = new List<Point2D[]>(n);
                for (int i = 0; i < n; i++)
                {
                    HTuple hom = SelectPlanarHomMat(homMat2D, i);
                    (rows[i], cols[i]) = PoseFromPlanarHomMat(model, hom);
                    scores[i] = score[i].D;
                    deformedList.Add(ProjectDeformableContourWithHomMat(model, hom) ?? Array.Empty<Point2D>());
                }

                return (rows, cols, scores, deformedList);
            }
            finally
            {
                hImg.Dispose();
                hoImage.Dispose();
            }
        }

        public static (double[] rows, double[] cols, double[] scores, List<Point2D[]> deformedContoursPerMatch) FindDeformableModelWithFallback(
            CalibImage inImg,
            long modelId,
            double angleStartDeg,
            double angleExtentDeg,
            double minScore,
            int numMatches,
            double maxOverlap,
            int numLevels,
            double greediness,
            HalconShapeModelCreateOptions? scaleOpt = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = FindDeformableModel(inImg, modelId, angleStartDeg, angleExtentDeg, minScore, numMatches, maxOverlap, numLevels, greediness, scaleOpt);
            if (first.rows.Length > 0)
                return first;

            cancellationToken.ThrowIfCancellationRequested();

            double retryScore = Math.Max(0.2, minScore * 0.65);
            double retryGreed = Math.Max(0.5, greediness * 0.85);
            if (Math.Abs(retryScore - minScore) < 1e-6 && Math.Abs(retryGreed - greediness) < 1e-6)
                return first;

            return FindDeformableModel(inImg, modelId, angleStartDeg, angleExtentDeg, retryScore, numMatches, maxOverlap, numLevels, retryGreed, scaleOpt);
        }

        public static void WriteDeformableModelToFile(long modelId, string filePath)
        {
            if (modelId < 0) throw new ArgumentOutOfRangeException(nameof(modelId));
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("路径不能为空", nameof(filePath));
            HalconDeformableModelRegistry.Get(modelId).WriteDeformableModel(filePath);
        }

        public static long LoadDeformableModelFromFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("路径不能为空", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("可变形模型文件不存在", filePath);

            var model = new HDeformableModel();
            model.ReadDeformableModel(filePath);
            HalconDeformableModelSubtype subtype = HalconDeformableModelRegistry.TryDetectSubtype(model);
            return HalconDeformableModelRegistry.Register(model, subtype);
        }

        public static string GetDeformableModelSubtypeLabel(long modelId) =>
            HalconDeformableModelRegistry.GetSubtype(modelId) == HalconDeformableModelSubtype.PlanarUncalib
                ? "透视"
                : "局部";

        public static string GetDeformableModelParamsSummary(long modelId)
        {
            HDeformableModel model = HalconDeformableModelRegistry.Get(modelId);
            HalconDeformableModelSubtype subtype = HalconDeformableModelRegistry.GetSubtype(modelId);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"ModelID: {modelId}");
            sb.AppendLine(subtype == HalconDeformableModelSubtype.PlanarUncalib
                ? "类型: 可变形(透视/平面未标定)"
                : "类型: 可变形(局部)");
            try
            {
                HTuple value = model.GetDeformableModelParams("num_levels");
                if (value != null && value.Length > 0)
                    sb.AppendLine($"NumLevels: {value[0]}");
            }
            catch
            {
                // ignored
            }

            return sb.ToString().TrimEnd();
        }

        public static Point2D[][] GetDeformableModelContourPoints(long modelId, int level = 1)
        {
            if (!HalconDeformableModelRegistry.TryGet(modelId, out HDeformableModel model))
                return Array.Empty<Point2D[]>();
            using HXLDCont xld = model.GetDeformableModelContours(level);
            int n = xld.CountObj();
            if (n <= 0)
                return Array.Empty<Point2D[]>();

            var list = new List<Point2D[]>(n);
            for (int i = 1; i <= n; i++)
            {
                HObject one = xld.SelectObj(i);
                try
                {
                    Point2D[] pts = ContourXldToPointArray(one);
                    if (pts.Length >= 2)
                        list.Add(EnsureClosedContourPoints(pts, 0.5, forceClose: true));
                }
                finally
                {
                    one.Dispose();
                }
            }

            return list.ToArray();
        }

        private static int[] SortIndicesByScoreDescending(double[] scores)
        {
            int n = scores.Length;
            var idx = new int[n];
            for (int i = 0; i < n; i++)
                idx[i] = i;
            Array.Sort(idx, (a, b) => scores[b].CompareTo(scores[a]));
            return idx;
        }

        private static (double[] rows, double[] cols, double[] angles, double[] scores) ReorderMatchResultsByScore(
            double[] rows,
            double[] cols,
            double[] angles,
            double[] scores)
        {
            if (scores.Length <= 1)
                return (rows, cols, angles, scores);

            int[] order = SortIndicesByScoreDescending(scores);
            var rows2 = new double[order.Length];
            var cols2 = new double[order.Length];
            var angles2 = new double[order.Length];
            var scores2 = new double[order.Length];
            for (int k = 0; k < order.Length; k++)
            {
                int i = order[k];
                rows2[k] = rows[i];
                cols2[k] = cols[i];
                angles2[k] = angles[i];
                scores2[k] = scores[i];
            }

            return (rows2, cols2, angles2, scores2);
        }

        private static Point2D[] OffsetContourToFullImage(Point2D[] contour, double rowOffset, double colOffset)
        {
            var shifted = new Point2D[contour.Length];
            for (int i = 0; i < contour.Length; i++)
                shifted[i] = new Point2D(contour[i].X + colOffset, contour[i].Y + rowOffset);
            return shifted;
        }

        private const int DefaultShapeMatchContourLevel = 1;
        public const double DefaultEndScoreWeight = 0.8;
        public const double DefaultEndArcFraction = 0.12;

        /// <summary>
        /// HALCON 分数只反映模板点命中比例，长条工件中段对齐、端部未对齐时仍可能很高。
        /// 用模板端部相对中段的边缘响应比压低分数；endScoreWeight=0 时不修正。
        /// </summary>
        private static double AdjustShapeMatchScoreForEndAlignment(
            HImage hGray,
            long rigidModelId,
            double matchRow,
            double matchCol,
            double matchAngleDeg,
            double halconScore,
            double endScoreWeight,
            double endArcFraction,
            int contourLevel = DefaultShapeMatchContourLevel)
        {
            if (halconScore <= 1e-9 || endScoreWeight <= 0)
                return halconScore;
            if (ResolveRegisteredShapeModelId(rigidModelId) < 0)
                return halconScore;

            double weight = Math.Clamp(endScoreWeight, 0, 1);
            double arcFraction = Math.Clamp(endArcFraction, 0.02, 0.45);
            double endFactor = ComputeShapeEndEdgeSupportFactor(
                hGray, rigidModelId, matchRow, matchCol, matchAngleDeg, contourLevel, arcFraction);
            return halconScore * (1.0 - weight + weight * endFactor);
        }

        private static double ComputeShapeEndEdgeSupportFactor(
            HImage hGray,
            long rigidModelId,
            double matchRow,
            double matchCol,
            double matchAngleDeg,
            int contourLevel,
            double endArcFraction)
        {
            Point2D[][] modelContours = GetShapeModelContourPoints(rigidModelId, contourLevel);
            if (modelContours.Length == 0)
                return 1.0;

            HOperatorSet.SobelAmp(hGray, out HObject ampHo, "sum_abs", 3);
            try
            {
                using var hAmp = new HImage(ampHo);
                double minFactor = 1.0;
                foreach (Point2D[]? modelContour in modelContours)
                {
                    if (modelContour == null || modelContour.Length < 4)
                        continue;
                    Point2D[] imgContour = TransformShapeModelContourToImage(
                        modelContour, matchRow, matchCol, matchAngleDeg);
                    if (imgContour.Length < 4)
                        continue;
                    double f = ComputeContourEndEdgeSupportFactor(hAmp, imgContour, endArcFraction);
                    minFactor = Math.Min(minFactor, f);
                }

                return minFactor;
            }
            finally
            {
                ampHo.Dispose();
            }
        }

        private static double ComputeContourEndEdgeSupportFactor(HImage hAmp, Point2D[] pts, double endArcFraction)
        {
            int n = pts.Length;
            if (n < 4)
                return 1.0;

            endArcFraction = Math.Clamp(endArcFraction, 0.05, 0.35);
            var cum = new double[n];
            for (int i = 1; i < n; i++)
            {
                double dr = pts[i].Y - pts[i - 1].Y;
                double dc = pts[i].X - pts[i - 1].X;
                cum[i] = cum[i - 1] + Math.Sqrt(dr * dr + dc * dc);
            }

            double total = cum[n - 1];
            if (total < 8)
                return 1.0;

            double endLen = total * endArcFraction;
            var endIdx = new List<int>();
            var midIdx = new List<int>();
            for (int i = 0; i < n; i++)
            {
                double s = cum[i];
                if (s <= endLen || s >= total - endLen)
                    endIdx.Add(i);
                else if (s >= total * 0.38 && s <= total * 0.62)
                    midIdx.Add(i);
            }

            if (endIdx.Count < 2 || midIdx.Count < 2)
                return 1.0;

            double endAmp = MeanNormalPeakAmpAtIndices(hAmp, pts, endIdx, out double endAlign);
            double midAmp = MeanNormalPeakAmpAtIndices(hAmp, pts, midIdx, out _);
            if (midAmp < 1e-6)
                return endAmp > 1e-6 ? endAlign : 0.25 * endAlign;

            double ampRatio = Math.Clamp(endAmp / midAmp, 0, 1);
            return Math.Clamp(ampRatio * endAlign, 0, 1);
        }

        /// <summary>沿轮廓法向搜索梯度峰；峰偏离轮廓越远，对齐因子越低。</summary>
        private static double MeanNormalPeakAmpAtIndices(
            HImage hAmp, Point2D[] pts, List<int> indices, out double meanAlignFactor)
        {
            hAmp.GetImageSize(out int width, out int height);
            double ampSum = 0;
            double alignSum = 0;
            int cnt = 0;
            const int normalHalfSpan = 5;
            foreach (int i in indices)
            {
                GetContourNormalAt(pts, i, out double nr, out double nc);
                double row = pts[i].Y;
                double col = pts[i].X;
                double peakAmp = 0;
                int peakOffset = 0;
                for (int d = -normalHalfSpan; d <= normalHalfSpan; d++)
                {
                    int r = (int)Math.Round(row + d * nr);
                    int c = (int)Math.Round(col + d * nc);
                    if (r < 0 || r >= height || c < 0 || c >= width)
                        continue;
                    HTuple v = hAmp.GetGrayval(r, c);
                    double a = v.Length > 0 ? v[0].D : 0;
                    if (a > peakAmp)
                    {
                        peakAmp = a;
                        peakOffset = Math.Abs(d);
                    }
                }

                if (peakAmp <= 0)
                    continue;

                ampSum += peakAmp;
                alignSum += 1.0 - peakOffset / (double)normalHalfSpan;
                cnt++;
            }

            meanAlignFactor = cnt > 0 ? Math.Clamp(alignSum / cnt, 0, 1) : 1.0;
            return cnt > 0 ? ampSum / cnt : 0;
        }

        private static void GetContourNormalAt(Point2D[] pts, int i, out double nr, out double nc)
        {
            int n = pts.Length;
            int im = i > 0 ? i - 1 : 0;
            int ip = i < n - 1 ? i + 1 : n - 1;
            double tr = pts[ip].Y - pts[im].Y;
            double tc = pts[ip].X - pts[im].X;
            double len = Math.Sqrt(tr * tr + tc * tc);
            if (len < 1e-6)
            {
                nr = 1;
                nc = 0;
                return;
            }

            nr = tc / len;
            nc = -tr / len;
        }

        /// <summary>对已有 FindShapeModel 结果按端部对齐修正分数（流程算子可调用）。</summary>
        public static (double[] rows, double[] cols, double[] angles, double[] scores) ApplyShapeMatchEndScoreWeight(
            CalibImage inImg,
            long rigidModelId,
            double[] rows,
            double[] cols,
            double[] angles,
            double[] scores,
            double endScoreWeight,
            double endArcFraction = DefaultEndArcFraction) =>
            AdjustCoarseShapeMatchScores(
                inImg, rigidModelId, rows, cols, angles, scores,
                endScoreWeight, endArcFraction, DefaultShapeMatchContourLevel);

        /// <summary>对单次匹配结果按刚性模板端部对齐修正分数（粗/精匹配均可调用）。</summary>
        public static double ApplyEndScoreWeightAtPose(
            CalibImage scoreImage,
            long rigidModelId,
            double matchRow,
            double matchCol,
            double matchAngleDeg,
            double halconScore,
            double endScoreWeight,
            double endArcFraction = DefaultEndArcFraction) =>
            ApplyFineMatchEndScoreWeight(
                scoreImage, rigidModelId, -1, null, matchRow, matchCol, matchAngleDeg,
                halconScore, endScoreWeight, endArcFraction);

        /// <summary>
        /// 精匹配分数端部修正：优先用变形轮廓；否则刚性 .shm；再否则 .dfm 模板轮廓。
        /// </summary>
        public static double ApplyFineMatchEndScoreWeight(
            CalibImage scoreImage,
            long rigidModelId,
            long deformableModelId,
            Point2D[]? matchedContour,
            double matchRow,
            double matchCol,
            double matchAngleDeg,
            double halconScore,
            double endScoreWeight,
            double endArcFraction = DefaultEndArcFraction)
        {
            if (scoreImage == null || halconScore <= 1e-9 || endScoreWeight <= 0)
                return halconScore;

            bool haveContour = matchedContour != null && matchedContour.Length >= 4;
            bool haveShape = ResolveRegisteredShapeModelId(rigidModelId) >= 0;
            bool haveDeform = ResolveRegisteredDeformableModelId(deformableModelId) >= 0;
            if (!haveContour && !haveShape && !haveDeform)
                return halconScore;

            try
            {
                HObject ho = CalibToHObject(scoreImage);
                try
                {
                    using var hGray = new HImage(EnsureGray(ho));
                    return AdjustFineMatchScoreForEndAlignment(
                        hGray, rigidModelId, deformableModelId, matchedContour,
                        matchRow, matchCol, matchAngleDeg, halconScore, endScoreWeight, endArcFraction);
                }
                finally
                {
                    ho.Dispose();
                }
            }
            catch (InvalidOperationException)
            {
                return halconScore;
            }
        }

        private static double AdjustFineMatchScoreForEndAlignment(
            HImage hGray,
            long rigidModelId,
            long deformableModelId,
            Point2D[]? matchedContour,
            double matchRow,
            double matchCol,
            double matchAngleDeg,
            double halconScore,
            double endScoreWeight,
            double endArcFraction)
        {
            double weight = Math.Clamp(endScoreWeight, 0, 1);
            double arcFraction = Math.Clamp(endArcFraction, 0.02, 0.45);
            double endFactor;

            if (matchedContour != null && matchedContour.Length >= 4)
            {
                HOperatorSet.SobelAmp(hGray, out HObject ampHo, "sum_abs", 3);
                try
                {
                    using var hAmp = new HImage(ampHo);
                    endFactor = ComputeContourEndEdgeSupportFactor(hAmp, matchedContour, arcFraction);
                }
                finally
                {
                    ampHo.Dispose();
                }
            }
            else if (ResolveRegisteredShapeModelId(rigidModelId) >= 0)
            {
                endFactor = ComputeShapeEndEdgeSupportFactor(
                    hGray, rigidModelId, matchRow, matchCol, matchAngleDeg,
                    DefaultShapeMatchContourLevel, arcFraction);
            }
            else if (ResolveRegisteredDeformableModelId(deformableModelId) >= 0)
            {
                endFactor = ComputeDeformableEndEdgeSupportFactor(
                    hGray, deformableModelId, matchRow, matchCol, matchAngleDeg,
                    DefaultShapeMatchContourLevel, arcFraction);
            }
            else
            {
                return halconScore;
            }

            return halconScore * (1.0 - weight + weight * endFactor);
        }

        private static double ComputeDeformableEndEdgeSupportFactor(
            HImage hGray,
            long deformableModelId,
            double matchRow,
            double matchCol,
            double matchAngleDeg,
            int contourLevel,
            double endArcFraction)
        {
            Point2D[][] modelContours = GetDeformableModelContourPoints(deformableModelId, contourLevel);
            if (modelContours.Length == 0)
                return 1.0;

            HOperatorSet.SobelAmp(hGray, out HObject ampHo, "sum_abs", 3);
            try
            {
                using var hAmp = new HImage(ampHo);
                double minFactor = 1.0;
                foreach (Point2D[]? modelContour in modelContours)
                {
                    if (modelContour == null || modelContour.Length < 4)
                        continue;
                    Point2D[] imgContour = TransformShapeModelContourToImage(
                        modelContour, matchRow, matchCol, matchAngleDeg);
                    if (imgContour.Length < 4)
                        continue;
                    double f = ComputeContourEndEdgeSupportFactor(hAmp, imgContour, endArcFraction);
                    minFactor = Math.Min(minFactor, f);
                }

                return minFactor;
            }
            finally
            {
                ampHo.Dispose();
            }
        }

        private static (double[] rows, double[] cols, double[] angles, double[] scores) AdjustCoarseShapeMatchScores(
            CalibImage inImg,
            long rigidModelId,
            double[] rows,
            double[] cols,
            double[] angles,
            double[] scores,
            double endScoreWeight,
            double endArcFraction,
            int contourLevel)
        {
            if (rows.Length == 0 || endScoreWeight <= 0)
                return (rows, cols, angles, scores);

            HObject ho = CalibToHObject(inImg);
            try
            {
                using var hGray = new HImage(EnsureGray(ho));
                var adjusted = new double[scores.Length];
                for (int i = 0; i < scores.Length; i++)
                {
                    adjusted[i] = AdjustShapeMatchScoreForEndAlignment(
                        hGray, rigidModelId, rows[i], cols[i], angles[i], scores[i],
                        endScoreWeight, endArcFraction, contourLevel);
                }

                return ReorderMatchResultsByScore(rows, cols, angles, adjusted);
            }
            finally
            {
                ho.Dispose();
            }
        }

        public static (double[] rows, double[] cols, double[] angles, double[] scores) CoarseShapeMatch(
            CalibImage inImg,
            long rigidModelId,
            double angleStartDeg,
            double angleExtentDeg,
            double minScore,
            int numMatches,
            double maxOverlap,
            string subPixel,
            int numLevels,
            double greediness,
            bool allowRetry,
            double endScoreWeight = DefaultEndScoreWeight,
            double endArcFraction = DefaultEndArcFraction)
        {
            var raw = allowRetry
                ? FindShapeModelWithFallback(inImg, rigidModelId, angleStartDeg, angleExtentDeg, minScore, numMatches, maxOverlap, subPixel, numLevels, greediness)
                : FindShapeModel(inImg, rigidModelId, angleStartDeg, angleExtentDeg, minScore, numMatches, maxOverlap, subPixel, numLevels, greediness);

            if (inImg == null || raw.scores.Length == 0)
                return raw;

            return AdjustCoarseShapeMatchScores(
                inImg, rigidModelId, raw.rows, raw.cols, raw.angles, raw.scores,
                endScoreWeight, endArcFraction, DefaultShapeMatchContourLevel);
        }

        private const double DomainFineAngleExtentDeg = 60;
        private const double DomainFineMinScore = 0.35;
        private const int DomainFineNumLevels = 2;
        private const double DomainFineGreediness = 0.75;
        private static readonly HalconShapeModelCreateOptions DomainFineScaleOpt = new()
        {
            ScaleMin = 0.9,
            ScaleMax = 1.1
        };

        /// <summary>读取单值粗位姿（double 或 double[] 首元素）。</summary>
        public static bool TryReadCoarseScalar(object? value, out double scalar)
        {
            scalar = 0;
            switch (value)
            {
                case double d:
                    scalar = d;
                    return true;
                case float f:
                    scalar = f;
                    return true;
                case int i:
                    scalar = i;
                    return true;
                case double[] arr when arr.Length > 0:
                    scalar = arr[0];
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>将 Mask 端口对象统一为列表（单张或 List）。</summary>
        public static List<CalibImage> CoerceCalibImageList(object? imageObj)
        {
            if (imageObj == null)
                return new List<CalibImage>();
            if (imageObj is CalibImage single)
                return new List<CalibImage> { single };
            if (imageObj is List<CalibImage> list)
                return list;
            if (imageObj is CalibImage[] arr)
                return new List<CalibImage>(arr);
            if (imageObj is HalconCoarseMaskBatch batch)
            {
                var legacy = new List<CalibImage>(batch.Count);
                legacy.AddRange(batch.Masks);
                return legacy;
            }

            throw new ArgumentException(
                $"期望 CalibImage、List<CalibImage> 或 CalibImage[]，实际为 {imageObj.GetType().Name}");
        }

        /// <summary>原图 + 单张填充 Mask → reduce_domain 域内图。</summary>
        public static CalibImage ReduceDomainByMask(CalibImage sourceImage, CalibImage maskCalib)
        {
            if (sourceImage == null)
                throw new ArgumentNullException(nameof(sourceImage));
            if (maskCalib == null)
                throw new ArgumentNullException(nameof(maskCalib));
            return ReduceDomainCalibWithMask(sourceImage, maskCalib);
        }

        /// <summary>
        /// 在单张 Mask 域内图（ReduceDomain 输出）上仅用 .dfm 精匹配。
        /// </summary>
        public static HalconCoarseFineMatchResult FineDeformableMatchOnDomainImage(
            CalibImage domainImg,
            long deformableModelId,
            double anchorRow = double.NaN,
            double anchorCol = double.NaN,
            double anchorAngleDeg = 0,
            double fineAngleMarginDeg = 5,
            double fineMinScore = 0.45,
            int fineNumLevels = 0,
            double fineGreediness = 0.75,
            double fineScaleMin = 0.97,
            double fineScaleMax = 1.03,
            bool wantDeformed = true,
            bool fineAllowFallback = false,
            double roiMarginPx = 12,
            double maxRoiHalfPx = 120,
            CalibImage? fullImage = null,
            long rigidModelId = -1,
            double fineEndScoreWeight = 0,
            double fineEndArcFraction = DefaultEndArcFraction)
        {
            if (domainImg == null)
                throw new ArgumentNullException(nameof(domainImg));
            if (double.IsNaN(anchorRow) || double.IsNaN(anchorCol))
            {
                if (!TryGetMaskedDomainCenterFromCalib(domainImg, out anchorRow, out anchorCol))
                {
                    anchorRow = domainImg.Height * 0.5;
                    anchorCol = domainImg.Width * 0.5;
                }
            }

            var scaleOpt = new HalconShapeModelCreateOptions
            {
                ScaleMin = fineScaleMin,
                ScaleMax = fineScaleMax
            };

            if (!TryFineMatchSingleDomainImage(
                    domainImg, deformableModelId, anchorRow, anchorCol, anchorAngleDeg,
                    fineAngleMarginDeg, fineMinScore, fineNumLevels, fineGreediness, scaleOpt,
                    wantDeformed, fineAllowFallback, roiMarginPx, maxRoiHalfPx, fullImage,
                    out double row, out double col, out double ang, out double score, out Point2D[]? contour))
                return HalconCoarseFineMatchResult.Empty;

            CalibImage scoreImg = fullImage ?? domainImg;
            score = ApplyFineMatchEndScoreWeight(
                scoreImg, rigidModelId, deformableModelId, contour, row, col, ang, score,
                fineEndScoreWeight, fineEndArcFraction);

            int xldW = fullImage?.Width ?? domainImg.Width;
            int xldH = fullImage?.Height ?? domainImg.Height;
            HalconXldContourBundle? xld = contour != null && contour.Length >= 2
                ? new HalconXldContourBundle
                {
                    Width = xldW,
                    Height = xldH,
                    Contours = new List<Point2D[]> { contour }
                }
                : null;

            return new HalconCoarseFineMatchResult
            {
                FineRows = new[] { row },
                FineCols = new[] { col },
                FineAngles = new[] { ang },
                FineScores = new[] { score },
                DeformedXld = xld
            };
        }

        /// <summary>多张域内图各跑一次 .dfm 精匹配（与 Mask/Out 列表一一对应）。</summary>
        public static HalconCoarseFineMatchResult FineDeformableMatchOnDomainImages(
            IList<CalibImage> domainImages,
            long deformableModelId,
            double[]? coarseRows,
            double[]? coarseCols,
            double[]? coarseAngles)
        {
            if (domainImages == null || domainImages.Count == 0)
                return HalconCoarseFineMatchResult.Empty;

            var fineRows = new List<double>();
            var fineCols = new List<double>();
            var fineAngles = new List<double>();
            var fineScores = new List<double>();
            var contours = new List<Point2D[]>();
            int w = domainImages[0].Width;
            int h = domainImages[0].Height;

            for (int i = 0; i < domainImages.Count; i++)
            {
                double anchorRow = coarseRows != null && i < coarseRows.Length
                    ? coarseRows[i]
                    : double.NaN;
                double anchorCol = coarseCols != null && i < coarseCols.Length
                    ? coarseCols[i]
                    : double.NaN;
                double anchorAng = coarseAngles != null && i < coarseAngles.Length
                    ? coarseAngles[i]
                    : 0;

                if (!TryFineMatchSingleDomainImage(
                        domainImages[i], deformableModelId, anchorRow, anchorCol, anchorAng,
                        5, 0.45, 0, 0.75,
                        new HalconShapeModelCreateOptions { ScaleMin = 0.97, ScaleMax = 1.03 },
                        wantDeformed: true, fineAllowFallback: false, roiMarginPx: 12, maxRoiHalfPx: 120,
                        null,
                        out double row, out double col, out double ang, out double score, out Point2D[]? contour))
                    continue;

                fineRows.Add(row);
                fineCols.Add(col);
                fineAngles.Add(ang);
                fineScores.Add(score);
                if (contour != null && contour.Length >= 2)
                    contours.Add(contour);
            }

            HalconXldContourBundle? xld = contours.Count > 0
                ? new HalconXldContourBundle { Width = w, Height = h, Contours = contours }
                : null;

            return new HalconCoarseFineMatchResult
            {
                CoarseRows = coarseRows ?? Array.Empty<double>(),
                CoarseCols = coarseCols ?? Array.Empty<double>(),
                CoarseAngles = coarseAngles ?? Array.Empty<double>(),
                FineRows = fineRows.ToArray(),
                FineCols = fineCols.ToArray(),
                FineAngles = fineAngles.ToArray(),
                FineScores = fineScores.ToArray(),
                DeformedXld = xld
            };
        }

        /// <summary>域内图对象（单张或 List）精匹配。</summary>
        public static HalconCoarseFineMatchResult FineDeformableMatchOnDomainObject(
            object domainObj,
            long deformableModelId,
            double[]? coarseRows,
            double[]? coarseCols,
            double[]? coarseAngles)
        {
            List<CalibImage> domains = CoerceCalibImageList(domainObj);
            if (domains.Count == 0)
                return HalconCoarseFineMatchResult.Empty;
            if (domains.Count == 1)
                return FineDeformableMatchOnDomainImage(
                    domains[0], deformableModelId,
                    coarseRows != null && coarseRows.Length > 0 ? coarseRows[0] : double.NaN,
                    coarseCols != null && coarseCols.Length > 0 ? coarseCols[0] : double.NaN,
                    coarseAngles != null && coarseAngles.Length > 0 ? coarseAngles[0] : 0);
            return FineDeformableMatchOnDomainImages(domains, deformableModelId, coarseRows, coarseCols, coarseAngles);
        }

        /// <summary>对 <see cref="HalconCoarseMaskBatch"/> 中每张填充 Mask 各 reduce_domain 一次并做 .dfm 精匹配。</summary>
        public static HalconCoarseFineMatchResult FineDeformableMatchOnMaskBatch(
            HalconCoarseMaskBatch maskBatch,
            long deformableModelId)
        {
            if (maskBatch == null)
                throw new ArgumentNullException(nameof(maskBatch));
            if (maskBatch.Count == 0)
                return HalconCoarseFineMatchResult.Empty;
            if (maskBatch.SourceImage == null)
                throw new InvalidOperationException("Mask 批缺少 SourceImage，请由 halcon_coarse_shape_reduce_domain 生成");

            var fineRows = new List<double>(maskBatch.Count);
            var fineCols = new List<double>(maskBatch.Count);
            var fineAngles = new List<double>(maskBatch.Count);
            var fineScores = new List<double>(maskBatch.Count);
            var contours = new List<Point2D[]>();

            for (int i = 0; i < maskBatch.Count; i++)
            {
                CalibImage maskCalib = maskBatch.Masks[i];
                double anchorRow = i < maskBatch.CoarseRows.Length ? maskBatch.CoarseRows[i] : maskBatch.ImageHeight * 0.5;
                double anchorCol = i < maskBatch.CoarseCols.Length ? maskBatch.CoarseCols[i] : maskBatch.ImageWidth * 0.5;
                double anchorAng = i < maskBatch.CoarseAngles.Length ? maskBatch.CoarseAngles[i] : 0;

                CalibImage domainImg = ReduceDomainCalibWithMask(maskBatch.SourceImage, maskCalib);
                try
                {
                    if (!TryFineMatchSingleDomainImage(
                            domainImg, deformableModelId, anchorRow, anchorCol, anchorAng,
                            5, 0.45, 0, 0.75,
                            new HalconShapeModelCreateOptions { ScaleMin = 0.97, ScaleMax = 1.03 },
                            wantDeformed: true, fineAllowFallback: false, roiMarginPx: 12,
                            maxRoiHalfPx: 120,
                            null,
                            out double row, out double col, out double ang, out double score, out Point2D[]? contour))
                        continue;

                    fineRows.Add(row);
                    fineCols.Add(col);
                    fineAngles.Add(ang);
                    fineScores.Add(score);
                    if (contour != null && contour.Length >= 2)
                        contours.Add(contour);
                }
                finally
                {
                    domainImg.Dispose();
                }
            }

            HalconXldContourBundle? xld = contours.Count > 0
                ? new HalconXldContourBundle
                {
                    Width = maskBatch.ImageWidth,
                    Height = maskBatch.ImageHeight,
                    Contours = contours
                }
                : null;

            return new HalconCoarseFineMatchResult
            {
                CoarseRows = maskBatch.CoarseRows,
                CoarseCols = maskBatch.CoarseCols,
                CoarseAngles = maskBatch.CoarseAngles,
                CoarseScores = maskBatch.CoarseScores ?? Array.Empty<double>(),
                FineRows = fineRows.ToArray(),
                FineCols = fineCols.ToArray(),
                FineAngles = fineAngles.ToArray(),
                FineScores = fineScores.ToArray(),
                DeformedXld = xld
            };
        }

        /// <summary>由可变形模板轮廓估计精匹配旋转矩形 ROI 半长。</summary>
        private static (double halfLenRow, double halfLenCol) EstimateDeformableModelHalfExtents(
            long deformableModelId,
            double marginPx,
            double maxHalfPx)
        {
            const double minHalf = 40;
            Point2D[][] contours = GetDeformableModelContourPoints(deformableModelId, 1);
            if (contours.Length == 0)
                return (CapHalf(minHalf + marginPx, maxHalfPx, minHalf), CapHalf(minHalf + marginPx, maxHalfPx, minHalf));

            double minR = double.MaxValue, maxR = double.MinValue;
            double minC = double.MaxValue, maxC = double.MinValue;
            foreach (Point2D[] c in contours)
            {
                foreach (Point2D p in c)
                {
                    minR = Math.Min(minR, p.Y);
                    maxR = Math.Max(maxR, p.Y);
                    minC = Math.Min(minC, p.X);
                    maxC = Math.Max(maxC, p.X);
                }
            }

            double halfR = Math.Max(minHalf, (maxR - minR) * 0.5 + marginPx);
            double halfC = Math.Max(minHalf, (maxC - minC) * 0.5 + marginPx);
            return (CapHalf(halfR, maxHalfPx, minHalf), CapHalf(halfC, maxHalfPx, minHalf));

            static double CapHalf(double half, double cap, double floorHalf)
            {
                if (cap <= 0)
                    return half;
                return Math.Min(half, Math.Max(cap, floorHalf));
            }
        }

        private static bool TryFineMatchSingleDomainImage(
            CalibImage domainImg,
            long deformableModelId,
            double anchorRow,
            double anchorCol,
            double anchorAngleDeg,
            double fineAngleMarginDeg,
            double fineMinScore,
            int fineNumLevels,
            double fineGreediness,
            HalconShapeModelCreateOptions scaleOpt,
            bool wantDeformed,
            bool fineAllowFallback,
            double roiMarginPx,
            double maxRoiHalfPx,
            CalibImage? fullImage,
            out double fineRow,
            out double fineCol,
            out double fineAngleDeg,
            out double fineScore,
            out Point2D[]? deformedContour)
        {
            fineRow = fineCol = fineAngleDeg = fineScore = 0;
            deformedContour = null;

            HObject ho = CalibToHObject(domainImg);
            try
            {
                HImage hMasked = new HImage(EnsureGray(ho));
                try
                {
                    double angleMargin = Math.Max(3.0, fineAngleMarginDeg);
                    bool haveAnchor = !double.IsNaN(anchorRow) && !double.IsNaN(anchorCol);

                    // 透视 .dfm 须在原图 ROI 上搜（域内图域外为 0，缺少背景会导致 FindPlanarUncalib 失败）
                    if (fullImage != null && haveAnchor)
                    {
                        HObject hoFull = CalibToHObject(fullImage);
                        try
                        {
                            HImage hFull = new HImage(EnsureGray(hoFull));
                            try
                            {
                                var (halfR, halfC) = EstimateDeformableModelHalfExtents(
                                    deformableModelId, roiMarginPx, maxRoiHalfPx);
                                if (TryFindDeformableNearPose(
                                        hFull, deformableModelId, anchorRow, anchorCol, anchorAngleDeg,
                                        halfR, halfC, angleMargin, fineMinScore, fineNumLevels, fineGreediness,
                                        scaleOpt, wantDeformed, fineAllowFallback,
                                        out fineRow, out fineCol, out fineScore, out deformedContour))
                                {
                                    fineAngleDeg = anchorAngleDeg;
                                    return true;
                                }
                            }
                            finally
                            {
                                hFull.Dispose();
                            }
                        }
                        finally
                        {
                            hoFull.Dispose();
                        }
                    }

                    HImage searchImg = hMasked;
                    HImage? croppedImg = null;
                    double cropRow1 = 0;
                    double cropCol1 = 0;
                    if (TryCropDomainImageForFineSearch(hMasked, roiMarginPx, out croppedImg, out cropRow1, out cropCol1))
                    {
                        searchImg = croppedImg!;
                    }

                    try
                    {
                        if (!TryFindDeformableInMaskedImage(
                                searchImg, deformableModelId, anchorRow, anchorCol, anchorAngleDeg,
                                angleMargin, fineMinScore, fineNumLevels, fineGreediness,
                                scaleOpt, wantDeformed, fineAllowFallback, cropRow1, cropCol1,
                                out fineRow, out fineCol, out fineScore, out deformedContour))
                            return false;

                        fineAngleDeg = anchorAngleDeg;
                        return true;
                    }
                    finally
                    {
                        if (!ReferenceEquals(searchImg, hMasked))
                            searchImg.Dispose();
                    }
                }
                finally
                {
                    hMasked.Dispose();
                }
            }
            finally
            {
                ho.Dispose();
            }
        }

        /// <summary>
        /// reduce_domain 经 CalibImage 往返后 GetDomain 常为整幅图；优先按非零像素阈值裁剪。
        /// </summary>
        private static bool TryCropDomainImageForFineSearch(
            HImage hDomain,
            double marginPx,
            out HImage cropped,
            out double cropRow1,
            out double cropCol1)
        {
            cropped = new HImage();
            cropRow1 = cropCol1 = 0;
            HOperatorSet.GetImageSize(hDomain, out HTuple imgW, out HTuple imgH);
            int fullPix = imgW.I * imgH.I;
            if (fullPix <= 0)
                return false;

            // 1) 阈值：域内图像素非零区域（reduce_domain 写 BMP 后仍有效）
            HOperatorSet.Threshold(hDomain, out HObject regHo, 1, 255);
            try
            {
                using var thRegion = new HRegion(regHo);
                if (thRegion.IsInitialized()
                    && TryGetRegionPixelCount(thRegion, out int thPix)
                    && thPix > 0
                    && thPix < fullPix * 0.90
                    && TryCropRegionFromDomainImage(hDomain, thRegion, marginPx, out cropped, out cropRow1, out cropCol1)
                    && IsCropSignificantlySmaller(cropped, fullPix))
                {
                    return true;
                }
            }
            finally
            {
                regHo.Dispose();
            }

            // 2) 内存 HALCON 域（仅当明显小于整图时采用）
            try
            {
                HOperatorSet.GetDomain(hDomain, out HObject domHo);
                try
                {
                    using var domain = new HRegion(domHo);
                    if (domain.IsInitialized()
                        && TryGetRegionPixelCount(domain, out int domPix)
                        && domPix > 0
                        && domPix < fullPix * 0.90
                        && TryCropRegionFromDomainImage(hDomain, domain, marginPx, out cropped, out cropRow1, out cropCol1)
                        && IsCropSignificantlySmaller(cropped, fullPix))
                    {
                        return true;
                    }
                }
                finally
                {
                    domHo.Dispose();
                }
            }
            catch
            {
                // ignored
            }

            cropped = new HImage();
            cropRow1 = cropCol1 = 0;
            return false;
        }

        private static bool TryGetRegionPixelCount(HRegion region, out int pixelCount)
        {
            pixelCount = 0;
            HOperatorSet.AreaCenter(region, out HTuple area, out HTuple _, out HTuple _);
            if (area.Length == 0)
                return false;
            pixelCount = (int)Math.Round(area[0].D);
            return pixelCount > 0;
        }

        private static bool IsCropSignificantlySmaller(HImage cropped, int fullPixelCount)
        {
            HOperatorSet.GetImageSize(cropped, out HTuple w, out HTuple h);
            return w.I * h.I < fullPixelCount * 0.90;
        }

        private static bool TryCropRegionFromDomainImage(
            HImage hDomain,
            HRegion region,
            double marginPx,
            out HImage cropped,
            out double cropRow1,
            out double cropCol1)
        {
            cropped = new HImage();
            cropRow1 = cropCol1 = 0;

            HRegion work = marginPx > 0.5 ? region.DilationCircle(marginPx) : region;
            bool disposeWork = marginPx > 0.5;
            try
            {
                HOperatorSet.SmallestRectangle1(work, out HTuple r1, out HTuple c1, out _, out _);
                if (r1.Length == 0)
                    return false;
                cropRow1 = r1[0].D;
                cropCol1 = c1[0].D;

                HOperatorSet.ReduceDomain(hDomain, work, out HObject reduced);
                try
                {
                    HOperatorSet.CropDomain(reduced, out HObject cropHo);
                    try
                    {
                        cropped = new HImage(cropHo);
                        HOperatorSet.GetImageSize(cropped, out HTuple cw, out HTuple ch);
                        return cropped.IsInitialized() && cw.I * ch.I >= 16;
                    }
                    finally
                    {
                        cropHo.Dispose();
                    }
                }
                finally
                {
                    reduced.Dispose();
                }
            }
            finally
            {
                if (disposeWork)
                    work.Dispose();
            }
        }

        private static bool TryGetMaskedDomainCenterFromCalib(CalibImage domainImg, out double row, out double col)
        {
            row = col = 0;
            HObject ho = CalibToHObject(domainImg);
            try
            {
                HImage hMasked = new HImage(EnsureGray(ho));
                try
                {
                    return TryGetMaskedDomainCenter(hMasked, out row, out col);
                }
                finally
                {
                    hMasked.Dispose();
                }
            }
            finally
            {
                ho.Dispose();
            }
        }

        private static bool TryGetMaskedDomainCenter(HImage hMasked, out double row, out double col)
        {
            row = col = 0;
            HOperatorSet.Threshold(hMasked, out HObject region, 1, 255);
            try
            {
                HOperatorSet.AreaCenter(region, out HTuple area, out HTuple r, out HTuple c);
                if (area == null || area.Length == 0 || area[0].D <= 0)
                    return false;
                row = r[0].D;
                col = c[0].D;
                return true;
            }
            finally
            {
                region.Dispose();
            }
        }

        /// <summary>在粗定位候选 ROI 内做可变形精匹配（局部或透视由 .dfm 类型决定）。</summary>
        public static HalconCoarseFineMatchResult FineDeformableShapeMatch(
            CalibImage inImg,
            long rigidModelId,
            long deformableModelId,
            double[] coarseRows,
            double[] coarseCols,
            double[] coarseAngles,
            double[]? coarseScores,
            double fineAngleMarginDeg,
            double fineMinScore,
            int fineNumLevels,
            double fineGreediness,
            double fineScaleMin,
            double fineScaleMax,
            double roiMarginPx,
            double maxRoiHalfPx,
            int maxFineMatches,
            string deformedContourMode,
            bool fineAllowFallback = false,
            bool rigidContourFallback = true,
            bool fineUseCoarseMask = true,
            double fineMaskErosionPx = 2,
            HalconCoarseMaskBatch? preReducedMasks = null,
            double endScoreWeight = DefaultEndScoreWeight,
            double endArcFraction = DefaultEndArcFraction)
        {
            if (coarseRows == null || coarseCols == null || coarseAngles == null
                || coarseRows.Length == 0 || coarseRows.Length != coarseCols.Length || coarseRows.Length != coarseAngles.Length)
                return HalconCoarseFineMatchResult.Empty;

            double[] scoresForSort = coarseScores != null && coarseScores.Length == coarseRows.Length
                ? coarseScores
                : coarseRows;

            int processCount = coarseRows.Length;
            if (maxFineMatches > 0)
                processCount = Math.Min(processCount, maxFineMatches);

            int[] order = SortIndicesByScoreDescending(scoresForSort);
            var (halfLenRow, halfLenCol) = EstimateShapeModelHalfExtents(rigidModelId, roiMarginPx, maxRoiHalfPx);
            bool wantAnyDeformed = !string.Equals(deformedContourMode, "none", StringComparison.OrdinalIgnoreCase);
            bool deformedFirstOnly = string.Equals(deformedContourMode, "first", StringComparison.OrdinalIgnoreCase);
            var scaleOpt = new HalconShapeModelCreateOptions
            {
                ScaleMin = fineScaleMin,
                ScaleMax = fineScaleMax
            };

            var fineRows = new List<double>(processCount);
            var fineCols = new List<double>(processCount);
            var fineAngles = new List<double>(processCount);
            var fineScores = new List<double>(processCount);
            var deformedContours = new List<Point2D[]>();

            var batchIndexByCoarse = new Dictionary<int, int>();
            if (preReducedMasks != null && preReducedMasks.Count > 0)
            {
                for (int b = 0; b < preReducedMasks.CoarseIndices.Length; b++)
                    batchIndexByCoarse[preReducedMasks.CoarseIndices[b]] = b;
            }

            HObject hoImage = CalibToHObject(inImg);
            HImage hFull = new HImage(EnsureGray(hoImage));
            try
            {
                bool deformedEmitted = false;
                for (int k = 0; k < processCount; k++)
                {
                    int i = order[k];
                    double cRow = coarseRows[i];
                    double cCol = coarseCols[i];
                    double cAng = coarseAngles[i];
                    bool wantDeformed = wantAnyDeformed && (!deformedFirstOnly || !deformedEmitted);

                    double fRow = 0, fCol = 0, fAng = 0, fScore = 0;
                    Point2D[]? deformed = null;
                    bool matched = false;

                    if (batchIndexByCoarse.TryGetValue(i, out int batchIdx)
                        && preReducedMasks!.SourceImage != null)
                    {
                        CalibImage domainImg = ReduceDomainCalibWithMask(
                            preReducedMasks.SourceImage, preReducedMasks.Masks[batchIdx]);
                        try
                        {
                            matched = TryFineMatchSingleDomainImage(
                                domainImg, deformableModelId, cRow, cCol, cAng,
                                fineAngleMarginDeg, fineMinScore, fineNumLevels, fineGreediness, scaleOpt,
                                wantDeformed, fineAllowFallback, roiMarginPx, maxRoiHalfPx,
                                null,
                                out fRow, out fCol, out fAng, out fScore, out deformed);
                        }
                        finally
                        {
                            domainImg.Dispose();
                        }
                    }
                    else if (fineUseCoarseMask
                        && TryBuildCoarseCandidateMaskRegion(
                            rigidModelId, cRow, cCol, cAng, fineMaskErosionPx, 1, 0, out HRegion? maskRegion))
                    {
                        try
                        {
                            matched = TryFineMatchInCoarseMask(
                                hFull, rigidModelId, deformableModelId, maskRegion,
                                cRow, cCol, cAng, halfLenRow, halfLenCol,
                                fineAngleMarginDeg, fineMinScore, fineNumLevels, fineGreediness, scaleOpt,
                                wantDeformed, fineAllowFallback, rigidContourFallback,
                                out fRow, out fCol, out fAng, out fScore, out deformed);
                        }
                        finally
                        {
                            maskRegion.Dispose();
                        }
                    }

                    if (!matched)
                    {
                        if (TryRigidShapeFineInRoi(
                                hFull, rigidModelId, cRow, cCol, cAng,
                                halfLenRow, halfLenCol, fineAngleMarginDeg, fineMinScore, fineNumLevels, fineGreediness,
                                endScoreWeight, endArcFraction,
                                out fRow, out fCol, out fAng, out fScore))
                        {
                            matched = true;
                            if (wantDeformed)
                            {
                                deformed = TryExtractDeformableContourAtPose(
                                    hFull, deformableModelId, fRow, fCol, fAng,
                                    halfLenRow, halfLenCol, fineAngleMarginDeg, fineMinScore,
                                    fineNumLevels, fineGreediness, scaleOpt);
                                if (deformed == null && rigidContourFallback)
                                    deformed = BuildShapeModelContourAtPose(rigidModelId, fRow, fCol, fAng);
                            }
                        }
                        else if (!TryFindDeformableNearPose(
                                hFull, deformableModelId, cRow, cCol, cAng,
                                halfLenRow, halfLenCol, fineAngleMarginDeg,
                                fineMinScore, fineNumLevels, fineGreediness, scaleOpt, wantDeformed, fineAllowFallback,
                                out fRow, out fCol, out fScore, out deformed))
                        {
                            continue;
                        }
                        else
                        {
                            matched = true;
                            fAng = cAng;
                        }
                    }

                    if (!matched)
                        continue;

                    if (deformed != null)
                        deformedEmitted = true;

                    if (endScoreWeight > 0)
                    {
                        fScore = ApplyFineMatchEndScoreWeight(
                            inImg, rigidModelId, deformableModelId, deformed,
                            fRow, fCol, fAng, fScore, endScoreWeight, endArcFraction);
                    }

                    fineRows.Add(fRow);
                    fineCols.Add(fCol);
                    fineAngles.Add(fAng);
                    fineScores.Add(fScore);
                    if (deformed != null && deformed.Length >= 2)
                        deformedContours.Add(deformed);
                }
            }
            finally
            {
                hFull.Dispose();
                hoImage.Dispose();
            }

            HalconXldContourBundle? xldBundle = null;
            if (deformedContours.Count > 0)
            {
                xldBundle = new HalconXldContourBundle
                {
                    Width = inImg.Width,
                    Height = inImg.Height,
                    Contours = deformedContours
                };
            }

            return new HalconCoarseFineMatchResult
            {
                CoarseRows = coarseRows,
                CoarseCols = coarseCols,
                CoarseAngles = coarseAngles,
                CoarseScores = coarseScores ?? Array.Empty<double>(),
                FineRows = fineRows.ToArray(),
                FineCols = fineCols.ToArray(),
                FineAngles = fineAngles.ToArray(),
                FineScores = fineScores.ToArray(),
                DeformedXld = xldBundle
            };
        }

        /// <summary>粗定位 + 精匹配（组合）；等价于 CoarseShapeMatch + FineDeformableShapeMatch。</summary>
        public static HalconCoarseFineMatchResult CoarseFineShapeMatch(
            CalibImage inImg,
            long rigidModelId,
            long deformableModelId,
            double coarseAngleStartDeg,
            double coarseAngleExtentDeg,
            double coarseMinScore,
            int coarseNumMatches,
            double coarseMaxOverlap,
            string subPixel,
            int coarseNumLevels,
            double coarseGreediness,
            bool coarseAllowRetry,
            double fineAngleMarginDeg,
            double fineMinScore,
            int fineNumLevels,
            double fineGreediness,
            double fineScaleMin,
            double fineScaleMax,
            double roiMarginPx,
            double maxRoiHalfPx,
            int maxFineMatches,
            string deformedContourMode,
            bool fineAllowFallback = false,
            bool rigidContourFallback = true,
            double endScoreWeight = DefaultEndScoreWeight,
            double endArcFraction = DefaultEndArcFraction,
            double fineEndScoreWeight = double.NaN,
            double fineEndArcFraction = double.NaN)
        {
            double fineWeight = double.IsNaN(fineEndScoreWeight) ? endScoreWeight : fineEndScoreWeight;
            double fineArc = double.IsNaN(fineEndArcFraction) ? endArcFraction : fineEndArcFraction;

            var coarse = CoarseShapeMatch(
                inImg, rigidModelId, coarseAngleStartDeg, coarseAngleExtentDeg,
                coarseMinScore, coarseNumMatches, coarseMaxOverlap, subPixel, coarseNumLevels, coarseGreediness, coarseAllowRetry,
                endScoreWeight, endArcFraction);

            if (coarse.rows.Length == 0)
                return HalconCoarseFineMatchResult.Empty;

            return FineDeformableShapeMatch(
                inImg, rigidModelId, deformableModelId,
                coarse.rows, coarse.cols, coarse.angles, coarse.scores,
                fineAngleMarginDeg, fineMinScore, fineNumLevels, fineGreediness,
                fineScaleMin, fineScaleMax, roiMarginPx, maxRoiHalfPx, maxFineMatches, deformedContourMode,
                fineAllowFallback, rigidContourFallback,
                endScoreWeight: fineWeight, endArcFraction: fineArc);
        }

        /// <summary>由粗位姿与刚性模板估计精匹配旋转矩形 ROI 半长（行/列方向，像素）。</summary>
        public static (double halfLenRow, double halfLenCol) GetFineMatchRoiHalfExtents(
            long rigidModelId, double roiMarginPx, double maxRoiHalfPx) =>
            EstimateShapeModelHalfExtents(rigidModelId, roiMarginPx, maxRoiHalfPx);

        private static (double halfLenRow, double halfLenCol) EstimateShapeModelHalfExtents(long rigidModelId, double marginPx, double maxHalfPx)
        {
            const double minHalf = 40;
            Point2D[][] contours = GetShapeModelContourPoints(rigidModelId, 1);
            if (contours.Length == 0)
                return (CapHalf(minHalf + marginPx, maxHalfPx, minHalf), CapHalf(minHalf + marginPx, maxHalfPx, minHalf));

            double minR = double.MaxValue, maxR = double.MinValue;
            double minC = double.MaxValue, maxC = double.MinValue;
            foreach (Point2D[] c in contours)
            {
                foreach (Point2D p in c)
                {
                    minR = Math.Min(minR, p.Y);
                    maxR = Math.Max(maxR, p.Y);
                    minC = Math.Min(minC, p.X);
                    maxC = Math.Max(maxC, p.X);
                }
            }

            double halfR = Math.Max(minHalf, (maxR - minR) * 0.5 + marginPx);
            double halfC = Math.Max(minHalf, (maxC - minC) * 0.5 + marginPx);
            return (CapHalf(halfR, maxHalfPx, minHalf), CapHalf(halfC, maxHalfPx, minHalf));

            static double CapHalf(double half, double cap, double floorHalf)
            {
                if (cap <= 0)
                    return half;
                double effectiveCap = Math.Max(cap, floorHalf);
                return Math.Min(half, effectiveCap);
            }
        }

        private static int ResolveDeformableFindNumLevels(long deformableModelId, int requested)
        {
            if (requested > 0)
                return requested;
            try
            {
                HTuple value = HalconDeformableModelRegistry.Get(deformableModelId).GetDeformableModelParams("num_levels");
                if (value != null && value.Length > 0 && value[0].I > 0)
                    return value[0].I;
            }
            catch
            {
                // 使用建模默认层数；读失败则回退 4 层
            }
            return 4;
        }

        /// <summary>冒烟/诊断：在粗候选 ROI 上尝试透视/局部与不同裁切方式。</summary>
        public static int DiagnoseFineMatchRoi(
            CalibImage img,
            long deformableModelId,
            long rigidModelId,
            double coarseRow,
            double coarseCol,
            double coarseAngleDeg)
        {
            int ok = 0;
            var (halfR, halfC) = EstimateShapeModelHalfExtents(rigidModelId, 20, 0);
            double half = Math.Max(halfR, halfC);
            Console.WriteLine($"    ROI half row={halfR:F1} col={halfC:F1} useMax={half:F1}");

            HObject hoImage = CalibToHObject(img);
            HImage hFull = new HImage(EnsureGray(hoImage));
            hoImage.Dispose();
            try
            {
                double phi = coarseAngleDeg * Math.PI / 180.0;
                HOperatorSet.GenRectangle2(out HObject rect, coarseRow, coarseCol, phi, half, half);
                try
                {
                    HDeformableModel model = HalconDeformableModelRegistry.Get(deformableModelId);
                    foreach (bool alignAxis in new[] { false, true })
                    {
                        var cropMode = alignAxis ? FineRoiCropMode.CropRectangle2AlignAxis : FineRoiCropMode.CropRectangle2;
                        if (!TryBuildFineSearchRoi(hFull, rect, coarseRow, coarseCol, phi, half, half, cropMode, out HObject cropped, out double cropR1, out double cropC1))
                            continue;

                        try
                        {
                            using var hRoi = new HImage(cropped);
                            if (RunDeformableFindInRoi(
                                    hFull, deformableModelId, coarseRow, coarseCol, coarseAngleDeg,
                                    half, half, 5, 0.25, 2, 0.85,
                                    new HalconShapeModelCreateOptions { ScaleMin = 0.9, ScaleMax = 1.1 },
                                    cropMode, false,
                                    out double fr, out double fc, out double fs, out _))
                            {
                                Console.WriteLine($"    OK fine path align={alignAxis} score={fs:F3} ({fr:F1},{fc:F1})");
                                ok++;
                            }
                            else
                                Console.WriteLine($"    -- fine path align={alignAxis}");

                            if (TryLocalDeformableInRoiImage(
                                    hRoi, model, coarseAngleDeg, 5, 0.25, 2, 0.85,
                                    new HalconShapeModelCreateOptions { ScaleMin = 0.9, ScaleMax = 1.1 },
                                    out double lr, out double lc, out double ls))
                            {
                                Console.WriteLine($"    OK local-in-roi align={alignAxis} score={ls:F3} ({lr + cropR1:F1},{lc + cropC1:F1})");
                                ok++;
                            }
                            else
                                Console.WriteLine($"    -- local-in-roi align={alignAxis}");
                        }
                        finally
                        {
                            cropped.Dispose();
                        }
                    }
                }
                finally
                {
                    rect.Dispose();
                }
            }
            finally
            {
                hFull.Dispose();
            }

            return ok;
        }

        public static string DumpDeformableModelParamKeys(long modelId)
        {
            HDeformableModel model = HalconDeformableModelRegistry.Get(modelId);
            var sb = new System.Text.StringBuilder();
            foreach (string key in new[] { "model_type", "num_levels", "angle_start", "angle_extent", "iso_scale_min", "iso_scale_max" })
            {
                try
                {
                    HTuple v = model.GetDeformableModelParams(key);
                    sb.AppendLine($"    param {key} = {v}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"    param {key}: ({ex.Message})");
                }
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>由粗位姿将刚性模板轮廓填充为 Region，作为该候选的搜索 Mask。</summary>
        private static bool TryBuildCoarseCandidateMaskRegion(
            long rigidModelId,
            double coarseRow,
            double coarseCol,
            double coarseAngleDeg,
            double erosionInsetPx,
            int contourLevel,
            double maskFillDilatePx,
            out HRegion? region)
        {
            region = null;
            ShapeMatchRegionMask[] masks = BuildShapeMatchFilledRegions(
                rigidModelId,
                new[] { coarseRow },
                new[] { coarseCol },
                new[] { coarseAngleDeg },
                contourLevel,
                erosionInsetPx,
                maskFillDilatePx);
            try
            {
                HRegion? r = masks[0].Region;
                if (r == null || !r.IsInitialized())
                    return false;
                masks[0].Region = null;
                region = r;
                return true;
            }
            finally
            {
                foreach (ShapeMatchRegionMask m in masks)
                    m.Dispose();
            }
        }

        private static bool TryReduceDomainImage(HImage hFullGray, HRegion region, out HImage maskedImage)
        {
            maskedImage = new HImage();
            HOperatorSet.ReduceDomain(hFullGray, region, out HObject reduced);
            try
            {
                maskedImage = new HImage(reduced);
                return maskedImage.IsInitialized();
            }
            finally
            {
                reduced.Dispose();
            }
        }

        /// <summary>在粗形状 Mask 域内精匹配（ReduceDomain，全图坐标）。</summary>
        private static bool TryFineMatchInCoarseMask(
            HImage hFullGray,
            long rigidModelId,
            long deformableModelId,
            HRegion maskRegion,
            double coarseRow,
            double coarseCol,
            double coarseAngleDeg,
            double halfLenRow,
            double halfLenCol,
            double fineAngleMarginDeg,
            double fineMinScore,
            int fineNumLevels,
            double fineGreediness,
            HalconShapeModelCreateOptions scaleOpt,
            bool wantDeformed,
            bool fineAllowFallback,
            bool rigidContourFallback,
            out double fineRow,
            out double fineCol,
            out double fineAngleDeg,
            out double fineScore,
            out Point2D[]? deformedContour)
        {
            fineRow = fineCol = fineAngleDeg = fineScore = 0;
            deformedContour = null;

            if (!TryReduceDomainImage(hFullGray, maskRegion, out HImage hMasked))
                return false;

            try
            {
                return TryFineMatchInMaskedHImage(
                    hMasked, rigidModelId, deformableModelId,
                    coarseRow, coarseCol, coarseAngleDeg,
                    fineAngleMarginDeg, fineMinScore, fineNumLevels, fineGreediness, scaleOpt,
                    wantDeformed, fineAllowFallback, rigidContourFallback,
                    out fineRow, out fineCol, out fineAngleDeg, out fineScore, out deformedContour);
            }
            finally
            {
                hMasked.Dispose();
            }
        }

        /// <summary>在已 reduce_domain 的全图灰度图上精匹配（与 <see cref="TryFineMatchInCoarseMask"/> 搜索逻辑相同）。</summary>
        private static bool TryFineMatchInMaskedHImage(
            HImage hMasked,
            long rigidModelId,
            long deformableModelId,
            double coarseRow,
            double coarseCol,
            double coarseAngleDeg,
            double fineAngleMarginDeg,
            double fineMinScore,
            int fineNumLevels,
            double fineGreediness,
            HalconShapeModelCreateOptions scaleOpt,
            bool wantDeformed,
            bool fineAllowFallback,
            bool rigidContourFallback,
            out double fineRow,
            out double fineCol,
            out double fineAngleDeg,
            out double fineScore,
            out Point2D[]? deformedContour)
        {
            fineRow = fineCol = fineAngleDeg = fineScore = 0;
            deformedContour = null;

            if (TryRigidShapeFineInMaskedImage(
                    hMasked, rigidModelId, coarseRow, coarseCol, coarseAngleDeg,
                    fineAngleMarginDeg, fineMinScore, fineNumLevels, fineGreediness,
                    out fineRow, out fineCol, out fineAngleDeg, out fineScore))
            {
                if (wantDeformed)
                {
                    deformedContour = TryExtractDeformableContourInMaskedImage(
                        hMasked, deformableModelId, coarseRow, coarseCol, coarseAngleDeg,
                        fineAngleMarginDeg, fineMinScore, fineNumLevels, fineGreediness, scaleOpt);
                    if (deformedContour == null && rigidContourFallback)
                        deformedContour = BuildShapeModelContourAtPose(rigidModelId, fineRow, fineCol, fineAngleDeg);
                }
                return true;
            }

            if (!TryFindDeformableInMaskedImage(
                    hMasked, deformableModelId, coarseRow, coarseCol, coarseAngleDeg,
                    fineAngleMarginDeg, fineMinScore, fineNumLevels, fineGreediness,
                    scaleOpt, wantDeformed, fineAllowFallback, 0, 0,
                    out fineRow, out fineCol, out fineScore, out deformedContour))
                return false;

            fineAngleDeg = coarseAngleDeg;
            return true;
        }

        private static bool TryRigidShapeFineInMaskedImage(
            HImage hMasked,
            long rigidModelId,
            double coarseRow,
            double coarseCol,
            double coarseAngleDeg,
            double angleMarginDeg,
            double minScore,
            int numLevels,
            double greediness,
            out double fineRow,
            out double fineCol,
            out double fineAngleDeg,
            out double fineScore)
        {
            fineRow = fineCol = fineAngleDeg = fineScore = 0;
            if (ResolveRegisteredShapeModelId(rigidModelId) < 0
                || !HalconShapeModelRegistry.TryGet(rigidModelId, out HShapeModel shapeModel))
                return false;

            double marginDeg = Math.Max(3.0, angleMarginDeg);
            int levels = numLevels > 0 ? Math.Min(numLevels, FineRoiMaxPyramidLevels) : FineRoiMaxPyramidLevels;
            double angleStartRad = (coarseAngleDeg - marginDeg) * Math.PI / 180.0;
            double angleExtentRad = 2 * marginDeg * Math.PI / 180.0;

            shapeModel.FindShapeModel(
                hMasked,
                angleStartRad,
                angleExtentRad,
                minScore,
                3,
                0.5,
                "least_squares",
                levels,
                greediness,
                out HTuple hvRow,
                out HTuple hvCol,
                out HTuple hvAng,
                out HTuple hvScore);

            return SelectShapeMatchNearestCoarse(
                hvRow, hvCol, hvAng, hvScore, 0, 0,
                coarseRow, coarseCol, 80,
                out fineRow, out fineCol, out fineAngleDeg, out fineScore);
        }

        private static bool TryFindDeformableInMaskedImage(
            HImage hMasked,
            long deformableModelId,
            double coarseRow,
            double coarseCol,
            double coarseAngleDeg,
            double fineAngleMarginDeg,
            double fineMinScore,
            int fineNumLevels,
            double fineGreediness,
            HalconShapeModelCreateOptions scaleOpt,
            bool wantDeformed,
            bool fineAllowFallback,
            double coordOffsetRow,
            double coordOffsetCol,
            out double fineRow,
            out double fineCol,
            out double fineScore,
            out Point2D[]? deformedContour)
        {
            double marginDeg = Math.Max(3.0, fineAngleMarginDeg);
            HalconDeformableModelSubtype subtype = HalconDeformableModelRegistry.GetSubtype(deformableModelId);
            FineRoiCropMode crop = subtype == HalconDeformableModelSubtype.PlanarUncalib
                ? FineRoiCropMode.CropRectangle2AlignAxis
                : FineRoiCropMode.CropRectangle2;

            if (RunDeformableFindOnSearchImage(
                    hMasked, deformableModelId, coarseRow, coarseCol, coarseAngleDeg,
                    marginDeg, fineMinScore, fineNumLevels, fineGreediness, scaleOpt,
                    crop, wantDeformed, coordOffsetRow, coordOffsetCol,
                    out fineRow, out fineCol, out fineScore, out deformedContour))
                return true;

            if (!fineAllowFallback)
                return false;

            double retryScore = Math.Max(0.15, fineMinScore * 0.6);
            double retryGreed = Math.Max(0.5, fineGreediness * 0.85);
            double retryMargin = Math.Max(marginDeg, 8.0);
            return RunDeformableFindOnSearchImage(
                hMasked, deformableModelId, coarseRow, coarseCol, coarseAngleDeg,
                retryMargin, retryScore, fineNumLevels, retryGreed, scaleOpt,
                crop, wantDeformed, coordOffsetRow, coordOffsetCol,
                out fineRow, out fineCol, out fineScore, out deformedContour);
        }

        private static Point2D[]? TryExtractDeformableContourInMaskedImage(
            HImage hMasked,
            long deformableModelId,
            double anchorRow,
            double anchorCol,
            double anchorAngleDeg,
            double angleMarginDeg,
            double minScore,
            int fineNumLevels,
            double greediness,
            HalconShapeModelCreateOptions scaleOpt)
        {
            HalconDeformableModelSubtype subtype = HalconDeformableModelRegistry.GetSubtype(deformableModelId);
            double marginDeg = Math.Max(3.0, angleMarginDeg);
            double contourMinScore = Math.Max(0.2, minScore * 0.85);
            FineRoiCropMode crop = subtype == HalconDeformableModelSubtype.PlanarUncalib
                ? FineRoiCropMode.CropRectangle2AlignAxis
                : FineRoiCropMode.CropRectangle2;

            if (RunDeformableFindOnSearchImage(
                    hMasked, deformableModelId, anchorRow, anchorCol, anchorAngleDeg,
                    marginDeg, contourMinScore, fineNumLevels, greediness, scaleOpt,
                    crop, includeDeformedContours: true, 0, 0,
                    out double dRow, out double dCol, out _, out Point2D[]? contour)
                && contour != null && contour.Length >= 2)
            {
                double dr = dRow - anchorRow;
                double dc = dCol - anchorCol;
                if (dr * dr + dc * dc <= 120 * 120)
                    return contour;
            }

            return null;
        }

        /// <summary>在粗候选 ROI 内用刚性 .shm 精定位（毫秒级），坐标为全图。</summary>
        private static bool TryRigidShapeFineInRoi(
            HImage hFullGray,
            long rigidModelId,
            double coarseRow,
            double coarseCol,
            double coarseAngleDeg,
            double halfLenRow,
            double halfLenCol,
            double angleMarginDeg,
            double minScore,
            int numLevels,
            double greediness,
            double endScoreWeight,
            double endArcFraction,
            out double fineRow,
            out double fineCol,
            out double fineAngleDeg,
            out double fineScore)
        {
            fineRow = fineCol = fineAngleDeg = fineScore = 0;
            if (ResolveRegisteredShapeModelId(rigidModelId) < 0
                || !HalconShapeModelRegistry.TryGet(rigidModelId, out HShapeModel shapeModel))
                return false;

            double halfSq = Math.Max(halfLenRow, halfLenCol);
            double marginDeg = Math.Max(3.0, angleMarginDeg);
            double phi = coarseAngleDeg * Math.PI / 180.0;
            HObject rect = new HObject();
            HObject cropped = new HObject();
            try
            {
                HOperatorSet.GenRectangle2(out rect, coarseRow, coarseCol, phi, halfSq, halfSq);
                if (!TryBuildFineSearchRoi(
                        hFullGray, rect, coarseRow, coarseCol, phi, halfSq, halfSq,
                        FineRoiCropMode.CropRectangle2, out cropped, out double cropRow1, out double cropCol1))
                    return false;

                using var hRoi = new HImage(cropped);
                int levels = numLevels > 0 ? Math.Min(numLevels, FineRoiMaxPyramidLevels) : FineRoiMaxPyramidLevels;
                double angleStartRad = (coarseAngleDeg - marginDeg) * Math.PI / 180.0;
                double angleExtentRad = 2 * marginDeg * Math.PI / 180.0;

                // 多候选：取距粗中心最近者，避免重叠 ROI 内总落到全局最高分同一实例
                const int maxCand = 12;
                shapeModel.FindShapeModel(
                    hRoi,
                    angleStartRad,
                    angleExtentRad,
                    minScore,
                    maxCand,
                    0.5,
                    "least_squares",
                    levels,
                    greediness,
                    out HTuple hvRow,
                    out HTuple hvCol,
                    out HTuple hvAng,
                    out HTuple hvScore);

                if (!SelectShapeMatchNearestCoarse(
                        hvRow, hvCol, hvAng, hvScore, cropRow1, cropCol1,
                        coarseRow, coarseCol, halfSq,
                        out fineRow, out fineCol, out fineAngleDeg, out fineScore))
                    return false;

                fineScore = AdjustShapeMatchScoreForEndAlignment(
                    hFullGray, rigidModelId, fineRow, fineCol, fineAngleDeg, fineScore,
                    endScoreWeight, endArcFraction);

                return true;
            }
            catch (HOperatorException)
            {
                return false;
            }
            finally
            {
                rect.Dispose();
                cropped.Dispose();
            }
        }

        /// <summary>在 ROI 内、锚定半径内取 HALCON 分数最高的候选（不再单纯取距粗中心最近）。</summary>
        private static bool SelectShapeMatchNearestCoarse(
            HTuple hvRow,
            HTuple hvCol,
            HTuple hvAng,
            HTuple hvScore,
            double cropRow1,
            double cropCol1,
            double coarseRow,
            double coarseCol,
            double roiHalfLen,
            out double fineRow,
            out double fineCol,
            out double fineAngleDeg,
            out double fineScore)
        {
            fineRow = fineCol = fineAngleDeg = fineScore = 0;
            if (hvScore == null || hvScore.Length == 0)
                return false;

            double maxAnchorDist = Math.Max(24, roiHalfLen * 0.85);
            int best = -1;
            double bestScore = double.NegativeInfinity;
            for (int i = 0; i < hvScore.Length; i++)
            {
                double row = hvRow[i].D + cropRow1;
                double col = hvCol[i].D + cropCol1;
                double dr = row - coarseRow;
                double dc = col - coarseCol;
                double dist = Math.Sqrt(dr * dr + dc * dc);
                if (dist > maxAnchorDist)
                    continue;

                double sc = hvScore[i].D;
                if (sc <= bestScore)
                    continue;
                bestScore = sc;
                best = i;
            }

            if (best < 0)
                return false;

            fineRow = hvRow[best].D + cropRow1;
            fineCol = hvCol[best].D + cropCol1;
            fineAngleDeg = hvAng[best].D * 180.0 / Math.PI;
            fineScore = hvScore[best].D;
            return true;
        }

        /// <summary>在已确定的精位姿处用 .dfm 提取变形轮廓（位姿仍由刚性精匹配给出）。</summary>
        private static Point2D[]? TryExtractDeformableContourAtPose(
            HImage hFullGray,
            long deformableModelId,
            double anchorRow,
            double anchorCol,
            double anchorAngleDeg,
            double halfLenRow,
            double halfLenCol,
            double angleMarginDeg,
            double minScore,
            int fineNumLevels,
            double greediness,
            HalconShapeModelCreateOptions scaleOpt)
        {
            HalconDeformableModelSubtype subtype = HalconDeformableModelRegistry.GetSubtype(deformableModelId);
            double halfSq = Math.Max(halfLenRow, halfLenCol);
            double marginDeg = Math.Max(3.0, angleMarginDeg);
            double contourMinScore = Math.Max(0.2, minScore * 0.85);
            int levels = ClampFineRoiFindLevels(deformableModelId, fineNumLevels);

            FineRoiCropMode crop = subtype == HalconDeformableModelSubtype.PlanarUncalib
                ? FineRoiCropMode.CropRectangle2AlignAxis
                : FineRoiCropMode.CropRectangle2;

            if (RunDeformableFindInRoi(
                    hFullGray, deformableModelId, anchorRow, anchorCol, anchorAngleDeg,
                    halfSq, halfSq, marginDeg, contourMinScore, levels, greediness, scaleOpt,
                    crop, includeDeformedContours: true,
                    out double dRow, out double dCol, out _, out Point2D[]? contour)
                && contour != null && contour.Length >= 2)
            {
                double dr = dRow - anchorRow;
                double dc = dCol - anchorCol;
                double maxDrift = Math.Max(32, halfSq * 0.95);
                if (dr * dr + dc * dc <= maxDrift * maxDrift)
                    return contour;
            }

            if (subtype == HalconDeformableModelSubtype.PlanarUncalib
                && RunDeformableFindInRoi(
                    hFullGray, deformableModelId, anchorRow, anchorCol, anchorAngleDeg,
                    halfSq, halfSq, marginDeg, contourMinScore, levels, greediness, scaleOpt,
                    FineRoiCropMode.CropRectangle2, includeDeformedContours: true,
                    out _, out _, out _, out contour)
                && contour != null && contour.Length >= 2)
                return contour;

            return null;
        }

        private static Point2D[]? BuildShapeModelContourAtPose(long rigidModelId, double row, double col, double angleDeg)
        {
            Point2D[][] contours = GetShapeModelContourPoints(rigidModelId, 1);
            if (contours.Length == 0 || contours[0].Length < 2)
                return null;
            Point2D[] src = contours[0];
            var dst = new Point2D[src.Length];
            for (int i = 0; i < src.Length; i++)
                dst[i] = TransformShapeModelPointToImage(src[i], row, col, angleDeg);
            return dst;
        }

        private static bool TryFindDeformableNearPose(
            HImage hFullGray,
            long deformableModelId,
            double coarseRow,
            double coarseCol,
            double coarseAngleDeg,
            double halfLenRow,
            double halfLenCol,
            double fineAngleMarginDeg,
            double fineMinScore,
            int fineNumLevels,
            double fineGreediness,
            HalconShapeModelCreateOptions scaleOpt,
            bool includeDeformedContours,
            bool allowFallback,
            out double fineRow,
            out double fineCol,
            out double fineScore,
            out Point2D[]? deformedContour)
        {
            fineRow = fineCol = fineScore = 0;
            deformedContour = null;

            int findLevels = ClampFineRoiFindLevels(deformableModelId, fineNumLevels);
            double marginDeg = Math.Max(3.0, fineAngleMarginDeg);
            HalconDeformableModelSubtype subtype = HalconDeformableModelRegistry.GetSubtype(deformableModelId);
            double halfSq = Math.Max(halfLenRow, halfLenCol);

            if (subtype == HalconDeformableModelSubtype.PlanarUncalib)
            {
                if (RunDeformableFindInRoi(
                        hFullGray, deformableModelId, coarseRow, coarseCol, coarseAngleDeg,
                        halfSq, halfSq, marginDeg, fineMinScore, findLevels, fineGreediness, scaleOpt,
                        FineRoiCropMode.CropRectangle2AlignAxis, includeDeformedContours,
                        out fineRow, out fineCol, out fineScore, out deformedContour))
                    return true;

                if (TryLocalDeformableFineInRoi(
                        hFullGray, deformableModelId, coarseRow, coarseCol, coarseAngleDeg,
                        halfSq, halfSq, marginDeg, fineMinScore, findLevels, fineGreediness, scaleOpt,
                        includeDeformedContours,
                        out fineRow, out fineCol, out fineScore, out deformedContour))
                    return true;
            }
            else if (RunDeformableFindInRoi(
                    hFullGray, deformableModelId, coarseRow, coarseCol, coarseAngleDeg,
                    halfSq, halfSq, marginDeg, fineMinScore, findLevels, fineGreediness, scaleOpt,
                    FineRoiCropMode.CropRectangle2, includeDeformedContours,
                    out fineRow, out fineCol, out fineScore, out deformedContour))
            {
                return true;
            }

            if (!allowFallback)
                return false;

            double retryScore = Math.Max(0.15, fineMinScore * 0.6);
            double retryGreed = Math.Max(0.5, fineGreediness * 0.85);
            double retryMargin = Math.Max(marginDeg, 8.0);
            FineRoiCropMode retryCrop = subtype == HalconDeformableModelSubtype.PlanarUncalib
                ? FineRoiCropMode.CropRectangle2AlignAxis
                : FineRoiCropMode.CropRectangle2;
            return RunDeformableFindInRoi(
                hFullGray, deformableModelId, coarseRow, coarseCol, coarseAngleDeg,
                halfSq, halfSq, retryMargin, retryScore, findLevels, retryGreed, scaleOpt,
                retryCrop, includeDeformedContours,
                out fineRow, out fineCol, out fineScore, out deformedContour);
        }

        private enum FineRoiCropMode
        {
            /// <summary>裁切旋转矩形的外接轴对齐矩形；角度搜索仍以粗角度为中心。</summary>
            CropRectangle2,
            /// <summary>裁切并将矩形轴对齐到图像坐标（透视精匹配推荐）。</summary>
            CropRectangle2AlignAxis,
            /// <summary>ReduceDomain + CropDomain，与早期实现一致。</summary>
            ReduceAndCropDomain
        }

        private static bool TryBuildFineSearchRoi(
            HImage hFullGray,
            HObject rect,
            double coarseRow,
            double coarseCol,
            double phi,
            double halfLenRow,
            double halfLenCol,
            FineRoiCropMode cropMode,
            out HObject cropped,
            out double cropRow1,
            out double cropCol1)
        {
            cropped = new HObject();
            cropRow1 = cropCol1 = 0;

            if (cropMode == FineRoiCropMode.CropRectangle2 || cropMode == FineRoiCropMode.CropRectangle2AlignAxis)
            {
                string align = cropMode == FineRoiCropMode.CropRectangle2AlignAxis ? "true" : "false";
                HOperatorSet.SmallestRectangle1(rect, out HTuple row1, out HTuple col1, out _, out _);
                cropRow1 = row1.Length > 0 ? row1[0].D : 0;
                cropCol1 = col1.Length > 0 ? col1[0].D : 0;
                HOperatorSet.CropRectangle2(hFullGray, out cropped, coarseRow, coarseCol, phi, halfLenRow, halfLenCol, align, "constant");
                return cropped.IsInitialized();
            }

            HOperatorSet.ReduceDomain(hFullGray, rect, out HObject reduced);
            try
            {
                HOperatorSet.SmallestRectangle1(reduced, out HTuple row1, out HTuple col1, out _, out _);
                cropRow1 = row1.Length > 0 ? row1[0].D : 0;
                cropCol1 = col1.Length > 0 ? col1[0].D : 0;
                HOperatorSet.CropDomain(reduced, out cropped);
                return cropped.IsInitialized();
            }
            finally
            {
                reduced.Dispose();
            }
        }

        private static bool RunDeformableFindInRoi(
            HImage hFullGray,
            long deformableModelId,
            double coarseRow,
            double coarseCol,
            double coarseAngleDeg,
            double halfLenRow,
            double halfLenCol,
            double angleMarginDeg,
            double minScore,
            int findLevels,
            double greediness,
            HalconShapeModelCreateOptions scaleOpt,
            FineRoiCropMode cropMode,
            bool includeDeformedContours,
            out double fineRow,
            out double fineCol,
            out double fineScore,
            out Point2D[]? deformedContour)
        {
            fineRow = fineCol = fineScore = 0;
            deformedContour = null;

            findLevels = ClampFineRoiFindLevels(deformableModelId, findLevels);

            double phi = coarseAngleDeg * Math.PI / 180.0;
            HObject rect = new HObject();
            HObject cropped = new HObject();
            try
            {
                HOperatorSet.GenRectangle2(out rect, coarseRow, coarseCol, phi, halfLenRow, halfLenCol);
                if (!TryBuildFineSearchRoi(hFullGray, rect, coarseRow, coarseCol, phi, halfLenRow, halfLenCol, cropMode, out cropped, out double cropRow1, out double cropCol1))
                    return false;

                using var hRoi = new HImage(cropped);
                return RunDeformableFindOnSearchImage(
                    hRoi, deformableModelId, coarseRow, coarseCol, coarseAngleDeg,
                    angleMarginDeg, minScore, findLevels, greediness, scaleOpt, cropMode, includeDeformedContours,
                    cropRow1, cropCol1,
                    out fineRow, out fineCol, out fineScore, out deformedContour);
            }
            finally
            {
                rect.Dispose();
                cropped.Dispose();
            }
        }

        private static bool RunDeformableFindOnSearchImage(
            HImage hSearch,
            long deformableModelId,
            double coarseRow,
            double coarseCol,
            double coarseAngleDeg,
            double angleMarginDeg,
            double minScore,
            int findLevels,
            double greediness,
            HalconShapeModelCreateOptions scaleOpt,
            FineRoiCropMode cropMode,
            bool includeDeformedContours,
            double coordOffsetRow,
            double coordOffsetCol,
            out double fineRow,
            out double fineCol,
            out double fineScore,
            out Point2D[]? deformedContour)
        {
            fineRow = fineCol = fineScore = 0;
            deformedContour = null;

            findLevels = ClampFineRoiFindLevels(deformableModelId, findLevels);
            if (HalconRuntimeSettings.IsTraceEnabled())
            {
                hSearch.GetImageSize(out HTuple sw, out HTuple sh);
                System.Diagnostics.Debug.WriteLine(
                    $"[HALCON] FindLocalDeformableModel 搜索图 {sw.I}x{sh.I}, levels={findLevels}, " +
                    $"thread_num={HalconRuntimeSettings.LastParallelFindThreadNum}");
            }

            HDeformableModel model = HalconDeformableModelRegistry.Get(deformableModelId);
            double marginRad = angleMarginDeg * Math.PI / 180.0;
            bool alignAxisCrop = cropMode == FineRoiCropMode.CropRectangle2AlignAxis;
            double searchAngleDeg = alignAxisCrop ? 0.0 : coarseAngleDeg;
            double angleStartRad = searchAngleDeg * Math.PI / 180.0 - marginRad;
            double angleExtentRad = 2 * marginRad;
            HalconDeformableModelSubtype subtype = HalconDeformableModelRegistry.GetSubtype(deformableModelId);

            if (subtype == HalconDeformableModelSubtype.PlanarUncalib)
            {
                FindPlanarUncalibOnImage(
                    hSearch, model, angleStartRad, angleExtentRad,
                    scaleOpt.ScaleMin, scaleOpt.ScaleMax, scaleOpt.ScaleMin, scaleOpt.ScaleMax,
                    minScore, 1, 0.5, findLevels, greediness,
                    out HTuple homMat2D, out HTuple planarScore);

                if (planarScore.Length == 0)
                    return false;

                HTuple hom = SelectPlanarHomMat(homMat2D, 0);
                (fineRow, fineCol) = PoseFromPlanarHomMat(model, hom);
                fineRow += coordOffsetRow;
                fineCol += coordOffsetCol;
                fineScore = planarScore[0].D;

                if (includeDeformedContours)
                {
                    Point2D[]? projected = ProjectDeformableContourWithHomMat(model, hom);
                    if (projected != null)
                        deformedContour = OffsetContourToFullImage(projected, coordOffsetRow, coordOffsetCol);
                }

                return true;
            }

            HTuple localResultType = new HTuple("deformed_contours");
            model.FindLocalDeformableModel(
                hSearch,
                out HImage? vectorField,
                out HXLDCont? deformedContours,
                angleStartRad,
                angleExtentRad,
                scaleOpt.ScaleMin,
                scaleOpt.ScaleMax,
                scaleOpt.ScaleMin,
                scaleOpt.ScaleMax,
                minScore,
                1,
                0.5,
                findLevels,
                greediness,
                localResultType,
                new HTuple(),
                new HTuple(),
                out HTuple score,
                out HTuple row,
                out HTuple column);

            vectorField?.Dispose();
            if (score.Length == 0)
            {
                deformedContours?.Dispose();
                return false;
            }

            fineRow = row[0].D + coordOffsetRow;
            fineCol = column[0].D + coordOffsetCol;
            fineScore = score[0].D;

            if (includeDeformedContours && deformedContours != null && deformedContours.IsInitialized() && deformedContours.CountObj() > 0)
            {
                var list = ParseXldContArrayToContourList(deformedContours, 1);
                deformedContours.Dispose();
                if (list.Count > 0 && list[0].Length >= 2)
                    deformedContour = OffsetContourToFullImage(list[0], coordOffsetRow, coordOffsetCol);
            }
            else
                deformedContours?.Dispose();

            return true;
        }

        /// <summary>透视模型在 ROI 内失败时，用局部可变形在相同裁切上再试（部分 .dfm 仅局部可找）。</summary>
        private static bool TryLocalDeformableFineInRoi(
            HImage hFullGray,
            long deformableModelId,
            double coarseRow,
            double coarseCol,
            double coarseAngleDeg,
            double halfLenRow,
            double halfLenCol,
            double angleMarginDeg,
            double minScore,
            int findLevels,
            double greediness,
            HalconShapeModelCreateOptions scaleOpt,
            bool includeDeformedContours,
            out double fineRow,
            out double fineCol,
            out double fineScore,
            out Point2D[]? deformedContour)
        {
            fineRow = fineCol = fineScore = 0;
            deformedContour = null;

            double phi = coarseAngleDeg * Math.PI / 180.0;
            HObject rect = new HObject();
            HObject cropped = new HObject();
            try
            {
                HOperatorSet.GenRectangle2(out rect, coarseRow, coarseCol, phi, halfLenRow, halfLenCol);
                if (!TryBuildFineSearchRoi(hFullGray, rect, coarseRow, coarseCol, phi, halfLenRow, halfLenCol,
                        FineRoiCropMode.CropRectangle2, out cropped, out double cropRow1, out double cropCol1))
                    return false;

                using var hRoi = new HImage(cropped);
                HDeformableModel model = HalconDeformableModelRegistry.Get(deformableModelId);
                if (!TryLocalDeformableInRoiImage(
                        hRoi, model, coarseAngleDeg, angleMarginDeg, minScore, findLevels, greediness, scaleOpt,
                        out double lr, out double lc, out double ls))
                    return false;

                fineRow = lr + cropRow1;
                fineCol = lc + cropCol1;
                fineScore = ls;
                if (includeDeformedContours)
                {
                    // 局部轮廓由 Find 另行获取代价大；精匹配位姿优先
                }
                return true;
            }
            finally
            {
                rect.Dispose();
                cropped.Dispose();
            }
        }

        private static bool TryLocalDeformableInRoiImage(
            HImage hRoi,
            HDeformableModel model,
            double coarseAngleDeg,
            double angleMarginDeg,
            double minScore,
            int findLevels,
            double greediness,
            HalconShapeModelCreateOptions scaleOpt,
            out double row,
            out double col,
            out double score)
        {
            row = col = score = 0;
            double marginRad = Math.Max(3.0, angleMarginDeg) * Math.PI / 180.0;
            double angleStartRad = coarseAngleDeg * Math.PI / 180.0 - marginRad;
            double angleExtentRad = 2 * marginRad;
            int levels = ClampFineRoiFindLevels(model, findLevels);
            try
            {
                model.FindLocalDeformableModel(
                    hRoi,
                    out HImage? vectorField,
                    out HXLDCont? deformedContours,
                    angleStartRad,
                    angleExtentRad,
                    scaleOpt.ScaleMin,
                    scaleOpt.ScaleMax,
                    scaleOpt.ScaleMin,
                    scaleOpt.ScaleMax,
                    minScore,
                    1,
                    0.5,
                    levels,
                    greediness,
                    new HTuple("deformed_contours"),
                    new HTuple(),
                    new HTuple(),
                    out HTuple sc,
                    out HTuple r,
                    out HTuple c);
                vectorField?.Dispose();
                deformedContours?.Dispose();
                if (sc.Length == 0)
                    return false;
                row = r[0].D;
                col = c[0].D;
                score = sc[0].D;
                return true;
            }
            catch (HOperatorException)
            {
                return false;
            }
        }

        public static ShapeMatchRegionMask[] BuildShapeMatchFilledRegions(
            long modelId,
            double[] rows,
            double[] cols,
            double[]? angles,
            int contourLevel,
            double erosionInsetPx,
            double maskFillDilatePx = 0) =>
            HalconRuntimeSettings.RunGeometrySafe(() =>
                BuildShapeMatchFilledRegionsCore(
                    modelId, rows, cols, angles, contourLevel, erosionInsetPx, maskFillDilatePx));

        private static ShapeMatchRegionMask[] BuildShapeMatchFilledRegionsCore(
            long modelId,
            double[] rows,
            double[] cols,
            double[]? angles,
            int contourLevel,
            double erosionInsetPx,
            double maskFillDilatePx = 0)
        {
            long shapeId = ResolveRegisteredShapeModelId(modelId);
            if (shapeId < 0)
                throw new InvalidOperationException(
                    $"形状模板 ModelId={modelId} 无效：请接 halcon_create/load_shape_model 的 ModelId（.shm），勿接可变形模型或已释放的 ID");

            int n = Math.Min(rows?.Length ?? 0, cols?.Length ?? 0);
            var masks = new ShapeMatchRegionMask[n];
            if (n == 0)
                return masks;

            HShapeModel shapeModel = HalconShapeModelRegistry.Get(shapeId);
            using HXLDCont modelXld = shapeModel.GetShapeModelContours(contourLevel);
            int objCount = modelXld.CountObj();
            if (objCount <= 0)
                return masks;

            for (int m = 0; m < n; m++)
            {
                masks[m] = new ShapeMatchRegionMask { Region = null };
                double matchRow = rows[m];
                double matchCol = cols[m];
                double angleDeg = angles != null && angles.Length > m ? angles[m] : 0;
                double angleRad = angleDeg * Math.PI / 180.0;

                HOperatorSet.HomMat2dIdentity(out HTuple hom);
                HOperatorSet.HomMat2dRotate(hom, angleRad, 0, 0, out hom);
                HOperatorSet.HomMat2dTranslate(hom, matchRow, matchCol, out hom);
                HOperatorSet.AffineTransContourXld(modelXld, out HObject transXld, hom);

                HObject fillXld = transXld;
                bool disposeFillXld = false;
                try
                {
                    if (erosionInsetPx > 0.5)
                    {
                        if (!TryInsetTransformedContourXldNormal(transXld, erosionInsetPx, out HObject insetXld))
                            continue;
                        fillXld = insetXld;
                        disposeFillXld = true;
                    }

                    HRegion? filled = BuildFilledRegionFromTransformedXld(fillXld, maskFillDilatePx);
                    if (filled == null || !filled.IsInitialized())
                        continue;

                    masks[m].Region = filled;
                }
                finally
                {
                    if (disposeFillXld)
                        fillXld.Dispose();
                    transXld.Dispose();
                }
            }

            return masks;
        }
        public static HalconCoarseMaskBatch BuildCoarseShapeMaskBatch(
            CalibImage image,
            long rigidModelId,
            double[] coarseRows,
            double[] coarseCols,
            double[]? coarseAngles,
            double[]? coarseScores,
            double maskErosionPx,
            int contourLevel = 1,
            int maxCandidates = 0,
            double maskFillDilatePx = 0)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));
            if (coarseRows == null || coarseCols == null)
                throw new ArgumentException("CoarseRow/CoarseColumn 不能为空");
            int n = Math.Min(coarseRows.Length, coarseCols.Length);
            if (n == 0)
                throw new InvalidOperationException("粗形状 Mask 批: 无粗候选位姿");

            if (coarseAngles == null || coarseAngles.Length < n)
            {
                double[] padded = new double[n];
                if (coarseAngles != null)
                    Array.Copy(coarseAngles, padded, Math.Min(coarseAngles.Length, n));
                coarseAngles = padded;
            }

            int emitCount = n;
            if (maxCandidates > 0)
                emitCount = Math.Min(emitCount, maxCandidates);

            image.RefreshProperties();
            int w = image.Width;
            int h = image.Height;
            var masks = new CalibImage[emitCount];
            var indices = new int[emitCount];
            var rows = new double[emitCount];
            var cols = new double[emitCount];
            var angles = new double[emitCount];
            double[]? scores = coarseScores != null && coarseScores.Length >= n
                ? new double[emitCount]
                : null;

            int built = 0;
            for (int i = 0; i < emitCount; i++)
            {
                if (!TryBuildCoarseShapeMaskCalib(
                        rigidModelId,
                        coarseRows[i],
                        coarseCols[i],
                        coarseAngles[i],
                        maskErosionPx,
                        contourLevel,
                        maskFillDilatePx,
                        w,
                        h,
                        out CalibImage? maskCalib)
                    || maskCalib == null)
                {
                    continue;
                }

                masks[built] = maskCalib;
                indices[built] = i;
                rows[built] = coarseRows[i];
                cols[built] = coarseCols[i];
                angles[built] = coarseAngles[i];
                if (scores != null)
                    scores[built] = coarseScores![i];
                built++;
            }

            if (built == 0)
            {
                string diag = DescribeCoarseMaskBatchFailure(
                    rigidModelId, coarseRows, coarseCols, coarseAngles, maskErosionPx, contourLevel, maskFillDilatePx, w, h);
                throw new InvalidOperationException(
                    $"粗形状 Mask 批: {emitCount} 个候选均未生成有效填充区域。{diag}");
            }

            if (built < emitCount)
            {
                Array.Resize(ref masks, built);
                Array.Resize(ref indices, built);
                Array.Resize(ref rows, built);
                Array.Resize(ref cols, built);
                Array.Resize(ref angles, built);
                if (scores != null)
                    Array.Resize(ref scores, built);
            }

            return new HalconCoarseMaskBatch
            {
                SourceImage = image,
                ImageWidth = w,
                ImageHeight = h,
                Masks = masks,
                CoarseIndices = indices,
                CoarseRows = rows,
                CoarseCols = cols,
                CoarseAngles = angles,
                CoarseScores = scores
            };
        }

        /// <inheritdoc cref="BuildCoarseShapeMaskBatch"/>
        public static HalconCoarseMaskBatch ReduceDomainBatchByCoarseShapeMask(
            CalibImage image,
            long rigidModelId,
            double[] coarseRows,
            double[] coarseCols,
            double[]? coarseAngles,
            double[]? coarseScores,
            double maskErosionPx,
            int contourLevel = 1,
            int maxCandidates = 0,
            double maskFillDilatePx = 0)
            => BuildCoarseShapeMaskBatch(
                image, rigidModelId, coarseRows, coarseCols, coarseAngles, coarseScores,
                maskErosionPx, contourLevel, maxCandidates, maskFillDilatePx);

        private static string DescribeCoarseMaskBatchFailure(
            long rigidModelId,
            double[] coarseRows,
            double[] coarseCols,
            double[]? coarseAngles,
            double maskErosionPx,
            int contourLevel,
            double maskFillDilatePx,
            int imageWidth,
            int imageHeight)
        {
            try
            {
                long shapeId = ResolveRegisteredShapeModelId(rigidModelId);
                if (shapeId < 0)
                    return " ModelId 无效。";

                HShapeModel shapeModel = HalconShapeModelRegistry.Get(shapeId);
                using HXLDCont modelXld = shapeModel.GetShapeModelContours(contourLevel);
                int objCount = modelXld.CountObj();
                double row0 = coarseRows[0];
                double col0 = coarseCols[0];
                double ang0 = coarseAngles != null && coarseAngles.Length > 0 ? coarseAngles[0] : 0;
                if (objCount <= 0)
                    return $" 模板 contourLevel={contourLevel} 无轮廓；首候选 row={row0:F1} col={col0:F1} angle={ang0:F1}°。";

                double angleRad = ang0 * Math.PI / 180.0;
                HOperatorSet.HomMat2dIdentity(out HTuple hom);
                HOperatorSet.HomMat2dRotate(hom, angleRad, 0, 0, out hom);
                HOperatorSet.HomMat2dTranslate(hom, row0, col0, out hom);
                HOperatorSet.AffineTransContourXld(modelXld, out HObject transXld, hom);
                try
                {
                    HOperatorSet.CountObj(transXld, out HTuple txCount);
                    if (txCount.I <= 0)
                        return $" 首候选 AffineTransContourXld 为空；row={row0:F1} col={col0:F1} angle={ang0:F1}°。";

                    HObject fillXld = transXld;
                    bool disposeFillXld = false;
                    if (maskErosionPx > 0.5)
                    {
                        if (!TryInsetTransformedContourXldNormal(transXld, maskErosionPx, out HObject insetXld))
                            return $" 首候选轮廓内缩失败（maskErosionPx={maskErosionPx}）；可尝试减小该值。";
                        fillXld = insetXld;
                        disposeFillXld = true;
                    }

                    try
                    {
                        HRegion? filled = BuildFilledRegionFromTransformedXld(fillXld, maskFillDilatePx);
                        if (filled == null || !filled.IsInitialized())
                        {
                            return
                                $" 首候选轮廓填充失败（GenRegion/FillUp）；contourObjs={objCount} image={imageWidth}x{imageHeight} erosion={maskErosionPx} thread_num={HalconRuntimeSettings.EffectiveThreadNum}。";
                        }

                        filled.Dispose();
                    }
                    finally
                    {
                        if (disposeFillXld)
                            fillXld.Dispose();
                    }
                }
                finally
                {
                    transXld.Dispose();
                }

                return
                    $" 首候选 Region 可生成但批处理全失败；请检查是否在 STA 线程调用 HALCON（应走 HalconComputeRunner）。";
            }
            catch (Exception ex)
            {
                return $" 诊断异常: {ex.Message}";
            }
        }

        /// <summary>由单候选位姿生成填充区域 Mask 图（区域内 255）。</summary>
        public static bool TryBuildCoarseShapeMaskCalib(
            long rigidModelId,
            double coarseRow,
            double coarseCol,
            double coarseAngleDeg,
            double maskErosionPx,
            int contourLevel,
            double maskFillDilatePx,
            int imageWidth,
            int imageHeight,
            out CalibImage? maskCalib)
        {
            maskCalib = null;
            if (!TryBuildCoarseCandidateMaskRegion(
                    rigidModelId, coarseRow, coarseCol, coarseAngleDeg, maskErosionPx, contourLevel, maskFillDilatePx,
                    out HRegion? region))
                return false;

            try
            {
                maskCalib = RegionToMaskCalibImage(region!, imageWidth, imageHeight);
                return true;
            }
            finally
            {
                region?.Dispose();
            }
        }

        /// <summary>用填充 Mask 对原图 reduce_domain（精匹配内部使用）。</summary>
        public static CalibImage ReduceDomainCalibWithMask(CalibImage sourceImage, CalibImage maskCalib)
        {
            if (sourceImage == null)
                throw new ArgumentNullException(nameof(sourceImage));
            if (maskCalib == null)
                throw new ArgumentNullException(nameof(maskCalib));

            if (!TryRegionFromMaskCalib(maskCalib, out HRegion? region))
                throw new InvalidOperationException("无法从 Mask 图恢复区域");

            try
            {
                HObject ho = CalibToHObject(sourceImage);
                try
                {
                    ho = EnsureGray(ho);
                    using var hGray = new HImage(ho);
                    ho = null;
                    if (!TryReduceDomainImage(hGray, region, out HImage reduced))
                        throw new InvalidOperationException("reduce_domain 失败");
                    try
                    {
                        return ToCalibGray(reduced);
                    }
                    finally
                    {
                        reduced.Dispose();
                    }
                }
                finally
                {
                    ho?.Dispose();
                }
            }
            finally
            {
                region.Dispose();
            }
        }

        private static bool TryRegionFromMaskCalib(CalibImage maskCalib, out HRegion? region)
        {
            region = null;
            HObject ho = CalibToHObject(maskCalib);
            try
            {
                ho = EnsureGray(ho);
                HOperatorSet.Threshold(ho, out HObject regHo, 1, 255);
                try
                {
                    region = new HRegion(regHo);
                    return region.IsInitialized();
                }
                finally
                {
                    regHo.Dispose();
                }
            }
            finally
            {
                ho.Dispose();
            }
        }
        private static double ResolveMaskFillDilationPx(HObject transXld, double maskFillDilatePx)
        {
            if (maskFillDilatePx > 0.5)
                return maskFillDilatePx;

            try
            {
                HOperatorSet.SmallestRectangle2Xld(
                    transXld, out HTuple _, out HTuple _, out HTuple _, out HTuple len1, out HTuple len2);
                double maxSide = 0;
                int n = Math.Min(len1.Length, len2.Length);
                for (int i = 0; i < n; i++)
                {
                    double side = Math.Max(len1[i].D, len2[i].D) * 2.0;
                    if (side > maxSide)
                        maxSide = side;
                }

                if (maxSide > 4)
                    return Math.Clamp(maxSide * 0.30, 18.0, 160.0);
            }
            catch
            {
                // ignored
            }

            return 24.0;
        }

        /// <summary>
        /// 沿轮廓法向内缩变换后的模板 XLD（<c>gen_parallel_contour_xld</c>，非 <c>erosion_circle</c> 全向腐蚀）。
        /// </summary>
        private static bool TryInsetTransformedContourXldNormal(HObject transXld, double insetPx, out HObject insetXld)
        {
            if (insetPx <= 0.5)
            {
                HOperatorSet.CopyObj(transXld, out insetXld, 1, -1);
                return true;
            }

            try
            {
                HOperatorSet.GenParallelContourXld(
                    transXld,
                    out insetXld,
                    new HTuple("regression_normal"),
                    -insetPx);
                HOperatorSet.CountObj(insetXld, out HTuple n);
                return n.I > 0;
            }
            catch (HOperatorException)
            {
                HOperatorSet.GenEmptyObj(out insetXld);
                return false;
            }
        }

        /// <summary>
        /// 形状模型为边缘折线：默认用最小外接矩形实心化（避免凸包把长条两端撑长）；
        /// <paramref name="maskFillDilatePx"/>&gt;0 时改用手动膨胀（凹形工件）。
        /// 多条轮廓且非嵌套（如 C 形/开口环的两段弧）时，用包含全部轮廓的最小外接矩形，避免只保留最大连通域。
        /// </summary>
        private static HRegion? BuildFilledRegionFromTransformedXld(HObject transXld, double maskFillDilatePx)
        {
            HOperatorSet.CountObj(transXld, out HTuple count);
            if (count.I <= 0)
                return null;

            List<Point2D[]> contourPts = ContourXldToPointArrays(transXld);
            if (contourPts.Count >= 2 && !AreShapeModelContoursNested(contourPts))
                return BuildFilledRegionMinRectEnclosingContours(transXld, maskFillDilatePx);

            HOperatorSet.GenRegionContourXld(transXld, out HObject marginObj, new HTuple("margin"));
            HRegion work;
            try
            {
                work = new HRegion(marginObj);
            }
            finally
            {
                marginObj.Dispose();
            }

            if (!work.IsInitialized())
                return null;

            bool useManualDilate = maskFillDilatePx > 0.5;
            if (useManualDilate)
            {
                double dilatePx = maskFillDilatePx;
                HRegion dilated = work.DilationCircle(dilatePx);
                work.Dispose();
                work = dilated;
            }
            else
            {
                work = ReplaceRegionWithSmallestRectangle2(work);
                if (work == null || !work.IsInitialized())
                    return null;
            }

            HOperatorSet.FillUp(work, out HObject fillHo);
            try
            {
                HRegion filled = new HRegion(fillHo);
                work.Dispose();
                work = filled;
            }
            finally
            {
                fillHo.Dispose();
            }

            HOperatorSet.AreaCenter(work, out HTuple areaAfter, out HTuple _, out HTuple _);
            double filledArea = areaAfter.Length > 0 ? areaAfter[0].D : 0;
            if (filledArea < 64)
            {
                work.Dispose();
                return null;
            }

            HOperatorSet.Connection(work, out HObject connHo);
            try
            {
                HOperatorSet.SelectShapeStd(connHo, out HObject largestHo, "max_area", 70);
                try
                {
                    var outer = new HRegion(largestHo);
                    if (!outer.IsInitialized())
                        return null;

                    work.Dispose();
                    if (useManualDilate)
                        return SubtractInteriorHoleRegions(outer, connHo);
                    return outer;
                }
                finally
                {
                    largestHo.Dispose();
                }
            }
            finally
            {
                connHo.Dispose();
            }
        }

        /// <summary>两条及以上轮廓互不包含（非嵌套环）时判定为 false。</summary>
        private static bool AreShapeModelContoursNested(IReadOnlyList<Point2D[]> contours)
        {
            if (contours.Count < 2)
                return false;

            if (contours.Count == 2)
            {
                return IsContourMostlyInsideOther(contours[0], contours[1])
                    || IsContourMostlyInsideOther(contours[1], contours[0]);
            }

            int outerIdx = FindLargestContourIndex(contours);
            Point2D[] outer = contours[outerIdx];
            for (int i = 0; i < contours.Count; i++)
            {
                if (i == outerIdx)
                    continue;
                if (!IsContourMostlyInsideOther(contours[i], outer))
                    return false;
            }

            return true;
        }

        private static int FindLargestContourIndex(IReadOnlyList<Point2D[]> contours)
        {
            int best = 0;
            double bestArea = 0;
            for (int i = 0; i < contours.Count; i++)
            {
                double area = EstimateContourBoundingArea(contours[i]);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = i;
                }
            }

            return best;
        }

        private static double EstimateContourBoundingArea(Point2D[] pts)
        {
            if (pts.Length == 0)
                return 0;
            double minR = pts[0].Y, maxR = pts[0].Y, minC = pts[0].X, maxC = pts[0].X;
            foreach (Point2D p in pts)
            {
                minR = Math.Min(minR, p.Y);
                maxR = Math.Max(maxR, p.Y);
                minC = Math.Min(minC, p.X);
                maxC = Math.Max(maxC, p.X);
            }

            return Math.Max(1, (maxR - minR) * (maxC - minC));
        }

        private static bool IsContourMostlyInsideOther(Point2D[] inner, Point2D[] outer)
        {
            if (inner.Length < 3 || outer.Length < 3)
                return false;

            int inside = 0;
            foreach (Point2D p in inner)
            {
                if (IsPointInsideClosedPoly(p.X, p.Y, outer))
                    inside++;
            }

            return inside >= Math.Max(3, (int)(inner.Length * 0.5));
        }

        /// <summary>非嵌套多轮廓：并集后取最小外接矩形实心填充。</summary>
        private static HRegion? BuildFilledRegionMinRectEnclosingContours(HObject transXld, double maskFillDilatePx)
        {
            HRegion? union = UnionMarginRegionsFromContourXld(transXld);
            if (union == null || !union.IsInitialized())
                return null;

            try
            {
                if (maskFillDilatePx > 0.5)
                {
                    HRegion dilated = union.DilationCircle(maskFillDilatePx);
                    union.Dispose();
                    union = dilated;
                }

                HRegion? rect = ReplaceRegionWithSmallestRectangle2(union);
                union.Dispose();
                if (rect == null || !rect.IsInitialized())
                    return null;

                HOperatorSet.AreaCenter(rect, out HTuple areaT, out HTuple _, out HTuple _);
                double area = areaT.Length > 0 ? areaT[0].D : 0;
                if (area < 64)
                {
                    rect.Dispose();
                    return null;
                }

                return rect;
            }
            catch
            {
                union?.Dispose();
                throw;
            }
        }

        private static HRegion? UnionMarginRegionsFromContourXld(HObject transXld)
        {
            HOperatorSet.CountObj(transXld, out HTuple nObj);
            int n = nObj.I;
            if (n <= 0)
                return null;

            HRegion? union = null;
            for (int i = 1; i <= n; i++)
            {
                HObject one = n == 1 ? transXld : transXld.SelectObj(i);
                try
                {
                    HOperatorSet.GenRegionContourXld(one, out HObject marginHo, "margin");
                    try
                    {
                        using var part = new HRegion(marginHo);
                        if (!part.IsInitialized())
                            continue;
                        if (union == null)
                            union = part.CopyObj(1, -1);
                        else
                        {
                            HRegion merged = union.Union2(part);
                            union.Dispose();
                            union = merged;
                        }
                    }
                    finally
                    {
                        marginHo.Dispose();
                    }
                }
                finally
                {
                    if (n > 1)
                        one.Dispose();
                }
            }

            return union;
        }

        private static HRegion? ReplaceRegionWithSmallestRectangle2(HRegion source)
        {
            if (source == null || !source.IsInitialized())
                return null;

            HOperatorSet.SmallestRectangle2(
                source, out HTuple rectRow, out HTuple rectCol, out HTuple rectPhi, out HTuple len1, out HTuple len2);
            if (rectRow.Length == 0)
                return null;

            HOperatorSet.GenRectangle2(
                out HObject rectHo, rectRow[0], rectCol[0], rectPhi[0], len1[0], len2[0]);
            try
            {
                return new HRegion(rectHo);
            }
            finally
            {
                rectHo.Dispose();
            }
        }
        public static double ComputeMaskFillRatio(CalibImage mask)
        {
            if (mask == null)
                return 0;
            mask.RefreshProperties();
            if (mask.Width <= 0 || mask.Height <= 0)
                return 0;

            CalibImage gray = ToSingleChannelGray(mask);
            try
            {
                var ni = gray.GetNativeStruct();
                int nPix = ni.width * ni.height;
                if (nPix <= 0)
                    return 0;
                var buf = new byte[nPix];
                Marshal.Copy(ni.data, buf, 0, nPix);
                int on = 0;
                for (int i = 0; i < nPix; i++)
                {
                    if (buf[i] > 0)
                        on++;
                }

                return on / (double)nPix;
            }
            finally
            {
                if (!ReferenceEquals(gray, mask))
                    gray.Dispose();
            }
        }
        private static HRegion SubtractInteriorHoleRegions(HRegion outer, HObject connectedComponents)
        {
            HOperatorSet.AreaCenter(outer, out HTuple outerAreaT, out HTuple _, out HTuple _);
            double outerArea = outerAreaT.Length > 0 ? outerAreaT[0].D : 0;
            if (outerArea <= 0)
                return outer;

            HRegion result = outer.CopyObj(1, -1);
            bool disposeResult = false;
            HOperatorSet.CountObj(connectedComponents, out HTuple nObj);
            int n = nObj.I;
            for (int i = 1; i <= n; i++)
            {
                HObject one = connectedComponents.SelectObj(i);
                try
                {
                    using var comp = new HRegion(one);
                    if (!comp.IsInitialized())
                        continue;

                    HOperatorSet.AreaCenter(comp, out HTuple at, out HTuple rows, out HTuple cols);
                    if (at.Length == 0 || rows.Length == 0 || cols.Length == 0)
                        continue;

                    double compArea = at[0].D;
                    if (compArea >= outerArea * 0.98 || compArea < outerArea * 0.02)
                        continue;

                    if (result.TestRegionPoint(rows[0].D, cols[0].D) != 1)
                        continue;

                    HRegion diff = result.Difference(comp);
                    if (disposeResult)
                        result.Dispose();
                    disposeResult = true;
                    result = diff;
                }
                finally
                {
                    one.Dispose();
                }
            }

            return result;
        }

        private const int FineRoiMaxPyramidLevels = 4;

        private static int ClampFineRoiFindLevels(long deformableModelId, int requested)
        {
            if (requested > 0)
                return Math.Min(requested, FineRoiMaxPyramidLevels);
            return Math.Min(ResolveDeformableFindNumLevels(deformableModelId, 0), FineRoiMaxPyramidLevels);
        }
    }
}
#endif
