using System;
using System.Windows;

namespace CalibOperatorCLI_Example
{
    public partial class RecipeNameInputDialog : Window
    {
        private readonly string? _renameFromName;

        public RecipeNameInputDialog(string prompt, string defaultName, string? renameFromName = null)
        {
            _renameFromName = renameFromName;
            InitializeComponent();
            TxtPrompt.Text = prompt;
            TxtName.Text = defaultName;
            TxtName.SelectAll();
            Loaded += (_, _) => TxtName.Focus();
        }

        public string RecipeName => TxtName.Text.Trim();

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!FlowRecipeCatalog.TryValidateRecipeName(RecipeName, out string error))
            {
                TxtError.Text = error;
                TxtError.Visibility = Visibility.Visible;
                TxtName.Focus();
                TxtName.SelectAll();
                return;
            }

            if (FlowRecipeCatalog.RecipeNameExists(RecipeName)
                && !string.Equals(RecipeName, _renameFromName, StringComparison.OrdinalIgnoreCase))
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
