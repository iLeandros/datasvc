namespace DataSvc.Models;

public sealed class MatchDetails
{
    public HalfTimeScore HalfTime { get; }
    public IReadOnlyList<MatchAction> Actions { get; }

    public MatchDetails(HalfTimeScore halfTime, IReadOnlyList<MatchAction> actions)
    {
        HalfTime = halfTime;
        Actions = actions ?? Array.Empty<MatchAction>();
    }
}
