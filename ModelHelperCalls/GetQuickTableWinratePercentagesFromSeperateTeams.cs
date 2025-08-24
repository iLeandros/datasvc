// File: ModelHelperCalls/BarChartsParser.cs
using System.Collections.ObjectModel;
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models; // BarChart, MatchFactData

namespace DataSvc.ModelHelperCalls;

namespace DataSvc.ModelHelperCalls
{
    public static class LastTeamsMatchesHelper
    {
        // Returns a 3x2 matrix of percentages (int):
        // rows: [0]=Wins, [1]=Draws, [2]=Losses
        // cols: [0]=Team1 (left half), [1]=Team2 (right half)
        public static int[,] GetQuickTableWinratePercentagesFromSeperateTeams(string? lastTeamsMatchesHtml)
        {
            var result = new int[3, 2]; // defaults to zeros
            if (string.IsNullOrWhiteSpace(lastTeamsMatchesHtml)) return result;

            var doc = new HtmlDocument();
            doc.LoadHtml(lastTeamsMatchesHtml);

            // two team columns
            var halves = doc.DocumentNode.SelectNodes(
                "//*[contains(concat(' ', normalize-space(@class), ' '), ' halfcontainer ')]"
            );
            if (halves == null || halves.Count < 2) return result;

            for (int j = 0; j < Math.Min(2, halves.Count); j++)
            {
                var half = halves[j];

                // W/D/L letters under graphics -> formastext -> .teamform
                var forms = half.SelectNodes(
                    ".//*[contains(concat(' ', normalize-space(@class), ' '), ' formastext ')]" +
                    "//div[contains(concat(' ', normalize-space(@class), ' '), ' teamform ')]"
                );

                int w = 0, d = 0, l = 0, total = forms?.Count ?? 0;
                if (forms != null)
                {
                    foreach (var f in forms)
                    {
                        var t = HtmlEntity.DeEntitize(f.InnerText ?? "").Trim().ToUpperInvariant();
                        if (t == "W") w++;
                        else if (t == "D") d++;
                        else if (t == "L") l++;
                    }
                }

                if (total > 0)
                {
                    result[0, j] = (100 * w) / total;
                    result[1, j] = (100 * d) / total;
                    result[2, j] = (100 * l) / total;
                }
            }

            return result;
        }
    }
}
