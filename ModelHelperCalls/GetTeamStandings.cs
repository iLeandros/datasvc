// File: ModelHelperCalls/TeamStandingsHelper.cs
using System;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using HtmlAgilityPack;
using DataSvc.Models;

namespace DataSvc.ModelHelperCalls
{
    public static class TeamStandingsHelper
    {
        public static TeamStandingsTable GetTeamStandings(string? htmlContent)
        {
            var standingsTable = new TeamStandingsTable();
            if (string.IsNullOrWhiteSpace(htmlContent)) return standingsTable;

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(htmlContent);

                // token-aware, case-insensitive container match
                var container = doc.DocumentNode.SelectNodes(
                    "//*[contains(concat(' ', normalize-space(@class), ' '), ' teamsstandings ') or " +
                    "   contains(concat(' ', normalize-space(@class), ' '), ' teamstatistics ')]")
                    ?.FirstOrDefault();

                var halfContainers = container?.SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' halfcontainer ')]");
                if (halfContainers == null || halfContainers.Count == 0)
                    return standingsTable;

                string Clean(string? s) => HtmlEntity.DeEntitize(s ?? string.Empty).Replace("\u00A0", " ").Trim();
                int ToInt(HtmlNode? n)
                {
                    var txt = Clean(n?.InnerText);
                    return int.TryParse(txt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
                }

                foreach (var half in halfContainers)
                {
                    // Optional competition header per half
                    var competitionText = Clean(half.SelectSingleNode(".//*[contains(concat(' ', @class, ' '), ' competition ')]")?.InnerText);
                    if (!string.IsNullOrEmpty(competitionText))
                    {
                        standingsTable.Standings.Add(new TeamStanding { TeamName = competitionText, IsHeader = true });
                    }

                    var rows = half.SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' standingrow ') and not(contains(@class,'legend'))]");
                    if (rows == null) continue;

                    foreach (var row in rows)
                    {
                        var cls = row.GetAttributeValue("class", "");
                        bool isHighlite = cls.Contains("highlite", StringComparison.OrdinalIgnoreCase)
                                       || cls.Contains("highlight", StringComparison.OrdinalIgnoreCase);
                        bool isMark = cls.Contains("mark", StringComparison.OrdinalIgnoreCase) && !isHighlite;

                        // nodes (with fallbacks)
                        var posNode     = row.SelectSingleNode(".//*[contains(@class,'pos')]");
                        var nameLink    = row.SelectSingleNode(".//*[contains(@class,'name')]//a");
                        var nameNode    = nameLink ?? row.SelectSingleNode(".//*[contains(@class,'name')]");
                        var matchesNode = row.SelectSingleNode(".//*[contains(@class,'matches')]");
                        var pointsNode  = row.SelectSingleNode(".//*[contains(@class,'points')]");

                        var overallW = row.SelectSingleNode(".//*[contains(@class,'overall')]//*[contains(@class,'wins')]");
                        var overallD = row.SelectSingleNode(".//*[contains(@class,'overall')]//*[contains(@class,'draw')]");
                        var overallL = row.SelectSingleNode(".//*[contains(@class,'overall')]//*[contains(@class,'lose')]");

                        var homeW = row.SelectSingleNode(".//*[contains(@class,'home')]//*[contains(@class,'wins')]");
                        var homeD = row.SelectSingleNode(".//*[contains(@class,'home')]//*[contains(@class,'draw')]");
                        var homeL = row.SelectSingleNode(".//*[contains(@class,'home')]//*[contains(@class,'lose')]");

                        var awayW = row.SelectSingleNode(".//*[contains(@class,'away')]//*[contains(@class,'wins')]");
                        var awayD = row.SelectSingleNode(".//*[contains(@class,'away')]//*[contains(@class,'draw')]");
                        var awayL = row.SelectSingleNode(".//*[contains(@class,'away')]//*[contains(@class,'lose')]");

                        var standing = new TeamStanding
                        {
                            Position = ToInt(posNode),
                            TeamName = Clean(nameNode?.InnerText),
                            TeamUrl  = nameLink?.GetAttributeValue("href", "").Trim() ?? "",
                            Matches  = ToInt(matchesNode),
                            Points   = ToInt(pointsNode),

                            Overall = new Record { Wins = ToInt(overallW), Draws = ToInt(overallD), Losses = ToInt(overallL) },
                            Home    = new Record { Wins = ToInt(homeW),    Draws = ToInt(homeD),    Losses = ToInt(homeL) },
                            Away    = new Record { Wins = ToInt(awayW),    Draws = ToInt(awayD),    Losses = ToInt(awayL) },

                            IsHighlite = isHighlite,
                            IsMark     = isMark
                        };

                        standingsTable.Standings.Add(standing);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GetTeamStandings error: " + ex.Message);
            }

            return standingsTable;
        }
    }
}
