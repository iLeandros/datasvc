// File: ModelHelperCalls/BarChartsParser.cs
using System.Collections.ObjectModel;
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models; // BarChart, MatchFactData

namespace DataSvc.ModelHelperCalls;

// ---------- LiveScores: HTML parser ----------
public static class LiveScoresParser
{
    /// <summary>
    /// Parse one day of livescores HTML into a LiveScoreDay (dateIso = "yyyy-MM-dd").
    /// Expects the records LiveScoreItem, LiveScoreGroup, LiveScoreDay to already exist.
    /// </summary>
    public static LiveScoreDay ParseDay(string html, string dateIso)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var groups = new List<LiveScoreGroup>();

        // 1) Find all livescore blocks; choose the one that actually contains the most matches.
        var liveBlocks = doc.DocumentNode
            .SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' livescore ')]")
            ?? new HtmlNodeCollection(null);

        HtmlNode? chosen = null;
        int bestCount = -1;

        foreach (var b in liveBlocks)
        {
            // Count matches under: .allmatches > div[id] > .match
            var count = b.SelectNodes(".//div[contains(@class,'allmatches')]//div[@id]//div[contains(@class,'match')]")?.Count ?? 0;
            if (count > bestCount)
            {
                bestCount = count;
                chosen = b;
            }
        }

        // Fallbacks if the structure differs:
        chosen ??= doc.DocumentNode.SelectSingleNode("//*[contains(@class,'allmatches')]")
                    ?.AncestorsAndSelf().FirstOrDefault(n =>
                        n.GetClasses().Contains("livescore"));

        // If still nothing, try to parse directly from the whole document (very defensive).
        var allMatchesContainer = chosen?
            .SelectSingleNode(".//div[contains(concat(' ', normalize-space(@class), ' '), ' allmatches ')]")
            ?? doc.DocumentNode.SelectSingleNode("//*[contains(@class,'allmatches')]");

        // 2) Find competition containers (divs with id). If none, we’ll parse matches directly.
        var compNodes = allMatchesContainer?
            .SelectNodes("./div[@id]")?.ToList()
            ?? allMatchesContainer?.SelectNodes(".//div[@id]")?.ToList()
            ?? new List<HtmlNode>();

        if (compNodes.Count == 0)
        {
            // No competition wrappers — parse any matches directly under the chosen block.
            var matches = ParseMatchesFromScope(chosen ?? doc.DocumentNode);
            if (matches.Count > 0)
                groups.Add(new LiveScoreGroup("All matches", matches));

            return new LiveScoreDay(dateIso, groups);
        }

        // 3) Parse each competition group.
        foreach (var comp in compNodes)
        {
            // Competition name is usually in header .name
            var compName = Clean(
                comp.SelectSingleNode(".//*[contains(@class,'header')]//*[contains(@class,'name')]")
                 ?? comp.SelectSingleNode(".//*[contains(@class,'name')]")
            );

            // Matches are usually inside a .body container, but sometimes directly under comp.
            var body = comp.SelectSingleNode(".//div[contains(@class,'body')]") ?? comp;
            var matches = ParseMatchesFromScope(body);

            // Only add groups that have a name or at least 1 match (to avoid empty noise)
            if (matches.Count > 0 || !string.IsNullOrWhiteSpace(compName))
                groups.Add(new LiveScoreGroup(string.IsNullOrWhiteSpace(compName) ? "Unnamed competition" : compName, matches));
        }

        return new LiveScoreDay(dateIso, groups);
    }

    // ----------------- helpers -----------------

    private static List<LiveScoreItem> ParseMatchesFromScope(HtmlNode scope)
    {
        var list = new List<LiveScoreItem>();

        var matchNodes = scope.SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' match ')]")
                         ?? new HtmlNodeCollection(null);

        foreach (var m in matchNodes)
        {
            // time & status live in .startblock
            var time   = Clean(m.SelectSingleNode(".//*[contains(@class,'startblock')]//*[contains(@class,'time')]"));
            var status = Clean(m.SelectSingleNode(".//*[contains(@class,'startblock')]//*[contains(@class,'status')]"));

            // home
            var homeNameNode  = m.SelectSingleNode(".//*[contains(@class,'teams')]//*[contains(@class,'hostteam')]//*[contains(@class,'name')]");
            var homeGoalsNode = m.SelectSingleNode(".//*[contains(@class,'teams')]//*[contains(@class,'hostteam')]//*[contains(@class,'goals')]");
            //var homeName  = NormalizeTeam(Clean(homeNameNode));
            var homeName  = Clean(homeNameNode);
            var homeGoals = Clean(homeGoalsNode);

            // away
            var awayNameNode  = m.SelectSingleNode(".//*[contains(@class,'teams')]//*[contains(@class,'guestteam')]//*[contains(@class,'name')]");
            var awayGoalsNode = m.SelectSingleNode(".//*[contains(@class,'teams')]//*[contains(@class,'guestteam')]//*[contains(@class,'goals')]");
            //var awayName  = NormalizeTeam(Clean(awayNameNode));
            var awayName  = Clean(awayNameNode);
            var awayGoals = Clean(awayGoalsNode);

            var Actions     = new List<MatchAction>();

            // Actions inside this matchitem (top-level .action blocks)
            var actions = item.SelectNodes(".//div[contains(concat(' ',normalize-space(@class),' '),' action ')]");
            if (actions != null)
            {
                foreach (var a in actions)
                {
                    // Player text (only from .player node)
                    var playerNode = a.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' player ')]");
                    var raw = Normalize(playerNode?.InnerText ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(raw)) continue; // skip icon-only rows
                  
                    // Infer side from nearest host/guest container
                    var side = SideFromAction(a);
                   
                    // Kind from the icon classes
                    var icon = a.SelectSingleNode(".//div[contains(@class,'matchaction')]/div");
                    var classStr = (icon?.GetAttributeValue("class", "") ?? "").ToLowerInvariant();
                    var kind = ClassToActionKind(classStr);
                 
                    // "45+2' Player Name" → (47, "Player Name")
                    var (minute, player) = ParseMinuteAndPlayer(raw);
                    if (string.IsNullOrWhiteSpace(player) && !minute.HasValue) continue; // still nothing? skip
                 
                    //matchData.Matches.Last().Actions.Add(new MatchAction(side, kind, minute, player));
                    matchItem.Actions.Add(new MatchAction(side, kind, minute, player));

                }
            }

            list.Add(new LiveScoreItem(
                time,
                status,
                homeName,
                homeGoals,
                awayGoals,
                awayName,
                Actions
            ));
        }

        return list;
    }

    private static string Clean(HtmlNode? node)
        => HtmlEntity.DeEntitize(node?.InnerText ?? string.Empty).Trim();

    private static string NormalizeTeam2(string? name)
    {
        var n = (name ?? string.Empty).Trim();
        try
        {
            // Use your existing helper for display normalization.
            return n;
            //return DataSvc.ModelHelperCalls.renameTeam.renameTeamNameToFitDisplayLabel(n);
        }
        catch
        {
            return n;
        }
    }
}
