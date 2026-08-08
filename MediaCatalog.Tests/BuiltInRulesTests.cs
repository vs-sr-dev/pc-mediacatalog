using MediaCatalog.Core.Consolidation;
using MediaCatalog.Core.Models;
using Xunit;

namespace MediaCatalog.Tests;

/// <summary>
/// The built-in judgement, written out in the language the user writes rules in, has to
/// actually be the built-in judgement.
///
/// This is the test that makes the wizard's third tab honest. Showing somebody "here is what
/// the program does, copy it and change it" is only worth doing if what is shown is what the
/// program does — and it is also a standing check on the language itself: the day the little
/// language can no longer express the rules the program relies on is the day it has stopped
/// being able to say the things that matter, and this is where that turns up.
/// </summary>
public class BuiltInRulesTests
{
    /// <summary>
    /// Answers the expensive questions from what the test says rather than from a disk.
    /// <see cref="SameContentAsync"/> is overridden exactly as the application overrides it.
    /// </summary>
    private sealed class Services : IRuleScriptServices
    {
        public bool SameThing { get; set; } = true;

        /// <summary>What a decode finds, by file name. Anything unlisted comes back sound.</summary>
        public Dictionary<string, bool> Decodes { get; } = new();

        public List<string> Scanned { get; } = new();

        public Task ProbeAsync(MediaFile file, CancellationToken ct) => Task.CompletedTask;

        public Task DeepScanAsync(MediaFile file, CancellationToken ct)
        {
            Scanned.Add(file.FileName);
            var sound = !Decodes.TryGetValue(file.FileName, out var verdict) || verdict;
            file.Integrity = sound ? IntegrityStatus.Ok : IntegrityStatus.Corrupt;
            return Task.CompletedTask;
        }

        public Task FingerprintAsync(MediaFile file, CancellationToken ct)
        {
            file.VideoFingerprint = "0000000000000000";
            return Task.CompletedTask;
        }

        public Task<bool> SameContentAsync(MediaFile a, MediaFile b, CancellationToken ct) =>
            Task.FromResult(SameThing);
    }

    private static MediaFile File(
        string name, int quality = 1080, double minutes = 100, long megabytes = 1000) => new()
    {
        FileName = name,
        FullPath = @"D:\x\" + name,
        Extension = ".mkv",
        Kind = MediaKind.Video,
        Quality = quality,
        DurationSeconds = minutes * 60,
        SizeBytes = megabytes * 1024 * 1024
    };

    private static async Task<(RuleVerdict Verdict, Services Services)> RunAsync(
        IReadOnlyList<MediaFile> copies, int tolerance = 60, Action<Services>? arrange = null)
    {
        var services = new Services();
        arrange?.Invoke(services);

        var program = RuleScriptParser.Parse(BuiltInRules.Script(tolerance));
        var session = new RuleScriptSession(program, services);
        return (await session.ChooseAsync(copies), services);
    }

    [Fact]
    public void TheBuiltInRulesAreSomethingTheBuilderCanRead()
    {
        Assert.True(
            RuleScriptParser.TryParse(BuiltInRules.Script(60), out var program, out var error),
            error);
        Assert.False(program.DecidesNothing);
        Assert.True(program.NeedsFingerprint);
        Assert.True(program.NeedsDeepScan);
        Assert.True(program.NeedsProbe);
    }

    /// <summary>
    /// The builder takes a script apart into pieces and puts it back together. If it cannot
    /// do that to the program's own rules, "copy these rules into mine" would hand somebody
    /// something subtly different from what they were shown.
    /// </summary>
    [Fact]
    public void TheBuiltInRulesSurviveBeingTakenApartAndPutBackTogether()
    {
        var program = RuleScriptParser.Parse(BuiltInRules.Script(60));

        var rebuilt = string.Join("\n", program.Statements.Select(s =>
        {
            var pieces = RuleScriptParser.Pieces(s.Condition);
            var action = RuleScriptParser.Written(s.Action);
            return pieces.Count == 0 ? action : $"if ({string.Join(" ", pieces)}) {action}";
        }));

        var again = RuleScriptParser.Parse(rebuilt);

        Assert.Equal(program.Statements.Count, again.Statements.Count);
        for (var i = 0; i < program.Statements.Count; i++)
        {
            Assert.Equal(program.Statements[i].Condition, again.Statements[i].Condition);
            Assert.Equal(program.Statements[i].Action, again.Statements[i].Action);
        }
    }

    [Fact]
    public async Task TheBetterPictureWins()
    {
        var best = File("best.mkv", quality: 2160);
        var worse = File("worse.mkv", quality: 1080);

        var (verdict, _) = await RunAsync(new[] { worse, best });

        Assert.Same(best, verdict.Winner);
    }

