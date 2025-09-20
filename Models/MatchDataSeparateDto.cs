namespace DataSvc.Models;

public sealed class MatchDataSeparateDto
{
    public List<MatchBetweenItemDto> Matches { get; set; } = new();
}
