namespace DataSvc.Models;

public sealed class TeamStatisticsDto
{
    public string? TeamName { get; set; }
    public string? TeamLogoUrl { get; set; }
    public List<FactItemDto> FactItems { get; set; } = new();
}
