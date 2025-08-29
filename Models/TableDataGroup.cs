using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace DataSvc.Models;

public class TableDataGroup
{
    [JsonPropertyName("ImagePath")]    public string? ImagePath { get; set; }
    [JsonPropertyName("Championship")] public string? Championship { get; set; }
    [JsonPropertyName("Tip")]          public string? Tip { get; set; }

    // Items live here instead of inheriting from a collection
    [JsonPropertyName("Items")]
    public ObservableCollection<TableDataItem> Items { get; set; } = new();

    public TableDataGroup() { }

    public TableDataGroup(string imagepath, string championship, string tip, ObservableCollection<TableDataItem> items)
    {
        ImagePath = imagepath;
        Championship = championship;
        Tip = tip;
        Items = items ?? new ObservableCollection<TableDataItem>();
    }
}
