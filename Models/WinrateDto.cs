namespace DataSvc.Models;

public sealed class WinrateDto
{
    public int[] Wins { get; set; } = Array.Empty<int>();   // [team1, team2]
    public int[] Draws { get; set; } = Array.Empty<int>();
    public int[] Losses { get; set; } = Array.Empty<int>();
}
