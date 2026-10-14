using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DreamTray.Display;

/// <summary>
/// Reports the moments monitors are likely to have lost a brightness set over DDC/CI:
/// the machine resuming from sleep or hibernation, the session being unlocked, and
/// the displays powering back on after the idle timeout turned them off (which is
/// not a sleep, so <see cref="SystemEvents.PowerModeChanged"/> never hears of it).
///
/// <see cref="Woke"/> is raised on a background thread and may fire more than once
/// for one wake — a resume also turns the displays on — so handlers must be cheap
/// and idempotent.
/// </summary>
public sealed class DisplayWakeWatcher : IDisposable
{
    private readonly Action<string> _log;
    /// <summary>Kept in a field: the native side calls it for as long as we are registered.</summary>
    private readonly DeviceNotifyCallback _callback;
    private nint _subscribeParams;
    private nint _registration;
    /// <summary>Last console display state: 0 off, 1 on, 2 dimmed; -1 before the first report.</summary>
    private int _displayState = -1;

    public event Action<string>? Woke;

    public DisplayWakeWatcher(Action<string> log)
    {
        _log = log;
        _callback = OnPowerSetting;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        RegisterDisplayState();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Raise("resume from sleep");
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect)
            Raise("session unlocked");
    }

    private void Raise(string reason)
    {
        var handler = Woke;
        if (handler == null) return;
        // Off the thread that told us: SystemEvents runs its handlers one after
        // another on its own window thread, and the power callback is a system thread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { handler(reason); }
            catch (Exception ex) { _log($"display wake handler threw: {ex.Message}"); }
        });
    }

    // ---------------------------------------------------------------- display power

    /// <summary>
    /// GUID_CONSOLE_DISPLAY_STATE. Registered with a callback rather than a window
    /// message, so this works without the app owning a top-level window.
    /// </summary>
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private void RegisterDisplayState()
    {
        try
        {
            var parameters = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
            {
                Callback = Marshal.GetFunctionPointerForDelegate(_callback),
                Context = nint.Zero,
            };
            _subscribeParams = Marshal.AllocHGlobal(Marshal.SizeOf<DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS>());
            Marshal.StructureToPtr(parameters, _subscribeParams, false);

            var guid = ConsoleDisplayState;
            uint err = PowerSettingRegisterNotification(ref guid, DEVICE_NOTIFY_CALLBACK,
                                                        _subscribeParams, out _registration);
            if (err != 0)
            {
                _registration = nint.Zero;
                _log($"display power notifications unavailable (error {err})");
            }
        }
        catch (Exception ex)
        {
            _registration = nint.Zero;
            _log($"display power notifications unavailable: {ex.Message}");
        }
    }

    private uint OnPowerSetting(nint context, uint type, nint setting)
    {
        try
        {
            if (type != PBT_POWERSETTINGCHANGE || setting == nint.Zero) return 0;
            // POWERBROADCAST_SETTING: GUID (16 bytes), DWORD DataLength, then the data.
            if (Marshal.PtrToStructure<Guid>(setting) != ConsoleDisplayState) return 0;
            if (Marshal.ReadInt32(setting, 16) < 4) return 0;
            int state = Marshal.ReadInt32(setting, 20);
            int previous = Interlocked.Exchange(ref _displayState, state);
            // Registration reports the current state straight away; that is not a
            // wake. Dimmed → on is not one either: the monitor never powered down.
            if (previous == 0 && state == 1) Raise("display turned on");
        }
        catch { /* never let an exception unwind into the power manager */ }
        return 0;
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (_registration != nint.Zero)
        {
            PowerSettingUnregisterNotification(_registration);
            _registration = nint.Zero;
        }
        if (_subscribeParams != nint.Zero)
        {
            Marshal.FreeHGlobal(_subscribeParams);
            _subscribeParams = nint.Zero;
        }
    }

    // ---------------------------------------------------------------- interop

    private const uint DEVICE_NOTIFY_CALLBACK = 2;
    private const uint PBT_POWERSETTINGCHANGE = 0x8013;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceNotifyCallback(nint context, uint type, nint setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        public nint Callback;
        public nint Context;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingRegisterNotification(ref Guid settingGuid, uint flags,
                                                                nint recipient, out nint registrationHandle);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingUnregisterNotification(nint registrationHandle);
}
