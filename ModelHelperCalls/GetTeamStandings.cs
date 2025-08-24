// File: ModelHelperCalls/TeamStandingsHelper.cs
using System.Collections.ObjectModel;
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models;

namespace DataSvc.ModelHelperCalls;

public static class TeamStandingsHelper
{
    public static async Task<TeamStandingsTable> GetTeamStandingsAsync(string htmlContent)
{
    try
    {
        var standingsTable = new TeamStandingsTable();
        var document = new HtmlDocument();
        document.LoadHtml(htmlContent);

        // 1) Find all halfcontainers under teamsstandings
        var halfContainers = document.DocumentNode
            .SelectNodes("//div[@class='teamsstandings']//div[@class='halfcontainer']");
        if (halfContainers == null)
        {
            // No halfcontainer found, return empty table
            return standingsTable;
        }

        // 2) Iterate each halfcontainer
        foreach (var halfContainer in halfContainers)
        {
            // 2a) Grab the competition text (if present)
            var competitionNode = halfContainer.SelectSingleNode(".//div[@class='competition']");
            string competitionText = competitionNode != null
                ? competitionNode.InnerText.Trim()
                : "Unknown Competition";

            // 2b) Create a header standing item
            var headerStanding = new TeamStanding
            {
                TeamName = competitionText,
                IsHeader = true
            };
            standingsTable.Standings.Add(headerStanding);

            // 2c) Within this halfcontainer, find its standing rows
            var rows = halfContainer.SelectNodes(".//div[contains(@class, 'standingrow') and not(contains(@class, 'legend'))]");
            if (rows == null)
            {
                // No rows for this halfcontainer, move on
                continue;
            }

            // 2d) Parse each row into a TeamStanding
            foreach (var row in rows)
            {
                // Check if row has "highlite" or "mark"
                var classAttribute = row.GetAttributeValue("class", "");
                var isHighlite = classAttribute.Contains("highlite");
                var isMark = classAttribute.Contains("mark") && !classAttribute.Contains("highlite");

                var standing = new TeamStanding
                {
                    Position = int.Parse(row.SelectSingleNode(".//div[@class='pos']").InnerText.Trim()),
                    TeamName = row.SelectSingleNode(".//div[@class='name']/a").InnerText.Trim(),
                    TeamUrl = row.SelectSingleNode(".//div[@class='name']/a").GetAttributeValue("href", "").Trim(),
                    Matches = int.Parse(row.SelectSingleNode(".//div[@class='matches']").InnerText.Trim()),
                    Points = int.Parse(row.SelectSingleNode(".//div[@class='common2']//div[@class='points']").InnerText.Trim()),
                    Overall = new Record
                    {
                        Wins = int.Parse(row.SelectSingleNode(".//div[@class='overall']//div[@class='wins']").InnerText.Trim()),
                        Draws = int.Parse(row.SelectSingleNode(".//div[@class='overall']//div[@class='draws']").InnerText.Trim()),
                        Losses = int.Parse(row.SelectSingleNode(".//div[@class='overall']//div[@class='loses']").InnerText.Trim())
                    },
                    Home = new Record
                    {
                        Wins = int.Parse(row.SelectSingleNode(".//div[@class='home']//div[@class='wins']").InnerText.Trim()),
                        Draws = int.Parse(row.SelectSingleNode(".//div[@class='home']//div[@class='draws']").InnerText.Trim()),
                        Losses = int.Parse(row.SelectSingleNode(".//div[@class='home']//div[@class='loses']").InnerText.Trim())
                    },
                    Away = new Record
                    {
                        Wins = int.Parse(row.SelectSingleNode(".//div[@class='away']//div[@class='wins']").InnerText.Trim()),
                        Draws = int.Parse(row.SelectSingleNode(".//div[@class='away']//div[@class='draws']").InnerText.Trim()),
                        Losses = int.Parse(row.SelectSingleNode(".//div[@class='away']//div[@class='loses']").InnerText.Trim())
                    },
                    IsHighlite = isHighlite,
                    IsMark = isMark
                };

                // Add the parsed standing to the table
                standingsTable.Standings.Add(standing);
            }
        }

        return standingsTable;
    }
    catch (Exception ex)
    {
        Debug.WriteLine("GetTeamStandingsAsync error: " + ex.Message);
        return null;
    }
}
}
