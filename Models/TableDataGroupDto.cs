using System.Collections.Generic;

namespace DataSvc.Models;

public class TableDataGroupDto
{
    public string? GroupImage { get; set; }
    public string? GroupName  { get; set; }
    public string? TipLabel   { get; set; }
    public List<TableDataItem> Items { get; set; } = new();
}
