using System.Globalization;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Consolidation;

/// <summary>
/// What makes two files copies of one thing, for a category the user has set rules on.
/// </summary>
public enum DuplicateMatch
{
    /// <summary>Byte-for-byte identical, plus anything claiming the same title and year.
    /// The built-in judgement, and what every catalogue used before rules existed.</summary>
    SameContentOrTitle = 0,

    /// <summary>The same bytes and nothing else — the strictest reading there is.</summary>
    SameContent = 1,

    /// <summary>The same file name, wherever the two files are. Nothing else is looked at.</summary>
    SameName = 2,

    /// <summary>The same title and year, whatever the files are called or contain.</summary>
    SameTitle = 3
}

/// <summary>Something about a file that a rule can compare between two copies of one thing.</summary>
public enum ConsolidationField
{
    /// <summary>How long it runs, in seconds. Needs a length to have been read.</summary>
    Length = 0,

    /// <summary>Picture height for video, bitrate for audio.</summary>
    Quality = 1,

    /// <summary>Size on disk, in bytes.</summary>
    Size = 2,

    /// <summary>Last modified, as the file system has it.</summary>
    Modified = 3,

    /// <summary>Whether a deep check found it sound. Ok beats not-checked beats corrupt.</summary>
    Integrity = 4,

    /// <summary>Whether the copy is already in the library, where it belongs.</summary>
    AlreadyFiled = 5,

    /// <summary>How long the file's name is — a tie-break for the plainer of two names.</summary>
    NameLength = 6
}

/// <summary>Which end of a comparison wins.</summary>
public enum RulePreference
{
    /// <summary>The greater value is kept: the longer cut, the better picture, the newer file.</summary>
    Greater = 0,

    /// <summary>The lesser value is kept: the smaller file, the older copy, the shorter name.</summary>
    Lesser = 1
}

/// <summary>
/// One step in deciding which copy of a thing to keep: a field, and which way round it
/// counts. Steps are applied in order and the first that can tell the copies apart decides,
/// which is what makes "the longer one, and if they run the same the better picture"
/// expressible as two lines instead of a paragraph.
/// </summary>
public class ConsolidationRule
{
    public ConsolidationField Field { get; set; } = ConsolidationField.Quality;

    public RulePreference Prefer { get; set; } = RulePreference.Greater;

    /// <summary>
    /// How far apart two values have to be before this step is willing to decide. Zero
    /// means any difference at all counts.
    ///
    /// It earns its place on length above all: two rips of one film that differ by four
    /// seconds differ by a distributor's ident, and letting that pick the winner means the
    /// picture quality — the thing anybody would actually choose on — never gets a say.
    /// </summary>
    public double Tolerance { get; set; }

    /// <summary>The rule reads as its sentence wherever one is shown — a list, a log, a tooltip.</summary>
    public override string ToString() => Describe();

    /// <summary>The rule as a sentence, for the settings list and the wizard.</summary>
    public string Describe()
    {
        var direction = Prefer == RulePreference.Greater ? "the greater" : "the lesser";
        var margin = Tolerance > 0
            ? $", when they differ by more than {Tolerance.ToString("0.##", CultureInfo.InvariantCulture)}"
            : "";
        return $"Keep {direction} {Label(Field).ToLowerInvariant()}{margin}";
    }

    /// <summary>What a field is called on screen.</summary>
    public static string Label(ConsolidationField field) => field switch
    {
        ConsolidationField.Length => "Length",
        ConsolidationField.Quality => "Quality",
        ConsolidationField.Size => "Size",
        ConsolidationField.Modified => "Date modified",
        ConsolidationField.Integrity => "Integrity",
        ConsolidationField.AlreadyFiled => "Already in the library",
        ConsolidationField.NameLength => "Length of the name",
        _ => field.ToString()
    };

    /// <summary>What a field means, for the wizard's explanation panel.</summary>
    public static string Explain(ConsolidationField field) => field switch
    {
        ConsolidationField.Length =>
            "How long the file runs. Blank until something has measured it — a scan with " +
            "ffprobe available, or Verify on the right-click menu.",
        ConsolidationField.Quality =>
            "Picture height for video (720, 1080, 2160) and bitrate for audio. Measured " +
            "alongside the length.",
        ConsolidationField.Size => "Size on disk. Always known, so this step can always decide.",
        ConsolidationField.Modified => "The date the file system carries. Newer is not better, only newer.",
        ConsolidationField.Integrity =>
            "What a deep check made of it: sound beats never-checked, and never-checked " +
            "beats corrupt. Only a deep check fills this in.",
        ConsolidationField.AlreadyFiled =>
            "Whether the copy is already in the library folder it belongs in. Preferring it " +
            "keeps the library still and moves nothing.",
        ConsolidationField.NameLength =>
            "How many characters the name runs to. A tie-break, for when everything that " +
            "matters agrees and one of the two is called something plainer.",
        _ => string.Empty
    };
}

/// <summary>What a rule set made of a group of copies.</summary>
/// <param name="Winner">The copy to keep, or null when the rules could not tell them apart.</param>
/// <param name="Why">
/// The step that decided, in words, so the user is never told a file was chosen without
/// being told what chose it.
/// </param>
/// <param name="Undecided">True when every step ran out with the copies still level.</param>
public record RuleVerdict(MediaFile? Winner, string Why, bool Undecided)
{
    public static RuleVerdict None(string why) => new(null, why, true);
}

