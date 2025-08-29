using System.Text.Json.Serialization;

namespace DataSvc.Models;

public class TableDataItem
{
    [JsonPropertyName("ImagePath")] public string? Flag { get; set; }
    [JsonPropertyName("BackgroundTipColour")] public string? BackgroundTipColour { get; set; }
    [JsonPropertyName("Time")] public string? Time { get; set; }

    [JsonPropertyName("TeamOne")] public string? HostTeam { get; set; }
    [JsonPropertyName("ScoreTeamOne")] public string? HostScore { get; set; }
    [JsonPropertyName("ScoreTeamTwo")] public string? GuestScore { get; set; }
    [JsonPropertyName("TeamTwo")] public string? GuestTeam { get; set; }

    [JsonPropertyName("Tip")] public string? Tip { get; set; }
    [JsonPropertyName("Like")] public string? LikeButtonImage { get; set; }
    [JsonPropertyName("Dislike")] public string? DislikeButtonImage { get; set; }
    [JsonPropertyName("Likes")] public string? LikePositive { get; set; }
    [JsonPropertyName("Dislikes")] public string? LikeNegative { get; set; }

    // These already match your client property names (case-insensitive is on)
    public string? One { get; set; }
    public string? Even { get; set; }
    public string? Two { get; set; }
    public string? HalfOne { get; set; }
    public string? HalfEven { get; set; }
    public string? HalfTwo { get; set; }
    public string? OneAbove { get; set; }
    public string? TwoAbove { get; set; }
    public string? ThreeAbove { get; set; }
    public string? BTS { get; set; }
    public string? OTS { get; set; }
    public string? YourPrediction { get; set; }
    public string? Href { get; set; }

    // Optional/extra: your client doesn’t have this; leaving it is fine (it’ll be ignored)
    public string? RowBackground { get; set; }

    public TableDataItem() { }
}
