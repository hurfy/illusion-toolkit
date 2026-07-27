using System.Windows;

namespace Illusion.Views;

/// <summary>
/// Keeps a window's opening size inside the desktop. A size in XAML is a wish — on the smallest screen the
/// editor supports (1280x720, whose work area is shorter still once the taskbar is out) a window asking for
/// 720 opens taller than the desktop, with its bottom edge under the taskbar.
/// </summary>
internal static class WindowFit
{
    /// <summary>
    /// Shrinks <paramref name="window"/>'s requested size to what the desktop offers. Call it in the
    /// constructor: the size is then already final when the window is placed, so nothing jumps and a
    /// centred window is centred on its real size.
    /// <para>
    /// The work area is the primary monitor's. On a multi-monitor desk that only makes the opening size a
    /// little conservative when the window lands on a roomier screen — never too large to fit, which is the
    /// direction that matters.
    /// </para>
    /// </summary>
    public static void ToWorkArea(Window window)
    {
        Rect work = SystemParameters.WorkArea;
        if (work.Width > 0) window.Width = Math.Min(window.Width, work.Width);
        if (work.Height > 0) window.Height = Math.Min(window.Height, work.Height);
    }
}
