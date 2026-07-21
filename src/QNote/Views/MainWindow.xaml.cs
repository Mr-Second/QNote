using Microsoft.UI.Xaml;

namespace QNote.Views;

/// <summary>
/// The application window: frameless (content extended into the title bar) with a
/// custom <c>TitleBar</c> and a Frame that hosts pages.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        RootFrame.Navigate(typeof(MainPage));
    }
}
