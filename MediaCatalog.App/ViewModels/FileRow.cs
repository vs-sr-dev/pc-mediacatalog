using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Models;

namespace MediaCatalog.App.ViewModels;

/// <summary>Display-friendly wrapper around a <see cref="MediaFile"/> for the grid.</summary>
public class FileRow : ObservableObject
{
    private bool _isDuplicate;
    private bool _isNearDuplicate;

    public FileRow(MediaFile model) => Model = model;

    public MediaFile Model { get; }

    public string FileName => Model.FileName;
    public string Kind => Model.Kind.ToString();

    public string Category => Model.Kind == MediaKind.Video
        ? Model.VideoCategory.ToString()
        : "—";

    public string Title => Model.ParsedTitle;
    public string Year => Model.Year?.ToString() ?? "";

    public string SeasonEpisode =>
        Model is { Season: { } s, Episode: { } e }
            ? $"S{s:00}E{e:00}"
            : "";

    public string SizeDisplay => Format.Bytes(Model.SizeBytes);
    public string Integrity => Model.Integrity.ToString();
    public string FullPath => Model.FullPath;

    public bool IsDuplicate
    {
        get => _isDuplicate;
        set { if (SetProperty(ref _isDuplicate, value)) OnPropertyChanged(nameof(DuplicateFlag)); }
    }

    public bool IsNearDuplicate
    {
        get => _isNearDuplicate;
        set { if (SetProperty(ref _isNearDuplicate, value)) OnPropertyChanged(nameof(DuplicateFlag)); }
    }

    /// <summary>Exact byte-duplicate takes precedence over a perceptual near-match.</summary>
    public string DuplicateFlag => IsDuplicate ? "DUP" : IsNearDuplicate ? "~dup" : "";

    public void Refresh()
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FullPath));
        OnPropertyChanged(nameof(SizeDisplay));
    }
}
