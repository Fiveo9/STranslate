using System.Text;

namespace STranslate.Core;

public sealed record TextPreviewSpan(string Text, bool IsChanged);

public sealed record TextPreviewComparison(
    IReadOnlyList<TextPreviewSpan> Before,
    IReadOnlyList<TextPreviewSpan> After,
    bool IsAbbreviated);

internal static class TextPreprocessingPreviewDiff
{
    internal const int MaxPreviewLength = 2000;

    internal static string Abbreviate(string text)
    {
        if (text.Length <= MaxPreviewLength) return text;
        var length = char.IsHighSurrogate(text[MaxPreviewLength - 1]) ? MaxPreviewLength - 1 : MaxPreviewLength;
        return text[..length];
    }

    internal static TextPreviewComparison Create(string before, string after, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var abbreviated = before.Length > MaxPreviewLength || after.Length > MaxPreviewLength;
        var left = Abbreviate(before).EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        var right = Abbreviate(after).EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        var prefix = 0;
        while (prefix < left.Length && prefix < right.Length && left[prefix] == right[prefix]) prefix++;
        var suffix = 0;
        while (suffix < left.Length - prefix && suffix < right.Length - prefix &&
               left[^(suffix + 1)] == right[^(suffix + 1)]) suffix++;
        var leftEnd = left.Length - suffix;
        var rightEnd = right.Length - suffix;
        var leftSpans = new List<TextPreviewSpan>();
        var rightSpans = new List<TextPreviewSpan>();
        Append(leftSpans, string.Concat(left.Take(prefix)), false);
        Append(rightSpans, string.Concat(right.Take(prefix)), false);
        var rows = leftEnd - prefix;
        var columns = rightEnd - prefix;
        if ((long)rows * columns > 250_000)
        {
            abbreviated = true;
            Append(leftSpans, string.Concat(left.Skip(prefix).Take(rows)), true);
            Append(rightSpans, string.Concat(right.Skip(prefix).Take(columns)), true);
        }
        else
        {
            var lengths = new int[rows + 1, columns + 1];
            for (var i = rows - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var j = columns - 1; j >= 0; j--)
                    lengths[i, j] = left[prefix + i] == right[prefix + j]
                        ? lengths[i + 1, j + 1] + 1
                        : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
            var x = 0;
            var y = 0;
            while (x < rows || y < columns)
            {
                if (x < rows && y < columns && left[prefix + x] == right[prefix + y])
                {
                    Append(leftSpans, left[prefix + x++], false);
                    Append(rightSpans, right[prefix + y++], false);
                }
                else if (x < rows && (y == columns || lengths[x + 1, y] >= lengths[x, y + 1]))
                    Append(leftSpans, left[prefix + x++], true);
                else
                    Append(rightSpans, right[prefix + y++], true);
            }
        }
        Append(leftSpans, string.Concat(left.Skip(leftEnd)), false);
        Append(rightSpans, string.Concat(right.Skip(rightEnd)), false);
        return new(leftSpans, rightSpans, abbreviated);
    }

    private static void Append(List<TextPreviewSpan> spans, string text, bool changed)
    {
        if (text.Length == 0) return;
        if (spans.Count > 0 && spans[^1].IsChanged == changed)
            spans[^1] = spans[^1] with { Text = spans[^1].Text + text };
        else
            spans.Add(new(text, changed));
    }
}
