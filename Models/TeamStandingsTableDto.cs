namespace DataSvc.Models;

public sealed class TeamStandingsTableDto
{
    public List<TeamStandingDto> Standings { get; set; } = new();
}
