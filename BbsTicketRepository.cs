using Npgsql;

namespace CWGamingServ;

public sealed class BbsTicketRepository : IBbsTicketRepository
{
    private readonly string _connectionString;

    public BbsTicketRepository(string connectionString)
    {
        _connectionString = connectionString;
        EnsureReady();
    }

    public int CreateTicket(BbsTicketRecord ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO bbs.Tickets (
                AppId,
                WorldId,
                ReporterBbsUserName,
                ReporterPlayerName,
                Title,
                BriefDescription,
                Description,
                LocationText,
                SubjectText,
                CreatedAt)
            VALUES (
                @appId,
                @worldId,
                @reporterBbsUserName,
                @reporterPlayerName,
                @title,
                @briefDescription,
                @description,
                @locationText,
                @subjectText,
                CURRENT_TIMESTAMP::TEXT)
            RETURNING Id";
        AddWriteParameters(command, ticket);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<BbsTicketSummary> GetTickets(int limit = 50)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, AppId, WorldId, Title, BriefDescription, CreatedAt, IsResolved
            FROM bbs.Tickets
            ORDER BY Id DESC
            LIMIT @limit";
        command.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        var tickets = new List<BbsTicketSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            tickets.Add(ReadSummary(reader));

        return tickets;
    }

    public BbsTicketRecord? LoadTicket(int id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, AppId, WorldId, ReporterBbsUserName, ReporterPlayerName, Title, BriefDescription,
                   Description, LocationText, SubjectText, CreatedAt, IsResolved, ResolvedAt, ResolvedByBbsUserName
            FROM bbs.Tickets
            WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return ReadRecord(reader);
    }

    public bool ResolveTicket(int id, string resolvedByBbsUserName)
    {
        string normalizedResolver = CWGaming.Shared.BbsText.NormalizeNamePart(resolvedByBbsUserName ?? string.Empty);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE bbs.Tickets
            SET IsResolved = 1,
                ResolvedAt = CURRENT_TIMESTAMP::TEXT,
                ResolvedByBbsUserName = @resolvedBy
            WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@resolvedBy", normalizedResolver);
        return command.ExecuteNonQuery() > 0;
    }

    public bool DeleteTicket(int id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM bbs.Tickets WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public void ClearTickets()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "TRUNCATE TABLE bbs.Tickets RESTART IDENTITY";
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

            CREATE TABLE IF NOT EXISTS bbs.Tickets (
                Id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                AppId CITEXT NOT NULL DEFAULT '',
                WorldId CITEXT NOT NULL DEFAULT '',
                ReporterBbsUserName CITEXT NOT NULL DEFAULT '',
                ReporterPlayerName CITEXT NOT NULL DEFAULT '',
                Title TEXT NOT NULL DEFAULT '',
                BriefDescription TEXT NOT NULL DEFAULT '',
                Description TEXT NOT NULL DEFAULT '',
                LocationText TEXT NOT NULL DEFAULT '',
                SubjectText TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP::TEXT,
                IsResolved INTEGER NOT NULL DEFAULT 0,
                ResolvedAt TEXT NOT NULL DEFAULT '',
                ResolvedByBbsUserName CITEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_bbs_tickets_createdat ON bbs.Tickets (CreatedAt);
            CREATE INDEX IF NOT EXISTS idx_bbs_tickets_appid ON bbs.Tickets (AppId);
            CREATE INDEX IF NOT EXISTS idx_bbs_tickets_isresolved ON bbs.Tickets (IsResolved);";
        command.ExecuteNonQuery();
    }

    private static void AddWriteParameters(NpgsqlCommand command, BbsTicketRecord ticket)
    {
        command.Parameters.AddWithValue("@appId", NormalizeScopedValue(ticket.AppId));
        command.Parameters.AddWithValue("@worldId", NormalizeScopedValue(ticket.WorldId));
        command.Parameters.AddWithValue("@reporterBbsUserName", CWGaming.Shared.BbsText.NormalizeNamePart(ticket.ReporterBbsUserName ?? string.Empty));
        command.Parameters.AddWithValue("@reporterPlayerName", CWGaming.Shared.BbsText.NormalizeNamePart(ticket.ReporterPlayerName ?? string.Empty));
        command.Parameters.AddWithValue("@title", (ticket.Title ?? string.Empty).Trim());
        command.Parameters.AddWithValue("@briefDescription", (ticket.BriefDescription ?? string.Empty).Trim());
        command.Parameters.AddWithValue("@description", ticket.Description ?? string.Empty);
        command.Parameters.AddWithValue("@locationText", (ticket.LocationText ?? string.Empty).Trim());
        command.Parameters.AddWithValue("@subjectText", (ticket.SubjectText ?? string.Empty).Trim());
    }

    private static string NormalizeScopedValue(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    private static BbsTicketSummary ReadSummary(NpgsqlDataReader reader)
    {
        return new BbsTicketSummary
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            AppId = reader.GetString(reader.GetOrdinal("AppId")),
            WorldId = reader.GetString(reader.GetOrdinal("WorldId")),
            Title = reader.GetString(reader.GetOrdinal("Title")),
            BriefDescription = reader.GetString(reader.GetOrdinal("BriefDescription")),
            CreatedAt = reader.GetString(reader.GetOrdinal("CreatedAt")),
            IsResolved = reader.GetInt32(reader.GetOrdinal("IsResolved")) != 0,
        };
    }

    private static BbsTicketRecord ReadRecord(NpgsqlDataReader reader)
    {
        return new BbsTicketRecord
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            AppId = reader.GetString(reader.GetOrdinal("AppId")),
            WorldId = reader.GetString(reader.GetOrdinal("WorldId")),
            ReporterBbsUserName = CWGaming.Shared.BbsText.NormalizeNamePart(reader.GetString(reader.GetOrdinal("ReporterBbsUserName"))),
            ReporterPlayerName = CWGaming.Shared.BbsText.NormalizeNamePart(reader.GetString(reader.GetOrdinal("ReporterPlayerName"))),
            Title = reader.GetString(reader.GetOrdinal("Title")),
            BriefDescription = reader.GetString(reader.GetOrdinal("BriefDescription")),
            Description = reader.GetString(reader.GetOrdinal("Description")),
            LocationText = reader.GetString(reader.GetOrdinal("LocationText")),
            SubjectText = reader.GetString(reader.GetOrdinal("SubjectText")),
            CreatedAt = reader.GetString(reader.GetOrdinal("CreatedAt")),
            IsResolved = reader.GetInt32(reader.GetOrdinal("IsResolved")) != 0,
            ResolvedAt = reader.GetString(reader.GetOrdinal("ResolvedAt")),
            ResolvedByBbsUserName = CWGaming.Shared.BbsText.NormalizeNamePart(reader.GetString(reader.GetOrdinal("ResolvedByBbsUserName"))),
        };
    }
}