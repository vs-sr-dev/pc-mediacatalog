using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Models;
using Xunit;

namespace MediaCatalog.Tests;

public class ClassifierTests
{
    /// <summary>An en dash, which is what the mangled separator in those names really is.</summary>
    private const string Dash = "\u2013";

    /// <summary>The replacement character a lossy encoding leaves in its place.</summary>
    private const string Mangled = "\uFFFD";

    private static MediaFile Parse(string fullPath)
    {
        var file = new MediaFile
        {
            FullPath = fullPath,
            FileName = Path.GetFileName(fullPath),
            Extension = Path.GetExtension(fullPath).ToLowerInvariant()
        };
        MediaClassifier.Classify(file);
        return file;
    }

    [Theory]
    [InlineData(@"D:\x\Home Improvement 5-26 Games Flames And Automobiles.avi", 5, 26)]
    [InlineData(@"D:\x\The Dead Zone - 01 01 - Wheel Of Fortune.mkv", 1, 1)]
    [InlineData(@"D:\x\The Dead Zone - 04 01 - Broken Circle.mkv", 4, 1)]
    [InlineData(@"D:\x\Show S01E02 Thing.mkv", 1, 2)]
    [InlineData(@"D:\x\Show 1x02 Thing.mkv", 1, 2)]
    [InlineData(@"D:\x\Sabrina, The Teenage Witch [01-01] Pilot [Dvdrip SAiNTS].avi", 1, 1)]
    [InlineData(@"D:\x\Some Show (02-14) Title.mkv", 2, 14)]
    [InlineData(@"D:\x\bull.2016.101.hdtv-lol[ettv].mkv", 1, 1)]
    [InlineData(@"D:\x\01 - the.flash.2014.101.hdtv-lol.mp4", 1, 1)]
    [InlineData(@"D:\x\some.show.2012.1102.hdtv-lol.mkv", 11, 2)]
    public void ReadsSeasonAndEpisode(string path, int season, int episode)
    {
        var file = Parse(path);
        Assert.Equal(season, file.Season);
        Assert.Equal(episode, file.Episode);
    }

    [Theory]
    [InlineData("Dexter (s8 {0} 1) A Beautiful Day.FLV", 8, 1)]
    [InlineData("Dexter (s8 {0} ep 3) Whats Eating Dexter Morgan.FLV", 8, 3)]
    public void ReadsSeasonAndEpisodeAcrossADash(string template, int season, int episode)
    {
        foreach (var separator in new[] { Dash, Mangled, "-" })
        {
            var file = Parse(@"D:\x\" + string.Format(template, separator));
            Assert.Equal(season, file.Season);
            Assert.Equal(episode, file.Episode);
        }
    }

    [Theory]
    [InlineData(@"D:\x\Blade Runner 2049 (2017) 1080p.mkv")]
    [InlineData(@"D:\x\The Matrix 1999.mkv")]
    [InlineData(@"D:\x\Blade.Runner.2049.2017.1080p.BluRay.x264.mkv")]
    [InlineData(@"D:\x\Some.Film.2016.1080p.BluRay.x264-GROUP.mkv")]
    [InlineData(@"D:\x\Some.Film.2016.720p.HDTV.x264.mkv")]
    [InlineData(@"D:\x\Some Film (2009-2012 remaster).mkv")]
    public void DoesNotInventNumberingForFilms(string path)
    {
        var file = Parse(path);
        Assert.Null(file.Season);
    }

    /// <summary>
    /// The episode prefix consolidation writes opens the name, so there is no word in front
    /// of it — which is what keeps "11-12 - Name.mkv" from reading as season 11 episode 12.
    /// </summary>
    [Fact]
    public void DoesNotReadTheEpisodePrefixAsASeasonCode()
    {
        var file = Parse(@"D:\x\11-12 - Games Flames And Automobiles.avi");
        Assert.Null(file.Season);
    }

    [Theory]
    [InlineData(@"D:\x\Show S06E11E12 Thing.mkv", 6, 11, 12)]
    [InlineData(@"D:\x\Show S01E01-E02 Thing.mkv", 1, 1, 2)]
    public void ReadsDoubleEpisodes(string path, int season, int first, int last)
    {
        var file = Parse(path);
        Assert.Equal(season, file.Season);
        Assert.Equal(first, file.Episode);
        Assert.Equal(last, file.EpisodeEnd);
    }

    [Theory]
    [InlineData(@"D:\TV\K\King Of The Hill\Season 04\1.avi", 4, 1)]
    [InlineData(@"D:\TV\Yes Minister, Season Three\01. Equal Opportunities.mkv", 3, 1)]
    public void ReadsWhatTheFoldersSay(string path, int season, int episode)
    {
        var file = Parse(path);
        Assert.Equal(season, file.Season);
        Assert.Equal(episode, file.Episode);
    }
}
