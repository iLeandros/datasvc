using System.Diagnostics;
using HtmlAgilityPack;

namespace DataSvc.ModelHelperCalls;

public static MatchData GetMatchDataBetween(string htmlContent)
{
    try
    {
        var matchData = new MatchData();
        var document = new HtmlDocument();
        document.LoadHtml(htmlContent);

        // Find the match items
        var matchItems = document.DocumentNode.Descendants("div")
            .Where(o => o.GetAttributeValue("class", "") == "matchbtwteams")
            .SelectMany(o => o.Descendants("div").Where(p => p.GetAttributeValue("class", "") == "matchitem"))
            .ToList();
        Debug.WriteLine("Matches: " + matchItems.Count);

        if (matchItems != null)
        {
            foreach (var item in matchItems)
            {
                var hostTeamNode = item.SelectSingleNode(".//div[@class='hostteam']//div[contains(@class, 'name')]");
                var guestTeamNode = item.SelectSingleNode(".//div[@class='guestteam']//div[contains(@class, 'name')]");

                var matchItem = new MatchItem
                {
                    Competition = item.SelectSingleNode(".//div[@class='competition']").InnerText.Trim(),
                    Date = item.SelectSingleNode(".//div[@class='date']").InnerText.Trim(),
                    HostTeam = hostTeamNode.InnerText.Trim(),
                    HostGoals = int.Parse(item.SelectSingleNode(".//div[@class='hostteam']//div[@class='goals']").InnerText.Trim()),
                    GuestTeam = guestTeamNode.InnerText.Trim(),
                    GuestGoals = int.Parse(item.SelectSingleNode(".//div[@class='guestteam']//div[@class='goals']").InnerText.Trim())
                };
                //Debug.WriteLine($"Host Team: {matchItem.HostTeam}, Guest Team: {matchItem.GuestTeam}");

                var actions = item.SelectNodes(".//div[@class='action']");
                if (actions != null)
                {
                    foreach (var action in actions)
                    {
                        var matchAction = new MatchAction
                        {
                            TeamType = action.SelectSingleNode(".//div[@class='matchaction']/../..").GetAttributeValue("class", ""),
                            ActionType = action.SelectSingleNode(".//div[@class='matchaction']").ChildNodes[0].GetAttributeValue("class", ""),
                            Player = action.SelectSingleNode(".//div[@class='player']").InnerText.Trim()
                        };

                        // Extract time from player field
                        var playerText = matchAction.Player;
                        var timeEndIndex = playerText.IndexOf('\'');
                        if (timeEndIndex > 0)
                        {
                            matchAction.Time = playerText.Substring(0, timeEndIndex + 1);
                            matchAction.Player = playerText.Substring(timeEndIndex + 1).Trim();
                        }

                        matchItem.Actions.Add(matchAction);
                    }
                }

                matchData.Matches.Add(matchItem);
            }
        }

        return matchData;
    }
    catch (Exception ex)
    {
        Debug.WriteLine("GetMatchDataBetween error: " + ex.Message);
        return null;
    }
}
