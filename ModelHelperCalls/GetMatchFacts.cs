// File: ModelHelperCalls/MatchFactsParser.cs
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models; // MatchFact

namespace DataSvc.ModelHelperCalls;

public static class MatchFactsParser
{
    // No need for async; we just parse a string
    public static List<MatchFact>? GetMatchFacts(string? htmlContent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(htmlContent))
                return null;

            var list = new List<MatchFact>();
            var document = new HtmlDocument();
            document.LoadHtml(htmlContent);

            // facts block lives as its own section: <div class="facts">...</div>
            var factsSection = document.DocumentNode.SelectSingleNode("//div[@class='facts']");
            if (factsSection == null)
                return null;

            var factRows = factsSection.SelectNodes(".//div[@class='datarow']");
            if (factRows == null || factRows.Count == 0)
                return new List<MatchFact>();

            foreach (var row in factRows)
            {
                var value = row.SelectSingleNode(".//div[@class='value']")?.InnerText?.Trim();
                var label = row.SelectSingleNode(".//div[@class='label']")?.InnerText?.Trim();
                if (!string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(value))
                    list.Add(new MatchFact { Label = label, Value = value });
            }

            return list;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("GetMatchFacts error: " + ex.Message);
            return null;
        }
    }
}
