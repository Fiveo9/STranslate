using iNKORE.UI.WPF.Modern.Controls;
using STranslate.ViewModels;

namespace STranslate.Controls;

public partial class TextPreprocessingDialog : ContentDialog
{
    public TextPreprocessingDialog(TextPreprocessingEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        IsPrimaryButtonEnabled = false;
        try
        {
            if (await ((TextPreprocessingEditViewModel)DataContext).ApplyAsync())
                Hide();
            else if (DataContext is TextPreprocessingEditViewModel { PatternError.Length: > 0 } editor)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsVisible || editor.PatternError.Length == 0) return;
                    PatternInput.BringIntoView();
                    PatternInput.Focus();
                    var offset = Math.Clamp(editor.PatternErrorOffset ?? 0, 0, PatternInput.Text.Length);
                    PatternInput.Select(offset, offset < PatternInput.Text.Length ? 1 : 0);
                }, System.Windows.Threading.DispatcherPriority.DataBind);
            }
        }
        finally { IsPrimaryButtonEnabled = true; }
    }
}
