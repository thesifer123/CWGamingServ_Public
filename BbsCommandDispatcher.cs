using System.Globalization;
using System.Runtime.CompilerServices;
using CWGaming.Shared;
using Ansi = CWGamingServ.BbsAnsi;

namespace CWGamingServ;

public sealed class BbsCommandDispatcher : IBbsCommandDispatcher
{
    private const int MaxTicketTitleLength = 80;
    private const int MaxTicketBriefLength = 120;
    private const int MaxTicketLocationLength = 120;
    private const int MaxTicketSubjectLength = 80;
    private const string MenuAutoAdvanceSyntax = "Syntax: ;menuauto [on|off]";
    private static readonly ConditionalWeakTable<IBbsConnection, TicketDraft> PendingTicketDrafts = [];

    private readonly IBbsUserRepository _bbsUserRepository;
    private readonly IBbsTicketRepository _ticketRepository;
    private readonly BbsIpAccessControl _ipAccessControl;

    /// <summary>
    /// Set by the FRONT host so <c>;restart</c> can announce board-wide and relaunch the whole topology.
    /// Null in the monolithic/backend host (there is no board to control from here), where <c>;restart</c>
    /// reports that in-realm SYSOP RESTART is the mechanism instead.
    /// </summary>
    public IBoardController? BoardController { get; set; }

    public BbsCommandDispatcher(IBbsUserRepository bbsUserRepository, IBbsTicketRepository ticketRepository, BbsIpAccessControl ipAccessControl)
    {
        _bbsUserRepository = bbsUserRepository;
        _ticketRepository = ticketRepository;
        _ipAccessControl = ipAccessControl;
    }

