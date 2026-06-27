namespace DataSvc.Models;

public sealed class BarChartDto
{
    public string? Title { get; set; }
    public string? HalfContainerId { get; set; }
    public List<MatchFactDataDto> Items { get; set; } = new();
}
