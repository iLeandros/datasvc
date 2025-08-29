using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace DataSvc.Models;

public class TableDataGroup
{
    [JsonPropertyName("ImagePath")]     public string? ImagePath     { get; set; }
    [JsonPropertyName("Championship")]  public string? Championship  { get; set; }
    [JsonPropertyName("Tip")]           public string? Tip           { get; set; }

    // If you want the JSON key to be exactly "Items" (not "items"), keep the attribute:
    [JsonPropertyName("Items")] public ObservableCollection<TableDataItem> Items { get; set; } = new();

    public TableDataGroup() { }

    public TableDataGroup(string groupImage, string groupName, string tipLabel, ObservableCollection<TableDataItem> items)
    {
        ImagePath = groupImage;
        Championship = groupName;
        Tip = tipLabel;
        Items = items ?? new ObservableCollection<TableDataItem>();
    }
}
