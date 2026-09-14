namespace CWGaming.Shared;

/// <summary>
/// A generic, door-agnostic view of one connected presence for the BBS <c>;who</c> list. Built by the
/// host over ALL connections (including ones still logging in / at the BBS menu) and by the active door
/// over its in-realm players. Carries no game concepts.
/// </summary>
public sealed record BbsPresenceSnapshot(string UserName, bool IsSysop, int MinutesOnline, string Location, string IpAddress);
