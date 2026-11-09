using STranslate.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace STranslate.Controls;

public sealed class TextPreviewDiffBlock : TextBlock
{
    public static readonly DependencyProperty SpansProperty = DependencyProperty.Register(
        nameof(Spans), typeof(IReadOnlyList<TextPreviewSpan>), typeof(TextPreviewDiffBlock),
        new PropertyMetadata(null, OnSpansChanged));

    public IReadOnlyList<TextPreviewSpan>? Spans
    {
        get => (IReadOnlyList<TextPreviewSpan>?)GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    private static void OnSpansChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var block = (TextPreviewDiffBlock)sender;
        block.Inlines.Clear();
        foreach (var span in block.Spans ?? [])
        {
            var run = new Run(span.Text.Replace("\r", "␍")
                .Replace("\n", "↵\n").Replace("\t", "⇥").Replace(" ", "·"));
            if (span.IsChanged)
            {
                run.SetResourceReference(TextElement.BackgroundProperty, SystemColors.HighlightBrushKey);
                run.SetResourceReference(TextElement.ForegroundProperty, SystemColors.HighlightTextBrushKey);
                run.TextDecorations = System.Windows.TextDecorations.Underline;
            }
            block.Inlines.Add(run);
        }
    }
}
