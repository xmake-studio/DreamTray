using System.Windows;
using System.Windows.Threading;
using DreamTray.App.Views;
using DreamTray.Logging;

namespace DreamTray.App;

/// <summary>
/// <c>DreamTray.exe --selftest</c> — builds every window and widget, forces a
/// layout pass, then exits with a pass/fail line.
///
/// A tray app's UI only exists after a click, so a plain "does it start" check
/// proves almost nothing: a broken style key or a widget that throws in its
/// constructor would not surface until the user opened the panel. This exercises
/// those paths headlessly, which makes it usable after any change.
/// </summary>
internal static class SelfTest
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    public static int Run(AppServices services, string? screenshotDir = null)
    {
        // A WinExe has no console of its own; attach one so the result is visible
        // when the test is run from a terminal.
        AllocConsole();
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

        int failures = 0;
        var report = new List<string>();

        void Check(string name, Action action)
        {
            try
            {
                action();
                report.Add($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                failures++;
                report.Add($"  FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Check("theme resources", () =>
        {
            foreach (var key in new[]
            {
                "BodyText", "CaptionText", "ValueText", "GlyphText", "SubtitleText",
                "Card", "FluentButton", "AccentButton", "IconButton", "ToggleSwitch",
                "FluentSlider", "ThinScrollViewer", "FluentComboBox", "FluentCheckBox",
                "FluentTextBox",
            })
            {
                if (Application.Current.TryFindResource(key) == null)
                    throw new InvalidOperationException($"missing style '{key}'");
            }
        });

        Check("widget glyphs render", () =>
        {
            // A missing Fluent code point shows as an empty box, or — if the literal
            // was lost in an encoding round-trip — as nothing at all, which is easy
            // to ship without noticing.
            var typeface = new System.Windows.Media.Typeface("Segoe Fluent Icons");
            if (!typeface.TryGetGlyphTypeface(out var glyphs))
                typeface = new System.Windows.Media.Typeface("Segoe MDL2 Assets");
            if (!typeface.TryGetGlyphTypeface(out glyphs))
                throw new InvalidOperationException("no Fluent icon font installed");

            var registry = new Widgets.WidgetRegistry(services);
            var missing = new List<string>();
            foreach (var factory in registry.All)
            {
                string glyph = factory.Glyph;
                if (string.IsNullOrEmpty(glyph))
                    missing.Add($"{factory.TypeId} (empty)");
                else if (!glyphs.CharacterToGlyphMap.ContainsKey(glyph[0]))
                    missing.Add($"{factory.TypeId} (U+{(int)glyph[0]:X4})");
            }
            if (missing.Count > 0)
                throw new InvalidOperationException("no glyph for: " + string.Join(", ", missing));
        });

        PanelWindow? panel = null;
        Check("panel window", () =>
        {
            panel = new PanelWindow(services, () => { });
            // Off-screen so the test never flashes a window at the user.
            panel.Left = -32000;
            panel.Top = -32000;
            panel.Show();
            panel.UpdateLayout();
            if (panel.ActualHeight <= 0) throw new InvalidOperationException("panel measured to zero height");
        });

        Check("every widget builds", () =>
        {
            var registry = new Widgets.WidgetRegistry(services);
            var host = services.CreateHost(services.Settings.Scope(new System.Text.Json.Nodes.JsonObject()));
            foreach (var factory in registry.All)
            {
                var context = new ProbeContext(host,
                    services.Settings.Scope(new System.Text.Json.Nodes.JsonObject()), factory.TypeId);
                using var widget = factory.Create(context);
                _ = widget.Title;
                _ = widget.View;
                _ = widget.CreateSettingsView();
            }
        });

        Check("panel survives add and remove", () =>
        {
            // Building each widget once (above) misses the failure that matters most:
            // adding or removing rebuilds every card, and a widget that hands out a
            // cached element cannot be re-parented into a fresh card. That threw
            // mid-loop and took every widget below it off the panel.
            if (panel == null) throw new InvalidOperationException("no panel to exercise");

            var manager = panel.Manager;
            var registry = new Widgets.WidgetRegistry(services);
            var probeHost = services.CreateHost(services.Settings.Scope(new System.Text.Json.Nodes.JsonObject()));

            int before = manager.Instances.Count;
            var added = new List<string>();

            // Add every widget this machine offers, so each add rebuilds a panel that
            // already holds all the previous ones.
            foreach (var factory in registry.Available(probeHost).ToList())
            {
                if (!manager.Add(factory.TypeId)) continue; // already placed, or singleton
                panel.UpdateLayout();
                if (panel.LastRebuildFailures > 0)
                    throw new InvalidOperationException(
                        $"{panel.LastRebuildFailures} card(s) failed to build after adding "
                        + $"'{factory.TypeId}' — see the log");
                added.Add(manager.Instances[^1].InstanceId);
            }

            // Remove them again so the user's own layout is exactly as it was.
            foreach (var instanceId in added)
            {
                manager.Remove(instanceId);
                panel.UpdateLayout();
                if (panel.LastRebuildFailures > 0)
                    throw new InvalidOperationException(
                        $"{panel.LastRebuildFailures} card(s) failed to build after a removal — see the log");
            }

            if (manager.Instances.Count != before)
                throw new InvalidOperationException(
                    $"widget count drifted: {before} before, {manager.Instances.Count} after");
        });

        SettingsWindow? settings = null;
        Check("settings window", () =>
        {
            settings = new SettingsWindow(services) { Left = -32000, Top = -32000 };
            settings.Show();
            settings.UpdateLayout();
        });

        // Only the landing page is built by the constructor, so a page that throws on
        // build stays invisible until someone clicks its nav entry.
        Check("every settings page builds", () =>
        {
            if (settings == null) throw new InvalidOperationException("no settings window to exercise");
            foreach (string key in SettingsWindow.PageKeys)
            {
                settings.ShowPage(key);
                settings.UpdateLayout();
            }
        });

        if (screenshotDir != null)
        {
            Check("render screenshots", () =>
            {
                Directory.CreateDirectory(screenshotDir);
                if (panel != null) Render(panel, Path.Combine(screenshotDir, "panel.png"), services);
                if (settings != null) Render(settings, Path.Combine(screenshotDir, "settings.png"), services);
            });
        }

        Check("brightness enumeration", () => _ = services.Hardware.GetDisplays(refresh: true));
        Check("display mode enumeration", () => _ = services.Hardware.GetDisplayDevices());

        panel?.Close();
        settings?.Close();

        string summary = failures == 0
            ? $"selftest: all checks passed"
            : $"selftest: {failures} check(s) failed";

        Log.Write(summary);
        foreach (var line in report) Log.Write(line);
        Log.Flush();

        Console.WriteLine(summary);
        foreach (var line in report) Console.WriteLine(line);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Rasterise a window's content for visual review. The DWM backdrop is composited
    /// by the desktop, not by WPF, so it cannot appear in a RenderTargetBitmap —
    /// the theme's opaque surface colour is painted underneath instead, which is
    /// also what the window falls back to when a backdrop is unavailable.
    /// </summary>
    private static void Render(Window window, string path, AppServices services)
    {
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();

        int width = (int)Math.Ceiling(content.ActualWidth);
        int height = (int)Math.Ceiling(content.ActualHeight);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("nothing to render");

        var backdrop = new System.Windows.Media.SolidColorBrush(
            services.Theme.IsDark
                ? System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)
                : System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3));

        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(backdrop, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(content), null,
                             new Rect(0, 0, width, height));
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Throwaway context so widgets can be built outside the panel.</summary>
    private sealed class ProbeContext(IPluginHost host, IStorage storage, string id) : IWidgetContext
    {
        public IPluginHost Host => host;
        public IStorage Storage => storage;
        public string InstanceId => "probe-" + id;
        public void RequestTitleUpdate() { }
    }
}
