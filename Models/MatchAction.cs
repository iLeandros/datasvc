namespace DataSvc.Models;

public sealed class MatchAction
{
    public TeamSide Side { get; }
    public ActionKind Kind { get; }
    public int? Minute { get; }
    public string Player { get; }

    public MatchAction(TeamSide side, ActionKind kind, int? minute, string player)
    {
        Side = side;
        Kind = kind;
        Minute = minute;
        Player = player ?? string.Empty;
    }

    public override string ToString() => $"{(Minute.HasValue ? Minute + "'" : "")} {Player} [{Side} {Kind}]";
}
/*
public class MatchAction
{
    public string TeamType { get; set; } // "hostteam" or "guestteam"
    public string ActionType { get; set; } // e.g., "goal", "ycard", "penalty", etc.
    public string Player { get; set; }
    public string Time { get; set; } // e.g., "46' Pogba Paul"
}
*/
