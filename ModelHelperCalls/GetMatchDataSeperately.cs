// File: ModelHelperCalls/GetMatchDataBetween.cs
using System.Diagnostics;
using HtmlAgilityPack;
using DataSvc.Models;

namespace DataSvc.ModelHelperCalls;

public static class MatchBetweenHelper
{
    public static MatchData? GetMatchDataSeparately(string htmlContent)
    {
        try
        {
            var matchData = new MatchData();
            var document = new HtmlDocument();
            document.LoadHtml(htmlContent);

            var matchItems = document.DocumentNode.Descendants("div")
                .Where(o => o.GetAttributeValue("class", "") == "matchbtwteams")
                .SelectMany(o => o.Descendants("div").Where(p => p.GetAttributeValue("class", "") == "matchitem"))
                .ToList();
            Debug.WriteLine("Matches: " + matchItems.Count);

            if (matchItems != null)
            {
                foreach (var item in matchItems)
                {
                    var hostTeamNode  = item.SelectSingleNode(".//div[@class='hostteam']//div[contains(@class, 'name')]");
                    var guestTeamNode = item.SelectSingleNode(".//div[@class='guestteam']//div[contains(@class, 'name')]");

                    var matchItem = new MatchItem
                    {
                        Competition = item.SelectSingleNode(".//div[@class='competition']").InnerText.Trim(),
                        Date        = item.SelectSingleNode(".//div[@class='date']").InnerText.Trim(),
                        HostTeam    = hostTeamNode.InnerText.Trim(),
                        HostGoals   = int.Parse(item.SelectSingleNode(".//div[@class='hostteam']//div[@class='goals']").InnerText.Trim()),
                        GuestTeam   = guestTeamNode.InnerText.Trim(),
                        GuestGoals  = int.Parse(item.SelectSingleNode(".//div[@class='guestteam']//div[@class='goals']").InnerText.Trim())
                    };

                    var actions = item.SelectNodes(".//div[@class='action']");
                    if (actions != null)
                    {
                        foreach (var action in actions)
                        {
                            var matchAction = new MatchAction
                            {
                                TeamType   = action.SelectSingleNode(".//div[@class='matchaction']/../..").GetAttributeValue("class", ""),
                                ActionType = action.SelectSingleNode(".//div[@class='matchaction']").ChildNodes[0].GetAttributeValue("class", ""),
                                Player     = action.SelectSingleNode(".//div[@class='player']").InnerText.Trim()
                            };

                            // peel out "45'+ Player Name"
                            var playerText   = matchAction.Player;
                            var timeEndIndex = playerText.IndexOf('\'');
                            if (timeEndIndex > 0)
                            {
                                matchAction.Time   = playerText.Substring(0, timeEndIndex + 1);
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
}
