using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Consolidation;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Naming;
using Xunit;

namespace MediaCatalog.Tests;

public class RenameTests
{
    private static MediaFile Extra(string name, string title, string? overrideCategory = null) => new()
    {
        FullPath = @"D:\TV\Burn Notice\Extras\" + name,
        FileName = name,
        Extension = Path.GetExtension(name),
        Kind = MediaKind.Video,
        VideoCategory = VideoCategory.TvExtra,
        TmdbName = title,
        TitleManuallySet = true,
        CategoryOverride = overrideCategory ?? string.Empty
    };

    /// <summary>
    /// The reported bug: a file filed as an extra but categorised TvShow by hand could not be
    /// renamed at all — the naming scheme has nothing to say about either, so nothing was
    /// proposed and a corrected title never reached the disk.
    /// </summary>
    [Theory]
    [InlineData("TvShow")]
    [InlineData("TvExtra")]
    [InlineData("MovieExtra")]
    [InlineData("Other")]
    [InlineData("")]
    public void AnythingCanBeRenamed(string category)
    {
        var file = Extra("Behind The Scenes.mkv", "Burn Notice", category);
        var proposal = RenameService.BuildAnyProposal(file, category.Length == 0 ? null : category);

        Assert.NotNull(proposal);
        Assert.True(proposal!.WillChange);
        Assert.Equal("Burn Notice - Behind The Scenes.mkv", proposal.ProposedName);
    }

    /// <summary>Running it twice must not put the title on twice.</summary>
    [Fact]
    public void TheTitleIsNotAddedTwice()
    {
        var file = Extra("Burn Notice - Behind The Scenes.mkv", "Burn Notice");
        Assert.Null(RenameService.BuildAnyProposal(file, "TvExtra"));
    }

    /// <summary>A corrected title is swapped in place when the old one is in the name.</summary>
    [Fact]
    public void ACorrectedTitleIsSwappedInPlace()
    {
        var file = Extra("Burn Notce - Behind The Scenes.mkv", "Burn Notice");
        var proposal = RenameService.BuildAnyProposal(file, "TvExtra", previousTitle: "Burn Notce");

        Assert.NotNull(proposal);
        Assert.Equal("Burn Notice - Behind The Scenes.mkv", proposal!.ProposedName);
    }

    /// <summary>The naming scheme still wins where it has something to say.</summary>
    [Fact]
    public void AnEpisodeStillFollowsTheScheme()
    {
        var episode = new MediaFile
        {
            FullPath = @"D:\x\whatever.mkv",
            FileName = "whatever.mkv",
            Extension = ".mkv",
            Kind = MediaKind.Video,
            VideoCategory = VideoCategory.TvShow,
            TmdbName = "Burn Notice",
            Season = 1,
            Episode = 2
        };

        var proposal = RenameService.BuildAnyProposal(episode, "TvShow");
        Assert.Equal("Burn Notice - S01E02.mkv", proposal!.ProposedName);
    }

    /// <summary>A file nothing has named cannot be renamed, and says so rather than guessing.</summary>
    [Fact]
    public void AFileWithNoTitleIsLeftAlone()
    {
        var file = new MediaFile
        {
            FullPath = @"D:\x\clip.mkv", FileName = "clip.mkv", Extension = ".mkv",
            Kind = MediaKind.Video, VideoCategory = VideoCategory.Unknown
        };
        Assert.Null(RenameService.BuildAnyProposal(file, "Unknown"));
    }
}

public class ExtraLinkerTests
{
    private static List<MediaFile> Library()
    {
        var episode = new MediaFile
        {
            Id = "owner",
            FullPath = @"D:\TV\Burn Notice\Season 01\01 - Pilot.mkv",
            FileName = "01 - Pilot.mkv",
            Extension = ".mkv",
            Kind = MediaKind.Video,
            VideoCategory = VideoCategory.TvShow,
            ParsedTitle = "Burn Notice",
            TmdbName = "Burn Notice",
            Season = 1,
            Episode = 1,
            SizeBytes = 1000
        };

        var extra = new MediaFile
        {
            Id = "extra",
            FullPath = @"D:\TV\Burn Notice\Extras\Behind The Scenes.mkv",
            FileName = "Behind The Scenes.mkv",
            Extension = ".mkv",
            Kind = MediaKind.Video,
            VideoCategory = VideoCategory.TvExtra
        };

        return new List<MediaFile> { episode, extra };
    }

    [Fact]
    public void AnExtraTakesItsOwnersTitle()
    {
        var files = Library();
        ExtraLinker.Link(files);

        Assert.Equal("Burn Notice", files[1].EffectiveTitle);
        Assert.Equal("owner", files[1].LinkedFileId);
    }

    /// <summary>
    /// The other half of the rename bug: a title typed onto an extra was overwritten with the
    /// owner's the moment anything re-linked, so the rename that should have followed it saw
    /// the old title and did nothing.
    /// </summary>
    [Fact]
    public void AHandTypedTitleOnAnExtraSurvivesLinking()
    {
        var files = Library();
        TitleUpdater.Set(new[] { files[1] }, "The Making Of Burn Notice", manual: true);

        ExtraLinker.Link(files);

        Assert.Equal("The Making Of Burn Notice", files[1].EffectiveTitle);
        Assert.Equal("owner", files[1].LinkedFileId);   // still linked, just not renamed
    }

    /// <summary>A category the user set by hand is the last word on what a file is.</summary>
    [Fact]
    public void AnExtraCategorisedAsAProgrammeIsNoLongerAdopted()
    {
        var files = Library();
        files[1].CategoryOverride = "TvShow";

        ExtraLinker.Link(files);

        Assert.Equal(string.Empty, files[1].EffectiveTitle);
        Assert.False(ExtraLinker.CountsAsExtra(files[1]));
    }
}

public class ConsolidationSessionTests
{
    [Fact]
    public void AnUnfinishedJobSurvivesBeingSaved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc-consolidation-{Guid.NewGuid():N}.xml");
        try
        {
            var session = new ConsolidationSession
            {
                Status = ConsolidationSessionStatus.Paused,
                DeleteOriginal = true,
                Total = 500,
                PendingIds = { "a", "b", "c" }
            };
            session.Save(path);

            var loaded = ConsolidationSession.Load(path);

            Assert.True(loaded.IsResumable);
            Assert.Equal(3, loaded.PendingIds.Count);
            Assert.Equal(497, loaded.Done);
            Assert.True(loaded.DeleteOriginal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// A session left as Running means the program went away mid-job, which is the case this
    /// exists for — so it is offered too.
    /// </summary>
    [Fact]
    public void AnInterruptedJobIsResumableToo()
    {
        var session = new ConsolidationSession
        {
            Status = ConsolidationSessionStatus.Running,
            Total = 10,
            PendingIds = { "x" }
        };
        Assert.True(session.IsResumable);
    }

    [Fact]
    public void AFinishedJobIsNot()
    {
        var session = new ConsolidationSession
        {
            Status = ConsolidationSessionStatus.Running,
            Total = 10
        };
        Assert.False(session.IsResumable);
    }
}
