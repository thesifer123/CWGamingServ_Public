using CWGaming.Shared;

namespace CWGamingServ;

/// <summary>
/// The <see cref="IBbsDoorContext"/> a pure-router FRONT exposes. The front runs no world, so BBS-level
/// commands can't read an in-process realm. Presence for <c>;who</c> comes from the front's own connection
/// registry (users sitting at the BBS login/menu) unioned with each backend realm's pushed
/// <c>public.online_players</c> (see <see cref="RealmPresenceReader"/>). Dropped-connection cleanup is a
/// no-op here: a proxied player's realm teardown happens in that realm's backend when the proxied socket
/// closes.
/// </summary>
public sealed class FrontDoorContext : IBbsDoorContext
{
    private readonly RealmPresenceReader? _realmPresence;

    /// <summary>The front's own transport, for at-BBS-menu presence. Set once the host is constructed.</summary>
    public IBbsHost? Host { get; set; }

    public FrontDoorContext(RealmPresenceReader? realmPresence = null) => _realmPresence = realmPresence;

    public IReadOnlyList<BbsPresenceSnapshot> GetBbsPresenceSnapshots()
    {
        // At-menu users the front holds directly, plus in-realm users each backend pushed to its DB.
        var atMenu = Host?.GetBbsPresenceSnapshots() ?? [];
        var inRealm = _realmPresence?.ReadInRealmPresence() ?? [];
        if (inRealm.Count == 0)
            return atMenu;

        return atMenu
            .Concat(inRealm)
            .OrderBy(s => s.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // A proxied connection dropping is handled inside the realm's backend; the front has no world state.
    public void HandleDroppedConnection(IBbsConnection connection) { }

    // No in-process realm to force-log-off from; backend sysop tooling handles in-realm emergency logoff.
    public bool EmergencyDisconnectOnlineCharacter(string characterName) => false;

    // The front owns no player store; account->character lookups are per-realm on the backends.
    public BbsLinkedPlayerInfo? LookupPlayerByBbsUser(string bbsUserName) => null;
}

/// <summary>Installed app the front never actually runs (it proxies via <see cref="FrontRealmLauncher"/>).</summary>
public sealed class NoopInstalledApp : IBbsInstalledApp
{
    public static readonly NoopInstalledApp Instance = new();

    public Task<BbsInstalledAppResult> RunAsync(IBbsConnection connection, BbsUserAccount account, BbsInstalledAppLaunchMode launchMode, CancellationToken ct)
        => Task.FromResult(BbsInstalledAppResult.Disconnected);
}
