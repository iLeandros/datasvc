using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.Maui.Graphics;
using System.Text;

namespace DataSvc.Models;

public class LiveTableDataGroupDto : ObservableCollection<LiveTableDataItemDto>
{
    public string Title { get; }
    public Microsoft.Maui.Graphics.Color HeaderColor { get; }

    public LiveTableDataGroupDto(Microsoft.Maui.Graphics.Color headerColor, string title, ObservableCollection<LiveTableDataItemDto> items)
        : base(items ?? new ObservableCollection<LiveTableDataItemDto>())
    {
        HeaderColor = headerColor;
        Title = title;
    }

    // Optional: total matches for header
    public int CountLabel => this?.Count ?? 0;
}
