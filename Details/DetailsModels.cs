// Details/DetailsModels.cs
using System;

namespace DataSvc.Details
{
    public record DetailsPayload(
        string? TeamsInfoHtml,
        string? MatchBetweenHtml,
    	string? TeamMatchesSeparateHtml, // NEW
        string? LastTeamsMatchesHtml,
        string? TeamsStatisticsHtml,
        string? TeamsBetStatisticsHtml,
    	string? FactsHtml, // <— NEW (nullable for backward compat)
    	string? TeamStandingsHtml // NEW
    );
    
    public record DetailsRecord(string Href, DateTimeOffset LastUpdatedUtc, DetailsPayload Payload);
}
