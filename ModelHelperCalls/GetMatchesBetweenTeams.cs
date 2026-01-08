using System.Collections.ObjectModel;
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models;
using DataSvc.Details;

namespace DataSvc.ModelHelperCalls;

public static class GetMatchesBetweenTeamsHelper
{
    // Parse the cached HTML of <div class="matchbtwteams">…</div> into your models
    public static ObservableCollection<DetailsTableDataGroupMatchesBetween>? ParseFromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // root container (allow extra class tokens)
            var root = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'matchbtwteams')]");
            if (root == null) return null;

            // Some pages render only &nbsp; → treat as empty
            var textInside = HtmlEntity.DeEntitize(root.InnerText ?? "").Trim();
            if (string.IsNullOrEmpty(textInside))
                return new ObservableCollection<DetailsTableDataGroupMatchesBetween>();

            // “rows” (allow extra class tokens, not just exact 'matchitem')
            var rows = root.SelectNodes(".//div[contains(@class,'matchitem')]");
            if (rows == null || rows.Count == 0)
                return new ObservableCollection<DetailsTableDataGroupMatchesBetween>
                {
                    new DetailsTableDataGroupMatchesBetween(new ObservableCollection<DetailsTableDataItemMatchesBetween>())
                };

            static string Clean(HtmlNode? n)
                => HtmlEntity.DeEntitize(n?.InnerText ?? "").Trim();

            static int ToInt(string? s)
                => int.TryParse((s ?? "").Trim(), out var v) ? v : 0;

            var items = new ObservableCollection<DetailsTableDataItemMatchesBetween>();

            foreach (var r in rows)
            {
                var host = r.SelectSingleNode(".//div[contains(@class,'hostteam')]");
                var guest = r.SelectSingleNode(".//div[contains(@class,'guestteam')]");

                // name may be in <a> or plain text
                var hostNameNode = host?.SelectSingleNode(".//div[contains(@class,'name')]//a")
                                   ?? host?.SelectSingleNode(".//div[contains(@class,'name')]");
                var guestNameNode = guest?.SelectSingleNode(".//div[contains(@class,'name')]//a")
                                    ?? guest?.SelectSingleNode(".//div[contains(@class,'name')]");

                var hostName = Clean(hostNameNode);
                var guestName = Clean(guestNameNode);

                var hostGoals = ToInt(Clean(host?.SelectSingleNode(".//div[contains(@class,'goals')]")));
                var guestGoals = ToInt(Clean(guest?.SelectSingleNode(".//div[contains(@class,'goals')]")));

                // HT goals live under details/info/holder/goals (two divs)
                var holder = r.SelectSingleNode(".//div[contains(@class,'details')]//div[contains(@class,'info')]//div[contains(@class,'holder')]");
                var htGoals = holder?.SelectNodes(".//div[contains(@class,'goals')]");
                var hostHT = htGoals != null && htGoals.Count > 0 ? ToInt(Clean(htGoals[0])) : 0;
                var guestHT = htGoals != null && htGoals.Count > 1 ? ToInt(Clean(htGoals[1])) : 0;

                // optional display shortening (your helper)
                //var hostDisplay = renameTeam.renameTeamNameToFitDisplayLabel(hostName);
                //var guestDisplay = renameTeam.renameTeamNameToFitDisplayLabel(guestName);

                items.Add(
                    new DetailsTableDataItemMatchesBetween(
                        new Match(
                            new hostTeam { name = hostName, goals = hostGoals },
                            new guestTeam { name = guestName, goals = guestGoals }),
                        new Details(new info { hostHTGoals = hostHT, guestHTGoals = guestHT })
                    )
                );
            }

            return new ObservableCollection<DetailsTableDataGroupMatchesBetween>
            {
                new DetailsTableDataGroupMatchesBetween(items)
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine("GetMatchesBetweenTeamsHelper.ParseFromHtml error: " + ex.Message);
            return null;
        }
    }
}
