# CyberVFD plugin

Streams live system metrics to the **CyberVFD** — an ESP32-C3 driving a GP1247AI
vacuum-fluorescent panel — over USB serial at 1 Hz, and puts the panel's power
switch on DreamTray's main panel.

It replaces the standalone `pc_agent` / `CyberVfdAgent` tray app from the CyberVFD
project. The discovery handshake and the wire format are unchanged, so **existing
firmware works as-is** — the difference is that the metrics now come from
DreamTray's shared sampler, so the machine runs one hardware-monitoring stack and
one tray icon instead of two of each.

## Requirements

The ESP32-C3 / GP1247AI panel it was written for. Without one the plugin loads,
finds nothing, and sits in "searching for the device…" — so it ships **disabled**.

## Setup

1. Build DreamTray (`publish.bat` at the repo root); the plugin is staged into
   `dist\plugins\CyberVfd\`.
2. Plug the panel in.
3. **Settings → Plugins → CyberVFD display** → enable.
4. If you were running the old standalone agent, uninstall it (`uninstall.bat` in
   that project). Two processes fight over the COM port, and two copies of
   LibreHardwareMonitor load the same kernel driver.

## Settings page

| Control | Effect |
|---|---|
| Serial port | `Auto-detect` (default) handshakes every COM port; or pin a specific one. |
| Panel power | Master off cuts the high-voltage supply and the backlight relay — the panel draws nothing and DreamTray stops sending data frames. |
| Backlight | Backlight relay only. |
| Panel brightness | The panel's hardware dimming register, 0–255, shown as a percentage. |
| Re-scan ports | Re-enumerate COM ports after plugging the device in. |

Every change is sent immediately, persisted under the `cybervfd` key in
`%APPDATA%\DreamTray\settings.json`, and re-sent on the next connect — so the
panel always matches what the page shows, even across a firmware reset.

The **CyberVFD** widget on the main panel mirrors the master power switch and the
connection status, so the display can be silenced without opening Settings.

## How it works

**Discovery** ([`SerialLink.cs`](SerialLink.cs)) — the device is identified by
handshake: send `CVFD?`, expect a line containing `CVFD1`. Not by COM number,
which Windows reassigns freely, and not by VID/PID, which would also match
unrelated boards using the same USB-serial bridge. Ports that don't answer are
closed again untouched. 115200 baud, DTR and RTS asserted (steady DTR won't reset
the C3).

**Wire format** ([`VfdFrame.cs`](VfdFrame.cs)) — one newline-terminated ASCII
frame per update, `.`-decimal, `|`-separated, starting with `D`: time, date, CPU
temp/clocks/power, RAM, GPU, VRAM, net, disks, battery, then exactly sixteen
per-thread load values (padded or truncated to that width). The firmware drops
frames with the wrong field count, so this must match `applyPacket` in
`src/graphics/renderers/vfd_monitor_renderer.h` exactly — a mismatched agent goes
dark rather than showing wrong numbers. Control frames are `C|PWR|`, `C|BL|`,
`C|BR|`.

**Threading** ([`CyberVfdPlugin.cs`](CyberVfdPlugin.cs)) — all serial I/O runs on
a dedicated `cybervfd-link` thread; a wedged COM port cannot stall the UI. The
plugin subscribes to the host sampler at 1 Hz, but the frame cadence is the
worker's own clock, not the callback: a frame goes out every second, repeating the
last sample if no new one arrived. Samples reach the plugin through the UI
dispatcher, so anything that stalls the sampler or the UI thread for more than the
firmware's 5-second watchdog would otherwise blank the tube and light it again on
the next sample — a stale metric for a second beats a display that flickers. Only
the clock field is always current. Control frames go out ahead of the data frame,
and undelivered ones are re-queued across a reconnect. With the panel powered off
no data frame is sent, and the firmware powers the tube down on its own.

Failed connects back off (1 s → 2 s → 5 s → 10 s) instead of retrying on a fixed
tick, and each port gets a ~1.8 s handshake window with the probe repeated through
it. Opening a port re-enumerates a USB-CDC device, so a scan that retries too
eagerly resets the panel just as it finishes booting and never converges.

## Layout

| File | |
|---|---|
| `CyberVfdPlugin.cs` | Lifecycle, persisted settings, the link worker thread. |
| `SerialLink.cs` | Port enumeration, handshake, send, teardown. |
| `VfdFrame.cs` | Frame builder — data and control. |
| `CyberVfdWidget.cs` | The main-panel widget. |
| `CyberVfdSettingsView.cs` | The Settings-window page. |
| `PluginUi.cs` | Thin helpers over the host's control styles. |

The project references `DreamTray.Contracts` with `Private="false"` so the
contracts assembly comes from the host — otherwise the plugin's `IDreamPlugin`
would be a different type than the one the loader checks against. Its own
dependency (`System.IO.Ports`) is copied into the plugin folder, which the host's
per-plugin `AssemblyLoadContext` resolves from.

## Troubleshooting

- **"searching for the device…"** — nothing answered the handshake. Check the
  panel is powered and enumerated, then **Re-scan ports**; pin the port manually
  if auto-detect keeps missing it.
- **"link lost"** — the port went away mid-write; the worker reconnects on its
  own, backing off up to 10 s between attempts.
- **Panel flickering on and off after a hot plug or a reboot** — that is the
  handshake never converging (the log shows repeated `cybervfd: connected on …`),
  or the frame stream stalling (`sensors: … ms since the last delivered sample`).
- **Panel dark, but connected** — check Panel power and Backlight; brightness at
  0% is also dark.
- **Panel blanks after ~5 s** — no data frames are arriving. That is the firmware
  watchdog, and it means the frame is being rejected: usually a field-count
  mismatch between this build and the firmware.
