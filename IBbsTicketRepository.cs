namespace CWGamingServ;

public interface IBbsTicketRepository
{
    int CreateTicket(BbsTicketRecord ticket);
    IReadOnlyList<BbsTicketSummary> GetTickets(int limit = 50);
    BbsTicketRecord? LoadTicket(int id);
    bool ResolveTicket(int id, string resolvedByBbsUserName);
    bool DeleteTicket(int id);
    void ClearTickets();
}