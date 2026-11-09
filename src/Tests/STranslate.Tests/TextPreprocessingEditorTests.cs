using STranslate.Core;
using STranslate.ViewModels;

namespace STranslate.Tests;

public class TextPreprocessingEditorTests
{
    [Fact]
    public async Task DraftChangesDoNotAffectSavedRulesAndDisposeCancelsEditing()
    {
        var settings = CreateSettings();
        var saved = settings.TextPreprocessing;
        using (var editor = new TextPreprocessingEditViewModel(settings, new(), Translate))
        {
            editor.Rules[0].Replacement = "draft";
            editor.IsEnabled = false;
            editor.AddRuleCommand.Execute(null);
            Assert.Same(saved, settings.TextPreprocessing);
            Assert.True(saved.IsEnabled);
            Assert.Equal("public", saved.Rules[0].Replacement);
            await editor.PreviewTask;
        }
        Assert.Single(settings.TextPreprocessing.Rules);
    }

    [Fact]
    public async Task ApplyValidatesEveryRuleAndReplacesSettingsOnce()
    {
        var settings = CreateSettings();
        var changes = 0;
        settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Settings.TextPreprocessing)) changes++; };
        using var editor = new TextPreprocessingEditViewModel(settings, new(), Translate);
        editor.Rules[0].Replacement = "saved";
        editor.AddRuleCommand.Execute(null);
        editor.SelectedRule!.IsEnabled = false;
        Assert.False(await editor.ApplyAsync());
        Assert.Equal(0, changes);
        editor.SelectedRule.Pattern = "valid";
        Assert.True(await editor.ApplyAsync());
        Assert.Equal(1, changes);
        Assert.Equal("saved", settings.TextPreprocessing.Rules[0].Replacement);
        editor.Rules[0].Replacement = "later draft";
        Assert.Equal("saved", settings.TextPreprocessing.Rules[0].Replacement);
    }

    [Fact]
    public async Task PreviewTracksOrderAndRemovalWhileIgnoringGlobalSwitchWithoutSaving()
    {
        var settings = CreateSettings();
        using var editor = new TextPreprocessingEditViewModel(settings, new(), Translate);
        editor.PreviewInput = "secret";
        editor.AddRuleCommand.Execute(null);
        editor.SelectedRule!.Pattern = "public";
        editor.SelectedRule.Replacement = "final";
        await editor.PreviewTask;
        Assert.Equal("final", editor.PreviewText);
        Assert.Equal([1, 1], editor.PreviewMatches.Select(match => match.MatchCount));

        editor.MoveRuleUpCommand.Execute(null);
        await editor.PreviewTask;
        Assert.Equal("public", editor.PreviewText);
        Assert.Equal([0, 1], editor.PreviewMatches.Select(match => match.MatchCount));
        editor.IsEnabled = false;
        await editor.PreviewTask;
        Assert.Equal("public", editor.PreviewText);
        Assert.Equal(1, editor.PreviewMatchCount);
        Assert.True(settings.TextPreprocessing.IsEnabled);
        editor.IsEnabled = true;
        editor.SelectedRule = editor.Rules[1];
        editor.RemoveRuleCommand.Execute(null);
        await editor.PreviewTask;
        Assert.Equal("secret", editor.PreviewText);
    }

    [Fact]
    public async Task DisabledConfigurationCanBeTestedWithoutEnablingTranslationOrDisabledRules()
    {
        var settings = CreateSettings();
        settings.TextPreprocessing.IsEnabled = false;
        settings.TextPreprocessing.Rules.Add(new() { Pattern = "public", Replacement = "wrong", IsEnabled = false });
        var service = new TextPreprocessingService();
        using var editor = new TextPreprocessingEditViewModel(settings, service, Translate);
        editor.PreviewInput = "secret";
        await editor.PreviewTask;
        Assert.Equal("public", editor.PreviewText);
        Assert.Single(editor.PreviewMatches);
        Assert.False(settings.TextPreprocessing.IsEnabled);
        Assert.Equal("secret", (await service.PrepareAsync("secret", settings.TextPreprocessing)).RequestText);
        Assert.True(await editor.ApplyAsync());
        Assert.False(settings.TextPreprocessing.IsEnabled);
    }

    [Fact]
    public async Task PreviewAndExecutionReportSameFailureCategory()
    {
        var settings = CreateSettings();
        using var editor = new TextPreprocessingEditViewModel(settings, new(), Translate);
        editor.Rules[0].Pattern = "(";
        await editor.PreviewTask;
        Assert.Contains("TranslatePreprocessingFailed", editor.PatternError);
        Assert.Contains("Position", editor.PatternError);
        Assert.Empty(editor.PreviewError);
        Assert.Empty(editor.PreviewText);
        Assert.False(await editor.ApplyAsync());
    }

    [Fact]
    public async Task RapidEditsCannotPublishAnObsoletePreview()
    {
        using var editor = new TextPreprocessingEditViewModel(CreateSettings(), new(), Translate);
        editor.PreviewInput = "secret";
        editor.Rules[0].Replacement = "old";
        var oldPreview = editor.PreviewTask;
        editor.Rules[0].Replacement = "new";
        await Task.WhenAll(oldPreview, editor.PreviewTask);
        Assert.Equal("new", editor.PreviewText);
    }

    [Fact]
    public async Task NewEmptyRuleShowsGuidanceUntilSaveIsAttempted()
    {
        using var editor = new TextPreprocessingEditViewModel(CreateSettings(), new(), Translate);
        editor.AddRuleCommand.Execute(null);
        await editor.PreviewTask;
        Assert.Empty(editor.PreviewError);
        Assert.Empty(editor.PatternError);
        Assert.True(editor.IsPreviewIncomplete);
        Assert.False(await editor.ApplyAsync());
        Assert.NotEmpty(editor.PatternError);
        editor.SelectedRule!.Pattern = "valid";
        await editor.PreviewTask;
        Assert.Empty(editor.PatternError);
        Assert.False(editor.IsPreviewIncomplete);
    }

    [Fact]
    public async Task SaveSelectsInvalidDisabledRuleAndFieldErrorFollowsSelection()
    {
        var settings = CreateSettings();
        settings.TextPreprocessing.Rules.Add(new() { Id = "broken", Pattern = "(", IsEnabled = false });
        using var editor = new TextPreprocessingEditViewModel(settings, new(), Translate);
        Assert.False(await editor.ApplyAsync());
        Assert.Equal("broken", editor.SelectedRule!.Id);
        Assert.NotEmpty(editor.PatternError);
        Assert.NotNull(editor.PatternErrorOffset);
        editor.SelectedRule = editor.Rules[0];
        Assert.Empty(editor.PatternError);
        editor.SelectedRule = editor.Rules[1];
        Assert.NotEmpty(editor.PatternError);
        editor.SelectedRule.Pattern = "fixed";
        await editor.PreviewTask;
        Assert.Empty(editor.PatternError);
        Assert.Null(editor.PatternErrorOffset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObsoleteSaveFailureCannotPublishAfterEditingOrDisposing(bool dispose)
    {
        var settings = CreateSettings();
        for (var i = 0; i < 500; i++)
            settings.TextPreprocessing.Rules.Add(new() { Pattern = $"sample{i}" });
        settings.TextPreprocessing.Rules.Add(new() { Pattern = "(" });
        using var editor = new TextPreprocessingEditViewModel(settings, new(), Translate);
        using var completion = new QueuedContext();
        var previous = SynchronizationContext.Current;
        Task<bool> save;
        try
        {
            SynchronizationContext.SetSynchronizationContext(completion);
            save = editor.ApplyAsync();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.False(save.IsCompleted);
        Assert.True(completion.Ready.Wait(TimeSpan.FromSeconds(5)));
        if (dispose) editor.Dispose();
        else editor.Rules[^1].Pattern = "fixed";
        completion.Drain();
        Assert.False(await save);
        Assert.Empty(editor.PatternError);
        Assert.Empty(editor.PreviewError);
        if (!dispose)
        {
            await editor.PreviewTask;
            Assert.Empty(editor.PatternError);
        }
    }

    private sealed class QueuedContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();
        internal ManualResetEventSlim Ready { get; } = new();
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _queue.Enqueue(() => callback(state));
            Ready.Set();
        }
        internal void Drain()
        {
            while (_queue.TryDequeue(out var callback)) callback();
        }
        public void Dispose() => Ready.Dispose();
    }

    [Fact]
    public async Task RuleCommandsTrackSelectionOrderAndEmptyList()
    {
        using var editor = new TextPreprocessingEditViewModel(CreateSettings(), new(), Translate);
        Assert.False(editor.MoveRuleUpCommand.CanExecute(null));
        Assert.False(editor.MoveRuleDownCommand.CanExecute(null));
        Assert.True(editor.RemoveRuleCommand.CanExecute(null));
        var changes = 0;
        editor.MoveRuleUpCommand.CanExecuteChanged += (_, _) => changes++;
        editor.AddRuleCommand.Execute(null);
        editor.SelectedRule!.Pattern = "public";
        var selected = editor.SelectedRule;
        Assert.True(editor.MoveRuleUpCommand.CanExecute(null));
        Assert.False(editor.MoveRuleDownCommand.CanExecute(null));
        editor.MoveRuleUpCommand.Execute(null);
        Assert.Same(selected, editor.Rules[0]);
        Assert.False(editor.MoveRuleUpCommand.CanExecute(null));
        Assert.True(editor.MoveRuleDownCommand.CanExecute(null));
        editor.RemoveRuleCommand.Execute(null);
        editor.RemoveRuleCommand.Execute(null);
        Assert.Null(editor.SelectedRule);
        Assert.False(editor.RemoveRuleCommand.CanExecute(null));
        Assert.False(editor.MoveRuleUpCommand.CanExecute(null));
        Assert.False(editor.MoveRuleDownCommand.CanExecute(null));
        Assert.True(changes > 0);
        await editor.PreviewTask;
    }

    [Theory]
    [InlineData("ACME-\\d+", "Product A", "ACME-\\d+ → Product·A")]
    [InlineData("remove", "", "remove → ∅")]
    [InlineData("line", "a\r\n\tb", "line → a␍↵⇥b")]
    public void RuleSummaryMakesWhitespaceAndEmptyReplacementVisible(string pattern, string replacement, string expected)
    {
        var summary = new STranslate.Converters.TextReplacementSummaryConverter().Convert(
            [pattern, replacement], typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, summary);
    }

    private static Settings CreateSettings() => new()
    {
        TextPreprocessing = new() { IsEnabled = true, Rules = [new() { Pattern = "secret", Replacement = "public" }] }
    };

    private static string Translate(string key) => key switch
    {
        "Translate_TextPreprocessingNewRule" => "Rule {0}",
        "Translate_TextPreprocessingErrorPosition" => "Position {0}",
        _ => key
    };
}
