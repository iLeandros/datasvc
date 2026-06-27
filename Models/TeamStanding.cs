namespace DataSvc.Models;

public class TeamStanding
{
    public int Position { get; set; }
    public string TeamName { get; set; }
    public string TeamUrl { get; set; }
    public int Matches { get; set; }
    public int Points { get; set; }
    public Record Overall { get; set; }
    public Record Home { get; set; }
    public Record Away { get; set; }
    public bool IsHighlite { get; set; } // New property
    public bool IsMark { get; set; } // Indicates if the row is highlighted with "mark"
    public bool IsHeader { get; set; } // Indicates if the row is highlighted with "mark"
}
