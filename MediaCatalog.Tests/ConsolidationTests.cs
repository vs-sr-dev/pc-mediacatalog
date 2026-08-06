using MediaCatalog.Core.Consolidation;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Naming;
using MediaCatalog.Core.Storage;
using Xunit;

namespace MediaCatalog.Tests;

public class EpisodePrefixTests
{
    [Theory]
    [InlineData("01 - Equal Opportunities.mkv", 1, true)]
    [InlineData("01 Equal Opportunities.mkv", 1, true)]
    [InlineData("1. Equal Opportunities.mkv", 1, true)]
    [InlineData("11-12 - Double.mkv", 11, true)]
    [InlineData("02 - Equal Opportunities.mkv", 1, false)]
    [InlineData("Equal Opportunities.mkv", 1, false)]
    [InlineData("1917 The Film.mkv", 1, false)]
    public void RecognisesANameThatIsAlreadyNumbered(string name, int episode, bool expected) =>
        Assert.Equal(expected, EpisodePrefixes.StartsWithEpisode(name, episode));

    [Theory]
    [InlineData("01 - 01 - Wheel Of Fortune.mkv", "01 - Wheel Of Fortune.mkv")]
    [InlineData("01 - 01 - 01 - Wheel Of Fortune.mkv", "01 - Wheel Of Fortune.mkv")]
    [InlineData("11-12 - 11-12 - Double.mkv", "11-12 - Double.mkv")]
    [InlineData("01 - Wheel Of Fortune.mkv", "01 - Wheel Of Fortune.mkv")]
    [InlineData("05 - 01 - Wheel Of Fortune.mkv", "05 - 01 - Wheel Of Fortune.mkv")]
    [InlineData("Wheel Of Fortune.mkv", "Wheel Of Fortune.mkv")]
    public void UndoesARepeatedPrefix(string name, string expected) =>
        Assert.Equal(expected, EpisodePrefixes.Collapse(name));

    [Fact]
    public void DoesNotNumberAFileTwice()
    {
        var settings = new AppSettings();
        var file = new MediaFile
        {
            FullPath = @"D:\x\01 - Equal Opportunities.mkv",
            FileName = "01 - Equal Opportunities.mkv",
            Extension = ".mkv",
            Season = 1,
            Episode = 1,
            TmdbName = "Yes Minister"
        };

        Assert.Equal("01 - Equal Opportunities.mkv",
            ConsolidationPlanner.PlanFileName(file, "TvShow", settings));
    }

    [Fact]
    public void NumbersAFileThatIsNotNumberedYet()
    {
        var settings = new AppSettings();
        var file = new MediaFile
        {
            FullPath = @"D:\x\Equal Opportunities.mkv",
            FileName = "Equal Opportunities.mkv",
            Extension = ".mkv",
            Season = 1,
            Episode = 1
        };

        Assert.Equal("01 - Equal Opportunities.mkv",
            ConsolidationPlanner.PlanFileName(file, "TvShow", settings));
    }

    /// <summary>A pattern that numbers a name which numbers itself must still say it once.</summary>
    [Fact]
    public void APatternDoesNotNumberAnAlreadyNumberedName()
    {
        var settings = new AppSettings();
        settings.CategoryFolders.Add(new CategoryConsolidation
        {
            Category = "TvShow",
            Folder = @"T:\TV",
            NameTemplate = "{episode:00} - {name}"
        });

        var file = new MediaFile
        {
            FullPath = @"D:\x\01 - Equal Opportunities.mkv",
            FileName = "01 - Equal Opportunities.mkv",
            Extension = ".mkv",
            Season = 1,
            Episode = 1
        };

        Assert.Equal("01 - Equal Opportunities.mkv",
            ConsolidationPlanner.PlanFileName(file, "TvShow", settings));
    }
}

public class LetterFolderTests
{
    private static MediaFile Film(string title, int year) => new()
    {
        FullPath = @"D:\downloads\film.mkv",
        FileName = "film.mkv",
        Extension = ".mkv",
        TmdbName = title,
        Year = year,
        Kind = MediaKind.Video,
        VideoCategory = VideoCategory.Movie
    };

