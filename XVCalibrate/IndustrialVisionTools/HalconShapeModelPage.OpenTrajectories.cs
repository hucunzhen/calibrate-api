using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace CalibOperatorCLI_Example
{
    public partial class HalconShapeModelPage
    {
        private readonly List<OpenTrajectoryEntry> _openTrajectoryEntries = new();

        /// <summary>正在编辑的已完成轨迹索引；null 表示绘制新条。</summary>
        private int? _editingOpenTrajectoryIndex;

        private void EnsureOpenTrajectoryEntryNames()
        {
            for (int i = 0; i < _openTrajectoryEntries.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(_openTrajectoryEntries[i].Name))
                    _openTrajectoryEntries[i].Name = $"轨迹 {i + 1}";
            }
        }

        private static string DefaultTrajectoryName(int index) => $"轨迹 {index + 1}";

        private void RefreshOpenTrajectoryListUi()
        {
            if (LstOpenTrajectories == null)
                return;

            EnsureOpenTrajectoryEntryNames();
            var labels = new List<string>();
            for (int i = 0; i < _openTrajectoryEntries.Count; i++)
            {
                OpenTrajectoryEntry e = _openTrajectoryEntries[i];
                string mark = _editingOpenTrajectoryIndex == i ? " [编辑中]" : "";
                labels.Add($"{i + 1}. {e.Name} ({e.Path.VertexCount} 点){mark}");
            }

            int keep = LstOpenTrajectories.SelectedIndex;
            LstOpenTrajectories.ItemsSource = labels;
            if (labels.Count == 0)
            {
                LstOpenTrajectories.SelectedIndex = -1;
                if (TxtOpenTrajectoryName != null)
                    TxtOpenTrajectoryName.Text = "";
                return;
            }

            int pick = keep >= 0 && keep < labels.Count ? keep : labels.Count - 1;
            LstOpenTrajectories.SelectedIndex = pick;
            SyncOpenTrajectoryNameTextFromSelection();
        }

        private void SyncOpenTrajectoryNameTextFromSelection()
        {
            if (TxtOpenTrajectoryName == null || LstOpenTrajectories == null)
                return;

            int idx = LstOpenTrajectories.SelectedIndex;
            if (idx < 0 || idx >= _openTrajectoryEntries.Count)
            {
                TxtOpenTrajectoryName.Text = "";
                return;
            }

            TxtOpenTrajectoryName.Text = _openTrajectoryEntries[idx].Name;
        }

        private void BtnEditOpenTrajectory_Click(object sender, RoutedEventArgs e)
        {
            if (LstOpenTrajectories?.SelectedIndex is not int idx || idx < 0 || idx >= _openTrajectoryEntries.Count)
            {
                MessageBox.Show("请先在列表中选择要编辑的轨迹。", "编辑轨迹", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_roiPath.VertexCount > 0 || _arcDraftEnd != null)
            {
                if (MessageBox.Show("当前有未完成的草稿，继续编辑将放弃草稿。是否继续？", "编辑轨迹",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                _roiPath.Clear();
                CancelArcDraft();
            }

            RoiContourPath? cloned = HalconGeometryPathEditor.ClonePath(_openTrajectoryEntries[idx].Path);
            if (cloned == null)
                return;

            _roiPath.Clear();
            CopyContourPathContents(cloned, _roiPath);
            _roiPath.IsClosed = false;
            _editingOpenTrajectoryIndex = idx;
            _openTrajectoryPreferPick = true;

            InvalidatePolygonFlatCache();
            RefreshAllOpenTrajectoryVisuals();
            RefreshOpenTrajectoryListUi();
            UpdateRoiEditHint();
            AppendLog($"正在编辑: {_openTrajectoryEntries[idx].Name}（单击优先取点；修改后点「完成本条」覆盖原轨迹）");
        }

        private void BtnDeleteOpenTrajectory_Click(object sender, RoutedEventArgs e)
        {
            if (LstOpenTrajectories?.SelectedIndex is not int idx || idx < 0 || idx >= _openTrajectoryEntries.Count)
            {
                MessageBox.Show("请先在列表中选择要删除的轨迹。", "删除轨迹", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string name = _openTrajectoryEntries[idx].Name;
            if (MessageBox.Show($"确定删除「{name}」？", "删除轨迹", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes)
                return;

            _openTrajectoryEntries.RemoveAt(idx);
            if (_editingOpenTrajectoryIndex == idx)
            {
                _editingOpenTrajectoryIndex = null;
                _roiPath.Clear();
                CancelArcDraft();
            }
            else if (_editingOpenTrajectoryIndex is int eidx && eidx > idx)
                _editingOpenTrajectoryIndex = eidx - 1;

            _hasOpenTrajectoriesRoi = _openTrajectoryEntries.Count > 0;
            _hasRoi = _hasOpenTrajectoriesRoi || _roiPath.VertexCount >= 2;

            RefreshAllOpenTrajectoryVisuals();
            RefreshOpenTrajectoryListUi();
            PersistSessionChange();
            AppendLog($"已删除轨迹: {name}，剩余 {_openTrajectoryEntries.Count} 条");
        }

        private void BtnApplyOpenTrajectoryName_Click(object sender, RoutedEventArgs e)
        {
            if (LstOpenTrajectories?.SelectedIndex is not int idx || idx < 0 || idx >= _openTrajectoryEntries.Count)
                return;

            string name = TxtOpenTrajectoryName?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("名称不能为空。", "重命名", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _openTrajectoryEntries[idx].Name = name;
            RefreshOpenTrajectoryListUi();
            PersistSessionChange();
            AppendLog($"轨迹已重命名为: {name}");
        }

        private void LstOpenTrajectories_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
                return;
            SyncOpenTrajectoryNameTextFromSelection();
        }

        private bool TryGetEditableOpenTrajectory(out int listIndex, out RoiContourPath path, out string name)
        {
            listIndex = -1;
            path = _roiPath;
            name = "";
            if (!IsOpenTrajectoriesModeActive())
                return false;

            int idx = LstOpenTrajectories?.SelectedIndex ?? -1;
            if (idx >= 0 && idx < _openTrajectoryEntries.Count)
            {
                listIndex = idx;
                name = _openTrajectoryEntries[idx].Name;
                path = _editingOpenTrajectoryIndex == idx
                    ? _roiPath
                    : _openTrajectoryEntries[idx].Path;
                return path.VertexCount >= 1;
            }

            if (_roiPath.VertexCount >= 1)
            {
                name = "当前草稿";
                path = _roiPath;
                return true;
            }

            return false;
        }

        private string UniqueTrajectoryCopyName(string sourceName)
        {
            string root = string.IsNullOrWhiteSpace(sourceName) ? "轨迹" : sourceName.Trim();
            string candidate = root + " 副本";
            int n = 2;
            while (_openTrajectoryEntries.Exists(e =>
                       string.Equals(e.Name, candidate, System.StringComparison.Ordinal)))
                candidate = $"{root} 副本{n++}";
            return candidate;
        }

        private void BtnCopyOpenTrajectory_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetEditableOpenTrajectory(out _, out RoiContourPath src, out string srcName)
                || src.VertexCount < 2)
            {
                MessageBox.Show("请先选择一条至少有两个点的轨迹。", "复制轨迹",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (IsBusyWithPointerEdit())
                return;

            OpenTrajSceneSnapshot before = CaptureOpenTrajSceneSnapshot();
            RoiContourPath clone = CloneContourPath(src);
            double d = RoiUsesMillimeters() ? 2.0 : ImageLengthToRoi(12);
            TranslateContourPath(clone, d, d);
            var entry = new OpenTrajectoryEntry
            {
                Name = UniqueTrajectoryCopyName(srcName == "当前草稿" ? DefaultTrajectoryName(_openTrajectoryEntries.Count) : srcName),
                Path = clone
            };
            _openTrajectoryEntries.Add(entry);
            _hasOpenTrajectoriesRoi = true;
            _hasRoi = true;
            CommitOpenTrajSceneEdit(before, $"复制轨迹 {entry.Name}");
            RefreshAllOpenTrajectoryVisuals();
            RefreshOpenTrajectoryListUi();
            if (LstOpenTrajectories != null)
                LstOpenTrajectories.SelectedIndex = _openTrajectoryEntries.Count - 1;
            PersistSessionChange();
            AppendLog($"已复制轨迹: {entry.Name}");
        }

        private void BtnMirrorOpenTrajectoryX_Click(object sender, RoutedEventArgs e) =>
            MirrorSelectedOpenTrajectory(flipX: true, flipY: false);

        private void BtnMirrorOpenTrajectoryY_Click(object sender, RoutedEventArgs e) =>
            MirrorSelectedOpenTrajectory(flipX: false, flipY: true);

        private void MirrorSelectedOpenTrajectory(bool flipX, bool flipY)
        {
            if (!TryGetEditableOpenTrajectory(out _, out RoiContourPath path, out string name)
                || path.VertexCount < 1)
            {
                MessageBox.Show("请先选择要镜像的轨迹。", "镜像轨迹",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (IsBusyWithPointerEdit())
                return;

            OpenTrajSceneSnapshot before = CaptureOpenTrajSceneSnapshot();
            Point origin = RoiContourPath.Centroid(path);
            RoiContourPath.Mirror(path, origin, flipX, flipY);
            string axis = flipX ? "左右" : "上下";
            CommitOpenTrajSceneEdit(before, $"镜像{axis} {name}");
            RefreshAllOpenTrajectoryVisuals();
            RefreshOpenTrajectoryListUi();
            PersistSessionChange();
            AppendLog($"已镜像{axis}: {name}");
        }

        private void BtnOffsetOpenTrajectory_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetEditableOpenTrajectory(out _, out RoiContourPath path, out string name)
                || path.VertexCount < 2)
            {
                MessageBox.Show("请先选择一条至少有两个点的轨迹。", "偏移轨迹",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (IsBusyWithPointerEdit())
                return;

            var dlg = new RoiOffsetDialog { Owner = Window.GetWindow(this) };
            dlg.Initialize(_lastRoiOffsetDistance, RoiUsesMillimeters());
            if (dlg.ShowDialog() != true)
                return;

            double signed = dlg.OffsetLeft ? dlg.Distance : -dlg.Distance;
            RoiContourPath trial = CloneContourPath(path);
            if (!RoiContourPath.TryOffset(trial, signed, out string error))
            {
                MessageBox.Show(error, "偏移轨迹", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            OpenTrajSceneSnapshot before = CaptureOpenTrajSceneSnapshot();
            CopyContourPathContents(trial, path);
            _lastRoiOffsetDistance = dlg.Distance;
            string side = dlg.OffsetLeft ? "左侧" : "右侧";
            CommitOpenTrajSceneEdit(before, $"偏移{side} {dlg.Distance:G6} {name}");
            RefreshAllOpenTrajectoryVisuals();
            RefreshOpenTrajectoryListUi();
            PersistSessionChange();
            AppendLog($"已偏移{side} {dlg.Distance:G6}: {name}");
        }
    }
}
