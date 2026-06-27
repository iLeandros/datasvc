using System.Text.Json.Serialization;

namespace DataSvc.Models;

public class TitlesAndHrefs
{
    [JsonPropertyName("dates")] public string? Dates { get; set; }
    [JsonPropertyName("href")]  public string? Href  { get; set; }
}

