// File: ModelHelperCalls/BarChartsParser.cs
using System.Collections.ObjectModel;
using System.Diagnostics;
using HtmlAgilityPack;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using DataSvc.Models; // BarChart, MatchFactData

namespace DataSvc.ModelHelperCalls;

// ---------- LiveScores: HTML parser ----------
public static class LiveScoresParser
{
    private const string ActionsUrl = "https://www.statarea.com/actions/controller/";
    /// <summary>
    /// Parse one day of livescores HTML into a LiveScoreDay (dateIso = "yyyy-MM-dd").
    /// Expects the records LiveScoreItem, LiveScoreGroup, LiveScoreDay to already exist.
    /// </summary>
    public static LiveScoreDay ParseDay(string html, string dateIso)
    {
        var doc = new HtmlDocument();
        //var htmlDesirialized = html.Replace("\\u003C", "<").Replace("\\u003E", ">");
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
            var matches = ParseMatchesFromScope(chosen ?? doc.DocumentNode, dateIso);
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
            var matches = ParseMatchesFromScope(body, dateIso);

            // Only add groups that have a name or at least 1 match (to avoid empty noise)
            if (matches.Count > 0 || !string.IsNullOrWhiteSpace(compName))
                groups.Add(new LiveScoreGroup(string.IsNullOrWhiteSpace(compName) ? "Unnamed competition" : compName, matches));
        }

