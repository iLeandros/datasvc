// File: ModelHelperCalls/BarChartsParser.cs
using System.Collections.ObjectModel;
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models; // BarChart, MatchFactData

namespace DataSvc.ModelHelperCalls;

// ---------- LiveScores: HTML parser ----------
public static class LiveScoresParser
{
    public static LiveScoreDay ParseDay(string html, string dateIso)
    {
        var website = new HtmlAgilityPack.HtmlDocument();
        website.LoadHtml(html);

        var groups = new List<LiveScoreGroup>();

        // livescore container → ".allmatches" → competitions by id
        var container = website.DocumentNode
            .SelectSingleNode("//*[contains(concat(' ', normalize-space(@class), ' '), ' livescore ')]");

        var allMatches = container?
            .SelectSingleNode(".//*[contains(concat(' ', normalize-space(@class), ' '), ' allmatches ')]");

        var comps = allMatches?
            .Elements("div")
            .Where(d => d.Attributes["id"] != null)
            .ToList() ?? new List<HtmlAgilityPack.HtmlNode>();

        foreach (var comp in comps)
        {
            var compName = HtmlEntity.DeEntitize(
                comp.SelectSingleNode(".//*[contains(@class,'header')]//*[contains(@class,'name')]")?.InnerText ?? ""
            ).Trim();

            var matchNodes = comp
                .SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' match ')]")
                ?? new HtmlAgilityPack.HtmlNodeCollection(null);

            var matches = new List<LiveScoreItem>();

            foreach (var m in matchNodes)
            {
                var time   = m.SelectSingleNode(".//*[contains(@class,'startblock')]//*[contains(@class,'time')]")?.InnerText ?? "";
                var status = m.SelectSingleNode(".//*[contains(@class,'startblock')]//*[contains(@class,'status')]")?.InnerText ?? "";

                var homeName  = m.SelectSingleNode(".//*[contains(@class,'teams')]//*[contains(@class,'hostteam')]//*[contains(@class,'name')]")?.InnerText ?? "";
                var homeGoals = m.SelectSingleNode(".//*[contains(@class,'teams')]//*[contains(@class,'hostteam')]//*[contains(@class,'goals')]")?.InnerText ?? "";

                var awayName  = m.SelectSingleNode(".//*[contains(@class,'teams')]//*[contains(@class,'guestteam')]//*[contains(@class,'name')]")?.InnerText ?? "";
                var awayGoals = m.SelectSingleNode(".//*[contains(@class,'teams')]//*[contains(@class,'guestteam')]//*[contains(@class,'goals')]")?.InnerText ?? "";

                // Normalize display names using your helper
                homeName = DataSvc.ModelHelperCalls.renameTeam.renameTeamNameToFitDisplayLabel(homeName ?? string.Empty);
                awayName = DataSvc.ModelHelperCalls.renameTeam.renameTeamNameToFitDisplayLabel(awayName ?? string.Empty);

                matches.Add(new LiveScoreItem(
                    time?.Trim() ?? "", status?.Trim() ?? "",
                    homeName?.Trim() ?? "", homeGoals?.Trim() ?? "",
                    awayGoals?.Trim() ?? "", awayName?.Trim() ?? ""
                ));
            }

            if (!string.IsNullOrWhiteSpace(compName))
                groups.Add(new LiveScoreGroup(compName, matches));
        }

        return new LiveScoreDay(dateIso, groups);
    }
}
