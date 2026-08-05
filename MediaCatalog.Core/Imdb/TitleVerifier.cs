using MediaCatalog.Core.Models;
using MediaCatalog.Core.Tmdb;

namespace MediaCatalog.Core.Imdb;

public record VerifyProgress(int Done, int Total, string Current, string Phase);

/// <param name="ImdbVerified">Titles confirmed against the local IMDb extract.</param>
/// <param name="TmdbVerified">Titles the extract didn't know, confirmed online instead.</param>
/// <param name="YearsFilled">Films and shows given the year they were missing.</param>
/// <param name="Unresolved">Files neither source could identify.</param>
/// <param name="YearsUncertain">
/// Of those years, how many came from a title that has been used more than once. The most
/// recent was taken; these are the ones worth a second look, and they are flagged on the
/// files themselves so they can be found again later.
/// </param>
/// <param name="GenresFilled">Files that gained the genres recorded against their title.</param>
public record VerifyReport(
    int ImdbVerified, int TmdbVerified, int YearsFilled, int Unresolved, int YearsUncertain = 0,
    int GenresFilled = 0)
{
    public string Describe() =>
        $"{ImdbVerified} title(s) confirmed from IMDb, {TmdbVerified} from TMDb, " +
        $"{YearsFilled} year(s) filled in, {Unresolved} still unidentified." +
        (GenresFilled > 0 ? $"\n{GenresFilled} file(s) gained their genres." : "") +
        (YearsUncertain > 0
            ? $"\n{YearsUncertain} of those year(s) came from a title that has been used more " +
              "than once — the most recent was taken, and they are marked with a ? in the Year " +
              "column. The \"Uncertain year\" view lists them."
            : "");
}

/// <summary>
/// Confirms film and programme titles, and fills in missing years.
///
/// The local IMDb extract is consulted first and answers the whole catalogue in one pass
/// with no rate limit; only what it cannot identify goes to TMDb, which allows one query
/// every two seconds and so is worth spending sparingly.
/// </summary>
public class TitleVerifier
{
    private readonly ImdbTitleIndex _imdb;
    private readonly TmdbClient? _tmdb;
    private readonly bool _useImdb;

    /// <param name="useImdb">
    /// False to leave the local extract out of the title check and go straight to TMDb.
    /// Years are still filled in from it either way: nothing else can supply them.
    /// </param>
    public TitleVerifier(ImdbTitleIndex imdb, TmdbClient? tmdb = null, bool useImdb = true)
    {
        _imdb = imdb;
        _tmdb = tmdb;
        _useImdb = useImdb;
    }

    /// <summary>Files whose title is still only a guess from the file name.</summary>
    public static bool NeedsVerification(MediaFile file) =>
        file.Kind == MediaKind.Video &&
        file.VideoCategory is VideoCategory.Movie or VideoCategory.TvShow
            or VideoCategory.MovieExtra or VideoCategory.TvExtra &&
        !file.TitleVerified;

    /// <summary>Video files that have a title to look up but no year against it.</summary>
    public static bool NeedsYear(MediaFile file) =>
        file.Kind == MediaKind.Video &&
        file.Year is null &&
        !string.IsNullOrWhiteSpace(file.EffectiveTitle);

    /// <summary>Video files that have a title but nothing yet saying what sort of thing it is.</summary>
    public static bool NeedsGenres(MediaFile file) =>
        file.Kind == MediaKind.Video &&
        string.IsNullOrWhiteSpace(file.Genres) &&
        !string.IsNullOrWhiteSpace(file.EffectiveTitle);