    /// <summary>At one quality, the longer copy holds everything the shorter one holds and more.</summary>
    [Fact]
    public async Task AtEqualQualityTheLongerWins()
    {
        var longer = File("longer.mkv", minutes: 100.5);
        var shorter = File("shorter.mkv", minutes: 100);

        var (verdict, _) = await RunAsync(new[] { shorter, longer });

        Assert.Same(longer, verdict.Winner);
    }

    /// <summary>At one quality and one length the extra bytes are padding rather than detail.</summary>
    [Fact]
    public async Task AtEqualQualityAndLengthTheSmallerWins()
    {
        var small = File("small.mkv", megabytes: 800);
        var big = File("big.mkv", megabytes: 4000);

        var (verdict, _) = await RunAsync(new[] { big, small });

        Assert.Same(small, verdict.Winner);
    }

    /// <summary>
    /// The case the ordered steps cannot express, and the reason the built-in rules are shown
    /// as a script as well: the copy that wins on paper is decoded, and a copy that will not
    /// decode loses to the one behind it.
    /// </summary>
    [Fact]
    public async Task TheBestCopyThatDecodesIsTheOneKept()
    {
        var best = File("best.mkv", quality: 2160);
        var sound = File("sound.mkv", quality: 1080);

        var (verdict, services) = await RunAsync(
            new[] { best, sound }, arrange: s => s.Decodes["best.mkv"] = false);

        Assert.Same(sound, verdict.Winner);
        // Both were decoded — the leader because it led, the other because it inherited the
        // lead — but neither more than once, however many rules named them.
        Assert.Equal(services.Scanned.Distinct().Count(), services.Scanned.Count);
    }

    [Fact]
    public async Task EveryCopyDamagedMeansNothingIsFiled()
    {
        var one = File("one.mkv", quality: 2160);
        var two = File("two.mkv", quality: 1080);

        var (verdict, _) = await RunAsync(new[] { one, two }, arrange: s =>
        {
            s.Decodes["one.mkv"] = false;
            s.Decodes["two.mkv"] = false;
        });

        Assert.True(verdict.Undecided);
        Assert.Null(verdict.Winner);
    }

    [Fact]
    public async Task CopiesThatAreNotTheSameThingGoToTheUser()
    {
        var (verdict, _) = await RunAsync(
            new[] { File("a.mkv"), File("b.mkv", quality: 720) },
            arrange: s => s.SameThing = false);

        Assert.True(verdict.Undecided);
    }

    [Fact]
    public async Task CopiesFurtherApartThanTheToleranceGoToTheUser()
    {
        var short_ = File("short.mkv", minutes: 100);
        var long_ = File("long.mkv", minutes: 110);   // ten minutes: a different cut

        var (verdict, _) = await RunAsync(new[] { short_, long_ }, tolerance: 60);

        Assert.True(verdict.Undecided);
    }

    /// <summary>A minute either way on a film is the credits, and decides nothing.</summary>
    [Fact]
    public async Task CopiesWithinTheToleranceAreStillChosenBetween()
    {
        var better = File("better.mkv", quality: 2160, minutes: 100);
        var worse = File("worse.mkv", quality: 720, minutes: 101);   // exactly 60s apart

        var (verdict, _) = await RunAsync(new[] { worse, better }, tolerance: 60);

        Assert.Same(better, verdict.Winner);
    }

    /// <summary>
    /// The steps are the fourth part of the judgement and only that, so they have to agree
    /// with the script wherever the script actually chooses. Where they differ is where the
    /// script declines to choose at all, which the wizard says in as many words.
    /// </summary>
    [Fact]
    public void TheStepsChooseTheSameCopyTheRulesDo()
    {
        var steps = BuiltInRules.Steps();

        var best = File("best.mkv", quality: 2160);
        var worse = File("worse.mkv", quality: 1080);
        Assert.Same(best, ConsolidationRules.Choose(new[] { worse, best }, steps).Winner);

        var longer = File("longer.mkv", minutes: 100.5);
        var shorter = File("shorter.mkv", minutes: 100);
        Assert.Same(longer, ConsolidationRules.Choose(new[] { shorter, longer }, steps).Winner);

        var small = File("small.mkv", megabytes: 800);
        var big = File("big.mkv", megabytes: 4000);
        Assert.Same(small, ConsolidationRules.Choose(new[] { big, small }, steps).Winner);
    }

    /// <summary>Every step reads as a sentence, since that is all the list ever shows.</summary>
    [Fact]
    public void EveryStepSaysWhatItDoes()
    {
        Assert.All(BuiltInRules.Steps(), step => Assert.NotEmpty(step.Describe()));
        Assert.NotEmpty(BuiltInRules.Explain("Movie", 60));
    }
}
