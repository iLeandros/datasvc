namespace DataSvc.ViewModelsAssists;

public static class TeamsInfoParser
{
    // Parse the <div class="teamsinfo">...</div> block into TeamBasicInfo[]
    public static ObservableCollection<TeamBasicInfo>? Parse(string? htmlContent)
    {
        if (string.IsNullOrWhiteSpace(htmlContent)) return null;

        try
        {
            var website = new HtmlDocument();
            website.LoadHtml(htmlContent);

            var halfContainers = website.DocumentNode
                .Descendants("div")
                .FirstOrDefault(o => o.GetAttributeValue("class", "") == "teamsinfo")?
                .Descendants("div")
                .Where(o => o.GetAttributeValue("class", "") == "halfcontainer")
                .ToList();

            if (halfContainers == null || halfContainers.Count < 2)
                return null; // shape not found

            var teamBasicInfos = new ObservableCollection<TeamBasicInfo>();

            foreach (var container in halfContainers)
            {
                var teamName      = container.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("class", "") == "name")?.InnerText?.Trim();
                var teamLogo      = container.Descendants("img").FirstOrDefault()?.GetAttributeValue("src", string.Empty);
                var countryEl     = container.Descendants("div").FirstOrDefault(o => o.GetAttributeValue("id", "") == "teamcountry");
                var country       = countryEl?.Descendants("span").FirstOrDefault()?.InnerText?.Trim();
                var countryFlag   = countryEl?.Descendants("img").FirstOrDefault()?.GetAttributeValue("src", string.Empty);

                teamBasicInfos.Add(new TeamBasicInfo
                {
                    TeamName    = teamName,
                    TeamFlag    = teamLogo,
                    Country     = country,
                    CountryFlag = countryFlag
                });
            }

            return teamBasicInfos;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("TeamsInfoParser.Parse error: " + ex.Message);
            return null;
        }
    }
}

