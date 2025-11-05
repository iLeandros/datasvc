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
                    
                            // Side from container
                            var sideClass = a.Ancestors("div")
                                             .Select(n => n.GetAttributeValue("class", "").ToLowerInvariant())
                                             .FirstOrDefault(c => c.Contains("hostteam") || c.Contains("guestteam")) ?? "";
                            var side = sideClass.Contains("guestteam") ? TeamSide.Guest : TeamSide.Host;
                    
                            // Kind from the icon classes
                            var icon = a.SelectSingleNode(".//div[contains(@class,'matchaction')]/div");
                            var classStr = (icon?.GetAttributeValue("class", "") ?? "").ToLowerInvariant();
                            var kind = ClassToActionKind(classStr);
                    
                            // "45+2' Player Name" → (47, "Player Name")
                            var (minute, player) = ParseMinuteAndPlayer(raw);
                            if (string.IsNullOrWhiteSpace(player) && !minute.HasValue) continue; // still nothing? skip
                    
                            matchData.Matches.Last().Actions.Add(new MatchAction(side, kind, minute, player));
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
    }
}
