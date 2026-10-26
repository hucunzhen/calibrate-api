using System;
using System.Collections.Generic;
using System.Windows;

namespace CalibOperatorCLI_Example
{
    public partial class HalconShapeModelPage
    {
        private enum OpenTrajHistoryKind
        {
            /// <summary>当前草稿上增加一个顶点（直线/圆弧入边，或首点）。</summary>
            PathVertex,
            /// <summary>增加一条连接线（可含自动完成本条 + 从终点续画首点）。</summary>
            Connector,
            /// <summary>「完成本条」把草稿写入列表。</summary>
            FinishEntry
        }

        private sealed class OpenTrajHistoryItem
        {
            public OpenTrajHistoryKind Kind { get; init; }

            public bool IsFirstVertex { get; init; }
            public Point Vertex { get; init; }
            public RoiEdgeKind EdgeKind { get; init; } = RoiEdgeKind.Line;
            public Point? ArcVia { get; init; }

            public RoiConnectorSegment? Connector { get; init; }
            public OpenTrajectoryEntry? AutoFinishedEntry { get; set; }
            public int? AutoFinishedReplacedEditIndex { get; set; }
            public RoiContourPath? AutoFinishedPreviousPath { get; set; }
            public Point? ClearedSingletonVertex { get; set; }
            public bool AddedResumeVertex { get; set; }
            public Point ResumeVertex { get; set; }

            public OpenTrajectoryEntry? FinishedEntry { get; set; }
            public int? FinishReplacedEditIndex { get; set; }
            public RoiContourPath? FinishPreviousPath { get; set; }
        }

        private readonly List<OpenTrajHistoryItem> _openTrajUndo = new();
        private readonly List<OpenTrajHistoryItem> _openTrajRedo = new();

        /// <summary>多边形/环形：撤销顶点后可重做。</summary>
        private readonly List<PathVertexRedoItem> _pathVertexRedo = new();

        private sealed class PathVertexRedoItem
        {
            public RoiContourPath Path { get; init; } = null!;
            public bool WasFirst { get; init; }
            public Point Vertex { get; init; }
            public RoiEdgeKind EdgeKind { get; init; }
            public Point? ArcVia { get; init; }
            public bool WasClosed { get; init; }
        }

        private void ClearOpenTrajHistory()
        {
            _openTrajUndo.Clear();
            _openTrajRedo.Clear();
        }

        private void ClearPathVertexRedo() => _pathVertexRedo.Clear();

        private void PushOpenTrajHistory(OpenTrajHistoryItem item)
        {
            _openTrajUndo.Add(item);
            _openTrajRedo.Clear();
            ScheduleSessionPersist();
        }

        private void CaptureOpenTrajHistoryIntoSettings(HalconShapeModelUiSettings s)
        {
            s.OpenTrajectoryUndo = ExportOpenTrajHistoryList(_openTrajUndo);
            s.OpenTrajectoryRedo = ExportOpenTrajHistoryList(_openTrajRedo);
        }

        private void ApplyOpenTrajHistoryFromSettings(HalconShapeModelUiSettings s)
        {
            _openTrajUndo.Clear();
            _openTrajRedo.Clear();
            if (s.OpenTrajectoryUndo != null)
            {
                foreach (OpenTrajHistoryItemDto? dto in s.OpenTrajectoryUndo)
                {
                    if (ImportOpenTrajHistoryItem(dto) is OpenTrajHistoryItem item)
                        _openTrajUndo.Add(item);
                }
            }

            if (s.OpenTrajectoryRedo != null)
            {
                foreach (OpenTrajHistoryItemDto? dto in s.OpenTrajectoryRedo)
                {
                    if (ImportOpenTrajHistoryItem(dto) is OpenTrajHistoryItem item)
                        _openTrajRedo.Add(item);
                }
            }
        }

        private static List<OpenTrajHistoryItemDto> ExportOpenTrajHistoryList(List<OpenTrajHistoryItem> src)
        {
            var list = new List<OpenTrajHistoryItemDto>(src.Count);
            foreach (OpenTrajHistoryItem item in src)
                list.Add(ExportOpenTrajHistoryItem(item));
            return list;
        }

