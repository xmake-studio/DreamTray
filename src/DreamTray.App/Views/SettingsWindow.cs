using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DreamTray.App.Interop;
using DreamTray.App.Widgets;
using DreamTray.Plugins;
using DreamTray.Theme;

namespace DreamTray.App.Views;

/// <summary>
/// The full settings window: start-up, appearance, panel animation, the APU power
/// policy and the plugin list.
///
/// Everything here edits the same state the widgets do — the TDP page and the TDP
/// widget's flyout write the same <see cref="Settings.TdpSettings"/> — so there is
/// no second copy to keep in sync.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly ContentControl _content = new();
    private readonly StackPanel _nav = new();

    private static readonly (string Key, string Glyph, string Label)[] Pages =
    [
        ("general",    "\uE713", "General"),
        ("animations", "\uE916", "Animations"),
        ("power",      "\uE945", "Power"),
        ("plugins",    "\uEA86", "Plugins"),
        ("about",      "\uE946", "About"),
    ];

    public SettingsWindow(AppServices services)
    {
        _services = services;

        Title = "DreamTray settings";
        Width = 720;
        Height = 560;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Application.Current?.TryFindResource("WindowBackground") as Brush ?? Brushes.White;

        Content = BuildLayout();
        Select("general");

        SourceInitialized += (_, _) =>
        {
            WindowEffects.SetDarkMode(this, _services.Theme.IsDark);
            // Mica is the material Windows uses for settings-style windows.
            if (WindowEffects.TryApplyBackdrop(this, WindowEffects.Backdrop.Mica))
            {
                WindowEffects.ExtendFrameIntoClientArea(this);
                Background = Brushes.Transparent;
            }
        };
        _services.Theme.Changed += OnThemeChanged;
    }

    /// <summary>Every page key, for <c>--selftest</c> to build each one in turn.</summary>
    internal static IEnumerable<string> PageKeys => Pages.Select(p => p.Key);

    /// <summary>Switch pages, for <c>--selftest</c>.</summary>
    internal void ShowPage(string key) => Select(key);

    private UIElement BuildLayout()
    {
        _nav.Margin = new Thickness(8, 12, 8, 12);
        _nav.Width = 180;

        foreach (var (key, glyph, label) in Pages)
        {
            var button = new Button
            {
                Style = Ui.Find("FluentButton"),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(10, 8, 10, 8),
                Tag = key,
                Content = NavContent(glyph, label),
            };
            button.Click += (_, _) => Select(key);
            _nav.Children.Add(button);
        }

        var scroller = new ScrollViewer
        {
            Style = Ui.Find("ThinScrollViewer"),
            Padding = new Thickness(20, 12, 20, 20),
            Content = _content,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_nav, 0);
        Grid.SetColumn(scroller, 1);
        grid.Children.Add(_nav);
        grid.Children.Add(scroller);
        return grid;
    }

    private static UIElement NavContent(string glyph, string label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Ui.Glyph(glyph, 14);
        icon.Margin = new Thickness(0, 0, 10, 0);
        panel.Children.Add(icon);
        panel.Children.Add(Ui.Body(label));
        return panel;
    }

    private void Select(string key)
    {
        foreach (var button in _nav.Children.OfType<Button>())
        {
            bool selected = (string?)button.Tag == key;
            button.Background = selected
                ? Application.Current?.TryFindResource("CardBackground") as Brush ?? Brushes.Transparent
                : Brushes.Transparent;
        }

        _content.Content = key switch
        {
            "animations" => BuildAnimationsPage(),
            "power" => BuildPowerPage(),
            "plugins" => BuildPluginsPage(),
            "about" => BuildAboutPage(),
            _ => BuildGeneralPage(),
        };
    }

    // ---------------------------------------------------------------- pages

    private UIElement Section(string title, params UIElement[] children)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        var heading = new TextBlock { Text = title, Style = Ui.Find("SubtitleText") };
        heading.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(heading);

        var card = new Border { Style = Ui.Find("Card"), Padding = new Thickness(16, 14, 16, 14) };
        var inner = new StackPanel();
        foreach (var child in children)
        {
            if (child is FrameworkElement fe && inner.Children.Count > 0 && fe.Margin == default)
                fe.Margin = new Thickness(0, 10, 0, 0);
            inner.Children.Add(child);
        }
        card.Child = inner;
        stack.Children.Add(card);
        return stack;
    }

    private UIElement BuildGeneralPage()
    {
        var autostart = Ui.Switch(_services.Autostart.IsEnabled(), enabled =>
        {
            if (!_services.Autostart.SetEnabled(enabled))
                MessageBox.Show(this,
                    "Could not change the start-up task. DreamTray must be running as " +
                    "administrator to register it.",
                    "DreamTray", MessageBoxButton.OK, MessageBoxImage.Warning);
        });

        var theme = Ui.Combo(
            new[] { ThemePreference.System, ThemePreference.Light, ThemePreference.Dark },
            _services.Theme.Preference,
            AppState.SetThemePreference,
            p => p switch
            {
                ThemePreference.System => "Follow Windows",
                ThemePreference.Light => "Always light",
                _ => "Always dark",
            });

        return Ui.Stack(
            Section("Start-up",
                Ui.LabelRow("Start DreamTray when I sign in", autostart),
                Ui.Caption("Registered as a scheduled task with highest privileges, so it starts " +
                           "elevated without a UAC prompt. Sensor readings and the power limit " +
                           "both need those rights.")),
            Section("Appearance",
                Ui.LabelRow("App theme", theme),
                Ui.Caption("The tray icon always follows the taskbar's own light/dark setting so it " +
                           "matches the network and volume icons.")),
            Section("Files",
                Ui.LabelRow("Settings and log", Ui.Button("Open folder", () =>
                    OpenPath(Settings.SettingsStore.Folder)))));
    }

    private UIElement BuildAnimationsPage()
    {
        var config = _services.Settings.Current.Animations;
        void Save() => _services.Settings.Save();

        // Rebuilding the page on toggle is what greys the duration fields out; the
        // panel itself re-reads these on every open, so nothing needs telling.
        var enabled = Ui.Switch(config.Enabled, v =>
        {
            config.Enabled = v;
            Save();
            Select("animations");
        });

        var openMs = Ui.Number(config.OpenMs, 0, Settings.AnimationSettings.MaxMs,
                               v => { config.OpenMs = v; Save(); });
        var closeMs = Ui.Number(config.CloseMs, 0, Settings.AnimationSettings.MaxMs,
                                v => { config.CloseMs = v; Save(); });

        var reset = Ui.Button("Restore defaults", () =>
        {
            config.Enabled = true;
            config.OpenMs = Settings.AnimationSettings.DefaultOpenMs;
            config.CloseMs = Settings.AnimationSettings.DefaultCloseMs;
            Save();
            Select("animations");
        });

        var timing = Section("Duration",
            Ui.LabelRow("Opening (ms)", openMs),
            Ui.LabelRow("Closing (ms)", closeMs),
            Ui.Caption($"Defaults are {Settings.AnimationSettings.DefaultOpenMs} ms in and " +
                       $"{Settings.AnimationSettings.DefaultCloseMs} ms out, which is about what " +
                       "Windows uses for its own tray flyouts. The panel travels a whole screen " +
                       "height, so much below 200 ms starts to look like a jump rather than a " +
                       "slide. 0 turns that direction off on its own."),
            reset);

        // Disabled rather than hidden: the values stay readable, and the page does not
        // change height when the switch is flipped.
        timing.IsEnabled = config.Enabled;

        return Ui.Stack(
            Section("Panel",
                Ui.LabelRow("Animate the panel", enabled),
                Ui.Caption("The panel slides in from beyond the screen edge and out again the same " +
                           "way, passing under the taskbar. Turned off, it simply appears and " +
                           "disappears at its resting position.")),
            timing);
    }

    private UIElement BuildPowerPage()
    {
        var tdp = _services.Tdp;
        var config = _services.Settings.Current.Tdp;

        if (!tdp.IsAvailable)
        {
            return Ui.Stack(Section("APU power limit",
                Ui.Body("Not available on this machine."),
                Ui.Caption(tdp.StatusText),
                Ui.Caption("DreamTray drives the limit through RyzenAdj. Put libryzenadj.dll and " +
                           "WinRing0x64.sys/.dll in the app's native\\ folder and restart. " +
                           "See native\\README.md.")));
        }

        void Save()
        {
            _services.ApplyTdpSettings();
            _services.Settings.Save();
        }

        var reapply = Ui.Number(config.ReapplySeconds, 0, 3600, v => { config.ReapplySeconds = v; Save(); });
        var acWatts = Ui.Number(config.AcWatts, config.MinWatts, config.MaxWatts,
                                v => { config.AcWatts = v; Save(); });
        var dcWatts = Ui.Number(config.DcWatts, config.MinWatts, config.MaxWatts,
                                v => { config.DcWatts = v; Save(); });
        // Editing a bound by hand is a deliberate choice, so stop the start-up probe
        // from widening it again on the next launch.
        var minWatts = Ui.Number(config.MinWatts, 1, 250,
                                 v => { config.MinWatts = v; config.RangeAutoDetected = false; Save(); });
        var maxWatts = Ui.Number(config.MaxWatts, 1, 250,
                                 v => { config.MaxWatts = v; config.RangeAutoDetected = false; Save(); });

        var useDefaults = Ui.Switch(config.UsePowerSourceDefaults, v =>
        {
            config.UsePowerSourceDefaults = v;
            Save();
            if (v) tdp.ApplyPowerSourceDefault();
        });

        var sections = new List<UIElement>
        {
            Section("Backend", Ui.Caption(tdp.StatusText)),
            Section("Keeping the limit applied",
                Ui.LabelRow("Re-apply every (seconds)", reapply),
                Ui.Caption("OEM utilities rewrite the SMU limits on their own schedule, which silently " +
                           "undoes your setting. Re-applying periodically wins that argument. " +
                           "0 disables it.")),
        };

        if (_services.Hardware.HasBattery)
        {
            sections.Add(Section("Defaults per power source",
                Ui.LabelRow("Switch limit when the charger changes", useDefaults),
                Ui.LabelRow("On charger (W)", acWatts),
                Ui.LabelRow("On battery (W)", dcWatts),
                Ui.Button("Apply the right one now", tdp.ApplyPowerSourceDefault)));
        }

        sections.Add(Section("Slider range",
            Ui.LabelRow("Minimum (W)", minWatts),
            Ui.LabelRow("Maximum (W)", maxWatts),
            Ui.Caption(config.RangeAutoDetected
                ? "Bounds for the TDP widget's slider. These were measured on this machine: the " +
                  "maximum comes from the highest power limit your firmware is configured with, " +
                  "and it is re-checked at every start (it can only go up, in case the first " +
                  "reading happened on battery). Type a value to pin the range instead."
                : "Bounds for the TDP widget's slider, set by hand — automatic detection no longer " +
                  "touches them. Raise the maximum only if you know your cooling can take it.")));

        return Ui.Stack([.. sections]);
    }

    private UIElement BuildPluginsPage()
    {
        var stack = new StackPanel();

        var header = Section("Plugins",
            Ui.Caption("Plugins live in the app's plugins\\ folder, one folder each. They read the " +
                       "same sensor data the widgets do, so a plugin never starts a second " +
                       "hardware-monitoring stack."),
            Ui.Button("Open plugins folder", () => OpenPath(PluginManager.PluginsRoot)));
        stack.Children.Add(header);

        var plugins = _services.Plugins.Plugins;
        if (plugins.Count == 0)
        {
            stack.Children.Add(Section("Installed", Ui.Body("No plugins found.")));
            return stack;
        }

        foreach (var plugin in plugins)
        {
            var toggle = Ui.Switch(_services.Plugins.IsEnabled(plugin.Id), enabled =>
            {
                _services.Plugins.SetEnabled(plugin.Id, enabled);
                Select("plugins"); // re-read state, including any start-up error
            });

            var children = new List<UIElement>
            {
                Ui.Row(Ui.Stack(
                    Ui.Body(plugin.Name),
                    Ui.Caption($"{plugin.Instance.Description}  ·  v{plugin.Instance.Version}")), toggle),
            };

            if (plugin.Error != null)
            {
                var error = Ui.Caption($"Error: {plugin.Error}");
                error.Foreground = Application.Current?.TryFindResource("DangerBrush") as Brush;
                children.Add(error);
            }

            if (_services.Plugins.IsEnabled(plugin.Id))
            {
                FrameworkElement? settings = null;
                try { settings = plugin.Instance.CreateSettingsView(); }
                catch (Exception ex) { Logging.Log.Write($"plugin settings failed: {ex}"); }

                if (settings != null)
                {
                    children.Add(Ui.Separator());
                    children.Add(settings);
                }
            }

            stack.Children.Add(Section(plugin.Name, children.ToArray()));
        }
        return stack;
    }

    private UIElement BuildAboutPage()
    {
        string version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString() ?? "1.0";

        return Ui.Stack(
            Section("DreamTray",
                Ui.Body($"Version {version}"),
                Ui.Caption($"Running elevated: {Startup.AutostartService.IsElevated}"),
                Ui.Caption("Sensor data comes from LibreHardwareMonitor plus native performance " +
                           "counters; brightness from the ACPI backlight interface and DDC/CI; " +
                           "the power limit from RyzenAdj.")),
            Section("Diagnostics",
                Ui.Caption("Run DreamTray.exe --dump from a console to print every detected sensor " +
                           "and the state of each backend. That is the fastest way to find out why " +
                           "a reading is missing."),
                Ui.LabelRow("Log file", Ui.Button("Open", () => OpenPath(Logging.Log.FilePath)))));
    }

    private static void OpenPath(string path)
    {
        try
        {
            Logging.Log.Flush();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Logging.Log.Write($"could not open {path}: {ex.Message}"); }
    }

    private void OnThemeChanged()
    {
        WindowEffects.SetDarkMode(this, _services.Theme.IsDark);
        if (Background is SolidColorBrush { Color.A: 255 })
            Background = Application.Current?.TryFindResource("WindowBackground") as Brush;
    }

    protected override void OnClosed(EventArgs e)
    {
        _services.Theme.Changed -= OnThemeChanged;
        base.OnClosed(e);
    }
}
