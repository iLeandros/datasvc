// File: ModelHelperCalls/BarChartsParser.cs
using System.Collections.ObjectModel;
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models; // BarChart, MatchFactData

namespace DataSvc.ModelHelperCalls;

// ---------- LiveScores: HTML parser ----------
public static class LiveScoreHtmlParser
{
    // Optional: wire your existing shortening/label-fit function here
    private static string FitLabel(string s) => renameTeamNameToFitDisplayLabel?.Invoke(s) ?? s;

    // If you don’t have a delegate handy, replace the call to FitLabel(...) with identity.
    public static Func<string, string>? renameTeamNameToFitDisplayLabel;

    private static string T(HtmlNode? n) => HtmlEntity.DeEntitize(n?.InnerText ?? string.Empty).Trim();

    // Token-safe class test in XPath via normalize-space
    private const string CLASS_TOKEN = "contains(concat(' ', normalize-space(@class), ' '), ' {0} ')";

    public static ObservableCollection<LiveTableDataGroup> Parse(string html)
    {
        var groups = new ObservableCollection<LiveTableDataGroup>();
        if (string.IsNullOrWhiteSpace(html)) return groups;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // 1) Find the allmatches container (the one that actually holds competitions)
        var allMatches = doc.DocumentNode.SelectSingleNode(
            $"//div[{string.Format(CLASS_TOKEN, "allmatches")}]"
        );

        if (allMatches == null)
        {
            // Page sometimes shows a version picker on top; content still exists below.
            // If nothing found, bail gracefully with empty groups.
            return groups;
        }

        // 2) Enumerate competitions that have an id (skips "competition ad")
        var competitions = allMatches.SelectNodes("./div[@id and " + string.Format(CLASS_TOKEN, "competition") + "]")
                           ?? new HtmlNodeCollection(null);

        foreach (var comp in competitions)
        {
            var title = T(comp.SelectSingleNode(".//div[" + string.Format(CLASS_TOKEN, "header") + "]/div[" + string.Format(CLASS_TOKEN, "name") + "]"));

            var items = new ObservableCollection<LiveTableDataItem>();

            // 3) Matches under the competition's body
            var matches = comp.SelectNodes(".//div[" + string.Format(CLASS_TOKEN, "body") + "]/div[" + string.Format(CLASS_TOKEN, "match") + "]")
                         ?? new HtmlNodeCollection(null);

            foreach (var m in matches)
            {
                var time   = T(m.SelectSingleNode(".//div[" + string.Format(CLASS_TOKEN, "startblock") + "]/div[" + string.Format(CLASS_TOKEN, "time") + "]"));
                var status = T(m.SelectSingleNode(".//div[" + string.Format(CLASS_TOKEN, "startblock") + "]/div[" + string.Format(CLASS_TOKEN, "status") + "]"));

                // Host
                var hostName  = T(m.SelectSingleNode(".//div[" + string.Format(CLASS_TOKEN, "teams") + "]/div[" + string.Format(CLASS_TOKEN, "hostteam") + "]/div[" + string.Format(CLASS_TOKEN, "name") + "]"));
                var hostGoals = T(m.SelectSingleNode(".//div[" + string.Format(CLASS_TOKEN, "teams") + "]/div[" + string.Format(CLASS_TOKEN, "hostteam") + "]/div[" + string.Format(CLASS_TOKEN, "goals") + "]"));

                // Guest
                var guestName  = T(m.SelectSingleNode(".//div[" + string.Format(CLASS_TOKEN, "teams") + "]/div[" + string.Format(CLASS_TOKEN, "guestteam") + "]/div[" + string.Format(CLASS_TOKEN, "name") + "]"));
                var guestGoals = T(m.SelectSingleNode(".//div[" + string.Format(CLASS_TOKEN, "teams") + "]/div[" + string.Format(CLASS_TOKEN, "guestteam") + "]/div[" + string.Format(CLASS_TOKEN, "goals") + "]"));

                // You had a livetimebgcolor concept; defaulting to Black (adjust if you need special colors for "pen", etc.)
                items.Add(new LiveTableDataItem(
                    time,
                    status,
                    Color.Black,
                    FitLabel(hostName),
                    hostGoals,
                    guestGoals,
                    FitLabel(guestName)
                ));
            }

            // Only add competitions that actually have matches
            if (items.Count > 0)
            {
                groups.Add(new LiveTableDataGroup(
                    Color.DarkSlateGray,
                    title,
                    items
                ));
            }
        }

        return groups;
    }
}
