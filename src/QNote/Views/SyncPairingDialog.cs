using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using QNote.Services;
using QNote.Sync;

namespace QNote.Views;

internal static class SyncPairingDialog
{
    public static (ContentDialog Dialog, RadioButtons Choices) Create(Page page, SyncDestination? destination)
    {
        var choices = new RadioButtons { SelectedIndex = 0 };
        AutomationProperties.SetAutomationId(choices, "SyncPairingChoices");
        AutomationProperties.SetName(choices, AppStrings.GetString("SyncPairingTitle"));
        foreach (var key in new[] { "SyncPairingMerge", "SyncPairingCloud", "SyncPairingLocal" })
            choices.Items.Add(AppStrings.GetString(key));
        var content = new StackPanel { Spacing = 12 };
        if (destination is not null)
            content.Children.Add(new TextBlock
            {
                Text = AppStrings.GetFormat("SyncPairingDestination", SyncOutcomeText.DestinationDetail(destination)),
                TextWrapping = TextWrapping.Wrap,
            });
        content.Children.Add(new TextBlock { Text = AppStrings.GetString("SyncPairingHint"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(choices);
        return (new ContentDialog
        {
            XamlRoot = page.XamlRoot, RequestedTheme = page.ActualTheme,
            Title = AppStrings.GetString("SyncPairingTitle"), Content = content,
            PrimaryButtonText = AppStrings.GetString("SyncPairingApply"),
            CloseButtonText = AppStrings.GetString("CancelButton"), DefaultButton = ContentDialogButton.Primary,
        }, choices);
    }
}
