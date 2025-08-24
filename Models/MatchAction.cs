namespace DataSvc.Models;

public class MatchAction
{
    public string TeamType { get; set; } // "hostteam" or "guestteam"
    public string ActionType { get; set; } // e.g., "goal", "ycard", "penalty", etc.
    public string Player { get; set; }
    public string Time { get; set; } // e.g., "46' Pogba Paul"
}
