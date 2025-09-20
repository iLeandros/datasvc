namespace DataSvc.Models;

public sealed class TeamStandingDto
{
    public int Position { get; set; }
    public string? TeamName { get; set; }
    public string? TeamUrl { get; set; }
    public int Matches { get; set; }
    public int Points { get; set; }
    public RecordDto Overall { get; set; } = new();
    public RecordDto Home { get; set; } = new();
    public RecordDto Away { get; set; } = new();
    public bool IsHighlite { get; set; }
    public bool IsMark { get; set; }
}
