using System.Globalization;
using MediaCatalog.Core.Storage;

namespace MediaCatalog.Core.Consolidation;

/// <summary>
/// The judgement the program makes for itself when a category has no rules of its own,
/// written down in both of the ways a user can write rules.
///
/// It exists because "the built-in judgement" was, until now, a paragraph of prose in a
/// dialog box and several hundred lines of code nobody outside the project will ever read.
/// Somebody sitting down to write their own rules was being asked to start from nothing while
/// a perfectly good set of rules sat just out of reach — and, worse, had no way of telling
/// whether the thing they were about to write was better or worse than what they already had.
///
/// So the built-in rules are shown, in the same steps and the same language the user's own
/// rules are written in, and can be copied into the builder and changed. That is also a
/// standing check on the language: a rule set the program itself relies on that the builder
/// could not express would be a builder that cannot say the important things, and the test
/// that runs these through the parser is there to catch exactly that.
/// </summary>
public static class BuiltInRules
{
    /// <summary>What counts as two copies of one thing, before any of this runs.</summary>
    public const DuplicateMatch Matching = DuplicateMatch.SameContentOrTitle;

    /// <summary>
    /// The built-in judgement in prose, for the panel that shows it.
    /// </summary>
    public static string Explain(string category, int toleranceSeconds)
    {
        var thing = Thing(category);
        return
            $"With no rules of its own, '{category}' is filed by the judgement built into the " +
            "program. It runs in four parts.\n\n" +

            $"First, what counts as two copies of one {thing}: {ConsolidationRules.Describe(Matching).ToLowerInvariant()}. " +
            "Files that are byte-for-byte identical are not a question at all — any of them will " +
            "do, so the copy already in the library is kept and the rest go.\n\n" +

            "Second, are they really the same thing? Both copies are fingerprinted and compared. " +
            "A copy that runs longer than the other is allowed for — the two are re-sampled over " +
            "the stretch they have in common, so a minute of credits on the end of one does not " +
            $"make it look like a different {thing}. If they still do not match, one of them is " +
            "mislabelled, and which to keep is not a decision the program is entitled to make: it " +
            "stands aside and puts them to you.\n\n" +

            $"Third, how far apart may they be? {Tolerance(toleranceSeconds)} Beyond that a longer " +
            "copy is a different cut rather than a better copy of the same one, and that too goes " +
            "to you.\n\n" +

            "Fourth, which one. The better picture wins — resolution for video, bitrate for audio. " +
            "Between two of equal quality, the longer: it holds everything the shorter one holds " +
            "and something besides. Between two of equal quality and equal length, the smaller, " +
            "since at one resolution and one running time the extra bytes are padding rather than " +
            "detail. Whichever copy that leaves in front is then decoded end to end, and if it " +
            "turns out to be damaged it is dropped — with every byte-identical copy of it, which " +
            "must be damaged too — and the next best copy gets its turn. If every copy fails, " +
            "nothing is filed and you are told.";
    }

    private static string Thing(string category) => category switch
    {
        "TvShow" or "TvExtra" => "episode",
        "Movie" or "MovieExtra" => "film",
        "Audio" => "recording",
        _ => "thing"
    };

    private static string Tolerance(int seconds) => seconds <= 0
        ? "For this category the lengths have to agree exactly."
        : $"For this category they may differ by up to {seconds} second{(seconds == 1 ? "" : "s")}.";

    // --- The same thing, as steps -------------------------------------------

    /// <summary>
    /// The built-in choice written as ordered steps.
    ///
    /// This is the fourth part above and only that. Steps compare one thing at a time and
    /// know nothing about any other, so there is no way to say "and stand aside if these turn
    /// out not to be the same thing at all" — which is the second and third parts, and the
    /// reason the script below exists. Somebody who copies these steps and changes nothing has
    /// a category that files more readily than the built-in judgement does, and the wizard
    /// says so rather than leaving them to find out.
    /// </summary>
    public static List<ConsolidationRule> Steps() => new()
    {
        new ConsolidationRule
        {
            Field = ConsolidationField.Integrity, Prefer = RulePreference.Greater
        },
        new ConsolidationRule
        {
            Field = ConsolidationField.Quality, Prefer = RulePreference.Greater
        },
        new ConsolidationRule
        {
            // Rounded to the second, as the built-in ranking does, so two copies separated by
            // a rounding difference are not separated at all and the size step gets its say.
            Field = ConsolidationField.Length, Prefer = RulePreference.Greater, Tolerance = 0.5
        },
        new ConsolidationRule
        {
            Field = ConsolidationField.Size, Prefer = RulePreference.Lesser
        }
    };

