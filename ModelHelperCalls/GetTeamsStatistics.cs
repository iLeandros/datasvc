// File: ModelHelperCalls/GetTeamsStatistic.cs
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models;

namespace DataSvc.ModelHelperCalls;

public static class GetTeamStatisticsHelper 
{
    public static async Task<List<TeamStatistics>> GetTeamsStatistics(string htmlContent)
    {
        try
        {
            var website = new HtmlDocument();
            website.LoadHtml(htmlContent);
    
            var halfContainers = website.DocumentNode.Descendants("div")
                .FirstOrDefault(o => o.GetAttributeValue("class", "") == "teamsstatistics")?
                .Descendants("div").Where(o => o.GetAttributeValue("class", "") == "halfcontainer").ToList();
    
            if (halfContainers == null || halfContainers.Count < 2)
                throw new Exception("Team match containers not found or insufficient number");
    
            var teamStatisticsList = new List<TeamStatistics>();
    
            foreach (var container in halfContainers)
            {
                var teamName = container.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "name")?.InnerText.Trim();
                var teamLogoUrl = container.Descendants("img").FirstOrDefault()?.GetAttributeValue("src", string.Empty);
                var factItems = container.Descendants("div")
                    .Where(o => o.GetAttributeValue("class", "") == "factitem")
                    .Select(factItem => new FactItem
                    {
                        Value = CleanAndParseValue(factItem.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "value")?.InnerText.Trim()),
                        Label = factItem.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "label")?.InnerText.Trim()
                    })
                    .ToList();
    
                var teamStatistics = new TeamStatistics
                {
                    TeamName = teamName,
                    TeamLogoUrl = teamLogoUrl,
                    FactItems = factItems
                };
    
                teamStatisticsList.Add(teamStatistics);
            }
    
            return teamStatisticsList;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("GetTeamsStatistics: " + ex.Message);
            return null;
        }
    }
    private static double CleanAndParseValue(string value)
    {
        // Remove all non-numeric characters except for periods
        string cleanedValue = new string(value.Where(c => char.IsDigit(c) || c == '.').ToArray());
    
        // Remove trailing period if it exists
        cleanedValue = cleanedValue.TrimEnd('.');
    
        // Attempt to parse the cleaned value to a double
        if (double.TryParse(cleanedValue, out double numericValue))
        {
            return numericValue;
        }
    
        // If parsing fails, return 0 as a default value
        return 0;
    }
}
