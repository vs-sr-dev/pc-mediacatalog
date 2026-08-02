using System.Text.RegularExpressions;

namespace MediaCatalog.Core.Naming;

/// <summary>
/// Puts a capital on the front of every word in a title, so a library filed from
/// "the.matrix.1999.mkv" reads *The Matrix* rather than *the matrix*.
///
/// Only the first letter is touched. A word that already carries capitals of its own —
/// <c>MASH</c>, <c>iCarly</c>, <c>WALL·E</c> — keeps them, because whoever wrote it that
/// way meant it and there is no way to tell a deliberate capital from an accidental one.
/// </summary>
public static class TitleCase
{
    /// <summary>
    /// A word: a run of letters and digits, optionally carrying apostrophes or internal
    /// dots ("S.W.A.T", "don't"). Everything between the words is left exactly as it is.
    /// </summary>
    private static readonly Regex Word = new(@"[\p{L}\p{N}][\p{L}\p{N}'’.]*", RegexOptions.Compiled);

    /// <summary>
    /// The title with each word's initial letter capitalised. A word after an opening
    /// bracket or quote counts as a word like any other.
    /// </summary>
    public static string Apply(string? title)
    {
        var text = title ?? string.Empty;
        if (text.Length == 0) return text;

        return Word.Replace(text, m =>
        {
            var word = m.Value;
            var first = word[0];
            return char.IsLower(first)
                ? char.ToUpperInvariant(first) + word[1..]
                : word;
        });
    }
}
