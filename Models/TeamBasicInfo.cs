using System.Text.Json.Serialization;

namespace DataSvc.Models;

public class TeamBasicInfo : INotifyPropertyChanged
{
    private string teamName;
    private string teamFlag;
    private string country;
    private string countryFlag;

    public string TeamName
    {
        get => teamName;
        set
        {
            teamName = value;
            OnPropertyChanged(nameof(TeamName));
        }
    }

    public string TeamFlag
    {
        get => teamFlag;
        set
        {
            teamFlag = value;
            OnPropertyChanged(nameof(TeamFlag));
        }
    }

    public string Country
    {
        get => country;
        set
        {
            country = value;
            OnPropertyChanged(nameof(Country));
        }
    }

    public string CountryFlag
    {
        get => countryFlag;
        set
        {
            countryFlag = value;
            OnPropertyChanged(nameof(CountryFlag));
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
