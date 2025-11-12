using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using DataSvc.Models;

namespace DataSvc.ModelHelperCalls;

public static class TeamsInfoParserDto
{
    public static List<TeamBasicInfoDto>? Parse(string? htmlContent)
    {
        if (string.IsNullOrWhiteSpace(htmlContent)) return null;

        try
        {
            var website = new HtmlAgilityPack.HtmlDocument();
            website.LoadHtml(htmlContent);

            var halfContainers = website.DocumentNode.Descendants("div")
                .FirstOrDefault(o => o.GetAttributeValue("class", "") == "teamsinfo")?
                .Descendants("div").Where(o => o.GetAttributeValue("class", "") == "halfcontainer")
                .ToList();

            if (halfContainers == null || halfContainers.Count < 2) return null;

            var list = new List<TeamBasicInfoDto>(capacity: halfContainers.Count);
            foreach (var container in halfContainers)
            {
                var teamName    = container.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "name")?.InnerText?.Trim();
                var teamLogo    = container.Descendants("img").FirstOrDefault()?.GetAttributeValue("src", string.Empty);
                var countryEl   = container.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("id", "") == "teamcountry");
                var country     = countryEl?.Descendants("span").FirstOrDefault()?.InnerText?.Trim();
                var countryFlag = countryEl?.Descendants("img").FirstOrDefault()?.GetAttributeValue("src", string.Empty);

                list.Add(new TeamBasicInfoDto
                {
                    teamName    = teamName,
                    teamFlag    = teamLogo,
                    country     = country,
                    countryFlag = countryFlag
                });
            }
            return list;
        }
        catch
        {
            return null;
        }
    }
}
