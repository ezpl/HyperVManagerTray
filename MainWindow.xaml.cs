using Microsoft.UI.Xaml;

namespace HyperVManagerTray;

/// <summary>Invisible host window — never shown; keeps the WinUI app alive (see <see cref="App"/>).</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.IsShownInSwitchers = false;
    }
}
