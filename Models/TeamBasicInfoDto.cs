namespace DataSvc.Models;

public sealed class TeamBasicInfoDto
{
    public string? teamName { get; set; }
    public string? teamFlag { get; set; }
    public string? country { get; set; }
    public string? countryFlag { get; set; }
    // extend with whatever you include server-side
}
