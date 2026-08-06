using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace DreamTray.App.Views;

/// <summary>
/// Invisible owner window for the tray context menu.
///
/// A <see cref="ContextMenu"/> opened with no focused owner stays on screen after
/// the user clicks away — the classic tray-menu bug. The documented fix is to give
/// the menu a foreground owner window and let it close when that window loses
/// activation, which is all this class does.
/// </summary>
internal sealed class MenuHostWindow : Window
{
    private static MenuHostWindow? _instance;

    private MenuHostWindow()
    {
        // Zero-sized, tool-window, off-screen: present to the window manager,
        // invisible to the user, and absent from Alt+Tab.
        Width = 0;
        Height = 0;
        Left = -32000;
        Top = -32000;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        ShowActivated = true;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
    }

    public static void ShowMenu(ContextMenu menu)
    {
        _instance ??= new MenuHostWindow();
        _instance.Show();

        // Explorer owns the foreground while the tray icon is clicked; take it, or
        // the menu opens behind and never receives the dismiss click.
        SetForegroundWindow(new WindowInteropHelper(_instance).Handle);

        menu.PlacementTarget = _instance;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;

        void OnClosed(object? sender, RoutedEventArgs e)
        {
            menu.Closed -= OnClosed;
            _instance?.Hide();
        }
        menu.Closed += OnClosed;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
}
