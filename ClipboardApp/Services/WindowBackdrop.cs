using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClipboardApp.Services;

public static class WindowBackdrop
{
    public static void Enable(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        window.Background = System.Windows.Media.Brushes.Transparent;
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = AccentState.AccentEnableBlurBehind,
                GradientColor = unchecked((int)0xAA121821)
            };

            var accentSize = Marshal.SizeOf(accent);
            var accentPtr = Marshal.AllocHGlobal(accentSize);
            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.WcaAccentPolicy,
                    SizeOfData = accentSize,
                    Data = accentPtr
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }
        catch
        {
            window.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 18, 24, 33));
        }
    }

    private enum WindowCompositionAttribute
    {
        WcaAccentPolicy = 19
    }

    private enum AccentState
    {
        AccentEnableBlurBehind = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
}
