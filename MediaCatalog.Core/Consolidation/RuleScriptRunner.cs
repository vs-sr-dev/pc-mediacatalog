using MediaCatalog.Core.Fingerprinting;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Consolidation;

/// <summary>
/// The expensive things a script can ask for. Supplied by the caller, because measuring a
/// file means running an external tool and owning a progress bar, and neither belongs here.
/// </summary>
public interface IRuleScriptServices
{
    /// <summary>Read the file's length and quality.</summary>
    Task ProbeAsync(MediaFile file, CancellationToken ct);

    /// <summary>Decode the file end to end and record what that found.</summary>
    Task DeepScanAsync(MediaFile file, CancellationToken ct);

    /// <summary>Calculate the file's perceptual fingerprint.</summary>
    Task FingerprintAsync(MediaFile file, CancellationToken ct);

    /// <summary>Say what the run is doing, for the status line.</summary>
    void Report(string what) { }

    /// <summary>
    /// Whether these two really are the same content, allowing for one running longer than
    /// the other.
    ///
    /// The default is a plain fingerprint comparison, which is all anything without the
    /// external tools can offer. The application supplies a better one — it re-samples the
    /// stretch the two files have in common, so a copy with a minute of credits on the end is
    /// still recognised as the same film — and that is what the built-in rules use.
    /// </summary>
    Task<bool> SameContentAsync(MediaFile a, MediaFile b, CancellationToken ct) =>
        Task.FromResult(FingerprintMatcher.LooksLikeSameContent(a, b));
}

/// <summary>
/// Runs a user's comparison script over every unique copy of one thing and says which to keep.
///
/// Two files at a time, always. More than two unique copies are settled by playing them off
/// against each other: the first two are compared, the winner is carried forward as File1 and
/// compared with the third, and so on until one copy is left standing. That is the whole of
/// the tournament, and it is what lets the language stay as small as two files and one
/// question — which of these.
///
/// Nothing expensive is ever done twice. A file that has been decoded, fingerprinted or
/// measured once stays decoded, fingerprinted and measured for the rest of the run, however
/// many comparisons it goes on to appear in — which matters, because a copy that keeps
/// winning appears in every comparison there is.
/// </summary>
public sealed class RuleScriptSession
{
    private readonly RuleScriptProgram _program;
    private readonly IRuleScriptServices _services;

    private readonly HashSet<string> _probed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scanned = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fingerprinted = new(StringComparer.OrdinalIgnoreCase);

    public RuleScriptSession(RuleScriptProgram program, IRuleScriptServices services)
    {
        _program = program;
        _services = services;
    }

    /// <summary>How many files this session has decoded, fingerprinted and measured.</summary>
    public int DeepScanned => _scanned.Count;
    public int Fingerprinted => _fingerprinted.Count;
    public int Probed => _probed.Count;

    /// <summary>
    /// The copy the script keeps, or an undecided verdict when it declines to choose.
    /// </summary>
    /// <param name="uniqueCopies">
    /// One file per distinct piece of content. Handing this the same bytes twice is not wrong,
    /// only wasteful: the script would be asked to choose between two files that are the same
    /// file, and whichever it picked would be right.
    /// </param>
    public async Task<RuleVerdict> ChooseAsync(
        IReadOnlyList<MediaFile> uniqueCopies, CancellationToken ct = default)
    {
        if (uniqueCopies.Count == 0) return RuleVerdict.None("There is nothing to choose between.");
        if (uniqueCopies.Count == 1) return new RuleVerdict(uniqueCopies[0], "It is the only copy.", false);
        if (_program.Statements.Count == 0) return RuleVerdict.None("The rules are empty.");

        // Whatever the script compares lengths or pictures on has to have been measured, or
        // every one of those comparisons is a comparison of nothing with nothing.
        if (_program.NeedsProbe)
            foreach (var copy in uniqueCopies) await ProbeAsync(copy, ct);

        var best = uniqueCopies[0];
        var why = string.Empty;

        for (var i = 1; i < uniqueCopies.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (winner, reason) = await CompareAsync(best, uniqueCopies[i], ct);
            if (winner == null) return RuleVerdict.None(reason);
            best = winner;
            why = reason;
        }

        return new RuleVerdict(best, why, false);
    }

