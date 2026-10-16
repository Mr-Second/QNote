using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QNote.Services;

namespace QNote.Views;

/// <summary>
/// New / rename todo list dialog: name + preset color swatches. Pure UI — a
/// blank name cancels the dialog; business validation (duplicates etc.) is the
/// service's job, surfaced by the caller. Mirrors <see cref="CategoryEditDialog"/>
/// without the icon grid (todo lists have no icon this stage).
/// </summary>
public sealed partial class TodoListDialog : ContentDialog
{
    public TodoListDialog(string title, string name, string colorHex)
    {
        InitializeComponent();
        Title = title;
        NameBox.Text = name;
        _selectedColor = colorHex;
    }

    private string _selectedColor;

    /// <summary>Trimmed list name (valid after the dialog closes).</summary>
    public string ListName => NameBox.Text.Trim();

    /// <summary>Chosen <c>#RRGGBB</c> color.</summary>
    public string ColorHex => _selectedColor;

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex })
            _selectedColor = hex;
        HideError();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => HideError();

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (ListName.Length == 0)
        {
            ShowError(AppStrings.GetString("TodoListNameRequired"));
            args.Cancel = true;
            return;
        }
        if (!Converters.HexToBrushConverter.TryParse(ColorHex, out _))
        {
            ShowError(AppStrings.GetString("CategoryEditColorInvalidHint"));
            args.Cancel = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
