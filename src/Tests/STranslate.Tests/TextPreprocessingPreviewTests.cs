using STranslate.Core;

namespace STranslate.Tests;

public class TextPreprocessingPreviewTests
{
    [Fact]
    public void DiffMarksSeparatedEditsWithoutHighlightingUnchangedWords()
    {
        var diff = TextPreprocessingPreviewDiff.Create("A middle B", "X middle Y");
        Assert.Equal("AB", string.Concat(diff.Before.Where(s => s.IsChanged).Select(s => s.Text)));
        Assert.Equal("XY", string.Concat(diff.After.Where(s => s.IsChanged).Select(s => s.Text)));
        Assert.Equal(" middle ", string.Concat(diff.Before.Where(s => !s.IsChanged).Select(s => s.Text)));
        Assert.Equal("X middle Y", string.Concat(diff.After.Select(s => s.Text)));
    }

    [Fact]
    public void DiffKeepsDeletedWhitespaceAndUnicodeCharactersIntact()
    {
        var diff = TextPreprocessingPreviewDiff.Create("😀 A\nB", "😁AB");
        Assert.Equal("😀 \n", string.Concat(diff.Before.Where(s => s.IsChanged).Select(s => s.Text)));
        Assert.Equal("😁", string.Concat(diff.After.Where(s => s.IsChanged).Select(s => s.Text)));
        Assert.Equal("AB", string.Concat(diff.After.Where(s => !s.IsChanged).Select(s => s.Text)));
    }

    [Fact]
    public void LongDiffIsBoundedAndClearlyMarkedAsAbbreviated()
    {
        var diff = TextPreprocessingPreviewDiff.Create(new string('A', 10000), new string('B', 10000));
        Assert.True(diff.IsAbbreviated);
        Assert.InRange(diff.Before.Sum(s => s.Text.Length), 1, 2000);
        Assert.InRange(diff.After.Sum(s => s.Text.Length), 1, 2000);
        Assert.All(diff.Before, s => Assert.True(s.IsChanged));
    }

    [Fact]
    public async Task PreviewStepsFollowTheSameChainAsTheFinalRequest()
    {
        var settings = new TextPreprocessingSettings { IsEnabled = true, Rules =
            [new() { Pattern = "A", Replacement = "B" },
             new() { Pattern = "B", Replacement = "wrong", IsEnabled = false },
             new() { Pattern = "B", Replacement = "C" }] };
        var service = new TextPreprocessingService();
        var preview = await service.PrepareAsync("A", settings, includePreview: true);
        Assert.Equal("C", preview.RequestText);
        Assert.Equal(["A", "B"], preview.MatchSummaries.Select(s => s.InputPreview));
        Assert.Equal(["B", "C"], preview.MatchSummaries.Select(s => s.OutputPreview));
        var request = await service.PrepareAsync("A", settings);
        Assert.All(request.MatchSummaries, s => Assert.Null(s.InputPreview));
        Assert.All(request.MatchSummaries, s => Assert.Null(s.OutputPreview));
    }

    [Fact]
    public async Task StepTextIsBoundedWithoutTruncatingTheActualRequest()
    {
        var settings = new TextPreprocessingSettings { IsEnabled = true, Rules = [new() { Pattern = "A", Replacement = "B" }] };
        var preview = await new TextPreprocessingService().PrepareAsync(new string('A', 10000), settings, includePreview: true);
        Assert.Equal(new string('B', 10000), preview.RequestText);
        var step = Assert.Single(preview.MatchSummaries);
        Assert.True(step.IsPreviewAbbreviated);
        Assert.Equal(2000, step.InputPreview!.Length);
        Assert.Equal(2000, step.OutputPreview!.Length);
    }
}
