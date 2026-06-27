using System;
using System.Collections.Generic;
using System.Text;
namespace DataSvc.Models;

public sealed class DetailsPerDateEnvelope
{
    public string? Date { get; set; }
    public int Total { get; set; }
    public DateTimeOffset GeneratedUtc { get; set; }
    public Dictionary<string, DetailsItemDto>? Items { get; set; }
}
