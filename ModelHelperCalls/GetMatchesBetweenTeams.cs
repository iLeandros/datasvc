using System.Collections.ObjectModel;
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models;

namespace DataSvc.ModelHelperCalls;

public static class GetMatchesBetweenTeamsHelper
{
    // ✅ Preferred in the server: parse the HTML we already cached
    public static ObservableCollection<DetailsTableDataGroupMatchesBetween>? ParseFromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        try
        {
            var website = new HtmlDocument();
            website.LoadHtml(html);

            // .matchbtwteams > .matchitem
            var matchesGroups = website.DocumentNode.Descendants("div")
                .Where(o => o.GetAttributeValue("class", "") == "matchbtwteams")
                .SelectMany(o => o.Descendants("div").Where(p => p.GetAttributeValue("class", "") == "matchitem"))
                .ToList();

            if (matchesGroups == null || matchesGroups.Count == 0)
                return new ObservableCollection<DetailsTableDataGroupMatchesBetween>();

            var itemToAdd = new ObservableCollection<DetailsTableDataItemMatchesBetween>();

            foreach (var matchGroup in matchesGroups)
            {
                var match = matchGroup.Descendants("div")
                    .FirstOrDefault(o => o.GetAttributeValue("class", "") == "match");
                if (match == null) continue;

                var hostteam = match.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "hostteam");
                var guestteam = match.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "guestteam");

                static int toInt(string? s) => int.TryParse(s, out var v) ? v : 0;

                int hostGoals = toInt(hostteam?.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "goals")?.InnerText);
                int guestGoals = toInt(guestteam?.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "goals")?.InnerText);

                string hostName = hostteam?.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "").Contains("name"))?.Element("a")?.InnerText?.Trim() ?? "";
                string guestName = guestteam?.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "").Contains("name"))?.Element("a")?.InnerText?.Trim() ?? "";

                var details = matchGroup.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "details");
                var info = details?.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "info")
                    ?.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "holder")
                    ?.Descendants("div").Where(o => o.GetAttributeValue("class", "") == "goals").ToList();

                int hostHTGoals = toInt(info?.ElementAtOrDefault(0)?.InnerText);
                int guestHTGoals = toInt(info?.ElementAtOrDefault(1)?.InnerText);

                // optional display rename (as in your code)
                var hostDisplay = renameTeam.renameTeamNameToFitDisplayLabel(hostName);
                var guestDisplay = renameTeam.renameTeamNameToFitDisplayLabel(guestName);

                itemToAdd.Add(new DetailsTableDataItemMatchesBetween(
                    new Match(new hostTeam { name = hostDisplay, goals = hostGoals },
                              new guestTeam { name = guestDisplay, goals = guestGoals }),
                    new Details(new info { hostHTGoals = hostHTGoals, guestHTGoals = guestHTGoals })
                ));
            }

            return new ObservableCollection<DetailsTableDataGroupMatchesBetween>
            {
                new DetailsTableDataGroupMatchesBetween(itemToAdd)
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine("GetMatchesBetweenTeams.ParseFromHtml error: " + ex.Message);
            return null;
        }
    }

    // ❗ Kept for completeness (uses HTTP like your original code) — not used by the server path
    public static async Task<ObservableCollection<DetailsTableDataGroupMatchesBetween>?> GetFromHrefAsync(string href)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible, MSIE 11, Windows NT 6.3; Trident/7.0; rv:11.0) like Gecko");
        var html = await client.GetStringAsync(href);
        return ParseFromHtml(html);
    }
}
