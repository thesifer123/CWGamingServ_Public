using System.Text.Json;
using Npgsql;

namespace CWGamingServ;

/// <summary>
/// Front-side realm-bus subscriber. Opens one persistent <c>LISTEN realm_bus</c> connection per realm DB so
/// the front can pick up a <c>board_restart_request</c> a realm BACKEND published when a sysop typed
/// <c>;restart</c> in the mud, and run the real board-wide orchestration through the
/// <see cref="FrontBoardController"/>. Reconnects with backoff if a connection drops; realms with no
/// configured DB are skipped. Only <c>board_restart_request</c> is acted on — every other realm-bus message
/// (gossip, telepath, the front's own <c>system</c> pushes, …) is ignored here.
/// </summary>
public sealed class FrontRealmBusListener
{
    private const string Channel = "realm_bus";

    private readonly IReadOnlyList<RealmDefinition> _realms;
    private readonly IBoardController _boardController;
    private readonly bool _quietLogging;
    private readonly CancellationTokenSource _cts = new();

    public FrontRealmBusListener(IReadOnlyList<RealmDefinition> realms, IBoardController boardController, bool quietLogging)
    {
        _realms = realms;
        _boardController = boardController;
        _quietLogging = quietLogging;
    }

    public void Start()
    {
        foreach (var realm in _realms)
        {
            if (string.IsNullOrWhiteSpace(realm.GameDb))
                continue;
            string connectionString = realm.GameDb;
            string realmId = realm.Id;
            _ = Task.Run(() => RunAsync(realmId, connectionString, _cts.Token));
        }
    }

    public void Stop()
    {
        try { _cts.Cancel(); }
        catch { /* already disposed */ }
    }

    private async Task RunAsync(string realmId, string connectionString, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ListenLoopAsync(connectionString, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_quietLogging)
                    Console.WriteLine($"[front-bus] listener for realm '{realmId}' error: {ex.Message}; reconnecting in 5s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ListenLoopAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        conn.Notification += OnNotification;

        await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", conn))
            await listen.ExecuteNonQueryAsync(ct);

        while (!ct.IsCancellationRequested)
            await conn.WaitAsync(ct);
    }

    private void OnNotification(object? sender, NpgsqlNotificationEventArgs e)
    {
        string payload = e.Payload;
        _ = Task.Run(() => DispatchAsync(payload));
    }

    private async Task DispatchAsync(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "board_restart_request")
                return;

            int seconds = root.TryGetProperty("seconds", out var s) && s.TryGetInt32(out int parsedSeconds) ? parsedSeconds : 20;
            bool restart = !root.TryGetProperty("restart", out var r) || r.ValueKind != JsonValueKind.False;
            string reason = root.TryGetProperty("reason", out var rs) ? (rs.GetString() ?? "in-realm sysop") : "in-realm sysop";

            await _boardController.RequestBoardStopAsync(seconds, restart, reason);
        }
        catch (Exception ex)
        {
            if (!_quietLogging)
                Console.WriteLine($"[front-bus] bad board-restart-request payload: {ex.Message}");
        }
    }
}
