using System.Text;
using System.Text.RegularExpressions;

namespace MediaCatalog.Core.Filtering;

/// <summary>
/// Case-insensitive wildcard matching for column filters: <c>*</c> matches any run of
/// characters, <c>?</c> matches exactly one. A pattern with no wildcards is treated as a
/// "contains" search, so typing plain text filters intuitively.
/// </summary>
public static class WildcardMatcher
{
    private static readonly Dictionary<string, Regex> Cache = new();

    public static bool IsMatch(string? value, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return true;
        value ??= string.Empty;

        // No wildcards → substring search (most natural for a filter box).
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return value.Contains(pattern, StringComparison.OrdinalIgnoreCase);

        if (!Cache.TryGetValue(pattern, out var regex))
        {
            regex = new Regex(ToRegex(pattern),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            Cache[pattern] = regex;
        }
        return regex.IsMatch(value);
    }

    /// <summary>
    /// The same wildcards, but the whole value has to match — a pattern with no wildcards in
    /// it is an exact comparison rather than a "contains" search.
    ///
    /// This is what a rule about a file extension needs: <c>.mp3</c> must mean .mp3 and not
    /// also .mp3x, while <c>.mp?</c> still covers .mp3 and .mp4 and <c>.m*</c> covers every
    /// extension beginning with m.
    /// </summary>
    public static bool IsMatchWhole(string? value, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        value ??= string.Empty;

        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);

        return IsMatch(value, pattern);   // ToRegex already anchors both ends
    }

    private static string ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (var c in pattern)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString())
            });
        }
        sb.Append('$');
        return sb.ToString();
    }
}
