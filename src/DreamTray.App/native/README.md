# native\ — optional PawnIO module override

**In the normal case you do not need to put anything here.** DreamTray reaches the
AMD SMU through the [PawnIO](https://pawnio.eu) driver, and the module blob it
needs already ships inside LibreHardwareMonitor, which DreamTray depends on.

What you *do* need once, on the machine, is the PawnIO driver itself.

## Install PawnIO

Grab the installer from [pawnio.eu](https://pawnio.eu) (source:
[namazso/PawnIO](https://github.com/namazso/PawnIO)) and run it. It installs a
signed kernel driver as a proper INF-based device — nothing to copy by hand, and
nothing for DreamTray to drop next to its own executable.

That is the whole setup. Start DreamTray elevated and the TDP slider appears.

## Why PawnIO and not RyzenAdj

DreamTray used to drive the SMU through RyzenAdj, which reaches it with
**WinRing0**. WinRing0 hands any user-mode caller unrestricted MSR and physical
memory read/write (CVE-2020-14979). That is a general privilege-escalation
primitive, so:

- it is on Microsoft's vulnerable-driver blocklist, and current Defender builds
  flag `WinRing0x64.sys` as a HackTool;
- every current anti-cheat — Easy Anti-Cheat, BattlEye, Vanguard — refuses to run
  while a process holds it. Launching a protected game with the old DreamTray
  running produced `Game Security Violation Detected (0x0000000D) [DreamTray.exe]`.

PawnIO solves this properly rather than hiding it. Instead of exposing raw ring-0
access, it runs **signed Pawn bytecode modules** inside the kernel and surfaces
only the ioctls those modules declare. The `RyzenSMU` module can drive the SMU
mailbox and nothing else, so there is no primitive for anti-cheat to object to.
This is the same move LibreHardwareMonitor made in 0.9.5, and the same one UXTU
uses for its power control.

## Overriding the module blob

DreamTray looks for the `RyzenSMU` module in this order:

```
native\RyzenSMU.bin        <- this folder, if present
RyzenSMU.bin               <- next to DreamTray.exe, if present
(embedded in LibreHardwareMonitorLib)
```

So dropping `RyzenSMU.bin` here overrides the bundled copy. That is worth doing
only if you need a newer module than the one LHM shipped with — for example when
support for a brand-new APU has landed upstream. Signed builds come from
[namazso/PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) releases.

A blob PawnIO will not accept is reported as such rather than silently ignored.

## Why administrator is required

PawnIO's device object is admin-only, so DreamTray's manifest requests elevation.
LibreHardwareMonitor needs the same driver for AMD CPU temperature and power, so
one elevation covers both — and both now go through PawnIO, meaning a normal
DreamTray run loads no blocklisted driver at all.

Note that PawnIO is compatible with Memory Integrity (Core isolation). The old
WinRing0 path was not, and the previous version of this file told you to turn it
off; you can turn it back on.

## Checking it worked

Run from a console:

```
DreamTray.exe --dump
```

The `=== TDP ===` section prints either

```
PawnIO ready (Phoenix, PM table 004C0007)
```

plus a live readback of the STAPM/fast/slow limits, or the reason it could not
initialise — PawnIO not installed, not elevated, or a processor whose SMU command
set DreamTray does not have mapped.

## A note on limits

DreamTray writes the STAPM, slow and fast limits together. OEM power software
often lowers only one of the three, and the lowest one wins — setting a single
limit is why "my TDP tool does nothing" happens. The fast limit is given a little
headroom above the sustained value so short bursts still behave normally.

The SMU command ids for those three limits are per-generation and follow
[RyzenAdj](https://github.com/FlyGoat/RyzenAdj)'s mapping in `lib/api.c`, which
remains the reference for this hardware even though its driver is no longer used.
A processor outside that mapping disables the feature rather than guessing, since
an unrecognised SMU command is not a harmless no-op.
