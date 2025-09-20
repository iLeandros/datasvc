namespace DataSvc.Models;

public sealed class MatchBetweenDto
{
    public List<MatchBetweenItemDto> Matches { get; set; } = new();
}
