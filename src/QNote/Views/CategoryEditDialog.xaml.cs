using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QNote.Controls;
using QNote.Converters;
using QNote.Services;

namespace QNote.Views;

/// <summary>
/// New / edit category dialog: name + preset-or-custom color + Segoe Fluent icon
/// grid. Pure UI — validation (blank name, #RRGGBB format, icon picked) happens
/// on the primary click and cancels the dialog; business validation (duplicates,
/// built-ins) is the service's job, surfaced by the caller.
/// </summary>
public sealed partial class CategoryEditDialog : ContentDialog
{
    public CategoryEditDialog(string title, string name, string colorHex, string iconKey)
    {
        InitializeComponent();
        Title = title;
        NameBox.Text = name;
        HexBox.Text = colorHex;

        var preset = IconCatalog.Options.FirstOrDefault(o => o.Key == iconKey);
        GlyphGrid.SelectedItem = preset ?? IconCatalog.Options[0];
    }

    /// <summary>Trimmed category name (valid after the dialog closes).</summary>
    public string CategoryName => NameBox.Text.Trim();

    /// <summary>Chosen <c>#RRGGBB</c> color.</summary>
    public string ColorHex => HexBox.Text.Trim();

    /// <summary>Chosen icon key (hex codepoint).</summary>
    public string IconKey => (GlyphGrid.SelectedItem as GlyphOption)?.Key ?? IconCatalog.Options[0].Key;

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex })
            HexBox.Text = hex;
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e) => HideError();

    private void GlyphGrid_ItemClick(object sender, ItemClickEventArgs e) => HideError();

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (CategoryName.Length == 0)
        {
            ShowError(AppStrings.GetString("CategoryErrorNameBlank"));
            args.Cancel = true;
            return;
        }
        if (!HexToBrushConverter.TryParse(ColorHex, out _))
        {
            ShowError(AppStrings.GetString("CategoryEditColorInvalidHint"));
            args.Cancel = true;
            return;
        }
        if (GlyphGrid.SelectedItem is null)
        {
            ShowError(AppStrings.GetString("CategoryEditIconRequired"));
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
