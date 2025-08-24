// File: ModelHelperCalls/MatchFactsParser.cs  (drop-in)
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using DataSvc.Models; // MatchFact

namespace DataSvc.ModelHelperCalls
{
    public static class MatchFactsParser
    {
        public static List<MatchFact> GetMatchFacts(string? factsHtml)
        {
            var facts = new List<MatchFact>();
            if (string.IsNullOrWhiteSpace(factsHtml)) return facts;

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(factsHtml);

                // If the HTML is the facts block already, root will just be that <div class="facts">.
                // If not, this still finds it safely.
                var root = doc.DocumentNode.SelectSingleNode(
                    "//*[contains(concat(' ', normalize-space(@class), ' '), ' facts ')]"
                ) ?? doc.DocumentNode;

                string Clean(HtmlNode? n) =>
                    HtmlEntity.DeEntitize(n?.InnerText ?? "")
                              .Replace("\u00A0", " ")
                              .Replace("\t", " ")
                              .Replace("\r", " ")
                              .Trim();

                // Preferred layout: rows with .label / .value (case-insensitive).
                const string ABC = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
                const string abc = "abcdefghijklmnopqrstuvwxyz";
                string t(string s) => $"translate({s},'{ABC}','{abc}')";

                var rows = root.SelectNodes($".//*[contains({t("@class")},'datarow')]");

                if (rows != null && rows.Count > 0)
                {
                    foreach (var row in rows)
                    {
                        var label = Clean(row.SelectSingleNode($".//*[contains({t("@class")},'label')]"));
                        var value = Clean(row.SelectSingleNode($".//*[contains({t("@class")},'value')]"));

                        // Fallbacks: if only one side exists, infer the other from the row text.
                        if (string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(value))
                        {
                            // Try the first non-empty text not equal to the value.
                            var cand = row.ChildNodes
                                          .Select(n => Clean(n))
                                          .FirstOrDefault(s => !string.IsNullOrEmpty(s) && s != value);
                            if (!string.IsNullOrEmpty(cand)) label = cand;
                        }
                        else if (string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(label))
                        {
                            var cand = row.ChildNodes
                                          .Select(n => Clean(n))
                                          .FirstOrDefault(s => !string.IsNullOrEmpty(s) && s != label);
                            if (!string.IsNullOrEmpty(cand)) value = cand;
                        }

                        if (!string.IsNullOrEmpty(label) || !string.IsNullOrEmpty(value))
                            facts.Add(new MatchFact { Label = label, Value = value });
                    }

                    return facts;
                }

                // Fallback layout: pair adjacent texty items ("Label" line, then "Value" like "12" or "80%").
                bool LooksLikeValue(string s) =>
                    Regex.IsMatch(s, @"^(\d+(\.\d+)?\s*(%|min\.?)?)$", RegexOptions.IgnoreCase);

                var linear = root.SelectNodes(".//li|.//p|.//div|.//span")
                                 ?.Select(Clean)
                                 .Where(s => !string.IsNullOrEmpty(s))
                                 .ToList()
                             ?? new List<string>();

                string? pending = null;
                foreach (var piece in linear)
                {
                    if (LooksLikeValue(piece) && !string.IsNullOrEmpty(pending))
                    {
                        facts.Add(new MatchFact { Label = pending, Value = piece });
                        pending = null;
                    }
                    else if (!piece.Equals("facts", System.StringComparison.OrdinalIgnoreCase))
                    {
                        pending = piece;
                    }
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
