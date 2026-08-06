using System.Diagnostics;
using System.Security.Principal;

namespace DreamTray.Startup;

/// <summary>
/// Start-at-logon support.
///
/// A Run-key shortcut is not enough here: DreamTray needs administrator rights for
/// the sensor driver and the SMU mailbox, and anything launched from the Run key
/// either gets a UAC prompt at every logon or silently starts unelevated. So this
/// registers a Task Scheduler task with "run with highest privileges", which starts
/// elevated and prompt-free.
///
/// The task XML is written out and handed to <c>schtasks /Create /XML</c>, because
/// the flags that matter (run on battery, no idle requirement, no execution time
/// limit) cannot be expressed with schtasks' command-line switches.
/// </summary>
public sealed class AutostartService(Action<string> log)
{
    private const string TaskName = "DreamTray";

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>Is the logon task registered right now?</summary>
    public bool IsEnabled()
    {
        var (code, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return code == 0;
    }

    /// <summary>Register or remove the logon task. Requires elevation.</summary>
    public bool SetEnabled(bool enabled)
    {
        if (!IsElevated)
        {
            log("autostart: administrator rights required");
            return false;
        }
        return enabled ? Register() : Unregister();
    }

    private bool Register()
    {
        string exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exe)) return false;

        string xmlPath = Path.Combine(Path.GetTempPath(), "dreamtray-task.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildXml(exe), new System.Text.UnicodeEncoding(false, true));
            var (code, output) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (code != 0) log($"autostart: schtasks failed ({code}): {output}");
            return code == 0;
        }
        catch (Exception ex)
        {
            log($"autostart: {ex.Message}");
            return false;
        }
        finally { try { File.Delete(xmlPath); } catch { } }
    }

    private bool Unregister()
    {
        var (code, output) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        if (code != 0) log($"autostart: delete failed ({code}): {output}");
        return code == 0;
    }

    private static string BuildXml(string exePath)
    {
        string user = WindowsIdentity.GetCurrent().Name;
        string dir = Path.GetDirectoryName(exePath) ?? "";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts DreamTray at logon with the rights its sensor and SMU access need.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Escape(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(exePath)}</Command>
                  <WorkingDirectory>{Escape(dir)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static (int Code, string Output) RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "could not start schtasks");
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode, output.Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
