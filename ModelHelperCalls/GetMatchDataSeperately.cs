// File: ModelHelperCalls/GetMatchDataSeparately.cs
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models;

namespace DataSvc.ModelHelperCalls;

public static MatchData? GetMatchDataSeparately(string htmlContent)
{
    try
    {
        var data = new MatchData();
        var doc = new HtmlDocument();
        doc.LoadHtml(htmlContent);

        // all match cards under .lastteamsmatches
        var items = doc.DocumentNode.SelectNodes(
            "//*[contains(concat(' ', normalize-space(@class), ' '), ' lastteamsmatches ')]" +
            "//*[contains(concat(' ', normalize-space(@class), ' '), ' matchitem ')]"
        );

        if (items == null || items.Count == 0) return data;

        string Clean(HtmlNode? n) => HtmlEntity.DeEntitize(n?.InnerText ?? "").Trim();
        int ToInt(HtmlNode? n) => int.TryParse(Clean(n), out var v) ? v : 0;

        foreach (var item in items)
        {
            var mi = new MatchItem
            {
                Competition = Clean(item.SelectSingleNode(".//*[contains(@class,'competition')]")),
                Date        = Clean(item.SelectSingleNode(".//*[contains(@class,'date')]")),
                HostTeam    = Clean(item.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' hostteam ')]//*[contains(concat(' ',normalize-space(@class),' '),' name ')]")),
                HostGoals   = ToInt(item.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' hostteam ')]//*[contains(concat(' ',normalize-space(@class),' '),' goals ')]")),
                GuestTeam   = Clean(item.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' guestteam ')]//*[contains(concat(' ',normalize-space(@class),' '),' name ')]")),
                GuestGoals  = ToInt(item.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' guestteam ')]//*[contains(concat(' ',normalize-space(@class),' '),' goals ')]"))
            };

            // actions inside this matchitem
            var actions = item.SelectNodes(".//*[contains(concat(' ',normalize-space(@class),' '),' action ')]");
            if (actions != null)
            {
                foreach (var a in actions)
                {
                    var side = a.Ancestors("div")
                                .FirstOrDefault(n =>
                                {
                                    var c = n.GetAttributeValue("class", "");
                                    return c.Contains("hostteam") || c.Contains("guestteam");
                                })?.GetAttributeValue("class", "") ?? "";

                    var ma = a.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' matchaction ')]");
                    var firstEl = ma?.ChildNodes.FirstOrDefault(n => n.NodeType == HtmlNodeType.Element);
                    var playerNode = a.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' player ')]");

                    var act = new MatchAction
                    {
                        TeamType   = side.Contains("guestteam") ? "guestteam" : (side.Contains("hostteam") ? "hostteam" : ""),
                        ActionType = firstEl?.GetAttributeValue("class", "") ?? "",
                        Player     = Clean(playerNode ?? a)
                    };

                    // extract "45' Name"
                    var s = act.Player;
                    var p = s.IndexOf('\'');
                    if (p > 0) { act.Time = s[..(p + 1)]; act.Player = s[(p + 1)..].Trim(); }

                    mi.Actions.Add(act);
                }
            }

            data.Matches.Add(mi);
        }

        return data;
    }
    catch (Exception ex)
    {
        Debug.WriteLine("GetMatchDataSeparately error: " + ex.Message);
        return null;
    }
}
