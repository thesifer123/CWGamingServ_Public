namespace CWGaming.Shared;

public enum BbsInstalledAppLaunchMode
{
    ShowMainMenu,
    AutoAdvanceMainMenu,
    EnterRealmDirectly,
}

public enum BbsInstalledAppResult
{
    ReturnedToBbs,
    Disconnected,
}

/// <summary>
/// One mounted door's per-connection runner. The BBS host hands it the generic connection and the
/// logged-in account; the door wraps the connection in its own session view and runs until the user
/// returns to the BBS or disconnects. The host obtains an instance from the door it loaded
/// (<see cref="IBbsDoor.InstalledApp"/>), so it never references a concrete door type.
/// </summary>
public interface IBbsInstalledApp
{
    Task<BbsInstalledAppResult> RunAsync(IBbsConnection connection, BbsUserAccount account, BbsInstalledAppLaunchMode launchMode, CancellationToken ct);
}
