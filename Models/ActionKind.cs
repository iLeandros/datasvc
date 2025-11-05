namespace DataSvc.Models;

public enum ActionKind
{
    Goal,
    Penalty,
    YellowCard,
    SecondYellow,   // for ".dycard"
    RedCard,        // if present as ".rcard"
    Unknown
}
