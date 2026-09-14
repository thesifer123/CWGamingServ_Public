using Npgsql;
using NpgsqlTypes;

namespace CWGaming.Shared;

public sealed class BbsUserRepository : IBbsUserRepository
{
    private readonly string _connectionString;

    public BbsUserRepository(string connectionString)
    {
        _connectionString = connectionString;
        EnsureReady();
    }

    public bool SetPassword(string userName, string newPasswordHash)
    {
        userName = BbsText.NormalizeNamePart(userName);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"UPDATE bbs.Users SET PasswordHash = @pass, UpdatedAt = CURRENT_TIMESTAMP::TEXT WHERE UserName = @name::citext";
        command.Parameters.AddWithValue("@name", userName);
        command.Parameters.AddWithValue("@pass", newPasswordHash);
        return command.ExecuteNonQuery() > 0;
    }

    public bool SetDisplayName(string userName, string displayName)
    {
        userName = BbsText.NormalizeNamePart(userName);
        displayName = BbsText.NormalizeNamePart(displayName);
        if (displayName.Length == 0)
            displayName = userName;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"UPDATE bbs.Users SET DisplayName = @display, UpdatedAt = CURRENT_TIMESTAMP::TEXT WHERE UserName = @name::citext";
        command.Parameters.AddWithValue("@name", userName);
        command.Parameters.AddWithValue("@display", displayName);
        return command.ExecuteNonQuery() > 0;
    }

    public bool SetSysopStatus(string userName, bool isSysop)
    {
        userName = BbsText.NormalizeNamePart(userName);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"UPDATE bbs.Users SET IsSysop = @issysop, UpdatedAt = CURRENT_TIMESTAMP::TEXT WHERE UserName = @name::citext";
        command.Parameters.AddWithValue("@name", userName);
        command.Parameters.AddWithValue("@issysop", isSysop ? 1 : 0);
        return command.ExecuteNonQuery() > 0;
    }

    public string GetSettingText(string key, string defaultValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM bbs.Settings WHERE Key = @key";
        command.Parameters.AddWithValue("@key", key.Trim());
        return command.ExecuteScalar() as string ?? defaultValue;
    }

    public void SetSettingText(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO bbs.Settings (Key, Value)
            VALUES (@key, @value)
            ON CONFLICT (Key) DO UPDATE SET
                Value = EXCLUDED.Value";
        command.Parameters.AddWithValue("@key", key.Trim());
        command.Parameters.AddWithValue("@value", value ?? string.Empty);
        command.ExecuteNonQuery();
    }


