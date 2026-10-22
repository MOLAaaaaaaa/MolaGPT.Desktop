using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace MolaGPT.Core.Net;

/// <summary>
/// The 绕过 list, compiled once per configuration. An entry is one of
/// <c>&lt;local&gt;</c> (a name with no dot), a CIDR block, a wildcard
/// (<c>*.corp.example</c>, <c>192.168.*</c>) or a plain name, which also covers its
/// subdomains. Loopback is not an entry: it never leaves the machine, and a proxy
/// for the Kimi bridge or a local Ollama would only ever break them.
/// </summary>
internal sealed class ProxyBypassList
{
    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    private readonly List<(IPAddress Network, int Prefix)> _blocks = [];
    private readonly List<Regex> _wildcards = [];
    private readonly List<string> _names = [];
    private bool _local;

    public static IEnumerable<string> Entries(string? text) =>
        (text ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static ProxyBypassList Parse(string? text)
    {
        var list = new ProxyBypassList();
        foreach (var raw in Entries(text))
        {
            var entry = raw.ToLowerInvariant();
            if (entry == "<local>")
                list._local = true;
            else if (entry.Contains('/') && TryParseBlock(entry, out var block))
                list._blocks.Add(block);
            else if (entry.Contains('*'))
                list._wildcards.Add(new Regex("^" + Regex.Escape(entry).Replace("\\*", ".*") + "$",
                    RegexOptions.CultureInvariant | RegexOptions.Compiled));
            else
                list._names.Add(entry.TrimStart('.'));
        }

        return list;
    }

    public static bool IsLoopback(Uri uri)
    {
        var host = Normalize(uri);
        return host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)
               || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    }

    public bool IsMatch(Uri uri)
    {
        var host = Normalize(uri);
        var isAddress = IPAddress.TryParse(host, out var address);
        if (_local && !isAddress && !host.Contains('.')) return true;

        if (isAddress)
            foreach (var (network, prefix) in _blocks)
                if (InBlock(address!, network, prefix))
                    return true;

        foreach (var name in _names)
            if (host == name || host.EndsWith("." + name, StringComparison.Ordinal))
                return true;
        foreach (var wildcard in _wildcards)
            if (wildcard.IsMatch(host))
                return true;
        return false;
    }

    private static string Normalize(Uri uri) => uri.DnsSafeHost.TrimEnd('.').ToLowerInvariant();

    private static bool TryParseBlock(string text, out (IPAddress, int) block)
    {
        block = default;
        var slash = text.IndexOf('/');
        if (!IPAddress.TryParse(text[..slash], out var network)
            || !int.TryParse(text[(slash + 1)..], out var prefix))
            return false;

        var bits = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix < 0 || prefix > bits) return false;
        block = (network, prefix);
        return true;
    }

    private static bool InBlock(IPAddress address, IPAddress network, int prefix)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != network.AddressFamily) return false;

        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        var whole = prefix / 8;
        for (var i = 0; i < whole; i++)
            if (a[i] != n[i])
                return false;

        var rest = prefix % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (a[whole] & mask) == (n[whole] & mask);
    }
}
