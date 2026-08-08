# DreamTray

One Windows tray app for the things you actually reach for day to day: display
brightness (built-in panel *and* external monitors), the APU power limit, live
power draw, and the light/dark switch — behind a Windows 11-style flyout made of
widgets you can rearrange, remove and configure individually.

It also hosts **plugins**, so background tools that need system metrics can live
here instead of running their own tray icon and their own copy of
LibreHardwareMonitor.

Nothing in it is written for one particular machine: every panel probes what it is
running on and hides itself when the hardware isn't there. See
[Supported systems](#supported-systems) for what that means in practice.

---

## Supported systems

**Required**

| | |
|---|---|
| OS | Windows 11, and Windows 10 1809 or newer. x64 only — the build is pinned to x64 and the TDP driver has no ARM64 build. |
| Runtime | .NET 8 Desktop Runtime. |
| Rights | Administrator for CPU temperature, package power and the TDP slider. Everything else works unelevated. |

Rounded corners and the Mica/acrylic backdrop are Windows 11 features; on
Windows 10 the flyout falls back to a plain opaque window and everything else
behaves identically.

**Per-feature support** — each widget probes for its own backend and is absent
from the picker when the machine can't do it, so an unsupported feature costs a
missing widget, not an error.

| Feature | Works on |
|---|---|
| APU power limit (TDP) | AMD Ryzen only, Raven Ridge and newer — the SMU command set is mapped per generation and an unrecognised part disables the slider rather than guessing. Needs the [PawnIO](https://pawnio.eu) driver (below) and administrator rights; Memory Integrity can stay on. Intel is not supported; there is no equivalent backend. The slider's range is measured from your own firmware on first launch, not assumed. |
| Temperatures, clocks, load, component power | Anything LibreHardwareMonitor can read — Intel and AMD CPUs, NVIDIA/AMD/Intel GPUs. Individual rows read "—" where a chip exposes no such sensor. The GPU/SoC power row is derived as package-minus-cores, which is meaningful on an APU and less so on a system with a discrete GPU. |
| Brightness | Built-in laptop panels via the ACPI/WMI backlight interface; external monitors via DDC/CI, which most desktop monitors support over DisplayPort/HDMI but some refuse. |
| Battery, and every "on battery / on charger" rule | Laptops and tablets. **On a desktop these do not appear at all** — no Battery widget, no battery row in Component power, no per-power-source TDP defaults, no automatic theme or refresh-rate switching. |
| Sleep timeout | Any machine with a readable Windows power plan. The lid-close control appears only where a lid exists. |
| Resolution & refresh rate | Any display the graphics driver enumerates modes for. |

**Tested on** a Ryzen 7 7840HS + Radeon 780M laptop under Windows 11. Other
configurations are supported by construction rather than by testing — if
something reads wrong on yours, `DreamTray.exe --dump` prints exactly what was
and wasn't detected.

---

## What you get

**Main panel** — click the tray icon.

| Widget | What it does |
|---|---|
| Brightness | One slider per display. Built-in panel via the ACPI backlight interface, external monitors via DDC/CI. Optional "move all together". |
| APU power limit | Sustained TDP slider showing live system and APU draw underneath. The range is detected from your own silicon. Settings: re-apply interval and, on a laptop, separate charger/battery defaults. |
| Theme | Flips the **Windows** light/dark setting. On a laptop, optionally go light on battery and back to dark on the charger. |
| Battery *(laptops only)* | Charge level, power source, time to empty or full. |
| Temperatures | CPU and GPU. |
| Clocks | CPU average/peak core clock, GPU clock. |
| Component power | CPU cores, GPU/SoC, whole package, and — on a laptop — the battery charge/discharge rate. |
| Load | CPU, GPU, memory. |
| Sleep | Standby idle timeout and the lid-close action, read and written on the active Windows power plan. Shows the AC or battery half depending on what you are running on, and follows the charger. |
| Resolution & refresh rate | Per-display mode picker. On a laptop, optionally switch to a chosen resolution and refresh rate when the charger comes or goes. Modes that do not match the panel's aspect ratio can be shown, faded, or left out. |

Plugins can add widgets of their own; the bundled one is listed under
[Writing a plugin](#writing-a-plugin).

Widgets are **drag-reorderable** (the ✎ button turns on edit mode), removable, and
re-addable from the **+** picker. Order and per-widget settings persist in
`%APPDATA%\DreamTray\settings.json`.

**Settings window** — start-at-logon, app theme, the full TDP policy, and the
plugin list with each plugin's own settings page.

---

## Install & run

Requires the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
(or the SDK) to build.

```bash
publish.bat
```

That produces `dist\DreamTray.exe` plus the `plugins\` and `native\` folders.
Run it and a gear appears in the notification area.

**It asks for administrator rights, and it needs them.** CPU temperature, CPU/GPU
package power and the SMU power limit are all kernel-level interfaces — the same
reason LibreHardwareMonitor and HWiNFO ask. Everything else (brightness, display
modes, battery, theme, GPU) works unelevated, so a denied prompt degrades rather
than breaks.

Turn on **Settings → General → Start DreamTray when I sign in**. That registers a
Task Scheduler logon task with highest privileges, which starts elevated *without*
a UAC prompt at every logon — a Run-key shortcut cannot do that.

### TDP control needs the PawnIO driver

Install [PawnIO](https://pawnio.eu) once — that is the whole setup, no files to
copy. The SMU module blob ships inside LibreHardwareMonitor, which DreamTray
already depends on. See
[`src/DreamTray.App/native/README.md`](src/DreamTray.App/native/README.md) for
the details and for how to override the bundled module.

Without PawnIO the TDP widget hides itself and everything else works.

PawnIO replaced WinRing0 here deliberately: WinRing0 is on Microsoft's
vulnerable-driver blocklist, and anti-cheats (Easy Anti-Cheat, BattlEye,
Vanguard) refuse to run alongside a process holding it. PawnIO runs signed
kernel-side modules that expose only the SMU mailbox, so DreamTray can stay
running while you play.

### Diagnostics

```bash
DreamTray.exe --dump
```

Prints every sensor LibreHardwareMonitor can see (with the raw names the mapping
code keys off), the enumerated displays and their brightness, the display modes,
and the state of the PawnIO/SMU backend. This is the first thing to run when a
reading is missing.

```bash
DreamTray.exe --tdp-probe 22
```

Writes the given limit and then samples the power table on a tight schedule, to
answer the question a stuck TDP slider raises: did the write never take effect, or
did it take effect and then get overwritten? A single before/after reading cannot
tell those apart. Phase 1 writes once and watches; phase 2 re-applies every 250 ms
to see whether a faster loop would hold the limit against something re-asserting
it. Needs elevation, and the tray app closed — it holds the SMU handle.

```bash
DreamTray.exe --selftest
```

Builds every window and every widget, forces a layout pass, validates that every
widget's icon code point exists in the installed Fluent font, and reports
pass/fail. Worth running after any UI change — a tray app's UI otherwise only gets
exercised when a human clicks the icon. Add `--screenshot <dir>` to also write
`panel.png` and `settings.png` for a look at the layout without opening anything.

---

## Why it costs nothing when idle

The whole design goal is that a tray app you never look at should not show up in
Task Manager.

- **One sampler, reference-counted.** `SensorSampler` is the only thing that reads
  hardware. Widgets and plugins subscribe to it; it ticks at the fastest interval
  anyone asked for. With **zero** subscribers the worker thread blocks on an event
  and the `SensorService` is disposed entirely — the LibreHardwareMonitor kernel
  driver is released, not merely left idle.
- **Closed panel = no subscribers.** Widgets subscribe on show and unsubscribe on
  hide. A closed panel polls nothing at all.
- **Background rules are the explicit exception.** A widget with a rule that must
  run while closed (auto-TDP on charger change, auto-light-theme on battery)
  declares `WantsBackgroundWork`, and the manager keeps exactly one shared 5-second
  subscription for that whole set.
- **No WinForms.** The tray icon is `Shell_NotifyIcon` from a message-only window,
  and the icon bitmap is rendered from the Segoe Fluent gear glyph at runtime.
  Pulling `System.Windows.Forms` into a WPF process for one 16-pixel icon costs
  several MB of working set.
- **Workstation, non-concurrent GC**, no background JIT churn.
- **DWM does the visuals.** Acrylic/Mica backdrops and rounded corners are window
  attributes, not app-side blur — no continuous GPU work.

---

## Layout

```
src/
  DreamTray.Contracts/   The plugin ABI: interfaces + plain data, nothing else.
  DreamTray.Core/        Hardware access, settings, plugin loader. No app UI.
    Sensors/             SensorService (LHM) + PDH/NT readers + SensorSampler.
    Display/             BrightnessService (WMI + DDC/CI), DisplayModeService.
    Power/               PawnIO/SMU interop, TdpService (limit + policy),
                         PowerPolicyService (standby timeout, lid action).
    Theme/               Windows theme tracking and switching.
    Startup/             Logon task registration.
    Plugins/             Discovery and per-plugin AssemblyLoadContexts.
    Settings/            The JSON document and per-owner storage scopes.
  DreamTray.App/         WPF: tray icon, panel, widgets, settings window.
    Interop/             Shell_NotifyIcon, DWM effects, runtime icon rendering.
    Themes/              Win11 tokens (code) + control styles (XAML).
    Widgets/BuiltIn/     The built-in widgets.
    Views/               Panel, settings, widget chrome.
plugins/
  DreamTray.Plugin.CyberVfd/   Bundled plugin; staged into the app's plugins\.
```

### Where the sensor code came from

`Sensors/` is ported from the CyberVFD `pc_agent`. The parts that matter and the
reasons they exist:

- **Per-thread CPU load** via `NtQuerySystemInformation`, not PDH — no localized
  counter names to break on a non-English Windows.
- **CPU clocks** via `\Processor Information(*)\% Processor Performance` × base MHz.
  LibreHardwareMonitor reports NaN per-core clocks on several AMD mobile parts;
  this counter reflects boost, matches Task Manager, and works everywhere.
- **Disk activity** via `\PhysicalDisk(*)\% Idle Time`, whose instance names carry
  the drive letters, which is how the system drive is identified.
- **CPU vs GPU power on an APU**: CPU power is the sum of the per-core SMU sensors;
  GPU power is *package minus cores*. LibreHardwareMonitor's "GPU Core" power
  sensor is mislabeled here and tracks CPU load; the subtraction does not.
- All PDH counters are added with `PdhAddEnglishCounter` for the same locale reason.

---

## Writing a plugin

Reference `DreamTray.Contracts` only, implement `IDreamPlugin` (or derive from
`DreamPluginBase`), and drop the build output in `plugins\<yourplugin>\`.

```csharp
public sealed class MyPlugin : DreamPluginBase
{
    private IDisposable? _subscription;

    public override string Id => "myplugin";       // stable: it is the settings key
    public override string Name => "My plugin";

    public override void Start()
    {
        _subscription = Host.SubscribeSensors(TimeSpan.FromSeconds(1), snapshot =>
        {
            Host.Log($"CPU {snapshot.CpuTemp:F0} °C, {snapshot.SystemPower:F1} W");
        });
    }

    public override void Stop() { _subscription?.Dispose(); _subscription = null; }
}
```

`IPluginHost` gives you the shared `SystemSnapshot` feed, persistent `Storage`,
`Hardware` (brightness, TDP, display modes, theme), the current theme, logging and
tray notifications. Return `IWidgetFactory` instances from `Widgets` to put
controls on the main panel, and a `FrameworkElement` from `CreateSettingsView()`
for a page in the Settings window.

Plugin UI can use the host's control styles by resource key — `BodyText`,
`CaptionText`, `ValueText`, `Card`, `FluentButton`, `ToggleSwitch`, `FluentSlider`,
`FluentComboBox`, `FluentCheckBox`, `FluentTextBox` — and it will match the
built-in widgets and follow light/dark automatically. See
`plugins/DreamTray.Plugin.CyberVfd/PluginUi.cs`.

Each plugin folder is loaded in its own `AssemblyLoadContext` with a resolver
rooted at that folder, so two plugins can depend on different versions of the same
library. `DreamTray.Contracts` is deliberately resolved from the host so the shared
interfaces are the same types on both sides.

### The bundled plugin

`plugins/DreamTray.Plugin.CyberVfd/` is a complete worked example — sensor
subscription, a background worker, persisted settings, a widget and a settings
page. See
[its README](plugins/DreamTray.Plugin.CyberVfd/README.md) for what it does and how
to set it up.
