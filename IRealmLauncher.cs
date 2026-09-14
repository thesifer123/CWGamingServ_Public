using System.Net.Sockets;
using System.Text;
using CWGaming.Shared;
using Ansi = CWGamingServ.BbsAnsi;

namespace CWGamingServ;

/// <summary>
/// What the BBS main menu's "Play" action does. Abstracted so the same login/menu code
/// (<see cref="BbsDoorSession"/>) serves both the legacy monolithic build (run the in-process door) and
/// the multi-realm front (choose a realm, then proxy to its backend process).
/// </summary>
public interface IRealmLauncher
{
    Task<BbsInstalledAppResult> PlayAsync(IBbsConnection client, BbsUserAccount account, BbsInstalledAppLaunchMode launchMode, CancellationToken ct);
}

/// <summary>
/// Drop the account's OTHER connections that are already inside the given realm. Supplied by the host,
/// which is the only thing that knows every connection. One account may hold one session per realm, so
/// this is the point the rule is enforced — the realm is not known any earlier than realm entry.
/// </summary>
public delegate void DisconnectDuplicateRealmSessions(string bbsUserName, string realmId, IBbsConnection newConnection);

/// <summary>Monolithic launcher: run the single in-process door, exactly as before.</summary>
public sealed class InstalledAppRealmLauncher : IRealmLauncher
{
    // One in-process world, so every entry is an entry into the same realm: this id just gives the
    // per-realm duplicate rule something to compare, and keeps monolithic behaviour as it was
    // (one session per account, because there is only one realm to hold it in).
    private const string MonolithicRealmId = "default";

    private readonly IBbsInstalledApp _installedApp;
    private readonly DisconnectDuplicateRealmSessions? _disconnectDuplicateRealmSessions;

    public InstalledAppRealmLauncher(IBbsInstalledApp installedApp, DisconnectDuplicateRealmSessions? disconnectDuplicateRealmSessions = null)
    {
        _installedApp = installedApp;
        _disconnectDuplicateRealmSessions = disconnectDuplicateRealmSessions;
    }

    public async Task<BbsInstalledAppResult> PlayAsync(IBbsConnection client, BbsUserAccount account, BbsInstalledAppLaunchMode launchMode, CancellationToken ct)
    {
        if (client is TelnetClient telnet)
            telnet.CurrentRealmId = MonolithicRealmId;
        _disconnectDuplicateRealmSessions?.Invoke(account.UserName, MonolithicRealmId, client);

        try
        {
            return await _installedApp.RunAsync(client, account, launchMode, ct);
        }
        finally
        {
            if (client is TelnetClient back)
                back.CurrentRealmId = "";
        }
    }
}

/// <summary>
/// Front launcher: offer the realm menu (skipped when only one realm is configured), then proxy the
/// connection to the chosen realm's backend game process. When the proxied leg ends (the player left the
/// realm, or a hangup / <c>=x</c>), control returns to the BBS menu.
/// </summary>
public sealed class FrontRealmLauncher : IRealmLauncher
{
    // How long the realm menu waits for input before menuauto picks the default realm — matches the
    // BBS main menu's 10s auto-advance delay so the two steps feel consistent.
    private const int RealmMenuAutoAdvanceDelayMs = 10_000;

    private readonly RealmRegistry _registry;
    private readonly string _proxySecret;
    private readonly bool _quietLogging;
    private readonly IBbsUserRepository? _bbsUserRepository;
    private readonly IBbsCommandDispatcher? _commandDispatcher;
    private readonly IBbsDoorContext? _doorContext;
    private readonly DisconnectDuplicateRealmSessions? _disconnectDuplicateRealmSessions;

    public FrontRealmLauncher(RealmRegistry registry, string proxySecret, bool quietLogging,
        IBbsUserRepository? bbsUserRepository = null, IBbsCommandDispatcher? commandDispatcher = null, IBbsDoorContext? doorContext = null,
        DisconnectDuplicateRealmSessions? disconnectDuplicateRealmSessions = null)
    {
        _registry = registry;
        _proxySecret = proxySecret;
        _quietLogging = quietLogging;
        _bbsUserRepository = bbsUserRepository;
        _commandDispatcher = commandDispatcher;
        _doorContext = doorContext;
        _disconnectDuplicateRealmSessions = disconnectDuplicateRealmSessions;
    }

    public async Task<BbsInstalledAppResult> PlayAsync(IBbsConnection client, BbsUserAccount account, BbsInstalledAppLaunchMode launchMode, CancellationToken ct)
    {
        var realms = _registry.EnabledRealms;
        if (realms.Count == 0)
        {
            await client.SendLineAsync(Ansi.Error("No realms are currently available. Please try again later."));
            return BbsInstalledAppResult.ReturnedToBbs;
        }

        RealmDefinition? chosen;
        if (realms.Count == 1)
        {
            chosen = realms[0];
        }
        else
        {
            // menuauto ON = never make the player stop on a menu. Two ways it engages here: the BBS main
            // menu already auto-advanced (launchMode == AutoAdvanceMainMenu) so we pick immediately, or the
            // player reached this screen and just idles — in which case we auto-pick after the same 10s wait.
            bool menuAutoEnabled = BbsMenuSettings.GetEffectiveAutoAdvanceEnabled(_bbsUserRepository);
            bool advanceNow = menuAutoEnabled && launchMode == BbsInstalledAppLaunchMode.AutoAdvanceMainMenu;
            chosen = await ChooseRealmAsync(client, realms, advanceNow, menuAutoEnabled, account.UserName, ct);
            if (chosen == null)
                return client.Connected ? BbsInstalledAppResult.ReturnedToBbs : BbsInstalledAppResult.Disconnected;
        }

        return await ProxyToRealmAsync(client, account, chosen, ct);
    }