    [Fact]
    public void FilmsAreSortedAToZUnlessToldOtherwise()
    {
        var settings = new AppSettings { FilmConsolidationDir = @"F:\Films" };
        settings.NormaliseCategoryFolders();

        Assert.Equal(@"F:\Films\B\Blade Runner (1982)",
            ConsolidationPlanner.PlanDirectory(Film("Blade Runner", 1982), "Movie", settings));

        settings.ConsolidationFor("Movie")!.UseLetterFolders = false;
        Assert.Equal(@"F:\Films\Blade Runner (1982)",
            ConsolidationPlanner.PlanDirectory(Film("Blade Runner", 1982), "Movie", settings));
    }

    [Fact]
    public void AnyCategoryCanBeSortedAToZ()
    {
        var settings = new AppSettings();
        settings.CategoryFolders.Add(new CategoryConsolidation
        {
            Category = "Audio",
            Folder = @"M:\Music",
            UseLetterFolders = true
        });

        var track = new MediaFile
        {
            FullPath = @"D:\x\track.mp3",
            FileName = "track.mp3",
            Extension = ".mp3",
            TmdbName = "Talking Heads",
            Kind = MediaKind.Audio
        };

        Assert.Equal(@"M:\Music\T", ConsolidationPlanner.PlanDirectory(track, "Audio", settings));
    }

    /// <summary>A title starting with a digit goes under #, as it always has.</summary>
    [Fact]
    public void NumbersGoUnderHash() =>
        Assert.Equal("#", ConsolidationPlanner.Bucket("1917"));
}

public class ExtraFilingTests
{
    private static AppSettings Library()
    {
        var settings = new AppSettings { TvConsolidationDir = @"T:\TV" };
        settings.NormaliseCategoryFolders();
        return settings;
    }

    private static MediaFile Extra(string path, int? season) => new()
    {
        FullPath = path,
        FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path),
        Kind = MediaKind.Video,
        VideoCategory = VideoCategory.TvExtra,
        TmdbName = "Burn Notice",
        Season = season
    };

    /// <summary>
    /// A featurette in the season's Extras folder is filed, even though the plan — which has
    /// no season to go on — names the show's own Extras folder.
    /// </summary>
    [Fact]
    public void AnExtraInTheSeasonsExtrasFolderIsFiled()
    {
        var settings = Library();
        var file = Extra(@"T:\TV\B\Burn Notice\Season 01\Extras\Behind The Scenes.mkv", null);

        Assert.True(ConsolidationPlanner.IsCorrectlyFiled(file, "TvExtra", settings));
    }

    [Fact]
    public void AnExtraInTheShowsExtrasFolderIsFiled()
    {
        var settings = Library();
        var file = Extra(@"T:\TV\B\Burn Notice\Extras\Behind The Scenes.mkv", null);

        Assert.True(ConsolidationPlanner.IsCorrectlyFiled(file, "TvExtra", settings));
    }

    [Fact]
    public void AnExtraUnderTheWrongShowIsNotFiled()
    {
        var settings = Library();
        var file = Extra(@"T:\TV\D\Dexter\Season 01\Extras\Behind The Scenes.mkv", null);

        Assert.False(ConsolidationPlanner.IsCorrectlyFiled(file, "TvExtra", settings));
    }

    [Fact]
    public void AnExtraLooseInTheLibraryIsNotFiled()
    {
        var settings = Library();
        var file = Extra(@"T:\TV\B\Burn Notice\Season 01\Behind The Scenes.mkv", null);

        Assert.False(ConsolidationPlanner.IsCorrectlyFiled(file, "TvExtra", settings));
    }
}

public class ConsolidationRuleTests
{
    private static MediaFile Copy(double seconds, int quality, long bytes) => new()
    {
        FullPath = $@"D:\x\{seconds}-{quality}.mkv",
        FileName = $"{seconds}-{quality}.mkv",
        Extension = ".mkv",
        DurationSeconds = seconds,
        Quality = quality,
        SizeBytes = bytes
    };

