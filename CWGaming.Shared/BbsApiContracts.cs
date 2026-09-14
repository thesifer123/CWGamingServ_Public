namespace CWGaming.Shared;

public static class BbsApiContract
{
    public const string ApiKeyHeaderName = "X-Bbs-Api-Key";
    public const string UsersRoute = "api/bbs-users";
    public const string SettingsRoute = "api/bbs-settings";
}

public sealed record BbsAuthenticateRequest(string UserName, string Password);
public sealed record BbsSetPasswordRequest(string UserName, string PasswordHash);
public sealed record BbsSetDisplayNameRequest(string UserName, string DisplayName);
public sealed record BbsSetSysopRequest(string UserName, bool IsSysop);
public sealed record BbsSetSettingRequest(string Key, string Value);
public sealed record BbsSyncUsersRequest(IReadOnlyList<BbsUserAccount> Users, bool OverwriteExistingPasswords);
