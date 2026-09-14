namespace CWGaming.Shared;

/// <summary>
/// The generic BBS telnet connection the host owns: all byte I/O, line editing, output buffering,
/// deferred broadcasts, reprompt redraw, GMCP, echo/history, and the per-connection presence/context
/// fields. It carries NO game concepts (no Player, Session, or MUD prompt) — a mounted "door" attaches
/// those through the opaque <see cref="AppData"/> slot and supplies its prompt via
/// <see cref="PromptProvider"/>. This is the contract that lets the host run any door without
/// referencing it.
/// </summary>
public interface IBbsConnection
{
    bool Connected { get; }
    string CurrentBbsUserName { get; set; }
    string CurrentHostedAppId { get; set; }
    string CurrentHostedWorldId { get; set; }
    string CurrentHostedLocation { get; set; }
    bool HasPendingInput { get; }
    bool IsAtLineStart { get; }

    /// <summary>
    /// Door-supplied prompt factory. After any out-of-band line (a broadcast or a deferred-broadcast
    /// flush) the host re-emits the result opaquely so it never constructs a game prompt itself — that
    /// keeps the host free of game-specific presentation. Returns empty (or null) when there is nothing
    /// to redraw: no active door session, or the door asked broadcasts not to reprompt.
    /// </summary>
    Func<string>? PromptProvider { get; set; }

    /// <summary>
    /// Opaque per-connection slot the mounted door uses to stash its own session object (the MajorBBS
    /// <c>usrptr</c> pattern). The host never reads or interprets it.
    /// </summary>
    object? AppData { get; set; }

    Task SendAsync(string text);
    Task SendLineAsync(string text = "");
    void BeginBuffering();
    void FlushOutput();

    /// <summary>
    /// Send pre-formatted full-screen ANSI art (login screens, WCCMAP-style maps) VERBATIM, bypassing the
    /// per-line transmit cap that <see cref="SendAsync"/> applies to prose. A centered 80-column banner can
    /// exceed that cap once ANSI colour codes are counted toward the length, and splitting one line injects
    /// a stray CRLF that tears the art down the middle. Defaults to <see cref="SendAsync"/> for connections
    /// that do not cap line length.
    /// </summary>
    Task SendAnsiArtAsync(string art) => SendAsync(art);

    /// <summary>
    /// True when the connected client negotiated GMCP (telnet option 201). Clients that never sent
    /// IAC DO GMCP (MegaMUD, raw telnet) report false and receive no GMCP bytes.
    /// </summary>
    bool GmcpEnabled => false;

    /// <summary>
    /// Send a GMCP package as a telnet subnegotiation: <c>IAC SB 201 "Package.Name {json}" IAC SE</c>.
    /// <paramref name="payloadJson"/> is the already-serialized JSON value. This rides an out-of-band
    /// telnet channel and never touches the visible byte stream; it is a no-op unless the client opted
    /// in, so legacy clients are byte-for-byte unaffected.
    /// </summary>
    Task SendGmcpAsync(string package, string payloadJson) => Task.CompletedTask;

    bool TryDeferBroadcastLine(string text, bool reprompt, bool prependLineBreak = true);
    Task FlushDeferredBroadcastLinesAsync();

    /// <summary>
    /// Begin/end a "held output" scope. While a scope is active AND the player has typed-ahead input,
    /// <see cref="SendLineAsync"/> routes into the deferred-broadcast queue instead of writing directly,
    /// so async output triggered by someone else (a party-follow render driven by the leader's move, a
    /// combat round) doesn't trample the line the player is composing. Flushed on enter/clear like a
    /// broadcast. Always pair Begin/End (try/finally). No-op for clients that don't buffer input.
    /// </summary>
    void BeginHeldOutput() { }
    void EndHeldOutput() { }
    Task EnsureNewLineAsync();
    Task PrepareForBroadcastAsync(bool forceClearCurrentLine = false);
    Task ClearCurrentLineAsync();

    /// <summary>
    /// Redraw the prompt in place for a server-pushed refresh (rest/meditate HP-mana climb, room-spell
    /// pulse, poison tick): clear the current line, re-emit <paramref name="prompt"/>, then restore any
    /// input the player has typed but not yet sent. Unlike <see cref="SendAsync"/> of a prompt, this
    /// never appends a second prompt inline or clobbers a half-typed command.
    /// </summary>
    Task RedrawPromptWithInputAsync(string prompt);
    Task DiscardBufferedLineEndingsAsync(CancellationToken ct = default);

    Task<string?> ReadLineAsync(CancellationToken ct = default);
    Task<string?> ReadLineEchoAsync(bool echo = true, CancellationToken ct = default);
    Task<string?> ReadLineEchoAsync(int timeoutMs, bool echo = true, CancellationToken ct = default);
    Task<string?> ReadLineMaskedAsync(char mask = '*', CancellationToken ct = default);
    Task<string?> ReadKeyAsync(CancellationToken ct = default);
    Task<string?> ReadKeyAsync(int timeoutMs, CancellationToken ct = default);

    void SetCommandHistoryEnabled(bool enabled);
    void SetEcho(bool enabled);

    /// <summary>
    /// Re-assert the transport's gameplay input mode (e.g. telnet char-at-a-time negotiation) when a
    /// session transitions into the realm. Transport-specific; a no-op for connections that don't
    /// negotiate, so a door can call it generically without knowing the concrete transport.
    /// </summary>
    Task ReassertGameplayInputModeAsync() => Task.CompletedTask;

    void Disconnect();

    /// <summary>
    /// True when the HOST deliberately closed this connection (a duplicate login replacing it, a sysop
    /// kick, board shutdown) rather than the peer going away. Doors need the distinction: a door that
    /// penalizes carrier loss must not punish a player whose session the board itself replaced. Defaults
    /// to false so a transport that never closes connections administratively needs no implementation.
    /// </summary>
    bool DisconnectedByHost => false;
}
