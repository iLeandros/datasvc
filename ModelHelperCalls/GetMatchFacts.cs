// File: ModelHelperCalls/MatchFactsParser.cs
using System.Collections.Generic;
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models; // MatchFact

namespace DataSvc.ModelHelperCalls
{
    public static class MatchFactsParser
    {
        public static List<MatchFact> GetMatchFacts(string? htmlContent)
        {
            var facts = new List<MatchFact>();
            if (string.IsNullOrWhiteSpace(htmlContent)) return facts;

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(htmlContent);

                // 1) Pick the *matchbtwteams* container that actually has a facts block with datarows
                var containers = doc.DocumentNode.SelectNodes(
                    "//div[contains(concat(' ', normalize-space(@class), ' '), ' matchbtwteams ')]"
                );

                HtmlNode? scope = null;
                int bestRows = -1;

                if (containers != null)
                {
                    foreach (var c in containers)
                    {
                        var fb = c.SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' facts ')]")
                                  ?.OrderByDescending(b => b.SelectNodes(".//div[contains(@class,'datarow')]")?.Count ?? 0)
                                  .FirstOrDefault();

                        var rows = fb?.SelectNodes(".//div[contains(@class,'datarow')]")?.Count ?? 0;
                        if (rows > bestRows)
                        {
                            bestRows = rows;
                            scope = c;
                        }
                    }
                }

                // Fallback: whole document if we didn’t find a good container
                scope ??= doc.DocumentNode;

                // 2) Inside the chosen scope, pick the facts block with the most datarows
                var factsBlock = scope.SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' facts ')]")
                                      ?.OrderByDescending(b => b.SelectNodes(".//div[contains(@class,'datarow')]")?.Count ?? 0)
                                      .FirstOrDefault();

                var rowsNodes = factsBlock?.SelectNodes(".//div[contains(@class,'datarow')]");
                if (rowsNodes == null || rowsNodes.Count == 0) return facts;

                foreach (var row in rowsNodes)
                {
                    string Clean(HtmlNode? n) => HtmlEntity.DeEntitize(n?.InnerText ?? "")
                                                          .Replace("\u00A0", " ")
                                                          .Trim();

                    var valueText = Clean(row.SelectSingleNode(".//div[contains(@class,'value')]"));
                    var labelText = Clean(row.SelectSingleNode(".//div[contains(@class,'label')]"));

                    if (labelText.Length == 0 && valueText.Length == 0) continue;
                    facts.Add(new MatchFact { Label = labelText, Value = valueText });
                }
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine("GetMatchFacts error: " + ex.Message);
            }

            return facts;
        }
    }
}
