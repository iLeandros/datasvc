namespace DataSvc.Models;
public sealed record CommentCountByDateGroupDto(
    string Href,
    IReadOnlyList<CommentCountByDateRowDto> Items
);
