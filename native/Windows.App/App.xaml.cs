using Microsoft.UI.Xaml;

namespace Signal.Native.Windows.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        _window = new MainWindow(arguments.Length == 2 ? arguments[1] : null);
        _window.Activate();
    }
}
