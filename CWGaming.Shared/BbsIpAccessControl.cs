using System.Net;

namespace CWGaming.Shared;

/// <summary>
/// BBS-host IP access control: a sysop-managed denylist (exact IPs + CIDR ranges) and an optional
/// per-IP concurrent-connection cap, enforced at accept time — before the telnet handshake and login.
///
/// This is only meaningful when the host actually sees the real client IP. Behind a NAT boundary
/// (e.g. the Windows->WSL2 gateway, which makes every client look like 172.17.32.1) the source IP is
/// rewritten before the packet reaches the socket, so a ban would match the gateway, not the client.
/// The deployment fix is WSL2 mirrored networking (authentic, unspoofable source IPs); see the
/// README. Once real IPs flow, this class enforces against them with no further changes.
///
/// Bans and the cap persist through the BBS settings KV store so they survive restarts; the live
/// per-IP connection counts are in-memory. Every member is thread-safe — the accept loop and every
/// disconnect path call in concurrently.
/// </summary>
public sealed class BbsIpAccessControl
{
    public const string DenylistSettingKey = "BbsIpDenylist";
    public const string MaxConnectionsPerIpSettingKey = "BbsMaxConnectionsPerIp";

    public enum ConnectionDecision
    {
        Allowed,
        Banned,
        TooManyConnections,
    }

    private sealed record BanEntry(string Display, IPNetwork Network);

    private readonly IBbsUserRepository? _settingsStore;
    private readonly object _gate = new();
    private readonly List<BanEntry> _bans = [];
    private readonly Dictionary<string, int> _activeByIp = new(StringComparer.OrdinalIgnoreCase);
    private int _maxConnectionsPerIp; // 0 = unlimited

    public BbsIpAccessControl(IBbsUserRepository? settingsStore = null)
    {
        _settingsStore = settingsStore;
        LoadFromSettings();
    }

    /// <summary>0 = unlimited. Reject the (N+1)th simultaneous connection from one IP.</summary>
    public int MaxConnectionsPerIp
    {
        get { lock (_gate) return _maxConnectionsPerIp; }
    }

    public void SetMaxConnectionsPerIp(int value)
    {
        value = Math.Max(0, value);
        lock (_gate)
        {
            _maxConnectionsPerIp = value;
            _settingsStore?.SetSettingText(MaxConnectionsPerIpSettingKey, value.ToString());
        }
    }

    /// <summary>
    /// Evaluate (and, if allowed, register) a new connection from <paramref name="rawIp"/>. Call
    /// <see cref="EndConnection"/> exactly once for every Allowed result when that connection closes.
    /// Unparseable/empty IPs are allowed but not counted — we never block what we can't identify.
    /// </summary>
    public ConnectionDecision TryBeginConnection(string? rawIp)
    {
        var address = NormalizeAddress(rawIp);
        if (address == null)
            return ConnectionDecision.Allowed;

        string key = address.ToString();
        lock (_gate)
        {
            if (IsBannedLocked(address))
                return ConnectionDecision.Banned;

            int current = _activeByIp.GetValueOrDefault(key);
            if (_maxConnectionsPerIp > 0 && current >= _maxConnectionsPerIp)
                return ConnectionDecision.TooManyConnections;

            _activeByIp[key] = current + 1;
            return ConnectionDecision.Allowed;
        }
    }

    public void EndConnection(string? rawIp)
    {
        var address = NormalizeAddress(rawIp);
        if (address == null)
            return;

        string key = address.ToString();
        lock (_gate)
        {
            if (!_activeByIp.TryGetValue(key, out int current))
                return;

            if (current <= 1)
                _activeByIp.Remove(key);
            else
                _activeByIp[key] = current - 1;
        }
    }

    public bool IsBanned(string? rawIp)
    {
        var address = NormalizeAddress(rawIp);
        if (address == null)
            return false;

        lock (_gate)
            return IsBannedLocked(address);
    }

    /// <summary>Add an exact IP ("1.2.3.4") or CIDR range ("1.2.3.0/24") to the denylist.</summary>
    public bool TryBan(string entry, out string normalizedDisplay, out string error)
    {
        normalizedDisplay = string.Empty;
        error = string.Empty;

        if (!TryParseEntry(entry, out var network, out string display))
        {
            error = $"'{entry?.Trim()}' is not a valid IP address or CIDR range (e.g. 203.0.113.5 or 203.0.113.0/24).";
            return false;
        }

        lock (_gate)
        {
            if (_bans.Any(b => b.Display.Equals(display, StringComparison.OrdinalIgnoreCase)))
            {
                normalizedDisplay = display;
                error = $"{display} is already banned.";
                return false;
            }

            _bans.Add(new BanEntry(display, network));
            SaveBansLocked();
        }

        normalizedDisplay = display;
        return true;
    }