    /// <summary>
    /// One pass of the script over one pair. The first <c>Consolidate</c> whose condition holds
    /// ends it and names the keeper; an <c>Undecided</c> ends it with nobody chosen; and a
    /// script that simply runs out has failed to choose, which is treated the same way.
    /// </summary>
    public async Task<(MediaFile? Winner, string Why)> CompareAsync(
        MediaFile file1, MediaFile file2, CancellationToken ct = default)
    {
        foreach (var statement in _program.Statements)
        {
            ct.ThrowIfCancellationRequested();

            if (statement.Condition is { } condition)
            {
                var holds = await EvaluateAsync(condition, file1, file2, ct);
                if (!holds.Truth) continue;
            }

            switch (statement.Action)
            {
                case ConsolidateAction consolidate:
                {
                    var winner = consolidate.Side == ScriptSide.File1 ? file1 : file2;
                    var loser = consolidate.Side == ScriptSide.File1 ? file2 : file1;
                    return (winner, $"\"{statement.Text}\" — kept {winner.FileName} over {loser.FileName}");
                }

                case UndecidedAction:
                    return (null, $"the rules stood aside: \"{statement.Text}\"");

                case CallAction call:
                    await EvaluateAsync(call.Call, file1, file2, ct);
                    break;
            }
        }

        return (null, "the rules ran out without choosing a copy");
    }

    // --- Evaluation ---------------------------------------------------------

    private async Task<ScriptValue> EvaluateAsync(
        ScriptExpr expr, MediaFile file1, MediaFile file2, CancellationToken ct)
    {
        switch (expr)
        {
            case NumberExpr number:
                return ScriptValue.Of(number.Value);

            case TextExpr text:
                return ScriptValue.Of(text.Value);

            case TruthExpr truth:
                return ScriptValue.Of(truth.Value);

            case PropertyExpr property:
                return Read(Side(property.Side, file1, file2), property.Property);

            case FileExpr:
                // A file on its own is an argument, never a value. Reaching here means a
                // script compared one with something, which says nothing either way.
                return ScriptValue.Of(false);

            case NotExpr not:
                return ScriptValue.Of(!(await EvaluateAsync(not.Inner, file1, file2, ct)).Truth);

            case LogicExpr logic:
            {
                // Short-circuited, and deliberately: "if (FingerprintFiles() AND …)" must not
                // fingerprint anything when the first half has already settled the answer.
                var left = await EvaluateAsync(logic.Left, file1, file2, ct);
                if (logic.And && !left.Truth) return ScriptValue.Of(false);
                if (!logic.And && left.Truth) return ScriptValue.Of(true);
                return ScriptValue.Of((await EvaluateAsync(logic.Right, file1, file2, ct)).Truth);
            }

            case CompareExpr compare:
            {
                var left = await EvaluateAsync(compare.Left, file1, file2, ct);
                var right = await EvaluateAsync(compare.Right, file1, file2, ct);
                return ScriptValue.Of(Compare(left, right, compare.Operator));
            }

            case CallExpr call:
                return await CallAsync(call, file1, file2, ct);
        }

        return ScriptValue.Of(false);
    }

    /// <summary>
    /// One comparison. Two numbers are compared as numbers; anything involving words is
    /// compared as words, ignoring case — which is what somebody asking whether an author is
    /// "Iain M. Banks" means, and which puts A before B when the question is which of two
    /// genres sorts first.
    /// </summary>
    private static bool Compare(ScriptValue left, ScriptValue right, string @operator)
    {
        var order = left.IsText || right.IsText
            ? string.Compare(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase)
            : Math.Abs(left.Number - right.Number) < 0.000001 ? 0
            : left.Number < right.Number ? -1 : 1;

        return @operator switch
        {
            ">" => order > 0,
            "<" => order < 0,
            ">=" => order >= 0,
            "<=" => order <= 0,
            "==" => order == 0,
            _ => order != 0
        };
    }

    private async Task<ScriptValue> CallAsync(
        CallExpr call, MediaFile file1, MediaFile file2, CancellationToken ct)
    {
        switch (call.Name)
        {
            case "DeepScan":
            {
                var target = Side(((FileExpr)call.Arguments[0]).Side, file1, file2);
                await DeepScanAsync(target, ct);
                return ScriptValue.Of(target.Integrity == IntegrityStatus.Ok);
            }

            case "FingerprintFiles":
                await FingerprintAsync(file1, ct);
                await FingerprintAsync(file2, ct);
                return ScriptValue.Of(HasFingerprint(file1) && HasFingerprint(file2));

            case "FingerprintsMatch":
                return ScriptValue.Of(
                    HasFingerprint(file1) && HasFingerprint(file2) &&
                    FingerprintMatcher.LooksLikeSameContent(file1, file2));

            case "SameContent":
                return ScriptValue.Of(await _services.SameContentAsync(file1, file2, ct));

            case "LengthDifferent":
            {
                // Strictly further apart than the margin: "within ten seconds" takes in ten
                // seconds exactly, which is what anybody writing the number means by it and
                // what the built-in rules have always done with their own tolerance.
                var margin = Math.Abs((await EvaluateAsync(call.Arguments[0], file1, file2, ct)).Number);
                var gap = Math.Abs(file1.DurationSeconds - file2.DurationSeconds);
                return ScriptValue.Of(gap > margin);
            }
        }

        return ScriptValue.Of(false);
    }

