// File: ModelHelperCalls/GetMatchDataSeparately.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HtmlAgilityPack;
using DataSvc.Models;

namespace DataSvc.ModelHelperCalls;

public static class MatchSeparatelyHelper
{
    public static MatchData? GetMatchDataSeparately(string htmlContent)
    {
        try
        {
            var data = new MatchData();
            var doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);

            // All match cards under .lastteamsmatches
            var items = doc.DocumentNode.SelectNodes(
                "//*[contains(concat(' ', normalize-space(@class), ' '), ' lastteamsmatches ')]" +
                "//*[contains(concat(' ', normalize-space(@class), ' '), ' matchitem ')]"
            );

            if (items == null || items.Count == 0) return data;

            string Clean(HtmlNode? n) => HtmlEntity.DeEntitize(n?.InnerText ?? "").Trim();
            int ToInt(HtmlNode? n) => int.TryParse(Clean(n), out var v) ? v : 0;

            foreach (var item in items)
            {
                var mi = new MatchItem
                {
                    Competition = Clean(item.SelectSingleNode(".//*[contains(@class,'competition')]")),
                    Date        = Clean(item.SelectSingleNode(".//*[contains(@class,'date')]")),
                    HostTeam    = Clean(item.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' hostteam ')]//*[contains(concat(' ',normalize-space(@class),' '),' name ')]")),
                    HostGoals   = ToInt(item.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' hostteam ')]//*[contains(concat(' ',normalize-space(@class),' '),' goals ')]")),
                    GuestTeam   = Clean(item.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' guestteam ')]//*[contains(concat(' ',normalize-space(@class),' '),' name ')]")),
                    GuestGoals  = ToInt(item.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' guestteam ')]//*[contains(concat(' ',normalize-space(@class),' '),' goals ')]")),
                    Actions     = new List<MatchAction>(),
                    HalfTime    = new HalfTimeScore(0, 0)
                };

                // Halftime (if available in this match card)
                mi.HalfTime = ParseHalfTime(item);

                // Actions inside this matchitem
                var actions = item.SelectNodes(".//*[contains(concat(' ',normalize-space(@class),' '),' action ')]");
                if (actions != null)
                {
                    foreach (var a in actions)
                    {
                        // Determine side from ancestor classes
                        var sideClass = a.Ancestors("div")
                                         .Select(n => n.GetAttributeValue("class", "").ToLowerInvariant())
                                         .FirstOrDefault(c => c.Contains("hostteam") || c.Contains("guestteam")) ?? "";

                        var side = sideClass.Contains("guestteam")
                            ? TeamSide.Guest
                            : sideClass.Contains("hostteam") ? TeamSide.Host : TeamSide.Host;

                        // Class-to-kind mapping (check the action and its first inner marker)
                        var markerContainer = a.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' matchaction ')]");
                        var firstEl = markerContainer?.ChildNodes.FirstOrDefault(n => n.NodeType == HtmlNodeType.Element);
                        var classStr = (firstEl?.GetAttributeValue("class", "") ?? a.GetAttributeValue("class", "")).ToLowerInvariant();
                        var kind = ClassToActionKind(classStr);

                        // Extract raw like "45+2' Player Name"
                        var playerNode = a.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' player ')]");
                        var raw = Clean(playerNode ?? a);
                        var (minute, player) = ParseMinuteAndPlayer(raw);

                        // Skip empty rows
                        if (string.IsNullOrWhiteSpace(player) && !minute.HasValue) continue;

                        mi.Actions.Add(new MatchAction(side, kind, minute, player));
                    }
                }

                data.Matches.Add(mi);
            }

            return data;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("GetMatchDataSeparately error: " + ex.Message);
            return null;
        }
    }

    // ---------- helpers ----------

    private static HalfTimeScore ParseHalfTime(HtmlNode matchItemNode)
    {
        // Examples supported:
        // <div class="halftime"><div class="goals">1</div><div class="goals">0</div></div>
        // <div class="ht">HT: 1-0</div>
        var holder = matchItemNode.SelectSingleNode(
            ".//div[contains(@class,'halftime') or contains(@class,'half-time') or contains(@class,'ht')]"
        );

        if (holder == null)
        {
            // Try to parse "HT 1-0" from a score/result area
            var scoreTextNode = matchItemNode.SelectSingleNode(".//div[contains(@class,'score') or contains(@class,'result')]");
            var scoreText = HtmlEntity.DeEntitize(scoreTextNode?.InnerText ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(scoreText))
            {
                var m2 = System.Text.RegularExpressions.Regex.Match(
                    scoreText, @"HT\s*[:\-]?\s*(\d+)\s*[-:]\s*(\d+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                if (m2.Success)
                {
                    return new HalfTimeScore(ParseIntSafe(m2.Groups[1].Value), ParseIntSafe(m2.Groups[2].Value));
                }
            }
            return new HalfTimeScore(0, 0);
        }

        // Structured holders
        var goalNodes = holder.SelectNodes(".//div[contains(@class,'goals') or contains(@class,'score')]");
        if (goalNodes != null && goalNodes.Count >= 2)
        {
            var host = ParseIntSafe(goalNodes[0].InnerText);
            var guest = ParseIntSafe(goalNodes[1].InnerText);
            return new HalfTimeScore(host, guest);
        }

        // Try "1-0" inside the holder text
        var text = HtmlEntity.DeEntitize(holder.InnerText ?? "").Trim();
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\s*[-:]\s*(\d+)");
        if (m.Success)
        {
            return new HalfTimeScore(ParseIntSafe(m.Groups[1].Value), ParseIntSafe(m.Groups[2].Value));
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
        if (string.IsNullOrWhiteSpace(raw))
            return (null, string.Empty);

        // Supports: 45' Player, 45+2' Player, 90 + 3' Player
        var m = System.Text.RegularExpressions.Regex.Match(
            raw, @"^\s*(\d+)(?:\s*\+\s*(\d+))?\s*'\s*(.+?)\s*$"
        );

        if (m.Success)
        {
            int baseMin = int.Parse(m.Groups[1].Value);
            int extra   = string.IsNullOrEmpty(m.Groups[2].Value) ? 0 : int.Parse(m.Groups[2].Value);
            var total   = baseMin + extra;
            var player  = m.Groups[3].Value.Trim();
            return (total, player);
        }

        // Fallback: no minute parsed; treat entire string as player name
        return (null, raw.Trim());
    }

    private static int ParseIntSafe(string? s)
    {
        if (int.TryParse((s ?? string.Empty).Trim(), out var v)) return v;
        return 0;
    }
}
