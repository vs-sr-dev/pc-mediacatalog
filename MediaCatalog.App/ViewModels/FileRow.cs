using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Models;

namespace MediaCatalog.App.ViewModels;

/// <summary>Display-friendly wrapper around a <see cref="MediaFile"/> for the grid.</summary>
public class FileRow : ObservableObject
{
    private bool _isDuplicate;
    private bool _isNearDuplicate;
    private bool _isTitleDuplicate;
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

    /// <summary>
    /// The year, with a "?" after it when it came from a title that has been used more than
    /// once. The most recent was taken, which is the better bet without being a certainty —
    /// and a figure that might be wrong is worth marking rather than presenting as fact.
    /// </summary>
    public string Year =>
        Model.Year is not { } year ? ""
        : Model.YearAmbiguous ? $"{year} ?"
        : year.ToString();

    /// <summary>"S01E02", or "S06E11-E12" when the file holds a double episode.</summary>
    public string SeasonEpisode => Model.NumberingDisplay;

    public string SizeDisplay => Format.Bytes(Model.SizeBytes);

    /// <summary>Raw size, so the Size column sorts by magnitude rather than by text.</summary>
    public long SizeBytes => Model.SizeBytes;

    /// <summary>How long the file runs, as "1:42:07". Blank until something has measured it.</summary>
    public string Length => Model.LengthDisplay;

    /// <summary>Raw seconds, so the Length column sorts by duration rather than by text.</summary>
    public double DurationSeconds => Model.DurationSeconds;

    /// <summary>Picture height for video ("1080p"), bitrate for audio ("320 kbps").</summary>
    public string Quality => Model.QualityDisplay;

    /// <summary>The raw figure, so the Quality column sorts by magnitude.</summary>
    public int QualityValue => Model.Quality;

    public string Integrity => Model.Integrity.ToString();
    public string FullPath => Model.FullPath;

    /// <summary>
    /// Title provenance: ✎ typed by the user, ✓ confirmed against the local IMDb data or
    /// TMDb, blank when it is still only a guess from the file name.
    /// </summary>
    public string TmdbFlag =>
        Model.TitleManuallySet ? "✎" : Model.TitleVerified ? "✓" : "";

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
        "Length" => Length,
        "Quality" => Quality,
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

    /// <summary>
    /// Another file claims the same title, year and numbering without being the same
    /// bytes — the same film downloaded twice from two different releases.
    /// </summary>
    public bool IsTitleDuplicate
    {
        get => _isTitleDuplicate;
        set { if (SetProperty(ref _isTitleDuplicate, value)) OnPropertyChanged(nameof(DuplicateFlag)); }
    }

    /// <summary>
    /// The strongest claim about this file, in order of how certain it is: byte-for-byte
    /// identical, then the same content in a different encoding, then merely calling itself
    /// the same thing.
    /// </summary>
    public string DuplicateFlag =>
        IsDuplicate ? "DUP" : IsNearDuplicate ? "~dup" : IsTitleDuplicate ? "title" : "";

    public void Refresh()
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FullPath));
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(Length));
        OnPropertyChanged(nameof(Quality));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TmdbFlag));
        OnPropertyChanged(nameof(FiledFlag));
        OnPropertyChanged(nameof(SeasonEpisode));
    }
}
