namespace DNSOverrideManager;

/// <summary>
/// Adds/removes a shortcut to the per-user Windows Startup folder (shell:startup)
/// so the application launches automatically at sign-in. Uses the built-in
/// WScript.Shell COM object, so no extra dependency is required.
/// </summary>
public static class StartupManager
{
    private const string ShortcutFileName = "DNS Override Manager.lnk";

    public static string StartupFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    public static string ShortcutPath => Path.Combine(StartupFolder, ShortcutFileName);

    /// <summary>True if a startup shortcut already exists.</summary>
    public static bool IsInStartup() => File.Exists(ShortcutPath);

    /// <summary>Creates the startup shortcut. Throws on failure.</summary>
    public static void AddToStartup()
    {
        string target = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the application executable path.");
        CreateShortcut(ShortcutPath, target);
    }

    /// <summary>Removes the startup shortcut if present.</summary>
    public static void RemoveFromStartup()
    {
        if (File.Exists(ShortcutPath))
            File.Delete(ShortcutPath);
    }

    private static void CreateShortcut(string lnkPath, string targetPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("The WScript.Shell COM object is not available on this system.");

        object shellObj = Activator.CreateInstance(shellType)!;
        dynamic shell = shellObj;
        dynamic shortcut = shell.CreateShortcut(lnkPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? string.Empty;
        shortcut.Description = "DNS Override Manager";
        shortcut.Save();
    }
}