    public async Task<bool> TryDispatchAsync(string input, IBbsConnection client, IBbsDoorContext door, string? viewerBbsUserName = null)
    {
        string trimmedInput = (input ?? string.Empty).Trim();
        SplitCommand(trimmedInput, out string command, out string args);

        if (IsEmergencyLogoffCommand(trimmedInput))
        {
            ClearPendingTicket(client);
            HandleEmergencyLogoff(client, door);
            return true;
        }

        if (TryGetPendingTicket(client, out var draft))
        {
            await HandlePendingTicketAsync(trimmedInput, client, draft, viewerBbsUserName);
            return true;
        }

        if (IsHelpCommand(trimmedInput))
        {
            await RenderHelpAsync(client, IsBbsSysop(viewerBbsUserName), BoardController != null);
            return true;
        }

        if (command.Equals(";menuauto", StringComparison.OrdinalIgnoreCase) ||
            command.Equals(";autoadvance", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveViewerName(viewerBbsUserName, client, out _))
            {
                await client.SendLineAsync("You must be logged in to use BBS menu commands.");
                return true;
            }

            if (string.IsNullOrWhiteSpace(args) || args.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                bool autoAdvanceEnabled = BbsMenuSettings.GetEffectiveAutoAdvanceEnabled(_bbsUserRepository);
                await client.SendLineAsync($"BBS menu auto-advance is currently {(autoAdvanceEnabled ? "ON" : "OFF")}.");
                return true;
            }

            if (!TryResolveBbsSysopViewerName(viewerBbsUserName, client, out _))
            {
                await client.SendLineAsync("Only BBS sysops can change BBS menu settings.");
                return true;
            }

            if (!BbsMenuSettings.TryParseToggle(args, out bool enabled))
            {
                await client.SendLineAsync(MenuAutoAdvanceSyntax);
                return true;
            }

            BbsMenuSettings.SetAutoAdvanceEnabled(_bbsUserRepository, enabled);
            await client.SendLineAsync($"BBS menu auto-advance set to {(enabled ? "ON" : "OFF")}.");
            return true;
        }

        if (command.Equals(";password", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveBbsSysopViewerName(viewerBbsUserName, client, out _))
            {
                await client.SendLineAsync("Only BBS sysops can reset BBS passwords.");
                return true;
            }

            await HandlePasswordAsync(client, args);
            return true;
        }

        if (command.Equals(";access", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveBbsSysopViewerName(viewerBbsUserName, client, out _))
            {
                await client.SendLineAsync("Only BBS sysops can change BBS sysop access.");
                return true;
            }

            await HandleAccessAsync(client, args);
            return true;
        }

        if (command.Equals(";ban", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveBbsSysopViewerName(viewerBbsUserName, client, out _))
            {
                await client.SendLineAsync("Only BBS sysops can ban IP addresses.");
                return true;
            }

            await HandleBanAsync(client, args);
            return true;
        }

        if (command.Equals(";unban", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveBbsSysopViewerName(viewerBbsUserName, client, out _))
            {
                await client.SendLineAsync("Only BBS sysops can unban IP addresses.");
                return true;
            }

            await HandleUnbanAsync(client, args);
            return true;
        }

        if (command.Equals(";bans", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveBbsSysopViewerName(viewerBbsUserName, client, out _))
            {
                await client.SendLineAsync("Only BBS sysops can view the IP banlist.");
                return true;
            }

            await HandleBansAsync(client);
            return true;
        }

        if (command.Equals(";maxconn", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveBbsSysopViewerName(viewerBbsUserName, client, out _))
            {
                await client.SendLineAsync("Only BBS sysops can change the per-IP connection cap.");
                return true;
            }

            await HandleMaxConnAsync(client, args);
            return true;
        }

        if (command.Equals(";restart", StringComparison.OrdinalIgnoreCase) ||
            command.Equals(";shutdown", StringComparison.OrdinalIgnoreCase))
        {
            bool restart = command.Equals(";restart", StringComparison.OrdinalIgnoreCase);
            if (!TryResolveBbsSysopViewerName(viewerBbsUserName, client, out string restarter))
            {
                await client.SendLineAsync($"Only BBS sysops can {(restart ? "restart" : "shut down")} the board.");
                return true;
            }

            await HandleBoardStopAsync(client, args, restarter, restart);
            return true;
        }

        if (command.Equals(";account", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveBbsSysopViewerName(viewerBbsUserName, client, out _))
            {
                await client.SendLineAsync("Only BBS sysops can manage BBS accounts.");
                return true;
            }

            await HandleAccountAsync(client, door, args);
            return true;
        }

        if (command.Equals(";users", StringComparison.OrdinalIgnoreCase) ||
            command.Equals(";listusers", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveBbsSysopViewerName(viewerBbsUserName, client, out _))
            {
                await client.SendLineAsync("Only BBS sysops can list BBS users.");
                return true;
            }

            await HandleUsersAsync(client, door, args);
            return true;
        }

        if (IsLegacyBugAliasCommand(command))
        {
            await SendLegacyBugAliasRetiredAsync(client);
            return true;
        }

        if (command.Equals(";tickets", StringComparison.OrdinalIgnoreCase) ||
            command.Equals(";listtickets", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveViewerName(viewerBbsUserName, client, out _))
            {
                await client.SendLineAsync("You must be logged in to use the BBS ticket system.");
                return true;
            }

            await RenderTicketListAsync(client, _ticketRepository.GetTickets());
            return true;
        }

        if (command.Equals(";showticket", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveViewerName(viewerBbsUserName, client, out _))
            {
                await client.SendLineAsync("You must be logged in to use the BBS ticket system.");
                return true;
            }

            if (!TryParseTicketId(args, out int ticketId))
            {
                await client.SendLineAsync($"Syntax: {command} <number>");
                return true;
            }

            await RenderTicketDetailAsync(client, _ticketRepository.LoadTicket(ticketId));
            return true;
        }

        if (command.Equals(";resolveticket", StringComparison.OrdinalIgnoreCase) ||
            command.Equals(";completeticket", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveBbsSysopViewerName(viewerBbsUserName, client, out string moderatorName))
            {
                await client.SendLineAsync("Only BBS sysops can moderate tickets.");
                return true;
            }

            if (!TryParseTicketId(args, out int ticketId))
            {
                await client.SendLineAsync($"Syntax: {command} <number>");
                return true;
            }

            var ticket = _ticketRepository.LoadTicket(ticketId);
            if (ticket == null)
            {
                await client.SendLineAsync("That ticket was not found.");
                return true;
            }

            if (ticket.IsResolved)
            {
                await client.SendLineAsync($"Ticket #{ticketId} is already resolved.");
                return true;
            }

            _ticketRepository.ResolveTicket(ticketId, moderatorName);
            await client.SendLineAsync($"Ticket #{ticketId} marked resolved.");
            return true;
        }

        if (command.Equals(";deleteticket", StringComparison.OrdinalIgnoreCase) ||
            command.Equals(";removeticket", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveBbsSysopViewerName(viewerBbsUserName, client, out _))
            {
                await client.SendLineAsync("Only BBS sysops can moderate tickets.");
                return true;
            }

            if (!TryParseTicketId(args, out int ticketId))
            {
                await client.SendLineAsync($"Syntax: {command} <number>");
                return true;
            }

            if (!_ticketRepository.DeleteTicket(ticketId))
            {
                await client.SendLineAsync("That ticket was not found.");
                return true;
            }

            await client.SendLineAsync($"Ticket #{ticketId} removed.");
            return true;
        }

        if (command.Equals(";ticket", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                await RenderTicketHelpAsync(client, IsBbsSysop(viewerBbsUserName));
                return true;
            }

            if (!TryResolveViewerName(viewerBbsUserName, client, out string viewerName))
            {
                await client.SendLineAsync("You must be logged in to use the BBS ticket system.");
                return true;
            }

            await StartTicketAsync(client, viewerName, args);
            return true;
        }

        if (!IsWhoCommand(trimmedInput))
            return false;

        bool showIpAddress = IsBbsSysop(viewerBbsUserName);
        var testAccountNames = _bbsUserRepository.GetUsers()
            .Where(account => account.IsTestAccount)
            .Select(account => account.UserName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var visibleEntries = door.GetBbsPresenceSnapshots()
            .Where(entry => !string.IsNullOrWhiteSpace(entry.UserName) && !testAccountNames.Contains(entry.UserName))
            .ToList();

        await RenderWhoAsync(client, visibleEntries, showIpAddress);
        return true;
    }

    private static bool IsEmergencyLogoffCommand(string input)
    {
        string normalized = (input ?? string.Empty).Trim();
        return normalized.Equals(";o", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("=x", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWhoCommand(string input)
    {
        string normalized = (input ?? string.Empty).Trim();
        return normalized.Equals(";w", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(";who", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHelpCommand(string input)
    {
        string normalized = (input ?? string.Empty).Trim();
        return normalized.Equals(";", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(";?", StringComparison.OrdinalIgnoreCase);
    }

    private async Task StartTicketAsync(IBbsConnection client, string viewerName, string initialTitle)
    {
        var identity = client.AppData as IBbsIdentity;
        var draft = new TicketDraft
        {
            AppId = ResolveAppId(client),
            WorldId = ResolveWorldId(client),
            ReporterBbsUserName = viewerName,
            ReporterPlayerName = identity?.CharacterName ?? string.Empty,
            DefaultLocationText = ResolveCurrentLocationText(client),
            Step = TicketDraftStep.AwaitingTitle,
        };

        if (!string.IsNullOrWhiteSpace(initialTitle))
        {
            if (!TryValidateFieldLength(initialTitle, MaxTicketTitleLength, out string titleError))
            {
                await client.SendLineAsync(titleError);
                return;
            }

            draft.Title = initialTitle.Trim();
            draft.Step = TicketDraftStep.AwaitingBriefDescription;
        }

        SetPendingTicket(client, draft);

        await client.SendLineAsync();
        await client.SendLineAsync("BBS ticket started. Type 'cancel' at any prompt to abort.");

        if (draft.Step == TicketDraftStep.AwaitingBriefDescription)
        {
            await client.SendLineAsync($"Title: {draft.Title}");
            await PromptForBriefAsync(client);
            return;
        }

        await PromptForTitleAsync(client);
    }

    private async Task HandlePendingTicketAsync(string input, IBbsConnection client, TicketDraft draft, string? viewerBbsUserName)
    {
        string normalized = input.Trim();

        SplitCommand(normalized, out string pendingCommand, out _);

        if (IsLegacyBugAliasCommand(pendingCommand))
        {
            await client.SendLineAsync("Legacy ;bug ticket aliases are retired. Use ;ticket and ;ticket cancel.");
            return;
        }

        if (normalized.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(";cancelticket", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(";ticket cancel", StringComparison.OrdinalIgnoreCase))
        {
            ClearPendingTicket(client);
            await client.SendLineAsync("BBS ticket canceled.");
            return;
        }

        switch (draft.Step)
        {
            case TicketDraftStep.AwaitingTitle:
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    await client.SendLineAsync("A short title is required.");
                    await PromptForTitleAsync(client);
                    return;
                }

                if (!TryValidateFieldLength(normalized, MaxTicketTitleLength, out string titleError))
                {
                    await client.SendLineAsync(titleError);
                    await PromptForTitleAsync(client);
                    return;
                }

                draft.Title = normalized;
                draft.Step = TicketDraftStep.AwaitingBriefDescription;
                await PromptForBriefAsync(client);
                return;

            case TicketDraftStep.AwaitingBriefDescription:
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    await client.SendLineAsync("A one-line summary is required.");
                    await PromptForBriefAsync(client);
                    return;
                }

                if (!TryValidateFieldLength(normalized, MaxTicketBriefLength, out string briefError))
                {
                    await client.SendLineAsync(briefError);
                    await PromptForBriefAsync(client);
                    return;
                }

                draft.BriefDescription = normalized;
                draft.Step = TicketDraftStep.AwaitingLocation;
                await PromptForLocationAsync(client, draft);
                return;

            case TicketDraftStep.AwaitingLocation:
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    draft.LocationText = draft.DefaultLocationText;
                }
                else if (normalized.Equals("-", StringComparison.Ordinal))
                {
                    draft.LocationText = string.Empty;
                }
                else
                {
                    if (!TryValidateFieldLength(normalized, MaxTicketLocationLength, out string locationError))
                    {
                        await client.SendLineAsync(locationError);
                        await PromptForLocationAsync(client, draft);
                        return;
                    }

                    draft.LocationText = normalized;
                }

                draft.Step = TicketDraftStep.AwaitingSubject;
                await PromptForSubjectAsync(client);
                return;

            case TicketDraftStep.AwaitingSubject:
                if (string.IsNullOrWhiteSpace(normalized) || normalized.Equals("-", StringComparison.Ordinal))
                {
                    draft.SubjectText = string.Empty;
                }
                else
                {
                    if (!TryValidateFieldLength(normalized, MaxTicketSubjectLength, out string subjectError))
                    {
                        await client.SendLineAsync(subjectError);
                        await PromptForSubjectAsync(client);
                        return;
                    }

                    draft.SubjectText = normalized;
                }

                draft.Step = TicketDraftStep.AwaitingDescription;
                await PromptForDescriptionAsync(client);
                return;

            case TicketDraftStep.AwaitingDescription:
                if (normalized.Equals(".", StringComparison.Ordinal))
                {
                    if (draft.DescriptionLines.Count == 0)
                    {
                        await client.SendLineAsync("Enter at least one line of detail before finishing with '.'.");
                        return;
                    }

                    var ticket = new BbsTicketRecord
                    {
                        AppId = draft.AppId,
                        WorldId = draft.WorldId,
                        ReporterBbsUserName = draft.ReporterBbsUserName,
                        ReporterPlayerName = draft.ReporterPlayerName,
                        Title = draft.Title,
                        BriefDescription = draft.BriefDescription,
                        Description = string.Join(Environment.NewLine, draft.DescriptionLines),
                        LocationText = draft.LocationText,
                        SubjectText = draft.SubjectText,
                    };

                    int ticketId = _ticketRepository.CreateTicket(ticket);
                    ClearPendingTicket(client);

                    await client.SendLineAsync($"Ticket #{ticketId} submitted. Use ;showticket {ticketId} to review it.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(normalized))
                {
                    await client.SendLineAsync("Enter a description line, or type '.' to submit.");
                    return;
                }

                draft.DescriptionLines.Add(normalized);
                await client.SendLineAsync("Added. Continue typing details, or '.' to submit.");
                return;
        }
    }

    private static void HandleEmergencyLogoff(IBbsConnection client, IBbsDoorContext door)
    {
        _ = SendEmergencyLogoffFarewellAsync(client);

        // If a realm character is attached, let the door tear it down (it runs the "just disconnected"
        // announcement and clears the connection); otherwise drop the BBS-level connection directly.
        var identity = client.AppData as IBbsIdentity;
        if (identity is { HasActiveCharacter: true })
            door.EmergencyDisconnectOnlineCharacter(identity.CharacterName);
        else
            client.Disconnect();
    }

    private static async Task SendEmergencyLogoffFarewellAsync(IBbsConnection client)
    {
        await client.SendLineAsync();
        await client.SendLineAsync($"{Ansi.BrightCyan}Thanks for joining us today!{Ansi.Reset}");
        await client.SendLineAsync();
        await client.SendLineAsync();
        await client.SendLineAsync();
    }

    private static async Task RenderHelpAsync(IBbsConnection client, bool showSysopCommands, bool boardRestartAvailable)
    {
        await client.SendLineAsync();
        await client.SendLineAsync("BBS Commands");
        await client.SendLineAsync("============");
        await client.SendLineAsync(";w / ;who             Show who is on the board");
        await client.SendLineAsync(";ticket [title]       Start a BBS ticket wizard");
        await client.SendLineAsync(";tickets              List submitted BBS tickets");
        await client.SendLineAsync(";showticket #         Show the full BBS ticket");
        if (showSysopCommands)
        {
            await client.SendLineAsync(";password <u> <pw>    Reset a BBS account password (BBS sysops)");
            await client.SendLineAsync(";access <u> <on|off> Grant/revoke BBS sysop access (BBS sysops)");
            await client.SendLineAsync(";account show <u>    Show BBS account details (BBS sysops)");
            await client.SendLineAsync(";account test <u>    Toggle a BBS test flag (BBS sysops)");
            await client.SendLineAsync(";users [all|test|player] List BBS users and links (BBS sysops)");
            await client.SendLineAsync(";menuauto [on|off]    Show/set BBS menu auto-advance (BBS sysops)");
            await client.SendLineAsync(";ban <ip-or-cidr>     Block an IP / range from connecting (BBS sysops)");
            await client.SendLineAsync(";unban <ip-or-cidr>   Remove an IP / range from the banlist (BBS sysops)");
            await client.SendLineAsync(";bans                 Show the IP banlist + connection cap (BBS sysops)");
            await client.SendLineAsync(";maxconn <n>          Set max connections per IP, 0=unlimited (BBS sysops)");
            await client.SendLineAsync(";resolveticket #      Mark a BBS ticket resolved (BBS sysops)");
            await client.SendLineAsync(";deleteticket #       Remove a BBS ticket (BBS sysops)");
            if (boardRestartAvailable)
            {
                await client.SendLineAsync(";restart [seconds]    Warn ALL realms + menus, then restart the board (BBS sysops)");
                await client.SendLineAsync(";shutdown [seconds]   Warn ALL realms + menus, then stop the board (BBS sysops)");
            }
        }
        await client.SendLineAsync(";o / =x               Emergency logoff from the realm");
        await client.SendLineAsync("; or ;?               Show this command list");
        await client.SendLineAsync();
        await client.SendLineAsync("Use BUG inside MMUDREBORN for game bug reports.");
        await client.SendLineAsync("During ;ticket entry, type 'cancel' to abort or '.' on a line by itself to finish the description.");
    }

    private static async Task RenderTicketHelpAsync(IBbsConnection client, bool showSysopCommands)
    {
        await client.SendLineAsync();
        await client.SendLineAsync("BBS Ticket Help");
        await client.SendLineAsync("===============");
        await client.SendLineAsync(";ticket              Start the guided BBS ticket wizard");
        await client.SendLineAsync(";ticket <title>      Start the wizard with a title already filled in");
        await client.SendLineAsync(";tickets             List recent BBS tickets as '# [APP] Title - Brief'");
        await client.SendLineAsync(";showticket #        Show the full stored BBS ticket");
        if (showSysopCommands)
        {
            await client.SendLineAsync(";resolveticket #     Mark a BBS ticket resolved (BBS sysops)");
            await client.SendLineAsync(";deleteticket #      Remove a BBS ticket (BBS sysops, alias: ;removeticket #)");
        }
        await client.SendLineAsync("Type 'cancel' during the wizard to abort.");
    }

    private static Task SendLegacyBugAliasRetiredAsync(IBbsConnection client)
    {
        return Task.WhenAll(
            client.SendLineAsync("Legacy ;bug ticket aliases are retired."),
            client.SendLineAsync("Use ;ticket, ;tickets, ;showticket, ;resolveticket, or ;deleteticket instead."));
    }

    private async Task HandlePasswordAsync(IBbsConnection client, string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await client.SendLineAsync("Syntax: ;password <user> <new-password>");
            return;
        }

        string targetName = BbsText.NormalizeNamePart(parts[0]);
        string newPassword = parts[1].Trim();
        if (newPassword.Length < 3)
        {
            await client.SendLineAsync("Password must be at least 3 characters.");
            return;
        }

        string newHash = BbsSecurity.HashPassword(newPassword);
        if (!_bbsUserRepository.SetPassword(targetName, newHash))
        {
            await client.SendLineAsync($"Cannot find BBS user {targetName}");
            return;
        }

        await client.SendLineAsync($"Password reset for BBS user {targetName}.");
    }

    private async Task HandleAccessAsync(IBbsConnection client, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            await client.SendLineAsync("Syntax: ;access <user> <ON|OFF>");
            return;
        }

        string targetName = BbsText.NormalizeNamePart(parts[0]);
        if (!TryParseToggleState(parts[1], out bool enabled))
        {
            await client.SendLineAsync("Syntax: ;access <user> <ON|OFF>");
            return;
        }

        if (!_bbsUserRepository.UserExists(targetName))
        {
            await client.SendLineAsync($"Cannot find BBS user {targetName}");
            return;
        }

        if (!_bbsUserRepository.SetSysopStatus(targetName, enabled))
        {
            await client.SendLineAsync($"Cannot find BBS user {targetName}");
            return;
        }

        await client.SendLineAsync($"{(enabled ? "Granted" : "Revoked")} BBS sysop access for {targetName}.");
    }

    private async Task HandleAccountAsync(IBbsConnection client, IBbsDoorContext door, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            await SendAccountHelpAsync(client);
            return;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "help":
            case "?":
                await SendAccountHelpAsync(client);
                return;

            case "show":
                if (parts.Length != 2)
                {
                    await SendAccountHelpAsync(client);
                    return;
                }

                await HandleAccountShowAsync(client, door, BbsText.NormalizeNamePart(parts[1]));
                return;

            case "test":
                if (parts.Length != 3)
                {
                    await SendAccountHelpAsync(client);
                    return;
                }

                await HandleAccountTestAsync(client, BbsText.NormalizeNamePart(parts[1]), parts[2]);
                return;

            default:
                await SendAccountHelpAsync(client);
                return;
        }
    }

    private static Task SendAccountHelpAsync(IBbsConnection client)
    {
        return Task.WhenAll(
            client.SendLineAsync("Syntax: ;account show <user>"),
            client.SendLineAsync("Syntax: ;account test <user> <ON|OFF>"),
            client.SendLineAsync("Example: ;account show Scottt"),
            client.SendLineAsync("Example: ;account test Scottt OFF"));
    }

    private async Task HandleAccountShowAsync(IBbsConnection client, IBbsDoorContext door, string targetName)
    {
        var account = _bbsUserRepository.LoadUser(targetName);
        if (account == null)
        {
            await client.SendLineAsync($"Cannot find BBS user {targetName}");
            return;
        }

        // The account no longer stores a player name; the door owns the link and resolves the owned
        // character by BBS user id (works whether or not the character is online).
        string characterName = door.LookupPlayerByBbsUser(account.UserName)?.Name ?? "(none)";

        await client.SendLineAsync($"[BBS ACCOUNT] {targetName}");
        await client.SendLineAsync($"  BBS User    : {account.UserName}");
        await client.SendLineAsync($"  Character   : {characterName}");
        await client.SendLineAsync($"  BBS Sysop   : {FormatYesNo(account.IsSysop)}");
        await client.SendLineAsync($"  BBS Test    : {FormatYesNo(account.IsTestAccount)}");
        await client.SendLineAsync($"  Last IP     : {account.LastIpAddress}");
    }

    private async Task HandleAccountTestAsync(IBbsConnection client, string targetName, string stateToken)
    {
        if (!TryParseToggleState(stateToken, out bool isTestAccount))
        {
            await client.SendLineAsync("Syntax: ;account test <user> <ON|OFF>");
            return;
        }

        var account = _bbsUserRepository.LoadUser(targetName);
        if (account == null)
        {
            await client.SendLineAsync($"Cannot find BBS user {targetName}");
            return;
        }

        account.IsTestAccount = isTestAccount;
        _bbsUserRepository.SaveUser(account);
        await client.SendLineAsync($"{(isTestAccount ? "Enabled" : "Cleared")} test-account status for BBS user {account.UserName}.");
    }

    private async Task HandleUsersAsync(IBbsConnection client, IBbsDoorContext door, string args)
    {
        UsersFilter filter = UsersFilter.All;
        string token = args.Trim();

        if (!string.IsNullOrWhiteSpace(token))
        {
            if (IsUsersHelpToken(token))
            {
                await SendUsersHelpAsync(client);
                return;
            }

            if (!TryParseUsersFilter(token, out filter))
            {
                await SendUsersHelpAsync(client);
                return;
            }
        }

        var users = _bbsUserRepository.GetUsers()
            .Select(user =>
            {
                var linkedPlayer = door.LookupPlayerByBbsUser(user.UserName);
                return (User: user, Player: linkedPlayer);
            })
            .Where(entry => filter switch
            {
                UsersFilter.All => true,
                UsersFilter.Test => entry.User.IsTestAccount || entry.Player?.IsTestAccount == true,
                UsersFilter.Player => !entry.User.IsTestAccount && entry.Player?.IsTestAccount == false,
                _ => true,
            })
            .OrderBy(entry => entry.User.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string heading = filter switch
        {
            UsersFilter.All => "[SYSOP LIST USERS]",
            UsersFilter.Test => "[SYSOP LIST USERS TEST]",
            UsersFilter.Player => "[SYSOP LIST USERS PLAYER]",
            _ => "[SYSOP LIST USERS]",
        };

        await client.SendLineAsync($"\n{heading} {users.Count} total\n");
        int lineNumberWidth = Math.Max(2, users.Count.ToString().Length);
        int line = 1;
        await client.SendLineAsync(FormatUsersRow(lineNumberWidth, "#", "Board Name", "Player Name", "BBS Sysop", "Game Sysop", "Tester", "BBS Test", "Player Test"));
        await client.SendLineAsync(FormatUsersRow(lineNumberWidth, "-", "----------", "-----------", "--------", "--------", "------", "--------", "-----------"));
        foreach (var entry in users)
        {
            string playerName = entry.Player?.Name ?? string.Empty;
            string playerTest = entry.Player == null ? "-" : (entry.Player.IsTestAccount ? "Yes" : "No");
            await client.SendLineAsync(FormatUsersRow(
                lineNumberWidth,
                line++.ToString(),
                entry.User.UserName,
                playerName,
                entry.User.IsSysop ? "Yes" : "No",
                entry.Player?.IsSysop == true ? "Yes" : "No",
                entry.Player?.IsTesterSysop == true ? "Yes" : "No",
                entry.User.IsTestAccount ? "Yes" : "No",
                playerTest));
        }

        await client.SendLineAsync($"\nThere {(users.Count == 1 ? "is" : "are")} {users.Count} BBS user{(users.Count == 1 ? "" : "s")} registered.\n");
    }

    private static Task SendUsersHelpAsync(IBbsConnection client)
    {
        return Task.WhenAll(
            client.SendLineAsync("Syntax: ;users [ALL|TEST|PLAYER]"),
            client.SendLineAsync("  ALL    -- List all BBS users"),
            client.SendLineAsync("  TEST   -- List only accounts flagged as test on BBS or Player"),
            client.SendLineAsync("  PLAYER -- List only linked non-test player accounts"),
            client.SendLineAsync("Example: ;users TEST"),
            client.SendLineAsync("Example: ;users PLAYER"));
    }

    private static async Task RenderTicketListAsync(IBbsConnection client, IReadOnlyList<BbsTicketSummary> tickets)
    {
        await client.SendLineAsync();
        await client.SendLineAsync("BBS Tickets");
        await client.SendLineAsync("===========");

        if (tickets.Count == 0)
        {
            await client.SendLineAsync("No BBS tickets have been submitted yet.");
            return;
        }

        foreach (var ticket in tickets.OrderBy(ticket => ticket.Id))
        {
            string resolutionTag = ticket.IsResolved ? $"{Ansi.BrightGreen}[Resolved]{Ansi.Reset} " : string.Empty;
            string appTag = string.IsNullOrWhiteSpace(ticket.AppId) ? string.Empty : $"[{ticket.AppId.ToUpperInvariant()}] ";
            await client.SendLineAsync($"#{ticket.Id} {resolutionTag}{appTag}{ticket.Title} - {ticket.BriefDescription}");
        }
    }

    private static async Task RenderTicketDetailAsync(IBbsConnection client, BbsTicketRecord? ticket)
    {
        await client.SendLineAsync();

        if (ticket == null)
        {
            await client.SendLineAsync("That ticket was not found.");
            return;
        }

        await client.SendLineAsync($"Ticket #{ticket.Id}: {ticket.Title}");
        await client.SendLineAsync($"Reported: {FormatTimestamp(ticket.CreatedAt)}");
        await client.SendLineAsync($"Reporter: {FormatReporter(ticket.ReporterPlayerName, ticket.ReporterBbsUserName)}");
        await client.SendLineAsync($"Scope: {FormatScope(ticket)}");
        await client.SendLineAsync($"Status: {FormatTicketStatus(ticket)}");
        await client.SendLineAsync($"Brief: {ticket.BriefDescription}");

        if (!string.IsNullOrWhiteSpace(ticket.LocationText))
            await client.SendLineAsync($"Location: {ticket.LocationText}");

        if (!string.IsNullOrWhiteSpace(ticket.SubjectText))
            await client.SendLineAsync($"Subject: {ticket.SubjectText}");

        await client.SendLineAsync("Description:");
        foreach (string line in ticket.Description.Split([Environment.NewLine], StringSplitOptions.None))
            await client.SendLineAsync(line);
    }

    private static async Task PromptForTitleAsync(IBbsConnection client)
    {
        await client.SendLineAsync($"Title (required, up to {MaxTicketTitleLength} characters):");
    }

    private static async Task PromptForBriefAsync(IBbsConnection client)
    {
        await client.SendLineAsync($"Brief summary (required, one line, up to {MaxTicketBriefLength} characters):");
    }

    private static async Task PromptForLocationAsync(IBbsConnection client, TicketDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.DefaultLocationText))
            await client.SendLineAsync($"Location (optional, Enter or '-' to skip, up to {MaxTicketLocationLength} characters):");
        else
            await client.SendLineAsync($"Location (Enter to use '{draft.DefaultLocationText}', '-' to skip, up to {MaxTicketLocationLength} characters):");
    }

    private static async Task PromptForSubjectAsync(IBbsConnection client)
    {
        await client.SendLineAsync($"Subject (optional, Enter or '-' to skip, up to {MaxTicketSubjectLength} characters):");
    }

    private static async Task PromptForDescriptionAsync(IBbsConnection client)
    {
        await client.SendLineAsync("Enter the full description, one line at a time.");
        await client.SendLineAsync("Include what happened, what you expected, and any steps to reproduce it.");
        await client.SendLineAsync("Type a single '.' on its own line when you are finished.");
    }

    private bool TryResolveViewerName(string? viewerBbsUserName, IBbsConnection client, out string viewerName)
    {
        viewerName = ResolveViewerName(viewerBbsUserName, client);
        return !string.IsNullOrWhiteSpace(viewerName);
    }

    private async Task HandleBanAsync(IBbsConnection client, string args)
    {
        string entry = (args ?? string.Empty).Trim();
        if (entry.Length == 0)
        {
            await client.SendLineAsync("Syntax: ;ban <ip-or-cidr>   (e.g. ;ban 203.0.113.5  or  ;ban 203.0.113.0/24)");
            return;
        }

        if (_ipAccessControl.TryBan(entry, out string display, out string error))
        {
            await client.SendLineAsync($"Banned {display}. New connections from it are now blocked.");
            return;
        }

        await client.SendLineAsync(error);
    }

    private async Task HandleUnbanAsync(IBbsConnection client, string args)
    {
        string entry = (args ?? string.Empty).Trim();
        if (entry.Length == 0)
        {
            await client.SendLineAsync("Syntax: ;unban <ip-or-cidr>   (use ;bans to see the exact entries)");
            return;
        }

        if (_ipAccessControl.TryUnban(entry))
        {
            await client.SendLineAsync($"Removed {entry} from the IP banlist.");
            return;
        }

        await client.SendLineAsync($"{entry} is not in the IP banlist. Use ;bans to see current entries.");
    }

    private async Task HandleBansAsync(IBbsConnection client)
    {
        int cap = _ipAccessControl.MaxConnectionsPerIp;
        var bans = _ipAccessControl.GetBans();

        await client.SendLineAsync();
        await client.SendLineAsync("IP Access Control");
        await client.SendLineAsync("=================");
        await client.SendLineAsync($"Per-IP connection cap: {(cap > 0 ? cap.ToString() : "unlimited")}  (change with ;maxconn <n>, 0 = unlimited)");
        await client.SendLineAsync();

        if (bans.Count == 0)
        {
            await client.SendLineAsync("No IP addresses are banned. Add one with ;ban <ip-or-cidr>.");
        }
        else
        {
            await client.SendLineAsync($"Banned ({bans.Count}):");
            foreach (string ban in bans)
                await client.SendLineAsync($"  {ban}");
        }

        var active = _ipAccessControl.GetActiveConnectionCounts();
        var multi = active.Where(a => a.Count > 1).ToList();
        if (multi.Count > 0)
        {
            await client.SendLineAsync();
            await client.SendLineAsync("Addresses with multiple live connections:");
            foreach (var (ip, count) in multi)
                await client.SendLineAsync($"  {ip}  x{count}");
        }
    }

    private async Task HandleMaxConnAsync(IBbsConnection client, string args)
    {
        string value = (args ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            int current = _ipAccessControl.MaxConnectionsPerIp;
            await client.SendLineAsync($"Per-IP connection cap is {(current > 0 ? current.ToString() : "unlimited")}. Set with ;maxconn <n> (0 = unlimited).");
            return;
        }

        if (!int.TryParse(value, out int max) || max < 0)
        {
            await client.SendLineAsync("Syntax: ;maxconn <n>   where n is 0 (unlimited) or a positive number of connections per IP.");
            return;
        }

        _ipAccessControl.SetMaxConnectionsPerIp(max);
        await client.SendLineAsync($"Per-IP connection cap set to {(max > 0 ? max.ToString() : "unlimited")}.");
    }

    // Board-wide restart/shutdown. Works from the BBS menu (front controller orchestrates directly) AND from
    // inside the mud (backend relay controller publishes the request up to the front over the realm bus).
    // Either way the board controller warns everyone with a countdown, then relaunches / stops the topology.
    private async Task HandleBoardStopAsync(IBbsConnection client, string args, string sysop, bool restart)
    {
        string verb = restart ? "restart" : "shut down";
        if (BoardController is not { } board)
        {
            await client.SendLineAsync($"Board {verb} is only available on the multi-realm board.");
            return;
        }

        int seconds = 20;
        var parts = (args ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && int.TryParse(parts[0], out int parsed) && parsed >= 0)
            seconds = Math.Min(parsed, 300);

        await board.RequestBoardStopAsync(seconds, restart, $"sysop {sysop}");
        await client.SendLineAsync($"Board {verb} requested ({seconds}s). Every realm and everyone on the menu will be warned.");
    }

    private bool TryResolveBbsSysopViewerName(string? viewerBbsUserName, IBbsConnection client, out string viewerName)
    {
        viewerName = ResolveViewerName(viewerBbsUserName, client);
        return !string.IsNullOrWhiteSpace(viewerName) && _bbsUserRepository.LoadUser(viewerName)?.IsSysop == true;
    }

    private bool IsBbsSysop(string? viewerBbsUserName)
    {
        string viewerName = ResolveViewerName(viewerBbsUserName, null);
        return !string.IsNullOrWhiteSpace(viewerName) && _bbsUserRepository.LoadUser(viewerName)?.IsSysop == true;
    }

    private static string ResolveViewerName(string? viewerBbsUserName, IBbsConnection? client)
    {
        if (!string.IsNullOrWhiteSpace(viewerBbsUserName))
            return viewerBbsUserName.Trim();

        if (!string.IsNullOrWhiteSpace(client?.CurrentBbsUserName))
            return client.CurrentBbsUserName.Trim();

        return string.Empty;
    }

    private static bool TryParseToggleState(string value, out bool enabled)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "on":
            case "yes":
            case "true":
            case "grant":
            case "enable":
            case "1":
                enabled = true;
                return true;

            case "off":
            case "no":
            case "false":
            case "revoke":
            case "disable":
            case "0":
                enabled = false;
                return true;

            default:
                enabled = false;
                return false;
        }
    }

    private static bool TryParseUsersFilter(string value, out UsersFilter filter)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "all":
                filter = UsersFilter.All;
                return true;

            case "test":
            case "tests":
            case "testaccount":
            case "testaccounts":
                filter = UsersFilter.Test;
                return true;

            case "player":
            case "players":
            case "real":
            case "live":
            case "nontest":
                filter = UsersFilter.Player;
                return true;

            default:
                filter = UsersFilter.All;
                return false;
        }
    }

