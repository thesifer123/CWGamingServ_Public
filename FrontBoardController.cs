using System.Text.Json;
using Npgsql;

namespace CWGamingServ;

/// <summary>
/// Board-wide control used by the <c>;restart</c> / <c>;shutdown</c> sysop commands. Only the FRONT process
/// can see the whole board (every realm's DB + the menu-client list), so it owns the actual orchestration.
/// A realm BACKEND — where a player typing <c>;restart</c> in the mud is actually handled — cannot see the
/// board, so its controller just relays the request up to the front over the realm bus.
/// </summary>
public interface IBoardController
{
    /// <summary>
    /// Begin a board-wide restart (<paramref name="restart"/> true) or shutdown: warn everyone, wait
    /// <paramref name="seconds"/>, then relaunch / stop the whole topology. Returns as soon as the countdown
    /// is armed (it runs detached) so the issuing session stays live.
    /// </summary>
    Task RequestBoardStopAsync(int seconds, bool restart, string reason);
}

/// <summary>
/// FRONT implementation: drives the whole board-stop. Fans the countdown out to everyone on the front
/// (menu / login / realm-select) and into every realm process (over each realm's <c>realm_bus</c>, which
/// injects it to that realm's in-game players), then exits with the supervisor's code so the whole board
/// restarts (42) or stays down (0).
/// </summary>
public sealed class FrontBoardController : IBoardController
{
    // Must match the game's realm-bus channel and the supervisor's exit-code contract (42 = relaunch,
    // 0 = intentional stop / stay down).
    private const string RealmBusChannel = "realm_bus";
    private const int RestartExitCode = 42;
    private const int ShutdownExitCode = 0;

    private readonly RealmRegistry _registry;
    private readonly TelnetServerHost _host;
    private readonly Action<int, string> _requestStop;
    private readonly bool _quietLogging;
    private int _stopArmed; // 0/1 latch: a board stop is a one-way trip, so ignore any second trigger.

    public FrontBoardController(RealmRegistry registry, TelnetServerHost host, Action<int, string> requestStop, bool quietLogging)
    {
        _registry = registry;
        _host = host;
        _requestStop = requestStop;
        _quietLogging = quietLogging;
    }

    public async Task RequestBoardStopAsync(int seconds, bool restart, string reason)
    {
        if (Interlocked.Exchange(ref _stopArmed, 1) == 1)
            return; // a countdown is already running; a board stop only happens once.

        string verb = restart ? "restart" : "shut down";
        await AnnounceAsync($"{BbsAnsi.BrightRed}{BbsAnsi.BgBlack}** The Realm will {verb} in {seconds} seconds. **{BbsAnsi.Reset}");

        if (seconds <= 0)
        {
            await FinishAsync(restart, reason);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                await FinishAsync(restart, reason);
            }
            catch { /* best-effort; the supervisor still tears down on any exit */ }
        });
    }

    private async Task FinishAsync(bool restart, string reason)
    {
        string state = restart ? "restarting for an emergency update" : "shutting down";
        await AnnounceAsync($"{BbsAnsi.BrightRed}{BbsAnsi.BgBlack}** The Realm is {state} now. **{BbsAnsi.Reset}");
        _requestStop(restart ? RestartExitCode : ShutdownExitCode, reason);
    }

    // Deliver a verbatim server notice to the whole board: front menu/login/realm-select clients, plus every
    // realm's in-game players (via each realm's realm_bus "system" broadcast).
    private async Task AnnounceAsync(string line)
    {
        await _host.BroadcastLineToMenuClientsAsync(line);

        string payload = JsonSerializer.Serialize(new { type = "system", msg = line });
        foreach (var realm in _registry.EnabledRealms)
        {
            if (string.IsNullOrWhiteSpace(realm.GameDb))
                continue;
            try
            {
                await using var conn = new NpgsqlConnection(realm.GameDb);
                await conn.OpenAsync();
                await using var notify = new NpgsqlCommand($"SELECT pg_notify('{RealmBusChannel}', @p);", conn);
                notify.Parameters.AddWithValue("p", payload);
                await notify.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                if (!_quietLogging)
                    Console.WriteLine($"[front] board announce to realm '{realm.Id}' failed (skipping): {ex.Message}");
            }
        }
    }
}

/// <summary>
/// BACKEND implementation: a player typing <c>;restart</c> in the mud is handled inside the realm backend,
/// which can't see the rest of the board. So this just publishes a <c>board_restart_request</c> onto the
/// realm's own <c>realm_bus</c>; the FRONT listens on every realm's bus and runs the real orchestration.
/// </summary>
public sealed class RelayBoardController : IBoardController
{
    private const string RealmBusChannel = "realm_bus";

    private readonly string _realmDbConnectionString;
    private readonly bool _quietLogging;

    public RelayBoardController(string realmDbConnectionString, bool quietLogging)
    {
        _realmDbConnectionString = realmDbConnectionString;
        _quietLogging = quietLogging;
    }

    public async Task RequestBoardStopAsync(int seconds, bool restart, string reason)
    {
        string payload = JsonSerializer.Serialize(new { type = "board_restart_request", seconds, restart, reason });
        try
        {
            await using var conn = new NpgsqlConnection(_realmDbConnectionString);
            await conn.OpenAsync();
            await using var notify = new NpgsqlCommand($"SELECT pg_notify('{RealmBusChannel}', @p);", conn);
            notify.Parameters.AddWithValue("p", payload);
            await notify.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            if (!_quietLogging)
                Console.WriteLine($"[backend] board {(restart ? "restart" : "shutdown")} relay failed: {ex.Message}");
        }
    }
}