/// <summary>
/// Applies the consolidation rules a user has built for a category: which files count as
/// copies of one thing, and which of those copies is the one to keep.
///
/// Every step is a comparison between two files and nothing more, so a set of them is
/// applied to a group by playing the group off against itself — the copy that survives every
/// comparison is the one the rules choose. That keeps the wizard honest as well: two sample
/// files and the steps in order is exactly what the engine does.
/// </summary>
public static class ConsolidationRules
{
    /// <summary>
    /// The copy the rules keep out of <paramref name="copies"/>, or an undecided verdict
    /// when the steps run out with nothing between them.
    /// </summary>
    public static RuleVerdict Choose(IReadOnlyList<MediaFile> copies, IReadOnlyList<ConsolidationRule> rules)
    {
        if (copies.Count == 0) return RuleVerdict.None("There is nothing to choose between.");
        if (copies.Count == 1) return new RuleVerdict(copies[0], "It is the only copy.", false);
        if (rules.Count == 0) return RuleVerdict.None("No rules are set for this category.");

        var best = copies[0];
        var why = string.Empty;

        for (var i = 1; i < copies.Count; i++)
        {
            var (winner, reason) = Between(best, copies[i], rules);
            if (winner == null) return RuleVerdict.None(
                "The rules cannot tell these copies apart: " + reason);
            best = winner;
            why = reason;
        }

        return new RuleVerdict(best, why, false);
    }

    /// <summary>
    /// The winner of one pair, and the step that decided it. A null winner means every step
    /// found them level.
    /// </summary>
    public static (MediaFile? Winner, string Why) Between(
        MediaFile a, MediaFile b, IReadOnlyList<ConsolidationRule> rules)
    {
        foreach (var rule in rules)
        {
            var left = ValueOf(a, rule.Field);
            var right = ValueOf(b, rule.Field);

            // A step nothing has measured decides nothing. Choosing on a length that is
            // zero at both ends because nobody ran ffprobe is choosing at random.
            if (left is not { } x || right is not { } y) continue;

            var margin = Math.Max(0, rule.Tolerance);
            if (Math.Abs(x - y) <= margin) continue;

            var greaterWins = rule.Prefer == RulePreference.Greater;
            var winner = (x > y) == greaterWins ? a : b;
            return (winner, $"{rule.Describe()} — {Show(rule.Field, x)} against {Show(rule.Field, y)}");
        }

        return (null, "every step found them equal");
    }

    /// <summary>
    /// A field's value as a number, or null when nothing has measured it and the step should
    /// stand aside.
    /// </summary>
    public static double? ValueOf(MediaFile file, ConsolidationField field) => field switch
    {
        ConsolidationField.Length => file.DurationSeconds > 0 ? file.DurationSeconds : null,
        ConsolidationField.Quality => file.Quality > 0 ? file.Quality : null,
        ConsolidationField.Size => file.SizeBytes > 0 ? file.SizeBytes : null,
        ConsolidationField.Modified => file.LastModifiedUtc > DateTime.MinValue
            ? file.LastModifiedUtc.Ticks
            : null,
        // Sound beats never-checked beats corrupt: a file nothing has decoded is not known
        // to be bad, and should not lose to one that is.
        ConsolidationField.Integrity => file.Integrity switch
        {
            IntegrityStatus.Ok => 2,
            IntegrityStatus.NotChecked => 1,
            _ => 0
        },
        ConsolidationField.AlreadyFiled => file.Consolidated ? 1 : 0,
        ConsolidationField.NameLength => file.FileName.Length,
        _ => null
    };

    /// <summary>A field's value as the user would read it.</summary>
    public static string Show(ConsolidationField field, double value) => field switch
    {
        ConsolidationField.Length => TimeSpan.FromSeconds(value).ToString(@"h\:mm\:ss"),
        ConsolidationField.Quality => ((int)value).ToString(CultureInfo.InvariantCulture),
        ConsolidationField.Size => Bytes((long)value),
        ConsolidationField.Modified => new DateTime((long)value).ToLocalTime()
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ConsolidationField.Integrity => value switch { >= 2 => "sound", >= 1 => "not checked", _ => "corrupt" },
        ConsolidationField.AlreadyFiled => value >= 1 ? "in the library" : "not filed",
        _ => value.ToString("0.##", CultureInfo.InvariantCulture)
    };

    private static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }

    /// <summary>
    /// True when two files count as copies of one thing under <paramref name="match"/>.
    /// The strictness is the user's: somebody who files by hand and knows their own naming
    /// may well want two files with one name treated as one thing, hashes or no hashes.
    /// </summary>
    public static bool AreCopies(MediaFile a, MediaFile b, DuplicateMatch match)
    {
        if (ReferenceEquals(a, b)) return false;

        var sameContent = a.HasHash && b.HasHash &&
                          string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase);
        var sameName = string.Equals(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
        var sameTitle = !string.IsNullOrWhiteSpace(a.EffectiveTitle) &&
                        string.Equals(a.EffectiveTitle, b.EffectiveTitle, StringComparison.OrdinalIgnoreCase) &&
                        a.Year == b.Year && a.Season == b.Season && a.Episode == b.Episode;

        return match switch
        {
            DuplicateMatch.SameContent => sameContent,
            DuplicateMatch.SameName => sameName,
            DuplicateMatch.SameTitle => sameTitle,
            _ => sameContent || sameTitle
        };
    }

    /// <summary>How a matching rule reads on screen.</summary>
    public static string Describe(DuplicateMatch match) => match switch
    {
        DuplicateMatch.SameContent => "Only files that are byte-for-byte identical",
        DuplicateMatch.SameName => "Files with the same name, wherever they are",
        DuplicateMatch.SameTitle => "Files claiming the same title, year and episode",
        _ => "Identical files, and anything claiming the same title and episode"
    };
}
