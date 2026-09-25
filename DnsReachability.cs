using System.Net;
using System.Net.Sockets;

namespace DNSOverrideManager;

/// <summary>
/// Tests whether a DNS server address is reachable by attempting a TCP connection to its
/// DNS port (53). A successful connect means the DNS service is up and routable — a more
/// reliable signal for a DNS server than an ICMP ping, which firewalls often block.
/// </summary>
public static class DnsReachability
{
    private const int DnsPort = 53;

    /// <summary>True if <paramref name="address"/> accepts a TCP connection on port 53 within the timeout.</summary>
    public static async Task<bool> IsReachableAsync(string address, int timeoutMs = 2000)
    {
        if (!IPAddress.TryParse(address, out var ip))
            return false;

        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(ip, DnsPort, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // Timeout, connection refused, host unreachable, or invalid address.
            return false;
        }
    }

    /// <summary>Runs reachability checks for all addresses in parallel and returns a per-address result map.</summary>
    public static async Task<Dictionary<string, bool>> TestAllAsync(IEnumerable<string> addresses, int timeoutMs = 2000)
    {
        var distinct = addresses
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct()
            .ToList();

        var results = await Task.WhenAll(
                distinct.Select(async a => (Address: a, Reachable: await IsReachableAsync(a, timeoutMs))))
            .ConfigureAwait(false);

        return results.ToDictionary(r => r.Address, r => r.Reachable);
    }
}
