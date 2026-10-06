using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
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
                if (FindControl(root, name) is not FrameworkElement fe)
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
                        d[name] = ReadComboTag(cmb) ?? cmb.SelectedIndex.ToString(CultureInfo.InvariantCulture);
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
                if (string.IsNullOrWhiteSpace(name) || !TryGet(values, name, out string? raw))
                    continue;
                if (FindControl(root, name) is not FrameworkElement fe)
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

        public static void ApplyCombo(ComboBox? cmb, string? raw)
        {
            if (cmb == null || string.IsNullOrWhiteSpace(raw))
                return;
            string want = raw.Trim();

            if (string.Equals(cmb.SelectedValuePath, "Tag", StringComparison.Ordinal))
            {
                cmb.SelectedValue = want;
                if (string.Equals(ReadComboTag(cmb), want, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            for (int i = 0; i < cmb.Items.Count; i++)
            {
                if (!string.Equals(ReadItemTag(cmb.Items[i]), want, StringComparison.OrdinalIgnoreCase))
                    continue;
                cmb.SelectedIndex = i;
                cmb.SelectedItem = cmb.Items[i];
                return;
            }

            if (int.TryParse(want, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
                && idx >= 0
                && idx < cmb.Items.Count)
                cmb.SelectedIndex = idx;
        }

        static FrameworkElement? FindControl(FrameworkElement root, string name)
        {
            if (root.FindName(name) is FrameworkElement named)
                return named;

            FieldInfo? field = root.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(root) as FrameworkElement;
        }

        static bool TryGet(IReadOnlyDictionary<string, string> values, string name, out string? raw)
        {
            if (values.TryGetValue(name, out raw))
                return true;
            foreach (var kv in values)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    raw = kv.Value;
                    return true;
                }
            }

            raw = null;
            return false;
        }

        static string? ReadComboTag(ComboBox cmb)
        {
            string? fromValue = cmb.SelectedValue?.ToString();
            if (!string.IsNullOrWhiteSpace(fromValue) && !int.TryParse(fromValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return fromValue.Trim();
            return ReadItemTag(cmb.SelectedItem);
        }

        static string? ReadItemTag(object? item)
        {
            if (item is ComboBoxItem cbi)
            {
                string? tag = cbi.Tag?.ToString();
                if (!string.IsNullOrWhiteSpace(tag))
                    return tag.Trim();
            }

            return null;
        }
    }
}
