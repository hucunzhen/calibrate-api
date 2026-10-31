using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using IoPath = System.IO.Path;

namespace CalibOperatorCLI_Example
{
    public partial class HalconShapeModelPage
    {
        private bool _suppressParamRecipeCombo;
        private string _lastParamRecipeId = "";

        private void RefreshParamRecipeCombo(string? selectId, bool apply)
        {
            if (CmbParamRecipe == null)
                return;

            _suppressParamRecipeCombo = true;
            try
            {
                CmbParamRecipe.Items.Clear();
                int selectIndex = -1;
                int i = 0;
                foreach (HalconShapeModelParamRecipe recipe in HalconShapeModelParamRecipeStore.ListAll())
                {
                    CmbParamRecipe.Items.Add(new ShapeParamRecipeListItem { Recipe = recipe });
                    if (!string.IsNullOrWhiteSpace(selectId)
                        && string.Equals(recipe.Id, selectId, StringComparison.OrdinalIgnoreCase))
                        selectIndex = i;
                    i++;
                }

                if (selectIndex >= 0)
                    CmbParamRecipe.SelectedIndex = selectIndex;
                else if (CmbParamRecipe.Items.Count > 0)
                    CmbParamRecipe.SelectedIndex = 0;

                _lastParamRecipeId = SelectedParamRecipe?.Id ?? selectId ?? "";
                UpdateParamRecipeHintAndButtons();
            }
            finally
            {
                _suppressParamRecipeCombo = false;
            }

            if (apply)
                ApplySelectedParamRecipe(log: true);
        }

        private HalconShapeModelParamRecipe? SelectedParamRecipe =>
            (CmbParamRecipe?.SelectedItem as ShapeParamRecipeListItem)?.Recipe;

        private void UpdateParamRecipeHintAndButtons()
        {
            HalconShapeModelParamRecipe? r = SelectedParamRecipe;
            TxtParamRecipeHint.Text = r == null
                ? "尚未选择配方。"
                : (string.IsNullOrWhiteSpace(r.Description) ? r.Name : r.Description);
            if (BtnOverwriteParamRecipe != null)
                BtnOverwriteParamRecipe.IsEnabled = r != null;
            if (BtnDeleteParamRecipe != null)
            {
                BtnDeleteParamRecipe.IsEnabled = r != null;
                BtnDeleteParamRecipe.Content = r is { IsBuiltIn: true } ? "恢复代码默认" : "删除";
                BtnDeleteParamRecipe.ToolTip = r is { IsBuiltIn: true }
                    ? "把该内置 JSON 恢复为程序里的代码默认，改动可提交 git。"
                    : "删除所选自定义配方。";
            }
        }

        private void CmbParamRecipe_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateParamRecipeHintAndButtons();
            if (_suppressParamRecipeCombo || !_uiReady)
                return;
            ApplySelectedParamRecipe(log: true);
        }

        private void BtnApplyParamRecipe_Click(object sender, RoutedEventArgs e) =>
            ApplySelectedParamRecipe(log: true);

        private void ApplySelectedParamRecipe(bool log)
        {
            HalconShapeModelParamRecipe? recipe = SelectedParamRecipe;
            if (recipe == null)
                return;

            _suppressSessionPersist = true;
            try
            {
                PageUiBinder.Apply(this, recipe.Controls, HalconShapeModelUiSettings.PersistControlNameList);
                if (recipe.Controls.TryGetValue("CmbTemplateSource", out string? src))
                    PageUiBinder.ApplyCombo(CmbTemplateSource, src);
                if (recipe.Controls.TryGetValue("CmbModelKind", out string? kind))
                    PageUiBinder.ApplyCombo(CmbModelKind, kind);
                UpdateCreatePanelsVisibility();
            }
            finally
            {
                _suppressSessionPersist = false;
            }

            _lastParamRecipeId = recipe.Id;
            PersistLastParamRecipeId(recipe.Id);
            ScheduleSessionPersist();
            if (log)
                AppendLog($"[参数配方] 已应用「{recipe.Name}」。");
            TryLoadRoiForSelectedParamRecipe(log);
        }

