using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace DataSvc.Models;

public class TableDataGroup : ObservableCollection<TableDataItem>
{
    // Needed by System.Text.Json to create the collection instance
    public TableDataGroup() : base() { }

    public TableDataGroup(string imagepath, string championship, string tip, ObservableCollection<TableDataItem> items)
        : base(items)
    {
        ImagePath = imagepath;
        Championship = championship;
        Tip = tip;
    }

    // Make setters public so JSON (or your code) can assign them
    public string ImagePath { get; set; }
    public string Championship { get; set; }
    public string Tip { get; set; }
}
