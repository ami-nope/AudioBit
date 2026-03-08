using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AudioBit.App.Infrastructure;

internal sealed class GlobalHotKeyService : IDisposable
{
    private const int HotKeyId = 0x0AB1;
    private const int WmHotKey = 0x0312;

    private HwndSource? _source;
    private IntPtr _handle;
    private bool _isRegistered;

    public event EventHandler? Pressed;

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _handle = new WindowInteropHelper(window).Handle;
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowProc);
    }

    public bool Register(string? hotKeyText)
    {
        if (string.IsNullOrWhiteSpace(hotKeyText) || !HotKeyGesture.TryParse(hotKeyText, out var gesture))
        {
            Unregister();
            return false;
        }

        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        Unregister();
        _isRegistered = RegisterHotKey(_handle, HotKeyId, (uint)gesture.Modifiers, gesture.VirtualKey);
        return _isRegistered;
    }

    public void Unregister()
    {
        if (!_isRegistered || _handle == IntPtr.Zero)
        {
            return;
        }

        UnregisterHotKey(_handle, HotKeyId);
        _isRegistered = false;
    }

    public void Dispose()
    {
        Unregister();

        if (_source is not null)
        {
            _source.RemoveHook(WindowProc);
            _source = null;
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
