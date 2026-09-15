using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CWGaming.Shared;
using Ansi = CWGamingServ.BbsAnsi;

namespace CWGamingServ;

public sealed class BbsDoorSession
{
    private const string WelcomeArtFileName = "mmud_reborn_80x24_centered_symmetry_v2.ans";
    private const string PreLoginMenuEnabledEnvironmentVariable = "MMUDREBORN_BBS_MENU_ENABLED";
    private static readonly TimeSpan LoginPromptDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LoginRetryDelay = TimeSpan.FromMilliseconds(325);
    private static readonly TimeSpan BbsMenuAutoAdvanceDelay = TimeSpan.FromSeconds(10);

    private readonly IBbsConnection _client;
    private readonly IBbsDoorContext _door;
    private readonly IBbsCommandDispatcher _commandDispatcher;
    private readonly IBbsUserRepository _bbsUserRepository;
    private readonly IRealmLauncher _realmLauncher;
    private readonly bool _markNewAccountsAsTestAccounts;
    private readonly Action<string, IBbsConnection>? _disconnectOtherConnectionsForAccount;
    private static string? _cachedWelcomeArt;

    private sealed record BbsMenuResult(bool AutoAdvanceMudMenu);
    private sealed record LoginSession(BbsUserAccount Account);

    public BbsDoorSession(IBbsConnection client, IBbsDoorContext door, IBbsCommandDispatcher commandDispatcher, IBbsUserRepository bbsUserRepository, IRealmLauncher realmLauncher, bool markNewAccountsAsTestAccounts = false, Action<string, IBbsConnection>? disconnectOtherConnectionsForAccount = null)
    {
        _client = client;
        _door = door;
        _commandDispatcher = commandDispatcher;
        _bbsUserRepository = bbsUserRepository;
        _realmLauncher = realmLauncher;
        _markNewAccountsAsTestAccounts = markNewAccountsAsTestAccounts;
        _disconnectOtherConnectionsForAccount = disconnectOtherConnectionsForAccount;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        SetHostedAppContext(HostedAppIds.Bbs, string.Empty);
        await ShowWelcome();

        var login = await LoginLoop(ct);
        if (login == null)
            return;

        // GHOSTING. An account authenticating here may still have OTHER connections signed in, but on a
        // multi-realm board those can be perfectly healthy sessions in another realm (Main and PvP at once).
        // So this drops only the ones that are provably DEAD — the kernel's keepalive evidence shows no ACK
        // from the peer within the ghost-silence budget (TelnetServerHost.DisconnectOtherConnectionsForAccount).
        // A live session is left alone however long it has been idle. The one-session-PER-REALM rule is
        // enforced at realm entry (DisconnectOtherConnectionsForAccountInRealm), where the realm is known.
        //
        // Nearly every player runs auto-reconnect software, which makes this the fast path back after a
        // dropped link: without it a reconnecting client had to clear BBS login, the BBS menu (10s
        // auto-advance), the MUD menu and the realm picker (another 10s) before its dead ghost was taken out
        // of the world — twenty-odd seconds of standing in a room being attacked with nobody at the keyboard,
        // and forever if the software only reopens the socket and waits for a human. Dropping the dead
        // CONNECTION here reuses the whole existing teardown chain: the proxy legs unwind, the backend socket
        // closes, and the door's HandleDroppedConnection pulls the character out of the world.
        _disconnectOtherConnectionsForAccount?.Invoke(login.Account.UserName, _client);

        SetPresenceLocation("Main Menu");

        await _client.SendLineAsync();
        await _client.SendLineAsync(Ansi.BrightGreen + $"Welcome back, {ResolveDisplayName(login.Account)}!" + Ansi.Reset);
        await _client.SendLineAsync();

        while (!ct.IsCancellationRequested && _client.Connected)
        {
            var appResult = await ShowPreLoginMenusAsync(login, ct);
            if (appResult != BbsInstalledAppResult.ReturnedToBbs || !_client.Connected)
                return;
            SetPresenceLocation("Main Menu");
        }
    }

