using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Models;

namespace MediaCatalog.App.ViewModels;

/// <summary>Display-friendly wrapper around a <see cref="MediaFile"/> for the grid.</summary>
public class FileRow : ObservableObject
{
    private bool _isDuplicate;
    private bool _isNearDuplicate;
    private string _category = "";

    public FileRow(MediaFile model) => Model = model;

    public MediaFile Model { get; }

    public string FileName => Model.FileName;
    public string Kind => Model.Kind.ToString();

    /// <summary>Effective category (per-file/folder override or auto), set by the VM.</summary>
    public string Category
    {
        get => _category;
        set => SetProperty(ref _category, value);
    }

    /// <summary>The validated/edited title when there is one, else the parsed one.</summary>
    public string Title => Model.EffectiveTitle;

    public string Year => Model.Year?.ToString() ?? "";

    public string SeasonEpisode =>
        Model is { Season: { } s, Episode: { } e }
            ? $"S{s:00}E{e:00}"
            : "";

    public string SizeDisplay => Format.Bytes(Model.SizeBytes);

    /// <summary>Raw size, so the Size column sorts by magnitude rather than by text.</summary>
    public long SizeBytes => Model.SizeBytes;

    public string Integrity => Model.Integrity.ToString();
    public string FullPath => Model.FullPath;

    /// <summary>Title provenance: ✓ confirmed by TMDb, ✎ typed by the user.</summary>
    public string TmdbFlag => Model.TitleManuallySet ? "✎" : Model.TmdbVerified ? "✓" : "";

    /// <summary>Whether the file has been filed into its consolidation location.</summary>
    public string FiledFlag => Model.Consolidated ? "✓" : "";

    /// <summary>Value of a named column, for wildcard column filtering.</summary>
    public string ColumnValue(string column) => column switch
    {
        "Name" => FileName,
        "Kind" => Kind,
        "Category" => Category,
        "Title" => Title,
        "Year" => Year,
        "S/E" => SeasonEpisode,
        "Size" => SizeDisplay,
        "Integrity" => Integrity,
        "Path" => FullPath,
        "Dup" => DuplicateFlag,
        "TMDb" => TmdbFlag,
        "Filed" => Model.Consolidated ? "yes" : "no",
        _ => ""
    };

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
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TmdbFlag));
        OnPropertyChanged(nameof(FiledFlag));
        OnPropertyChanged(nameof(SeasonEpisode));
    }
}
