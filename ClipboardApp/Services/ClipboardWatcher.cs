using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClipboardApp.Services;

public sealed class ClipboardWatcher : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private readonly HwndSource _source;
    private bool _disposed;

    public ClipboardWatcher(WindowInteropHelper helper)
    {
        if (helper.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Window handle has not been created.");
        }

        _source = HwndSource.FromHwnd(helper.Handle)
            ?? throw new InvalidOperationException("Unable to create clipboard message source.");
        _source.AddHook(WndProc);
        AddClipboardFormatListener(helper.Handle);
    }

    public event EventHandler? ClipboardChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
