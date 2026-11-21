using Microsoft.UI.Xaml;
using Signal.Native.Windows.App;

namespace Signal.Native.Uno.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        _window = new MainWindow(arguments.Length == 2 ? arguments[1] : null,
            applicationTitle: "Signal Uno", settingsProfile: "Signal Uno");
        _window.Activate();
    }
}
