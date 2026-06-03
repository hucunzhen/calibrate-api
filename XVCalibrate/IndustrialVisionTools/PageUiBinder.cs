using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CalibOperatorCLI_Example
{
    internal static class PageUiBinder
    {
        public static Dictionary<string, string> Capture(FrameworkElement root, IEnumerable<string> names)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (root.FindName(name) is not FrameworkElement fe)
                    continue;
                switch (fe)
                {
                    case TextBox tb:
                        d[name] = tb.Text ?? "";
                        break;
                    case CheckBox cb:
                        d[name] = cb.IsChecked == true ? "true" : "false";
                        break;
                    case ComboBox cmb:
                        if (cmb.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
                            d[name] = tag.Trim();
                        else
                            d[name] = cmb.SelectedIndex.ToString(CultureInfo.InvariantCulture);
                        break;
                    case RadioButton rb:
                        d[name] = rb.IsChecked == true ? "true" : "false";
                        break;
                }
            }

            return d;
        }

        public static void Apply(FrameworkElement root, IReadOnlyDictionary<string, string>? values, IEnumerable<string> names, bool suppressChange = false)
        {
            if (values == null || values.Count == 0)
                return;

            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name) || !values.TryGetValue(name, out string? raw))
                    continue;
                if (root.FindName(name) is not FrameworkElement fe)
                    continue;

                switch (fe)
                {
                    case TextBox tb:
                        tb.Text = raw ?? "";
                        break;
                    case CheckBox cb:
                        cb.IsChecked = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                            || raw == "1";
                        break;
                    case ComboBox cmb:
                        ApplyCombo(cmb, raw);
                        break;
                    case RadioButton rb:
                        rb.IsChecked = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                            || raw == "1";
                        break;
                }
            }
        }

        private static void ApplyCombo(ComboBox cmb, string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return;
            foreach (var item in cmb.Items)
            {
                if (item is ComboBoxItem cbi
                    && cbi.Tag is string tag
                    && string.Equals(tag, raw, StringComparison.OrdinalIgnoreCase))
                {
                    cmb.SelectedItem = cbi;
                    return;
                }
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
                && idx >= 0
                && idx < cmb.Items.Count)
                cmb.SelectedIndex = idx;
        }
    }
}
