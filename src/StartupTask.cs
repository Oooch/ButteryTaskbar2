using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace ButteryTaskbar3;

// A scheduled task rather than a Run registry key: only a task can start the app elevated at log-in
// without a UAC prompt, and the hooks need elevation to see input going to apps run as administrator.
internal static class StartupTask
{
    private const string TaskName = "ButteryTaskbar3";

    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"", elevated: false) == 0;

    public static bool Enable()
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), "ButteryTaskbar3-task.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(), Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", elevated: true) == 0;
        }
        catch (IOException e)
        {
            Log.Write($"Could not write task XML: {e.Message}");
            return false;
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    public static bool Disable() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F", elevated: true) == 0;

    private static int RunSchtasks(string arguments, bool elevated)
    {
        var info = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = elevated,
            Verb = elevated ? "runas" : "",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(info)!;
            process.WaitForExit();
            Log.Write($"schtasks {arguments} -> exit {process.ExitCode}");
            return process.ExitCode;
        }
        catch (Win32Exception e)
        {
            // Includes the user declining the UAC prompt.
            Log.Write($"schtasks {arguments} -> {e.Message}");
            return -1;
        }
    }

    private static string BuildTaskXml()
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var exe = SecurityElement.Escape(Environment.ProcessPath!);

        // Priority 4 is normal process priority; the Task Scheduler default (7) is below normal,
        // which makes the keyboard and mouse hooks lag under load.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Buttery Taskbar 3 when you log in.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
