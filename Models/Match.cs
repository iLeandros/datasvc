namespace DataSvc.Models;

public class Match
{
    public Match(hostTeam hostteam, guestTeam guestteam)
    {
        this.hostTeam = hostteam;
        this.guestTeam = guestteam;
    }
    public hostTeam hostTeam { get; set; }
    public guestTeam guestTeam { get; set; }
}
