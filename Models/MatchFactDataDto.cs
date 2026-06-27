namespace DataSvc.Models;

public sealed class MatchFactDataDto
{
    // server sends PascalCase here (from your model), but deserializer is case-insensitive
    public string? Name { get; set; }
    public int Percentage { get; set; }
    public double NormalizedPercentage { get; set; }
}
