namespace DataSvc.Models;

public static class renameTeam
{
    public static string renameTeamNameToFitDisplayLabel(string? name) => name?.Trim() ?? "";
}
