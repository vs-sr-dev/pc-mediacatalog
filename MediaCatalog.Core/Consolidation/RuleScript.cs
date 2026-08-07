using System.Globalization;
using System.Text;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Consolidation;

/// <summary>Which of the two files in one comparison a term is about.</summary>
public enum ScriptSide
{
    /// <summary>The copy carried forward — the winner of every comparison so far.</summary>
    File1 = 0,

    /// <summary>The next unique copy, the one being compared against the carried-forward one.</summary>
    File2 = 1
}

/// <summary>A value in the little language: a number, with a note of whether it was written
/// as a truth value, so it can be shown back the way it was meant.</summary>
public readonly struct ScriptValue
{
    private ScriptValue(double number, bool isBoolean)
    {
        Number = number;
        IsBoolean = isBoolean;
    }

    public double Number { get; }
    public bool IsBoolean { get; }

    /// <summary>Anything that is not zero is true, which is how the comparisons chain.</summary>
    public bool Truth => Number != 0;

    public static ScriptValue Of(double number) => new(number, false);
    public static ScriptValue Of(bool truth) => new(truth ? 1 : 0, true);

    public override string ToString() => IsBoolean
        ? (Truth ? "true" : "false")
        : Number.ToString("0.##", CultureInfo.InvariantCulture);
}

// --- The tree a script parses into ------------------------------------------------------

public abstract record ScriptExpr;

/// <summary>Something about one of the two files: <c>File1.Quality</c>.</summary>
public sealed record PropertyExpr(ScriptSide Side, string Property) : ScriptExpr;

/// <summary>A file named on its own, which only ever happens as an argument.</summary>
public sealed record FileExpr(ScriptSide Side) : ScriptExpr;

public sealed record NumberExpr(double Value) : ScriptExpr;

public sealed record TruthExpr(bool Value) : ScriptExpr;

/// <summary>A call: <c>DeepScan(File1)</c>, <c>LengthDifferent(10)</c>, <c>FingerprintFiles()</c>.</summary>
public sealed record CallExpr(string Name, IReadOnlyList<ScriptExpr> Arguments) : ScriptExpr
{
    // Two calls are the same call when they say the same thing. A record would compare the
    // argument *list* by reference and call every DeepScan(File1) different from every other,
    // which makes comparing two parsed scripts — the one on screen against the one on disk —
    // answer no every time.
    public bool Equals(CallExpr? other) =>
        other is not null && Name == other.Name && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode() =>
        Arguments.Aggregate(Name.GetHashCode(), HashCode.Combine);
}

public sealed record CompareExpr(ScriptExpr Left, string Operator, ScriptExpr Right) : ScriptExpr;

public sealed record LogicExpr(ScriptExpr Left, bool And, ScriptExpr Right) : ScriptExpr;

public sealed record NotExpr(ScriptExpr Inner) : ScriptExpr;

/// <summary>What a statement does once its condition holds.</summary>
public abstract record ScriptAction;

/// <summary>
/// The one action that ends a comparison. The file named is the copy that is kept — the one
/// consolidated into the library — and every unique copy still to be looked at is compared
/// against it in turn.
/// </summary>
public sealed record ConsolidateAction(ScriptSide Side) : ScriptAction;

/// <summary>A call made for what it does rather than what it answers: a scan, a fingerprint.</summary>
public sealed record CallAction(CallExpr Call) : ScriptAction;

/// <summary>
/// Stop, and hand these copies to the user. The other way a comparison can end, and the one
/// worth writing down: a script that has established the two files are not the same content
/// at all should say so and stand aside, rather than carry on choosing between them.
/// </summary>
public sealed record UndecidedAction : ScriptAction;

/// <param name="Condition">Null for a statement with no <c>if</c> in front of it.</param>
/// <param name="Text">The line as the user wrote it, for saying afterwards what decided.</param>
public sealed record ScriptStatement(ScriptExpr? Condition, ScriptAction Action, string Text);

/// <summary>
/// A parsed set of comparison rules, with what running it is going to cost worked out in
/// advance so the caller can measure once rather than per comparison.
/// </summary>
public sealed record RuleScriptProgram(IReadOnlyList<ScriptStatement> Statements)
{
    /// <summary>True when the script reads a length or a quality, which ffprobe has to supply.</summary>
    public bool NeedsProbe { get; init; }

    /// <summary>True when the script asks for a decode anywhere in it.</summary>
    public bool NeedsDeepScan { get; init; }

    /// <summary>True when the script asks for fingerprints anywhere in it.</summary>
    public bool NeedsFingerprint { get; init; }

    /// <summary>True when no statement can ever end a comparison, which is a script that decides nothing.</summary>
    public bool DecidesNothing => !Statements.Any(s => s.Action is ConsolidateAction);
}

