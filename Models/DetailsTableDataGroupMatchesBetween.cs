using System.Collections.ObjectModel;

namespace DataSvc.Models;

public class DetailsTableDataGroupMatchesBetween : ObservableCollection<DetailsTableDataItemMatchesBetween>
{
    public DetailsTableDataGroupMatchesBetween(ObservableCollection<DetailsTableDataItemMatchesBetween> animals) : base(animals)
    {
        this.Items = new ObservableCollection<DetailsTableDataItemMatchesBetween>();
    }
    public new ObservableCollection<DetailsTableDataItemMatchesBetween> Items { get; set; }
}
