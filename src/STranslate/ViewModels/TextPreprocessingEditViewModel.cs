using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STranslate.Core;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace STranslate.ViewModels;

public partial class TextPreprocessingEditViewModel : ObservableObject, IDisposable
{
    private readonly Settings _settings;
    private readonly TextPreprocessingService _service;
    private readonly Func<string, string> _translate;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _previewCancellation;
    private bool _invalidConfiguration;
    private int _revision;
    private bool _disposed;
    private bool _initializing = true;
    private TextPreprocessingException? _patternFailure;

    public TextPreprocessingEditViewModel(Settings settings, TextPreprocessingService service, Func<string, string> translate)
    {
        _settings = settings;
        _service = service;
        _translate = translate;
        var saved = settings.TextPreprocessing;
        _invalidConfiguration = saved is null || saved.InvalidConfiguration.HasValue ||
            saved.Rules is null || saved.Rules.Any(rule => rule is null);
        IsEnabled = saved?.IsEnabled ?? false;
        foreach (var rule in saved?.Rules ?? [])
        {
            if (rule is null) continue;
            var draft = rule.Clone();
            Rules.Add(draft);
            draft.PropertyChanged += OnRuleChanged;
        }
        Rules.CollectionChanged += OnRulesChanged;
        SelectedRule = Rules.FirstOrDefault();
        _initializing = false;
        QueuePreview();
    }

    public ObservableCollection<TextReplacementRule> Rules { get; } = [];
    public ObservableCollection<TextReplacementPreview> PreviewMatches { get; } = [];

