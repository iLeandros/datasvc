using System.Collections.ObjectModel;

namespace DataSvc.Models;

public class MatchData
{
    public ObservableCollection<MatchItem> Matches { get; set; } = new ObservableCollection<MatchItem>();
}
