using System.Diagnostics;

namespace DNSOverrideManager;

internal static class Program
{
    /// <summary>
    /// Application entry point. Runs a hidden host form that owns the system-tray icon.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        // Force administrator: if this instance is not elevated, relaunch ourselves
        // with the "runas" verb (which triggers UAC) and exit the non-elevated process.
        if (!DnsService.IsElevated())
        {
            RelaunchElevated();
            return;
        }

        // Single-instance: if another (elevated) instance already owns the named event,
        // signal it to surface its tray balloon and exit this duplicate. Otherwise claim it.
        var alreadyRunningEvent = AcquireSingleInstance();
        if (alreadyRunningEvent is null)
            return; // a running instance was notified; nothing more to do here

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Last line of defense: surface any unhandled error as a dialog instead of
        // crashing with the JIT-debugging exception window.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                ShowFatal(ex);
        };

        var mainForm = new MainForm(alreadyRunningEvent);
        Application.Run(mainForm);
    }

    /// <summary>
    /// Returns the named event this instance should own, or null if another instance is
    /// already running (in which case that instance is signaled to surface its balloon).
    /// </summary>
    private static EventWaitHandle? AcquireSingleInstance()
    {
        try
        {
            using var existing = EventWaitHandle.OpenExisting(MainForm.AlreadyRunningEventName);
            existing.Set(); // ask the running instance to show "already running"
            return null;
        }
        catch (System.Threading.WaitHandleCannotBeOpenedException)
        {
            // No other instance — claim the event for this one.
            return new EventWaitHandle(false, EventResetMode.AutoReset, MainForm.AlreadyRunningEventName);
        }
    }

    private static void ShowFatal(Exception ex)
    {
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{ex.Message}\n\n" +
            "If the problem persists, try running the application as Administrator.",
            "DNS Override Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// Relaunches the current executable with administrator privileges and exits this
    /// (non-elevated) instance. Guarantees the app always runs elevated.
    /// </summary>
    private static void RelaunchElevated()
    {
        bool started = false;
        try
        {
            string? exePath = Environment.ProcessPath;
            if (exePath is null)
                throw new InvalidOperationException("Could not determine the application executable path.");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true, // required for the "runas" verb to work
                Verb = "runas"          // triggers the UAC elevation prompt
            };
            Process.Start(psi);
            started = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "This application requires administrator rights.\n\n" +
                $"Elevation failed: {ex.Message}\n\n" +
                "Please run it as Administrator manually.",
                "DNS Override Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Environment.Exit(started ? 0 : 1);
    }
}
