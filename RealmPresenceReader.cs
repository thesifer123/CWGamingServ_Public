using CWGaming.Shared;
using Npgsql;

namespace CWGamingServ;

/// <summary>
/// Reads in-realm presence for a cross-realm <c>;who</c> without reaching into any backend's memory: each
/// realm's backend already pushes its online roster to <c>public.online_players</c> in its own game DB, so
/// the front just unions those tables (a few tiny reads). Realms without a configured
/// <see cref="RealmDefinition.GameDb"/>, or whose DB is momentarily unreachable, are skipped — a partial
/// who list beats a failed command.
/// </summary>
public sealed class RealmPresenceReader
{
    private readonly IReadOnlyList<RealmDefinition> _realms;
    private readonly bool _quietLogging;

    public RealmPresenceReader(IReadOnlyList<RealmDefinition> realms, bool quietLogging)
    {
        _realms = realms;
        _quietLogging = quietLogging;
    }

    public IReadOnlyList<BbsPresenceSnapshot> ReadInRealmPresence()
    {
        var results = new List<BbsPresenceSnapshot>();
        foreach (var realm in _realms)
        {
            if (string.IsNullOrWhiteSpace(realm.GameDb))
                continue;

            try
            {
                using var conn = new NpgsqlConnection(realm.GameDb);
                conn.Open();
                using var cmd = new NpgsqlCommand(
                    "SELECT name, issysop FROM public.online_players ORDER BY name", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string name = reader.GetString(0);
                    bool isSysop = !reader.IsDBNull(1) && reader.GetBoolean(1);
                    // Location doubles as the realm label so a cross-realm who shows who is where.
                    results.Add(new BbsPresenceSnapshot(name, isSysop, 0, $"In {realm.DisplayName}", string.Empty));
                }
            }
            catch (Exception ex)
            {
                if (!_quietLogging)
                    Console.WriteLine($"[front] presence read for realm '{realm.Id}' failed (skipping): {ex.Message}");
            }
        }

        return results;
    }
}
