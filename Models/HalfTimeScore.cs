namespace DataSvc.Models;

public sealed class HalfTimeScore
{
    public int Host { get; }
    public int Guest { get; }

    public HalfTimeScore(int host, int guest)
    {
        Host = host;
        Guest = guest;
    }

    public override string ToString() => $"{Host}-{Guest}";
}
