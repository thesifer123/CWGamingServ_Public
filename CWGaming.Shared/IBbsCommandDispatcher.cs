namespace CWGaming.Shared;

/// <summary>
/// Handles BBS-level "<c>;</c>" commands (<c>;who</c>, <c>;ticket</c>, <c>;account</c>, ...) that work
/// the same across any mounted door. The host implements it; the door invokes it for input it does not
/// itself consume, supplying the generic connection and an <see cref="IBbsDoorContext"/> view of its
/// world. Neither side needs to reference the other's concrete types.
/// </summary>
public interface IBbsCommandDispatcher
{
    Task<bool> TryDispatchAsync(string input, IBbsConnection client, IBbsDoorContext door, string? viewerBbsUserName = null);
}

public sealed class NoOpBbsCommandDispatcher : IBbsCommandDispatcher
{
    public static NoOpBbsCommandDispatcher Instance { get; } = new();

    private NoOpBbsCommandDispatcher()
    {
    }

    public Task<bool> TryDispatchAsync(string input, IBbsConnection client, IBbsDoorContext door, string? viewerBbsUserName = null)
    {
        return Task.FromResult(false);
    }
}
