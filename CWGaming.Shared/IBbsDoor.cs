namespace CWGaming.Shared;

/// <summary>
/// The host-provided services a door needs when it is created: the BBS command dispatcher it routes
/// unconsumed input to, the shared BBS account store its world reads/links against, and the process
/// startup args (so the door can run one-shot maintenance modes). The host builds all of these BBS-side
/// pieces, then hands them to the door factory — the door never constructs BBS infrastructure itself.
/// </summary>
public sealed class BbsDoorServices
{
    public required IBbsCommandDispatcher CommandDispatcher { get; init; }
    public required IBbsUserRepository BbsUserRepository { get; init; }
    public string[] StartupArgs { get; init; } = [];
}

/// <summary>
/// One mounted door (e.g. mmudreborn). The host loads it through an <see cref="IBbsDoorFactory"/> it
/// discovers by reflection, then drives it: it asks for the door's <see cref="Context"/> (world services
/// BBS commands need) and <see cref="InstalledApp"/> (the per-connection runner), attaches itself as the
/// presence host, and starts/stops the door's background work alongside the telnet host. The host holds
/// only this interface — never a concrete door type — so it can host any door.
/// </summary>
public interface IBbsDoor
{
    /// <summary>The door's app id (e.g. "mmudreborn"), for presence/ticket scoping.</summary>
    string AppId { get; }

    /// <summary>World services BBS-level commands (;who, ;users, emergency logoff) call into.</summary>
    IBbsDoorContext Context { get; }

    /// <summary>The per-connection runner the host launches once a user enters the door.</summary>
    IBbsInstalledApp InstalledApp { get; }

    /// <summary>
    /// Invoked by the door when it requests a process exit (sysop restart/shutdown). The host wires its
    /// graceful-shutdown handler here. Arguments are (exitCode, reason).
    /// </summary>
    Action<int, string>? OnShutdownRequested { get; set; }

    /// <summary>Process exit code the supervisor reads for a clean stop.</summary>
    int ShutdownExitCode { get; }

    /// <summary>Process exit code the supervisor reads to relaunch (sysop restart / deploy).</summary>
    int RestartExitCode { get; }

    /// <summary>Give the door the host's presence provider (connections still at the BBS login/menu).</summary>
    void AttachHost(IBbsHost host);

    /// <summary>Start the door's background work (world ticks, etc.).</summary>
    void Start();

    /// <summary>Persist and stop the door's world.</summary>
    void Stop();
}

/// <summary>
/// A door's entry point. The host finds an implementation by scanning a configured door assembly via
/// reflection and instantiating it with a parameterless constructor, then calls <see cref="Create"/>.
/// </summary>
public interface IBbsDoorFactory
{
    IBbsDoor Create(BbsDoorServices services);
}
