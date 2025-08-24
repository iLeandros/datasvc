// File: ModelHelperCalls/BarChartsParser.cs
using System.Collections.ObjectModel;
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models; // BarChart, MatchFactData

namespace DataSvc.ModelHelperCalls;

public static class BarChartsParser
{
    public static ObservableCollection<BarChart>? GetBarChartsData(string? htmlContent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(htmlContent))
                return null;

            var barCharts = new ObservableCollection<BarChart>();
            var document = new HtmlDocument();
            document.LoadHtml(htmlContent);

            var teamsBetStatisticsDiv = document.DocumentNode
                .Descendants("div")
                .FirstOrDefault(o => o.GetAttributeValue("class", "") == "teamsbetstatistics");

            if (teamsBetStatisticsDiv == null)
            {
                //Debug.WriteLine("'teamsbetstatistics' div not found");
                return null;
            }

            var halfContainers = teamsBetStatisticsDiv.Descendants("div")
                .Where(o => o.GetAttributeValue("class", "") == "halfcontainer")
                .ToList();

            if (halfContainers == null || halfContainers.Count == 0)
            {
                //Debug.WriteLine("No 'halfcontainer' divs found");
                return null;
            }

            int halfContainerId = 1;
            foreach (var halfContainer in halfContainers)
            {
                var chartSections = halfContainer.SelectNodes(".//div[contains(@class, 'barchart')]");
                if (chartSections == null) { halfContainerId++; continue; }

                foreach (var section in chartSections)
                {
                    var titleNode = section.SelectSingleNode(".//div[contains(@class, 'title')]");
                    var title = (titleNode?.InnerText ?? "Untitled").Trim();

                    var matchFacts = new List<MatchFactData>();
                    var rows = section.SelectNodes(".//div[@class='barrow']");
                    if (rows != null)
                    {
                        foreach (var row in rows)
                        {
                            var nameNode = row.SelectSingleNode(".//div[@class='name']");
                            var barNode  = row.SelectSingleNode(".//div[@class='bar']");
                            var name = (nameNode?.InnerText ?? "").Trim();
                            var pctText = (barNode?.InnerText ?? "").Trim().TrimEnd('%');

                            if (int.TryParse(pctText, out int pct))
                            {
                                matchFacts.Add(new MatchFactData { Title = title, Name = name, Percentage = pct });
                            }
                        }
                    }

                    barCharts.Add(new BarChart(title, matchFacts)
                    {
                        HalfContainerId = $"HalfContainer{halfContainerId}"
                    });
                }

                halfContainerId++;
            }

            return barCharts;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetBarChartsData error: {ex.Message}");
            return null;
        }
    }
}
