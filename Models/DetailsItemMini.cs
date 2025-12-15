namespace DataSvc.Models;

// What the MAUI ItemDetailViewModel actually reads
// (TeamsInfo, MatchFacts, TeamStandings, H2H Matches, BarCharts)
public sealed class DetailsItemMini
{
    public List<TeamBasicInfoDto>?    TeamsInfo { get; set; }
    public List<MatchFactDto>?        MatchFacts { get; set; }
    public TeamStandingsTableDto?     TeamStandings { get; set; }

    public class H2HDto
    {
        public List<H2HMatchDto>? Matches { get; set; }
    }
    public H2HDto? MatchDataBetween { get; set; }

    public List<BarChartDto>? BarCharts { get; set; }
}
