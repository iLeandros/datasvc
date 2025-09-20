namespace DataSvc.Models;

public sealed class MatchBetweenItemDto
{
    public string? Competition { get; set; }
    public string? Date { get; set; }
    public string? HostTeam { get; set; }
    public int HostGoals { get; set; }
    public string? GuestTeam { get; set; }
    public int GuestGoals { get; set; }
    // plus any actions you included
}
