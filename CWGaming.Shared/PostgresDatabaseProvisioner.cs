using Npgsql;

namespace CWGaming.Shared;

public static class PostgresDatabaseProvisioner
{
    public static void EnsureDatabaseExists(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Database))
            throw new InvalidOperationException("PostgreSQL connection string must include a database name.");

        try
        {
            EnsureDatabaseExists(builder, BuildAdminConnectionString(builder.ConnectionString));
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            var fallbackAdminConnectionString = ResolveFallbackAdminConnectionString(builder);
            if (string.IsNullOrWhiteSpace(fallbackAdminConnectionString))
                throw new InvalidOperationException("PostgreSQL user cannot create the configured database. Set MMUDREBORN_POSTGRES_ADMIN_CONNECTION to a role with CREATE DATABASE privileges.", ex);

            EnsureDatabaseExists(builder, BuildAdminConnectionString(fallbackAdminConnectionString));
        }
    }

    private static void EnsureDatabaseExists(NpgsqlConnectionStringBuilder targetBuilder, NpgsqlConnectionStringBuilder adminBuilder)
    {
        using var adminConn = new NpgsqlConnection(adminBuilder.ConnectionString);
        adminConn.Open();

        using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", adminConn);
        exists.Parameters.AddWithValue("name", targetBuilder.Database);
        if (exists.ExecuteScalar() != null)
            return;

        string createSql = $"CREATE DATABASE {QuoteIdentifier(targetBuilder.Database)}";
        if (!string.IsNullOrWhiteSpace(targetBuilder.Username))
            createSql += $" OWNER {QuoteIdentifier(targetBuilder.Username)}";

        using var create = new NpgsqlCommand(createSql, adminConn);
        create.ExecuteNonQuery();
    }

    private static NpgsqlConnectionStringBuilder BuildAdminConnectionString(string connectionString)
    {
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
        };

        return adminBuilder;
    }

    private static string? ResolveFallbackAdminConnectionString(NpgsqlConnectionStringBuilder targetBuilder)
    {
        string? configuredAdminConnection = Environment.GetEnvironmentVariable("MMUDREBORN_POSTGRES_ADMIN_CONNECTION");
        if (!string.IsNullOrWhiteSpace(configuredAdminConnection))
            return configuredAdminConnection;

        return null;
    }

    private static string QuoteIdentifier(string value)
    {
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
