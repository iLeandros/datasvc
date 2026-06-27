using System.Collections.ObjectModel;

namespace DataSvc.Models;

public class LiveScoresResponse
{
    public string Date { get; set; }
    public List<LiveScoreGroupResponse> Groups { get; set; }
}