        return new LiveScoreDay(dateIso, groups);
    }

    // ----------------- helpers -----------------
   private static List<LiveScoreItem> ParseMatchesFromScope(HtmlNode scope, string dateIso)
    {
        var list = new List<LiveScoreItem>();
    
        var matchNodes = scope.SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' match ')]")
                         ?? new HtmlNodeCollection(null);
    
        foreach (var m in matchNodes)
        {
            // NEW: get match id from <div class="match" id="1455348">
            var matchId = m.GetAttributeValue("id", string.Empty).Trim();
    
            // time & status live in .startblock
            var time   = Clean(m.SelectSingleNode(".//*[contains(@class,'startblock')]//*[contains(@class,'time')]"));
            var status = Clean(m.SelectSingleNode(".//*[contains(@class,'startblock')]//*[contains(@class,'status')]"));
    
            // home
            var homeNameNode  = m.SelectSingleNode(".//*[contains(@class,'teams')]//*[contains(@class,'hostteam')]//*[contains(@class,'name')]");
            var homeGoalsNode = m.SelectSingleNode(".//*[contains(@class,'teams')]//*[contains(@class,'hostteam')]//*[contains(@class,'goals')]");
            var homeName      = Clean(homeNameNode);
            var homeGoals     = Clean(homeGoalsNode);
    
            // away
            var awayNameNode  = m.SelectSingleNode(".//*[contains(@class,'teams')]//*[contains(@class,'guestteam')]//*[contains(@class,'name')]");
            var awayGoalsNode = m.SelectSingleNode(".//*[contains(@class,'teams')]//*[contains(@class,'guestteam')]//*[contains(@class,'goals')]");
            var awayName      = Clean(awayNameNode);
            var awayGoals     = Clean(awayGoalsNode);
    
            var actionsList = new List<MatchAction>();
    
            // ---- 1) ORIGINAL LOGIC: parse any inline .matchactions on the page ----
    
            var actionsRoot = m.SelectSingleNode(
                ".//div[contains(concat(' ', normalize-space(@class), ' '), ' matchactions ')]"
            );
    
            var actionNodes = actionsRoot?
                .SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' action ')]")
                ?? new HtmlNodeCollection(null);
    
            foreach (var a in actionNodes)
            {
                // Player text
                var playerNode = a.SelectSingleNode(
                    ".//*[contains(concat(' ', normalize-space(@class), ' '), ' player ')]"
                );
                var raw = Normalize(playerNode?.InnerText ?? string.Empty);
    
                // Side
                var side = SideFromAction(a);
    
                // Icon → kind
                var iconNode = a.SelectSingleNode(".//div[contains(@class,'matchaction')]/div");
                var classStr = (iconNode?.GetAttributeValue("class", "") ?? "").ToLowerInvariant();
                var kind = ClassToActionKind(classStr);
    
                // Minute + player name
                var (minute, player) = ParseMinuteAndPlayer(raw);
                if (string.IsNullOrWhiteSpace(player) && !minute.HasValue) continue;
    
                actionsList.Add(new MatchAction(side, kind, minute, player));
            }
    
            // ---- 2) NEW: if no actions yet, hit the Ajax endpoint and parse that snippet ----
    
            if (actionsList.Count == 0 && !string.IsNullOrEmpty(matchId))
            {
                try
                {
                    var ajaxHtml = FetchMatchActionsHtml(matchId);
    
                    if (!string.IsNullOrWhiteSpace(ajaxHtml))
                    {
                        var ajaxDoc = new HtmlDocument();
                        ajaxDoc.LoadHtml(ajaxHtml);
    
                        // The snippet you got with curl is already a bunch of <div class='action'> elements,
                        // sometimes with a leading <div class='info'>, <div class='teamtitle'> etc.
                        // So we can just treat the whole document node as the root and scan for .action.
                        var ajaxNodes = ajaxDoc.DocumentNode
                            .SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' action ')]")
                            ?? new HtmlNodeCollection(null);
    
                        foreach (var a in ajaxNodes)
                        {
                            var playerNode = a.SelectSingleNode(
                                ".//*[contains(concat(' ', normalize-space(@class), ' '), ' player ')]"
                            );
                            var raw = Normalize(playerNode?.InnerText ?? string.Empty);
    
                            var side = SideFromAction(a);
    
                            var iconNode = a.SelectSingleNode(".//div[contains(@class,'matchaction')]/div");
                            var classStr = (iconNode?.GetAttributeValue("class", "") ?? "").ToLowerInvariant();
                            var kind = ClassToActionKind(classStr);
    
                            var (minute, player) = ParseMinuteAndPlayer(raw);
                            if (string.IsNullOrWhiteSpace(player) && !minute.HasValue) continue;
    
                            actionsList.Add(new MatchAction(side, kind, minute, player));
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Use Console.WriteLine so it shows up in `journalctl -u datasvc`
                    Console.WriteLine($"[LiveScoresParser] Failed to fetch actions for match {matchId}: {ex}");
                }
            }
    
            // ---- remove or keep your debug lines as you like ----
            actionsList.Add(new MatchAction(TeamSide.Host, ActionKind.Unknown, actionNodes.Count, matchId));
            // actionsList.Add(new MatchAction(TeamSide.Host, ActionKind.Unknown, matchNodes.Count, actionsRoot?.InnerHtml ?? ""));
    
            list.Add(new LiveScoreItem(
                time,
                status,
                homeName,
                homeGoals,
                awayGoals,
                awayName,
                actionsList
            ));
        }
    
        return list;
    }

    /// <summary>
    /// Fetches the HTML snippet for the livescore actions of a single match.
    /// </summary>
    private static string FetchMatchActionsHtml(string matchId)
    {
        // This mirrors the curl you just ran:
        // POST https://www.statarea.com/actions/controller/
        // Content-Type: application/x-www-form-urlencoded; charset=UTF-8
        // body: object={"action":"getLivescoreMatchActions","matchid":"1455348"}
    
        using var http = new HttpClient();
    
        var payloadObject = new
        {
            action  = "getLivescoreMatchActions",
            matchid = matchId
        };
    
        var json = JsonSerializer.Serialize(payloadObject);
    
        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("object", json),
        });
    
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.statarea.com/actions/controller/")
        {
            Content = form
        };
    
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("*/*");
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/142.0.0.0 Safari/537.36");
    
        // Optional; you can pass a date if you want the referrer to match
        // req.Headers.Referrer = new Uri($"https://www.statarea.com/livescore/date/{dateIso}/");
    
        var resp = http.SendAsync(req).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
    
        var html = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return html;
    }
    
    private static TeamSide SideFromAction(HtmlNode actionNode)
        {
            // action → (hostteam|guestteam) → ...
            if (actionNode.SelectSingleNode(".//div[contains(@class,'guestteam')]") != null)
                return TeamSide.Guest;
            if (actionNode.SelectSingleNode(".//div[contains(@class,'hostteam')]") != null)
                return TeamSide.Host;
        
            // Fallback (shouldn’t hit with your structure)
            return TeamSide.Host;
        }
        
        private static string Normalize(string s)
        {
            if (s == null) return string.Empty;
            // Html decode, replace NBSP (char + entity), collapse spaces, trim
            var decoded = HtmlEntity.DeEntitize(s)
                .Replace('\u00A0', ' ')
                .Replace("&nbsp;", " ");
            return System.Text.RegularExpressions.Regex.Replace(decoded, @"\s+", " ").Trim();
        }

        private static TeamSide InferSide(HtmlNode node, string classString)
        {
            // Prefer explicit cue in class name
            if (classString.Contains("host"))  return TeamSide.Host;
            if (classString.Contains("guest")) return TeamSide.Guest;

            // Then, look at ancestors
            var p = node.ParentNode;
            while (p != null)
            {
                var c = p.GetAttributeValue("class", string.Empty).ToLowerInvariant();
                if (c.Contains("hostteam") || c.Contains("host"))  return TeamSide.Host;
                if (c.Contains("guestteam") || c.Contains("guest")) return TeamSide.Guest;
                p = p.ParentNode;
            }
            // Fallback
            return TeamSide.Host;
        }

        private static HalfTimeScore ParseHalfTime(HtmlNode matchItemNode)
        {
            // 1) Classic "ht/halftime" holder
            var holder = matchItemNode.SelectSingleNode(
                ".//div[contains(@class,'halftime') or contains(@class,'half-time') or contains(@class,'ht')]"
            );
            if (holder != null)
            {
                var nodes = holder.SelectNodes(".//div[contains(@class,'goals') or contains(@class,'score')]");
                if (nodes != null && nodes.Count >= 2)
                    return new HalfTimeScore(ParseIntSafe(nodes[0].InnerText), ParseIntSafe(nodes[1].InnerText));
        
                var text = Normalize(holder.InnerText);
                var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\s*[-:]\s*(\d+)");
                if (m.Success) return new HalfTimeScore(ParseIntSafe(m.Groups[1].Value), ParseIntSafe(m.Groups[2].Value));
            }
        
            // 2) The Statarea "details > info > holder > goals goals" pattern  ← your sample
            var infoHolderGoals = matchItemNode.SelectNodes(".//div[@class='details']//div[@class='info']//div[@class='holder']/div[@class='goals']");
            if (infoHolderGoals != null && infoHolderGoals.Count >= 2)
            {
                return new HalfTimeScore(ParseIntSafe(infoHolderGoals[0].InnerText), ParseIntSafe(infoHolderGoals[1].InnerText));
            }
        
            // 3) Try to read from a "score/result" text that includes 'HT x-y'
            var scoreTextNode = matchItemNode.SelectSingleNode(".//div[contains(@class,'score') or contains(@class,'result')]");
            var scoreText = Normalize(scoreTextNode?.InnerText ?? "");
            if (!string.IsNullOrWhiteSpace(scoreText))
            {
                var m2 = System.Text.RegularExpressions.Regex.Match(scoreText, @"HT\s*[:\-]?\s*(\d+)\s*[-:]\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m2.Success)
                    return new HalfTimeScore(ParseIntSafe(m2.Groups[1].Value), ParseIntSafe(m2.Groups[2].Value));
            }
        
            return new HalfTimeScore(0, 0);
        }


        private static ActionKind ClassToActionKind(string cls)
        {
            if (string.IsNullOrWhiteSpace(cls)) return ActionKind.Unknown;
            var c = cls.ToLowerInvariant();
            if (c.Contains("goal"))    return ActionKind.Goal;
            if (c.Contains("penalty")) return ActionKind.Penalty;
            if (c.Contains("dycard"))  return ActionKind.SecondYellow;
            if (c.Contains("ycard"))   return ActionKind.YellowCard;
            if (c.Contains("rcard"))   return ActionKind.RedCard;
            return ActionKind.Unknown;
        }

        private static (int? minute, string player) ParseMinuteAndPlayer(string raw)
        {
            var text = Normalize(raw);
        
            // Accept 45', 45 + 2', 45+2'
            var m = System.Text.RegularExpressions.Regex.Match(
                text, @"^\s*(\d+)(?:\s*\+\s*(\d+))?\s*'\s*(.+?)\s*$"
            );
        
            if (m.Success)
            {
                int baseMin = int.Parse(m.Groups[1].Value);
                int extra   = string.IsNullOrEmpty(m.Groups[2].Value) ? 0 : int.Parse(m.Groups[2].Value);
                return (baseMin + extra, m.Groups[3].Value.Trim());
            }
        
            // If it looks like "19'Name" without a space, handle that too
            m = System.Text.RegularExpressions.Regex.Match(
                text, @"^\s*(\d+)(?:\s*\+\s*(\d+))?\s*'(.+?)\s*$"
            );
            if (m.Success)
            {
                int baseMin = int.Parse(m.Groups[1].Value);
                int extra   = string.IsNullOrEmpty(m.Groups[2].Value) ? 0 : int.Parse(m.Groups[2].Value);
                return (baseMin + extra, m.Groups[3].Value.Trim());
            }
        
            // Last resort: no minute parsed; return as player
            return (null, text);
        }


        private static int ParseIntSafe(string? s)
        {
            if (int.TryParse((s ?? string.Empty).Trim(), out var v)) return v;
            return 0;
        }

        private static string? TryGetInnerText(HtmlNode root, string xpath)
        {
            var n = root.SelectSingleNode(xpath);
            return n?.InnerText?.Trim();
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