    private NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureReady()
    {
        PostgresDatabaseProvisioner.EnsureDatabaseExists(_connectionString);

        using var connection = OpenConnection();
        using (var extension = connection.CreateCommand())
        {
            extension.CommandText = "CREATE EXTENSION IF NOT EXISTS citext";
            extension.ExecuteNonQuery();
        }

        connection.ReloadTypes();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE SCHEMA IF NOT EXISTS bbs;

            CREATE TABLE IF NOT EXISTS bbs.Users (
                UserName CITEXT PRIMARY KEY,
                PasswordHash TEXT NOT NULL,
                IsSysop INTEGER NOT NULL DEFAULT 0,
                IsTestAccount INTEGER NOT NULL DEFAULT 0,
                LastIpAddress TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP::TEXT,
                UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP::TEXT
            );

            ALTER TABLE bbs.Users
            ADD COLUMN IF NOT EXISTS IsSysop INTEGER NOT NULL DEFAULT 0;

            -- Public handle shown to other users; CITEXT so uniqueness/impersonation checks are
            -- case-insensitive. Backfill existing rows to their login name so none are left blank.
            ALTER TABLE bbs.Users
            ADD COLUMN IF NOT EXISTS DisplayName CITEXT NOT NULL DEFAULT '';

            UPDATE bbs.Users SET DisplayName = UserName WHERE DisplayName IS NULL OR DisplayName = '';

            ALTER TABLE bbs.Users
            ADD COLUMN IF NOT EXISTS IsTestAccount INTEGER NOT NULL DEFAULT 0;

            ALTER TABLE bbs.Users
            ADD COLUMN IF NOT EXISTS LastIpAddress TEXT NOT NULL DEFAULT '';

            -- Retire the legacy BBS-side account→character link. The link now lives authoritatively on the
            -- door's own player row (Players.BbsUserId, in the game database); the door's bootstrap backfill
            -- has already mirrored every LinkedPlayerName onto it. This drop targets the ACTIVE bbs.Users in
            -- the BBS database (this repo's own connection) — no cross-database reach into Players, and no
            -- ordering dependency, since nothing here or downstream reads the column any more.
            DROP INDEX IF EXISTS bbs.idx_bbs_users_linkedplayername;

            ALTER TABLE bbs.Users
            DROP COLUMN IF EXISTS LinkedPlayerName;

            CREATE TABLE IF NOT EXISTS bbs.Settings (
                Key CITEXT PRIMARY KEY,
                Value TEXT NOT NULL DEFAULT ''
            );";
        command.ExecuteNonQuery();
    }

    public bool UserExists(string userName)
    {
        userName = BbsText.NormalizeNamePart(userName);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        // bbs.Users.UserName is CITEXT (case-insensitive), but Npgsql binds a plain string parameter as
        // `text`, which makes "UserName = @name" resolve to the TEXT equality operator (the citext column
        // is down-cast to text) and become CASE-SENSITIVE — so checking "GARY" would miss an existing
        // "Gary" and let a duplicate-by-case account be created. Casting the parameter to citext keeps the
        // comparison case-insensitive and still uses the citext primary-key index. The same cast is
        // required on every UserName lookup below (login, link, update).
        command.CommandText = "SELECT 1 FROM bbs.Users WHERE UserName = @name::citext";
        command.Parameters.AddWithValue("@name", userName);
        return command.ExecuteScalar() != null;
    }

    public BbsUserAccount? LoadUser(string userName)
    {
        userName = BbsText.NormalizeNamePart(userName);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT UserName, DisplayName, PasswordHash, IsSysop, IsTestAccount, LastIpAddress
            FROM bbs.Users
            WHERE UserName = @name::citext";
        command.Parameters.AddWithValue("@name", userName);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return ReadAccount(reader);
    }

    public BbsUserAccount? LoadUser(string userName, string password)
    {
        var account = LoadUser(userName);
        if (account == null)
            return null;

        if (!BbsSecurity.VerifyPassword(password, account.PasswordHash))
            return null;

        // Transparent upgrade: re-hash legacy/weaker credentials with the current scheme on a
        // successful login so the stored hash strengthens over time without locking anyone out.
        if (BbsSecurity.NeedsRehash(account.PasswordHash))
        {
            string upgraded = BbsSecurity.HashPassword(password);
            if (SetPassword(account.UserName, upgraded))
                account.PasswordHash = upgraded;
        }

        return account;
    }

    public void SaveUser(BbsUserAccount account)
    {
        account.UserName = BbsText.NormalizeNamePart(account.UserName);
        account.DisplayName = BbsText.NormalizeNamePart(account.DisplayName);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO bbs.Users (UserName, DisplayName, PasswordHash, IsSysop, IsTestAccount, LastIpAddress, CreatedAt, UpdatedAt)
            VALUES (@name, COALESCE(NULLIF(@display, ''), @name), @pass, @issysop, @istestaccount, @lastipaddress, CURRENT_TIMESTAMP::TEXT, CURRENT_TIMESTAMP::TEXT)
            ON CONFLICT (UserName) DO UPDATE SET
                DisplayName = COALESCE(NULLIF(EXCLUDED.DisplayName, ''), bbs.Users.DisplayName),
                PasswordHash = EXCLUDED.PasswordHash,
                IsTestAccount = EXCLUDED.IsTestAccount,
                LastIpAddress = COALESCE(NULLIF(EXCLUDED.LastIpAddress, ''), bbs.Users.LastIpAddress),
                UpdatedAt = CURRENT_TIMESTAMP::TEXT";
        command.Parameters.AddWithValue("@name", account.UserName);
        command.Parameters.AddWithValue("@display", account.DisplayName);
        command.Parameters.AddWithValue("@pass", account.PasswordHash);
        command.Parameters.AddWithValue("@issysop", account.IsSysop ? 1 : 0);
        command.Parameters.AddWithValue("@istestaccount", account.IsTestAccount ? 1 : 0);
        command.Parameters.AddWithValue("@lastipaddress", account.LastIpAddress);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<BbsUserAccount> GetUsers()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT UserName, DisplayName, PasswordHash, IsSysop, IsTestAccount, LastIpAddress
            FROM bbs.Users
            ORDER BY UserName";

        var accounts = new List<BbsUserAccount>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            accounts.Add(ReadAccount(reader));

        return accounts;
    }

    public int SyncUsers(IReadOnlyCollection<BbsUserAccount> users, bool overwriteExistingPasswords = false)
    {
        ArgumentNullException.ThrowIfNull(users);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // DisplayName is seeded to the login name for brand-new rows but never overwritten on conflict —
        // a player→BBS sync must not stomp a handle the member chose in Account Settings.
        command.CommandText = overwriteExistingPasswords
            ? @"
            INSERT INTO bbs.Users (UserName, DisplayName, PasswordHash, IsSysop, IsTestAccount, LastIpAddress, CreatedAt, UpdatedAt)
            VALUES (@name, @name, @pass, @issysop, @istestaccount, @lastipaddress, CURRENT_TIMESTAMP::TEXT, CURRENT_TIMESTAMP::TEXT)
            ON CONFLICT (UserName) DO UPDATE SET
                PasswordHash = EXCLUDED.PasswordHash,
                IsTestAccount = EXCLUDED.IsTestAccount,
                LastIpAddress = COALESCE(NULLIF(EXCLUDED.LastIpAddress, ''), bbs.Users.LastIpAddress),
                UpdatedAt = CURRENT_TIMESTAMP::TEXT"
            : @"
            INSERT INTO bbs.Users (UserName, DisplayName, PasswordHash, IsSysop, IsTestAccount, LastIpAddress, CreatedAt, UpdatedAt)
            VALUES (@name, @name, @pass, @issysop, @istestaccount, @lastipaddress, CURRENT_TIMESTAMP::TEXT, CURRENT_TIMESTAMP::TEXT)
            ON CONFLICT (UserName) DO UPDATE SET
                IsTestAccount = EXCLUDED.IsTestAccount,
                LastIpAddress = COALESCE(NULLIF(EXCLUDED.LastIpAddress, ''), bbs.Users.LastIpAddress),
                UpdatedAt = CURRENT_TIMESTAMP::TEXT";

        var nameParameter = command.CreateParameter();
        nameParameter.ParameterName = "@name";
        command.Parameters.Add(nameParameter);

        var passwordParameter = command.CreateParameter();
        passwordParameter.ParameterName = "@pass";
        command.Parameters.Add(passwordParameter);

        var sysopParameter = command.CreateParameter();
        sysopParameter.ParameterName = "@issysop";
        command.Parameters.Add(sysopParameter);

        var testAccountParameter = command.CreateParameter();
        testAccountParameter.ParameterName = "@istestaccount";
        command.Parameters.Add(testAccountParameter);

        var lastIpAddressParameter = command.CreateParameter();
        lastIpAddressParameter.ParameterName = "@lastipaddress";
        command.Parameters.Add(lastIpAddressParameter);

        int count = 0;
        foreach (var user in users)
        {
            nameParameter.Value = BbsText.NormalizeNamePart(user.UserName);
            passwordParameter.Value = user.PasswordHash;
            sysopParameter.Value = user.IsSysop ? 1 : 0;
            testAccountParameter.Value = user.IsTestAccount ? 1 : 0;
            lastIpAddressParameter.Value = user.LastIpAddress;
            command.ExecuteNonQuery();
            count++;
        }

        transaction.Commit();
        return count;
    }

    private static BbsUserAccount ReadAccount(NpgsqlDataReader reader)
    {
        string userName = BbsText.NormalizeNamePart(reader.GetString(reader.GetOrdinal("UserName")));
        string displayName = BbsText.NormalizeNamePart(reader.GetString(reader.GetOrdinal("DisplayName")));
        return new BbsUserAccount
        {
            UserName = userName,
            // Older rows predating the DisplayName column (or blank handles) fall back to the login name.
            DisplayName = displayName.Length == 0 ? userName : displayName,
            PasswordHash = reader.GetString(reader.GetOrdinal("PasswordHash")),
            IsSysop = reader.GetInt32(reader.GetOrdinal("IsSysop")) != 0,
            IsTestAccount = reader.GetInt32(reader.GetOrdinal("IsTestAccount")) != 0,
            LastIpAddress = reader.GetString(reader.GetOrdinal("LastIpAddress")),
        };
    }
}
