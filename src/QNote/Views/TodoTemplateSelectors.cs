using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QNote.ViewModels;

namespace QNote.Views;

/// <summary>
/// Middle-column task card selector: the completed variant renders the
/// strikethrough/de-emphasized card (collapsed tail), the incomplete variant
/// the normal card. Data templates are declared in TodoPage.xaml resources.
/// </summary>
public sealed partial class TodoTaskTemplateSelector : DataTemplateSelector
{
    public DataTemplate? IncompleteTemplate { get; set; }

    public DataTemplate? CompletedTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        item is TodoTaskItemViewModel { IsCompleted: true } ? CompletedTemplate : IncompleteTemplate;
}

/// <summary>
/// Left-pane navigation selector: smart-view rows / the 我的清单 group cap /
/// DB list rows (three templates, one flat collection).
/// </summary>
public sealed partial class TodoNavTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SmartTemplate { get; set; }

    public DataTemplate? CapTemplate { get; set; }

    public DataTemplate? ListTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        item is TodoNavItemViewModel nav
            ? nav.Kind switch
            {
                TodoNavKind.ListsCap => CapTemplate,
                TodoNavKind.List => ListTemplate,
                _ => SmartTemplate,
            }
            : SmartTemplate;
}
