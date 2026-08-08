using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DreamTray.App.Themes;

/// <summary>
/// Owns the app's colour tokens.
///
/// The palette is set in code rather than in two parallel Light/Dark XAML
/// dictionaries: with one source of truth for the key names, a token can never
/// exist in one theme and be missing in the other, and switching themes is a
/// dictionary update rather than a full resource reload (no visual flash).
///
/// Values follow the Windows 11 (WinUI 2/3) common control tokens, and the accent
/// comes from the user's Windows accent colour so the app matches the system.
/// </summary>
internal static class ThemeManager
{
    /// <summary>Repaint every control for the given theme.</summary>
    public static void Apply(ResourceDictionary resources, bool dark, bool translucent)
    {
        var accent = ReadAccentColor(dark);

        // Surfaces. These are the WinUI "solid background" and "card background"
        // values the Settings app uses, as opaque colours rather than low-alpha
        // white over an unknown backdrop — layering 5% white on whatever showed
        // through was what made everything read as flat grey.
        Set(resources, "WindowBackground", dark
            ? Rgba(0x1A, 0x1A, 0x1A, translucent ? (byte)0xF2 : (byte)0xFF)
            : Rgba(0xEE, 0xEE, 0xEE, translucent ? (byte)0xF2 : (byte)0xFF));

        // The hairline around the panel itself. Unlike the cards this one *is* a
        // light stroke on dark: the panel floats over the desktop rather than over
        // another surface, so it needs the edge to separate it from whatever is behind.
        Set(resources, "WindowStroke", dark
            ? Rgba(0xFF, 0xFF, 0xFF, 0x1F)
            : Rgba(0x00, 0x00, 0x00, 0x1F));

        // Card — every widget sits on one of these. Clearly lighter than the window
        // in dark mode and clearly closer to white in light mode, which is what
        // gives the Settings app its sense of depth.
        Set(resources, "CardBackground", dark
            ? Rgba(0x26, 0x26, 0x26, 0xFF)
            : Rgba(0xFB, 0xFB, 0xFB, 0xFF));
        Set(resources, "CardBackgroundHover", dark
            ? Rgba(0x2C, 0x2C, 0x2C, 0xFF)
            : Rgba(0xFF, 0xFF, 0xFF, 0xFF));
        // WinUI draws card edges with a dark stroke in both themes; a white stroke
        // on dark haloes the card and reads as grey haze rather than an edge.
        Set(resources, "CardStroke", dark
            ? Rgba(0x00, 0x00, 0x00, 0x19)
            : Rgba(0x00, 0x00, 0x00, 0x0F));

        // Control fills (buttons, combo boxes, switch tracks).
        Set(resources, "ControlFill", dark
            ? Rgba(0xFF, 0xFF, 0xFF, 0x0F)
            : Rgba(0xFF, 0xFF, 0xFF, 0xFF));
        Set(resources, "ControlFillHover", dark
            ? Rgba(0xFF, 0xFF, 0xFF, 0x17)
            : Rgba(0xF9, 0xF9, 0xF9, 0xFF));
        Set(resources, "ControlFillPressed", dark
            ? Rgba(0xFF, 0xFF, 0xFF, 0x0A)
            : Rgba(0xF2, 0xF2, 0xF2, 0xFF));
        Set(resources, "ControlStroke", dark
            ? Rgba(0xFF, 0xFF, 0xFF, 0x18)
            : Rgba(0x00, 0x00, 0x00, 0x17));

        // Subtle fill — transparent controls that only appear on hover.
        Set(resources, "SubtleFillHover", dark
            ? Rgba(0xFF, 0xFF, 0xFF, 0x0F)
            : Rgba(0x00, 0x00, 0x00, 0x0A));
        Set(resources, "SubtleFillPressed", dark
            ? Rgba(0xFF, 0xFF, 0xFF, 0x0A)
            : Rgba(0x00, 0x00, 0x00, 0x06));

        // Text. Primary is full strength — Windows reserves the dimmed greys for
        // secondary and disabled text only, and using grey for ordinary labels is
        // exactly what makes a UI look washed out next to the real thing.
        Set(resources, "TextPrimary", dark
            ? Rgba(0xFF, 0xFF, 0xFF, 0xFF)
            : Rgba(0x00, 0x00, 0x00, 0xE4));
        Set(resources, "TextSecondary", dark
            ? Rgba(0xFF, 0xFF, 0xFF, 0xC5)
            : Rgba(0x00, 0x00, 0x00, 0x9E));
        Set(resources, "TextTertiary", dark
            ? Rgba(0xFF, 0xFF, 0xFF, 0x87)
            : Rgba(0x00, 0x00, 0x00, 0x72));
        Set(resources, "TextOnAccent", dark
            ? Rgba(0x00, 0x00, 0x00, 0xE4)
            : Rgba(0xFF, 0xFF, 0xFF, 0xFF));

        // Flyouts and tooltips sit above the window, so they need their own
        // slightly lighter surface rather than inheriting the window colour.
        Set(resources, "FlyoutBackground", dark
            ? Rgba(0x26, 0x26, 0x26, 0xFF)
            : Rgba(0xF9, 0xF9, 0xF9, 0xFF));

        // Accent.
        Set(resources, "AccentBrush", accent);
        Set(resources, "AccentBrushHover", Shade(accent, dark ? -0.08 : 0.08));
        Set(resources, "AccentBrushPressed", Shade(accent, dark ? -0.16 : 0.16));
        Set(resources, "FocusStroke", dark
            ? Rgba(0xFF, 0xFF, 0xFF, 0xFF)
            : Rgba(0x00, 0x00, 0x00, 0xE4));

        // Semantic colours for readouts.
        Set(resources, "SuccessBrush", dark ? Rgba(0x6C, 0xCB, 0x5F, 0xFF) : Rgba(0x0F, 0x7B, 0x0F, 0xFF));
        Set(resources, "WarningBrush", dark ? Rgba(0xFC, 0xE1, 0x00, 0xFF) : Rgba(0x9D, 0x5D, 0x00, 0xFF));
        Set(resources, "DangerBrush", dark ? Rgba(0xFF, 0x99, 0xA4, 0xFF) : Rgba(0xC4, 0x2B, 0x1C, 0xFF));

        resources["IsDarkTheme"] = dark;
    }

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        if (resources[key] is SolidColorBrush existing && !existing.IsFrozen)
        {
            existing.Color = color;
            return;
        }
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private static void Set(ResourceDictionary resources, string key, SolidColorBrush brush) =>
        Set(resources, key, brush.Color);

    private static Color Rgba(byte r, byte g, byte b, byte a) => Color.FromArgb(a, r, g, b);

    /// <summary>Lighten (positive) or darken (negative) towards white/black.</summary>
    private static Color Shade(Color c, double amount)
    {
        double t = Math.Abs(amount);
        byte target = amount >= 0 ? (byte)255 : (byte)0;
        return Color.FromArgb(c.A,
            (byte)(c.R + (target - c.R) * t),
            (byte)(c.G + (target - c.G) * t),
            (byte)(c.B + (target - c.B) * t));
    }

    /// <summary>
    /// The user's Windows accent colour. Windows stores per-theme variants under
    /// the DWM key; the plain <c>AccentColor</c> is stored ABGR, not ARGB.
    /// </summary>
    private static Color ReadAccentColor(bool dark)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                var c = Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF),
                                      (byte)((abgr >> 16) & 0xFF));
                // The raw accent is tuned for the light theme; lift it a little on
                // dark backgrounds so text on it keeps enough contrast.
                return dark ? Shade(c, 0.25) : c;
            }
        }
        catch { /* fall through to the Windows default blue */ }
        return dark ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8);
    }
}
