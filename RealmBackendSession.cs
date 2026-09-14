using CWGaming.Shared;

namespace CWGamingServ;

/// <summary>
/// Backend-mode connection handler. A realm backend accepts ONLY front-proxied connections: it reads the
/// front's handshake (which vouches for an already-authenticated BBS account), verifies the shared secret,
/// then runs this realm for that account with no BBS login (the front already did it). The realm's own
/// pre-game menu ("Enter the Realm / Help / Counts / ...") still shows — it's part of the classic
/// experience — so we launch via <see cref="BbsInstalledAppLaunchMode.ShowMainMenu"/> /
/// <see cref="BbsInstalledAppLaunchMode.AutoAdvanceMainMenu"/> per the shared menuauto setting, exactly as
/// the monolithic build does. Backends must therefore listen on loopback only; the secret is the trust gate.
/// </summary>
public sealed class RealmBackendSession
{
    private readonly TelnetClient _client;
    private readonly IBbsUserRepository _bbsUserRepository;
    private readonly IBbsInstalledApp _installedApp;
    private readonly string _proxySecret;
    private readonly bool _quietLogging;

    public RealmBackendSession(TelnetClient client, IBbsUserRepository bbsUserRepository, IBbsInstalledApp installedApp, string proxySecret, bool quietLogging)
    {
        _client = client;
        _bbsUserRepository = bbsUserRepository;
        _installedApp = installedApp;
        _proxySecret = proxySecret;
        _quietLogging = quietLogging;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // 1. Read the front's handshake first, raw, before any telnet negotiation — the front becomes a
        //    transparent pipe immediately after sending it, so from here on the stream carries the real
        //    client's telnet traffic.
        string? line = await _client.ReadInternalHandshakeLineAsync(RealmProxyProtocol.MaxHandshakeLineLength, ct);
        if (line == null)
        {
            _client.Disconnect();
            return;
        }

        var handshake = RealmProxyProtocol.TryParse(line, _proxySecret, out string failureReason);
        if (handshake == null)
        {
            if (!_quietLogging)
                Console.WriteLine($"[{_client.Id}] backend rejected proxy handshake: {failureReason}");
            _client.Disconnect();
            return;
        }

        // 2. Resolve the account the front already authenticated — no password (the front vouches via the
        //    secret). Backends share the one cwgaming_bbs, so this is the same account store the front used.
        BbsUserAccount? account = _bbsUserRepository.LoadUser(handshake.UserName);
        if (account == null)
        {
            if (!_quietLogging)
                Console.WriteLine($"[{_client.Id}] backend: front-authenticated account '{handshake.UserName}' not found");
            _client.Disconnect();
            return;
        }

        // 3. Attribute presence to the real client the front captured, not the loopback front connection.
        if (!string.IsNullOrWhiteSpace(handshake.ClientIp))
            _client.BbsPresenceIpAddress = handshake.ClientIp;
        _client.CurrentBbsUserName = account.UserName;

        // 4. Negotiate telnet with the real client (through the transparent front), then go straight in.
        await _client.NegotiateAsync();
        await _client.DrainInitialClientHandshakeAsync(ct);

        // The front handled BBS login + realm selection, but the realm's OWN pre-game menu must still show
        // (dropping the player straight into the world was the regression). Honour the same menuauto toggle
        // the BBS/realm menus use: auto-advance on -> render the menu then auto-enter after the idle delay;
        // off -> render it and wait for the player. Never EnterRealmDirectly, which would skip it entirely.
        var launchMode = BbsMenuSettings.GetEffectiveAutoAdvanceEnabled(_bbsUserRepository)
            ? BbsInstalledAppLaunchMode.AutoAdvanceMainMenu
            : BbsInstalledAppLaunchMode.ShowMainMenu;

        try
        {
            await _installedApp.RunAsync(_client, account, launchMode, ct);
        }
        finally
        {
            // Returning to the "BBS" from a backend means returning to the FRONT's menu: just end the
            // proxied connection so the front regains control of the terminal.
            _client.Disconnect();
        }
    }
}
