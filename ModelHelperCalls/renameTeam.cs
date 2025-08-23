namespace DataSvc.ModelHelperCalls;

public class renameTeam
{
    public static string renameTeamNameToFitDisplayLabel(string team)
    {
        if (team.Length > 13)
        {
            return team.Substring(0, 13) + "."; ;
        }
        else
        {
            return team;
        }
    }
}
