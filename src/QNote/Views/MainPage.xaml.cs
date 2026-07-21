using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using QNote.ViewModels;

namespace QNote.Views;

/// <summary>Placeholder content page; its VM is resolved from the DI composition root.</summary>
public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }
}