    private async Task<BbsInstalledAppResult> ShowPreLoginMenusAsync(LoginSession login, CancellationToken ct)
    {
        if (!IsPreLoginMenuEnabled())
            return await _realmLauncher.PlayAsync(_client, login.Account, BbsInstalledAppLaunchMode.EnterRealmDirectly, ct);

        while (!ct.IsCancellationRequested && _client.Connected)
        {
            var bbsChoice = await ShowBbsMainMenuAsync(login, ct);
            if (bbsChoice == null)
                return BbsInstalledAppResult.Disconnected;

            var launchMode = bbsChoice.AutoAdvanceMudMenu
                ? BbsInstalledAppLaunchMode.AutoAdvanceMainMenu
                : BbsInstalledAppLaunchMode.ShowMainMenu;

            return await _realmLauncher.PlayAsync(_client, login.Account, launchMode, ct);
        }

        return BbsInstalledAppResult.Disconnected;
    }

    private async Task<BbsMenuResult?> ShowBbsMainMenuAsync(LoginSession login, CancellationToken ct)
    {
        string viewerBbsUserName = login.Account.UserName;
        while (!ct.IsCancellationRequested && _client.Connected)
        {
            SetHostedAppContext(HostedAppIds.Bbs, string.Empty);
            SetPresenceLocation("Main Menu");
            await _client.SendLineAsync($"{Ansi.BrightWhite}Welcome - Please choose what you want to do{Ansi.Reset}");
            await _client.SendLineAsync();
            await _client.SendLineAsync($"{Ansi.BrightYellow}(1){Ansi.Reset} Play MMUDREBORN");
            await _client.SendLineAsync($"{Ansi.BrightYellow}(2){Ansi.Reset} Account Settings");
            await _client.SendLineAsync($"{Ansi.BrightYellow}(3){Ansi.Reset} Logoff");
            await _client.SendLineAsync();
            await _client.SendAsync(BbsAnsi.MainMenuSelectionPrompt());

            bool autoAdvanced = false;
            var choice = await ReadMenuChoiceAsync(BbsMenuAutoAdvanceDelay, "1", viewerBbsUserName, ct);
            if (choice == null)
                return null;

            if (choice == GameTransportConstants.ReadCommandHandledSentinel)
                continue;

            if (choice == GameTransportConstants.ReadTimeoutSentinel)
            {
                autoAdvanced = true;
                choice = "1";
                await _client.SendLineAsync(choice);
            }

            switch (NormalizeMenuChoice(choice))
            {
                case "1":
                case "P":
                case "PLAY":
                    return new BbsMenuResult(autoAdvanced);

                case "2":
                case "A":
                case "ACCOUNT":
                case "SETTINGS":
                    if (!await ShowAccountSettingsMenuAsync(login.Account, viewerBbsUserName, ct))
                        return null;
                    break;

                case "3":
                case "L":
                case "LOGOFF":
                case "Q":
                case "QUIT":
                case "X":
                    await DisconnectWithGoodbyeAsync();
                    return null;

                default:
                    await _client.SendLineAsync();
                    await _client.SendLineAsync(Ansi.Error("Please choose 1, 2, or 3."));
                    await _client.SendLineAsync();
                    break;
            }
        }

        return null;
    }

    // Words a non-sysop member may not include in a display name, to prevent impersonating staff.
    private static readonly string[] ReservedDisplayNameWords = { "sysop", "admin" };

