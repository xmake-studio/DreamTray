# CyberLCD plugin

Streams live system metrics to the **CyberLCD** -- an RP2040 driving a
316x55 inverted 1-bit LCD panel with two addressable ARGB strips -- over USB
serial at 1 Hz, and puts the panel's power switch on DreamTray's main panel.

It is this device's only PC-side agent (there is no standalone `pc_agent`
for CyberLCD, unlike the older CyberVFD project) -- one hardware-monitoring
stack, one tray icon.

## Requirements

The RP2040 / 316x55 panel it was written for (see the `CyberLCD` PlatformIO
project). Without one the plugin loads, finds nothing, and sits in
"searching for the device…" -- so it ships **disabled**.

## Setup

1. Build DreamTray (`publish.bat` at the repo root), or `dotnet build` this
   plugin's `.csproj` directly for local testing.
2. Plug the panel in.
3. **Settings → Plugins → CyberLCD display** → enable.

## Settings page

| Control | Effect |
|---|---|
| Serial port | `Auto-detect` (default) handshakes every COM port; or pin a specific one. |
| Panel power | Master off blanks the panel and its backlight strip. Does **not** touch the desk strip -- see below. |
| Display brightness | Brightness of the display's own backlight strip (behind the panel), 0-255 shown as a percentage. |
| Desk strip brightness | Brightness of the external desk-perimeter strip, 0-255 shown as a percentage. |
| **Display backlight** mode | `Temperature` (default) -- a live gradient from the CPU's colour (left) to the GPU's colour (right), blue when cool through red when hot, same technique as `ProjectHello`'s live temperature coloring. `Solid` -- one color, picked below. `Gradient` -- any number of position+color stops, picked below. `Rainbow` -- a continuously scrolling hue cycle, with Speed (deg/s) and Width (LEDs per cycle) sliders. |
| **Desk strip** mode | `Solid`, `Gradient`, or `Rainbow` (no Temperature option -- it's not attached to anything with a temperature). |
| LED count | How many LEDs are actually wired into the desk strip. |
| Re-scan ports | Re-enumerate COM ports after plugging the device in. |

Every change is sent immediately, persisted under the `cyberlcd` key in
`%APPDATA%\DreamTray\settings.json`, and re-sent on the next connect -- so
the device always matches what the page shows, even across a firmware reset.

**The desk strip is independent of the PC link on purpose.** It is ambient
desk lighting, not a status indicator, so unlike the display backlight it is
never blanked by Panel power or by the link dropping -- and the firmware
itself (not this plugin) saves its mode/color/gradient/rainbow-params/
brightness/LED-count to the device's own flash, restoring and smoothly
fading it back in at boot even if DreamTray never connects. This plugin's
own persisted settings and the firmware's flash copy are independent stores
that happen to agree once a change round-trips through the link -- if you
edit the desk strip while disconnected there is nothing to edit *into* (the
control just has no device to talk to), so in practice the flash copy only
ever changes via this same settings page.

The **CyberLCD** widget on the main panel mirrors the master power switch
and the connection status, so the device can be silenced without opening
Settings.

## How it works

**Discovery** ([`SerialLink.cs`](SerialLink.cs)) -- the device is identified
by handshake: send `CLCD?`, expect a line containing `CLCD1`. A different
magic than CyberVFD's (`CVFD1`) so the two plugins never pick up each
other's device. 115200 baud, DTR and RTS asserted.

**Wire format** ([`CyberLcdFrame.cs`](CyberLcdFrame.cs)) -- one
newline-terminated ASCII frame per update, `.`-decimal, `|`-separated,
starting with `D`: time, date, CPU temp/clocks/power, RAM, GPU (including
GPU power, standing in for a VRAM clock DreamTray doesn't track), VRAM,
net, then the **two busiest physical drives right now** (each as busy-%
plus its own dynamic label -- not a fixed C:/D: pair, and not read/write
KB/s, which DreamTray doesn't track), then exactly twenty per-thread load
values. The firmware drops frames with the wrong field count, so this must
match `applyPacket` in `src/graphics/renderers/lcd_monitor_renderer.h`
exactly.

Control frames: `C|PWR|`, `C|DBR|`/`C|KBR|` (display/desk brightness),
`C|DM|`/`C|KM|` (display/desk RGB mode -- `T` for Temperature, `S|RRGGBB`
for Solid, `G|pos:RRGGBB,pos:RRGGBB,...` for Gradient, `R|speed|width` for
Rainbow), `C|KN|` (desk LED count). See `SerialIngest::handleControl` in
`src/serial_ingest.h`.

**Threading** ([`CyberLcdPlugin.cs`](CyberLcdPlugin.cs)) -- all serial I/O
runs on a dedicated `cyberlcd-link` thread; a wedged COM port cannot stall
the UI. The plugin subscribes to the host sampler at 1 Hz, but the frame
cadence is the worker's own clock: a frame goes out every second, repeating
the last sample if no new one arrived (only the clock field is always
current). Control frames go out ahead of the data frame, and undelivered
ones are re-queued across a reconnect. Failed connects back off
(1s → 2s → 5s → 10s) instead of retrying on a fixed tick.

**RGB controls** ([`PluginUi.cs`](PluginUi.cs)) -- `ColorSwatch` is a small
self-built HSL popup (hue/sat/lightness sliders + hex entry) rather than a
WinForms `ColorDialog`, since nothing in DreamTray needed a color picker
before this plugin. `GradientStopEditor` is a position+color row list with
add/remove, used by both strips' Gradient mode. Rainbow's Speed/Width are
two plain sliders.

**Busiest-two-drives** ([`CyberLcdFrame.cs`](CyberLcdFrame.cs):`BusiestTwoDisks`)
-- reads `SystemSnapshot.DiskLoads`/`DiskLabels` (every physical drive,
sorted busiest-first by `DiskLoadReader.ReadAll()` in
`DreamTray.Core/Sensors/LowLevelReaders.cs`) and sends the top two,
whichever they are. Falls back to the older `Disk0Load`/`Disk1Load` pair
("system drive + one other") if `DiskLoads` is empty, so this still works
against an older host build.

**Desk strip flash persistence** -- this plugin does not implement it; the
firmware does (`FlashStore` in `src/flash_store.h/.cpp`, raw pico-sdk
`hardware_flash` with interrupts disabled around the erase+program, the
documented single-core-safe pattern -- mbed's `FlashIAP` has no backing
implementation for this board). The plugin's only involvement is sending the
same `C|KM`/`C|KBR`/`C|KN` commands it always did; the firmware decides when
to actually write flash (debounced, a few seconds after the desk strip's
configuration stops changing, so dragging a slider does not hammer it).

## Layout

| File | |
|---|---|
| `CyberLcdPlugin.cs` | Lifecycle, persisted settings, the link worker thread, RGB mode/color state. |
| `SerialLink.cs` | Port enumeration, handshake, send, teardown. |
| `CyberLcdFrame.cs` | Frame builder (data + control) and the hex/gradient-stop (de)serialization shared with settings persistence. |
| `CyberLcdWidget.cs` | The main-panel widget. |
| `CyberLcdSettingsView.cs` | The Settings-window page. |
| `PluginUi.cs` | Host-style control helpers, plus `ColorSwatch` and `GradientStopEditor`. |

The project references `DreamTray.Contracts` with `Private="false"` so the
contracts assembly comes from the host. Its own dependency
(`System.IO.Ports`) is copied into the plugin folder, which the host's
per-plugin `AssemblyLoadContext` resolves from.

## Troubleshooting

Same failure modes as the CyberVFD plugin -- see its README for the general
shape (searching/link lost/backing off/hot-plug flicker). Specific to this
device: **panel blank but strips lit** usually means Panel power is on but
no data frame has arrived yet (nothing to draw); **strips not tracking
load/temperature** means the mode is Solid/Gradient rather than
Temperature, or (for the desk strip) that mode doesn't exist there by
design.