    [ObservableProperty] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial TextReplacementRule? SelectedRule { get; set; }
    [ObservableProperty] public partial string PreviewInput { get; set; } = "Please review ACME-123 today.";
    [ObservableProperty] public partial string PreviewText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewError { get; set; } = string.Empty;
    [ObservableProperty] public partial int PreviewMatchCount { get; set; }
    [ObservableProperty] public partial bool IsPreviewing { get; set; }
    [ObservableProperty] public partial bool IsSaving { get; set; }
    [ObservableProperty] public partial TextPreviewComparison? PreviewDiff { get; set; }
    [ObservableProperty] public partial string PatternError { get; set; } = string.Empty;
    [ObservableProperty] public partial int? PatternErrorOffset { get; set; }
    [ObservableProperty] public partial bool IsPreviewIncomplete { get; set; }
    [ObservableProperty] public partial int RuleOrderRevision { get; set; }
    internal Task PreviewTask { get; private set; } = Task.CompletedTask;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_initializing) DraftChanged();
    }

    partial void OnPreviewInputChanged(string value) => QueuePreview();

    partial void OnSelectedRuleChanged(TextReplacementRule? value)
    {
        UpdatePatternError();
        RefreshRuleCommands();
    }

    [RelayCommand]
    private void AddRule()
    {
        var rule = new TextReplacementRule { Name = string.Format(_translate("Translate_TextPreprocessingNewRule"), Rules.Count + 1) };
        Rules.Add(rule);
        SelectedRule = rule;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveRule))]
    private void RemoveRule()
    {
        if (SelectedRule is not { } rule) return;
        var index = Rules.IndexOf(rule);
        Rules.Remove(rule);
        SelectedRule = Rules.ElementAtOrDefault(Math.Min(index, Rules.Count - 1));
    }

    private bool CanRemoveRule() => SelectedRule is not null && Rules.Contains(SelectedRule);
    private bool CanMoveRuleUp() => SelectedRule is not null && Rules.IndexOf(SelectedRule) > 0;
    private bool CanMoveRuleDown() => SelectedRule is not null && Rules.IndexOf(SelectedRule) is var index &&
        index >= 0 && index < Rules.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveRuleUp))] private void MoveRuleUp() => MoveRule(-1);
    [RelayCommand(CanExecute = nameof(CanMoveRuleDown))] private void MoveRuleDown() => MoveRule(1);

    private void RefreshRuleCommands()
    {
        RemoveRuleCommand.NotifyCanExecuteChanged();
        MoveRuleUpCommand.NotifyCanExecuteChanged();
        MoveRuleDownCommand.NotifyCanExecuteChanged();
    }

    private void MoveRule(int offset)
    {
        if (SelectedRule is null) return;
        var index = Rules.IndexOf(SelectedRule);
        if (index >= 0 && index + offset >= 0 && index + offset < Rules.Count)
            Rules.Move(index, index + offset);
    }

    private void OnRulesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RuleOrderRevision++;
        if (e.Action != NotifyCollectionChangedAction.Move)
        {
            if (e.OldItems is not null)
                foreach (TextReplacementRule rule in e.OldItems) rule.PropertyChanged -= OnRuleChanged;
            if (e.NewItems is not null)
                foreach (TextReplacementRule rule in e.NewItems) rule.PropertyChanged += OnRuleChanged;
        }
        RefreshRuleCommands();
        DraftChanged();
    }

    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e) => DraftChanged();

    private void DraftChanged()
    {
        _invalidConfiguration = false;
        _revision++;
        _patternFailure = null;
        PatternError = string.Empty;
        PatternErrorOffset = null;
        PreviewError = string.Empty;
        QueuePreview();
    }

    private TextPreprocessingSettings CreateConfiguration() => new()
    {
        IsEnabled = IsEnabled,
        Rules = Rules.Select(rule => rule.Clone()).ToList()
    };

    private void QueuePreview()
    {
        if (!_disposed && !_initializing)
            PreviewTask = RefreshPreviewAsync();
    }

    internal async Task RefreshPreviewAsync()
    {
        _previewCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewCancellation = cancellation;
        var snapshot = CreateConfiguration();
        // Preview the draft without changing the saved switch or per-rule switches.
        snapshot.IsEnabled = true;
        var input = PreviewInput;
        var invalid = _invalidConfiguration;
        IsPreviewing = true;
        IsPreviewIncomplete = false;
        try
        {
            await Task.Delay(180, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            if (invalid)
                throw new TextPreprocessingException("", "", "", errorKind: "InvalidConfiguration");
            if (input.Length == 0 || snapshot.Rules.Any(rule => rule.IsEnabled && string.IsNullOrEmpty(rule.Pattern)))
            {
                ClearPreview();
                IsPreviewIncomplete = true;
                return;
            }
            var prepared = await _service.PrepareAsync(input, snapshot, cancellation.Token, includePreview: true);
            var difference = await Task.Run(() => TextPreprocessingPreviewDiff.Create(
                input, prepared.RequestText, cancellation.Token), cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return;
            PreviewText = prepared.RequestText;
            PreviewDiff = difference;
            PreviewMatchCount = prepared.TotalMatchCount;
            PreviewMatches.Clear();
            foreach (var match in prepared.MatchSummaries)
                PreviewMatches.Add(new(match.RuleName, match.MatchCount,
                    match.InputPreview ?? string.Empty, match.OutputPreview ?? string.Empty, match.IsPreviewAbbreviated));
            PreviewError = string.IsNullOrWhiteSpace(prepared.RequestText)
                ? _translate("TranslatePreprocessingEmpty") : string.Empty;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (TextPreprocessingException ex)
        {
            if (_disposed || cancellation.IsCancellationRequested) return;
            ClearPreview();
            PresentFailure(ex);
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
                IsPreviewing = false;
            }
            cancellation.Dispose();
        }
    }

    private void ClearPreview()
    {
        PreviewText = string.Empty;
        PreviewDiff = null;
        PreviewMatchCount = 0;
        PreviewMatches.Clear();
        PreviewError = string.Empty;
    }

    private void PresentFailure(TextPreprocessingException failure)
    {
        _patternFailure = failure.ErrorKind == "InvalidPattern" ? failure : null;
        UpdatePatternError();
        if (_patternFailure is null)
            PreviewError = TextPreprocessingErrorText.Format(failure, _translate);
    }

    private void UpdatePatternError()
    {
        var selectedFailure = _patternFailure is { } failure && SelectedRule?.Id == failure.RuleId;
        PatternError = selectedFailure ? TextPreprocessingErrorText.Format(_patternFailure!, _translate) : string.Empty;
        PatternErrorOffset = selectedFailure ? _patternFailure!.ErrorOffset : null;
        if (_patternFailure is not null)
            PreviewError = selectedFailure ? string.Empty : TextPreprocessingErrorText.Format(_patternFailure, _translate);
    }

    public async Task<bool> ApplyAsync()
    {
        if (_disposed || IsSaving) return false;
        IsSaving = true;
        var snapshot = CreateConfiguration();
        var revision = _revision;
        try
        {
            if (_invalidConfiguration)
                throw new TextPreprocessingException("", "", "", errorKind: "InvalidConfiguration");
            await Task.Run(() => _service.Validate(snapshot), _lifetime.Token);
            if (_disposed || revision != _revision) return false;
            _settings.TextPreprocessing = snapshot;
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (TextPreprocessingException ex)
        {
            if (_disposed || revision != _revision) return false;
            _previewCancellation?.Cancel();
            SelectedRule = Rules.FirstOrDefault(rule => rule.Id == ex.RuleId) ?? SelectedRule;
            PresentFailure(ex);
            return false;
        }
        finally { IsSaving = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        Rules.CollectionChanged -= OnRulesChanged;
        foreach (var rule in Rules) rule.PropertyChanged -= OnRuleChanged;
    }
}

public sealed record TextReplacementPreview(
    string RuleName, int MatchCount, string InputText, string OutputText, bool IsAbbreviated);