    // Returns false only when the connection dropped (so the caller ends the session); a normal
    // "return to main menu" or any validation outcome returns true.
    private async Task<bool> ShowAccountSettingsMenuAsync(BbsUserAccount account, string viewerBbsUserName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _client.Connected)
        {
            SetPresenceLocation("Account Settings");
            await _client.SendLineAsync();
            await _client.SendLineAsync($"{Ansi.BrightWhite}Account Settings{Ansi.Reset}");
            await _client.SendLineAsync();
            await _client.SendLineAsync($"{Ansi.BrightYellow}(1){Ansi.Reset} Change Display Name (current: {ResolveDisplayName(account)})");
            await _client.SendLineAsync($"{Ansi.BrightYellow}(2){Ansi.Reset} Change Password");
            await _client.SendLineAsync($"{Ansi.BrightYellow}(3){Ansi.Reset} Return to Main Menu");
            await _client.SendLineAsync();
            await _client.SendAsync(BbsAnsi.MainMenuSelectionPrompt());

            var choice = await ReadBbsCommandAwareLineAsync(echo: true, viewerBbsUserName, ct);
            if (choice == null)
                return false;
            if (choice == GameTransportConstants.ReadCommandHandledSentinel)
                continue;

            switch (NormalizeMenuChoice(choice))
            {
                case "1":
                case "D":
                case "NAME":
                    if (!await ChangeDisplayNameAsync(account, viewerBbsUserName, ct))
                        return false;
                    break;

                case "2":
                case "P":
                case "PASS":
                case "PASSWORD":
                    if (!await ChangePasswordAsync(account, ct))
                        return false;
                    break;

                case "3":
                case "0":
                case "R":
                case "RETURN":
                case "BACK":
                case "Q":
                case "X":
                    return true;

                default:
                    await _client.SendLineAsync();
                    await _client.SendLineAsync(Ansi.Error("Please choose 1, 2, or 3."));
                    await _client.SendLineAsync();
                    break;
            }
        }

