using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CWGaming.Shared;

public sealed class HttpBbsUserRepository : IBbsUserRepository, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;

    public HttpBbsUserRepository(string baseUrl, string apiKey, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _disposeHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri(NormalizeBaseUrl(baseUrl), UriKind.Absolute);

        if (_httpClient.DefaultRequestHeaders.Contains(BbsApiContract.ApiKeyHeaderName))
            _httpClient.DefaultRequestHeaders.Remove(BbsApiContract.ApiKeyHeaderName);

        _httpClient.DefaultRequestHeaders.Add(BbsApiContract.ApiKeyHeaderName, apiKey);
    }

    public bool SetPassword(string userName, string newPasswordHash)
    {
        userName = BbsText.NormalizeNamePart(userName);
        return SendPost<bool>($"{BbsApiContract.UsersRoute}/set-password", new BbsSetPasswordRequest(userName, newPasswordHash));
    }

    public bool SetDisplayName(string userName, string displayName)
    {
        userName = BbsText.NormalizeNamePart(userName);
        displayName = BbsText.NormalizeNamePart(displayName);
        return SendPost<bool>($"{BbsApiContract.UsersRoute}/set-display-name", new BbsSetDisplayNameRequest(userName, displayName));
    }

    public bool SetSysopStatus(string userName, bool isSysop)
    {
        userName = BbsText.NormalizeNamePart(userName);
        return SendPost<bool>($"{BbsApiContract.UsersRoute}/set-sysop", new BbsSetSysopRequest(userName, isSysop));
    }

    public string GetSettingText(string key, string defaultValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = SendGetOptional<string>($"{BbsApiContract.SettingsRoute}/{EncodePathSegment(key.Trim())}");
        return value ?? defaultValue;
    }

    public void SetSettingText(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        SendPostWithoutResponse($"{BbsApiContract.SettingsRoute}/save", new BbsSetSettingRequest(key.Trim(), value ?? string.Empty));
    }

    public bool UserExists(string userName)
    {
        userName = BbsText.NormalizeNamePart(userName);
        return SendGet<bool>($"{BbsApiContract.UsersRoute}/{EncodePathSegment(userName)}/exists");
    }

    public BbsUserAccount? LoadUser(string userName)
    {
        userName = BbsText.NormalizeNamePart(userName);
        return SendGetOptional<BbsUserAccount>($"{BbsApiContract.UsersRoute}/{EncodePathSegment(userName)}");
    }

    public BbsUserAccount? LoadUser(string userName, string password)
    {
        userName = BbsText.NormalizeNamePart(userName);
        return SendPostOptional<BbsUserAccount>($"{BbsApiContract.UsersRoute}/authenticate", new BbsAuthenticateRequest(userName, password));
    }

    public void SaveUser(BbsUserAccount account)
    {
        SendPostWithoutResponse($"{BbsApiContract.UsersRoute}/save", NormalizeAccount(account));
    }

    public IReadOnlyList<BbsUserAccount> GetUsers()
    {
        return SendGet<List<BbsUserAccount>>(BbsApiContract.UsersRoute);
    }

    public int SyncUsers(IReadOnlyCollection<BbsUserAccount> users, bool overwriteExistingPasswords = false)
    {
        ArgumentNullException.ThrowIfNull(users);

        var normalizedUsers = users
            .Select(NormalizeAccount)
            .ToList();

        return SendPost<int>(
            $"{BbsApiContract.UsersRoute}/sync-from-players",
            new BbsSyncUsersRequest(normalizedUsers, overwriteExistingPasswords));
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
            _httpClient.Dispose();
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        return baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
    }

    private static string EncodePathSegment(string value)
    {
        return Uri.EscapeDataString(value);
    }

    private static BbsUserAccount NormalizeAccount(BbsUserAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return new BbsUserAccount
        {
            UserName = BbsText.NormalizeNamePart(account.UserName),
            DisplayName = BbsText.NormalizeNamePart(account.DisplayName),
            PasswordHash = account.PasswordHash,
            IsSysop = account.IsSysop,
            IsTestAccount = account.IsTestAccount,
            LastIpAddress = account.LastIpAddress,
        };
    }

    private T SendGet<T>(string path) where T : notnull
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = Send(request);
        response.EnsureSuccessStatusCode();

        var payload = response.Content.ReadFromJsonAsync<T>(JsonOptions).GetAwaiter().GetResult();
        return payload ?? throw new InvalidOperationException($"BBS API route '{path}' returned an empty response body.");
    }

    private T? SendGetOptional<T>(string path) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = Send(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return response.Content.ReadFromJsonAsync<T>(JsonOptions).GetAwaiter().GetResult();
    }

    private T SendPost<T>(string path, object payload) where T : notnull
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };

        using var response = Send(request);
        response.EnsureSuccessStatusCode();

        var responsePayload = response.Content.ReadFromJsonAsync<T>(JsonOptions).GetAwaiter().GetResult();
        return responsePayload ?? throw new InvalidOperationException($"BBS API route '{path}' returned an empty response body.");
    }

    private T? SendPostOptional<T>(string path, object payload) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };

        using var response = Send(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return response.Content.ReadFromJsonAsync<T>(JsonOptions).GetAwaiter().GetResult();
    }

    private void SendPostWithoutResponse(string path, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };

        using var response = Send(request);
        response.EnsureSuccessStatusCode();
    }

    private HttpResponseMessage Send(HttpRequestMessage request)
    {
        return _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
    }
}
