# native\ — RyzenAdj drop-in

The APU power-limit slider talks to the AMD SMU through **RyzenAdj**, which is not
redistributed here. Without these files DreamTray still runs; the TDP widget just
reports that the backend is unavailable and hides itself from the widget picker.

## What to put here

From a [RyzenAdj](https://github.com/FlyGoat/RyzenAdj) release archive (win64):

```
native\libryzenadj.dll
native\WinRing0x64.dll
native\WinRing0x64.sys
```

`libryzenadj.dll` is the API DreamTray binds to. `WinRing0x64.sys` is the kernel
driver it loads to reach the SMU mailbox, and `WinRing0x64.dll` is the user-mode
half.

Put all three in this folder and DreamTray sorts out the rest. It has to, because
WinRing0 does not look for them here: `WinRing0x64.dll` is resolved by the normal
loader search (next to the **exe**, or on PATH), and `WinRing0x64.sys` is located
by WinRing0 itself from `GetModuleFileName(NULL)` — again the **exe's** directory,
with no way to redirect it. So at start-up DreamTray adds this folder to the DLL
search path and copies the two WinRing0 files up next to `DreamTray.exe`. If you
would rather do it by hand, putting them next to the exe yourself works too.

## Why administrator is required

The SMU mailbox is only reachable from kernel mode, so RyzenAdj loads a driver.
That needs administrator rights, which is why DreamTray's manifest requests
elevation. This is the same requirement LibreHardwareMonitor has for AMD CPU
temperature and power, so one elevation covers both.

## Checking it worked

Run from a console:

```
DreamTray.exe --dump
```

The `=== TDP ===` section prints either `RyzenAdj ready (…)` plus a live readback
of the STAPM/fast/slow limits, or the reason it could not initialise.

## A note on limits

DreamTray writes the STAPM, slow and fast limits together. OEM power software
often lowers only one of the three, and the lowest one wins — setting a single
limit is why "my TDP tool does nothing" happens. The fast limit is given a little
headroom above the sustained value so short bursts still behave normally.
