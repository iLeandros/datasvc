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
    public string? RowBackground { get; set; }

    public TableDataItem() { }

    public TableDataItem(
        string flag, string backgroundTipColour, string time,
        string hostTeam, string? hostScore, string? guestScore, string guestTeam,
        string? tip, string likeButtonImage, string dislikeButtonImage,
        string? likePositive, string? likeNegative,
        string? coef1, string? coef2, string? coef3, string? coef4, string? coef5,
        string? coef6, string? coef7, string? coef8, string? coef9, string? coef10, string? coef11,
        string version, string? href, string rowBackground)
    {
        Flag = flag;
        BackgroundTipColour = backgroundTipColour;
        Time = time;
        HostTeam = hostTeam;
        HostScore = hostScore;
        GuestScore = guestScore;
        GuestTeam = guestTeam;
        Tip = tip;
        LikeButtonImage = likeButtonImage;
        DislikeButtonImage = dislikeButtonImage;
        LikePositive = likePositive;
        LikeNegative = likeNegative;
        One = coef1; Even = coef2; Two = coef3; HalfOne = coef4; HalfEven = coef5;
        HalfTwo = coef6; OneAbove = coef7; TwoAbove = coef8; ThreeAbove = coef9; BTS = coef10; OTS = coef11;
        YourPrediction = version;
        Href = href;
        RowBackground = rowBackground;
    }
}