    private static bool IsUsersHelpToken(string value)
    {
        string token = value.Trim();
        return token.Equals("help", StringComparison.OrdinalIgnoreCase)
            || token.Equals("?", StringComparison.Ordinal);
    }

    private static bool IsLegacyBugAliasCommand(string command)
    {
        return command.Equals(";bug", StringComparison.OrdinalIgnoreCase)
            || command.Equals(";bugs", StringComparison.OrdinalIgnoreCase)
            || command.Equals(";listbugs", StringComparison.OrdinalIgnoreCase)
            || command.Equals(";showbug", StringComparison.OrdinalIgnoreCase)
            || command.Equals(";completebug", StringComparison.OrdinalIgnoreCase)
            || command.Equals(";deletebug", StringComparison.OrdinalIgnoreCase)
            || command.Equals(";removebug", StringComparison.OrdinalIgnoreCase);
    }

    // The generic identity the mounted door attaches to the connection (via AppData). Null at the BBS
    // level (login / main menu) where no door session has wrapped the connection.
    private static IBbsIdentity? ResolveIdentity(IBbsConnection client) => client.AppData as IBbsIdentity;

    private static string ResolveAppId(IBbsConnection client)
    {
        if (!string.IsNullOrWhiteSpace(client.CurrentHostedAppId))
            return client.CurrentHostedAppId.Trim();

        return ResolveIdentity(client)?.HasActiveCharacter == true ? BbsAppIds.Mmudreborn : BbsAppIds.Bbs;
    }