        /// <summary>
        /// 配方目录 <c>roi/outline|coarse|fine.xvroi.json</c>（自定义配方用名称）。
        /// 仅当文件已保存过才加载；没有对应文件则保持当前 ROI。
        /// </summary>
        private void TryLoadRoiForSelectedParamRecipe(bool log)
        {
            if (_insideSessionRestore)
                return;

            HalconShapeModelParamRecipe? recipe = SelectedParamRecipe;
            if (recipe == null)
                return;

            if (!TryGetParamRecipeRoiPath(recipe, out string path, out string relative))
            {
                if (log)
                    AppendLog("[参数配方] 未选择流程配方目录，无法匹配 ROI。");
                return;
            }

            if (!File.Exists(path))
            {
                if (log)
                    AppendLog($"[参数配方] 未找到已保存 ROI（{relative}），保持当前 ROI。");
                return;
            }

            try
            {
                RoiSnapshotDocument doc = RoiSnapshotFileIo.Load(path);
                ApplyRoiSnapshotDocument(doc, path);
            }
            catch (Exception ex)
            {
                AppendLog($"[参数配方] 加载 ROI 失败（{relative}）: {ex.Message}");
            }
        }

        private bool TryGetParamRecipeRoiPath(
            HalconShapeModelParamRecipe recipe,
            out string fullPath,
            out string relativePath)
        {
            fullPath = "";
            relativePath = "";
            string? recipeDir = FlowRecipeCatalog.TryGetSelectedRecipeDirectory();
            if (string.IsNullOrWhiteSpace(recipeDir))
                return false;

            string stem = GetParamRecipeRoiFileStem(recipe);
            relativePath = IoPath.Combine("roi", stem + ".xvroi.json");
            fullPath = IoPath.Combine(recipeDir, relativePath);
            return true;
        }

        private static string GetParamRecipeRoiFileStem(HalconShapeModelParamRecipe recipe)
        {
            string id = recipe.Id?.Trim() ?? "";
            string name = recipe.Name?.Trim() ?? "";
            if (IsOutlineParamRecipe(id, name))
                return "outline";
            if (IsCoarseParamRecipe(id, name))
                return "coarse";
            if (IsFineParamRecipe(id, name))
                return "fine";

            string raw = name;
            if (string.IsNullOrWhiteSpace(raw))
                raw = id.StartsWith("user:", StringComparison.OrdinalIgnoreCase) ? id[5..] : id;
            return SanitizeRoiFileStem(raw);
        }

        private static string SanitizeRoiFileStem(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "recipe";

            var invalid = IoPath.GetInvalidFileNameChars();
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw.Trim())
                sb.Append(Array.IndexOf(invalid, c) >= 0 || c is ':' or '/' or '\\' ? '_' : c);

