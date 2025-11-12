using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Maui.Graphics;

namespace DataSvc.Models;

public class LiveTableDataItemDto
{
    public string Time { get; set; }
    public string LiveTime { get; set; }
    public icrosoft.Maui.Graphics.Color LiveTimeBgColor { get; set; } = icrosoft.Maui.Graphics.Colors.Black;

    public string HomeTeam { get; set; }
    public string HomeGoals { get; set; }
    public string AwayGoals { get; set; }
    public string AwayTeam { get; set; }

    // Handy for binding: "1 - 0"
    public string Score => $"{HomeGoals ?? ""} - {AwayGoals ?? ""}";
    public List<MatchAction> Action { get; set; }
    public string MatchID { get; set; }
}
