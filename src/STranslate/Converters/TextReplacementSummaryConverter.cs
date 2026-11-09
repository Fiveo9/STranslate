using System.Globalization;
using System.Windows.Data;

namespace STranslate.Converters;

public sealed class TextReplacementSummaryConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => $"{Visible(values.ElementAtOrDefault(0) as string)} → {Visible(values.ElementAtOrDefault(1) as string)}";

    private static string Visible(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "∅";
        var abbreviated = text.Length > 120;
        if (abbreviated)
            text = text[..(char.IsHighSurrogate(text[119]) ? 119 : 120)];
        return text.Replace("\r", "␍").Replace("\n", "↵").Replace("\t", "⇥").Replace(" ", "·")
            + (abbreviated ? "…" : string.Empty);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class TextReplacementOrderConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length >= 2 && values[1] is System.Collections.IList rules
            ? (rules.IndexOf(values[0]) + 1).ToString(culture) : string.Empty;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