    /// <summary>What the steps above cannot say, in a sentence, for the panel that shows them.</summary>
    public const string StepsFallShort =
        "These steps are the choosing, and only the choosing. What they cannot say is when not " +
        "to choose at all — that two copies which do not match are somebody's mislabelling, or " +
        "that a copy running ten minutes longer is a different cut. A step compares one thing " +
        "and knows nothing about any other, so it always chooses. The rules on the right say " +
        "the whole of it.";

    // --- The same thing, in the language ------------------------------------

    /// <summary>
    /// The built-in judgement written out as rules of the user's own — the whole of it,
    /// including the two places it declines to choose.
    ///
    /// The tournament makes this a faithful reproduction rather than an approximation. The
    /// engine plays copies off two at a time, which is precisely what these rules are written
    /// for, and the run's ban on doing anything expensive twice is what makes
    /// <c>DeepScan</c> appear on six lines without a file ever being decoded more than once.
    ///
    /// The shape of the last six lines is worth reading twice. <c>AND DeepScan(File1)</c> is
    /// not a second condition so much as an order of work: the comparison decides which copy
    /// is in front, and only that one is decoded. A copy that fails its decode fails the rule,
    /// falls through, and — since every later rule that would have kept it is also guarded by
    /// its now-known state — cannot win any of them either. That is exactly what the built-in
    /// run does, and it is why nothing is decoded that did not need to be.
    /// </summary>
    public static string Script(int toleranceSeconds)
    {
        var margin = toleranceSeconds.ToString(CultureInfo.InvariantCulture);

        return string.Join("\n", new[]
        {
            "# Are these really the same thing? Fingerprint both and compare, allowing for",
            "# one running longer than the other.",
            "FingerprintFiles()",
            "if (NOT SameContent()) Undecided",
            "",
            "# Further apart than this and it is a different cut, not a better copy.",
            $"if (LengthDifferent({margin})) Undecided",
            "",
            "# The better picture, then the longer, then the smaller - and whichever copy that",
            "# leaves in front is decoded before it is kept. One that will not decode falls",
            "# through to the next rule, which is how the second-best copy gets its turn.",
            "if (File1.Quality > File2.Quality AND DeepScan(File1)) Consolidate(File1)",
            "if (File2.Quality > File1.Quality AND DeepScan(File2)) Consolidate(File2)",
            "if (File1.Length > File2.Length AND DeepScan(File1)) Consolidate(File1)",
            "if (File2.Length > File1.Length AND DeepScan(File2)) Consolidate(File2)",
            "if (File1.Size <= File2.Size AND DeepScan(File1)) Consolidate(File1)",
            "if (DeepScan(File2)) Consolidate(File2)",
            "",
            "# Both copies are damaged. There is nothing sound to file.",
            "Undecided"
        });
    }

    /// <summary>The built-in judgement for a category, using the length tolerance set for it.</summary>
    public static string Script(AppSettings settings, string category) =>
        Script(settings.DurationToleranceFor(category));

    /// <summary>
    /// Where the written-out rules and the run differ, said plainly rather than left for
    /// somebody to discover. Two small things, and both of them are the run being cleverer
    /// about work it has already done rather than about what to keep.
    /// </summary>
    public const string Differences =
        "Two details of the run are not in the rules above, and neither changes which copy is " +
        "kept. The run compares every copy against the first one before choosing between any of " +
        "them, where the rules confirm each pair as they meet; and when a copy fails its decode " +
        "the run also removes the byte-identical copies of it, which cannot be sound if it is " +
        "not. Copy these rules and you have the built-in judgement.";
}
