using System;
using System.Collections.Generic;
using System.Windows;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 选中一段后，把同一连通分量上的路径/连接线按该段做刚体变换（平移+旋转，不缩放），
    /// 使关联线的相对位置与方向保持不变。
    /// </summary>
    internal static class RoiLinkedGeometry
    {
        public readonly struct Similarity2D
        {
            public double OriginX { get; init; }
            public double OriginY { get; init; }
            public double NewOriginX { get; init; }
            public double NewOriginY { get; init; }
            public double Cos { get; init; }
            public double Sin { get; init; }
            public double Scale { get; init; }

            public Point Map(Point p)
            {
                double x = p.X - OriginX;
                double y = p.Y - OriginY;
                double xr = Scale * (Cos * x - Sin * y);
                double yr = Scale * (Sin * x + Cos * y);
                return new Point(NewOriginX + xr, NewOriginY + yr);
            }

            /// <summary>把边 AB 的位姿变到 A′B′：对齐起点并转到新方向，长度不缩放。</summary>
            public static Similarity2D FromEdge(Point a, Point b, Point a2, Point b2)
            {
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double dx2 = b2.X - a2.X;
                double dy2 = b2.Y - a2.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                double len2 = Math.Sqrt(dx2 * dx2 + dy2 * dy2);

                if (len < 1e-9 || len2 < 1e-9)
                {
                    return new Similarity2D
                    {
                        OriginX = a.X,
                        OriginY = a.Y,
                        NewOriginX = a2.X,
                        NewOriginY = a2.Y,
                        Cos = 1,
                        Sin = 0,
                        Scale = 1
                    };
                }

                double ang = Math.Atan2(dy2, dx2) - Math.Atan2(dy, dx);
                return new Similarity2D
                {
                    OriginX = a.X,
                    OriginY = a.Y,
                    NewOriginX = a2.X,
                    NewOriginY = a2.Y,
                    Cos = Math.Cos(ang),
                    Sin = Math.Sin(ang),
                    Scale = 1
                };
            }
        }

        public static void TransformPathInPlace(RoiContourPath path, Similarity2D t)
        {
            for (int i = 0; i < path.Vertices.Count; i++)
                path.Vertices[i] = t.Map(path.Vertices[i]);
            for (int i = 0; i < path.ArcVia.Count; i++)
            {
                if (path.ArcVia[i] is Point via)
                    path.ArcVia[i] = t.Map(via);
            }
        }

        public static void CopyAndTransformPath(RoiContourPath origin, RoiContourPath dest, Similarity2D t)
        {
            dest.Clear();
            dest.IsClosed = origin.IsClosed;
            foreach (Point p in origin.Vertices)
                dest.Vertices.Add(t.Map(p));
            foreach (RoiEdgeKind k in origin.EdgeKinds)
                dest.EdgeKinds.Add(k);
            foreach (Point? v in origin.ArcVia)
                dest.ArcVia.Add(v is Point p ? t.Map(p) : null);
        }

        public static RoiConnectorSegment TransformConnector(RoiConnectorSegment origin, Similarity2D t) =>
            new RoiConnectorSegment(t.Map(origin.Start), t.Map(origin.End));

        public static void CollectConnected(
            RoiContourPath? seedPath,
            int seedConnectorIndex,
            IReadOnlyList<RoiContourPath> allPaths,
            IReadOnlyList<RoiConnectorSegment> connectors,
            double snapTol,
            out HashSet<RoiContourPath> paths,
            out HashSet<int> connectorIndices)
        {
            paths = new HashSet<RoiContourPath>(ReferenceEqualityComparer.Instance);
            connectorIndices = new HashSet<int>();
            double tol = Math.Max(snapTol, 1e-6);
            var pathQueue = new Queue<RoiContourPath>();
            var connQueue = new Queue<int>();

            if (seedPath != null)
            {
                paths.Add(seedPath);
                pathQueue.Enqueue(seedPath);
            }

            if (seedConnectorIndex >= 0 && seedConnectorIndex < connectors.Count)
            {
                connectorIndices.Add(seedConnectorIndex);
                connQueue.Enqueue(seedConnectorIndex);
            }

            while (pathQueue.Count > 0 || connQueue.Count > 0)
            {
                while (pathQueue.Count > 0)
                {
                    RoiContourPath path = pathQueue.Dequeue();
                    for (int i = 0; i < connectors.Count; i++)
                    {
                        if (connectorIndices.Contains(i))
                            continue;
                        if (!ConnectorTouchesPath(connectors[i], path, tol))
                            continue;
                        connectorIndices.Add(i);
                        connQueue.Enqueue(i);
                    }

                    foreach (RoiContourPath other in allPaths)
                    {
                        if (paths.Contains(other) || other.VertexCount == 0)
                            continue;
                        if (!PathsTouch(path, other, tol))
                            continue;
                        paths.Add(other);
                        pathQueue.Enqueue(other);
                    }
                }

                while (connQueue.Count > 0)
                {
                    int ci = connQueue.Dequeue();
                    RoiConnectorSegment c = connectors[ci];
                    foreach (RoiContourPath path in allPaths)
                    {
                        if (paths.Contains(path) || path.VertexCount == 0)
                            continue;
                        if (!ConnectorTouchesPath(c, path, tol))
                            continue;
                        paths.Add(path);
                        pathQueue.Enqueue(path);
                    }

                    for (int j = 0; j < connectors.Count; j++)
                    {
                        if (connectorIndices.Contains(j))
                            continue;
                        if (!ConnectorsTouch(c, connectors[j], tol))
                            continue;
                        connectorIndices.Add(j);
                        connQueue.Enqueue(j);
                    }
                }
            }
        }

        private static bool Near(Point a, Point b, double tol)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return dx * dx + dy * dy <= tol * tol;
        }

        private static bool PathTouchesPoint(RoiContourPath path, Point p, double tol)
        {
            foreach (Point v in path.Vertices)
            {
                if (Near(v, p, tol))
                    return true;
            }

            return false;
        }

        private static bool ConnectorTouchesPath(RoiConnectorSegment c, RoiContourPath path, double tol) =>
            PathTouchesPoint(path, c.Start, tol) || PathTouchesPoint(path, c.End, tol);

        private static bool ConnectorsTouch(RoiConnectorSegment a, RoiConnectorSegment b, double tol) =>
            Near(a.Start, b.Start, tol) || Near(a.Start, b.End, tol)
            || Near(a.End, b.Start, tol) || Near(a.End, b.End, tol);

        private static bool PathsTouch(RoiContourPath a, RoiContourPath b, double tol)
        {
            foreach (Point va in a.Vertices)
            {
                foreach (Point vb in b.Vertices)
                {
                    if (Near(va, vb, tol))
                        return true;
                }
            }

            return false;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<RoiContourPath>
        {
            public static readonly ReferenceEqualityComparer Instance = new();

            public bool Equals(RoiContourPath? x, RoiContourPath? y) => ReferenceEquals(x, y);

            public int GetHashCode(RoiContourPath obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
