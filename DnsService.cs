using System.Diagnostics;
using System.Management;

namespace DNSOverrideManager;

/// <summary>Raised when a WMI DNS operation fails.</summary>
public sealed class DnsOperationException : Exception
{
    public DnsOperationException(string message) : base(message) { }
    public DnsOperationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Snapshot of one network adapter's original DNS configuration.</summary>
public sealed class AdapterBackup
{
    public required string Index { get; init; }
    public required string Description { get; init; }
    public required bool DhcpEnabled { get; init; }
    public required string[] OriginalDns { get; init; }

    /// <summary>The interface name (Win32_NetworkAdapter.NetConnectionID) that netsh uses; empty if unknown.</summary>
    public string NetConnectionId { get; init; } = string.Empty;
}

/// <summary>Collection of adapter snapshots captured before an override is applied.</summary>
public sealed class DnsBackup
{
    public List<AdapterBackup> Adapters { get; } = new();
    public bool HasData => Adapters.Count > 0;
}

/// <summary>
/// Manages DNS servers on the active network adapters via WMI
/// (Win32_NetworkAdapterConfiguration). Backs up the current configuration,
/// applies an override pair, and restores the original state.
/// </summary>
public static class DnsService
{
    private const string EnumerateQuery =
        "SELECT Index, Description, DHCPEnabled, DNSServerSearchOrder " +
        "FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE";

    /// <summary>True if the current process is running with administrator rights.</summary>
    public static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Captures the current DNS configuration of every IP-enabled adapter that has
    /// at least one DNS server configured. Adapters without DNS are ignored so we
    /// don't disturb virtual/disabled interfaces.
    /// </summary>
    public static DnsBackup Capture()
    {
        var backup = new DnsBackup();

        // Best effort: resolve each adapter's netsh interface name (NetConnectionID), keyed
        // by Description. Isolated in its own try so a failure here can never break the core
        // capture below — we simply fall back to WMI EnableDHCP on restore if it's missing.
        var namesByDescription = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var adapterSearcher = new ManagementObjectSearcher(
                "SELECT Description, NetConnectionID FROM Win32_NetworkAdapter");
            foreach (ManagementObject a in adapterSearcher.Get())
            {
                var desc = Convert.ToString(a["Description"]);
                var name = Convert.ToString(a["NetConnectionID"]);
                if (!string.IsNullOrEmpty(desc) && !string.IsNullOrEmpty(name))
                    namesByDescription[desc] = name;
            }
        }
        catch
        {
            // Non-fatal: without a resolved name we fall back to WMI EnableDHCP on restore.
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(EnumerateQuery);
            foreach (ManagementObject mo in searcher.Get())
            {
                var dns = ReadDns(mo);
                if (dns.Length == 0)
                    continue;

                string description = Convert.ToString(mo["Description"]) ?? string.Empty;
                namesByDescription.TryGetValue(description, out var netName);

                backup.Adapters.Add(new AdapterBackup
                {
                    Index = Convert.ToString(mo["Index"]) ?? string.Empty,
                    Description = description,
                    DhcpEnabled = ToBool(mo["DHCPEnabled"]),
                    OriginalDns = dns,
                    NetConnectionId = netName ?? string.Empty
                });
            }
        }
        catch (Exception ex)
        {
            throw new DnsOperationException(
                $"Could not read the current network adapter configuration via WMI: {ex.Message}" +
                (IsElevated() ? string.Empty : " — try running as Administrator."), ex);
        }
        return backup;
    }

    /// <summary>Applies the override DNS servers (one to four) to every adapter in the backup.</summary>
    public static void ApplyOverride(DnsBackup backup, IReadOnlyList<string> servers)
    {
        var serverArray = servers
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToArray();
        if (serverArray.Length == 0)
            throw new DnsOperationException("No DNS servers to apply.");

        foreach (var a in backup.Adapters)
            // Wrap in object[] so the DNS list is passed as ONE array parameter,
            // not fanned out into individual string parameters.
            WithAdapter(a.Index, mo => Invoke(mo, "SetDNSServerSearchOrder", new object[] { serverArray }));
    }