            string s = sb.ToString().Trim('_');
            return string.IsNullOrWhiteSpace(s) ? "recipe" : s;
        }

        private void BtnSaveAsParamRecipe_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new RecipeNameInputDialog(
                "自定义参数配方名称（一类任务一套参数）",
                SelectedParamRecipe is { IsBuiltIn: false } r ? r.Name : "",
                renameFromName: null,
                checkFlowCatalog: false)
            {
                Owner = Window.GetWindow(this),
                Title = "另存参数配方"
            };
            if (dlg.ShowDialog() != true)
                return;

            SaveCurrentAsUserRecipe(dlg.RecipeName, overwrite: false);
        }

        private void BtnOverwriteParamRecipe_Click(object sender, RoutedEventArgs e)
        {
            HalconShapeModelParamRecipe? recipe = SelectedParamRecipe;
            if (recipe == null)
                return;

            if (recipe.IsBuiltIn)
            {
                string? gitDir = HalconShapeModelParamRecipeStore.TryFindGitRecipesDirectory();
                string gitHint = string.IsNullOrWhiteSpace(gitDir)
                    ? "未找到工程目录，无法写入可提交 git 的文件。请从源码编译运行。"
                    : gitDir;
                MessageBoxResult confirm = MessageBox.Show(
                    Window.GetWindow(this),
                    $"把当前参数写进仓库内置配方「{recipe.Name}」？\n保存到：\n{gitHint}\n改完后把该 JSON 提交 git。",
                    "更新内置配方",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                    return;

                var stored = new HalconShapeModelParamRecipe
                {
                    Id = recipe.Id,
                    Name = recipe.Name,
                    Description = recipe.Description,
                    Controls = PageUiBinder.Capture(this, HalconShapeModelUiSettings.PersistControlNameList)
                };
                if (!HalconShapeModelParamRecipeStore.TrySaveBuiltinDefault(stored, out string savedPath, out string error))
                {
                    MessageBox.Show(Window.GetWindow(this), error, "参数配方", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AppendLog($"[参数配方] 已写入内置「{recipe.Name}」：{savedPath}（可 git 提交）");
                RefreshParamRecipeCombo(recipe.Id, apply: false);
                return;
            }

            SaveCurrentAsUserRecipe(recipe.Name, overwrite: true);
        }

        private void SaveCurrentAsUserRecipe(string name, bool overwrite)
        {
            var recipe = new HalconShapeModelParamRecipe
            {
                Name = name.Trim(),
                Description = SelectedParamRecipe?.Description ?? "",
                Controls = PageUiBinder.Capture(this, HalconShapeModelUiSettings.PersistControlNameList)
            };

            if (!overwrite && HalconShapeModelParamRecipeStore.UserNameExists(recipe.Name))
            {
                MessageBox.Show(Window.GetWindow(this), $"配方「{recipe.Name}」已存在。请换名，或选中后点「覆盖保存」。",
                    "参数配方", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!HalconShapeModelParamRecipeStore.TrySaveUserRecipe(recipe, out string error))
            {
                MessageBox.Show(Window.GetWindow(this), error, "参数配方", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AppendLog($"[参数配方] 已保存「{recipe.Name}」。");
            RefreshParamRecipeCombo(recipe.Id, apply: false);
            _lastParamRecipeId = recipe.Id;
            PersistLastParamRecipeId(recipe.Id);
        }

        private void BtnDeleteParamRecipe_Click(object sender, RoutedEventArgs e)
        {
            HalconShapeModelParamRecipe? recipe = SelectedParamRecipe;
            if (recipe == null)
                return;

            if (recipe.IsBuiltIn)
            {
                MessageBoxResult reset = MessageBox.Show(
                    Window.GetWindow(this),
                    $"把内置配方「{recipe.Name}」的 JSON 恢复为代码里的默认值？恢复后的文件同样可以 git 提交。",
                    "恢复代码默认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (reset != MessageBoxResult.Yes)
                    return;

                if (!HalconShapeModelParamRecipeStore.TryResetBuiltin(recipe.Id, out string savedPath, out string resetError))
                {
                    MessageBox.Show(Window.GetWindow(this), resetError, "参数配方",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AppendLog($"[参数配方] 已恢复「{recipe.Name}」代码默认：{savedPath}");
                RefreshParamRecipeCombo(recipe.Id, apply: true);
                return;
            }

            MessageBoxResult confirm = MessageBox.Show(
                Window.GetWindow(this),
                $"删除自定义配方「{recipe.Name}」？",
                "参数配方",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
                return;

            if (!HalconShapeModelParamRecipeStore.TryDeleteUserRecipe(recipe.Id, out string error))
            {
                MessageBox.Show(Window.GetWindow(this), error, "参数配方", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AppendLog($"[参数配方] 已删除「{recipe.Name}」。");
            RefreshParamRecipeCombo(HalconShapeModelParamRecipeStore.BuiltInRecipes[0].Id, apply: false);
        }

        private void PersistLastParamRecipeId(string id)
        {
            try
            {
                HalconShapeModelUiSettings s = HalconShapeModelUiSettings.Load();
                s.LastParamRecipeId = id ?? "";
                s.Save();
            }
            catch
            {
            }
        }
    }
}