        private static OpenTrajHistoryItemDto ExportOpenTrajHistoryItem(OpenTrajHistoryItem item)
        {
            var dto = new OpenTrajHistoryItemDto
            {
                Kind = item.Kind.ToString(),
                IsFirstVertex = item.IsFirstVertex,
                VertexX = item.Vertex.X,
                VertexY = item.Vertex.Y,
                EdgeKind = item.EdgeKind.ToString(),
                ArcViaX = item.ArcVia?.X,
                ArcViaY = item.ArcVia?.Y,
                AutoFinishedReplacedEditIndex = item.AutoFinishedReplacedEditIndex,
                AddedResumeVertex = item.AddedResumeVertex,
                ResumeVertexX = item.ResumeVertex.X,
                ResumeVertexY = item.ResumeVertex.Y,
                FinishReplacedEditIndex = item.FinishReplacedEditIndex
            };

            if (item.Connector != null)
            {
                dto.Connector = new HalconShapeModelRoiConnectorDto
                {
                    StartX = item.Connector.StartX,
                    StartY = item.Connector.StartY,
                    EndX = item.Connector.EndX,
                    EndY = item.Connector.EndY
                };
            }

            if (item.AutoFinishedEntry != null)
            {
                dto.AutoFinishedName = item.AutoFinishedEntry.Name;
                dto.AutoFinishedPath = ExportRoiPath(item.AutoFinishedEntry.Path);
            }

            if (item.AutoFinishedPreviousPath != null)
                dto.AutoFinishedPreviousPath = ExportRoiPath(item.AutoFinishedPreviousPath);

            if (item.ClearedSingletonVertex is Point single)
            {
                dto.ClearedSingletonX = single.X;
                dto.ClearedSingletonY = single.Y;
            }

            if (item.FinishedEntry != null)
            {
                dto.FinishedName = item.FinishedEntry.Name;
                dto.FinishedPath = ExportRoiPath(item.FinishedEntry.Path);
            }

            if (item.FinishPreviousPath != null)
                dto.FinishPreviousPath = ExportRoiPath(item.FinishPreviousPath);

            return dto;
        }

        private static OpenTrajHistoryItem? ImportOpenTrajHistoryItem(OpenTrajHistoryItemDto? dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.Kind))
                return null;
            if (!Enum.TryParse(dto.Kind, ignoreCase: true, out OpenTrajHistoryKind kind))
                return null;

            if (!Enum.TryParse(dto.EdgeKind ?? "Line", ignoreCase: true, out RoiEdgeKind edgeKind))
                edgeKind = RoiEdgeKind.Line;

            RoiConnectorSegment? connector = null;
            if (dto.Connector != null)
            {
                connector = new RoiConnectorSegment(
                    dto.Connector.StartX, dto.Connector.StartY,
                    dto.Connector.EndX, dto.Connector.EndY);
            }

            OpenTrajectoryEntry? autoFinished = null;
            if (dto.AutoFinishedPath != null)
            {
                var path = new RoiContourPath();
                ImportRoiPath(path, dto.AutoFinishedPath);
                path.IsClosed = false;
                autoFinished = new OpenTrajectoryEntry
                {
                    Name = dto.AutoFinishedName ?? "",
                    Path = path
                };
            }

            RoiContourPath? autoPrev = null;
            if (dto.AutoFinishedPreviousPath != null)
            {
                autoPrev = new RoiContourPath();
                ImportRoiPath(autoPrev, dto.AutoFinishedPreviousPath);
                autoPrev.IsClosed = false;
            }

            OpenTrajectoryEntry? finished = null;
            if (dto.FinishedPath != null)
            {
                var path = new RoiContourPath();
                ImportRoiPath(path, dto.FinishedPath);
                path.IsClosed = false;
                finished = new OpenTrajectoryEntry
                {
                    Name = dto.FinishedName ?? "",
                    Path = path
                };
            }

            RoiContourPath? finishPrev = null;
            if (dto.FinishPreviousPath != null)
            {
                finishPrev = new RoiContourPath();
                ImportRoiPath(finishPrev, dto.FinishPreviousPath);
                finishPrev.IsClosed = false;
            }

