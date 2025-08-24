// File: ModelHelperCalls/MatchFactsParser.cs
using System.Collections.Generic;
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models; // MatchFact

namespace DataSvc.ModelHelperCalls
{
    public static class MatchFactsParser
    {
        // GetMatchFacts.cs — replace method body with this more robust version
        public static List<MatchFact> GetMatchFacts(string? htmlContent)
        {
            var facts = new List<MatchFact>();
            if (string.IsNullOrWhiteSpace(htmlContent)) return facts;
        
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(htmlContent);
        
                // 1) Locate the facts block (class OR id OR heading with 'facts')
                var factsBlock =
                    doc.DocumentNode.SelectSingleNode("//*[contains(concat(' ', normalize-space(@class), ' '), ' facts ')]")
                    ?? doc.DocumentNode.SelectSingleNode("//*[@id='facts' or contains(@id,'facts')]")
                    ?? doc.DocumentNode.SelectNodes("//div[.//h1|.//h2|.//h3|.//div[contains(@class,'title')]]")
                          ?.FirstOrDefault(n => HtmlEntity.DeEntitize(n.InnerText ?? "").ToLowerInvariant().Contains("facts"));
        
                if (factsBlock == null) return facts; // nothing to parse
        
                string Clean(HtmlNode? n) => HtmlEntity.DeEntitize(n?.InnerText ?? "")
                                                        .Replace("\u00A0", " ").Replace("\r", " ")
                                                        .Replace("\t", " ").Trim();
        
                bool IsValue(string s)
                {
                    // number, number%, or "NN min."
                    return System.Text.RegularExpressions.Regex.IsMatch(
                        s, @"^(\d+(\.\d+)?\s*(%|min\.?)?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
        
                // 2) Strict layout first: .datarow > (.label, .value)
                var rows = factsBlock.SelectNodes(".//*[contains(@class,'datarow')]") ?? new HtmlNodeCollection(null);
                foreach (var row in rows)
                {
                    var label = Clean(row.SelectSingleNode(".//*[contains(@class,'label')]"));
                    var value = Clean(row.SelectSingleNode(".//*[contains(@class,'value')]"));
                    if (label.Length == 0 && value.Length == 0) continue;
                    if (label.Length > 0 || value.Length > 0)
                        facts.Add(new MatchFact { Label = label, Value = value });
                }
        
                if (facts.Count > 0) return facts;
        
                // 3) Fallback: pair adjacent lines/items that look like "Label" then "Value"
                var items = factsBlock.SelectNodes("./*|.//li|.//p|.//span|.//div") ?? new HtmlNodeCollection(null);
                string? pendingLabel = null;
        
                foreach (var it in items)
                {
                    var t = Clean(it);
                    if (string.IsNullOrWhiteSpace(t)) continue;
        
                    // If this element *contains* an explicit value node, use it
                    var explicitVal = it.SelectSingleNode(".//*[contains(@class,'value')]");
                    var explicitLbl = it.SelectSingleNode(".//*[contains(@class,'label')]");
                    if (explicitVal != null || explicitLbl != null)
                    {
                        var lbl = Clean(explicitLbl ?? it);
                        var val = Clean(explicitVal);
                        if (!string.IsNullOrEmpty(lbl) || !string.IsNullOrEmpty(val))
                            facts.Add(new MatchFact { Label = lbl, Value = val });
                        pendingLabel = null;
                        continue;
                    }
        
                    // Otherwise infer from text
                    if (IsValue(t))
                    {
                        if (!string.IsNullOrEmpty(pendingLabel))
                        {
                            facts.Add(new MatchFact { Label = pendingLabel, Value = t });
                            pendingLabel = null;
                        }
                    }
                    else
                    {
                        // New potential label (avoid headings that are just 'facts')
                        if (!t.Equals("facts", StringComparison.OrdinalIgnoreCase) &&
                            !t.Contains("advert", StringComparison.OrdinalIgnoreCase) &&
                            t.Length > 2)
                        {
                            pendingLabel = t;
                        }
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
