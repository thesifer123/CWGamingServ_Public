using System.Net;
using System.Net.Sockets;
using System.Text;
using CWGamingServ;

// Reproduction harness for the BBS login input parser.
//
// Drives the REAL production TelnetClient parser (linked from ../../TelnetClient.cs)
// over a loopback socket, feeding the byte sequences that different telnet clients
// send for Enter, then mimicking the login read pattern:
//
//     loop { name = ReadLineEchoAsync(echo: true); if (empty) continue; }
//
// For each scenario we print every line the parser yields, so phantom blank lines
// or off-by-one buffering show up plainly.

Console.OutputEncoding = Encoding.UTF8;

static string Show(string? s) => s switch
{
    null => "<null>",
    "" => "<empty>",
    _ => "\"" + s + "\"",
};

static byte[] Bytes(string s)
{
    var b = new byte[s.Length];
    for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
    return b;
}

// Enter encodings sent by real clients.
const string CRLF = "\r\n";   // PuTTY default, most clients
const string CRNUL = "\r\0";  // RFC 854 "bare CR", Windows telnet.exe in NVT mode
const string CR = "\r";       // some raw clients
const string LF = "\n";       // netcat / unix raw

// Each scenario is a list of byte chunks sent with a small delay between them,
// so we can model a client that splits the line terminator into its own packet.
var scenarios = new (string Name, string[] Chunks, bool NegotiateFirst)[]
{
    ("CRLF together",            new[] { "alice" + CRLF, "bob" + CRLF }, false),
    ("content then CRLF split",  new[] { "alice", CRLF, "bob", CRLF }, false),
    ("CR split from LF",         new[] { "alice" + CR, LF, "bob" + CR, LF }, false),
    ("char-at-a-time + CRLF",    new[] { "a","l","i","c","e", CR, LF, "b","o","b", CR, LF }, false),
    ("char-at-a-time + CRNUL",   new[] { "a","l","i","c","e", CR, "\0", "b","o","b", CR, "\0" }, false),
    ("LF before CR (\\n\\r)",    new[] { "alice" + LF + CR, "bob" + LF + CR }, false),
    ("LFCR split",               new[] { "alice" + LF, CR, "bob" + LF, CR }, false),
    ("double Enter CRLF",        new[] { "alice" + CRLF, CRLF, "bob" + CRLF }, false),
    ("negotiation + CRLF split", new[] { "alice", CRLF, "bob", CRLF }, true),
};

foreach (var sc in scenarios)
{
    await RunScenarioAsync(sc.Name, sc.Chunks.Select(Bytes).ToArray(), sc.NegotiateFirst);
}

static async Task RunScenarioAsync(string name, byte[][] chunks, bool negotiateFirst)
{
    Console.WriteLine($"=== {name} ===");

    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;

    using var terminal = new TcpClient();
    await terminal.ConnectAsync(IPAddress.Loopback, port);
    using var serverSocket = await listener.AcceptTcpClientAsync();
    listener.Stop();

    var client = new TelnetClient(serverSocket);

    // Drain whatever the server writes back (negotiation + echo) so writes never block.
    var terminalStream = terminal.GetStream();
    var drain = Task.Run(async () =>
    {
        var buf = new byte[1024];
        try { while (await terminalStream.ReadAsync(buf) > 0) { } } catch { }
    });

    if (negotiateFirst)
    {
        await client.NegotiateAsync();
        // Realistic client reply to our options + a NAWS window-size subnegotiation.
        var reply = new byte[]
        {
            255, 253, 0,            // IAC DO BINARY
            255, 251, 0,            // IAC WILL BINARY
            255, 253, 1,            // IAC DO ECHO
            255, 253, 3,            // IAC DO SGA
            255, 251, 3,            // IAC WILL SGA
            255, 251, 31,           // IAC WILL NAWS
            255, 250, 31, 0, 80, 0, 24, 255, 240, // IAC SB NAWS 0 80 0 24 IAC SE
        };
        await terminalStream.WriteAsync(reply);
        await terminalStream.FlushAsync();
    }

    // Send each chunk as its own packet with a gap, so a split line terminator
    // really arrives in a separate ReadAsync (mimicking a char-at-a-time client).
    var writer = Task.Run(async () =>
    {
        foreach (var chunk in chunks)
        {
            await terminalStream.WriteAsync(chunk);
            await terminalStream.FlushAsync();
            await Task.Delay(60);
        }
    });

    // Mimic the login read loop: read lines until input stalls.
    for (int i = 0; i < 6; i++)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        string? line;
        try { line = await client.ReadLineEchoAsync(echo: true, cts.Token); }
        catch (OperationCanceledException) { line = "<canceled>"; }

        if (line == null)
        {
            Console.WriteLine($"  read[{i}] => <null/stall>");
            break;
        }

        Console.WriteLine($"  read[{i}] => {Show(line)}{(string.IsNullOrEmpty(line) ? "   <-- empty: login loop would silently re-prompt" : "")}");
    }

    try { await writer; } catch { }
    client.Disconnect();
    terminal.Close();
    try { await drain; } catch { }
    Console.WriteLine();
}