/// <summary>
/// Everything the little language knows about, in one place, so the parser, the wizard's
/// palette and the help text can never disagree about what may be written.
/// </summary>
public static class RuleScriptVocabulary
{
    /// <summary>A file property, and what it means when the user hovers over it.</summary>
    public record Term(string Name, bool IsTruth, string Meaning);

    public static readonly IReadOnlyList<Term> Properties = new List<Term>
    {
        new("Size", false, "Size on disk, in bytes. Always known, so a comparison on it always decides."),
        new("Length", false,
            "How long the file runs, in seconds. Zero until something has measured it — the run " +
            "measures both copies before it starts when the script mentions this."),
        new("Quality", false,
            "Picture height for video (720, 1080, 2160), bitrate for audio. Measured alongside the length."),
        new("Modified", false, "Last modified, as a number that grows with the date. Newer is not better, only newer."),
        new("NameLength", false, "How many characters the file's name runs to — a tie-break for the plainer name."),
        new("AlreadyFiled", true, "True when this copy is already in the library folder it belongs in."),
        new("DeepCheckIntegrity", true,
            "True when a deep check found the file sound. False for a file nothing has decoded, " +
            "so run DeepScan on it first if you mean to rely on this."),
        new("Corrupt", true, "True when a deep check found the file damaged."),
        new("Checked", true, "True when a deep check has been run on this file at all."),
        new("HasFingerprint", true, "True when a perceptual fingerprint has been calculated for this file.")
    };

    /// <summary>A function, its arity, and what it does.</summary>
    public record Function(string Name, int Arity, bool TakesFile, string Meaning);

    public static readonly IReadOnlyList<Function> Functions = new List<Function>
    {
        new("DeepScan", 1, true,
            "Decode the file end to end and record what that found in its DeepCheckIntegrity, " +
            "Corrupt and Checked. Answers true when the file came back sound. Slow — minutes for " +
            "a feature — and never done twice for the same file in one run."),
        new("FingerprintFiles", 0, false,
            "Calculate a perceptual fingerprint for both files, if they do not already have one. " +
            "Answers true when both ended up with one. Each file is only ever fingerprinted once " +
            "in a run."),
        new("FingerprintsMatch", 0, false,
            "True when both fingerprints are close enough to call the same content. Fingerprint " +
            "the files first, or this has nothing to compare and answers false."),
        new("LengthDifferent", 1, false,
            "Compare how long the two files run. False when they are within the number of seconds " +
            "you pass — the same length, allowing for a distributor's ident — and true when they " +
            "are further apart than that."),
        new("Consolidate", 1, true,
            "The file you name is the one kept: it is filed, and every unique copy still to be " +
            "looked at is compared against it. Nothing after this runs, so it is what ends a " +
            "comparison.")
    };

    /// <summary>
    /// The two ways a comparison can end. Both are written where an action goes, and nothing
    /// after them runs.
    /// </summary>
    public static readonly IReadOnlyList<Term> Endings = new List<Term>
    {
        new("Consolidate(File1)", false, "Keep the carried-forward copy and compare the rest against it."),
        new("Consolidate(File2)", false, "Keep the copy being compared, and carry that one forward instead."),
        new("Undecided", false,
            "Stop, and put these copies to the user. Worth saying when the script has just " +
            "established that the two are not the same content at all.")
    };

    public static bool IsProperty(string name) =>
        Properties.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static Function? FunctionNamed(string name) =>
        Functions.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name as the vocabulary spells it, so a script reads tidily whatever was typed.</summary>
    public static string CanonicalProperty(string name) =>
        Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Name
        ?? name;

    public static string CanonicalFunction(string name) => FunctionNamed(name)?.Name ?? name;

    /// <summary>A worked script, for somebody who has never seen one.</summary>
    public const string Example =
        "FingerprintFiles()\n" +
        "if (NOT FingerprintsMatch()) Undecided\n" +
        "if (LengthDifferent(60)) Undecided\n" +
        "if (File1.Quality > File2.Quality) Consolidate(File1)\n" +
        "if (File2.Quality > File1.Quality) Consolidate(File2)\n" +
        "if (File1.Size <= File2.Size) Consolidate(File1)\n" +
        "Consolidate(File2)";
}

/// <summary>Where a script went wrong, and what was wrong with it.</summary>
public sealed class RuleScriptException : Exception
{
    public RuleScriptException(string message, int line) : base(message) => Line = line;

    /// <summary>The line the trouble is on, counting from one. Zero when it is the script as a whole.</summary>
    public int Line { get; }

    public override string ToString() =>
        Line > 0 ? $"Line {Line}: {Message}" : Message;
}
