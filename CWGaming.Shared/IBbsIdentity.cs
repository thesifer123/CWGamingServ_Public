namespace CWGaming.Shared;

/// <summary>
/// The generic identity a mounted door attaches to a connection (through <see cref="IBbsConnection.AppData"/>)
/// so BBS-level code can read who is connected and where they are WITHOUT referencing any game type. The
/// door's per-session object implements this; the host reads it via <c>connection.AppData as IBbsIdentity</c>.
/// All members return empty/false when no door character is active (e.g. at the BBS login / main menu).
/// </summary>
public interface IBbsIdentity
{
    /// <summary>True when a door character is active for this connection (was: "Player != null").</summary>
    bool HasActiveCharacter { get; }

    /// <summary>The active character's name, or empty when none. Used as a ticket reporter name.</summary>
    string CharacterName { get; }

    /// <summary>
    /// A presence user-name to fall back to when the connection's BBS presence name is blank (the door
    /// derives it from its character). Empty when no character is active.
    /// </summary>
    string PresenceUserName { get; }

    /// <summary>
    /// The door's <c>;who</c> location label while a character is active. The door composes its own label
    /// (e.g. the door name plus the active character: "mmudreborn (Ptery)"); empty when no character is
    /// active, in which case the host shows the connection's BBS presence location.
    /// </summary>
    string PresenceLocation { get; }

    /// <summary>
    /// A rich location string for a ticket draft (e.g. room name + coordinates), or empty when no
    /// character is active — in which case the host uses the connection's BBS presence location.
    /// </summary>
    string TicketLocation { get; }
}
