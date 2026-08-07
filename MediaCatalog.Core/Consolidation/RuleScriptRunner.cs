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
                return ScriptValue.Of(compare.Operator switch
                {
                    ">" => left.Number > right.Number,
                    "<" => left.Number < right.Number,
                    ">=" => left.Number >= right.Number,
                    "<=" => left.Number <= right.Number,
                    "==" => Math.Abs(left.Number - right.Number) < 0.000001,
                    _ => Math.Abs(left.Number - right.Number) >= 0.000001
                });
            }

            case CallExpr call:
                return await CallAsync(call, file1, file2, ct);
        }

        return ScriptValue.Of(false);
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

            case "LengthDifferent":
            {
                var margin = Math.Abs((await EvaluateAsync(call.Arguments[0], file1, file2, ct)).Number);
                var gap = Math.Abs(file1.DurationSeconds - file2.DurationSeconds);
                return ScriptValue.Of(gap >= margin);
            }
        }

        return ScriptValue.Of(false);
    }

    private static MediaFile Side(ScriptSide side, MediaFile file1, MediaFile file2) =>
        side == ScriptSide.File1 ? file1 : file2;

    private static bool HasFingerprint(MediaFile file) =>
        !string.IsNullOrEmpty(file.AudioFingerprint) || !string.IsNullOrEmpty(file.VideoFingerprint);

    /// <summary>One thing about one file, as a number the comparisons can use.</summary>
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
        _ => ScriptValue.Of(false)
    };

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
