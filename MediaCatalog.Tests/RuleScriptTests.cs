using MediaCatalog.Core.Consolidation;
using MediaCatalog.Core.Models;
using Xunit;

namespace MediaCatalog.Tests;

public class RuleScriptTests
{
    /// <summary>Counts what a script asked for, so "once per file" can be asserted rather than hoped.</summary>
    private sealed class CountingServices : IRuleScriptServices
    {
        public List<string> Probed { get; } = new();
        public List<string> Scanned { get; } = new();
        public List<string> Fingerprinted { get; } = new();

        /// <summary>What a deep scan finds, by file name. Anything unlisted comes back sound.</summary>
        public Dictionary<string, IntegrityStatus> Verdicts { get; } = new();

        public Task ProbeAsync(MediaFile file, CancellationToken ct)
        {
            Probed.Add(file.FileName);
            return Task.CompletedTask;
        }

        public Task DeepScanAsync(MediaFile file, CancellationToken ct)
        {
            Scanned.Add(file.FileName);
            file.Integrity = Verdicts.TryGetValue(file.FileName, out var verdict)
                ? verdict
                : IntegrityStatus.Ok;
            return Task.CompletedTask;
        }

        public Task FingerprintAsync(MediaFile file, CancellationToken ct)
        {
            Fingerprinted.Add(file.FileName);
            file.VideoFingerprint = "0";
            return Task.CompletedTask;
        }
    }

    private static MediaFile File(
        string name, long megabytes = 100, int quality = 720, double minutes = 90) => new()
    {
        FileName = name,
        FullPath = @"D:\x\" + name,
        Extension = ".mkv",
        Kind = MediaKind.Video,
        SizeBytes = megabytes * 1024 * 1024,
        Quality = quality,
        DurationSeconds = minutes * 60
    };

    private static async Task<RuleVerdict> RunAsync(
        string script, IReadOnlyList<MediaFile> copies, IRuleScriptServices? services = null)
    {
        var program = RuleScriptParser.Parse(script);
        var session = new RuleScriptSession(program, services ?? new CountingServices());
        return await session.ChooseAsync(copies);
    }

    [Fact]
    public async Task KeepsTheCopyTheRuleNames()
    {
        var big = File("big.mkv", megabytes: 900);
        var small = File("small.mkv", megabytes: 100);

        var verdict = await RunAsync(
            "if (File1.Size >= File2.Size) Consolidate(File1)\nConsolidate(File2)",
            new[] { big, small });

        Assert.Same(big, verdict.Winner);
    }

    /// <summary>
    /// More than two unique copies are settled two at a time, the winner carried forward —
    /// which is the whole of the tournament, and the reason the language only ever has to
    /// talk about File1 and File2.
    /// </summary>
    [Fact]
    public async Task PlaysMoreThanTwoCopiesOffAgainstEachOther()
    {
        var worst = File("worst.mkv", quality: 480);
        var best = File("best.mkv", quality: 2160);
        var middling = File("middling.mkv", quality: 1080);

        var verdict = await RunAsync(
            "if (File1.Quality >= File2.Quality) Consolidate(File1)\nConsolidate(File2)",
            new[] { worst, best, middling });

        Assert.Same(best, verdict.Winner);
    }

    [Fact]
    public async Task LengthDifferentIsFalseWithinTheMargin()
    {
        var first = File("first.mkv", minutes: 90);
        var second = File("second.mkv", minutes: 90.1); // six seconds apart

        var within = await RunAsync(
            "if (LengthDifferent(10)) Undecided\nConsolidate(File1)",
            new[] { first, second });
        Assert.Same(first, within.Winner);

        var beyond = await RunAsync(
            "if (LengthDifferent(5)) Undecided\nConsolidate(File1)",
            new[] { first, second });
        Assert.True(beyond.Undecided);
    }

    /// <summary>
    /// "Within six seconds" takes in six seconds exactly. Anything else makes the figure
    /// somebody typed mean one second less than it says, and puts the language a hair out of
    /// step with the tolerance the built-in rules use.
    /// </summary>
    [Fact]
    public async Task ExactlyTheMarginApartIsNotDifferent()
    {
        var first = File("first.mkv", minutes: 90);
        var second = File("second.mkv", minutes: 91);   // sixty seconds

        var verdict = await RunAsync(
            "if (LengthDifferent(60)) Undecided\nConsolidate(File1)",
            new[] { first, second });

        Assert.Same(first, verdict.Winner);
    }

