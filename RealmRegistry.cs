using System.Text.Json;
using System.Text.Json.Serialization;

namespace CWGamingServ;

/// <summary>
/// One realm the BBS front can route a connection to. Infrastructure identity (<see cref="Id"/>,
/// <see cref="BackendHost"/>/<see cref="BackendPort"/>) is deliberately dumb and stable; the
/// player-facing <see cref="DisplayName"/> (e.g. "PvP", "Hardcore") is a presentation concern the
/// sysop can rename without touching databases or processes. The front proxies a chosen connection to
/// the realm's backend game process at <see cref="BackendHost"/>:<see cref="BackendPort"/>.
/// </summary>
public sealed record RealmDefinition
{
    /// <summary>Stable infra id / DB stem (e.g. "main" -> mmudreborn, "two" -> mmudreborn_two).</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }

    /// <summary>Player-facing name shown in the BBS realm menu (renamable anytime).</summary>
    [JsonPropertyName("displayName")] public required string DisplayName { get; init; }

    /// <summary>Loopback host of the realm's backend game process (default 127.0.0.1).</summary>
    [JsonPropertyName("backendHost")] public string BackendHost { get; init; } = "127.0.0.1";

    /// <summary>Internal (non-public) telnet port the backend game process listens on.</summary>
    [JsonPropertyName("backendPort")] public required int BackendPort { get; init; }

    /// <summary>
    /// Optional Postgres connection to this realm's game DB, used ONLY so the front can read the realm's
    /// pushed <c>public.online_players</c> for a cross-realm <c>;who</c>. Blank = that realm's players are
    /// simply omitted from the front's aggregated who list (routing still works).
    /// </summary>
    [JsonPropertyName("gameDb")] public string GameDb { get; init; } = "";

    /// <summary>Hidden from the menu (and un-routable) when false, without deleting its config row.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;

    /// <summary>Menu ordering; lower sorts first. Ties fall back to config order.</summary>
    [JsonPropertyName("order")] public int Order { get; init; }
}

/// <summary>
/// The set of realms the BBS front offers. Loaded from a JSON file (path via
/// <see cref="ConfigPathEnvironmentVariable"/>, else <c>realms.json</c> beside the host binary). The
/// front owns this because it owns the port, the login, and the realm menu; the backend game processes
/// know nothing about each other.
/// </summary>
public sealed class RealmRegistry
{
    public const string ConfigPathEnvironmentVariable = "MMUDREBORN_REALMS_CONFIG";
    private const string DefaultConfigFileName = "realms.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class RealmsFile
    {
        [JsonPropertyName("realms")] public List<RealmDefinition> Realms { get; init; } = [];
    }

    private readonly List<RealmDefinition> _realms;

    private RealmRegistry(List<RealmDefinition> realms) => _realms = realms;

    /// <summary>Realms to offer, enabled-only, sorted by <see cref="RealmDefinition.Order"/>.</summary>
    public IReadOnlyList<RealmDefinition> EnabledRealms =>
        _realms.Where(r => r.Enabled).OrderBy(r => r.Order).ToArray();

    /// <summary>All configured realms (including disabled), in configured order.</summary>
    public IReadOnlyList<RealmDefinition> AllRealms => _realms;

    public RealmDefinition? FindById(string id) =>
        _realms.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolve the config file path: the <see cref="ConfigPathEnvironmentVariable"/> override, else
    /// <c>realms.json</c> in the host binary's directory.
    /// </summary>
    public static string ResolveConfigPath()
    {
        string? configured = Environment.GetEnvironmentVariable(ConfigPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return Path.Combine(AppContext.BaseDirectory, DefaultConfigFileName);
    }

    /// <summary>
    /// Load the registry from <see cref="ResolveConfigPath"/>. Throws <see cref="InvalidOperationException"/>
    /// with an actionable message if the file is missing, malformed, empty, or has duplicate ids — the
    /// front cannot route without a valid realm list, so failing loud beats silently offering no realms.
    /// </summary>
    public static RealmRegistry Load() => LoadFromPath(ResolveConfigPath());

    public static RealmRegistry LoadFromPath(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Realm registry not found at '{path}'. Create it (or set {ConfigPathEnvironmentVariable}) " +
                "with a JSON body: { \"realms\": [ { \"id\": \"main\", \"displayName\": \"Main\", \"backendPort\": 2601 } ] }.");

        RealmsFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RealmsFile>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Realm registry '{path}' is not valid JSON: {ex.Message}", ex);
        }

        var realms = parsed?.Realms ?? [];
        if (realms.Count == 0)
            throw new InvalidOperationException($"Realm registry '{path}' declares no realms.");

        foreach (var realm in realms)
        {
            if (string.IsNullOrWhiteSpace(realm.Id))
                throw new InvalidOperationException($"Realm registry '{path}' has a realm with a blank id.");
            if (string.IsNullOrWhiteSpace(realm.DisplayName))
                throw new InvalidOperationException($"Realm '{realm.Id}' in '{path}' has a blank displayName.");
            if (realm.BackendPort is <= 0 or > 65535)
                throw new InvalidOperationException($"Realm '{realm.Id}' in '{path}' has an invalid backendPort {realm.BackendPort}.");
        }

        var duplicateId = realms.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicateId != null)
            throw new InvalidOperationException($"Realm registry '{path}' has duplicate realm id '{duplicateId}'.");

        return new RealmRegistry(realms);
    }
}
