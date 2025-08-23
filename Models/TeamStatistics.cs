namespace DataSvc.Models;

public class TeamStatistics
{
    public string TeamName { get; set; }
    public string TeamLogoUrl { get; set; }
    public List<FactItem> FactItems { get; set; }
}