    private static string ResolveWorldId(IBbsConnection client)
    {
        if (!string.IsNullOrWhiteSpace(client.CurrentHostedWorldId))
            return client.CurrentHostedWorldId.Trim();

        return ResolveIdentity(client)?.HasActiveCharacter == true ? HostedAppIds.DefaultWorldId : string.Empty;
    }

    private static string ResolveCurrentLocationText(IBbsConnection client)
    {
        // The door supplies a rich in-realm location (room name + coordinates); at the BBS level it is
        // empty and we fall back to the connection's BBS presence location ("Main Menu", "Logging In").
        string doorLocation = ResolveIdentity(client)?.TicketLocation ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(doorLocation))
            return doorLocation;

        if (client is TelnetClient telnetClient)
            return telnetClient.BbsPresenceLocation.Trim();

        return string.Empty;
    }

    private static bool TryValidateFieldLength(string value, int maxLength, out string errorMessage)
    {
        if (value.Trim().Length <= maxLength)
        {
            errorMessage = string.Empty;
            return true;
        }

        errorMessage = $"Please keep that under {maxLength} characters.";
        return false;
    }

    private static bool TryParseTicketId(string args, out int ticketId)
    {
        return int.TryParse(args, out ticketId) && ticketId > 0;
    }

    private static void SplitCommand(string input, out string command, out string args)
    {
        string trimmed = (input ?? string.Empty).Trim();
        int spaceIndex = trimmed.IndexOf(' ');
        if (spaceIndex < 0)
        {
            command = trimmed;
            args = string.Empty;
            return;
        }

        command = trimmed[..spaceIndex];
        args = trimmed[(spaceIndex + 1)..].Trim();
    }

    private static bool TryGetPendingTicket(IBbsConnection client, out TicketDraft draft)
    {
        return PendingTicketDrafts.TryGetValue(client, out draft!);
    }

    private static void SetPendingTicket(IBbsConnection client, TicketDraft draft)
    {
        PendingTicketDrafts.Remove(client);
        PendingTicketDrafts.Add(client, draft);
    }

    private static void ClearPendingTicket(IBbsConnection client)
    {
        PendingTicketDrafts.Remove(client);
    }

    private static string FormatTimestamp(string timestamp)
    {
        if (DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        return timestamp;
    }

    private static string FormatReporter(string reporterPlayerName, string reporterBbsUserName)
    {
        if (!string.IsNullOrWhiteSpace(reporterPlayerName) &&
            !string.IsNullOrWhiteSpace(reporterBbsUserName) &&
            !string.Equals(reporterPlayerName, reporterBbsUserName, StringComparison.OrdinalIgnoreCase))
        {
            return $"{reporterPlayerName} ({reporterBbsUserName})";
        }

        if (!string.IsNullOrWhiteSpace(reporterPlayerName))
            return reporterPlayerName;

        return string.IsNullOrWhiteSpace(reporterBbsUserName) ? "Unknown" : reporterBbsUserName;
    }

    private static string FormatScope(BbsTicketRecord ticket)
    {
        string app = string.IsNullOrWhiteSpace(ticket.AppId) ? BbsAppIds.Bbs.ToUpperInvariant() : ticket.AppId.ToUpperInvariant();
        return string.IsNullOrWhiteSpace(ticket.WorldId)
            ? app
            : $"{app} / {ticket.WorldId}";
    }

    private static string FormatYesNo(bool value)
    {
        return value ? "Yes" : "No";
    }

    private static string FormatUsersRow(int lineNumberWidth, string rowNumber, string boardName, string playerName, string bbsSysop, string gameSysop, string gameTester, string bbsTest, string playerTest)
    {
        return "  "
            + rowNumber.PadLeft(lineNumberWidth)
            + " " + boardName.PadRight(15)
            + " " + playerName.PadRight(15)
            + " " + bbsSysop.PadRight(9)
            + " " + gameSysop.PadRight(10)
            + " " + gameTester.PadRight(6)
            + " " + bbsTest.PadRight(8)
            + " " + playerTest.PadRight(11);
    }

    private static string FormatTicketStatus(BbsTicketRecord ticket)
    {
        if (!ticket.IsResolved)
            return "Open";

        string resolvedBy = string.IsNullOrWhiteSpace(ticket.ResolvedByBbsUserName) ? "Unknown" : ticket.ResolvedByBbsUserName;
        if (string.IsNullOrWhiteSpace(ticket.ResolvedAt))
            return $"Resolved by {resolvedBy}";

        return $"Resolved by {resolvedBy} on {FormatTimestamp(ticket.ResolvedAt)}";
    }

    private static async Task RenderWhoAsync(IBbsConnection client, IReadOnlyList<BbsPresenceSnapshot> entries, bool showIpAddress)
    {
        var visibleEntries = entries
            .OrderBy(entry => entry.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await client.SendLineAsync();
        // Location is supplied pre-composed by the door (e.g. mmudreborn reports "mmudreborn (Ptery)" —
        // door id plus its active character name). The column is wide enough to hold the door name plus a
        // parenthesised character name without pushing the IP column out of alignment.
        if (showIpAddress)
        {
            await client.SendLineAsync($"{"User",-22} {"Time",5}    {"Location",-28} IP");
            await client.SendLineAsync(new string('=', 76));
        }
        else
        {
            await client.SendLineAsync($"{"User",-22} {"Time",5}    Location");
            await client.SendLineAsync(new string('=', 60));
        }

        if (visibleEntries.Count == 0)
        {
            await client.SendLineAsync("No users are currently connected.");
            return;
        }

        foreach (var entry in visibleEntries)
        {
            string displayName = entry.IsSysop ? $"{entry.UserName} (Sysop)" : entry.UserName;
            string minutesOnline = Math.Max(0, entry.MinutesOnline).ToString(CultureInfo.InvariantCulture);

            if (showIpAddress)
            {
                await client.SendLineAsync($"{displayName,-22} {minutesOnline,5}    {entry.Location,-28} {entry.IpAddress}");
            }
            else
            {
                await client.SendLineAsync($"{displayName,-22} {minutesOnline,5}    {entry.Location}");
            }
        }
    }

    private enum TicketDraftStep
    {
        AwaitingTitle,
        AwaitingBriefDescription,
        AwaitingLocation,
        AwaitingSubject,
        AwaitingDescription,
    }

    private sealed class TicketDraft
    {
        public string AppId { get; set; } = string.Empty;
        public string WorldId { get; set; } = string.Empty;
        public string ReporterBbsUserName { get; set; } = string.Empty;
        public string ReporterPlayerName { get; set; } = string.Empty;
        public string DefaultLocationText { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string BriefDescription { get; set; } = string.Empty;
        public string LocationText { get; set; } = string.Empty;
        public string SubjectText { get; set; } = string.Empty;
        public List<string> DescriptionLines { get; } = [];
        public TicketDraftStep Step { get; set; }
    }

    private enum UsersFilter
    {
        All,
        Test,
        Player,
    }
}