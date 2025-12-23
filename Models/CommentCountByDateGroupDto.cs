namespace DataSvc.Models;
public sealed record CommentCountByDateGroupDto(
    public string? Href { get; set; }
    public IReadOnlyList<CommentCountDto> Items { get; set; }
);
