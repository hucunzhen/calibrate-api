using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;

namespace CalibOperatorPInvoke;

/// <summary>
/// 独立窗口：用 HelixToolkit 显示 3D 折线轨迹（管状体 + 单点时球）。
/// </summary>
public sealed class Trajectory3DPreviewWindow : Window
{
    readonly HelixViewport3D _viewport = new() { ZoomExtentsWhenLoaded = true };
    readonly DefaultLights _lights = new();
    GridLinesVisual3D? _grid;
    Visual3D? _trajectoryVisual;

    public Trajectory3DPreviewWindow()
    {
        Title = "3D 轨迹";
        Width = 960;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(28, 28, 32));

        _viewport.Children.Add(_lights);
        _viewport.CameraRotationMode = CameraRotationMode.Turntable;
        Content = _viewport;
    }

    public void ApplyTrajectory(IReadOnlyList<CalibPoint3D> path, double tubeDiameter, bool showGrid, double gridExtent)
    {
        if (_trajectoryVisual != null)
        {
            _viewport.Children.Remove(_trajectoryVisual);
            _trajectoryVisual = null;
        }

        if (showGrid)
        {
            if (_grid == null)
            {
                _grid = new GridLinesVisual3D
                {
                    Width = gridExtent,
                    Length = gridExtent,
                    MajorDistance = Math.Max(gridExtent / 20.0, 1.0),
                    MinorDistance = Math.Max(gridExtent / 40.0, 0.5),
                    Thickness = 0.02
                };
                _viewport.Children.Insert(0, _grid);
            }
        }
        else if (_grid != null)
        {
            _viewport.Children.Remove(_grid);
            _grid = null;
        }

        double d = tubeDiameter > 1e-9 ? tubeDiameter : 0.8;
        if (path.Count == 0)
            return;

        if (path.Count == 1)
        {
            var p = path[0];
            var sph = new SphereVisual3D
            {
                Center = new Point3D(p.X, p.Y, p.Z),
                Radius = Math.Max(d * 0.6, 0.35),
                Fill = new SolidColorBrush(Color.FromRgb(64, 169, 255))
            };
            _trajectoryVisual = sph;
            _viewport.Children.Add(sph);
        }
        else
        {
            var pc = new Point3DCollection();
            foreach (var p in path)
                pc.Add(new Point3D(p.X, p.Y, p.Z));
            var tube = new TubeVisual3D
            {
                Path = pc,
                Diameter = d,
                ThetaDiv = 10,
                Fill = new SolidColorBrush(Color.FromRgb(64, 169, 255))
            };
            _trajectoryVisual = tube;
            _viewport.Children.Add(tube);
        }

        _viewport.ZoomExtents();
    }
}