    private async Task<RealmDefinition?> ChooseRealmAsync(IBbsConnection client, IReadOnlyList<RealmDefinition> realms, bool advanceNow, bool menuAutoEnabled, string viewerBbsUserName, CancellationToken ct)
    {
        // "Back" sits after the realm entries (e.g. two realms -> Back is 3), so the numbering reads
        // top-to-bottom instead of the odd "(0) Back".
        int backChoice = realms.Count + 1;

        while (client.Connected && !ct.IsCancellationRequested)
        {
            await client.SendLineAsync();
            await client.SendLineAsync($"{Ansi.BrightWhite}Choose a realm{Ansi.Reset}");
            await client.SendLineAsync();
            for (int i = 0; i < realms.Count; i++)
                await client.SendLineAsync($"{Ansi.BrightYellow}({i + 1}){Ansi.Reset} {realms[i].DisplayName}");
            await client.SendLineAsync($"{Ansi.BrightYellow}({backChoice}){Ansi.Reset} Back");
            await client.SendLineAsync();
            await client.SendAsync($"{Ansi.BrightWhite}Realm: {Ansi.Reset}");

            // menuauto: pick the default realm (option 1 = lowest-Order, "Main") and echo the choice,
            // mirroring the BBS main menu's auto-advance so an idle login flows all the way into the game.
            if (advanceNow)
            {
                await client.SendLineAsync("1");
                return realms[0];
            }

            // With menuauto on, wait only 10s for a choice before auto-picking; otherwise block for input.
            string? input = menuAutoEnabled
                ? await client.ReadLineEchoAsync(RealmMenuAutoAdvanceDelayMs, echo: true, ct)
                : await client.ReadLineEchoAsync(echo: true, ct);
            if (input == null)
                return null; // disconnected
            if (input == GameTransportConstants.ReadTimeoutSentinel)
            {
                await client.SendLineAsync("1");
                return realms[0]; // idle + menuauto -> default realm
            }

            input = input.Trim();
            if (input.Length == 0)
                continue;

            // Route BBS commands (;restart, ;who, ;o / =x, ;?, …) exactly as the other BBS menus do, so this
            // screen isn't a dead end for them. Non-command input (a realm number, "back") returns false and
            // falls through to the choice handling below.
            if (_commandDispatcher != null && _doorContext != null &&
                await _commandDispatcher.TryDispatchAsync(input, client, _doorContext, viewerBbsUserName))
            {
                if (!client.Connected)
                    return null; // e.g. an emergency logoff dropped the connection
                continue; // redraw the realm menu
            }

            if (input == backChoice.ToString() || input is "q" or "Q" or "x" or "X")
                return null; // back to BBS menu

            if (int.TryParse(input, out int choice) && choice >= 1 && choice <= realms.Count)
                return realms[choice - 1];

            await client.SendLineAsync(Ansi.Error($"Please choose 1-{realms.Count}, or {backChoice} to go back."));
        }

        return null;
    }

    private async Task<BbsInstalledAppResult> ProxyToRealmAsync(IBbsConnection client, BbsUserAccount account, RealmDefinition realm, CancellationToken ct)
    {
        if (client is not TelnetClient telnet)
            throw new InvalidOperationException("The realm front proxy requires a telnet transport.");

        TcpClient? backend = null;
        try
        {
            backend = new TcpClient { NoDelay = true };
            await backend.ConnectAsync(realm.BackendHost, realm.BackendPort, ct);

            // Claim this realm for the connection BEFORE the duplicate sweep, so a simultaneous entry by
            // the same account cannot see an unclaimed slot and let both in; then clear the account's other
            // sessions in THIS realm only. Sessions of the same account in other realms are exactly what
            // the multi-realm board promises, and are left running.
            telnet.CurrentRealmId = realm.Id;
            _disconnectDuplicateRealmSessions?.Invoke(account.UserName, realm.Id, telnet);

            // Vouch for the already-authenticated account, then become a transparent pipe. The backend
            // reads this line first and runs the realm directly.
            string handshake = RealmProxyProtocol.FormatHandshake(_proxySecret, account.UserName, account.DisplayName, telnet.RemoteAddress);
            byte[] bytes = Encoding.ASCII.GetBytes(handshake);
            var backendStream = backend.GetStream();
            await backendStream.WriteAsync(bytes, ct);
            await backendStream.FlushAsync(ct);

            await telnet.RunRawProxyAsync(backend, ct);
        }
        catch (Exception ex)
        {
            if (!_quietLogging)
                Console.WriteLine($"[{telnet.Id}] realm proxy to '{realm.Id}' ({realm.BackendHost}:{realm.BackendPort}) failed: {ex.Message}");
            try { await client.SendLineAsync(Ansi.Error($"Could not reach realm '{realm.DisplayName}'. Please try another.")); }
            catch { /* client may be gone */ }
        }
        finally
        {
            // Back on the front (BBS menu) — no longer holding this realm's one-session-per-account slot.
            telnet.CurrentRealmId = "";
            try { backend?.Close(); } catch { }
        }

        if (!client.Connected)
            return BbsInstalledAppResult.Disconnected;

        // Player left the realm but is still on the board: re-assert the front's terminal mode for the BBS
        // menu (the backend re-negotiated telnet for gameplay), then return to the menu.
        try
        {
            await telnet.NegotiateAsync();
            await telnet.DrainInitialClientHandshakeAsync(ct);
        }
        catch { /* best-effort; menu read will still work */ }

        return BbsInstalledAppResult.ReturnedToBbs;
    }
}
