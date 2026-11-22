using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CorsiDuplicate.UI.Controls;

/// <summary>
/// Paints a window's native title bar in the app palette (dark caption, light text) so the
/// header no longer looks like a stock white Windows bar. Windows 11 takes exact colours;
/// Windows 10 only honours the dark-mode flag; older systems ignore both calls harmlessly.
/// </summary>
public static class TitleBarTheme
{
    private const int UseImmersiveDarkMode = 20;
    private const int CaptionColor = 35;
    private const int BorderColor = 34;
    private const int TextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var dark = 1;
            DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int));

            // COLORREF is 0x00BBGGRR. Surface #1E2128, border #333844, text #E8EAEF.
            var caption = 0x28211E;
            var border = 0x443833;
            var text = 0xEFEAE8;
            DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, BorderColor, ref border, sizeof(int));
            DwmSetWindowAttribute(handle, TextColor, ref text, sizeof(int));
        }
        catch (Exception)
        {
            // Cosmetic only: keep the default title bar if DWM is unavailable.
        }
    }
}
