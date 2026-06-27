using System.Collections.ObjectModel;

namespace DataSvc.Models;

public class BarChart : ObservableCollection<MatchFactData>
{
    public string Title { get; set; }
    public string HalfContainerId { get; set; }
    public List<MatchFactData> MatchFacts { get; set; }

    public BarChart(string title, List<MatchFactData> matchFacts) : base(matchFacts)
    {
        Title = title;
        MatchFacts = matchFacts;
    }
}
