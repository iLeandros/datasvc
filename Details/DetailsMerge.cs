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

// Details/DetailsMerge.cs
namespace DataSvc.Details
{
    internal static class DetailsMerge
    {
        static string? PreferOldUnlessNullOrEmpty(string? oldValue, string? newValue)
        {
            if (!string.IsNullOrWhiteSpace(oldValue)) return oldValue;
            if (!string.IsNullOrWhiteSpace(newValue)) return newValue;
            return null;
        }
    
        public static DetailsRecord Merge(DetailsRecord? oldRec, DetailsRecord newRec)
        {
            // If we had nothing before, just take the fresh record as-is.
            if (oldRec is null) return newRec;
    
            var pOld = oldRec.Payload;
            var pNew = newRec.Payload;
    
            // Merge with your “prefer old unless empty” rule
            var mergedPayload = new DetailsPayload(
                PreferOldUnlessNullOrEmpty(pOld.TeamsInfoHtml,           pNew.TeamsInfoHtml),
                PreferOldUnlessNullOrEmpty(pOld.MatchBetweenHtml,        pNew.MatchBetweenHtml),
                PreferOldUnlessNullOrEmpty(pOld.TeamMatchesSeparateHtml, pNew.TeamMatchesSeparateHtml),
                PreferOldUnlessNullOrEmpty(pOld.LastTeamsMatchesHtml,    pNew.LastTeamsMatchesHtml),
                PreferOldUnlessNullOrEmpty(pOld.TeamsStatisticsHtml,     pNew.TeamsStatisticsHtml),
                PreferOldUnlessNullOrEmpty(pOld.TeamsBetStatisticsHtml,  pNew.TeamsBetStatisticsHtml),
                PreferOldUnlessNullOrEmpty(pOld.FactsHtml,               pNew.FactsHtml),
                PreferOldUnlessNullOrEmpty(pOld.TeamStandingsHtml,       pNew.TeamStandingsHtml)
            );
    
            // No-op? Keep the existing record (preserves LastUpdatedUtc).
            if (mergedPayload == pOld) return oldRec;
    
            // Real change: keep the merged payload, and use the fresh scrape’s timestamp.
            // (newRec.LastUpdatedUtc is set where the record is scraped.)
            return new DetailsRecord(newRec.Href, newRec.LastUpdatedUtc, mergedPayload);
        }
    }
}
