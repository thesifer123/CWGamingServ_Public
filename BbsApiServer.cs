using System.Net;
using System.Text.Json;
using CWGaming.Shared;

namespace CWGamingServ;

public sealed class BbsApiServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private HttpListener _listener = new();
    private readonly string _prefix;
    private readonly IBbsUserRepository _bbsUserRepository;
    private readonly string _apiKey;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listenerTask;

    public BbsApiServer(string baseUrl, IBbsUserRepository bbsUserRepository, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(bbsUserRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _bbsUserRepository = bbsUserRepository;
        _apiKey = apiKey;
        _prefix = NormalizePrefix(baseUrl);
        _listener.Prefixes.Add(_prefix);
    }

    public void Start()
    {
        if (_listener.IsListening)
            return;

        // On a fast supervised restart the previous instance's socket can still be in TIME_WAIT / mid
        // teardown for a moment, so HttpListener.Start() throws "Address already in use" (errno 98).
        // Retry for a few seconds rather than letting the unhandled exception abort the whole host
        // (exit 134) and put the supervisor into a relaunch loop. (The supervisor now also launches the
        // binary directly so the old instance can't be orphaned — this just rides out the brief race.)
        //
        // A FAILED HttpListener.Start() leaves THAT instance unusable: a second Start() on the same
        // object throws ObjectDisposedException ("Cannot access a disposed object"), NOT another
        // HttpListenerException — which the old single-instance retry didn't catch, so it crashed the
        // host with a misleading error on attempt 2. So each retry must run against a FRESH listener.
        // And if the port is still held after every attempt (a genuine duplicate instance, not just a
        // TIME_WAIT race), surface a clear "port in use" error instead of looping forever / crashing.
        const int maxAttempts = 20;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                _listener.Start();
                break;
            }
            catch (Exception ex) when (ex is HttpListenerException { ErrorCode: 98 } or ObjectDisposedException)
            {
                if (attempt >= maxAttempts)
                    throw new InvalidOperationException(
                        $"BBS API could not bind {_prefix} after {maxAttempts} attempts — the port is already in use (another instance still running?).", ex);

                Console.WriteLine($"[BbsApiServer] port still in use (attempt {attempt}/{maxAttempts}); retrying in 500ms...");
                Thread.Sleep(500);
                _listener = RecreateListener();
            }
        }

        _listenerTask = Task.Run(() => ListenAsync(_shutdown.Token));
    }

    // A listener whose Start() faulted can't be reused, so build a fresh one carrying the same prefix.
    private HttpListener RecreateListener()
    {
        try { _listener.Close(); } catch { /* the faulted listener may already be disposed */ }
        var fresh = new HttpListener();
        fresh.Prefixes.Add(_prefix);
        return fresh;
    }

    public void Stop()
    {
        _shutdown.Cancel();

        if (_listener.IsListening)
            _listener.Stop();
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
        _shutdown.Dispose();

        if (_listenerTask != null)
        {
            try
            {
                _listenerTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestSafelyAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleRequestSafelyAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            await HandleRequestAsync(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (context.Response.OutputStream.CanWrite)
                context.Response.Close();
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.InternalServerError, ex.Message, cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (!IsAuthorized(context.Request))
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.Unauthorized, "Unauthorized", cancellationToken);
            return;
        }

        string[] segments = context.Request.Url?.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];

        if (segments.Length < 2 ||
            !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.NotFound, "Not found", cancellationToken);
            return;
        }

        if (segments[1].Equals("bbs-settings", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSettingsRequestAsync(context, segments, cancellationToken);
            return;
        }

        if (!segments[1].Equals("bbs-users", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.NotFound, "Not found", cancellationToken);
            return;
        }

        if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && segments.Length == 2)
        {
            await WriteJsonAsync(context.Response, _bbsUserRepository.GetUsers(), HttpStatusCode.OK, cancellationToken);
            return;
        }

        if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && segments.Length == 3)
        {
            string userName = Uri.UnescapeDataString(segments[2]);
            var account = _bbsUserRepository.LoadUser(userName);
            if (account == null)
            {
                await WriteNotFoundAsync(context.Response);
                return;
            }

            await WriteJsonAsync(context.Response, account, HttpStatusCode.OK, cancellationToken);
            return;
        }

        if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 4 &&
            segments[3].Equals("exists", StringComparison.OrdinalIgnoreCase))
        {
            string userName = Uri.UnescapeDataString(segments[2]);
            await WriteJsonAsync(context.Response, _bbsUserRepository.UserExists(userName), HttpStatusCode.OK, cancellationToken);
            return;
        }

        if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.MethodNotAllowed, "Method not allowed", cancellationToken);
            return;
        }

        if (segments.Length != 3)
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.NotFound, "Not found", cancellationToken);
            return;
        }

        switch (segments[2].ToLowerInvariant())
        {
            case "authenticate":
                {
                    var request = await ReadJsonAsync<BbsAuthenticateRequest>(context.Request, cancellationToken);
                    var account = _bbsUserRepository.LoadUser(request.UserName, request.Password);
                    if (account == null)
                    {
                        await WriteNotFoundAsync(context.Response);
                        return;
                    }

                    await WriteJsonAsync(context.Response, account, HttpStatusCode.OK, cancellationToken);
                    return;
                }

            case "save":
                {
                    var account = await ReadJsonAsync<BbsUserAccount>(context.Request, cancellationToken);
                    _bbsUserRepository.SaveUser(account);
                    context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                    context.Response.Close();
                    return;
                }

            case "set-password":
                {
                    var request = await ReadJsonAsync<BbsSetPasswordRequest>(context.Request, cancellationToken);
                    bool updated = _bbsUserRepository.SetPassword(request.UserName, request.PasswordHash);
                    await WriteJsonAsync(context.Response, updated, HttpStatusCode.OK, cancellationToken);
                    return;
                }

            case "set-display-name":
                {
                    var request = await ReadJsonAsync<BbsSetDisplayNameRequest>(context.Request, cancellationToken);
                    bool updated = _bbsUserRepository.SetDisplayName(request.UserName, request.DisplayName);
                    await WriteJsonAsync(context.Response, updated, HttpStatusCode.OK, cancellationToken);
                    return;
                }

            case "set-sysop":
                {
                    var request = await ReadJsonAsync<BbsSetSysopRequest>(context.Request, cancellationToken);
                    bool updated = _bbsUserRepository.SetSysopStatus(request.UserName, request.IsSysop);
                    await WriteJsonAsync(context.Response, updated, HttpStatusCode.OK, cancellationToken);
                    return;
                }

            case "sync-from-players":
                {
                    var request = await ReadJsonAsync<BbsSyncUsersRequest>(context.Request, cancellationToken);
                    int synced = _bbsUserRepository.SyncUsers(request.Users, request.OverwriteExistingPasswords);
                    await WriteJsonAsync(context.Response, synced, HttpStatusCode.OK, cancellationToken);
                    return;
                }
        }

        await WriteErrorAsync(context.Response, HttpStatusCode.NotFound, "Not found", cancellationToken);
    }

    private async Task HandleSettingsRequestAsync(HttpListenerContext context, string[] segments, CancellationToken cancellationToken)
    {
        if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && segments.Length == 3)
        {
            string key = Uri.UnescapeDataString(segments[2]);
            string value = _bbsUserRepository.GetSettingText(key, string.Empty);
            await WriteJsonAsync(context.Response, value, HttpStatusCode.OK, cancellationToken);
            return;
        }

        if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 3 &&
            segments[2].Equals("save", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadJsonAsync<BbsSetSettingRequest>(context.Request, cancellationToken);
            _bbsUserRepository.SetSettingText(request.Key, request.Value);
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            context.Response.Close();
            return;
        }

        if (!context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            !context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.MethodNotAllowed, "Method not allowed", cancellationToken);
            return;
        }

        await WriteErrorAsync(context.Response, HttpStatusCode.NotFound, "Not found", cancellationToken);
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        string? providedKey = request.Headers[BbsApiContract.ApiKeyHeaderName];
        return string.Equals(providedKey, _apiKey, StringComparison.Ordinal);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        var payload = await JsonSerializer.DeserializeAsync<T>(request.InputStream, JsonOptions, cancellationToken);
        return payload ?? throw new InvalidOperationException($"Expected a JSON payload for {typeof(T).Name}.");
    }

    private static async Task WriteJsonAsync<T>(HttpListenerResponse response, T payload, HttpStatusCode statusCode, CancellationToken cancellationToken)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.OutputStream, payload, JsonOptions, cancellationToken);
        response.Close();
    }

    private static async Task WriteErrorAsync(HttpListenerResponse response, HttpStatusCode statusCode, string message, CancellationToken cancellationToken)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.OutputStream, new { error = message }, JsonOptions, cancellationToken);
        response.Close();
    }

    private static Task WriteNotFoundAsync(HttpListenerResponse response)
    {
        response.StatusCode = (int)HttpStatusCode.NotFound;
        response.Close();
        return Task.CompletedTask;
    }

    private static string NormalizePrefix(string baseUrl)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);
        string normalized = uri.ToString();
        return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : normalized + "/";
    }
}