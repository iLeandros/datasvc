using System;
using System.Collections.Generic;
using System.Text;

namespace DataSvc.Models;

public class LiveScoreItemResponse
{
    public string Time { get; set; }
    public string Status { get; set; }
    public string HomeTeam { get; set; }
    public string HomeGoals { get; set; }
    public string AwayGoals { get; set; }
    public string AwayTeam { get; set; }
    // Half-time goals (optional)
    public string HalfTimeHomeGoals { get; set; }
    public string HalfTimeAwayGoals { get; set; }

    public List<MatchAction> Action { get; set; }
    public string MatchID { get; set; }
}
