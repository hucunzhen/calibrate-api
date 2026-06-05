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

            InvalidatePolygonFlatCache();
            UpdatePolygonPreview();
            RefreshOpenTrajectoryListUi();
            AppendLog($"正在编辑: {_openTrajectoryEntries[idx].Name}（修改后点「完成本条」覆盖原轨迹）");
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

            RebuildOpenTrajectoriesVisual();
            RefreshOpenTrajectoryListUi();
            UpdatePolygonPreview();
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
    }
}
