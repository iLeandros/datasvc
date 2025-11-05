// File: ModelHelperCalls/GetMatchDataSeparately.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
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

            string Clean(HtmlNode? n) => Normalize(n?.InnerText ?? "");
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

                // Halftime (robust)
                mi.HalfTime = ParseHalfTime(item);
                // Reconcile if page listed guest-first
                mi.HalfTime = ReconcileHalfTimeWithActions(mi);

                // Actions: parse only from ".player" text; skip blanks
                var actions = item.SelectNodes(".//*[contains(concat(' ',normalize-space(@class),' '),' action ')]");
                if (actions != null)
                {
                    foreach (var a in actions)
                    {
                        // Player text (avoid taking the whole action node to skip icons/&nbsp;)
                        var playerNode = a.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' player ')]");
                        var raw = Normalize(playerNode?.InnerText ?? string.Empty);
                        if (string.IsNullOrWhiteSpace(raw)) continue;

                        // Infer side from nearest host/guest container
                        var sideClass = a.Ancestors("div")
                                         .Select(n => n.GetAttributeValue("class", "").ToLowerInvariant())
                                         .FirstOrDefault(c => c.Contains("hostteam") || c.Contains("guestteam")) ?? "";
                        var side = sideClass.Contains("guestteam")
                            ? TeamSide.Guest
                            : TeamSide.Host;

                        // Kind: look for inner icon class
                        var icon = a.SelectSingleNode(".//div[contains(@class,'matchaction')]/div") 
                                   ?? a.SelectSingleNode(".//*[contains(@class,'matchaction')]");
                        var classStr = (icon?.GetAttributeValue("class", "") ?? a.GetAttributeValue("class", "")).ToLowerInvariant();
                        var kind = ClassToActionKind(classStr);

                        // Minute + player
                        var (minute, player) = ParseMinuteAndPlayer(raw);
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

    private static string Normalize(string s)
    {
        if (s == null) return string.Empty;
        // Html-decode, replace NBSP (char + entity), collapse whitespace
        var decoded = HtmlEntity.DeEntitize(s).Replace('\u00A0', ' ').Replace("&nbsp;", " ");
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static HalfTimeScore ParseHalfTime(HtmlNode matchItemNode)
    {
        // 1) Classic "ht/halftime" holder
        var holder = matchItemNode.SelectSingleNode(
            ".//div[contains(@class,'halftime') or contains(@class,'half-time') or contains(@class,'ht')]"
        );
        if (holder != null)
        {
            var goalNodes = holder.SelectNodes(".//div[contains(@class,'goals') or contains(@class,'score')]");
            if (goalNodes != null && goalNodes.Count >= 2)
                return new HalfTimeScore(ParseIntSafe(goalNodes[0].InnerText), ParseIntSafe(goalNodes[1].InnerText));

            var text = Normalize(holder.InnerText);
            var m = Regex.Match(text, @"(\d+)\s*[-:]\s*(\d+)");
            if (m.Success)
                return new HalfTimeScore(ParseIntSafe(m.Groups[1].Value), ParseIntSafe(m.Groups[2].Value));
        }

        // 2) Statarea-like: details > info > holder > goals goals
        var infoHolderGoals = matchItemNode.SelectNodes(".//div[@class='details']//div[@class='info']//div[@class='holder']/div[@class='goals']");
        if (infoHolderGoals != null && infoHolderGoals.Count >= 2)
        {
            return new HalfTimeScore(ParseIntSafe(infoHolderGoals[0].InnerText), ParseIntSafe(infoHolderGoals[1].InnerText));
        }

        // 3) "HT x-y" inside a score/result text
        var scoreTextNode = matchItemNode.SelectSingleNode(".//div[contains(@class,'score') or contains(@class,'result')]");
        var scoreText = Normalize(scoreTextNode?.InnerText ?? "");
        if (!string.IsNullOrWhiteSpace(scoreText))
        {
            var m2 = Regex.Match(scoreText, @"HT\s*[:\-]?\s*(\d+)\s*[-:]\s*(\d+)", RegexOptions.IgnoreCase);
            if (m2.Success)
                return new HalfTimeScore(ParseIntSafe(m2.Groups[1].Value), ParseIntSafe(m2.Groups[2].Value));
        }

        return new HalfTimeScore(0, 0);
    }

    private static HalfTimeScore ReconcileHalfTimeWithActions(MatchItem mi)
    {
        if (mi == null) return new HalfTimeScore(0, 0);

        // Count first-half goals from actions (<= 50' to include stoppage)
        const int cutoff = 50;
        bool Goalish(ActionKind k) => k == ActionKind.Goal || k == ActionKind.Penalty;

        int fhHost = mi.Actions.Count(a =>
            a.Side == TeamSide.Host && a.Minute.HasValue && a.Minute.Value <= cutoff && Goalish(a.Kind));

        int fhGuest = mi.Actions.Count(a =>
            a.Side == TeamSide.Guest && a.Minute.HasValue && a.Minute.Value <= cutoff && Goalish(a.Kind));

        int parsedHost = mi.HalfTime.Host;
        int parsedGuest = mi.HalfTime.Guest;

        // If totals differ, trust parsed (maybe missing events)
        if ((parsedHost + parsedGuest) != (fhHost + fhGuest))
            return mi.HalfTime;

        // If inverted, swap
        if (parsedHost != fhHost && parsedGuest == fhHost && parsedHost == fhGuest)
            return new HalfTimeScore(parsedGuest, parsedHost);

        return mi.HalfTime;
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

        // 45' Name / 45+2' Name / 90 + 3' Name
        var m = Regex.Match(text, @"^\s*(\d+)(?:\s*\+\s*(\d+))?\s*'\s*(.+?)\s*$");
        if (m.Success)
        {
            int baseMin = int.Parse(m.Groups[1].Value);
            int extra   = string.IsNullOrEmpty(m.Groups[2].Value) ? 0 : int.Parse(m.Groups[2].Value);
            return (baseMin + extra, m.Groups[3].Value.Trim());
        }

        // Also accept "19'Name" (no space)
        m = Regex.Match(text, @"^\s*(\d+)(?:\s*\+\s*(\d+))?\s*'(.+?)\s*$");
        if (m.Success)
        {
            int baseMin = int.Parse(m.Groups[1].Value);
            int extra   = string.IsNullOrEmpty(m.Groups[2].Value) ? 0 : int.Parse(m.Groups[2].Value);
            return (baseMin + extra, m.Groups[3].Value.Trim());
        }

        // Last resort: treat as player
        return (null, text);
    }

    private static int ParseIntSafe(string? s)
    {
        if (int.TryParse(Normalize(s ?? ""), out var v)) return v;
        return 0;
    }
}