    private static MediaFile Side(ScriptSide side, MediaFile file1, MediaFile file2) =>
        side == ScriptSide.File1 ? file1 : file2;

    private static bool HasFingerprint(MediaFile file) =>
        !string.IsNullOrEmpty(file.AudioFingerprint) || !string.IsNullOrEmpty(file.VideoFingerprint);

    /// <summary>
    /// One thing about one file, as a value the comparisons can use.
    ///
    /// Anything the built-in list does not know is a plugin's field, and what sort of thing
    /// it holds is the plugin's own declaration: a page count compares as a number, a
    /// publication date as a date, an author as words. A file of some other kind entirely has
    /// no such field, and comes back empty rather than pretending to a value — which is what
    /// makes a rule about e-books harmless in a library that also holds films.
    /// </summary>
    private static ScriptValue Read(MediaFile file, string property) => property switch
    {
        "Size" => ScriptValue.Of(file.SizeBytes),
        "Length" => ScriptValue.Of(file.DurationSeconds),
        "Quality" => ScriptValue.Of(file.Quality),
        // Seconds since the start of the century: a number that grows with the date and stays
        // small enough to read in a message.
        "Modified" => ScriptValue.Of(file.LastModifiedUtc == default
            ? 0
            : (file.LastModifiedUtc - new DateTime(2000, 1, 1)).TotalSeconds),
        "NameLength" => ScriptValue.Of(file.FileName.Length),
        "AlreadyFiled" => ScriptValue.Of(file.Consolidated),
        "DeepCheckIntegrity" => ScriptValue.Of(file.Integrity == IntegrityStatus.Ok),
        "Corrupt" => ScriptValue.Of(file.Integrity == IntegrityStatus.Corrupt),
        "Checked" => ScriptValue.Of(file.Integrity != IntegrityStatus.NotChecked),
        "HasFingerprint" => ScriptValue.Of(HasFingerprint(file)),
        _ => PluginValue(file, property)
    };

    /// <summary>A plugin's field, read as whatever sort of thing the plugin said it is.</summary>
    public static ScriptValue PluginValue(MediaFile file, string property)
    {
        var raw = file.FieldValue(property);
        var type = Plugins.MediaPlugins.Field(property)?.Type ?? Plugins.PluginFieldType.Text;

        switch (type)
        {
            case Plugins.PluginFieldType.Number:
                // Something that was meant to be a figure and is not — "unknown", or blank —
                // is zero, so a rule asking for more than three hundred pages passes it over
                // rather than choosing on a value nobody has.
                return ScriptValue.Of(
                    double.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var number)
                        ? number
                        : 0);

            case Plugins.PluginFieldType.Date:
                // The same scale as Modified, so the two are comparable and both read as a
                // number that grows with the date.
                return ScriptValue.Of(
                    DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var date)
                        ? (date - new DateTime(2000, 1, 1)).TotalSeconds
                        : 0);

            case Plugins.PluginFieldType.Truth:
                return ScriptValue.Of(raw.Trim() is "true" or "True" or "TRUE" or "yes" or "Yes"
                    or "YES" or "1");

            default:
                return ScriptValue.Of(raw);
        }
    }

    // --- The expensive things, each done once per file ----------------------

    private async Task ProbeAsync(MediaFile file, CancellationToken ct)
    {
        if (!_probed.Add(file.FullPath)) return;
        _services.Report($"Measuring {file.FileName}");
        await _services.ProbeAsync(file, ct);
    }

    private async Task DeepScanAsync(MediaFile file, CancellationToken ct)
    {
        if (!_scanned.Add(file.FullPath)) return;
        _services.Report($"Deep checking {file.FileName}");
        await _services.DeepScanAsync(file, ct);
    }

    private async Task FingerprintAsync(MediaFile file, CancellationToken ct)
    {
        if (HasFingerprint(file)) return;
        if (!_fingerprinted.Add(file.FullPath)) return;
        _services.Report($"Fingerprinting {file.FileName}");
        await _services.FingerprintAsync(file, ct);
    }
}
