using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using CWGaming.Shared;

namespace CWGamingServ;

public sealed class TelnetClient : IBbsConnection
{
    private readonly record struct DeferredBroadcastLine(string Text, bool Reprompt, bool PrependLineBreak);

    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly StringBuilder _inputBuffer = new();
    private readonly List<string> _commandHistory = [];
    private readonly object _writeLock = new();
    private readonly Queue<DeferredBroadcastLine> _deferredBroadcastLines = new();
    private static readonly Encoding Cp437 = Encoding.GetEncoding(437);
    private bool _isAtLineStart = true;
    private int _hasPendingInput;
    private int _heldOutputDepth;
    private int _droppedDeferredBroadcastLineCount;
    private bool _commandHistoryEnabled;
    private int _commandHistoryBrowseIndex = -1;
    private string _commandHistoryDraft = string.Empty;

    private readonly MemoryStream _outputBuffer = new(4096);
    private bool _buffering;

    private readonly byte[] _readBuf = new byte[256];
    private int _readPos;
    private int _readLen;
    // The line terminator byte ('\r' or '\n') most recently emitted, or 0 if none is
    // pending. Used to absorb the companion byte of a two-byte sequence (\r\n, \n\r,
    // \r\0, \n\0) that arrives in a later read, so one Enter never yields a phantom
    // empty line — independent of which order the client sends CR/LF/NUL.
    private byte _lastLineTerminator;
    private string _forwardedRemoteAddress = string.Empty;

    private const int MaxInputLength = 256;
    private const int MaxCommandHistoryEntries = 50;
    private const int MaxDeferredBroadcastLines = 128;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public bool Connected => _tcpClient.Connected;
    // The mounted door stashes its per-session client object here and supplies the prompt the host
    // redraws after broadcasts. The host transport never interprets either — see IBbsConnection.
    public Func<string>? PromptProvider { get; set; }
    public object? AppData { get; set; }
    public string CurrentBbsUserName { get; set; } = "";
    // Which realm this connection is INSIDE right now ("" = on the front: login, BBS menu, realm picker).
    // Duplicate-session policy is per realm, not per account — one login is meant to hold a session in
    // Main and another in PvP at the same time — so the kick needs to know where each connection is.
    // Written by the realm launcher on the session task, read by the host's kick on another; hence volatile.
    private volatile string _currentRealmId = "";
    public string CurrentRealmId
    {
        get => _currentRealmId;
        set => _currentRealmId = value ?? "";
    }
    public string CurrentHostedAppId { get; set; } = HostedAppIds.Bbs;
    public string CurrentHostedWorldId { get; set; } = string.Empty;
    public string CurrentHostedLocation
    {
        get => BbsPresenceLocation;
        set => BbsPresenceLocation = value;
    }
    public bool HasPendingInput => Volatile.Read(ref _hasPendingInput) != 0;
    public bool IsAtLineStart => _isAtLineStart;
    public bool GmcpEnabled => _gmcpEnabled;
    public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
    public string BbsPresenceUserName { get; set; } = "";
    public bool BbsPresenceIsSysop { get; set; }
    public string BbsPresenceLocation { get; set; } = "Logging In";
    public string BbsPresenceIpAddress { get; set; } = "";
    public string RemoteAddress => string.IsNullOrWhiteSpace(_forwardedRemoteAddress)
        ? (_tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty
        : _forwardedRemoteAddress;

    private const byte IAC = 255;
    private const byte WILL = 251;
    private const byte WONT = 252;
    private const byte DONT = 254;
    private const byte DO = 253;
    private const byte SB = 250;
    private const byte SE = 240;
    private const byte BINARY = 0;
    private const byte ECHO = 1;
    private const byte SGA = 3;
    private const byte LINEMODE = 34;
    private const byte NAWS = 31;
    private const byte GMCP = 201; // Generic Mud Communication Protocol (out-of-band JSON state)

    // Set true only after the client replies IAC DO GMCP to our offer. Legacy clients
    // (MegaMUD, raw telnet) never do, so they receive zero GMCP bytes. Written on the
    // read loop, read on the game loop, hence volatile.
    private volatile bool _gmcpEnabled;

    public TelnetClient(TcpClient tcpClient)
    {
        _tcpClient = tcpClient;
        _tcpClient.NoDelay = true;
        ConfigureKeepAlive();
        _stream = tcpClient.GetStream();
    }

    // Linux IPPROTO_TCP / TCP_USER_TIMEOUT. Not exposed as a SocketOptionName, so it goes in raw.
    private const int IPPROTO_TCP = 6;
    private const int TCP_USER_TIMEOUT = 18;

    // The ACTIVE budget: how long a peer may leave OUR data unacknowledged before the kernel errors the
    // connection. This is the one that matters, because the dangerous case is always a busy connection —
    // the combat coordinator writes to a player every pulse, so a client that has gone away starts
    // accumulating unacked bytes within seconds of the fight starting.
    //
    // Deliberately MUCH tighter than the idle budget below. These used to share one ~2 minute clock, which
    // meant the harmless case (idle player, nobody attacking them) and the harmful one (dead client being
    // beaten to death) were reaped on the same slow timer. Ten seconds is about two combat rounds.
    //
    // Why not tighter still: ordinary packet loss recovers far faster than this (Linux starts at a 200ms
    // RTO and doubles, so a normal recovery is sub-second, and even five straight failed retransmits only
    // totals ~6s), so 10s is nowhere near loss territory — but an ISP route flap can be a few seconds, and
    // the odd wifi laptop parks its radio for two or three. Ten clears both.
    private const int DefaultDeadPeerActiveTimeoutMs = 10_000;

    // The IDLE budget: nothing in flight, so TCP_USER_TIMEOUT has nothing to measure and keepalive probes
    // are the only liveness signal. Probe after 10s of quiet, then 5s x 2 = ~20s to declare the peer gone.
    // Relaxed on purpose: a player sitting idle is by definition not in combat, so a ghost here costs
    // nothing but a WHO entry. Note the probes are answered by the peer's OS kernel, not by the MUD client
    // or the human, so an AFK player never trips this — only a machine that is off, asleep or unreachable.
    private const int DefaultDeadPeerIdleSeconds = 10;
    private const int DefaultDeadPeerProbeIntervalSeconds = 5;
    private const int DefaultDeadPeerProbeCount = 2;

    // Sysop-tunable without a rebuild (host restart required — these are applied per socket at accept).
    // The right values are empirical: watch the ghost reports and tighten. Deliberately env vars rather
    // than a game ServerSetting, because socket liveness is a transport concern the BBS host owns and must
    // keep working for every door, not just the realm.
    public const string DeadPeerActiveTimeoutEnvironmentVariable = "CWGAMING_DEAD_PEER_ACTIVE_MS";
    public const string DeadPeerIdleSecondsEnvironmentVariable = "CWGAMING_DEAD_PEER_IDLE_SECONDS";
    public const string DeadPeerProbeIntervalEnvironmentVariable = "CWGAMING_DEAD_PEER_PROBE_SECONDS";
    public const string DeadPeerProbeCountEnvironmentVariable = "CWGAMING_DEAD_PEER_PROBE_COUNT";

    // How long a peer may go without acknowledging ANYTHING before the board is willing to call it a
    // ghost. Must sit comfortably above the keepalive schedule above: a live peer's OS answers a probe
    // within (idle + probe interval), i.e. ~15s by default, so 30s only ever accuses a peer that has
    // missed several probes in a row. Used to decide kicks, never to close a connection on its own —
    // the kernel's own keepalive/TCP_USER_TIMEOUT budgets do the closing.
    private const int DefaultGhostSilenceSeconds = 30;
    public const string GhostSilenceSecondsEnvironmentVariable = "CWGAMING_GHOST_SILENCE_SECONDS";

    public static TimeSpan GhostSilenceBudget =>
        TimeSpan.FromSeconds(ReadTuning(GhostSilenceSecondsEnvironmentVariable, DefaultGhostSilenceSeconds, 5, 3600));

    private static int ReadTuning(string environmentVariable, int fallback, int min, int max)
    {
        string? raw = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out int value))
            return fallback;

