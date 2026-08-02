using System.Text.RegularExpressions;

namespace MediaCatalog.Core.Classification;

/// <summary>
/// Reads a season number however it is written: "3", "03", "Three", "third", "twenty one".
///
/// Libraries filed by hand are full of "Season Three" folders, and a folder that says the
/// season in words carries exactly as much information as one that says it in digits —
/// there is no reason for the program to understand only half of them.
/// </summary>
public static class NumberWords
{
    /// <summary>The words for 0–19, cardinal and ordinal alike.</summary>
    private static readonly Dictionary<string, int> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0,
        ["one"] = 1, ["first"] = 1,
        ["two"] = 2, ["second"] = 2,
        ["three"] = 3, ["third"] = 3,
        ["four"] = 4, ["fourth"] = 4,
        ["five"] = 5, ["fifth"] = 5,
        ["six"] = 6, ["sixth"] = 6,
        ["seven"] = 7, ["seventh"] = 7,
        ["eight"] = 8, ["eighth"] = 8,
        ["nine"] = 9, ["ninth"] = 9,
        ["ten"] = 10, ["tenth"] = 10,
        ["eleven"] = 11, ["eleventh"] = 11,
        ["twelve"] = 12, ["twelfth"] = 12,
        ["thirteen"] = 13, ["thirteenth"] = 13,
        ["fourteen"] = 14, ["fourteenth"] = 14,
        ["fifteen"] = 15, ["fifteenth"] = 15,
        ["sixteen"] = 16, ["sixteenth"] = 16,
        ["seventeen"] = 17, ["seventeenth"] = 17,
        ["eighteen"] = 18, ["eighteenth"] = 18,
        ["nineteen"] = 19, ["nineteenth"] = 19
    };

    /// <summary>The multiples of ten, which may be followed by a unit ("twenty one").</summary>
    private static readonly Dictionary<string, int> Tens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twenty"] = 20, ["twentieth"] = 20,
        ["thirty"] = 30, ["thirtieth"] = 30,
        ["forty"] = 40, ["fortieth"] = 40,
        ["fifty"] = 50, ["fiftieth"] = 50,
        ["sixty"] = 60, ["sixtieth"] = 60,
        ["seventy"] = 70, ["seventieth"] = 70,
        ["eighty"] = 80, ["eightieth"] = 80,
        ["ninety"] = 90, ["ninetieth"] = 90
    };

    /// <summary>
    /// Every word this understands, as a regex alternation. Used to build the season
    /// patterns, so digits and words are matched by the same expression rather than by two
    /// that could drift apart.
    /// </summary>
    public static readonly string WordPattern =
        string.Join("|", Tens.Keys.Concat(Units.Keys)
            .OrderByDescending(w => w.Length)   // "seventeen" before "seven"
            .Select(Regex.Escape));

    /// <summary>
    /// A season number written in digits or in words, as a regex fragment. Deliberately
    /// unnamed so it can be embedded in a larger pattern under whatever name that needs.
    /// </summary>
    public static readonly string NumberPattern =
        $@"(?:\d{{1,3}}|(?:{WordPattern})(?:[\s\-](?:{WordPattern}))?)";

    /// <summary>
    /// The value of <paramref name="text"/> as a number, or null when it isn't one.
    /// Accepts digits, a single word, or a tens-and-units pair ("twenty one", "twenty-one").
    /// </summary>
    public static int? Parse(string? text)
    {
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0) return null;

        if (int.TryParse(s, out var digits)) return digits;

        var words = s.Split(new[] { ' ', '-', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            1 => Single(words[0]),
            // "twenty one" only makes sense as tens followed by units; "one twenty" does not.
            2 when Tens.TryGetValue(words[0], out var tens) && Units.TryGetValue(words[1], out var unit)
                   && unit is > 0 and < 10 => tens + unit,
            _ => null
        };
    }

    private static int? Single(string word) =>
        Units.TryGetValue(word, out var unit) ? unit
        : Tens.TryGetValue(word, out var tens) ? tens
        : null;
}
