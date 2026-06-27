using System.Collections.ObjectModel;

namespace DataSvc.Models;

public class LiveTableDataGroupDto : ObservableCollection<LiveTableDataItemDto>
{
    public string Title { get; }
    public string HeaderColor { get; }

    public LiveTableDataGroupDto(string headerColor, string title, ObservableCollection<LiveTableDataItemDto> items)
        : base(items ?? new ObservableCollection<LiveTableDataItemDto>())
    {
        HeaderColor = headerColor;
        Title = title;
    }

    public int CountLabel => this?.Count ?? 0;
}
