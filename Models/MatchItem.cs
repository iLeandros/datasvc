namespace DataSvc.Models;

public class MatchItem
{
    public string? Competition { get; set; }
    public string? Date { get; set; }
    public string HostTeam { get; set; }
    public int HostGoals { get; set; }
    public string GuestTeam { get; set; }
    public int GuestGoals { get; set; }
    public List<MatchAction> Actions { get; set; } = new();
    public HalfTimeScore HalfTime { get; set; } = new(0,0);

}
