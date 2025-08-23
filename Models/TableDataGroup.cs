using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace DataSvc.Models;

public class TableDataGroup : ObservableCollection<TableDataItem>
{
    [JsonPropertyName("groupImage")] public string? GroupImage { get; set; }
    [JsonPropertyName("groupName")]  public string? GroupName  { get; set; }
    [JsonPropertyName("tipLabel")]   public string? TipLabel   { get; set; }

    public TableDataGroup() { }

    public TableDataGroup(string groupImage, string groupName, string tipLabel, ObservableCollection<TableDataItem> items)
        : base(items)
    {
        GroupImage = groupImage;
        GroupName  = groupName;
        TipLabel   = tipLabel;
    }
}

