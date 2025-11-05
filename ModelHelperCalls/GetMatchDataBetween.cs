// File: ModelHelperCalls/GetMatchDataBetween.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HtmlAgilityPack;
using DataSvc.Models;

namespace DataSvc.ModelHelperCalls
{
    /// <summary>
    /// Parses the "matches between two teams" page markup and produces strongly typed data.
    /// This version targets the new immutable MatchAction model and HalfTimeScore value object.
    /// </summary>
    public static class MatchBetweenHelper
    {
        /// <summary>
        /// Parse an HTML page showing multiple historical results between the same two teams.
        /// It expects each match "card" to contain host/guest names and goals, an actions area,
        /// and (optionally) a halftime score block.
        /// </summary>
        public static MatchData? GetMatchDataBetween(string htmlContent)
        {
            try
            {
                var matchData = new MatchData();
                var document = new HtmlDocument();
                document.LoadHtml(htmlContent);

                // Heuristic: match container nodes. Adjust selectors to your actual markup if needed.
                var matchNodes = document.DocumentNode.SelectNodes(
                    "//div[contains(@class,'match') or contains(@class,'result')][contains(@class,'item') or contains(@class,'box') or contains(@class,'row') or contains(@class,'card')]"
                ) ?? document.DocumentNode.SelectNodes("//div[contains(@class,'match-item') or contains(@class,'result-item')]");

                if (matchNodes == null || matchNodes.Count == 0)
                    return matchData;

                foreach (var item in matchNodes)
                {
                    var hostTeamNode  = item.SelectSingleNode(".//div[contains(@class,'hostteam')]//div[contains(@class,'name')]")
                                       ?? item.SelectSingleNode(".//div[contains(@class,'host') and contains(@class,'team')]//div[contains(@class,'name')]");
                    var guestTeamNode = item.SelectSingleNode(".//div[contains(@class,'guestteam')]//div[contains(@class,'name')]")
                                       ?? item.SelectSingleNode(".//div[contains(@class,'guest') and contains(@class,'team')]//div[contains(@class,'name')]");

                    var matchItem = new MatchItem
                    {
                        Competition = TryGetInnerText(item, ".//div[contains(@class,'competition')]"),
                        Date        = TryGetInnerText(item, ".//div[contains(@class,'date')]"),
                        HostTeam    = hostTeamNode?.InnerText.Trim() ?? string.Empty,
                        HostGoals   = ParseIntSafe(TryGetInnerText(item, ".//div[contains(@class,'hostteam')]//div[@class='goals']") 
                                                   ?? TryGetInnerText(item, ".//div[contains(@class,'host')]//div[contains(@class,'goals')]")),
                        GuestTeam   = guestTeamNode?.InnerText.Trim() ?? string.Empty,
                        GuestGoals  = ParseIntSafe(TryGetInnerText(item, ".//div[contains(@class,'guestteam')]//div[@class='goals']") 
                                                   ?? TryGetInnerText(item, ".//div[contains(@class,'guest')]//div[contains(@class,'goals')]")),
                        Actions     = new List<MatchAction>(),
                        HalfTime    = new HalfTimeScore(0, 0)
                    };

                    // Halftime (if present on this card)
                    matchItem.HalfTime = ParseHalfTime(item);

                    // Actions, robust to different classnames. We look for elements which represent a single action.
                    var actionNodes = item.SelectNodes(".//div[contains(@class,'action') or contains(@class,'event')]");
                    if (actionNodes != null)
                    {
                        foreach (var actionNode in actionNodes)
                        {
                            // Decide side from the node class or its parents
                            var cls  = actionNode.GetAttributeValue("class", string.Empty).ToLowerInvariant();
                            var side = InferSide(actionNode, cls);

                            // Determine kind from the css classnames (goal, ycard, dycard, penalty, rcard)
                            var kind = ClassToActionKind(cls);

                            // Extract raw text like: 45+2' Player Name
                            var raw = actionNode.InnerText.Trim();
                            var (minute, player) = ParseMinuteAndPlayer(raw);

                            // Skip empty lines
                            if (string.IsNullOrWhiteSpace(player) && !minute.HasValue)
                                continue;

                            // Create new strongly-typed action
                            var matchAction = new MatchAction(side, kind, minute, player);
                            matchItem.Actions.Add(matchAction);
                        }
                    }

                    matchData.Matches.Add(matchItem);
                }

                return matchData;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GetMatchDataBetween error: " + ex.Message);
                return null;
            }
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
            // Example structures this tries to support:
            // 1) <div class="halftime"><div class="goals">1</div><div class="goals">0</div></div>
            // 2) <div class="ht">HT: 1-0</div>
            var holder =
                matchItemNode.SelectSingleNode(".//div[contains(@class,'halftime') or contains(@class,'half-time') or contains(@class,'ht')]");

            if (holder == null)
            {
                // Try to parse from a textual token like "HT 1-0" within the score line
                var scoreText = TryGetInnerText(matchItemNode, ".//div[contains(@class,'score') or contains(@class,'result')]");
                if (!string.IsNullOrWhiteSpace(scoreText))
                {
                    var m2 = System.Text.RegularExpressions.Regex.Match(scoreText, @"HT\s*[:\-]?\s*(\d+)\s*[-:]\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m2.Success)
                    {
                        return new HalfTimeScore(ParseIntSafe(m2.Groups[1].Value), ParseIntSafe(m2.Groups[2].Value));
                    }
                }
                return new HalfTimeScore(0, 0);
            }

            // Structured holders with two goal nodes
            var goalNodes = holder.SelectNodes(".//div[contains(@class,'goals') or contains(@class,'score')]/text() | .//div[contains(@class,'goals') or contains(@class,'score')]");
            if (goalNodes != null && goalNodes.Count >= 2)
            {
                var host = ParseIntSafe(goalNodes[0].InnerText);
                var guest = ParseIntSafe(goalNodes[1].InnerText);
                return new HalfTimeScore(host, guest);
            }

            // Try "1-0" inside the holder text
            var text = holder.InnerText;
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

            // Patterns like:
            // 45' Player, 45+2' Player, 90 + 3' Player
            var m = System.Text.RegularExpressions.Regex.Match(
                raw, @"^\s*(\d+)(?:\s*\+\s*(\d+))?\s*'\s*(.+?)\s*$");

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
    }
}
