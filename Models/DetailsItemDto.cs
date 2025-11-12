namespace DataSvc.Models;

public sealed class DetailsItemDto
{
    public string? Href { get; set; }
    public DateTimeOffset LastUpdatedUtc { get; set; }

    // Teams info (typed or HTML)
    public List<TeamBasicInfoDto>? TeamsInfo { get; set; }   // ← was TeamsInfoDto
    public string? TeamsInfoHtml { get; set; }

    // Matches between
    public MatchBetweenDto? MatchDataBetween { get; set; }
    public string? MatchBetweenHtml { get; set; }

    // Per-team recent matches (separate)
    public MatchDataSeparateDto? RecentMatchesSeparate { get; set; }
    public string? RecentMatchesSeparateHtml { get; set; }

    // Bar charts (projected as {title, halfContainerId, items})
    public List<BarChartDto>? BarCharts { get; set; }
    public string? TeamsBetStatisticsHtml { get; set; }

    // Facts
    public List<MatchFactDto>? MatchFacts { get; set; }
    public string? FactsHtml { get; set; }

    // Winrate (named rows with 2 columns: [team1, team2])
    public WinrateDto? LastTeamsWinrate { get; set; }
    public string? LastTeamsMatchesHtml { get; set; }

    // Team statistics (two sides, each with fact items)
    public List<TeamStatisticsDto>? TeamsStatistics { get; set; }
    public string? TeamsStatisticsHtml { get; set; }

    // Team standings
    public TeamStandingsTableDto? TeamStandings { get; set; }
    public string? TeamStandingsHtml { get; set; }

    // Chart segments (e.g., for possession)
    public List<ChartSegment>? ChartSegments { get; set; }
}
