using Npgsql;

namespace CWGaming.Shared;

public static class RuntimeConfiguration
{
    public const string GamePostgresConnectionEnvironmentVariable = "MMUDREBORN_POSTGRES_CONNECTION";
    public const string BbsPostgresConnectionEnvironmentVariable = "CWGAMING_BBS_POSTGRES_CONNECTION";
    public const string BbsApiUrlEnvironmentVariable = "CWGAMING_BBS_API_URL";
    public const string BbsApiKeyEnvironmentVariable = "CWGAMING_BBS_API_KEY";
    public const string FastTestModeEnvironmentVariable = "MMUDREBORN_FAST_TEST_MODE";
    public const string QuietTestLoggingEnvironmentVariable = "MMUDREBORN_QUIET_TEST_LOGS";
    private const string DefaultBbsDatabaseName = "cwgaming_bbs";

    public static string ResolveGamePostgresConnectionString()
    {
        string? value = Environment.GetEnvironmentVariable(GamePostgresConnectionEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        return "Host=localhost;Port=5432;Database=mmudreborn;Username=mmudreborn;Password=mmudreborn";
    }

    public static string ResolveBbsPostgresConnectionString(string? gameConnectionString = null)
    {
        string? value = Environment.GetEnvironmentVariable(BbsPostgresConnectionEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        var builder = new NpgsqlConnectionStringBuilder(gameConnectionString ?? ResolveGamePostgresConnectionString())
        {
            Database = DefaultBbsDatabaseName,
        };

        return builder.ConnectionString;
    }

    public static string ResolveBbsApiBaseUrl()
    {
        string? value = Environment.GetEnvironmentVariable(BbsApiUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        return "http://127.0.0.1:5097/";
    }

    public static string ResolveBbsApiKey()
    {
        string? value = Environment.GetEnvironmentVariable(BbsApiKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        return "local-dev-bbs-api-key";
    }

    public static bool IsFastTestModeEnabled()
    {
        return IsTruthyEnvironmentVariable(FastTestModeEnvironmentVariable);
    }

    public static bool IsQuietTestLoggingEnabled()
    {
        return IsTruthyEnvironmentVariable(QuietTestLoggingEnvironmentVariable);
    }

    private static bool IsTruthyEnvironmentVariable(string environmentVariable)
    {
        string? value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
