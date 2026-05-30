using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CalibOperatorCLI_Example
{
    /// <summary>非空字符串 → Visible，否则 Collapsed。</summary>
    public sealed class NonEmptyStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>数值减去 Offset（用于工具箱描述 MaxWidth ≈ 视口宽 - 边距）。</summary>
    public sealed class SubtractDoubleConverter : IValueConverter
    {
        public double Offset { get; set; } = 32;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double w = value switch
            {
                double d => d,
                float f => f,
                int i => i,
                _ => 200
            };
            return Math.Max(80, w - Offset);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
