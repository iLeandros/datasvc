namespace DataSvc.Models;
public sealed class CommentCountDto
{
    public ulong? MatchId { get; set; }
    public int Total { get; set; }
    public int TopLevel { get; set; }
    public int Replies { get; set; }
}
