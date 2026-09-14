namespace CWGaming.Shared;

// The connection-registry service the BBS host provides that a door can't derive on its own. Message
// routing (room/realm/player broadcasts, reprompt, forced disconnect) lives in the door's world — it owns
// the player registry and reaches each connection via the connection back-ref. The only thing left here
// is presence over ALL connections, including ones still logging in / at the BBS menu (which the door
// never sees), so it stays a host responsibility. The door receives it via its IBbsDoor.AttachHost hook.
public interface IBbsHost
{
    IReadOnlyList<BbsPresenceSnapshot> GetBbsPresenceSnapshots();
}
