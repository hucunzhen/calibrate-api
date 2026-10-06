using System;
using System.Windows;

namespace CalibOperatorCLI_Example
{
    public partial class RecipeNameInputDialog : Window
    {
        private readonly string? _renameFromName;
        private readonly bool _checkFlowCatalog;

        public RecipeNameInputDialog(string prompt, string defaultName, string? renameFromName = null, bool checkFlowCatalog = true)
        {
            _renameFromName = renameFromName;
            _checkFlowCatalog = checkFlowCatalog;
            InitializeComponent();
            TxtPrompt.Text = prompt;
            TxtName.Text = defaultName;
            TxtName.SelectAll();
            Loaded += (_, _) => TxtName.Focus();
        }

        public string RecipeName => TxtName.Text.Trim();

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string error;
            bool valid = _checkFlowCatalog
                ? FlowRecipeCatalog.TryValidateRecipeName(RecipeName, out error)
                : HalconShapeModelParamRecipeStore.TryValidateUserName(RecipeName, out error);
            if (!valid)
            {
                TxtError.Text = error;
                TxtError.Visibility = Visibility.Visible;
                TxtName.Focus();
                TxtName.SelectAll();
                return;
            }

            bool exists = _checkFlowCatalog
                ? FlowRecipeCatalog.RecipeNameExists(RecipeName)
                : HalconShapeModelParamRecipeStore.UserNameExists(RecipeName);
            if (exists && !string.Equals(RecipeName, _renameFromName, StringComparison.OrdinalIgnoreCase))
            {
                TxtError.Text = "该配方名称已存在。";
                TxtError.Visibility = Visibility.Visible;
                TxtName.Focus();
                TxtName.SelectAll();
                return;
            }

            DialogResult = true;
        }
    }
}
