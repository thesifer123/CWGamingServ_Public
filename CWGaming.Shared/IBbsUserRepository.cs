namespace CWGaming.Shared;

/// <summary>
/// The BBS board account store. Generic — a mounted door reads/links accounts through it but never needs
/// the concrete (Postgres / HTTP) implementation. The game→BBS bridge (sync from door players) is NOT on
/// this interface because it is door-specific; the concrete repositories expose it separately.
/// </summary>
public interface IBbsUserRepository
{
    /// <summary>
    /// Sets the password for a BBS user.
    /// Returns true if the user exists and was updated.
    /// </summary>
    bool SetPassword(string userName, string newPasswordHash);

    /// <summary>
    /// Sets the public display name (handle) for a BBS user. Only updates the DisplayName column, so it
    /// never clobbers the account's password or linked character. Returns true if the user was updated.
    /// </summary>
    bool SetDisplayName(string userName, string displayName);
    bool SetSysopStatus(string userName, bool isSysop);
    string GetSettingText(string key, string defaultValue);
    void SetSettingText(string key, string value);

    bool UserExists(string userName);
    BbsUserAccount? LoadUser(string userName);
    BbsUserAccount? LoadUser(string userName, string password);
    void SaveUser(BbsUserAccount account);
    IReadOnlyList<BbsUserAccount> GetUsers();
    int SyncUsers(IReadOnlyCollection<BbsUserAccount> users, bool overwriteExistingPasswords = false);
}
