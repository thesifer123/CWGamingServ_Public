namespace CWGaming.Shared;

/// <summary>
/// A door-agnostic, generic record of the character a BBS account owns, resolved by the mounted door
/// (which owns player storage) from the account's BBS user id. Carries only the fields the host renders
/// for the <c>;users</c> / <c>;account</c> listings.
/// </summary>
public sealed record BbsLinkedPlayerInfo(string Name, bool IsSysop, bool IsTestAccount, bool IsTesterSysop);

/// <summary>
/// The services the active door exposes to BBS-level command handling (<c>;who</c>, <c>;users</c>,
/// emergency logoff, dropped-connection cleanup). The host owns the connection and the BBS commands;
/// the door owns the world/player registry. This interface is the seam between them, so the host never
/// references a game type. The mounted door's world implements it.
/// </summary>
public interface IBbsDoorContext
{
    /// <summary>Presence over the door's in-realm players for the <c>;who</c> list.</summary>
    IReadOnlyList<BbsPresenceSnapshot> GetBbsPresenceSnapshots();

    /// <summary>Forcibly remove an in-realm character (emergency logoff). Returns false if not online.</summary>
    bool EmergencyDisconnectOnlineCharacter(string characterName);

    /// <summary>
    /// A connection dropped underneath the host (socket closed / read loop ended); the door tears down
    /// any in-realm character attached to it and runs its own departure announcement.
    /// </summary>
    void HandleDroppedConnection(IBbsConnection connection);

    /// <summary>
    /// Look up the character a BBS account owns, by the account's BBS user id, for the <c>;users</c> /
    /// <c>;account</c> merge. The door owns the account↔character link (via the player's stored BBS user
    /// id), so the host passes only the login name. Returns null when the id is blank or the account owns
    /// no character in this door.
    /// </summary>
    BbsLinkedPlayerInfo? LookupPlayerByBbsUser(string bbsUserName);
}
