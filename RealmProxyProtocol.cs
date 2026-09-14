using System.Text;

namespace CWGamingServ;

/// <summary>How this process participates in the multi-realm topology.</summary>
public enum ServerRole
{
    /// <summary>Today's default: one process does BBS login AND runs the (single) in-process realm.</summary>
    Monolithic,

    /// <summary>Owns the public port: BBS login + realm menu, then proxies the connection to a backend.</summary>
    Front,

    /// <summary>Runs one realm's game world; accepts only front-proxied, pre-authenticated connections.</summary>
    Backend,
}

/// <summary>
/// The wire contract between the BBS front and a realm backend, plus role/secret resolution.
///
/// When a logged-in user picks a realm, the front opens a loopback TCP connection to that realm's backend
/// and sends a single tab-delimited handshake LINE (below) identifying the already-authenticated account,
/// then becomes a transparent byte pipe. The backend reads exactly that line first (raw, before any telnet
/// negotiation), verifies the shared secret, and runs the realm directly for that account — it never does
/// its own BBS login. Because the backend trusts the front's word, backends MUST listen on loopback only
/// and the secret gates the handshake.
///
/// Handshake: <c>MMUDREALM1\t{secret}\t{userName}\t{displayName}\t{clientIp}\n</c>
/// </summary>
public static class RealmProxyProtocol
{
    public const string RoleEnvironmentVariable = "MMUDREBORN_ROLE";
    public const string ProxySecretEnvironmentVariable = "MMUDREBORN_REALM_PROXY_SECRET";
    public const string RealmIdEnvironmentVariable = "MMUDREBORN_REALM_ID";

    private const string HandshakeMagic = "MMUDREALM1";
    private const char FieldSeparator = '\t';
    public const int MaxHandshakeLineLength = 512;

    // Used only when no secret is configured — fine for an all-loopback single-host dev box, but a
    // deployment that could receive backend connections from elsewhere must set the env var.
    private const string DevDefaultSecret = "local-dev-realm-proxy-secret";

    public static ServerRole ResolveRole()
    {
        string? raw = Environment.GetEnvironmentVariable(RoleEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return ServerRole.Monolithic;

        return raw.Trim().ToLowerInvariant() switch
        {
            "front" => ServerRole.Front,
            "backend" => ServerRole.Backend,
            "monolithic" or "mono" or "" => ServerRole.Monolithic,
            var other => throw new InvalidOperationException(
                $"Unknown {RoleEnvironmentVariable} '{other}'. Use 'front', 'backend', or 'monolithic'."),
        };
    }

    public static string ResolveProxySecret()
    {
        string? value = Environment.GetEnvironmentVariable(ProxySecretEnvironmentVariable);
        return string.IsNullOrWhiteSpace(value) ? DevDefaultSecret : value;
    }

    /// <summary>This backend's realm id (for logging/presence), from <see cref="RealmIdEnvironmentVariable"/>.</summary>
    public static string ResolveRealmId() =>
        Environment.GetEnvironmentVariable(RealmIdEnvironmentVariable)?.Trim() ?? string.Empty;

    public sealed record Handshake(string UserName, string DisplayName, string ClientIp);

    public static string FormatHandshake(string secret, string userName, string displayName, string clientIp)
    {
        // Guard the delimiter: names are validated elsewhere, but never let a stray tab/newline split fields.
        static string Clean(string s) => (s ?? string.Empty).Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        var sb = new StringBuilder(64);
        sb.Append(HandshakeMagic).Append(FieldSeparator)
          .Append(Clean(secret)).Append(FieldSeparator)
          .Append(Clean(userName)).Append(FieldSeparator)
          .Append(Clean(displayName)).Append(FieldSeparator)
          .Append(Clean(clientIp)).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Parse and authenticate a handshake line. Returns null (reason set) on bad magic, wrong secret, or
    /// malformed fields — the backend closes the connection in that case.
    /// </summary>
    public static Handshake? TryParse(string line, string expectedSecret, out string failureReason)
    {
        failureReason = string.Empty;
        if (string.IsNullOrEmpty(line))
        {
            failureReason = "empty handshake";
            return null;
        }

        string[] parts = line.TrimEnd('\r', '\n').Split(FieldSeparator);
        if (parts.Length != 5 || parts[0] != HandshakeMagic)
        {
            failureReason = "bad magic/format";
            return null;
        }

        // Constant-time-ish compare (length + ordinal) to avoid leaking secret length via early-out.
        if (!FixedEquals(parts[1], expectedSecret))
        {
            failureReason = "secret mismatch";
            return null;
        }

        string userName = parts[2].Trim();
        if (userName.Length == 0)
        {
            failureReason = "blank user";
            return null;
        }

        string displayName = parts[3].Trim();
        string clientIp = parts[4].Trim();
        return new Handshake(userName, displayName.Length == 0 ? userName : displayName, clientIp);
    }

    private static bool FixedEquals(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
