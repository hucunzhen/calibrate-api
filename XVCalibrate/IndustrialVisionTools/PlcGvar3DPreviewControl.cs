using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// PLC 页嵌入：Helix 3D 显示 GVAR 线段列表与当前 XYZ 轴位置。
    /// 世界坐标轴 + XY 网格常驻；刷新仅更新轨迹/轴标记，不重置视角。
    /// </summary>
    public sealed class PlcGvar3DPreviewControl : UserControl
    {
        const double WorldGridExtent = 400.0;
        const double WorldAxisLength = 50.0;

        readonly HelixViewport3D _viewport = new()
        {
            ZoomExtentsWhenLoaded = false,
            ShowCoordinateSystem = false,
            ShowViewCube = false
        };
        readonly DefaultLights _lights = new();
        readonly List<Visual3D> _dynamicVisuals = new();
        readonly GridLinesVisual3D _worldGrid = new()
        {
            Width = WorldGridExtent,
            Length = WorldGridExtent,
            MajorDistance = 20,
            MinorDistance = 5,
            Thickness = 0.02
        };
        readonly CoordinateSystemVisual3D _worldAxes = new()
        {
            ArrowLengths = WorldAxisLength
        };
        bool _worldFrameReady;
        bool _initialZoomDone;

        public PlcGvar3DPreviewControl()
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 32));
            _viewport.CameraRotationMode = CameraRotationMode.Turntable;
            _viewport.Children.Add(_lights);
            EnsureWorldFrame();
            Content = _viewport;
        }

        void EnsureWorldFrame()
        {
            if (_worldFrameReady)
                return;

            _viewport.Children.Insert(0, _worldGrid);
            _viewport.Children.Insert(1, _worldAxes);
            AddWorldAxisLabels();
            _worldFrameReady = true;
        }

        void AddWorldAxisLabels()
        {
            double d = WorldAxisLength * 1.15;
            _viewport.Children.Add(new BillboardTextVisual3D
            {
                Position = new Point3D(d, 0, 0),
                Text = "X",
                Foreground = Brushes.Red,
                FontSize = 14
            });
            _viewport.Children.Add(new BillboardTextVisual3D
            {
                Position = new Point3D(0, d, 0),
                Text = "Y",
                Foreground = Brushes.LimeGreen,
                FontSize = 14
            });
            _viewport.Children.Add(new BillboardTextVisual3D
            {
                Position = new Point3D(0, 0, d),
                Text = "Z",
                Foreground = Brushes.DeepSkyBlue,
                FontSize = 14
            });
        }

        /// <summary>
        /// 刷新场景：GVAR 线段 + 当前轴位置；不调用 ZoomExtents，保留用户旋转/缩放。
        /// </summary>
        public void SetScene(GVAR[]? segments, double axisX, double axisY, double axisZ, bool hasAxisPosition)
        {
            ClearDynamicVisuals();

            var bounds = new List<Point3D>();
            if (segments != null)
            {
                foreach (var g in segments)
                    AppendGvarBounds(g, bounds);
            }

            if (hasAxisPosition && !double.IsNaN(axisX) && !double.IsNaN(axisY) && !double.IsNaN(axisZ))
            {
                var axisPt = new Point3D(axisX, axisY, axisZ);
                bounds.Add(axisPt);
                AddAxisMarker(axisPt, ComputeMarkerSize(bounds));
            }

            if (segments != null && segments.Length > 0)
                AddGvarSegments(segments);

            if (!_initialZoomDone && bounds.Count > 0)
            {
                _viewport.ZoomExtents();
                _initialZoomDone = true;
            }
        }

        public void ClearScene()
        {
            ClearDynamicVisuals();
        }

        /// <summary>重置为默认视角（可选，当前未绑定按钮）。</summary>
        public void ResetCamera()
        {
            _viewport.ZoomExtents();
            _initialZoomDone = true;
        }

        void ClearDynamicVisuals()
        {
            foreach (var v in _dynamicVisuals)
                _viewport.Children.Remove(v);
            _dynamicVisuals.Clear();
        }

        void AddGvarSegments(GVAR[] segments)
        {
            var palette = new[]
            {
                Color.FromRgb(123, 31, 162),
                Color.FromRgb(66, 165, 245),
                Color.FromRgb(38, 166, 154),
                Color.FromRgb(255, 183, 77),
                Color.FromRgb(171, 71, 188)
            };

            for (int i = 0; i < segments.Length; i++)
            {
                var pts = SampleGvarPolyline(segments[i]);
                if (pts.Count == 0)
                    continue;

                var color = new SolidColorBrush(palette[i % palette.Length]);
                if (pts.Count == 1)
                {
                    AddDynamicVisual(new SphereVisual3D
                    {
                        Center = pts[0],
                        Radius = 0.5,
                        Fill = color
                    });
                    continue;
                }

                if (pts.Count == 2)
                {
                    AddDynamicVisual(new LinesVisual3D
                    {
                        Points = new Point3DCollection(pts),
                        Color = ((SolidColorBrush)color).Color,
                        Thickness = 2.5
                    });
                    continue;
                }

                var pc = new Point3DCollection();
                foreach (var p in pts)
                    pc.Add(p);
                AddDynamicVisual(new TubeVisual3D
                {
                    Path = pc,
                    Diameter = 0.45,
                    ThetaDiv = 8,
                    Fill = color
                });
            }
        }

        static List<Point3D> SampleGvarPolyline(GVAR g)
        {
            var pts = new List<Point3D>();
            bool maybeArc = Math.Abs(g.r) > 1e-4f
                && Math.Abs(g.end_deg - g.start_deg) > 1e-3f
                && (Math.Abs(g.cx) > 1e-6f || Math.Abs(g.cy) > 1e-6f);

            if (maybeArc)
            {
                const int steps = 32;
                double a0 = g.start_deg * Math.PI / 180.0;
                double a1 = g.end_deg * Math.PI / 180.0;
                for (int i = 0; i <= steps; i++)
                {
                    double t = i / (double)steps;
                    double ang = a0 + (a1 - a0) * t;
                    float z = g.z0 + (g.z1 - g.z0) * (float)t;
                    pts.Add(new Point3D(
                        g.cx + g.r * Math.Cos(ang),
                        g.cy + g.r * Math.Sin(ang),
                        z));
                }
                return pts;
            }

            pts.Add(new Point3D(g.spVec3_p0.x, g.spVec3_p0.y, g.spVec3_p0.z));
            pts.Add(new Point3D(g.spVec3_p1.x, g.spVec3_p1.y, g.spVec3_p1.z));
            return pts;
        }

        static void AppendGvarBounds(GVAR g, List<Point3D> bounds)
        {
            foreach (var p in SampleGvarPolyline(g))
                bounds.Add(p);
        }

        void AddAxisMarker(Point3D center, double arm)
        {
            double r = Math.Max(arm * 0.35, 0.8);
            AddDynamicVisual(new SphereVisual3D
            {
                Center = center,
                Radius = r,
                Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0))
            });

            AddDynamicVisual(MakeAxisLine(center, new Vector3D(arm, 0, 0), Colors.OrangeRed));
            AddDynamicVisual(MakeAxisLine(center, new Vector3D(0, arm, 0), Colors.Gold));
            AddDynamicVisual(MakeAxisLine(center, new Vector3D(0, 0, arm), Colors.DeepSkyBlue));
        }

        static LinesVisual3D MakeAxisLine(Point3D origin, Vector3D dir, Color color)
        {
            var end = origin + dir;
            return new LinesVisual3D
            {
                Points = new Point3DCollection { origin, end },
                Color = color,
                Thickness = 3
            };
        }

        static double ComputeMarkerSize(IReadOnlyList<Point3D> bounds)
        {
            if (bounds.Count == 0)
                return 5.0;
            double minX = bounds[0].X, maxX = minX;
            double minY = bounds[0].Y, maxY = minY;
            double minZ = bounds[0].Z, maxZ = minZ;
            for (int i = 1; i < bounds.Count; i++)
            {
                var p = bounds[i];
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }
            double span = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            return Math.Clamp(span * 0.08, 2.0, 40.0);
        }

        void AddDynamicVisual(Visual3D visual)
        {
            _viewport.Children.Add(visual);
            _dynamicVisuals.Add(visual);
        }
    }
}