    /// <summary>Restores the original DNS configuration captured in the backup.</summary>
    public static void Restore(DnsBackup backup)
    {
        foreach (var a in backup.Adapters)
        {
            if (a.DhcpEnabled)
            {
                // Return the adapter to DHCP ("Automatic") so Windows shows it as using
                // DHCP-provided servers again. netsh reliably sets the DNS source; the WMI
                // EnableDHCP method does not always take effect on some adapters.
                SetDnsSourceToDhcp(a);
            }
            else
            {
                // Static adapter: write back the exact servers captured before the override.
                WithAdapter(a.Index, mo => Invoke(mo, "SetDNSServerSearchOrder", new object[] { a.OriginalDns }));
            }
        }
    }

    /// <summary>
    /// Sets an adapter's DNS source back to DHCP ("Automatic") using netsh, which reliably
    /// switches the mode (the WMI EnableDHCP method often does not). The client then
    /// re-fetches the DHCP-provided servers. Falls back to WMI EnableDHCP if the interface
    /// name could not be resolved.
    /// </summary>
    private static void SetDnsSourceToDhcp(AdapterBackup adapter)
    {
        if (!string.IsNullOrEmpty(adapter.NetConnectionId))
        {
            RunProcess("netsh", args =>
            {
                args.Add("interface");
                args.Add("ip");
                args.Add("set");
                args.Add("dns");
                args.Add($"name={adapter.NetConnectionId}");
                args.Add("source=dhcp");
            });
        }
        else
        {
            WithAdapter(adapter.Index, mo => Invoke(mo, "EnableDHCP", Array.Empty<object>()));
        }
    }

    /// <summary>Runs a console tool (hidden) and throws if it reports a non-zero exit code.</summary>
    private static void RunProcess(string fileName, Action<IList<string>> configureArgs)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        configureArgs(psi.ArgumentList);

        using var process = Process.Start(psi)
            ?? throw new DnsOperationException($"Failed to start '{fileName}'.");
        process.WaitForExit(15_000);
        if (!process.HasExited)
        {
            process.Kill();
            throw new DnsOperationException($"'{fileName}' timed out while restoring DNS.");
        }
        if (process.ExitCode != 0)
            throw new DnsOperationException($"'{fileName}' failed with exit code {process.ExitCode}.");
    }

    // ---- WMI helpers ----

    /// <summary>Runs <paramref name="action"/> against the single adapter with the given WMI index.</summary>
    private static void WithAdapter(string index, Action<ManagementObject> action)
    {
        if (string.IsNullOrEmpty(index))
            return;

        using var searcher = new ManagementObjectSearcher(
            $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE Index = '{index}'");
        foreach (ManagementObject mo in searcher.Get())
        {
            action(mo);
            return;
        }
    }

    private static string[] ReadDns(ManagementObject mo)
    {
        if (mo["DNSServerSearchOrder"] is string[] arr && arr.Length > 0)
            return arr;
        return Array.Empty<string>();
    }

    private static bool ToBool(object? value) => value is bool b && b;

    /// <summary>Invokes a WMI method and throws if it reports a non-zero status.
    /// <paramref name="args"/> is the complete method parameter list.</summary>
    private static void Invoke(ManagementObject mo, string method, object[] args)
    {
        object? result;
        try
        {
            result = mo.InvokeMethod(method, args);
        }
        catch (Exception ex)
        {
            // WMI can throw many exception types (ManagementException, COMException,
            // InvalidCastException for malformed arguments, ...). Wrap them all so the
            // UI shows a friendly message instead of crashing.
            throw new DnsOperationException(
                $"WMI call '{method}' failed: {ex.Message}" +
                (IsElevated() ? string.Empty : " — try running as Administrator."), ex);
        }

        int code = result switch
        {
            int i => i,
            uint u => (int)u,
            _ => 0
        };
        if (code != 0)
            throw new DnsOperationException(
                $"WMI method '{method}' failed with status {code}. " +
                (IsElevated()
                    ? "Verify the adapter is active and IP-enabled."
                    : "Run the application as Administrator to modify DNS settings."));
    }
}