    /// <summary>
    /// SameContent goes through the services rather than comparing fingerprints here, so the
    /// application can supply the better test — the one that lines two copies up over the
    /// stretch they have in common — and the built-in rules mean the same thing written out
    /// as they do when they run.
    /// </summary>
    [Fact]
    public async Task SameContentIsAskedOfTheServices()
    {
        var services = new SayingServices { Same = false };

        var verdict = await RunAsync(
            "if (NOT SameContent()) Undecided\nConsolidate(File1)",
            new[] { File("a.mkv"), File("b.mkv") }, services);

        Assert.True(verdict.Undecided);
        Assert.True(services.Asked);
    }

    private sealed class SayingServices : IRuleScriptServices
    {
        public bool Same { get; init; }
        public bool Asked { get; private set; }

        public Task ProbeAsync(MediaFile file, CancellationToken ct) => Task.CompletedTask;
        public Task DeepScanAsync(MediaFile file, CancellationToken ct) => Task.CompletedTask;
        public Task FingerprintAsync(MediaFile file, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> SameContentAsync(MediaFile a, MediaFile b, CancellationToken ct)
        {
            Asked = true;
            return Task.FromResult(Same);
        }
    }

    /// <summary>
    /// Words in quotes, for the fields a plugin brings that are not quantities. Either quote
    /// opens a run, and doubling one inside stands for it — so a name with an apostrophe can
    /// be written without anybody having to learn an escape.
    /// </summary>
    [Theory]
    [InlineData("if (File1.Size > 0) Consolidate(File1)", 1)]
    [InlineData("if (\"a\" == \"a\") Consolidate(File1)", 1)]
    [InlineData("if ('Frankie''s' != \"other\") Consolidate(File1)", 1)]
    public void ReadsWordsInQuotes(string script, int statements) =>
        Assert.Equal(statements, RuleScriptParser.Parse(script).Statements.Count);

    [Fact]
    public void AQuoteThatIsNeverClosedIsRefused() =>
        Assert.False(RuleScriptParser.TryParse(
            "if (File1.Size == \"never ends) Consolidate(File1)", out _, out _));

    /// <summary>
    /// A copy that keeps winning turns up in every comparison there is, and decoding it once
    /// per round would be the difference between an evening and a week.
    /// </summary>
    [Fact]
    public async Task DecodesAndFingerprintsEachFileOnlyOnce()
    {
        var services = new CountingServices();
        var copies = new[]
        {
            File("a.mkv", quality: 2160), File("b.mkv", quality: 1080), File("c.mkv", quality: 720)
        };

        await RunAsync(
            "FingerprintFiles()\n" +
            "if (DeepScan(File1) AND DeepScan(File2)) Consolidate(File1)\n" +
            "Consolidate(File2)",
            copies, services);

        Assert.Equal(3, services.Scanned.Count);
        Assert.Equal(3, services.Scanned.Distinct().Count());
        Assert.Equal(3, services.Fingerprinted.Count);
    }

    [Fact]
    public async Task MeasuresEveryCopyOnceWhenTheRulesReadALength()
    {
        var services = new CountingServices();
        var copies = new[] { File("a.mkv"), File("b.mkv"), File("c.mkv") };

        await RunAsync(
            "if (File1.Length >= File2.Length) Consolidate(File1)\nConsolidate(File2)",
            copies, services);

        Assert.Equal(new[] { "a.mkv", "b.mkv", "c.mkv" }, services.Probed);
    }

    [Fact]
    public async Task DeepScanFillsInWhatTheNextRuleReads()
    {
        var services = new CountingServices();
        services.Verdicts["broken.mkv"] = IntegrityStatus.Corrupt;

        var broken = File("broken.mkv", quality: 2160);
        var sound = File("sound.mkv", quality: 720);

        var verdict = await RunAsync(
            "DeepScan(File1)\n" +
            "DeepScan(File2)\n" +
            "if (File1.Corrupt) Consolidate(File2)\n" +
            "if (File2.Corrupt) Consolidate(File1)\n" +
            "if (File1.Quality >= File2.Quality) Consolidate(File1)\n" +
            "Consolidate(File2)",
            new[] { broken, sound }, services);

        Assert.Same(sound, verdict.Winner);
    }

