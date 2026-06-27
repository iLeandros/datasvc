namespace DataSvc.Models;

public class ClubElo
{
    public int Rank { get; set; }
    public string Club { get; set; }
    public string Country { get; set; }
    public int Level { get; set; }
    public double Elo { get; set; }
    public string From { get; set; }
    public string To { get; set; }
}
