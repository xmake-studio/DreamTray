using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using DreamTray.Theme.Interop;

namespace DreamTray.Theme;

/// <summary>Applies colour modes through the Windows theme manager on an STA thread.</summary>
internal static class WindowsThemeSwitcher
{
    // This shell COM interface is undocumented. Fail visibly if unavailable rather
    // than falling back to registry writes, which leave Explorer partly themed.
    public static void Apply(bool dark)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("The Windows theme manager requires an STA thread.");

        var type = Type.GetTypeFromCLSID(new Guid("9324DA94-50EC-4A14-A770-E90CA03E7C8F"), true)!;
        var manager = (IThemeManager2)Activator.CreateInstance(type)!;
        try
        {
            manager.Init(InitializationFlags.ThemeInitNoFlags);
            // Save unsaved personalization changes before taking a copy.
            manager.UpdateCustomTheme();
            using var themes = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes");
            var source = themes?.GetValue("CurrentTheme") as string;
            if (string.IsNullOrWhiteSpace(source))
                throw new InvalidOperationException("Windows did not report its current theme file.");
            source = Environment.ExpandEnvironmentVariables(source);

            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DreamTray", "Themes");
            Directory.CreateDirectory(directory);
            // Alternate files so we never overwrite the file Windows is reading.
            var path = Path.Combine(directory, dark ? "Dark.theme" : "Light.theme");
            if (string.Equals(Path.GetFullPath(source), path, StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(directory, dark ? "Dark-current.theme" : "Light-current.theme");
            File.Copy(source, path, overwrite: true);

            // Native INI APIs retain the encoding of Windows' ANSI/UTF-16 theme files.
            Write(path, "Theme", "DisplayName", dark ? "DreamTray Dark" : "DreamTray Light");
            Write(path, "Theme", "ThemeId", Guid.NewGuid().ToString("B"));
            Write(path, "VisualStyles", "AppMode", dark ? "Dark" : "Light");
            Write(path, "VisualStyles", "SystemMode", dark ? "Dark" : "Light");
            // CurrentTheme can point at a saved theme whose accent is now outdated.
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("ColorizationColor") is int color)
                Write(path, "VisualStyles", "ColorizationColor", $"0X{unchecked((uint)color):X8}");
            using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (desktop?.GetValue("AutoColorization") is int automatic)
                Write(path, "VisualStyles", "AutoColorization", automatic == 0 ? "0" : "1");

            const ThemeApplyFlags preserve = ThemeApplyFlags.ThemeApplyFlagIgnoreBackground
                | ThemeApplyFlags.ThemeApplyFlagIgnoreCursor
                | ThemeApplyFlags.ThemeApplyFlagIgnoreDesktopIcons
                | ThemeApplyFlags.ThemeApplyFlagIgnoreSound
                | ThemeApplyFlags.ThemeApplyFlagIgnoreScreensaver;
            manager.AddAndSelectTheme(nint.Zero, path, preserve, ThemePackFlags.ThemepackFlagSilent);
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    private static void Write(string path, string section, string key, string value)
    {
        if (!WritePrivateProfileString(section, key, value, path))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrivateProfileString(string section, string key, string value, string path);
}
