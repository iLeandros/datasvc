using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace DataSvc.Models;

public class TableDataGroup : ObservableCollection<TableDataItem>
{
    [JsonPropertyName("ImagePath")] public string? ImagePath { get; set; }
    [JsonPropertyName("Championship")]  public string? Championship  { get; set; }
    [JsonPropertyName("Tip")]   public string? Tip   { get; set; }

    public TableDataGroup() { }

    public TableDataGroup(string groupImage, string groupName, string tipLabel, ObservableCollection<TableDataItem> items)
        : base(items)
    {
        ImagePath = groupImage;
        Championship  = groupName;
        Tip   = tipLabel;
    }
}