        return Math.Clamp(value, min, max);
    }

    private void ConfigureKeepAlive()
    {
        try
        {
            int idleSeconds = ReadTuning(DeadPeerIdleSecondsEnvironmentVariable, DefaultDeadPeerIdleSeconds, 1, 3600);
            int probeSeconds = ReadTuning(DeadPeerProbeIntervalEnvironmentVariable, DefaultDeadPeerProbeIntervalSeconds, 1, 600);
            int probeCount = ReadTuning(DeadPeerProbeCountEnvironmentVariable, DefaultDeadPeerProbeCount, 1, 20);
            int activeTimeoutMs = ReadTuning(DeadPeerActiveTimeoutEnvironmentVariable, DefaultDeadPeerActiveTimeoutMs, 1_000, 600_000);

            var socket = _tcpClient.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, idleSeconds); } catch { }
            try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, probeSeconds); } catch { }
            try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, probeCount); } catch { }

            // GHOSTING. Keepalive alone only covers an IDLE connection: the moment the server has bytes
            // in flight, the keepalive timer is replaced by the retransmission timer, and Linux then
            // retries for tcp_retries2 (15) hops — roughly 13-30 MINUTES — before erroring the socket.
            // For a MUD that is exactly the wrong case: a player whose client quits or whose link drops
            // mid-combat is the one we are actively writing to. The socket sits ESTABLISHED with an
            // unacked Send-Q, Poll(SelectRead) never goes true (no data, no FIN, no error yet), so the
            // proxy pump keeps polling, the realm backend keeps the session, and the character stands in
            // the room being attacked for the whole retransmit budget. Live capture on the board showed
            // exactly this: sockets in `timer:(on, ...)` with a non-empty Send-Q alongside the players
            // reporting they had hung up.
            //
            // TCP_USER_TIMEOUT bounds that: once data has gone unacknowledged this long the connection
            // errors, the read/poll fails, the proxy tears both legs down and the host's teardown runs
            // HandleDroppedConnection — which is what actually takes the character out of the world.
            try
            {
                socket.SetRawSocketOption(IPPROTO_TCP, TCP_USER_TIMEOUT, BitConverter.GetBytes(activeTimeoutMs));
            }
            catch { }
        }
        catch { }
    }

    // Linux TCP_INFO (getsockopt IPPROTO_TCP / 11) has a fixed layout: eight u8 fields, then u32s.
    // Byte 0 is tcpi_state (1 = ESTABLISHED); tcpi_last_ack_recv — MILLISECONDS since the peer last
    // acknowledged anything — is the thirteenth u32, at byte 56.
    private const int TCP_INFO = 11;
    private const int TcpInfoLastAckRecvOffset = 56;
    private const byte TcpStateEstablished = 1;

    /// <summary>
    /// How long since the peer's TCP stack last acknowledged anything, read straight from the kernel.
    ///
    /// This is the liveness signal the board already pays for and never consulted. SO_KEEPALIVE (above)
    /// probes a quiet connection every few seconds and the probes are answered by the peer's OPERATING
    /// SYSTEM — not by the MUD client, and not by the human. So a player who is stood still doing nothing
    /// keeps this near zero, while a machine that is off, asleep, or unreachable lets it climb. That is
    /// exactly the distinction "have they typed lately?" cannot make, and the reason no part of this
    /// server should ever decide a session is gone by measuring input.
    ///
    /// Returns false when the kernel won't tell us (non-Linux, or the socket is already torn down), in
    /// which case callers must assume the peer is ALIVE — an unproven death is not a death.
    /// </summary>
    public bool TryGetPeerAckSilence(out TimeSpan silence)
    {
        silence = TimeSpan.Zero;
        try
        {
            Span<byte> info = stackalloc byte[TcpInfoLastAckRecvOffset + sizeof(uint)];
            int written = _tcpClient.Client.GetRawSocketOption(IPPROTO_TCP, TCP_INFO, info);
            if (written < info.Length)
                return false;

            if (info[0] != TcpStateEstablished)
            {
                silence = TimeSpan.MaxValue; // FIN/RST already seen: as dead as a socket gets
                return true;
            }

            silence = TimeSpan.FromMilliseconds(BitConverter.ToUInt32(info[TcpInfoLastAckRecvOffset..]));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True only when the kernel can PROVE the far end is gone — the socket is closed, no longer
    /// established, or has ignored every keepalive probe for <paramref name="silenceBudget"/>. Anything
    /// less (including "we couldn't measure") reports false, because the cost of the two answers is not
    /// symmetric: leaving a ghost standing for one more keepalive round is a cosmetic WHO entry, while
    /// dropping a live player mid-fight is the bug this whole path exists to avoid.
    /// <paramref name="diagnosis"/> always comes back populated, for logging either way.
    /// </summary>
    public bool IsPeerProvablyDead(TimeSpan silenceBudget, out string diagnosis)
    {
        if (!Connected)
        {
            diagnosis = "socket already closed";
            return true;
        }

        if (!TryGetPeerAckSilence(out TimeSpan silence))
        {
            diagnosis = "peer liveness unknown (kernel would not report TCP_INFO)";
            return false;
        }

        if (silence == TimeSpan.MaxValue)
        {
            diagnosis = "TCP connection is no longer established";
            return true;
        }

        if (silence > silenceBudget)
        {
            diagnosis = $"peer has not acknowledged anything for {silence.TotalSeconds:F0}s";
            return true;
        }

        diagnosis = $"peer acknowledged {silence.TotalSeconds:F1}s ago";
        return false;
    }

    /// <summary>One-line liveness summary for disconnect/kick logging.</summary>
    public string DescribePeerLiveness()
    {
        IsPeerProvablyDead(GhostSilenceBudget, out string diagnosis);
        return diagnosis;
    }

    public async Task NegotiateAsync()
    {
        await SendRawAsync(new byte[]
        {
            IAC, WILL, BINARY, IAC, DO, BINARY,
            IAC, WILL, ECHO,
            IAC, WILL, SGA, IAC, DO, SGA,
            IAC, DONT, LINEMODE,
            IAC, DO, NAWS,
            IAC, WILL, GMCP,
        });
    }

    // Drain anything the client sends as part of its connect-time handshake — its IAC negotiation
    // responses (WILL/WONT/DO/DONT, subnegotiations like NAWS) and the stray CR/LF/NUL bytes some
    // clients (Megamud most notably) emit right after a fresh TCP connect. Without this, the very
    // first ReadLine consumes one of those bytes as an empty input → the login loop sees "" and
    // immediately reprints the prompt. That's the "two prompts in a row" failure mode that hits
    // ~70% of Megamud first-connects after a server restart. Stops at the first byte that looks
    // like real user input (anything not IAC/CR/LF/NUL), leaving it in the buffer for the read.
    public async Task DrainInitialClientHandshakeAsync(CancellationToken ct = default)
    {
        if (RuntimeConfiguration.IsFastTestModeEnabled())
            return;

        // 175ms grace for slow links to deliver the negotiation reply; reset on each drained
        // byte so a long subnegotiation doesn't time us out mid-consume.
        DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(175);
        bool consumedAny = false;

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadlineUtc)
        {
            if (!TryPeekImmediateByte(out byte b, out bool fromBuffer))
            {
                try
                {
                    await Task.Delay(10, ct);
                }
                catch
                {
                    break;
                }
                continue;
            }

            if (b == IAC)
            {
                ConsumeImmediateByte(fromBuffer);
                await SkipTelnetCommand(ct);
                consumedAny = true;
                deadlineUtc = DateTime.UtcNow.AddMilliseconds(100);
                continue;
            }

            if (b == '\r' || b == '\n' || b == 0)
            {
                ConsumeImmediateByte(fromBuffer);
                consumedAny = true;
                deadlineUtc = DateTime.UtcNow.AddMilliseconds(100);
                continue;
            }

            // First real input byte — stop. Don't consume it; the next ReadLine will.
            break;
        }

        if (consumedAny)
        {
            _lastLineTerminator = 0;
            _inputBuffer.Clear();
            Volatile.Write(ref _hasPendingInput, 0);
        }
    }

    public Task ReassertGameplayInputModeAsync()
    {
        return SendRawAsync(new byte[]
        {
            IAC, WILL, ECHO,
            IAC, WILL, SGA, IAC, DO, SGA,
            IAC, DONT, LINEMODE,
        });
    }

    public async Task TryCaptureForwardedRemoteAddressAsync(CancellationToken ct = default)
    {
        if (HasBufferedData)
            return;

        DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(150);
        while (!ct.IsCancellationRequested && _tcpClient.Available <= 0)
        {
            if (DateTime.UtcNow >= deadlineUtc)
                return;

            try
            {
                await Task.Delay(5, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (_tcpClient.Available <= 0)
            return;

        while (!ct.IsCancellationRequested && _readLen < _readBuf.Length)
        {
            int remainingMs = (int)Math.Max(1, (deadlineUtc - DateTime.UtcNow).TotalMilliseconds);
            if (remainingMs <= 0)
                break;

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(remainingMs);

            int bytesRead;
            try
            {
                bytesRead = await _stream.ReadAsync(_readBuf.AsMemory(_readLen, _readBuf.Length - _readLen), readCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                return;
            }

            if (bytesRead <= 0)
                break;

            _readLen += bytesRead;

            var status = TryParseProxyProtocolHeader(_readBuf.AsSpan(0, _readLen), out int consumedLength, out string forwardedAddress);
            if (status == ProxyHeaderParseStatus.Valid)
            {
                _forwardedRemoteAddress = forwardedAddress;
                _readPos = consumedLength;
                return;
            }

            if (status == ProxyHeaderParseStatus.Invalid)
            {
                _readPos = 0;
                return;
            }

            if (DateTime.UtcNow >= deadlineUtc && _tcpClient.Available <= 0)
                break;
        }

        _readPos = 0;
    }

    // Transport safety net: a single logical line (the bytes between CRLFs) longer than this gets split.
    // Classic MUD clients — notably MegaMUD — read each line into a fixed parse buffer; a line that overruns
    // it hard-freezes the client (black screen, must reload the program) WITHOUT the server noticing. A
    // door can emit such a line innocently: e.g. an evil-quest textblock whose prose paragraph is ~1530
    // chars on one line froze a 4-player party while the realm stayed up. This is a door-agnostic terminal/
    // transport concern, so it lives in the BBS host (every door is protected), at the single write
    // chokepoint. 510 is comfortably below the observed safe-vs-freeze window (a 682-char line delivered
    // fine; a 1530-char line froze) yet far above every parser-critical line (combat/stat/WHO/prompt are
    // all well under ~150), so MegaMUD's line parsing is never disturbed. The client re-wraps for display,
    // so this only governs transport safety, not appearance.
    private const int MaxTransmittedLineLength = 510;

    public Task SendAsync(string text)
    {
        if (!Connected)
            return Task.CompletedTask;

        var bytes = Cp437.GetBytes(EnforceMaxLineLength(text, MaxTransmittedLineLength));
        lock (_writeLock)
        {
            try
            {
                if (_buffering)
                {
                    _outputBuffer.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();
                }

                UpdateLineStartState(text);
            }
            catch { }
        }

        return Task.CompletedTask;
    }

    // Pre-formatted full-screen ANSI art (login screen, WCCMAP maps) sent VERBATIM — identical to SendAsync
    // except it does NOT run EnforceMaxLineLength. Art lines are intentionally wide: a centered 80-column
    // banner can exceed MaxTransmittedLineLength (510) once ANSI colour codes are counted, and the cap would
    // then jam a CRLF into the middle of that line, tearing the art (the login screen split down the middle
    // after the cap landed in 1b4ef1c). The cap stays on for normal SendAsync game text, where it protects
    // thin-buffered clients (MegaMUD) from a parse-buffer freeze.
    public Task SendAnsiArtAsync(string art)
    {
        if (!Connected || string.IsNullOrEmpty(art))
            return Task.CompletedTask;

        var bytes = Cp437.GetBytes(art);
        lock (_writeLock)
        {
            try
            {
                if (_buffering)
                {
                    _outputBuffer.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();
                }

                UpdateLineStartState(art);
            }
            catch { }
        }

        return Task.CompletedTask;
    }

    // Split any logical line longer than maxLen so no single CRLF-delimited line overruns a client's parse
    // buffer. ANSI escape sequences (ESC[...m) are treated as atomic and counted toward the length but never
    // broken across; a break prefers the last space so prose stays word-aligned, falling back to a hard cut
    // for an unbroken run. No reset is injected at a break — the active SGR colour carries to the next line,
    // matching how the rest of the output pipeline persists colour across newlines. Returns the input
    // unchanged (no allocation beyond the fast-path check) whenever nothing can exceed the cap.
    public static string EnforceMaxLineLength(string text, int maxLen)
    {
        // Fast path: if the whole payload fits, no individual line can exceed the cap.
        if (text.Length <= maxLen)
            return text;

        var sb = new StringBuilder(text.Length + 16);
        int n = text.Length;
        int lineLen = 0;            // chars emitted on the current logical line (incl. ANSI bytes)
        int lastSpaceOut = -1;      // index in sb of the last space on the current line (break candidate)
        int lineLenAtSpace = 0;     // lineLen as of that space

        for (int i = 0; i < n;)
        {
            char c = text[i];

            if (c == '\n')
            {
                sb.Append(c);
                i++;
                lineLen = 0; lastSpaceOut = -1; lineLenAtSpace = 0;
                continue;
            }

            if (c == '\x1b' && i + 1 < n && text[i + 1] == '[')
            {
                // Copy the whole CSI sequence (ESC [ ... final-byte) atomically so a break never lands inside it.
                int start = i;
                i += 2;
                while (i < n && !(text[i] >= '@' && text[i] <= '~')) i++;
                if (i < n) i++;   // include the final byte
                int len = i - start;
                sb.Append(text, start, len);
                lineLen += len;
                continue;
            }

            sb.Append(c);
            i++;
            if (c == ' ') { lastSpaceOut = sb.Length - 1; lineLenAtSpace = lineLen; }
            lineLen++;

            // Break once the line is full AND real content continues (don't append a spurious break when
            // the line ends here or a CRLF comes next).
            if (lineLen >= maxLen && i < n && text[i] != '\n' && text[i] != '\r')
            {
                if (lastSpaceOut >= 0)
                {
                    sb.Remove(lastSpaceOut, 1);
                    sb.Insert(lastSpaceOut, "\r\n");
                    lineLen -= lineLenAtSpace + 1;   // chars that moved onto the new line (after the space)
                }
                else
                {
                    sb.Append("\r\n");
                    lineLen = 0;
                }
                lastSpaceOut = -1; lineLenAtSpace = 0;
            }
        }

        return sb.ToString();
    }

    // Begin/end a held-output scope (IBbsConnection). While active and the player is mid-typing,
    // SendLineAsync routes async output (e.g. a party-follow room render driven by the leader's move)
    // into the deferred queue instead of trampling the line being composed.
    public void BeginHeldOutput() => Interlocked.Increment(ref _heldOutputDepth);
    public void EndHeldOutput() => Interlocked.Decrement(ref _heldOutputDepth);

    public async Task SendLineAsync(string text = "")
    {
        // While a held-output scope is active, defer this line if the player has typed-ahead input
        // (TryDeferBroadcastLine returns false when there's no pending input, so we fall through and
        // send directly). Keeps a leader-driven follow render from interrupting the follower's typing.
        if (Volatile.Read(ref _heldOutputDepth) > 0 && TryDeferBroadcastLine(text, reprompt: false))
            return;

        // *Combat Off* / *Combat Engaged* omit a trailing ESC[0m to stay byte-exact with stock;
        // the stock client repaints colour on the NEXT line's
        // preamble. Our raw-text lines carry no leading escape, so they inherit that leftover
        // colour and bleed (e.g. "Your command had no effect." rendered in the *Combat Off* brown).
        // Repaint any line that doesn't already begin with its own escape sequence. Megamud-parsed
        // lines (prompt, combat, room) all start with LinePreamble or a colour code, so they stay
        // byte-identical; the reset-less combat-toggle line is itself untouched because the reset
        // lands on the following line, never on the toggle line.
        if (text.Length > 0 && text[0] != '\x1b')
            text = Ansi.Reset + text;

        await SendAsync(text + "\r\n");
    }

    public void BeginBuffering()
    {
        lock (_writeLock)
        {
            _buffering = true;
            _outputBuffer.SetLength(0);
        }
    }

    public void FlushOutput()
    {
        lock (_writeLock)
        {
            try
            {
                if (_outputBuffer.Length > 0)
                {
                    _stream.Write(_outputBuffer.GetBuffer(), 0, (int)_outputBuffer.Length);
                    _stream.Flush();
                    _outputBuffer.SetLength(0);
                }
            }
            catch { }

            _buffering = false;
        }
    }

    public bool TryDeferBroadcastLine(string text, bool reprompt, bool prependLineBreak = true)
    {
        if (!Connected)
            return false;

        lock (_writeLock)
        {
            if (!HasPendingInput)
                return false;

            if (_deferredBroadcastLines.Count >= MaxDeferredBroadcastLines)
            {
                _deferredBroadcastLines.Dequeue();
                _droppedDeferredBroadcastLineCount++;
            }

            _deferredBroadcastLines.Enqueue(new DeferredBroadcastLine(text, reprompt, prependLineBreak));
            return true;
        }
    }

    public async Task FlushDeferredBroadcastLinesAsync()
    {
        if (!Connected)
            return;

        List<DeferredBroadcastLine>? lines = null;
        int droppedCount;
        lock (_writeLock)
        {
            if (_deferredBroadcastLines.Count == 0 && _droppedDeferredBroadcastLineCount == 0)
                return;

            lines = new List<DeferredBroadcastLine>(_deferredBroadcastLines.Count);
            while (_deferredBroadcastLines.Count > 0)
                lines.Add(_deferredBroadcastLines.Dequeue());

            droppedCount = _droppedDeferredBroadcastLineCount;
            _droppedDeferredBroadcastLineCount = 0;
        }

        if (droppedCount > 0)
        {
            await PrepareForBroadcastAsync();
            await SendLineAsync($"[{droppedCount} message(s) skipped while you were typing.]");
        }

        bool promptJustWritten = false;

        foreach (var line in lines)
        {
            // No break between a prompt and the line that follows it. Stock leaves the prompt sitting
            // mid-line and lets the NEXT line's own ESC[79D ESC[K walk back over it — the capture shows
            // "…]:" immediately followed by ESC[79D with no CR/LF in between. Emitting our usual
            // CRLF/clear-line here would add a byte sequence stock never sends.
            if (line.PrependLineBreak && !promptJustWritten)
                await PrepareForBroadcastAsync();

            await SendLineAsync(line.Text);
            promptJustWritten = false;

            // One prompt per LINE, not one per batch. Stock appends the prompt to every pushed line's
            // own buffer, so a flushed batch goes
            // out as line/prompt/line/prompt/… — verified byte-for-byte against a stock server while
            // holding a half-typed line through 30 combat rounds.
            //
            // Each intermediate prompt is wiped by the NEXT line's ESC[79D ESC[K before the terminal
            // repaints, so this is visually identical to a single trailing prompt — but Megamud parses
            // the byte stream, not the screen, and uses the prompt as its record boundary. Emitting one
            // prompt for the whole batch collapsed N state transitions into one, which is what left it
            // frozen after a deferred combat batch (a *Combat Off* buried mid-batch never got a
            // boundary of its own).
            //
            // The door owns the prompt; the host re-emits it opaquely. An empty/absent prompt means the
            // door asked for no reprompt (no active player, or SuppressBroadcastReprompt).
            if (!line.Reprompt)
                continue;

            string? linePrompt = PromptProvider?.Invoke();
            if (string.IsNullOrEmpty(linePrompt))
                continue;

            await SendAsync(linePrompt);
            promptJustWritten = true;
        }
    }

    private static readonly byte[] CrLfSeq = { 13, 10 };
    private static readonly byte[] ClearLineSeq = { 13, 0x1b, (byte)'[', (byte)'2', (byte)'K' };
    private static readonly byte[] BackspaceSeq = { 8, 32, 8 };

    public Task EnsureNewLineAsync()
    {
        if (!Connected)
            return Task.CompletedTask;

        lock (_writeLock)
        {
            try
            {
                if (!_isAtLineStart)
                {
                    if (_buffering)
                        _outputBuffer.Write(CrLfSeq, 0, CrLfSeq.Length);
                    else
                    {
                        _stream.Write(CrLfSeq, 0, CrLfSeq.Length);
                        _stream.Flush();
                    }

                    _isAtLineStart = true;
                }
            }
            catch { }
        }

        return Task.CompletedTask;
    }

    public Task PrepareForBroadcastAsync(bool forceClearCurrentLine = false)
    {
        if (!Connected)
            return Task.CompletedTask;

        lock (_writeLock)
        {
            try
            {
                if (_isAtLineStart)
                    return Task.CompletedTask;

                byte[] sequence = (forceClearCurrentLine || !HasPendingInput) ? ClearLineSeq : CrLfSeq;
                if (_buffering)
                    _outputBuffer.Write(sequence, 0, sequence.Length);
                else
                {
                    _stream.Write(sequence, 0, sequence.Length);
                    _stream.Flush();
                }

                _isAtLineStart = true;
            }
            catch { }
        }

        return Task.CompletedTask;
    }

    public Task RedrawPromptWithInputAsync(string prompt)
    {
        if (!Connected || string.IsNullOrEmpty(prompt))
            return Task.CompletedTask;

        lock (_writeLock)
        {
            try
            {
                // A server-pushed prompt refresh (rest/meditate HP-mana climb, room-spell pulse, poison
                // tick) can land while the player is mid-command. Rewrite the WHOLE line in place — CR +
                // erase-to-end-of-line, the fresh prompt, then the player's still-unsent input restored
                // after it — so the refresh never appends a second prompt inline or visually eats a
                // half-typed command. The game read loop (ReadLineAsync) doesn't server-echo, so the
                // typed text only lives client-side + in _inputBuffer; re-emitting _inputBuffer after the
                // clear reconstructs exactly what the player sees. _inputBuffer stays the source of truth,
                // so a keystroke racing this redraw is at worst a transient visual glitch, never a lost
                // or duplicated command.
                string payload = "\r\x1b[2K" + prompt + _inputBuffer.ToString();
                var bytes = Cp437.GetBytes(payload);
                if (_buffering)
                    _outputBuffer.Write(bytes, 0, bytes.Length);
                else
                {
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();
                }

                UpdateLineStartState(payload);
            }
            catch { }
        }

        return Task.CompletedTask;
    }

    public Task ClearCurrentLineAsync()
    {
        if (!Connected)
            return Task.CompletedTask;

        lock (_writeLock)
        {
            try
            {
                if (!_isAtLineStart)
                {
                    if (_buffering)
                        _outputBuffer.Write(ClearLineSeq, 0, ClearLineSeq.Length);
                    else
                    {
                        _stream.Write(ClearLineSeq, 0, ClearLineSeq.Length);
                        _stream.Flush();
                    }

                    _isAtLineStart = true;
                }
            }
            catch { }
        }

        return Task.CompletedTask;
    }

    public async Task DiscardBufferedLineEndingsAsync(CancellationToken ct = default)
    {
        bool consumedAny = false;
        DateTime settleUntilUtc = DateTime.UtcNow;
        DateTime initialWaitUntilUtc = DateTime.UtcNow.AddMilliseconds(125);

        while (!ct.IsCancellationRequested)
        {
            if (!TryPeekImmediateByte(out byte b, out bool fromBuffer))
            {
                if (!consumedAny)
                {
                    if (DateTime.UtcNow >= initialWaitUntilUtc)
                        break;
                }
                else if (DateTime.UtcNow >= settleUntilUtc)
                {
                    break;
                }

                try
                {
                    await Task.Delay(5, ct);
                }
                catch
                {
                    break;
                }

                continue;
            }

            // If a prior line's terminator is still pending and its companion byte is here, drop it.
            if (_lastLineTerminator != 0 && IsCompanionTerminator(_lastLineTerminator, b))
            {
                ConsumeImmediateByte(fromBuffer);
                _lastLineTerminator = 0;
                consumedAny = true;
                settleUntilUtc = DateTime.UtcNow.AddMilliseconds(75);
                continue;
            }

            // Otherwise do NOT forget a pending terminator here. An interleaved telnet/control byte
            // (e.g. CR <IAC ...> LF) can sit before the companion; clearing now would let that late LF
            // be read as an empty line (e.g. an empty password). Leave it for the next read to resolve.
            if (b != '\r' && b != '\n' && b != 0)
                break;

            ConsumeImmediateByte(fromBuffer);
            consumedAny = true;
            settleUntilUtc = DateTime.UtcNow.AddMilliseconds(75);

            if (b == '\r' || b == '\n')
                _lastLineTerminator = b;
        }

        if (consumedAny)
        {
            _inputBuffer.Clear();
            Volatile.Write(ref _hasPendingInput, 0);
        }
    }

    private void EchoByte(byte b)
    {
        lock (_writeLock)
        {
            try
            {
                if (_buffering)
                    _outputBuffer.WriteByte(b);
                else
                {
                    _stream.WriteByte(b);
                    _stream.Flush();
                }

                _isAtLineStart = false;
            }
            catch { }
        }
    }

    private void EchoBackspace()
    {
        lock (_writeLock)
        {
            try
            {
                if (_buffering)
                    _outputBuffer.Write(BackspaceSeq, 0, BackspaceSeq.Length);
                else
                {
                    _stream.Write(BackspaceSeq, 0, BackspaceSeq.Length);
                    _stream.Flush();
                }
            }
            catch { }
        }
    }

    private void EchoCrLf()
    {
        lock (_writeLock)
        {
            try
            {
                if (_buffering)
                    _outputBuffer.Write(CrLfSeq, 0, CrLfSeq.Length);
                else
                {
                    _stream.Write(CrLfSeq, 0, CrLfSeq.Length);
                    _stream.Flush();
                }

                _isAtLineStart = true;
            }
            catch { }
        }
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken ct)
    {
        if (_readPos >= _readLen)
        {
            try
            {
                _readLen = await _stream.ReadAsync(_readBuf.AsMemory(0, _readBuf.Length), ct);
            }
            catch
            {
                return -1;
            }

            _readPos = 0;
            if (_readLen == 0)
                return -1;
        }

        return _readBuf[_readPos++];
    }

    private bool TryPeekImmediateByte(out byte b, out bool fromBuffer)
    {
        if (HasBufferedData)
        {
            b = _readBuf[_readPos];
            fromBuffer = true;
            return true;
        }

        try
        {
            if (_tcpClient.Available <= 0)
            {
                b = 0;
                fromBuffer = false;
                return false;
            }

            byte[] singleByte = new byte[1];
            int received = _tcpClient.Client.Receive(singleByte, 0, 1, SocketFlags.Peek);
            if (received <= 0)
            {
                b = 0;
                fromBuffer = false;
                return false;
            }

            b = singleByte[0];
            fromBuffer = false;
            return true;
        }
        catch
        {
            b = 0;
            fromBuffer = false;
            return false;
        }
    }

    private void ConsumeImmediateByte(bool fromBuffer)
    {
        if (fromBuffer)
        {
            _readPos++;
            return;
        }

        try
        {
            byte[] singleByte = new byte[1];
            _tcpClient.Client.Receive(singleByte, 0, 1, SocketFlags.None);
        }
        catch
        {
        }
    }

    private bool HasBufferedData => _readPos < _readLen;

    private bool HasReadableSocketState()
    {
        if (HasBufferedData)
            return true;

        try
        {
            return _tcpClient.Client.Poll(0, SelectMode.SelectRead);
        }
        catch
        {
            return true;
        }
    }

    // True when `next` is the companion byte that completes a two-byte line
    // terminator begun by `terminator`: CR->LF, LF->CR, or CR/LF->NUL (telnet CR NUL).
    // It is deliberately NOT the same terminator repeated, so a genuine double Enter
    // (\r\r or \n\n) still counts as two separate blank lines.
    private static bool IsCompanionTerminator(byte terminator, byte next)
    {
        if (next == 0)
            return true;

        return terminator == '\r' ? next == '\n' : next == '\r';
    }

    private bool ShouldDiscardBufferedLineEndingByte(byte b)
    {
        if (_lastLineTerminator == 0)
            return false;

        bool discard = IsCompanionTerminator(_lastLineTerminator, b);
        _lastLineTerminator = 0;
        return discard;
    }

    // Called immediately after a line is emitted on a CR or LF. If the companion byte
    // is already buffered it is consumed now; otherwise we remember the terminator so a
    // companion arriving in a later read is discarded rather than read as a blank line.
    private void AbsorbLineTerminatorCompanion(byte terminator)
    {
        if (HasBufferedData && IsCompanionTerminator(terminator, _readBuf[_readPos]))
        {
            _readPos++;
            _lastLineTerminator = 0;
            return;
        }

        _lastLineTerminator = terminator;
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct = default)
    {
        _inputBuffer.Clear();
        Volatile.Write(ref _hasPendingInput, 0);

        while (!ct.IsCancellationRequested)
        {
            int raw = await ReadByteAsync(ct);
            if (raw < 0)
                return null;

            byte b = (byte)raw;
            if (b == IAC)
            {
                await SkipTelnetCommand(ct);
                continue;
            }

            if (ShouldDiscardBufferedLineEndingByte(b))
                continue;

            if (b == '\r' || b == '\n')
            {
                AbsorbLineTerminatorCompanion(b);
                Volatile.Write(ref _hasPendingInput, 0);
                var line = SanitizeInput(_inputBuffer.ToString());
                _inputBuffer.Clear();
                return line;
            }

            if (b == 8 || b == 127)
            {
                bool hadPendingInput = _inputBuffer.Length > 0;
                if (_inputBuffer.Length > 0)
                {
                    _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                    Volatile.Write(ref _hasPendingInput, _inputBuffer.Length > 0 ? 1 : 0);
                }

                await FlushDeferredBroadcastLinesIfInputClearedAsync(hadPendingInput);

                continue;
            }

            if (b >= 32 && b < 127)
            {
                if (_inputBuffer.Length < MaxInputLength)
                {
                    _inputBuffer.Append((char)b);
                    Volatile.Write(ref _hasPendingInput, 1);
                }
            }
        }

        return null;
    }

    public async Task<string?> ReadLineEchoAsync(bool echo = true, CancellationToken ct = default)
    {
        _inputBuffer.Clear();
        Volatile.Write(ref _hasPendingInput, 0);
        ResetCommandHistoryNavigation();

        while (!ct.IsCancellationRequested)
        {
            int raw = await ReadByteAsync(ct);
            if (raw < 0)
                return null;

            byte b = (byte)raw;
            if (b == IAC)
            {
                await SkipTelnetCommand(ct);
                continue;
            }

            if (ShouldDiscardBufferedLineEndingByte(b))
                continue;

            if (b == 27)
            {
                await HandleReadLineEscapeSequenceAsync(echo, ct);
                continue;
            }

            if (b == '\r' || b == '\n')
            {
                AbsorbLineTerminatorCompanion(b);
                if (echo)
                    EchoCrLf();
                return CompleteBufferedLine();
            }

            if (b == 8 || b == 127)
            {
                bool hadPendingInput = _inputBuffer.Length > 0;
                if (_commandHistoryBrowseIndex >= 0)
                    ResetCommandHistoryNavigation();

                if (_inputBuffer.Length > 0)
                {
                    _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                    Volatile.Write(ref _hasPendingInput, _inputBuffer.Length > 0 ? 1 : 0);
                    if (echo)
                        EchoBackspace();
                }

                await FlushDeferredBroadcastLinesIfInputClearedAsync(hadPendingInput);

                continue;
            }

            if (b >= 32 && b < 127)
            {
                if (_commandHistoryBrowseIndex >= 0)
                    ResetCommandHistoryNavigation();

                if (_inputBuffer.Length < MaxInputLength)
                {
                    _inputBuffer.Append((char)b);
                    Volatile.Write(ref _hasPendingInput, 1);
                    if (echo)
                        EchoByte(b);
                }
            }
        }

        return null;
    }

    public async Task<string?> ReadLineEchoAsync(int timeoutMs, bool echo = true, CancellationToken ct = default)
    {
        if (timeoutMs <= 0)
            return await ReadLineEchoAsync(echo, ct);

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (!ct.IsCancellationRequested)
        {
            bool hasData = HasReadableSocketState();
            if (!hasData)
            {
                int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0)
                    return GameTransportConstants.ReadTimeoutSentinel;

                int delayMs = Math.Min(25, remainingMs);
                try
                {
                    await Task.Delay(delayMs, ct);
                }
                catch
                {
                    return null;
                }

                continue;
            }

            int raw = await ReadByteAsync(ct);
            if (raw < 0)
                return null;

            byte b = (byte)raw;
            if (b == IAC)
            {
                await SkipTelnetCommand(ct);
                continue;
            }

            if (ShouldDiscardBufferedLineEndingByte(b))
                continue;

            if (b == 27)
            {
                await HandleReadLineEscapeSequenceAsync(echo, ct);
                continue;
            }

            if (b == '\r' || b == '\n')
            {
                AbsorbLineTerminatorCompanion(b);
                if (echo)
                    EchoCrLf();
                return CompleteBufferedLine();
            }

            if (b == 8 || b == 127)
            {
                bool hadPendingInput = _inputBuffer.Length > 0;
                if (_commandHistoryBrowseIndex >= 0)
                    ResetCommandHistoryNavigation();

                if (_inputBuffer.Length > 0)
                {
                    _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                    Volatile.Write(ref _hasPendingInput, _inputBuffer.Length > 0 ? 1 : 0);
                    if (echo)
                        EchoBackspace();
                }

                await FlushDeferredBroadcastLinesIfInputClearedAsync(hadPendingInput);

                continue;
            }

            if (b >= 32 && b < 127)
            {
                if (_commandHistoryBrowseIndex >= 0)
                    ResetCommandHistoryNavigation();

                if (_inputBuffer.Length < MaxInputLength)
                {
                    _inputBuffer.Append((char)b);
                    Volatile.Write(ref _hasPendingInput, 1);
                    if (echo)
                        EchoByte(b);
                }
            }
        }

        return null;
    }

    public async Task<string?> ReadLineMaskedAsync(char mask = '*', CancellationToken ct = default)
    {
        _inputBuffer.Clear();
        Volatile.Write(ref _hasPendingInput, 0);
        ResetCommandHistoryNavigation();

        byte maskByte = mask is >= ' ' and < ''
            ? (byte)mask
            : (byte)'*';

        while (!ct.IsCancellationRequested)
        {
            int raw = await ReadByteAsync(ct);
            if (raw < 0)
                return null;

            byte b = (byte)raw;
            if (b == IAC)
            {
                await SkipTelnetCommand(ct);
                continue;
            }

            if (ShouldDiscardBufferedLineEndingByte(b))
                continue;

            if (b == 27)
            {
                await HandleReadLineEscapeSequenceAsync(false, ct);
                continue;
            }

            if (b == '\r' || b == '\n')
            {
                AbsorbLineTerminatorCompanion(b);
                EchoCrLf();
                return CompleteBufferedLine(rememberHistory: false);
            }

            if (b == 8 || b == 127)
            {
                bool hadPendingInput = _inputBuffer.Length > 0;
                if (_commandHistoryBrowseIndex >= 0)
                    ResetCommandHistoryNavigation();

                if (_inputBuffer.Length > 0)
                {
                    _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                    Volatile.Write(ref _hasPendingInput, _inputBuffer.Length > 0 ? 1 : 0);
                    EchoBackspace();
                }

                await FlushDeferredBroadcastLinesIfInputClearedAsync(hadPendingInput);

                continue;
            }

            if (b >= 32 && b < 127)
            {
                if (_commandHistoryBrowseIndex >= 0)
                    ResetCommandHistoryNavigation();

                if (_inputBuffer.Length < MaxInputLength)
                {
                    _inputBuffer.Append((char)b);
                    Volatile.Write(ref _hasPendingInput, 1);
                    EchoByte(maskByte);
                }
            }
        }

        return null;
    }

    public async Task<string?> ReadKeyAsync(CancellationToken ct = default)
    {
        return await ReadKeyCoreAsync(ct);
    }

    public async Task<string?> ReadKeyAsync(int timeoutMs, CancellationToken ct = default)
    {
        if (timeoutMs <= 0)
            return await ReadKeyAsync(ct);

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!ct.IsCancellationRequested)
        {
            if (HasReadableSocketState())
                return await ReadKeyCoreAsync(ct);

            int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remainingMs <= 0)
                return "Timeout";

            int delayMs = Math.Min(25, remainingMs);
            try
            {
                await Task.Delay(delayMs, ct);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private async Task<string?> ReadKeyCoreAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int raw = await ReadByteAsync(ct);
            if (raw < 0)
                return null;

            byte b = (byte)raw;
            if (b == IAC)
            {
                await SkipTelnetCommand(ct);
                continue;
            }

            if (ShouldDiscardBufferedLineEndingByte(b))
                continue;

            if (b == 27)
                return await ReadEscapeSequenceTokenAsync(ct);

            if (b == '\r' || b == '\n')
            {
                AbsorbLineTerminatorCompanion(b);
                return "Enter";
            }

            if (b == ' ')
                return "Space";
            if (b == 21)
                return "CtrlU";
            if (b == 8 || b == 127)
                return "Backspace";
            if (b >= 32 && b < 127)
                return ((char)b).ToString();
        }

        return null;
    }

    public void SetCommandHistoryEnabled(bool enabled)
    {
        _commandHistoryEnabled = enabled;
        if (!enabled)
            ResetCommandHistoryNavigation();
    }

    public void SetEcho(bool enabled)
    {
        if (!enabled)
            SendRawAsync([IAC, WILL, ECHO]).Wait();
    }

    public bool DisconnectedByHost { get; private set; }

    /// <summary>Why the BOARD closed this connection, or null when the peer is the one that left. Read
    /// during teardown so every disconnect can be logged with a cause instead of a bare "disconnected".</summary>
    public string? HostDisconnectReason { get; private set; }

    public void Disconnect()
    {
        try { _tcpClient.Close(); } catch { }
    }

    /// <summary>
    /// Close this connection on the BOARD's initiative — a duplicate login replacing it, a sysop kick, a
    /// shutdown. Identical to <see cref="Disconnect"/> except that it records WHY, so the mounted door can
    /// tell an administrative close apart from the peer vanishing. Set before closing, because closing is
    /// what unblocks the owning session task that will read the flag.
    /// </summary>
    public void DisconnectByHost(string? reason = null)
    {
        DisconnectedByHost = true;
        HostDisconnectReason = reason;
        Disconnect();
    }

    /// <summary>
    /// Last words on a connection the board is about to close, so the player learns WHY their client went
    /// dead instead of guessing at a network fault. Best-effort and time-boxed: if this connection is
    /// mid-proxy the line can interleave with in-flight game output, which is a cost worth paying on a
    /// socket that closes microseconds later — the alternative is a player staring at a dead window.
    /// </summary>
    public void TrySendFarewellLine(string text)
    {
        try { SendLineAsync(text).Wait(TimeSpan.FromMilliseconds(250)); }
        catch { /* peer may already be gone; the close is what matters */ }
    }

    /// <summary>
    /// Backend side: read the single raw handshake LINE the front sends first (before any telnet
    /// negotiation), byte-by-byte up to '\n', so we never over-read into the client's telnet stream that
    /// the front starts forwarding immediately after. Returns null on EOF or if the line exceeds
    /// <paramref name="maxLength"/> (a malformed/hostile peer).
    /// </summary>
    public async Task<string?> ReadInternalHandshakeLineAsync(int maxLength, CancellationToken ct)
    {
        var sb = new StringBuilder(64);
        var one = new byte[1];
        while (sb.Length <= maxLength)
        {
            int n = await _stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0)
                return null; // EOF before newline
            char c = (char)one[0];
            if (c == '\n')
                return sb.ToString();
            if (c != '\r')
                sb.Append(c);
        }

        return null; // over-length: treat as malformed
    }

    /// <summary>
    /// Front side: become a transparent byte pipe between this client connection and a realm
    /// <paramref name="backend"/>. Forwards any bytes already buffered from the client, then pumps both
    /// directions until either leg closes (a hangup / <c>=x</c> on the backend, or the client dropping),
    /// then tears both down. The front stops interpreting the stream entirely, so the backend's telnet
    /// negotiation and game I/O reach the real client directly.
    /// </summary>
    // True while this client is a transparent byte-pipe to a realm backend. A board-wide announce must NOT
    // be written to such a client (it would corrupt the game byte stream); its in-realm players receive the
    // notice through their realm's own broadcast instead. Only menu/login/realm-select clients get the fan-out.
    private volatile bool _isProxying;
    public bool IsProxying => _isProxying;

    public async Task RunRawProxyAsync(TcpClient backend, CancellationToken ct)
    {
        _isProxying = true;
        try
        {
            await RunRawProxyCoreAsync(backend, ct);
        }
        finally
        {
            _isProxying = false;
        }
    }

    private async Task RunRawProxyCoreAsync(TcpClient backend, CancellationToken ct)
    {
        var backendStream = backend.GetStream();

        // Hand the backend any client bytes we read but did not consume (rare trailing bytes after the
        // menu line) before live traffic starts.
        if (_readPos < _readLen)
        {
            await backendStream.WriteAsync(_readBuf.AsMemory(_readPos, _readLen - _readPos), ct);
            await backendStream.FlushAsync(ct);
        }
        _readPos = _readLen = 0;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task clientToBackend = PumpFromClientAsync(backendStream, linked.Token);
        Task<bool> backendToClient = PumpAsync(backendStream, _stream, linked.Token);

        var firstFinished = await Task.WhenAny(clientToBackend, backendToClient);

        // Which SIDE left decides whether the player keeps their connection. The backend leg ends for two
        // very different reasons and only one of them is the backend's doing: reading EOF from the backend
        // is the realm handing control back, but a failed WRITE to the client means the CLIENT is the one
        // that went away (a dead peer surfaces on the send side first — see TCP_USER_TIMEOUT above). Taking
        // "the backend leg finished" to mean "the backend hung up" left a vanished player's socket alive
        // and returned ReturnedToBbs, so the front then talked to a corpse instead of dropping them.
        bool backendClosedFirst = ReferenceEquals(firstFinished, backendToClient) && backendToClient.Result;

        // One leg ended; unblock the other. The BACKEND socket always goes, but the CLIENT socket is
        // dropped only when the CLIENT is the side that left. A backend closing on its own is the normal
        // way a realm hands control back to the front (reroll, death on the last life, QUIT): the player
        // is still on the board and must land on the BBS menu, so tearing their socket down here would
        // log them off the whole BBS instead.
        linked.Cancel();
        try { backend.Client.Shutdown(SocketShutdown.Both); } catch { }
        if (!backendClosedFirst)
            try { _tcpClient.Client.Shutdown(SocketShutdown.Both); } catch { }

        // Both pumps must be finished before the caller reads from the client stream again — a surviving
        // client->backend pump would swallow the keystrokes the BBS menu is waiting on.
        try { await Task.WhenAll(clientToBackend, backendToClient); } catch { }
    }

    // The client leg polls rather than parking in ReadAsync. A socket receive cannot be cancelled once it
    // is in flight (the token is only observed before the operation starts), so a pump blocked on the
    // player's next keystroke would outlive the proxy and still own the client stream when the front
    // returns to its menu — stealing the very input the menu is waiting on. Polling keeps each read
    // short-lived so cancellation takes effect promptly and the stream is handed back cleanly.
    private async Task PumpFromClientAsync(Stream to, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var socket = _tcpClient.Client;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Non-blocking probe, then yield. Poll(0) never parks the thread and Task.Delay is
                // cancellable, so the loop stays fully async and unwinds as soon as the proxy ends.
                if (!socket.Poll(0, SelectMode.SelectRead))
                {
                    await Task.Delay(25, ct);
                    continue;
                }

                // Readable with nothing buffered means the peer closed.
                if (socket.Available == 0)
                    break;

                int read = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0)
                    break;

                await to.WriteAsync(buffer.AsMemory(0, read), ct);
                await to.FlushAsync(ct);
            }
        }
        catch
        {
            // Peer closed / reset / cancelled — normal end of a proxied leg.
        }
    }

    // Returns true when the SOURCE side ended the leg (clean EOF from `from`), false when the leg died on
    // the destination — a write/flush that threw, i.e. the far end went away. The caller needs the
    // difference: source-EOF on the backend leg is a realm handing control back to the front, while a
    // failed write to the client is a dropped player.
    private static async Task<bool> PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await from.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                }
                catch
                {
                    return true;   // source faulted — treat as the source ending the leg
                }

                if (read <= 0)
                    return true;   // clean EOF from the source

                await to.WriteAsync(buffer.AsMemory(0, read), ct);
                await to.FlushAsync(ct);
            }
        }
        catch
        {
            // The destination write failed: that end is gone.
            return false;
        }
    }

    private string CompleteBufferedLine(bool rememberHistory = true)
    {
        string line = SanitizeInput(_inputBuffer.ToString());
        if (rememberHistory)
            RememberCommandHistoryEntry(line);
        _inputBuffer.Clear();
        Volatile.Write(ref _hasPendingInput, 0);
        ResetCommandHistoryNavigation();
        return line;
    }

    private async Task FlushDeferredBroadcastLinesIfInputClearedAsync(bool hadPendingInput)
    {
        if (!hadPendingInput || _inputBuffer.Length != 0)
            return;

        await FlushDeferredBroadcastLinesAsync();
    }

    private void RememberCommandHistoryEntry(string line)
    {
        if (!_commandHistoryEnabled || string.IsNullOrWhiteSpace(line))
            return;

        _commandHistory.Add(line);
        if (_commandHistory.Count > MaxCommandHistoryEntries)
            _commandHistory.RemoveAt(0);
    }

    private void ResetCommandHistoryNavigation()
    {
        _commandHistoryBrowseIndex = -1;
        _commandHistoryDraft = string.Empty;
    }

    private async Task HandleReadLineEscapeSequenceAsync(bool echo, CancellationToken ct)
    {
        string token = await ReadEscapeSequenceTokenAsync(ct);
        switch (token)
        {
            case "Up":
                NavigateCommandHistory(-1, echo);
                break;
            case "Down":
                NavigateCommandHistory(1, echo);
                break;
        }
    }

    private void NavigateCommandHistory(int direction, bool echo)
    {
        if (!_commandHistoryEnabled || _commandHistory.Count == 0)
            return;

        if (_commandHistoryBrowseIndex < 0)
        {
            _commandHistoryDraft = _inputBuffer.ToString();
            _commandHistoryBrowseIndex = _commandHistory.Count;
        }

        int nextIndex = Math.Clamp(_commandHistoryBrowseIndex + direction, 0, _commandHistory.Count);
        if (nextIndex == _commandHistoryBrowseIndex)
            return;

        _commandHistoryBrowseIndex = nextIndex;
        string replacement = _commandHistoryBrowseIndex == _commandHistory.Count
            ? _commandHistoryDraft
            : _commandHistory[_commandHistoryBrowseIndex];

        ReplaceCurrentInputBuffer(replacement, echo);
    }

    private void ReplaceCurrentInputBuffer(string replacement, bool echo)
    {
        string previous = _inputBuffer.ToString();
        _inputBuffer.Clear();
        _inputBuffer.Append(replacement);
        Volatile.Write(ref _hasPendingInput, _inputBuffer.Length > 0 ? 1 : 0);

        if (!echo)
            return;

        for (int i = 0; i < previous.Length; i++)
            EchoBackspace();

        for (int i = 0; i < replacement.Length; i++)
            EchoByte((byte)replacement[i]);
    }

    private async Task<string> ReadEscapeSequenceTokenAsync(CancellationToken ct)
    {
        int raw = await ReadByteAsync(ct);
        if (raw < 0)
            return "Escape";

        if ((byte)raw == '[')
        {
            raw = await ReadByteAsync(ct);
            if (raw < 0)
                return "Escape";

            if ((byte)raw == (byte)'3')
            {
                int tilde = await ReadByteAsync(ct);
                if (tilde == (byte)'~')
                    return "Delete";

                return "Escape";
            }

            return (byte)raw switch
            {
                (byte)'A' => "Up",
                (byte)'B' => "Down",
                (byte)'C' => "Right",
                (byte)'D' => "Left",
                _ => "Escape",
            };
        }

        return "Escape";
    }

    private async Task SkipTelnetCommand(CancellationToken ct)
    {
        int raw = await ReadByteAsync(ct);
        if (raw < 0)
            return;

        byte command = (byte)raw;
        if (command == SB)
        {
            while (true)
            {
                raw = await ReadByteAsync(ct);
                if (raw < 0)
                    return;
                if ((byte)raw == IAC)
                {
                    raw = await ReadByteAsync(ct);
                    if (raw < 0)
                        return;
                    if ((byte)raw == SE)
                        break;
                }
            }
        }
        else if (command >= WILL && command <= DONT)
        {
            int optionRaw = await ReadByteAsync(ct);
            if (optionRaw < 0)
                return;

            byte option = (byte)optionRaw;
            if (command == WILL && option == LINEMODE)
            {
                await SendRawAsync([IAC, DONT, LINEMODE]);
            }
            else if (command == DO && option == LINEMODE)
            {
                await SendRawAsync([IAC, WONT, LINEMODE]);
            }
            else if (option == GMCP)
            {
                // The client accepted (DO) or declined (DONT) our GMCP offer. Latch it;
                // SendGmcpAsync stays silent until DO arrives, so non-GMCP clients never
                // see a single subnegotiation byte.
                _gmcpEnabled = command == DO;
            }
        }
    }

    private void UpdateLineStartState(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\n')
                _isAtLineStart = true;
            else if (ch != '\r')
                _isAtLineStart = false;
        }
    }

    private Task SendRawAsync(byte[] data)
    {
        if (!Connected)
            return Task.CompletedTask;

        lock (_writeLock)
        {
            try
            {
                _stream.Write(data, 0, data.Length);
                _stream.Flush();
            }
            catch { }
        }

        return Task.CompletedTask;
    }

    public Task SendGmcpAsync(string package, string payloadJson)
    {
        if (!_gmcpEnabled || !Connected)
            return Task.CompletedTask;

        // Wire form: IAC SB GMCP "Package.Name {json}" IAC SE. The payload is ASCII
        // (GMCP package names and JSON), and any 0xFF byte inside it must be doubled so
        // it is not mistaken for an IAC command.
        string message = string.IsNullOrEmpty(payloadJson) ? package : $"{package} {payloadJson}";
        byte[] body = Encoding.ASCII.GetBytes(message);

        var frame = new List<byte>(body.Length + 8) { IAC, SB, GMCP };
        foreach (byte b in body)
        {
            frame.Add(b);
            if (b == IAC)
                frame.Add(IAC);
        }
        frame.Add(IAC);
        frame.Add(SE);

        return SendRawAsync(frame.ToArray());
    }

    private static ProxyHeaderParseStatus TryParseProxyProtocolHeader(ReadOnlySpan<byte> buffer, out int consumedLength, out string forwardedAddress)
    {
        consumedLength = 0;
        forwardedAddress = string.Empty;

        ReadOnlySpan<byte> prefix = "PROXY "u8;
        if (buffer.Length < prefix.Length)
            return prefix[..buffer.Length].SequenceEqual(buffer) ? ProxyHeaderParseStatus.NeedMoreData : ProxyHeaderParseStatus.Invalid;

        if (!buffer[..prefix.Length].SequenceEqual(prefix))
            return ProxyHeaderParseStatus.Invalid;

        int lineEndIndex = -1;
        for (int index = 0; index < buffer.Length - 1; index++)
        {
            if (buffer[index] == '\r' && buffer[index + 1] == '\n')
            {
                lineEndIndex = index;
                break;
            }
        }

        if (lineEndIndex < 0)
            return buffer.Length >= 108 ? ProxyHeaderParseStatus.Invalid : ProxyHeaderParseStatus.NeedMoreData;

        consumedLength = lineEndIndex + 2;
        string header = Encoding.ASCII.GetString(buffer[..lineEndIndex]);
        string[] parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 2 && parts[1].Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            return ProxyHeaderParseStatus.Valid;

        if (parts.Length >= 6 && parts[1].StartsWith("TCP", StringComparison.OrdinalIgnoreCase) && IPAddress.TryParse(parts[2], out _))
            forwardedAddress = parts[2];

        return ProxyHeaderParseStatus.Valid;
    }

    private enum ProxyHeaderParseStatus
    {
        NeedMoreData,
        Valid,
        Invalid,
    }

    private static string SanitizeInput(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var cleanChars = input.Where(ch => ch >= 32 && ch < 127).ToArray();
        var clean = new string(cleanChars);
        if (clean.Length > MaxInputLength)
            clean = clean[..MaxInputLength];
        return clean;
    }
}
