using System.Text.Json.Serialization;

namespace DataSvc.Models;

public class TableDataItem
{
    public string? Flag { get; set; }
    public string? BackgroundTipColour { get; set; }
    public string? Time { get; set; }
    public string? HostTeam { get; set; }
    public string? HostScore { get; set; }
    public string? GuestScore { get; set; }
    public string? GuestTeam { get; set; }
    public string? Tip { get; set; }
    public string? LikeButtonImage { get; set; }
    public string? DislikeButtonImage { get; set; }
    public string? LikePositive { get; set; }
    public string? LikeNegative { get; set; }
    public string? Coef1 { get; set; }
    public string? Coef2 { get; set; }
    public string? Coef3 { get; set; }
    public string? Coef4 { get; set; }
    public string? Coef5 { get; set; }
    public string? Coef6 { get; set; }
    public string? Coef7 { get; set; }
    public string? Coef8 { get; set; }
    public string? Coef9 { get; set; }
    public string? Coef10 { get; set; }
    public string? Coef11 { get; set; }
    public string? Version { get; set; }
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
        Coef1 = coef1; Coef2 = coef2; Coef3 = coef3; Coef4 = coef4; Coef5 = coef5;
        Coef6 = coef6; Coef7 = coef7; Coef8 = coef8; Coef9 = coef9; Coef10 = coef10; Coef11 = coef11;
        Version = version;
        Href = href;
        RowBackground = rowBackground;
    }
}
