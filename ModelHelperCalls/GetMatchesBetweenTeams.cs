using System.Collections.ObjectModel;
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models;

namespace DataSvc.ModelHelperCalls;

public static async Task<ObservableCollection<DetailsTableDataGroupMatchesBetween>> GetMatchesBetweenTeams(TableDataItem item)
{
    using (HttpClient client = new HttpClient())
    {
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible, MSIE 11, Windows NT 6.3; Trident/7.0; rv:11.0) like Gecko");
        using (HttpResponseMessage response = await client.GetAsync(item.Href))
        using (HttpContent content = response.Content)
        {
            try
            {
                string result = await content.ReadAsStringAsync();
                var website = new HtmlDocument();
                website.LoadHtml(result);

                // Find the match items
                var matchesGroups = website.DocumentNode.Descendants("div")
                    .Where(o => o.GetAttributeValue("class", "") == "matchbtwteams")
                    .SelectMany(o => o.Descendants("div").Where(p => p.GetAttributeValue("class", "") == "matchitem"))
                    .ToList();

                if (matchesGroups == null || matchesGroups.Count == 0)
                    throw new Exception("Matches not found");

                ObservableCollection<DetailsTableDataGroupMatchesBetween> items = new ObservableCollection<DetailsTableDataGroupMatchesBetween>();
                ObservableCollection<DetailsTableDataItemMatchesBetween> itemToAdd = new ObservableCollection<DetailsTableDataItemMatchesBetween>();

                foreach (var matchGroup in matchesGroups)
                {
                    DateTime date;
                    if (!DateTime.TryParse(matchGroup.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "date")?.InnerText, out date))
                    {
                        date = DateTime.Now;
                    }

                    var match = matchGroup.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "match");

                    var hostteam = match.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "hostteam");
                    int hostteamGoals = int.Parse(hostteam.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "goals")?.InnerText ?? "0");
                    string hostteamName = hostteam.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "").Contains("name"))?.Element("a")?.InnerText.Trim();

                    var guestteam = match.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "guestteam");
                    int guestteamGoals = int.Parse(guestteam.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "goals")?.InnerText ?? "0");
                    string guestteamName = guestteam.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "").Contains("name"))?.Element("a")?.InnerText.Trim();

                    var details = matchGroup.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "details");
                    var info = details.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "info")?
                        .Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "holder")?
                        .Descendants("div").Where(o => o.GetAttributeValue("class", "") == "goals").ToList();

                    int hosthtgoals = int.Parse(info?[0].InnerText ?? "0");
                    int guesthtgoals = int.Parse(info?[1].InnerText ?? "0");

                    itemToAdd.Add(new DetailsTableDataItemMatchesBetween(new
                    Match(new hostTeam() { name = renameTeam.renameTeamNameToFitDisplayLabel(hostteamName), goals = hostteamGoals }, new guestTeam()
                    {
                        name = renameTeam.renameTeamNameToFitDisplayLabel(guestteamName),
                        goals = guestteamGoals
                    }), new Details(new info() { hostHTGoals = hosthtgoals, guestHTGoals = guesthtgoals })));
                }

                items.Add(new DetailsTableDataGroupMatchesBetween(itemToAdd));
                return items;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GetMatchesBetweenTeams Error: " + ex.Message);
                return null;
            }
        }
    }
}
