using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Scanning;

namespace MediaCatalog.App.ViewModels;

/// <summary>A selectable drive/root shown in the scan list.</summary>
public class DriveItem : ObservableObject
{
    private bool _isSelected;

    public DriveItem(ScanRoot root) => Root = root;

    public ScanRoot Root { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Path => Root.Path;

    public string Display =>
        $"{Root.Path}  {(string.IsNullOrWhiteSpace(Root.Label) ? "" : $"[{Root.Label}] ")}" +
        $"— {Format.Bytes(Root.FreeBytes)} free / {Format.Bytes(Root.TotalBytes)}";
}
