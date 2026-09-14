using CWGaming.Shared;

namespace CWGamingServ;

public static class BbsAppIds
{
    public const string Bbs = HostedAppIds.Bbs;
    public const string Mmudreborn = HostedAppIds.Mmudreborn;
}

public sealed class BbsTicketRecord
{
    public int Id { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public string ReporterBbsUserName { get; set; } = string.Empty;
    public string ReporterPlayerName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string BriefDescription { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string LocationText { get; set; } = string.Empty;
    public string SubjectText { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public bool IsResolved { get; set; }
    public string ResolvedAt { get; set; } = string.Empty;
    public string ResolvedByBbsUserName { get; set; } = string.Empty;
}

public sealed class BbsTicketSummary
{
    public int Id { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string BriefDescription { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public bool IsResolved { get; set; }
}