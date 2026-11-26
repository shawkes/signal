using System.Runtime.CompilerServices;
using Uno.UI.Hosting;

namespace Signal.Native.Uno.App;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
            WindowsStartupSplash.Show();

        try
        {
            RunHost();
        }
        finally
        {
            WindowsStartupSplash.Close();
        }
    }

    // Let the splash start before the runtime resolves the Uno host types.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunHost()
    {
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            .UseWin32()
            .Build();

        host.Run();
    }
}
