namespace DataSvc.Models;

public class LiveTableDataItemDto
{
    public string Time { get; set; }
    public string LiveTime { get; set; }
    public string LiveTimeBgColor { get; set; } = "Black";

    public string HomeTeam { get; set; }
    public string HomeGoals { get; set; }
    public string AwayGoals { get; set; }
    public string AwayTeam { get; set; }

    public string Score => $"{HomeGoals ?? ""} - {AwayGoals ?? ""}";
    public List<MatchAction> Action { get; set; }
    public string MatchID { get; set; }

    public int? HalfTimeHomeGoals { get; set; }
    public int? HalfTimeAwayGoals { get; set; }
    public bool HasHalfTime => HalfTimeHomeGoals.HasValue && HalfTimeAwayGoals.HasValue;
    public string HalfTimeScore => HasHalfTime ? $"{HalfTimeHomeGoals} - {HalfTimeAwayGoals}" : string.Empty;
}