    [Fact]
    public void TheFirstStepThatCanTellThemApartDecides()
    {
        var rules = new List<ConsolidationRule>
        {
            new() { Field = ConsolidationField.Length, Prefer = RulePreference.Greater, Tolerance = 60 },
            new() { Field = ConsolidationField.Quality, Prefer = RulePreference.Greater }
        };

        // Same length within the minute's tolerance, so quality decides.
        var lower = Copy(5400, 720, 1_000);
        var higher = Copy(5430, 1080, 2_000);
        var verdict = ConsolidationRules.Choose(new[] { lower, higher }, rules);

        Assert.Same(higher, verdict.Winner);
        Assert.Contains("quality", verdict.Why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LengthWinsWhenItIsGenuinelyDifferent()
    {
        var rules = new List<ConsolidationRule>
        {
            new() { Field = ConsolidationField.Length, Prefer = RulePreference.Greater, Tolerance = 60 },
            new() { Field = ConsolidationField.Quality, Prefer = RulePreference.Greater }
        };

        var longer = Copy(7200, 720, 1_000);
        var better = Copy(5400, 1080, 2_000);

        Assert.Same(longer, ConsolidationRules.Choose(new[] { longer, better }, rules).Winner);
    }

    [Fact]
    public void AStepNothingHasMeasuredStandsAside()
    {
        var rules = new List<ConsolidationRule>
        {
            new() { Field = ConsolidationField.Length, Prefer = RulePreference.Greater },
            new() { Field = ConsolidationField.Size, Prefer = RulePreference.Greater }
        };

        var small = Copy(0, 0, 1_000);
        var large = Copy(0, 0, 9_000);

        Assert.Same(large, ConsolidationRules.Choose(new[] { small, large }, rules).Winner);
    }

    [Fact]
    public void CopiesTheRulesCannotSeparateAreLeftAlone()
    {
        var rules = new List<ConsolidationRule>
        {
            new() { Field = ConsolidationField.Quality, Prefer = RulePreference.Greater }
        };

        var verdict = ConsolidationRules.Choose(new[] { Copy(100, 1080, 5), Copy(100, 1080, 5) }, rules);
        Assert.True(verdict.Undecided);
        Assert.Null(verdict.Winner);
    }

    [Fact]
    public void MatchingByNameIgnoresEverythingElse()
    {
        var a = new MediaFile { FileName = "Film.mkv", Sha256 = "aaa" };
        var b = new MediaFile { FileName = "Film.mkv", Sha256 = "bbb" };

        Assert.True(ConsolidationRules.AreCopies(a, b, DuplicateMatch.SameName));
        Assert.False(ConsolidationRules.AreCopies(a, b, DuplicateMatch.SameContent));
    }
}

public class SettingsRoundTripTests
{
    [Fact]
    public void RulesAndLetterFoldersSurviveBeingSaved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc-settings-{Guid.NewGuid():N}.xml");
        try
        {
            var settings = new AppSettings();
            settings.CategoryFolders.Add(new CategoryConsolidation
            {
                Category = "Movie",
                Folder = @"F:\Films",
                UseLetterFolders = false,
                MatchBy = DuplicateMatch.SameName,
                DeepCheckBeforeConsolidating = true,
                Rules =
                {
                    new ConsolidationRule
                    {
                        Field = ConsolidationField.Length,
                        Prefer = RulePreference.Greater,
                        Tolerance = 60
                    }
                }
            });
            settings.Save(path);

            var loaded = AppSettings.Load(path);
            var movie = loaded.ConsolidationFor("Movie");

            Assert.NotNull(movie);
            Assert.False(movie!.UseLetterFolders);
            Assert.Equal(DuplicateMatch.SameName, movie.MatchBy);
            Assert.True(movie.DeepCheckBeforeConsolidating);
            Assert.Single(movie.Rules);
            Assert.Equal(60, movie.Rules[0].Tolerance);

            // An unset flag still means "whatever this category has always done".
            Assert.True(loaded.UseLetterFoldersFor("TvShow"));
            Assert.False(loaded.UseLetterFoldersFor("Movie"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
