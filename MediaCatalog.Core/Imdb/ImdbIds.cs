namespace MediaCatalog.Core.Imdb;

/// <summary>
/// IMDb identifiers, in and out of the form they are stored in.
///
/// IMDb writes every identifier as "tt0369179" — two letters that are the same on every row
/// of every file, and a run of leading zeros that carry no information either. Nine
/// characters of text where a number will do, on tens of millions of rows across two files,
/// is most of a gigabyte spent saying "tt" and "0". So the extract keeps the number.
/// </summary>
public static class ImdbIds
{
    /// <summary>
    /// The number inside "tt0369179", or 0 when the field is missing, null or malformed —
    /// 0 is not a real IMDb identifier, so it doubles as "no such thing" without needing a
    /// nullable to carry it about.
    /// </summary>
    public static int Parse(ReadOnlySpan<char> tconst)
    {
        var s = tconst.Trim();
        if (s.Length > 2 && (s[0] is 't' or 'T') && (s[1] is 't' or 'T')) s = s[2..];

        var value = 0;
        foreach (var c in s)
        {
            if (c is < '0' or > '9') return 0;
            // Anything this long is not an IMDb identifier; stopping is better than wrapping.
            if (value > (int.MaxValue - (c - '0')) / 10) return 0;
            value = value * 10 + (c - '0');
        }
        return value;
    }

    /// <summary>The identifier written the way IMDb writes it, for showing to a person.</summary>
    public static string Format(int id) => id <= 0 ? string.Empty : $"tt{id:0000000}";
}
