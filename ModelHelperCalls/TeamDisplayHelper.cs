// ModelHelperCalls/TeamDisplayHelper.cs
// FIX M13: renamed from renameTeam (lowercase) to TeamDisplayHelper (PascalCase)
//          method renamed from renameTeamNameToFitDisplayLabel2 to Truncate
//          simplified from multi-line if/else to a single expression
namespace DataSvc.ModelHelperCalls;

public static class TeamDisplayHelper
{
    /// <summary>Truncates a team name to <paramref name="maxLength"/> chars, appending '.' if trimmed.</summary>
    public static string Truncate(string? team, int maxLength = 13)
    {
        if (string.IsNullOrEmpty(team)) return string.Empty;
        return team.Length > maxLength ? team[..maxLength] + "." : team;
    }
}

/// <summary>
/// Backward-compatibility shim — keeps existing call sites compiling.
/// Remove once all callers have been updated to use <see cref="TeamDisplayHelper"/>.
/// </summary>
[Obsolete("Use TeamDisplayHelper.Truncate instead.")]
public static class renameTeam
{
    [Obsolete("Use TeamDisplayHelper.Truncate instead.")]
    public static string renameTeamNameToFitDisplayLabel2(string team)
        => TeamDisplayHelper.Truncate(team);
}
