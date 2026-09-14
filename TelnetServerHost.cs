using System.Net;
using System.Net.Sockets;
using CWGaming.Shared;

namespace CWGamingServ;

public sealed class TelnetServerHost : IBbsHost
{
    private readonly TcpListener _listener;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TelnetClient> _clients = [];
    private readonly object _clientsLock = new();
    private readonly IBbsDoorContext _door;
    private readonly IBbsCommandDispatcher _commandDispatcher;
    private readonly IBbsUserRepository _bbsUserRepository;
    private readonly IBbsInstalledApp _installedApp;
    private readonly BbsIpAccessControl _ipAccessControl;
    private readonly bool _markNewAccountsAsTestAccounts;
    private readonly bool _quietTestLogging;
    private readonly ServerRole _role;
    private readonly string _proxySecret;
    private readonly RealmRegistry? _realmRegistry;

    public TelnetServerHost(int port, IBbsDoorContext door, IBbsCommandDispatcher commandDispatcher, IBbsUserRepository bbsUserRepository, IBbsInstalledApp installedApp, BbsIpAccessControl ipAccessControl, bool markNewAccountsAsTestAccounts = false, ServerRole role = ServerRole.Monolithic, string? proxySecret = null, RealmRegistry? realmRegistry = null)
    {
        _port = port;
        _door = door;
        _commandDispatcher = commandDispatcher;
        _bbsUserRepository = bbsUserRepository;
        _installedApp = installedApp;
        _ipAccessControl = ipAccessControl;
        _markNewAccountsAsTestAccounts = markNewAccountsAsTestAccounts;
        _quietTestLogging = RuntimeConfiguration.IsQuietTestLoggingEnabled();
        _role = role;
        _proxySecret = proxySecret ?? RealmProxyProtocol.ResolveProxySecret();
        _realmRegistry = realmRegistry;
        // A realm backend accepts only front-proxied connections — bind loopback so it is never reachable
        // by real clients. The front and the legacy monolithic listen on all interfaces as before.
        _listener = new TcpListener(role == ServerRole.Backend ? IPAddress.Loopback : IPAddress.Any, port);
    }

