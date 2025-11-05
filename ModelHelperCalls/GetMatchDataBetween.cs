// File: ModelHelperCalls/GetMatchDataBetween.cs
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models;

namespace DataSvc.ModelHelperCalls;

public static class MatchBetweenHelper
{
    public static MatchData? GetMatchDataBetween(string htmlContent)
    {
        try
        {
            var matchData = new MatchData();
            var document = new HtmlDocument();
            document.LoadHtml(htmlContent);

            var matchItems = document.DocumentNode.Descendants("div")
                .Where(o => o.GetAttributeValue("class", "") == "matchbtwteams")
                .SelectMany(o => o.Descendants("div").Where(p => p.GetAttributeValue("class", "") == "matchitem"))
                .ToList();
            Debug.WriteLine("Matches: " + matchItems.Count);

            if (matchItems != null)
            {
                foreach (var item in matchItems)
                {
                    var hostTeamNode  = item.SelectSingleNode(".//div[@class='hostteam']//div[contains(@class, 'name')]");
                    var guestTeamNode = item.SelectSingleNode(".//div[@class='guestteam']//div[contains(@class, 'name')]");

                    var matchItem = new MatchItem
                    {
                        Competition = item.SelectSingleNode(".//div[@class='competition']").InnerText.Trim(),
                        Date        = item.SelectSingleNode(".//div[@class='date']").InnerText.Trim(),
                        HostTeam    = hostTeamNode.InnerText.Trim(),
                        HostGoals   = int.Parse(item.SelectSingleNode(".//div[@class='hostteam']//div[@class='goals']").InnerText.Trim()),
                        GuestTeam   = guestTeamNode.InnerText.Trim(),
                        GuestGoals  = int.Parse(item.SelectSingleNode(".//div[@class='guestteam']//div[@class='goals']").InnerText.Trim())
                    };

                    // REPLACE your current 'if (actions != null) { ... }' block with this:

                    var actions = item.SelectNodes(".//div[contains(@class,'action')]");
                    if (actions != null)
                    {
                        foreach (var action in actions)
                        {
                            // side: detect which team node is present in the action
                            var hostNode = action.SelectSingleNode(".//div[contains(@class,'hostteam')]");
                            var guestNode = action.SelectSingleNode(".//div[contains(@class,'guestteam')]");
                            var side     = hostNode != null ? TeamSide.Host : TeamSide.Guest;
                            var teamNode = hostNode ?? guestNode;
                            if (teamNode == null) continue;
                    
                            // kind: the first child under ".matchaction" holds the marker (e.g., "goal", "ycard", "dycard", "penalty", "rcard")
                            var marker   = teamNode.SelectSingleNode(".//div[contains(@class,'matchaction')]/div[1]");
                            var kind     = ClassToActionKind(marker?.GetAttributeValue("class", "") ?? string.Empty);
                    
                            // minute + player: typical formats "26' Kiessling S." or "45+2' Player Name"
                            var playerRaw = teamNode.SelectSingleNode(".//div[contains(@class,'player')]")?.InnerText?.Trim() ?? string.Empty;
                            var (minute, player) = ParseMinuteAndPlayer(playerRaw);
                    
                            // build your new strongly-typed action
                            var matchAction = new MatchAction(side, kind, minute, player);
                            matchItem.HalfTime = ParseHalfTime(item);
                    
                            matchItem.Actions.Add(matchAction);
                        }
                    }


                    matchData.Matches.Add(matchItem);
                }
            }

            return matchData;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("GetMatchDataBetween error: " + ex.Message);
            return null;
        }
    }

    private static HalfTimeScore ParseHalfTime(HtmlNode matchItemNode)
    {
        if (matchItemNode == null)
            return new HalfTimeScore(0, 0);
    
        // .details > .info > .holder contains two ".goals" nodes (host, guest)
        var holder = matchItemNode.SelectSingleNode(
            ".//div[contains(@class,'details')]//div[contains(@class,'info')]//div[contains(@class,'holder')]");
    
        if (holder == null)
            return new HalfTimeScore(0, 0);
    
        var goalNodes = holder.SelectNodes(".//div[contains(@class,'goals')]");
        if (goalNodes == null || goalNodes.Count < 2)
            return new HalfTimeScore(0, 0);
    
        int host = ParseIntSafe(goalNodes[0].InnerText);
        int guest = ParseIntSafe(goalNodes[1].InnerText);
    
        return new HalfTimeScore(host, guest);
    }
    
    private static int ParseIntSafe(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        // strip everything except digits (defensive against &nbsp; or stray chars)
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    
    private static ActionKind ClassToActionKind(string cls)
    {
        if (string.IsNullOrWhiteSpace(cls)) return ActionKind.Unknown;
    
        // normalize to be safe with multiple classes
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
        if (string.IsNullOrWhiteSpace(raw)) return (null, string.Empty);
    
        // Matches: 90' Name   |  45+2' Name   |  5'Name
        // Groups: 1=base, 2=extra (optional), 3=player
        var m = System.Text.RegularExpressions.Regex.Match(
            raw, @"^\s*(\d+)(?:\+(\d+))?\s*'\s*(.+?)\s*$");
    
        if (m.Success)
        {
            int baseMin = int.Parse(m.Groups[1].Value);
            int extra   = string.IsNullOrEmpty(m.Groups[2].Value) ? 0 : int.Parse(m.Groups[2].Value);
            var total   = baseMin + extra;
            var player  = m.Groups[3].Value.Trim();
            return (total, player);
        }
    
        // Fallback: no minute parsed, keep raw as player
        return (null, raw.Trim());
    }

}
