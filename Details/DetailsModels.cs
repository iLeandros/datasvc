// Details/DetailsModels.cs
using System;

using DataSvc.Models;
using DataSvc.ModelHelperCalls;
using DataSvc.VIPHandler;
using DataSvc.Auth; // AuthController + SessionAuthHandler namespace
using DataSvc.MainHelpers; // MainHelpers
using DataSvc.Likes; // MainHelpers
using DataSvc.Services; // Services
using DataSvc.Analyzer;
using DataSvc.ClubElo;
using DataSvc.MainHelpers;
using DataSvc.Parsed;
using DataSvc.Details;
using DataSvc.LiveScores;

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
