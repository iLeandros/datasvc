// AllhrefsMapper.cs
using System.Linq;
using DataSvc.ModelHelperCalls;
using DataSvc.Models;

namespace DataSvc.MainHelpers;

public static class AllhrefsMapper
{
    public static object MapDetailsRecordToAllhrefsItem(
        DetailsRecord i,
        bool preferTeamsInfoHtml,
        bool preferMatchBetweenHtml,
        bool preferSeparateMatchesHtml,
        bool preferBetStatsHtml,
        bool preferFactsHtml,
        bool preferLastTeamsHtml,
        bool preferTeamsStatisticsHtml,
        bool preferTeamStandingsHtml)
    {
        var p = i.Payload ?? new DetailsPayload(
                null, // TeamsInfoHtml
                null, // MatchBetweenHtml
                null, // TeamMatchesSeparateHtml
                null, // TeamsBetStatisticsHtml
                null, // FactsHtml
                null, // LastTeamsMatchesHtml
                null, // TeamsStatisticsHtml
                null  // TeamStandingsHtml
            );

        var parsedTeamsInfo = preferTeamsInfoHtml ? null
            : TeamsInfoParser.Parse(p.TeamsInfoHtml);

        var matchDataBetween = preferMatchBetweenHtml ? null
            : MatchBetweenHelper.GetMatchDataBetween(p.MatchBetweenHtml ?? string.Empty);

        var recentMatchesSeparate = preferSeparateMatchesHtml ? null
            : MatchSeparatelyHelper.GetMatchDataSeparately(p.TeamMatchesSeparateHtml ?? string.Empty);

        var rawBarCharts = preferBetStatsHtml ? null
            : BarChartsParser.GetBarChartsData(p.TeamsBetStatisticsHtml ?? string.Empty);

        var barCharts = rawBarCharts?.Select(b => new
        {
            title = b.Title,
            halfContainerId = b.HalfContainerId,
            items = b.ToList()
        }).ToList();

        var matchFacts = preferFactsHtml ? null
            : MatchFactsParser.GetMatchFacts(p.FactsHtml);

        object? lastTeamsWinrate = null;
        if (!preferLastTeamsHtml)
        {
            var m = LastTeamsMatchesHelper.GetQuickTableWinratePercentagesFromSeperateTeams(p.LastTeamsMatchesHtml ?? string.Empty);
            lastTeamsWinrate = new
            {
                wins   = new[] { m[0, 0], m[0, 1] },
                draws  = new[] { m[1, 0], m[1, 1] },
                losses = new[] { m[2, 0], m[2, 1] }
            };
        }

        var teamsStats = preferTeamsStatisticsHtml ? null
            : GetTeamStatisticsHelper.GetTeamsStatistics(p.TeamsStatisticsHtml ?? string.Empty);

        var teamStandingsParsed = preferTeamStandingsHtml ? null
            : TeamStandingsHelper.GetTeamStandings(p.TeamStandingsHtml ?? string.Empty);

        return new
        {
            href           = i.Href,
            lastUpdatedUtc = i.LastUpdatedUtc,

            teamsInfo     = parsedTeamsInfo,
            teamsInfoHtml = preferTeamsInfoHtml ? p.TeamsInfoHtml : null,

            matchDataBetween = matchDataBetween,
            matchBetweenHtml = preferMatchBetweenHtml ? p.MatchBetweenHtml : null,

            recentMatchesSeparate     = recentMatchesSeparate,
            recentMatchesSeparateHtml = preferSeparateMatchesHtml ? p.TeamMatchesSeparateHtml : null,

            barCharts              = barCharts,
            teamsBetStatisticsHtml = preferBetStatsHtml ? p.TeamsBetStatisticsHtml : null,

            matchFacts = matchFacts,
            factsHtml  = preferFactsHtml ? p.FactsHtml : null,

            lastTeamsWinrate     = lastTeamsWinrate,
            lastTeamsMatchesHtml = preferLastTeamsHtml ? p.LastTeamsMatchesHtml : null,

            teamsStatistics     = teamsStats,
            teamsStatisticsHtml = preferTeamsStatisticsHtml ? p.TeamsStatisticsHtml : null,

            teamStandings     = teamStandingsParsed,
            teamStandingsHtml = preferTeamStandingsHtml ? p.TeamStandingsHtml : null
        };
    }
    
    public static DetailsItemDto MapDetailsRecordToDetailsItemDto(DetailsRecord rec)
    {
        var p = rec.Payload ?? new DetailsPayload(
            null, null, null, null, null, null, null, null
        );

        var teamsInfo      = TeamsInfoParserDto.Parse(p.TeamsInfoHtml);
        var matchBetween   = MatchBetweenHelper.GetMatchDataBetween(p.MatchBetweenHtml ?? string.Empty);
        var recentSeparate = MatchSeparatelyHelper.GetMatchDataSeparately(p.TeamMatchesSeparateHtml ?? string.Empty);

        var rawBarCharts = BarChartsParser.GetBarChartsData(p.TeamsBetStatisticsHtml ?? string.Empty);
        var barCharts = rawBarCharts?.Select(b => new BarChartDto
        {
            Title = b.Title,
            HalfContainerId = b.HalfContainerId,
            Items = b.ToList()
        }).ToList();

        var matchFacts = MatchFactsParser.GetMatchFacts(p.FactsHtml);

        var win = LastTeamsMatchesHelper.GetQuickTableWinratePercentagesFromSeperateTeams(p.LastTeamsMatchesHtml ?? string.Empty);
        var lastTeamsWinrate = (win is null)
            ? null
            : new WinrateDto
              {
                  Wins   = new[] { win[0,0], win[0,1] },
                  Draws  = new[] { win[1,0], win[1,1] },
                  Losses = new[] { win[2,0], win[2,1] }
              };

        var teamStats  = GetTeamStatisticsHelper.GetTeamsStatistics(p.TeamsStatisticsHtml ?? string.Empty);
        var standings  = TeamStandingsHelper.GetTeamStandings(p.TeamStandingsHtml ?? string.Empty);

        return new DetailsItemDto
        {
            Href           = rec.Href,
            LastUpdatedUtc = rec.LastUpdatedUtc,

            TeamsInfo      = teamsInfo,
            TeamsInfoHtml  = null,

            MatchDataBetween = matchBetween,
            MatchBetweenHtml = null,

            RecentMatchesSeparate     = recentSeparate,
            RecentMatchesSeparateHtml = null,

            BarCharts              = barCharts,
            TeamsBetStatisticsHtml = null,

            MatchFacts = matchFacts,
            FactsHtml  = null,

            LastTeamsWinrate     = lastTeamsWinrate,
            LastTeamsMatchesHtml = null,

            TeamsStatistics     = teamStats,
            TeamsStatisticsHtml = null,

            TeamStandings     = standings,
            TeamStandingsHtml = null
        };
    }
}
