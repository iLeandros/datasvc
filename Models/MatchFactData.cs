using System.Collections.ObjectModel;

public class MatchFactData
{
    public string Name { get; set; }
    public int Percentage { get; set; }
    // Add a property for the normalized percentage
    public double NormalizedPercentage => Percentage / 100.0;
}