            return new OpenTrajHistoryItem
            {
                Kind = kind,
                IsFirstVertex = dto.IsFirstVertex,
                Vertex = new Point(dto.VertexX, dto.VertexY),
                EdgeKind = edgeKind,
                ArcVia = dto.ArcViaX is double avx && dto.ArcViaY is double avy
                    ? new Point(avx, avy)
                    : null,
                Connector = connector,
                AutoFinishedEntry = autoFinished,
                AutoFinishedReplacedEditIndex = dto.AutoFinishedReplacedEditIndex,
                AutoFinishedPreviousPath = autoPrev,
                ClearedSingletonVertex = dto.ClearedSingletonX is double csx && dto.ClearedSingletonY is double csy
                    ? new Point(csx, csy)
                    : null,
                AddedResumeVertex = dto.AddedResumeVertex,
                ResumeVertex = new Point(dto.ResumeVertexX, dto.ResumeVertexY),
                FinishedEntry = finished,
                FinishReplacedEditIndex = dto.FinishReplacedEditIndex,
                FinishPreviousPath = finishPrev
            };
        }

        private void RecordOpenTrajPathVertexIfActive(RoiContourPath path)
        {
            if (!IsOpenTrajectoriesModeActive() || !ReferenceEquals(path, _roiPath))
                return;
            if (path.VertexCount < 1)
                return;

            bool isFirst = path.VertexCount == 1;
            Point vertex = path.Vertices[^1];
            RoiEdgeKind kind = RoiEdgeKind.Line;
            Point? via = null;
            if (!isFirst && path.EdgeKinds.Count > 0)
            {
                int ei = path.EdgeKinds.Count - 1;
                kind = path.EdgeKinds[ei];
                if (kind == RoiEdgeKind.Arc && ei < path.ArcVia.Count)
                    via = path.ArcVia[ei];
            }

            PushOpenTrajHistory(new OpenTrajHistoryItem
            {
                Kind = OpenTrajHistoryKind.PathVertex,
                IsFirstVertex = isFirst,
                Vertex = vertex,
                EdgeKind = kind,
                ArcVia = via
            });
        }

        private bool TryUndoOpenTrajectoryHistory()
        {
            if (_connectorDraftStart != null)
            {
                _connectorDraftStart = null;
                UpdateConnectorRubberVisual();
                AppendLog("已取消未完成的连接线");
                return true;
            }

            if (_arcDraftEnd != null)
            {
                CancelArcDraft();
                UpdatePolygonPreview();
                AppendLog("已取消未完成的圆弧段");
                return true;
            }

            if (_openTrajUndo.Count == 0)
                return false;

            OpenTrajHistoryItem item = _openTrajUndo[^1];
            _openTrajUndo.RemoveAt(_openTrajUndo.Count - 1);
            ApplyOpenTrajUndo(item);
            _openTrajRedo.Add(item);
            RefreshOpenTrajectoryVisualAfterHistory();
            PersistSessionChange();
            return true;
        }

        private bool TryRedoOpenTrajectoryHistory()
        {
            if (_openTrajRedo.Count == 0)
                return false;

            OpenTrajHistoryItem item = _openTrajRedo[^1];
            _openTrajRedo.RemoveAt(_openTrajRedo.Count - 1);
            ApplyOpenTrajRedo(item);
            _openTrajUndo.Add(item);
            RefreshOpenTrajectoryVisualAfterHistory();
            PersistSessionChange();
            return true;
        }

        private void ApplyOpenTrajUndo(OpenTrajHistoryItem item)
        {
            switch (item.Kind)
            {
                case OpenTrajHistoryKind.PathVertex:
                    if (_roiPath.VertexCount > 0)
                        _roiPath.RemoveLastVertex();
                    AppendLog($"开放轨迹撤销: 普通段，当前条剩余 {_roiPath.VertexCount} 个顶点");
                    break;

                case OpenTrajHistoryKind.Connector:
                    UndoConnectorAction(item);
                    AppendLog($"开放轨迹撤销: 连接线，剩余 {_trajectoryConnectors.Count} 条");
                    break;

                case OpenTrajHistoryKind.FinishEntry:
                    UndoFinishEntryAction(item);
                    AppendLog("开放轨迹撤销: 完成本条");
                    break;
            }
        }

        private void ApplyOpenTrajRedo(OpenTrajHistoryItem item)
        {
            switch (item.Kind)
            {
                case OpenTrajHistoryKind.PathVertex:
                    RedoPathVertexAction(item);
                    AppendLog($"开放轨迹重做: 普通段，当前条 {_roiPath.VertexCount} 个顶点");
                    break;

                case OpenTrajHistoryKind.Connector:
                    RedoConnectorAction(item);
                    AppendLog($"开放轨迹重做: 连接线，共 {_trajectoryConnectors.Count} 条");
                    break;

                case OpenTrajHistoryKind.FinishEntry:
                    RedoFinishEntryAction(item);
                    AppendLog("开放轨迹重做: 完成本条");
                    break;
            }
        }

        private void UndoConnectorAction(OpenTrajHistoryItem item)
        {
            if (item.AddedResumeVertex
                && _roiPath.VertexCount >= 1
                && Dist(_roiPath.Vertices[0], item.ResumeVertex) < 1.0)
            {
                // 连接线之后的续画草稿：按时间序撤销时此处应只剩续画首点
                _roiPath.Clear();
            }

            if (_trajectoryConnectors.Count > 0
                && item.Connector != null
                && Dist(_trajectoryConnectors[^1].Start, item.Connector.Start) < 1e-6
                && Dist(_trajectoryConnectors[^1].End, item.Connector.End) < 1e-6)
            {
                _trajectoryConnectors.RemoveAt(_trajectoryConnectors.Count - 1);
            }
            else if (_trajectoryConnectors.Count > 0)
                _trajectoryConnectors.RemoveAt(_trajectoryConnectors.Count - 1);

            if (item.AutoFinishedEntry != null)
                RestoreFinishedEntryToDraft(item.AutoFinishedEntry, item.AutoFinishedReplacedEditIndex, item.AutoFinishedPreviousPath);
            else if (item.ClearedSingletonVertex is Point single)
            {
                _roiPath.Clear();
                _roiPath.AddFirstVertex(single);
            }
        }

        private void RedoConnectorAction(OpenTrajHistoryItem item)
        {
            if (item.Connector == null)
                return;

            if (item.AutoFinishedEntry != null)
                ReapplyFinishEntry(item.AutoFinishedEntry, item.AutoFinishedReplacedEditIndex, item.AutoFinishedPreviousPath);
            else if (item.ClearedSingletonVertex != null)
                _roiPath.Clear();

            _trajectoryConnectors.Add(item.Connector);
            if (item.AddedResumeVertex)
            {
                _roiPath.Clear();
                _roiPath.AddFirstVertex(item.ResumeVertex);
            }
        }

        private void UndoFinishEntryAction(OpenTrajHistoryItem item)
        {
            if (item.FinishedEntry == null)
                return;
            RestoreFinishedEntryToDraft(item.FinishedEntry, item.FinishReplacedEditIndex, item.FinishPreviousPath);
        }

        private void RedoFinishEntryAction(OpenTrajHistoryItem item)
        {
            if (item.FinishedEntry == null)
                return;
            ReapplyFinishEntry(item.FinishedEntry, item.FinishReplacedEditIndex, item.FinishPreviousPath);
        }

        private void RestoreFinishedEntryToDraft(
            OpenTrajectoryEntry finished,
            int? replacedEditIndex,
            RoiContourPath? previousPath)
        {
            _roiPath.Clear();
            CopyContourPathContents(finished.Path, _roiPath);
            _roiPath.IsClosed = false;

            if (replacedEditIndex is int idx && idx >= 0 && idx < _openTrajectoryEntries.Count)
            {
                if (previousPath != null)
                    _openTrajectoryEntries[idx].Path = CloneContourPath(previousPath);
                _editingOpenTrajectoryIndex = idx;
            }
            else if (_openTrajectoryEntries.Count > 0)
            {
                _openTrajectoryEntries.RemoveAt(_openTrajectoryEntries.Count - 1);
                _editingOpenTrajectoryIndex = null;
            }

            _hasOpenTrajectoriesRoi = _openTrajectoryEntries.Count > 0;
            _hasRoi = _hasOpenTrajectoriesRoi || _roiPath.VertexCount >= 2;
        }

        private void ReapplyFinishEntry(
            OpenTrajectoryEntry finished,
            int? replacedEditIndex,
            RoiContourPath? previousPath)
        {
            RoiContourPath saved = CloneContourPath(finished.Path);
            saved.IsClosed = false;
            if (replacedEditIndex is int idx && idx >= 0 && idx < _openTrajectoryEntries.Count)
            {
                _openTrajectoryEntries[idx].Path = saved;
                _editingOpenTrajectoryIndex = null;
            }
            else
            {
                _openTrajectoryEntries.Add(new OpenTrajectoryEntry
                {
                    Name = finished.Name,
                    Path = saved
                });
                _editingOpenTrajectoryIndex = null;
            }

            _roiPath.Clear();
            _hasOpenTrajectoriesRoi = true;
            _hasRoi = true;
            _ = previousPath;
        }

        private void RedoPathVertexAction(OpenTrajHistoryItem item)
        {
            if (item.IsFirstVertex || _roiPath.VertexCount == 0)
                _roiPath.AddFirstVertex(item.Vertex);
            else if (item.EdgeKind == RoiEdgeKind.Arc && item.ArcVia is Point via)
                _roiPath.AddArcSegment(item.Vertex, via);
            else
                _roiPath.AddLineSegment(item.Vertex);
        }

        private void RefreshOpenTrajectoryVisualAfterHistory()
        {
            CancelArcDraft();
            _connectorDraftStart = null;
            InvalidatePolygonFlatCache();
            RebuildOpenTrajectoriesVisual();
            RebuildConnectorsVisual();
            UpdatePolygonPreview();
            UpdateConnectorRubberVisual();
            RefreshOpenTrajectoryListUi();
        }

        private bool TryRedoPathVertex()
        {
            if (_pathVertexRedo.Count == 0)
                return false;

            PathVertexRedoItem item = _pathVertexRedo[^1];
            _pathVertexRedo.RemoveAt(_pathVertexRedo.Count - 1);

            if (item.WasFirst || item.Path.VertexCount == 0)
                item.Path.AddFirstVertex(item.Vertex);
            else if (item.EdgeKind == RoiEdgeKind.Arc && item.ArcVia is Point via)
                item.Path.AddArcSegment(item.Vertex, via);
            else
                item.Path.AddLineSegment(item.Vertex);

            if (item.WasClosed && item.Path.VertexCount >= 3)
            {
                // 闭合状态在开放撤销时不会进此栈；多边形闭合后一般不再走顶点撤销
            }

            if (ReferenceEquals(item.Path, _roiPath))
            {
                InvalidatePolygonFlatCache();
                UpdatePolygonPreview();
            }
            else
            {
                InvalidateRingFlatCache();
                UpdateRingPreview();
                UpdateRingRubberVisual();
            }

            AppendLog($"重做: 已恢复顶点，当前 {item.Path.VertexCount} 个");
            return true;
        }

        private void CapturePathVertexForRedo(RoiContourPath path)
        {
            if (path.VertexCount < 1)
                return;

            bool wasFirst = path.VertexCount == 1;
            Point vertex = path.Vertices[^1];
            RoiEdgeKind kind = RoiEdgeKind.Line;
            Point? via = null;
            if (!wasFirst && path.EdgeKinds.Count > 0)
            {
                int ei = path.EdgeKinds.Count - 1;
                kind = path.EdgeKinds[ei];
                if (kind == RoiEdgeKind.Arc && ei < path.ArcVia.Count)
                    via = path.ArcVia[ei];
            }

            _pathVertexRedo.Add(new PathVertexRedoItem
            {
                Path = path,
                WasFirst = wasFirst,
                Vertex = vertex,
                EdgeKind = kind,
                ArcVia = via,
                WasClosed = path.IsClosed
            });
        }

        private void UndoLastPathVertex(RoiContourPath path, string logPrefix)
        {
            if (path.VertexCount <= 0)
                return;
            CapturePathVertexForRedo(path);
            path.RemoveLastVertex();
            AppendLog($"{logPrefix}: 剩余 {path.VertexCount} 个顶点");
        }
    }
}