    /// <summary>Remove a ban by its displayed entry (exact IP or CIDR), case-insensitive.</summary>
    public bool TryUnban(string entry)
    {
        string trimmed = (entry ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return false;

        // Match either the literal stored display, or the normalized form of the supplied entry.
        string? normalized = TryParseEntry(trimmed, out _, out string display) ? display : null;

        lock (_gate)
        {
            int removed = _bans.RemoveAll(b =>
                b.Display.Equals(trimmed, StringComparison.OrdinalIgnoreCase) ||
                (normalized != null && b.Display.Equals(normalized, StringComparison.OrdinalIgnoreCase)));

            if (removed == 0)
                return false;

            SaveBansLocked();
            return true;
        }
    }

    public IReadOnlyList<string> GetBans()
    {
        lock (_gate)
            return _bans.Select(b => b.Display).ToList();
    }

    /// <summary>Live per-IP connection counts (IP, count), for sysop display. Snapshot.</summary>
    public IReadOnlyList<(string Ip, int Count)> GetActiveConnectionCounts()
    {
        lock (_gate)
            return _activeByIp.Select(kvp => (kvp.Key, kvp.Value)).OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
    }

    private bool IsBannedLocked(IPAddress address)
    {
        foreach (var ban in _bans)
        {
            // IPNetwork.Contains only matches within the same address family, so a v4 ban never
            // accidentally matches a v6 client (and vice versa).
            if (ban.Network.BaseAddress.AddressFamily == address.AddressFamily && ban.Network.Contains(address))
                return true;
        }

        return false;
    }

    private void LoadFromSettings()
    {
        if (_settingsStore == null)
            return;

        string raw = _settingsStore.GetSettingText(DenylistSettingKey, string.Empty);
        foreach (string token in raw.Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseEntry(token, out var network, out string display)
                && !_bans.Any(b => b.Display.Equals(display, StringComparison.OrdinalIgnoreCase)))
            {
                _bans.Add(new BanEntry(display, network));
            }
        }

        if (int.TryParse(_settingsStore.GetSettingText(MaxConnectionsPerIpSettingKey, string.Empty), out int max) && max > 0)
            _maxConnectionsPerIp = max;
    }

    private void SaveBansLocked()
    {
        _settingsStore?.SetSettingText(DenylistSettingKey, string.Join(",", _bans.Select(b => b.Display)));
    }

    private static IPAddress? NormalizeAddress(string? rawIp)
    {
        if (string.IsNullOrWhiteSpace(rawIp))
            return null;

        string text = rawIp.Trim();
        if (!IPAddress.TryParse(text, out var address))
            return null;

        // Normalize IPv4-mapped IPv6 (::ffff:1.2.3.4) down to plain IPv4 so a v4 ban matches a
        // dual-stack socket that reports the mapped form.
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return address;
    }

    private static bool TryParseEntry(string? entry, out IPNetwork network, out string display)
    {
        network = default;
        display = string.Empty;

        string text = (entry ?? string.Empty).Trim();
        if (text.Length == 0)
            return false;

        int slash = text.IndexOf('/');
        if (slash < 0)
        {
            // Exact IP -> a full-length single-host network (/32 for v4, /128 for v6).
            if (!IPAddress.TryParse(text, out var single))
                return false;

            if (single.IsIPv4MappedToIPv6)
                single = single.MapToIPv4();

            int fullPrefix = single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
            network = new IPNetwork(single, fullPrefix);
            display = single.ToString();
            return true;
        }

        if (!IPAddress.TryParse(text[..slash], out var baseAddress)
            || !int.TryParse(text[(slash + 1)..], out int prefix))
        {
            return false;
        }

        if (baseAddress.IsIPv4MappedToIPv6)
            baseAddress = baseAddress.MapToIPv4();

        int maxPrefix = baseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefix < 0 || prefix > maxPrefix)
            return false;

        // IPNetwork requires the base address to have zeroed host bits; mask it ourselves so a sysop
        // can type any address in the range (e.g. 203.0.113.5/24) and we canonicalize to 203.0.113.0/24.
        var masked = MaskToPrefix(baseAddress, prefix);
        network = new IPNetwork(masked, prefix);
        display = $"{masked}/{prefix}";
        return true;
    }

    private static IPAddress MaskToPrefix(IPAddress address, int prefix)
    {
        byte[] bytes = address.GetAddressBytes();
        int remainingBits = prefix;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (remainingBits >= 8)
            {
                remainingBits -= 8;
                continue;
            }

            if (remainingBits <= 0)
            {
                bytes[i] = 0;
                continue;
            }

            bytes[i] = (byte)(bytes[i] & (0xFF << (8 - remainingBits)));
            remainingBits = 0;
        }

        return new IPAddress(bytes);
    }
}
