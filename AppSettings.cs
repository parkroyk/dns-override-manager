using System.Text.Json;
using Microsoft.Win32;

namespace DNSOverrideManager;

/// <summary>A named set of up to four alternate DNS servers.</summary>
public sealed class DnsProfile
{
    public string Name { get; set; } = "";
    public List<string> Servers { get; set; } = new();
}

/// <summary>
/// Persists user configuration in the Windows Registry under
/// HKEY_CURRENT_USER\Software\DNSOverrideManager.
/// </summary>
public sealed class AppSettings
{
    private const string SubKey = @"Software\DNSOverrideManager";
    private const string ProfilesValue = "Profiles";
    private const string ActiveProfileValue = "ActiveProfile";
    private const string TimeoutValue = "TimeoutMinutes";

    public const int NoTimeout = 0;
    public const int MaxProfileNameLength = 30;
    public const int MaxServerFieldLength = 15;
    public const int MaxServersPerProfile = 4;

    private static readonly JsonSerializerOptions JsonOpts = new();

    /// <summary>All configured DNS profiles.</summary>
    public List<DnsProfile> Profiles { get; set; } = new();

    /// <summary>Name of the default profile (the one applied when selected from the tray menu).</summary>
    public string ActiveProfileName { get; set; } = "";

    /// <summary>Override lifetime in minutes. 0 = no timeout.</summary>
    public int TimeoutMinutes { get; set; } = NoTimeout;

    /// <summary>The default profile, or null if the name does not match any profile.</summary>
    public DnsProfile? ActiveProfile =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, ActiveProfileName, StringComparison.OrdinalIgnoreCase));

    private static RegistryKey OpenOrCreate()
        => Registry.CurrentUser.CreateSubKey(SubKey, writable: true);

    /// <summary>Loads settings (profiles, default profile, and timeout) from the registry.</summary>
    public static AppSettings Load()
    {
        var s = new AppSettings();
        using var key = Registry.CurrentUser.OpenSubKey(SubKey);
        if (key is null)
            return s;

        if (key.GetValue(TimeoutValue) is int t)
            s.TimeoutMinutes = Math.Max(0, t);

        // New layout: JSON-serialized profile list.
        if (key.GetValue(ProfilesValue) is string json && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<List<DnsProfile>>(json, JsonOpts);
                if (loaded is not null)
                    s.Profiles = Sanitize(loaded);
            }
            catch
            {
                // Corrupt JSON — start with an empty profile list.
            }
        }

        // No auto-created default profile: if none exist, the user must add one via Settings.
        // When profiles do exist, keep ActiveProfileName pointing at a real one (the first).
        NormalizeActive(s);
        return s;
    }

    /// <summary>Writes the current settings to the registry.</summary>
    public void Save()
    {
        NormalizeActive(this);
        using var key = OpenOrCreate();
        key.SetValue(ProfilesValue, JsonSerializer.Serialize(Profiles, JsonOpts), RegistryValueKind.String);
        key.SetValue(ActiveProfileValue, ActiveProfileName, RegistryValueKind.String);
        key.SetValue(TimeoutValue, TimeoutMinutes, RegistryValueKind.DWord);
    }

    /// <summary>Ensures ActiveProfileName points at an existing profile (defaulting to the first).</summary>
    private static void NormalizeActive(AppSettings s)
    {
        if (s.Profiles.Count == 0)
        {
            s.ActiveProfileName = "";
            return;
        }

        bool valid = s.Profiles.Any(p => string.Equals(p.Name, s.ActiveProfileName, StringComparison.OrdinalIgnoreCase));
        if (!valid)
            s.ActiveProfileName = s.Profiles[0].Name;
    }

    /// <summary>Clamps names/fields to their limits and drops empty servers/profiles.</summary>
    private static List<DnsProfile> Sanitize(List<DnsProfile> loaded)
    {
        var result = new List<DnsProfile>();
        foreach (var p in loaded)
        {
            var name = p.Name?.Trim() ?? "";
            if (name.Length == 0)
                continue;

            var servers = (p.Servers ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Take(MaxServersPerProfile)
                .ToList();
            if (servers.Count == 0)
                continue;

            result.Add(new DnsProfile
            {
                Name = name.Substring(0, Math.Min(name.Length, MaxProfileNameLength)),
                Servers = servers
            });
        }
        return result;
    }
}
