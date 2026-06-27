// File: ModelHelperCalls/GetTeamsStatistics.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using HtmlAgilityPack;
using DataSvc.Models; // TeamStatistics, FactItem

namespace DataSvc.ModelHelperCalls
{
    public static class GetTeamStatisticsHelper
    {
        public static List<TeamStatistics> GetTeamsStatistics(string? htmlContent)
        {
            var teamStatisticsList = new List<TeamStatistics>();
            if (string.IsNullOrWhiteSpace(htmlContent)) return teamStatisticsList;

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(htmlContent);

                var halfContainers = doc.DocumentNode
                    .Descendants("div")
                    .FirstOrDefault(o => o.GetAttributeValue("class", "") == "teamsstatistics")
                    ?.Descendants("div") // ← missing dot fixed
                    .Where(o => o.GetAttributeValue("class", "") == "halfcontainer")
                    .ToList();

                if (halfContainers == null || halfContainers.Count < 2)
                {
                    Debug.WriteLine("GetTeamsStatistics: containers not found or insufficient number");
                    return teamStatisticsList;
                }

                string Clean(string? s) =>
                    HtmlEntity.DeEntitize(s ?? string.Empty).Replace("\u00A0", " ").Trim();

                foreach (var container in halfContainers)
                {
                    var teamName   = Clean(container.Descendants("div")
                                          .FirstOrDefault(o => o.GetAttributeValue("class","") == "name")?.InnerText);
                    var teamLogoUrl= container.Descendants("img")
                                          .FirstOrDefault()?.GetAttributeValue("src", string.Empty);

                    var factItems = container.Descendants("div")
                        .Where(o => o.GetAttributeValue("class", "") == "factitem")
                        .Select(fi =>
                        {
                            var valText = Clean(fi.Descendants("div")
                                            .FirstOrDefault(o => o.GetAttributeValue("class","") == "value")?.InnerText);
                            var lblText = Clean(fi.Descendants("div")
                                            .FirstOrDefault(o => o.GetAttributeValue("class","") == "label")?.InnerText);
                            return new FactItem { Value = ParseDouble(valText), Label = lblText };
                        })
                        .ToList();

                    teamStatisticsList.Add(new TeamStatistics
                    {
                        TeamName    = teamName,
                        TeamLogoUrl = teamLogoUrl,
                        FactItems   = factItems
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GetTeamsStatistics error: " + ex.Message);
            }

            return teamStatisticsList;
        }

        private static double ParseDouble(string s)
        {
            // Keep digits and '.' only, then parse invariantly (e.g., "45.5%")
            var cleaned = new string(s.Where(c => char.IsDigit(c) || c == '.').ToArray()).TrimEnd('.');
            return double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0d;
        }
    }
}