        return _client.Connected;
    }

    private async Task<bool> ChangeDisplayNameAsync(BbsUserAccount account, string viewerBbsUserName, CancellationToken ct)
    {
        await _client.SendLineAsync();
        await _client.SendAsync($"{Ansi.BrightWhite}Enter new display name (blank to cancel): {Ansi.Reset}");

        var input = await ReadLoginPromptLineAsync(true, viewerBbsUserName, ct);
        if (input == null || !_client.Connected)
            return _client.Connected;
        if (input == GameTransportConstants.ReadCommandHandledSentinel)
            return true;

        string candidate = BbsText.NormalizeNamePart(input);
        if (candidate.Length == 0)
        {
            await _client.SendLineAsync(Ansi.BrightYellow + "Display name unchanged." + Ansi.Reset);
            return true;
        }

        if (candidate.Length < 3 || candidate.Length > 12)
        {
            await _client.SendLineAsync(Ansi.Error("Display name must be 3-12 characters."));
            return true;
        }

        foreach (char c in candidate)
        {
            if (!char.IsLetterOrDigit(c))
            {
                await _client.SendLineAsync(Ansi.Error("Display name may contain only letters and numbers."));
                return true;
            }
        }

        if (!account.IsSysop && ContainsReservedWord(candidate))
        {
            await _client.SendLineAsync(Ansi.Error("That display name is reserved and cannot be used."));
            return true;
        }

        if (IsDisplayNameTaken(candidate, account.UserName))
        {
            await _client.SendLineAsync(Ansi.Error("That display name is already in use by another member."));
            return true;
        }

        account.DisplayName = candidate;
        _bbsUserRepository.SetDisplayName(account.UserName, candidate);
        SetPresenceAccount(account);
        await _client.SendLineAsync(Ansi.BrightGreen + $"Display name changed to {candidate}." + Ansi.Reset);
        return true;
    }

    private async Task<bool> ChangePasswordAsync(BbsUserAccount account, CancellationToken ct)
    {
        await _client.SendLineAsync();
        await _client.SendAsync($"{Ansi.BrightWhite}Enter current password (blank to cancel): {Ansi.Reset}");
        var current = await ReadLoginPasswordLineAsync(ct);
        await _client.SendLineAsync();
        if (current == null || !_client.Connected)
            return _client.Connected;
        if (string.IsNullOrEmpty(current.Trim()))
        {
            await _client.SendLineAsync(Ansi.BrightYellow + "Password change cancelled." + Ansi.Reset);
            return true;
        }
        if (!BbsSecurity.VerifyPassword(current.Trim(), account.PasswordHash))
        {
            await _client.SendLineAsync(Ansi.Error("Current password is incorrect."));
            return true;
        }

        await _client.SendAsync($"{Ansi.BrightWhite}Enter new password: {Ansi.Reset}");
        var newPass = await ReadLoginPasswordLineAsync(ct);
        await _client.SendLineAsync();
        if (newPass == null || !_client.Connected)
            return _client.Connected;
        if (string.IsNullOrWhiteSpace(newPass) || newPass.Trim().Length < 3)
        {
            await _client.SendLineAsync(Ansi.Error("Password must be at least 3 characters."));
            return true;
        }

        await _client.SendAsync($"{Ansi.BrightWhite}Confirm new password: {Ansi.Reset}");
        var confirm = await ReadLoginPasswordLineAsync(ct);
        await _client.SendLineAsync();
        if (confirm == null || !_client.Connected)
            return _client.Connected;
        if (!string.Equals(newPass.Trim(), confirm.Trim(), StringComparison.Ordinal))
        {
            await _client.SendLineAsync(Ansi.Error("Passwords do not match."));
            return true;
        }

        string newHash = BbsSecurity.HashPassword(newPass.Trim());
        account.PasswordHash = newHash;
        _bbsUserRepository.SetPassword(account.UserName, newHash);
        await _client.SendLineAsync(Ansi.BrightGreen + "Password changed successfully." + Ansi.Reset);
        return true;
    }

    private static bool ContainsReservedWord(string candidate)
    {
        foreach (var word in ReservedDisplayNameWords)
        {
            if (candidate.Contains(word, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // A candidate handle is taken if any OTHER account already uses it as either its login name or its
    // display name (case-insensitive). A member may reuse their own login name as their display name.
    private bool IsDisplayNameTaken(string candidate, string ownUserName)
    {
        foreach (var user in _bbsUserRepository.GetUsers())
        {
            if (string.Equals(user.UserName, ownUserName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(user.UserName, candidate, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ResolveDisplayName(user), candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string ResolveDisplayName(BbsUserAccount account) =>
        string.IsNullOrWhiteSpace(account.DisplayName) ? account.UserName : account.DisplayName;

    private async Task<string?> ReadMenuChoiceAsync(TimeSpan? timeout, string? autoChoice, string? viewerBbsUserName, CancellationToken ct)
    {
        if (timeout.HasValue && autoChoice != null && IsPreLoginMenuAutoAdvanceEnabled())
        {
            var choice = await ReadBbsCommandAwareLineAsync((int)timeout.Value.TotalMilliseconds, echo: true, viewerBbsUserName, ct);
            if (choice == null)
                return null;

            return choice == GameTransportConstants.ReadTimeoutSentinel
                ? GameTransportConstants.ReadTimeoutSentinel
                : choice;
        }

        return await ReadBbsCommandAwareLineAsync(echo: true, viewerBbsUserName, ct);
    }

    private async Task DisconnectWithGoodbyeAsync()
    {
        await _client.SendLineAsync();
        await _client.SendLineAsync($"{Ansi.BrightWhite}Goodbye.{Ansi.Reset}");
        _client.Disconnect();
    }

    private static string NormalizeMenuChoice(string? choice)
    {
        return (choice ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static bool IsPreLoginMenuEnabled()
    {
        return ReadEnvironmentToggle(PreLoginMenuEnabledEnvironmentVariable, defaultValue: true);
    }

    private bool IsPreLoginMenuAutoAdvanceEnabled()
    {
        return BbsMenuSettings.GetEffectiveAutoAdvanceEnabled(_bbsUserRepository);
    }

    private static bool ReadEnvironmentToggle(string variableName, bool defaultValue)
    {
        var rawValue = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(rawValue))
            return defaultValue;

        return rawValue.Trim().ToUpperInvariant() switch
        {
            "1" or "TRUE" or "YES" or "ON" => true,
            "0" or "FALSE" or "NO" or "OFF" => false,
            _ => defaultValue,
        };
    }

    private async Task ShowWelcome()
    {
        if (RuntimeConfiguration.IsFastTestModeEnabled())
            return;

        var welcomeArt = LoadWelcomeArt();
        if (!string.IsNullOrWhiteSpace(welcomeArt))
        {
            // Full-screen ANSI art sent VERBATIM: bypass the 510-char per-line transmit cap, which would
            // otherwise split the widest banner line (>510 once colour codes count) and tear the art down
            // the middle. See TelnetClient.SendAnsiArtAsync.
            await _client.SendAnsiArtAsync(welcomeArt);

            if (!EndsWithLineBreak(welcomeArt))
                await _client.SendLineAsync();

            await _client.SendLineAsync();
            return;
        }

        await _client.SendLineAsync(Ansi.ClearScreen);
        await _client.SendLineAsync(Ansi.BrightCyan + "+----------------------------------------------------------------+" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|                                                                |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|  " + Ansi.BrightMagenta + "#     #                             #     # #     # ######  " + Ansi.BrightCyan + "  |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|  " + Ansi.BrightMagenta + "##   ##   ##        #  ####  #####  ##   ## #     # #     # " + Ansi.BrightCyan + "  |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|  " + Ansi.BrightMagenta + "# # # #  #  #       # #    # #    # # # # # #     # #     # " + Ansi.BrightCyan + "  |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|  " + Ansi.BrightMagenta + "#  #  # #    #      # #    # #    # #  #  # #     # #     # " + Ansi.BrightCyan + "  |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|  " + Ansi.BrightMagenta + "#     # #****#      # #    # #***#  #     # #     # #     # " + Ansi.BrightCyan + "  |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|  " + Ansi.BrightMagenta + "#     # #    # #    # #    # #   #  #     # #     # #     # " + Ansi.BrightCyan + "  |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|  " + Ansi.BrightMagenta + "#     # #    #  ####   ####  #    # #     #  #####  ######  " + Ansi.BrightCyan + "  |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|                                                                |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|          " + Ansi.BrightYellow + "######                                     " + Ansi.BrightCyan + "           |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|          " + Ansi.BrightYellow + "#     # ###### #####   ####  #####  #    # " + Ansi.BrightCyan + "           |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|          " + Ansi.BrightYellow + "#     # #      #    # #    # #    # ##   # " + Ansi.BrightCyan + "           |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|          " + Ansi.BrightYellow + "#****#  #####  #####  #    # #    # # #  # " + Ansi.BrightCyan + "           |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|          " + Ansi.BrightYellow + "#   #   #      #    # #    # #####  #  # # " + Ansi.BrightCyan + "           |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|          " + Ansi.BrightYellow + "#    #  #      #    # #    # #   #  #   ## " + Ansi.BrightCyan + "           |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|          " + Ansi.BrightYellow + "#     # ###### #####   ####  #    # #    # " + Ansi.BrightCyan + "           |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|                                                                |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|                  " + Ansi.BrightWhite + "Welcome to MMud Reborn!" + Ansi.BrightCyan + "                   |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "|                                                                |" + Ansi.Reset);
        await _client.SendLineAsync(Ansi.BrightCyan + "+----------------------------------------------------------------+" + Ansi.Reset);
        await _client.SendLineAsync();
    }

    private static bool EndsWithLineBreak(string text)
    {
        return text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal);
    }

    private static string? LoadWelcomeArt()
    {
        if (_cachedWelcomeArt != null)
            return _cachedWelcomeArt;

        var path = ResolveWelcomeArtPath(WelcomeArtFileName);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
                return null;

            _cachedWelcomeArt = Encoding.GetEncoding(437).GetString(bytes);
            return _cachedWelcomeArt;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveWelcomeArtPath(string fileName)
    {
        static string? TryResolveFromRoot(string? root, string fileName)
        {
            while (!string.IsNullOrEmpty(root))
            {
                var direct = Path.Combine(root, fileName);
                if (File.Exists(direct))
                    return direct;

                var data = Path.Combine(root, "Data", fileName);
                if (File.Exists(data))
                    return data;

                root = Directory.GetParent(root)?.FullName;
            }

            return null;
        }

        var path = TryResolveFromRoot(AppContext.BaseDirectory, fileName);
        if (!string.IsNullOrEmpty(path))
            return path;

        path = TryResolveFromRoot(Directory.GetCurrentDirectory(), fileName);
        if (!string.IsNullOrEmpty(path))
            return path;

        return null;
    }

    private async Task<LoginSession?> LoginLoop(CancellationToken ct)
    {
        // Set when the prior iteration's read returned empty. Keeps the existing prompt on screen
        // for the user's next keystroke instead of stacking a second prompt — guards against any
        // stray CR/LF that the connect-time drain in TelnetClient might have missed (e.g. a byte
        // that arrives between the drain and the first ReadLine).
        bool suppressNextNamePromptRedisplay = false;

        while (!ct.IsCancellationRequested)
        {
            SetPresenceLocation("Logging In");
            if (!suppressNextNamePromptRedisplay)
            {
                if (!await DelayDuringLoginAsync(LoginPromptDelay, ct)) return null;
                await _client.SendAsync(BbsAnsi.LoginNamePrompt());
            }
            suppressNextNamePromptRedisplay = false;

            var name = await ReadLoginPromptLineAsync(true, null, ct);
            if (name == null) return null;
            if (name == GameTransportConstants.ReadCommandHandledSentinel) continue;
            name = name.Trim();

            if (string.IsNullOrEmpty(name))
            {
                suppressNextNamePromptRedisplay = true;
                continue;
            }

            if (name.Equals("new", StringComparison.OrdinalIgnoreCase))
            {
                var created = await CreateBbsAccountAsync(ct);
                if (created != null)
                    return new LoginSession(created);

                continue;
            }

            var account = _bbsUserRepository.LoadUser(name);
            if (account == null)
            {
                ClearPresenceAccount();
                await _client.SendLineAsync(Ansi.Error("BBS account not found. Type 'new' to create one."));
                if (!await DelayDuringLoginAsync(LoginRetryDelay, ct)) return null;
                continue;
            }

            SetPresenceAccount(account);

            if (!await DelayDuringLoginAsync(LoginPromptDelay, ct)) return null;
            await _client.SendAsync(BbsAnsi.PasswordPrompt());
            var pass = await ReadLoginPasswordLineAsync(ct);
            await _client.SendLineAsync();

            if (string.IsNullOrEmpty(pass?.Trim()))
            {
                await _client.SendLineAsync(Ansi.Error("Invalid password."));
                if (!await DelayDuringLoginAsync(LoginRetryDelay, ct)) return null;
                continue;
            }

            account = _bbsUserRepository.LoadUser(name, pass.Trim());
            if (account == null)
            {
                await _client.SendLineAsync(Ansi.Error("Invalid password."));
                if (!await DelayDuringLoginAsync(LoginRetryDelay, ct)) return null;
                continue;
            }

            SetPresenceAccount(account);
            UpdateLastKnownIpAddress(account);
            SetPresenceLocation("Main Menu");

            return new LoginSession(account);
        }

        return null;
    }

    private async Task<BbsUserAccount?> CreateBbsAccountAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SetPresenceLocation("Logging In");
            if (!await DelayDuringLoginAsync(LoginPromptDelay, ct)) return null;
            await _client.SendAsync(BbsAnsi.NewAccountNamePrompt());
            var newName = await ReadLoginPromptLineAsync(true, null, ct);
            if (string.IsNullOrWhiteSpace(newName))
                continue;
            if (newName == GameTransportConstants.ReadCommandHandledSentinel)
                continue;

            newName = BbsText.NormalizeNamePart(newName);
            if (newName.Length < 3 || newName.Length > 9)
            {
                await _client.SendLineAsync(Ansi.Error("Name must be 3-9 characters."));
                if (!await DelayDuringLoginAsync(LoginRetryDelay, ct)) return null;
                continue;
            }

            if (_bbsUserRepository.UserExists(newName))
            {
                await _client.SendLineAsync(Ansi.Error("That BBS account name is already taken."));
                if (!await DelayDuringLoginAsync(LoginRetryDelay, ct)) return null;
                continue;
            }

            if (!await DelayDuringLoginAsync(LoginPromptDelay, ct)) return null;
            await _client.SendAsync(BbsAnsi.NewPasswordPrompt());
            var newPass = await ReadLoginPasswordLineAsync(ct);
            await _client.SendLineAsync();
            if (string.IsNullOrWhiteSpace(newPass) || newPass.Trim().Length < 3)
            {
                await _client.SendLineAsync(Ansi.Error("Password must be at least 3 characters."));
                if (!await DelayDuringLoginAsync(LoginRetryDelay, ct)) return null;
                continue;
            }

            if (!await DelayDuringLoginAsync(LoginPromptDelay, ct)) return null;
            await _client.SendAsync(BbsAnsi.ConfirmPasswordPrompt());
            var confirmPass = await ReadLoginPasswordLineAsync(ct);
            await _client.SendLineAsync();

            if (!string.Equals(newPass.Trim(), confirmPass?.Trim(), StringComparison.Ordinal))
            {
                await _client.SendLineAsync(Ansi.Error("Passwords do not match."));
                if (!await DelayDuringLoginAsync(LoginRetryDelay, ct)) return null;
                continue;
            }

            var account = new BbsUserAccount
            {
                UserName = newName,
                // Seed the public handle to the login name so it's never blank; the member can change it
                // later in Account Settings.
                DisplayName = newName,
                PasswordHash = BbsSecurity.HashPassword(newPass.Trim()),
                IsTestAccount = _markNewAccountsAsTestAccounts,
                LastIpAddress = ResolveRemoteAddress(),
            };

            _bbsUserRepository.SaveUser(account);
            SetPresenceAccount(account);
            await _client.SendLineAsync();
            await _client.SendLineAsync(Ansi.BrightGreen + "BBS account created successfully!" + Ansi.Reset);
            await _client.SendLineAsync();
            return account;
        }

        return null;
    }

    private static async Task<bool> DelayDuringLoginAsync(TimeSpan delay, CancellationToken ct)
    {
        if (RuntimeConfiguration.IsFastTestModeEnabled())
            return !ct.IsCancellationRequested;

        try
        {
            await Task.Delay(delay, ct);
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<string?> ReadLoginPromptLineAsync(bool echo, string? viewerBbsUserName, CancellationToken ct)
    {
        string? input = await ReadBbsCommandAwareLineAsync(echo, viewerBbsUserName, ct);
        if (input == null || input == GameTransportConstants.ReadCommandHandledSentinel || !_client.Connected)
            return input;

        await _client.DiscardBufferedLineEndingsAsync(ct);
        return input;
    }

    private async Task<string?> ReadLoginPasswordLineAsync(CancellationToken ct)
    {
        string? input = await _client.ReadLineMaskedAsync('*', ct);
        if (input == null || !_client.Connected)
            return input;

        await _client.DiscardBufferedLineEndingsAsync(ct);
        return input;
    }

    private async Task<string?> ReadBbsCommandAwareLineAsync(bool echo, string? viewerBbsUserName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var input = await _client.ReadLineEchoAsync(echo, ct);
            if (input == null)
                return null;

            if (await _commandDispatcher.TryDispatchAsync(input.Trim(), _client, _door, viewerBbsUserName))
            {
                if (_client.Connected)
                    await _client.SendLineAsync();
                return GameTransportConstants.ReadCommandHandledSentinel;
            }

            return input;
        }

        return null;
    }

    private async Task<string?> ReadBbsCommandAwareLineAsync(int timeoutMs, bool echo, string? viewerBbsUserName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var input = await _client.ReadLineEchoAsync(timeoutMs, echo, ct);
            if (input == null || input.Equals(GameTransportConstants.ReadTimeoutSentinel, StringComparison.Ordinal))
                return input;

            if (await _commandDispatcher.TryDispatchAsync(input.Trim(), _client, _door, viewerBbsUserName))
            {
                if (_client.Connected)
                    await _client.SendLineAsync();
                return GameTransportConstants.ReadCommandHandledSentinel;
            }

            return input;
        }

        return null;
    }

    private void SetPresenceLocation(string location)
    {
        _client.CurrentHostedLocation = location;
    }

    private void SetHostedAppContext(string appId, string worldId)
    {
        _client.CurrentHostedAppId = appId;
        _client.CurrentHostedWorldId = worldId;
    }

    private void SetPresenceAccount(BbsUserAccount account)
    {
        if (_client is not TelnetClient telnetClient)
            return;

        // Identity (CurrentBbsUserName) stays the login name; the WHO-visible presence name is the public
        // display handle so other users never see a member's sign-in name.
        _client.CurrentBbsUserName = account.UserName;
        telnetClient.BbsPresenceUserName = ResolveDisplayName(account);
        telnetClient.BbsPresenceIsSysop = account.IsSysop;
        telnetClient.BbsPresenceIpAddress = account.LastIpAddress.Trim();
    }

    private void ClearPresenceAccount()
    {
        _client.CurrentBbsUserName = string.Empty;
        SetHostedAppContext(HostedAppIds.Bbs, string.Empty);

        if (_client is not TelnetClient telnetClient)
            return;

        telnetClient.BbsPresenceUserName = string.Empty;
        telnetClient.BbsPresenceIsSysop = false;
        telnetClient.BbsPresenceIpAddress = string.Empty;
    }

    private void UpdateLastKnownIpAddress(BbsUserAccount account)
    {
        string resolvedAddress = ResolvePresenceIpAddress(account);
        if (string.IsNullOrWhiteSpace(resolvedAddress) || string.Equals(account.LastIpAddress, resolvedAddress, StringComparison.Ordinal))
        {
            SetPresenceAccount(account);
            return;
        }

        account.LastIpAddress = resolvedAddress;
        _bbsUserRepository.SaveUser(account);
        SetPresenceAccount(account);
    }

    private string ResolveRemoteAddress()
    {
        return _client is TelnetClient telnetClient ? telnetClient.RemoteAddress : string.Empty;
    }

    private string ResolvePresenceIpAddress(BbsUserAccount account)
    {
        string remoteAddress = ResolveRemoteAddress().Trim();
        string storedAddress = account.LastIpAddress.Trim();

        if (ShouldPreferStoredIpAddress(storedAddress, remoteAddress))
            return storedAddress;

        return string.IsNullOrWhiteSpace(remoteAddress) ? storedAddress : remoteAddress;
    }

    private static bool ShouldPreferStoredIpAddress(string storedAddress, string remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(storedAddress) || string.IsNullOrWhiteSpace(remoteAddress))
            return false;

        if (string.Equals(storedAddress, remoteAddress, StringComparison.Ordinal))
            return false;

        return IsPrivateOrLoopbackAddress(remoteAddress) && !IsPrivateOrLoopbackAddress(storedAddress);
    }

    private static bool IsPrivateOrLoopbackAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var parsed))
            return false;

        if (IPAddress.IsLoopback(parsed))
            return true;

        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = parsed.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        if (parsed.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = parsed.GetAddressBytes();
            return parsed.IsIPv6LinkLocal
                || parsed.IsIPv6SiteLocal
                || (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }
}