    [Fact]
    public async Task RulesThatRunOutDecideNothing()
    {
        var verdict = await RunAsync(
            "if (File1.Quality > File2.Quality) Consolidate(File1)",
            new[] { File("a.mkv", quality: 720), File("b.mkv", quality: 1080) });

        Assert.True(verdict.Undecided);
        Assert.Null(verdict.Winner);
    }

    [Fact]
    public async Task UndecidedStandsAsideAndSaysWhichRuleDidIt()
    {
        var verdict = await RunAsync(
            "if (NOT FingerprintsMatch()) Undecided\nConsolidate(File1)",
            new[] { File("a.mkv"), File("b.mkv") });

        Assert.True(verdict.Undecided);
        Assert.Contains("Undecided", verdict.Why);
    }

    [Theory]
    [InlineData("if (File1.Sixe > File2.Size) Consolidate(File1)")]
    [InlineData("if (File1.Size > File2.Size) Explode(File1)")]
    [InlineData("if (File1.Size > File2.Size Consolidate(File1)")]
    [InlineData("DeepScan()")]
    [InlineData("LengthDifferent(10, 20)")]
    [InlineData("if (File1.Size > File2.Size) Consolidate(File1) @")]
    public void RefusesWhatItCannotRun(string script)
    {
        Assert.False(RuleScriptParser.TryParse(script, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void KnowsWhatItIsGoingToCost()
    {
        var cheap = RuleScriptParser.Parse("if (File1.Size > File2.Size) Consolidate(File1)");
        Assert.False(cheap.NeedsProbe);
        Assert.False(cheap.NeedsDeepScan);
        Assert.False(cheap.NeedsFingerprint);

        var dear = RuleScriptParser.Parse(
            "FingerprintFiles()\nif (LengthDifferent(30) AND DeepScan(File1)) Consolidate(File2)\n" +
            "Consolidate(File1)");
        Assert.True(dear.NeedsProbe);
        Assert.True(dear.NeedsDeepScan);
        Assert.True(dear.NeedsFingerprint);
    }

    /// <summary>
    /// The builder takes a script apart into the pieces it was dragged out of, and putting
    /// those pieces back together has to give the same rules — otherwise opening a saved
    /// script in the wizard would quietly change what it says.
    /// </summary>
    [Theory]
    [InlineData("if (File1.Quality >= File2.Quality) Consolidate(File1)")]
    [InlineData("if (NOT File1.Corrupt AND File1.Size < File2.Size) Consolidate(File1)")]
    [InlineData("if (File1.AlreadyFiled OR File1.Quality > File2.Quality) Consolidate(File1)")]
    [InlineData("if (File1.Corrupt AND (File2.AlreadyFiled OR File2.Checked)) Consolidate(File2)")]
    [InlineData("if (LengthDifferent(60)) Undecided")]
    [InlineData("FingerprintFiles()")]
    [InlineData("Consolidate(File2)")]
    [InlineData("if (SameContent()) Consolidate(File1)")]
    [InlineData("if (File1.NameLength != 0 AND NOT File2.Corrupt) Consolidate(File1)")]
    public void PiecesGoBackTogetherIntoTheSameRule(string line)
    {
        var statement = RuleScriptParser.Parse(line).Statements.Single();

        var pieces = RuleScriptParser.Pieces(statement.Condition);
        var action = RuleScriptParser.Written(statement.Action);
        var rebuilt = pieces.Count == 0 ? action : $"if ({string.Join(" ", pieces)}) {action}";

        var again = RuleScriptParser.Parse(rebuilt).Statements.Single();
        Assert.Equal(statement.Condition, again.Condition);
        Assert.Equal(statement.Action, again.Action);
    }

    [Fact]
    public void TheWorkedExampleIsItselfAValidScript()
    {
        Assert.True(RuleScriptParser.TryParse(RuleScriptVocabulary.Example, out var program, out var error),
            error);
        Assert.False(program.DecidesNothing);
    }

    [Fact]
    public void ReadsNotesAndTheOtherWaysPeopleWriteTheSameThing()
    {
        var program = RuleScriptParser.Parse(
            "# the better picture wins\n" +
            "if (file1.quality > file2.quality) consolidate(file1);\n" +
            "if (File1.Size = File2.Size && File1.NameLength <= File2.NameLength) then Consolidate(File1)\n" +
            "Consolidate(File2)");

        Assert.Equal(3, program.Statements.Count);
        Assert.All(program.Statements, s => Assert.IsType<ConsolidateAction>(s.Action));
    }
}
