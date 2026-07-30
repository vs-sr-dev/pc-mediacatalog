namespace MediaCatalog.App.ViewModels;

/// <summary>One active results filter: a wildcard pattern on a column, optionally negated.</summary>
public class FilterClause
{
    public required string Column { get; init; }
    public required string Pattern { get; init; }
    public bool Negate { get; init; }

    public string Display => $"{Column} {(Negate ? "≠" : "~")} {Pattern}  ✕";
}