    public async Task StartAsync()
    {
        _listener.Start();
        Console.WriteLine($"CWGamingServ telnet host listening on port {_port}...");

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener.AcceptTcpClientAsync(_cts.Token);
                var client = new TelnetClient(tcpClient);

                // Enforce the sysop denylist + per-IP connection cap BEFORE the telnet handshake/login.
                // We read the raw socket IP (the forwarded-header capture has not run yet); with WSL2
                // mirrored networking that is the real client IP. A denied connection is closed here and
                // never reaches a session, so it can't consume a door slot.
                string remoteIp = client.RemoteAddress;
                // Backends take only loopback connections from the trusted front, which already enforced
                // the denylist + per-IP cap for the real client — so skip the gate here (it would meter the
                // loopback front address, not the player).
                if (_role != ServerRole.Backend)
                {
                    var decision = _ipAccessControl.TryBeginConnection(remoteIp);
                    if (decision != BbsIpAccessControl.ConnectionDecision.Allowed)
                    {
                        if (!_quietTestLogging)
                            Console.WriteLine($"[{client.Id}] Connection from {remoteIp} rejected: {decision}");

                        _ = RejectConnectionAsync(client, decision);
                        continue;
                    }
                }

                lock (_clientsLock)
                    _clients.Add(client);

                _ = Task.Run(() => HandleClient(client, remoteIp));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Accept error: {ex.Message}");
            }
        }
    }

    private static async Task RejectConnectionAsync(TelnetClient client, BbsIpAccessControl.ConnectionDecision decision)
    {
        try
        {
            string message = decision == BbsIpAccessControl.ConnectionDecision.Banned
                ? "Your address has been blocked by the sysop."
                : "Too many simultaneous connections from your address.";
            await client.SendLineAsync(message);
        }
        catch
        {
            // The peer may already be gone; closing is all that matters.
        }
        finally
        {
            client.Disconnect();
        }
    }

    private async Task HandleClient(TelnetClient client, string remoteIp)
    {
        try
        {
            if (_role == ServerRole.Backend)
            {
                // Backend: no forwarded-address capture and no BBS login — read the front's handshake and
                // run this realm directly for the account it vouches for.
                var backendSession = new RealmBackendSession(client, _bbsUserRepository, _installedApp, _proxySecret, _quietTestLogging);
                await backendSession.RunAsync(_cts.Token);
                return;
            }

            if (!_quietTestLogging)
                Console.WriteLine($"[{client.Id}] Client connected");
            await client.TryCaptureForwardedRemoteAddressAsync(_cts.Token);
            await client.NegotiateAsync();
            // Consume the client's IAC-negotiation reply and any stray CR/LF/NUL from its
            // connect-time handshake so the first login prompt isn't read as an empty input.
            await client.DrainInitialClientHandshakeAsync(_cts.Token);

            IRealmLauncher launcher = _role == ServerRole.Front && _realmRegistry != null
                ? new FrontRealmLauncher(_realmRegistry, _proxySecret, _quietTestLogging, _bbsUserRepository, _commandDispatcher, _door, DisconnectOtherConnectionsForAccountInRealm)
                : new InstalledAppRealmLauncher(_installedApp, DisconnectOtherConnectionsForAccountInRealm);
            var session = new BbsDoorSession(client, _door, _commandDispatcher, _bbsUserRepository, launcher, _markNewAccountsAsTestAccounts, DisconnectOtherConnectionsForAccount);
            await session.RunAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{client.Id}] Error: {ex}");
        }
        finally
        {
            if (!_quietTestLogging)
            {
                // Always say WHY. A bare "Client disconnected" is unfalsifiable: a board-initiated kick, a
                // player typing QUIT and a link that died all looked identical in the log, so diagnosing a
                // player's "I keep getting disconnected" meant guessing. The kernel's own liveness reading
                // separates "their peer stopped answering" from "we closed it, here is the reason".
                string cause = client.HostDisconnectReason ?? $"peer left or link failed ({client.DescribePeerLiveness()})";
                Console.WriteLine($"[{client.Id}] Client disconnected — {cause} [{remoteIp}"
                    + (string.IsNullOrWhiteSpace(client.CurrentBbsUserName) ? "" : $", '{client.CurrentBbsUserName}'")
                    + (string.IsNullOrEmpty(client.CurrentRealmId) ? "" : $", realm '{client.CurrentRealmId}'")
                    + $", up {(DateTime.UtcNow - client.ConnectedAtUtc).TotalMinutes:F1}m]");
            }
            _door.HandleDroppedConnection(client);

            client.Disconnect();
            if (_role != ServerRole.Backend)
                _ipAccessControl.EndConnection(remoteIp);
            lock (_clientsLock)
                _clients.Remove(client);
        }
    }

    public IReadOnlyList<BbsPresenceSnapshot> GetBbsPresenceSnapshots()
    {
        lock (_clientsLock)
        {
            return _clients
                .Select(CreateBbsPresenceSnapshot)
                .Where(snapshot => snapshot != null)
                .Cast<BbsPresenceSnapshot>()
                .OrderBy(snapshot => snapshot.UserName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Send a line to every client sitting on the front (login prompt, BBS menu, realm-select) — i.e. NOT
    /// currently proxied into a realm. Used for board-wide notices like a restart countdown; in-realm
    /// players are skipped here because they receive the notice through their realm's own broadcast, and
    /// writing to a live proxy pipe would corrupt the game byte stream. Best-effort per client.
    /// </summary>
    public async Task BroadcastLineToMenuClientsAsync(string line)
    {
        TelnetClient[] targets;
        lock (_clientsLock)
            targets = _clients.Where(c => !c.IsProxying).ToArray();

        foreach (var client in targets)
        {
            try { await client.SendLineAsync(line); }
            catch { /* client may be mid-disconnect; a restart notice is best-effort */ }
        }
    }

    /// <summary>
    /// Login-time GHOST sweep: drop the account's other connections that the kernel can PROVE are gone.
    ///
    /// This used to drop every other connection for the account, unconditionally, on the theory that a
    /// newly authenticating connection is by definition the account's newest. That reasoning is wrong the
    /// moment one account is allowed more than one live session — which the multi-realm board exists to
    /// allow (one login, a character in Main AND one in PvP). The result was a self-sustaining kick loop:
    /// the client in Main signs in and kills the one in PvP, whose auto-reconnect signs in and kills Main,
    /// forever, roughly once a minute. Both players were fine; the board was disconnecting them.
    ///
    /// So liveness decides, and it is measured rather than assumed — see TelnetClient.IsPeerProvablyDead,
    /// which reads the kernel's own keepalive evidence. A connection that is answering probes is a player,
    /// even a player who has not typed for an hour, and is left alone. The duplicate-session rule itself
    /// is enforced per realm at realm entry (see <see cref="DisconnectOtherConnectionsForAccountInRealm"/>),
    /// where the realm is actually known — at login it is not.
    ///
    /// Closing the socket is all this does: the owning HandleClient task unwinds and its finally block runs
    /// the normal teardown (HandleDroppedConnection), which is what removes any realm character. Matching is
    /// on CurrentBbsUserName, the login identity, not the public presence handle.
    /// </summary>
    private void DisconnectOtherConnectionsForAccount(string bbsUserName, IBbsConnection newConnection)
    {
        if (string.IsNullOrWhiteSpace(bbsUserName))
            return;

        TelnetClient[] others;
        lock (_clientsLock)
        {
            others = _clients
                .Where(c => !ReferenceEquals(c, newConnection)
                            && c.CurrentBbsUserName.Equals(bbsUserName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        TimeSpan budget = TelnetClient.GhostSilenceBudget;
        foreach (var c in others)
        {
            if (!c.IsPeerProvablyDead(budget, out string diagnosis))
            {
                if (!_quietTestLogging)
                    Console.WriteLine($"[{c.Id}] Keeping the other live session for '{bbsUserName}' ({c.RemoteAddress}, realm '{DescribeRealm(c)}') — {diagnosis}");
                continue;
            }

            if (!_quietTestLogging)
                Console.WriteLine($"[{c.Id}] Dropping ghost session for '{bbsUserName}' ({c.RemoteAddress}, realm '{DescribeRealm(c)}') — {diagnosis}");
            c.DisconnectByHost($"ghost cleared on re-login: {diagnosis}");
        }
    }

    /// <summary>
    /// Realm-entry duplicate rule: one account may hold ONE session per realm. Called as a connection
    /// enters <paramref name="realmId"/>, it drops the account's other connections already inside that
    /// same realm — including live ones, because two clients driving one account in one world is the
    /// case the guard is actually for (and the door's own same-character check is only a backstop).
    /// Connections of the same account sitting on the front, or playing a DIFFERENT realm, are untouched.
    /// </summary>
    private void DisconnectOtherConnectionsForAccountInRealm(string bbsUserName, string realmId, IBbsConnection newConnection)
    {
        if (string.IsNullOrWhiteSpace(bbsUserName))
            return;

        TelnetClient[] duplicates;
        lock (_clientsLock)
        {
            duplicates = _clients
                .Where(c => !ReferenceEquals(c, newConnection)
                            && c.CurrentBbsUserName.Equals(bbsUserName, StringComparison.OrdinalIgnoreCase)
                            && c.CurrentRealmId.Equals(realmId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        string from = (newConnection as TelnetClient)?.RemoteAddress ?? "another connection";
        foreach (var c in duplicates)
        {
            if (!_quietTestLogging)
                Console.WriteLine($"[{c.Id}] Dropping duplicate session for '{bbsUserName}' ({c.RemoteAddress}) — the account entered realm '{realmId}' again from {from} ({c.DescribePeerLiveness()})");
            c.TrySendFarewellLine($"*** Your account entered this realm from another connection ({from}). ***");
            c.DisconnectByHost($"same-realm duplicate login from {from}");
        }
    }

    private static string DescribeRealm(TelnetClient client)
        => string.IsNullOrEmpty(client.CurrentRealmId) ? "-" : client.CurrentRealmId;

    public void Stop()
    {
        _cts.Cancel();
        _listener.Stop();
        lock (_clientsLock)
        {
            foreach (var c in _clients)
                c.DisconnectByHost("the board is shutting down");
            _clients.Clear();
        }
    }

    private static BbsPresenceSnapshot? CreateBbsPresenceSnapshot(TelnetClient client)
    {
        // The mounted door attaches a generic IBbsIdentity (via AppData); the host reads who is connected
        // and where they are without referencing any game type. Null at the BBS login / menu.
        var identity = client.AppData as IBbsIdentity;
        string userName = client.BbsPresenceUserName;
        if (string.IsNullOrWhiteSpace(userName) && identity != null)
            userName = identity.PresenceUserName;

        if (string.IsNullOrWhiteSpace(userName))
            return null;

        string location = !string.IsNullOrWhiteSpace(identity?.PresenceLocation)
            ? identity.PresenceLocation
            : string.IsNullOrWhiteSpace(client.BbsPresenceLocation) ? "Logging In" : client.BbsPresenceLocation;

        int minutesOnline = (int)Math.Max(0, (DateTime.UtcNow - client.ConnectedAtUtc).TotalMinutes);
        return new BbsPresenceSnapshot(userName, client.BbsPresenceIsSysop, minutesOnline, location, client.BbsPresenceIpAddress);
    }
}
