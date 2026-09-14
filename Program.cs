using System.Runtime.InteropServices;
using System.Text;
using CWGaming.Shared;
using Npgsql;

namespace CWGamingServ;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        string bbsPostgresConnectionString = RuntimeConfiguration.ResolveBbsPostgresConnectionString();
        string bbsApiBaseUrl = RuntimeConfiguration.ResolveBbsApiBaseUrl();
        string bbsApiKey = RuntimeConfiguration.ResolveBbsApiKey();
        int port = ResolvePort(args);

        Console.WriteLine($"BBS PostgreSQL: {FormatConnectionString(bbsPostgresConnectionString)}");
        Console.WriteLine($"BBS API: {bbsApiBaseUrl}");

        // BBS-side infrastructure the host owns. The direct repo backs the in-process BBS API; the door's
        // world reaches BBS accounts over HTTP through that API (so a door could run out-of-process too).
        var directBbsUserRepo = new BbsUserRepository(bbsPostgresConnectionString);

        using var bbsApiServer = new BbsApiServer(bbsApiBaseUrl, directBbsUserRepo, bbsApiKey);
        bbsApiServer.Start();

        using var gameBbsUserRepo = new HttpBbsUserRepository(bbsApiBaseUrl, bbsApiKey);
        var bbsTicketRepo = new BbsTicketRepository(bbsPostgresConnectionString);
        // Sysop IP denylist + per-IP connection cap, persisted via the BBS settings store and enforced
        // at accept time. Shared between the dispatcher (;ban/;unban/;bans/;maxconn) and the host (gate).
        var ipAccessControl = new BbsIpAccessControl(directBbsUserRepo);
        var bbsCommandDispatcher = new BbsCommandDispatcher(directBbsUserRepo, bbsTicketRepo, ipAccessControl);

        var role = RealmProxyProtocol.ResolveRole();
        Console.WriteLine($"Server role: {role}");
        var bbsApiServerRef = bbsApiServer;
        int shutdownStarted = 0;

        // FRONT role: a pure router. It loads NO world — it owns the public port, does BBS login + the
        // realm menu, and proxies each chosen connection to that realm's backend process. All realms
        // (including "main") run as separate backends, so isolation is total (architecture A).
        if (role == ServerRole.Front)
        {
            var realmRegistry = RealmRegistry.Load();
            var enabledRealms = realmRegistry.EnabledRealms;
            Console.WriteLine($"Front router: {enabledRealms.Count} realm(s): " +
                string.Join(", ", enabledRealms.Select(r => $"{r.DisplayName} -> {r.BackendHost}:{r.BackendPort}")));

            var presenceReader = new RealmPresenceReader(enabledRealms, RuntimeConfiguration.IsQuietTestLoggingEnabled());
            var frontContext = new FrontDoorContext(presenceReader);
            var frontHost = new TelnetServerHost(port, frontContext, bbsCommandDispatcher, directBbsUserRepo,
                NoopInstalledApp.Instance, ipAccessControl,
                role: ServerRole.Front, proxySecret: RealmProxyProtocol.ResolveProxySecret(), realmRegistry: realmRegistry);
            frontContext.Host = frontHost;

            FrontRealmBusListener? boardBusListener = null;
            void FrontShutdown(int exitCode, string reason)
            {
                if (Interlocked.Exchange(ref shutdownStarted, 1) == 1)
                    return;
                Console.WriteLine($"Front shutting down ({reason})...");
                try { boardBusListener?.Stop(); } catch { }
                try { frontHost.Stop(); } catch { }
                try { bbsApiServerRef.Stop(); } catch { }
                Environment.Exit(exitCode);
            }

            // Board-wide `;restart`/`;shutdown`: the front owns every realm's DB + the menu-client list, so it
            // warns everyone and relaunches the whole topology (exit 42) without any realm having to signal up.
            var frontBoardController = new FrontBoardController(
                realmRegistry, frontHost, FrontShutdown, RuntimeConfiguration.IsQuietTestLoggingEnabled());
            bbsCommandDispatcher.BoardController = frontBoardController;

            // Also listen on every realm's bus so a `;restart` typed INSIDE the mud (handled by that realm's
            // backend, which can't see the board) relays up here and runs the same board-wide orchestration.
            boardBusListener = new FrontRealmBusListener(
                enabledRealms, frontBoardController, RuntimeConfiguration.IsQuietTestLoggingEnabled());
            boardBusListener.Start();

            Console.CancelKeyPress += (_, e) => { e.Cancel = true; FrontShutdown(0, "Ctrl+C"); };
            using var frontSigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; FrontShutdown(0, "SIGTERM"); });

            Console.WriteLine($"CWGamingServ FRONT started on port {port}");
            Console.WriteLine($"Connect with: telnet localhost {port}");
            Console.WriteLine("Press Ctrl+C to stop.");
            try { await frontHost.StartAsync(); } catch (OperationCanceledException) { }
            return;
        }

        // MONOLITHIC (legacy default) or BACKEND: load and run the door (the game world). A BACKEND binds
        // loopback and accepts only front-proxied, pre-authenticated connections; MONOLITHIC does login +
        // in-process realm exactly as before. Discover the door by reflection over the Shared factory so
        // the host holds no compile-time reference to any door.
        var doorFactory = DoorLoader.LoadDoorFactory();
        var door = doorFactory.Create(new BbsDoorServices
        {
            CommandDispatcher = bbsCommandDispatcher,
            BbsUserRepository = gameBbsUserRepo,
            StartupArgs = args,
        });
        Console.WriteLine($"Loaded door: {door.AppId} (role {role})");

        var host = new TelnetServerHost(port, door.Context, bbsCommandDispatcher, directBbsUserRepo, door.InstalledApp, ipAccessControl,
            role: role, proxySecret: RealmProxyProtocol.ResolveProxySecret());
        door.AttachHost(host);

        // A realm BACKEND handles `;restart` typed in the mud but can't see the rest of the board, so it
        // relays the request up to the front over its own realm_bus (the front listens on every realm's bus).
        // MONOLITHIC has no front to relay to, so it leaves the board controller unset (`;restart` reports it
        // is a multi-realm-only command there).
        if (role == ServerRole.Backend)
        {
            bbsCommandDispatcher.BoardController = new RelayBoardController(
                RuntimeConfiguration.ResolveGamePostgresConnectionString(), RuntimeConfiguration.IsQuietTestLoggingEnabled());
        }
        // One graceful path for Ctrl+C, SIGTERM (docker stop / systemd), and a sysop restart/shutdown
        // request routed up from the door: persist world state, stop accepting, then exit with the
        // requested code so the supervisor knows whether to relaunch (door RestartExitCode) or stop.
        void GracefulShutdown(int exitCode, string reason)
        {
            if (Interlocked.Exchange(ref shutdownStarted, 1) == 1)
                return;
            Console.WriteLine($"Shutting down ({reason}); saving world state...");
            try { door.Stop(); } catch (Exception ex) { Console.WriteLine($"door.Stop failed: {ex.Message}"); }
            try { host.Stop(); } catch { }
            try { bbsApiServerRef.Stop(); } catch { }
            Console.WriteLine($"Shutdown complete (exit {exitCode}).");
            Environment.Exit(exitCode);
        }

        door.OnShutdownRequested = (exitCode, reason) => GracefulShutdown(exitCode, reason);
        door.Start();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            GracefulShutdown(door.ShutdownExitCode, "Ctrl+C");
        };
        // SIGTERM is what `docker stop` / systemd send — without this, the previous Ctrl+C-only hook
        // skipped the save and ground items were lost on every non-interactive stop.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            GracefulShutdown(door.ShutdownExitCode, "SIGTERM");
        });

        Console.WriteLine($"CWGamingServ BBS host started on port {port}");
        Console.WriteLine($"Connect with: telnet localhost {port}");
        Console.WriteLine("Press Ctrl+C to stop.");

        try
        {
            await host.StartAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static int ResolvePort(string[] args)
    {
        if (args.Length > 0 && int.TryParse(args[0], out var cliPort) && cliPort > 0)
            return cliPort;

        var envPort = Environment.GetEnvironmentVariable("MMUDREBORN_PORT");
        if (int.TryParse(envPort, out var configuredPort) && configuredPort > 0)
            return configuredPort;

        return 2323;
    }

    private static string FormatConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(builder.Password))
            builder.Password = "******";

        return builder.ToString();
    }
}
