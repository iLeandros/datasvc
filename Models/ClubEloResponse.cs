namespace DataSvc.Models;

public class ClubEloResponse
{
    public string UpdatedUtc { get; set; }
    public int Count { get; set; }
    public List<ClubElo> Items { get; set; }
}