    /// <summary>
    /// Verify <paramref name="targets"/>, fill in their missing years and record their
    /// genres. All three share one IMDb pass, since they ask the same kind of question.
    /// </summary>
    public async Task<VerifyReport> VerifyAsync(
        IReadOnlyList<MediaFile> targets,
        IProgress<VerifyProgress>? progress = null,
        CancellationToken ct = default)
    {
        var toVerify = targets.Where(NeedsVerification).ToList();
        var toYear = targets.Where(NeedsYear).ToList();
        var toGenre = targets.Where(NeedsGenres).ToList();
        if (toVerify.Count == 0 && toYear.Count == 0 && toGenre.Count == 0)
            return new VerifyReport(0, 0, 0, 0);

        // Everything we might ask IMDb about, asked once.
        var questions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidatesOf = new Dictionary<MediaFile, List<string>>();

        foreach (var file in toVerify)
        {
            var candidates = TvNameValidator.Candidates(file).Take(MaxCandidates).ToList();
            candidatesOf[file] = candidates;
            foreach (var c in candidates) questions.Add(c);
        }
        foreach (var file in toYear) questions.Add(file.EffectiveTitle);
        foreach (var file in toGenre) questions.Add(file.EffectiveTitle);

        progress?.Report(new VerifyProgress(0, toVerify.Count, string.Empty,
            _imdb.IsLoaded ? "Checking IMDb titles" : "Reading IMDBData.tsv"));

        var answers = _imdb.IsAvailable
            ? await _imdb.LookupManyAsync(questions.ToList(), ct)
            : new Dictionary<string, ImdbMatch?>(StringComparer.OrdinalIgnoreCase);

        ImdbMatch? Ask(string title) =>
            answers.TryGetValue(title, out var m) ? m : null;

        // --- Pass one: the local extract ---
        var imdbVerified = 0;
        var unresolved = new List<MediaFile>();

        for (var i = 0; i < toVerify.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = toVerify[i];
            progress?.Report(new VerifyProgress(i, toVerify.Count, file.FileName, "Checking IMDb titles"));

            var hit = _useImdb
                ? candidatesOf[file].Select(Ask).FirstOrDefault(m => m != null)
                : null;
            if (hit == null) { unresolved.Add(file); continue; }

            file.TmdbName = hit.Title;
            file.ImdbVerified = true;
            ApplyGenres(file, hit);
            imdbVerified++;
        }

        // --- Pass two: TMDb for what is left ---
        // Films go to the film index and programmes to the programme one: TMDb keeps them
        // apart, and *Fargo* is both. Each is tried under the name parsed from the file and
        // then under its containing folders — the folder is often the only place a film's
        // name survives, a release called "xvid-grp.avi" sitting inside "Blade Runner (1982)".
        var tmdbVerified = 0;
        if (_tmdb is { IsConfigured: true })
        {
            var online = unresolved.ToList();

            for (var i = 0; i < online.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = online[i];
                progress?.Report(new VerifyProgress(i, online.Count, file.FileName, "Checking TMDb titles"));

                var isFilm = file.VideoCategory is VideoCategory.Movie or VideoCategory.MovieExtra;

                foreach (var candidate in candidatesOf[file])
                {
                    var result = isFilm
                        ? await _tmdb.ValidateMovieAsync(candidate, ct)
                        : await _tmdb.ValidateTvAsync(candidate, ct);
                    if (!result.Found) continue;

                    file.TmdbName = result.CanonicalName;
                    file.TmdbVerified = true;
                    tmdbVerified++;
                    unresolved.Remove(file);
                    break;
                }
            }
        }

        // --- Years ---
        // Done last, so a title just confirmed above is the one we look the year up under.
        var yearsFilled = 0;
        var yearsUncertain = 0;
        foreach (var file in toYear)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Year is not null) continue;

            var match = Ask(file.EffectiveTitle);
            if (match?.Year is not { } year) continue;

            file.Year = year;

            // One title, several things released under it. The newest is the better bet —
            // the copy somebody has is far more often the current release than the
            // fifty-year-old one — but it is still a bet, and the file says so.
            file.YearAmbiguous = match.Ambiguous;
            file.AlternativeYear = match.Ambiguous ? match.EarliestYear : null;
            if (match.Ambiguous) yearsUncertain++;

            yearsFilled++;
        }

        // --- Genres ---
        // Asked of everything that has a title, not only of what was verified above: a file
        // whose title was already confirmed by an earlier run has no genres against it yet,
        // and this is the pass that fills them in. Nothing extra is read to do it — the
        // answers are the ones already in hand.
        var genresFilled = 0;
        foreach (var file in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(file.Genres)) continue;
            if (Ask(file.EffectiveTitle) is { } genreMatch && ApplyGenres(file, genreMatch))
                genresFilled++;
        }

        progress?.Report(new VerifyProgress(toVerify.Count, toVerify.Count, string.Empty, "Done"));
        return new VerifyReport(
            imdbVerified, tmdbVerified, yearsFilled, unresolved.Count, yearsUncertain, genresFilled);
    }

    /// <summary>
    /// Record the genres IMDb has against a title, if it has any and the file has none.
    /// A genre already on the file is left alone: it may have been corrected by hand.
    /// </summary>
    private static bool ApplyGenres(Models.MediaFile file, ImdbMatch match)
    {
        if (!string.IsNullOrWhiteSpace(file.Genres)) return false;
        if (match.GenreNames.Count == 0) return false;

        file.Genres = string.Join(", ", match.GenreNames);
        return true;
    }

    /// <summary>
    /// How far up the folder tree to look for a name. Deep paths otherwise contribute
    /// dozens of increasingly meaningless candidates ("Media", "D:") per file.
    /// </summary>
    private const int MaxCandidates = 6;
}
