using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using QNote.ViewModels;

namespace QNote.Views;

/// <summary>
/// The three-pane notes screen. Code-behind is UI wiring only: it resolves the VM
/// from DI, forwards the ListView selection / Loaded / Ctrl+S to the VM, and hosts
/// the delete-confirmation dialog (UI types stay out of the VM).
/// </summary>
public sealed partial class NotesPage : Page
{
    public NotesPageViewModel ViewModel { get; }

    public NotesPage()
    {
        ViewModel = App.Services.GetRequiredService<NotesPageViewModel>();
        InitializeComponent();
        ViewModel.FocusTitleRequested += OnFocusTitleRequested;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ViewModel.LoadAsync();

    private void OnFocusTitleRequested() => TitleBox.Focus(FocusState.Programmatic);

    private async void NotesList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await ViewModel.OnSelectionChangedAsync();

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除便签",
            Content = "确认删除这条便签?此操作无法撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.DeleteSelectedNoteAsync();
    }

    private async void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.SaveCommand.CanExecute(null))
            await ViewModel.SaveCommand.ExecuteAsync(null);
    }
}
