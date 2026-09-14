namespace CWGaming.Shared;

/// <summary>
/// Sentinel strings the BBS transport and the mounted door pass through the read pipeline in place of a
/// real input line: a read that timed out, or a line the BBS command dispatcher already handled. Generic
/// so the host transport and any door agree on them.
/// </summary>
public static class GameTransportConstants
{
    public const string ReadTimeoutSentinel = "\0READ_TIMEOUT\0";
    public const string ReadCommandHandledSentinel = "\0READ_COMMAND\0";
}
