using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinISOBuilder.Models;

public record IsoInfo
{
    public string FilePath { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public bool IsExtractedFolder { get; init; }
    public string ImageName { get; init; } = "";
    public string Architecture { get; init; } = "";
    public List<EditionItem> Editions { get; init; } = [];
}

public class EditionItem : INotifyPropertyChanged
{
    public int Index { get; init; }
    public string Name { get; init; } = "";

    public string DisplayText => $"Index {Index} - {Name}";

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
