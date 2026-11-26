using System.Runtime.InteropServices;

namespace Signal.Native.Uno.App;

internal static class WindowsStartupSplash
{
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsBorder = 0x00800000;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint SsCenter = 0x0001;
    private const uint SsIcon = 0x0003;
    private const uint SsCenterImage = 0x0200;
    private const uint WmClose = 0x0010;
    private const uint WmSetFont = 0x0030;
    private const uint StmSetIcon = 0x0170;
    private const uint SpiGetWorkArea = 0x0030;
    private const int SwShowNoActivate = 4;
    private const int DefaultGuiFont = 17;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;

    private static readonly Lock Gate = new();
    private static nint _window;
    private static bool _closed;

    public static void Show()
    {
        // Keep the window painting while Uno and the audio/MIDI devices start on the main thread.
        new Thread(Run) { IsBackground = true, Name = "Signal startup splash" }.Start();
    }

    public static void Close()
    {
        lock (Gate)
        {
            _closed = true;
            if (_window != 0)
                PostMessageW(_window, WmClose, 0, 0);
        }
    }

    private static void Run()
    {
        const int width = 304;
        const int height = 132;
        const int iconSize = 48;
        const string message = "Starting Signal Uno…";

        if (!SystemParametersInfoW(SpiGetWorkArea, 0, out var area, 0))
            return;

        int x = (area.Left + area.Right - width) / 2;
        int y = (area.Top + area.Bottom - height) / 2;
        nint window = CreateWindowExW(WsExToolWindow | WsExNoActivate, "STATIC", message,
            WsPopup | WsBorder | SsCenter | SsCenterImage, x, y, width, height, 0, 0, 0, 0);
        if (window == 0)
            return;

        nint icon = 0;
        try
        {
            icon = LoadImageW(0, Path.Combine(AppContext.BaseDirectory, "signaluno.ico"),
                ImageIcon, iconSize, iconSize, LrLoadFromFile);
            if (icon != 0)
            {
                nint image = CreateWindowExW(0, "STATIC", null, WsChild | WsVisible | SsIcon | SsCenterImage,
                    (width - iconSize) / 2, 18, iconSize, iconSize, window, 0, 0, 0);
                if (image != 0)
                {
                    SendMessageW(image, StmSetIcon, icon, 0);
                    nint label = CreateWindowExW(0, "STATIC", message, WsChild | WsVisible | SsCenter | SsCenterImage,
                        0, 76, width - 2, 34, window, 0, 0, 0);
                    if (label != 0)
                    {
                        SetWindowTextW(window, "");
                        SendMessageW(label, WmSetFont, GetStockObject(DefaultGuiFont), 0);
                    }
                }
            }
            SendMessageW(window, WmSetFont, GetStockObject(DefaultGuiFont), 0);

            lock (Gate)
            {
                if (_closed)
                    return;

                _window = window;
                ShowWindow(window, SwShowNoActivate);
            }

            while (GetMessageW(out var next, 0, 0, 0) > 0)
            {
                if (next.Window == window && next.Id == WmClose)
                    break;
                DispatchMessageW(in next);
            }
        }
        finally
        {
            lock (Gate)
                _window = 0;
            DestroyWindow(window);
            if (icon != 0)
                DestroyIcon(icon);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter, out Rect rect, uint flags);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string? title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(nint window, string title);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint GetStockObject(int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetMessageW(out Message message, nint window, uint min, uint max);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint DispatchMessageW(in Message message);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyIcon(nint icon);
}
