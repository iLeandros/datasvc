// Details/DetailsMerge.cs
using DataSvc.Models;

namespace DataSvc.Details;

internal static class DetailsMerge
{
    /// <summary>
    /// Returns the newer (non-empty) value, falling back to the old one.
    /// FIX H10: was "prefer old unless empty", which permanently froze cached data.
    /// Now correctly prefers the fresh value so updates from re-scraping are applied.
    /// </summary>
    private static string? PreferNewUnlessEmpty(string? oldValue, string? newValue)
    {
        if (!string.IsNullOrWhiteSpace(newValue)) return newValue;
        if (!string.IsNullOrWhiteSpace(oldValue)) return oldValue;
        return null;
    }

    public static DetailsRecord Merge(DetailsRecord? oldRec, DetailsRecord newRec)
    {
        // Nothing cached yet — use the fresh record directly.
        if (oldRec is null) return newRec;

        var pOld = oldRec.Payload;
        var pNew = newRec.Payload;

        var mergedPayload = new DetailsPayload(
            PreferNewUnlessEmpty(pOld.TeamsInfoHtml,           pNew.TeamsInfoHtml),
            PreferNewUnlessEmpty(pOld.MatchBetweenHtml,        pNew.MatchBetweenHtml),
            PreferNewUnlessEmpty(pOld.TeamMatchesSeparateHtml, pNew.TeamMatchesSeparateHtml),
            PreferNewUnlessEmpty(pOld.LastTeamsMatchesHtml,    pNew.LastTeamsMatchesHtml),
            PreferNewUnlessEmpty(pOld.TeamsStatisticsHtml,     pNew.TeamsStatisticsHtml),
            PreferNewUnlessEmpty(pOld.TeamsBetStatisticsHtml,  pNew.TeamsBetStatisticsHtml),
            PreferNewUnlessEmpty(pOld.FactsHtml,               pNew.FactsHtml),
            PreferNewUnlessEmpty(pOld.TeamStandingsHtml,       pNew.TeamStandingsHtml)
        );

        // No real change — keep the existing record (preserves its LastUpdatedUtc).
        if (mergedPayload == pOld) return oldRec;

        // Payload changed — stamp with the fresh scrape's timestamp.
        return new DetailsRecord(newRec.Href, newRec.LastUpdatedUtc, mergedPayload);
    }
}
