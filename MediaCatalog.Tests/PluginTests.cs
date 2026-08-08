using System.IO.Compression;
using System.Text;
using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Consolidation;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Plugins;
using MediaCatalog.Core.Scanning;
using MediaCatalog.Core.Storage;
using Xunit;

namespace MediaCatalog.Tests;

/// <summary>
/// The plugin contract, tested against a plugin loaded off disk through its own load context
/// by the shape of its methods — which is the whole of what makes a plugin a plugin. A
/// plugin system tested only against a class written inside the test assembly is a plugin
/// system tested against the one arrangement that cannot go wrong.
/// </summary>
public class PluginTests : IDisposable
{
    /// <summary>The example plugin, as the program would load it.</summary>
    private static MediaPlugin LoadEBooks() =>
        MediaPlugin.Load(Path.Combine(AppContext.BaseDirectory, "MediaCatalog.Plugins.EBooks.dll"));

    public void Dispose() => MediaPlugins.Clear();

    [Fact]
    public void APluginIsFoundByTheShapeOfItsMethods()
    {
        var plugin = LoadEBooks();

        Assert.True(plugin.IsUsable, plugin.Problem);
        Assert.Equal("E-books", plugin.Name);
        Assert.Equal("EBook", plugin.MediaType);
        Assert.True(plugin.Handles(".epub"));
        Assert.True(plugin.Handles(".EPUB"));       // an extension is not case
        Assert.False(plugin.Handles(".mkv"));
        Assert.Contains(plugin.Fields, f => f.Name == "Author" && f.Type == PluginFieldType.Text);
        Assert.Contains(plugin.Fields, f => f.Name == "Pages" && f.Type == PluginFieldType.Number);
    }

    [Fact]
    public void AnAssemblyWithNoPluginInItSaysSoRatherThanThrowing()
    {
        var plugin = MediaPlugin.Bind("nothing.dll", new[] { typeof(string) });

        Assert.False(plugin.IsUsable);
        Assert.NotEmpty(plugin.Problem);
    }

    /// <summary>A DLL that is not a .NET assembly at all — a native one, or a renamed zip.</summary>
    [Fact]
    public void SomethingThatIsNotAnAssemblySaysWhyItWouldNotLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllText(path, "this is not an assembly");
        try
        {
            var plugin = MediaPlugin.Load(path);
            Assert.False(plugin.IsUsable);
            Assert.Contains("would not load", plugin.Problem);
        }
        finally { File.Delete(path); }
    }

    // --- What it reads out of a real file -----------------------------------

    [Fact]
    public void ReadsWhatTheBookSaysAboutItself()
    {
        var path = WriteEpub("Someone Else - Some Other Title (2011).epub");
        try
        {
            var values = LoadEBooks().Read(path)
                .ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

            // The book's own metadata beats what the file happens to be called.
            Assert.Equal("Consider Phlebas", values["BookName"]);
            Assert.Equal("Iain M. Banks", values["Author"]);
            Assert.Equal("1987", values["YearPublished"]);
            Assert.Equal("Macmillan", values["Publisher"]);
            Assert.Equal("en", values["Language"]);
            Assert.Equal("3", values["Chapters"]);

            // An EPUB reflows. It has no pages, and the plugin does not invent any.
            Assert.False(values.ContainsKey("Pages"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FallsBackOnTheFileNameForWhatItCannotOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), "Iain M. Banks - Use of Weapons (1990).mobi");
        File.WriteAllText(path, "not really a mobi");
        try
        {
            var values = LoadEBooks().Read(path)
                .ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

            Assert.Equal("Iain M. Banks", values["Author"]);
            Assert.Equal("Use of Weapons", values["BookName"]);
            Assert.Equal("1990", values["YearPublished"]);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A field nobody declared has no label, no type and nowhere to go, so it is dropped.</summary>
    [Fact]
    public void OnlyDeclaredFieldsComeBack()
    {
        var plugin = MediaPlugin.Bind("chatty.dll", new[] { typeof(ChattyPlugin) });

        var values = plugin.Read("anything");

        Assert.Contains(values, v => v.Name == "Declared");
        Assert.DoesNotContain(values, v => v.Name == "Undeclared");
    }

    // --- What a loaded plugin changes about the program ---------------------

    [Fact]
    public void ALoadedPluginMakesItsFileTypesMedia()
    {
        Assert.False(MediaExtensions.IsMedia(".epub"));

        MediaPlugins.Use(new[] { LoadEBooks() });

        Assert.True(MediaExtensions.IsMedia(".epub"));
        Assert.True(MediaExtensions.IsPluginMedia(".epub"));
        Assert.Equal(MediaKind.Other, MediaExtensions.Classify(".epub"));

        // It cannot take away what the program already knows.
        Assert.Equal(MediaKind.Video, MediaExtensions.Classify(".mkv"));
    }

    [Fact]
    public void APluginBringsACategoryWithIt()
    {
        MediaPlugins.Use(new[] { LoadEBooks() });

        var settings = new AppSettings();
        Assert.Contains("EBook", CategoryResolver.All(settings));

        var book = new MediaFile { Extension = ".epub", Kind = MediaKind.Other };
        Assert.Equal("EBook", CategoryResolver.Auto(book));
    }

    /// <summary>
    /// A file whose name carries an episode code is still a book. The plugin owns the
    /// extension, so nothing else gets a say about what the file is.
    /// </summary>
    [Fact]
    public void ANameThatLooksLikeAnEpisodeDoesNotMakeABookIntoTelevision()
    {
        MediaPlugins.Use(new[] { LoadEBooks() });

        var book = new MediaFile
        {
            Extension = ".epub", Kind = MediaKind.Other, Season = 1, Episode = 2
        };

        Assert.Equal("EBook", CategoryResolver.Auto(book));
    }

    [Fact]
    public void APluginsFieldsBecomeThingsARuleCanWrite()
    {
        MediaPlugins.Use(new[] { LoadEBooks() });

        Assert.Contains(RuleScriptVocabulary.Properties, p => p.Name == "Author");
        Assert.Contains(RuleScriptVocabulary.Properties, p => p.Name == "Pages");

        Assert.True(RuleScriptParser.TryParse(
            "if (File1.Pages > File2.Pages) Consolidate(File1)\nConsolidate(File2)",
            out _, out var error), error);
    }

    /// <summary>
    /// A plugin must never take a name the built-in rules already use. File1.Size meaning the
    /// size on disk for a film and something else entirely for a book is a rule that means two
    /// things, which is a rule nobody can read.
    /// </summary>
    [Fact]
    public void APluginCannotTakeANameTheBuiltInRulesUse()
    {
        MediaPlugins.Use(new[] { MediaPlugin.Bind("greedy.dll", new[] { typeof(GreedyPlugin) }) });

        Assert.DoesNotContain(RuleScriptVocabulary.PluginProperties, p => p.Name == "Size");
        Assert.Contains(RuleScriptVocabulary.PluginProperties, p => p.Name == "Binding");
    }

    // --- Choosing between two books -----------------------------------------

    private static MediaFile Book(string name, params (string Field, string Value)[] fields) => new()
    {
        FileName = name,
        FullPath = @"D:\books\" + name,
        Extension = ".epub",
        Kind = MediaKind.Other,
        SizeBytes = 1024 * 1024,
        PluginFields = fields
            .Select(f => new MediaFileField { Name = f.Field, Value = f.Value })
            .ToList()
    };

    [Fact]
    public async Task RulesCanChooseOnAPluginsNumberField()
    {
        MediaPlugins.Use(new[] { LoadEBooks() });

        var thin = Book("thin.epub", ("Pages", "180"));
        var thick = Book("thick.epub", ("Pages", "640"));

        var program = RuleScriptParser.Parse(
            "if (File1.Pages > File2.Pages) Consolidate(File1)\nConsolidate(File2)");
        var verdict = await new RuleScriptSession(program, new Nothing()).ChooseAsync(
            new[] { thin, thick });

        Assert.Same(thick, verdict.Winner);
    }

    [Fact]
    public async Task RulesCanChooseOnAPluginsTextField()
    {
        MediaPlugins.Use(new[] { LoadEBooks() });

        var wanted = Book("a.epub", ("Author", "Iain M. Banks"));
        var other = Book("b.epub", ("Author", "Somebody Else"));

        var program = RuleScriptParser.Parse(
            "if (File1.Author == \"iain m. banks\") Consolidate(File1)\nConsolidate(File2)");
        var verdict = await new RuleScriptSession(program, new Nothing()).ChooseAsync(
            new[] { wanted, other });

        Assert.Same(wanted, verdict.Winner);
    }

    [Fact]
    public void StepsCanChooseOnAPluginsField()
    {
        MediaPlugins.Use(new[] { LoadEBooks() });

        var thin = Book("thin.epub", ("Pages", "180"));
        var thick = Book("thick.epub", ("Pages", "640"));

        var steps = new List<ConsolidationRule>
        {
            new() { FieldName = "Pages", Prefer = RulePreference.Greater }
        };

        Assert.Same(thick, ConsolidationRules.Choose(new[] { thin, thick }, steps).Winner);
        Assert.Contains("640", ConsolidationRules.Choose(new[] { thin, thick }, steps).Why);
    }

    /// <summary>
    /// The case that would otherwise ruin a library: a step about page counts, applied to two
    /// films, has to stand aside so the step under it can decide — rather than find them
    /// equal at nothing and stop everything below it.
    /// </summary>
    [Fact]
    public void AStepAboutAFieldNeitherFileHasStandsAside()
    {
        MediaPlugins.Use(new[] { LoadEBooks() });

        var small = new MediaFile { FileName = "a.mkv", SizeBytes = 100, Kind = MediaKind.Video };
        var big = new MediaFile { FileName = "b.mkv", SizeBytes = 900, Kind = MediaKind.Video };

        var steps = new List<ConsolidationRule>
        {
            new() { FieldName = "Pages", Prefer = RulePreference.Greater },
            new() { Field = ConsolidationField.Size, Prefer = RulePreference.Greater }
        };

        Assert.Same(big, ConsolidationRules.Choose(new[] { small, big }, steps).Winner);
    }

    /// <summary>
    /// A plugin can be slow — one that opens an archive to read a book's metadata is far too
    /// slow to ask again every time a window opens — so what it said is written to the
    /// catalogue and has to survive the round trip.
    /// </summary>
    [Fact]
    public void WhatAPluginSaidSurvivesTheCatalogue()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var catalog = new Catalog();
            catalog.Files.Add(Book("book.epub", ("Author", "Iain M. Banks"), ("Pages", "471")));
            MediaCatalog.Core.Persistence.CatalogStore.Save(catalog, path);

            var loaded = MediaCatalog.Core.Persistence.CatalogStore.Load(path);
            var book = loaded.Files.Single();

            Assert.Equal("Iain M. Banks", book.FieldValue("Author"));
            Assert.Equal("471", book.FieldValue("Pages"));
            Assert.Equal(string.Empty, book.FieldValue("NothingLikeThis"));
            Assert.True(book.HasPluginFields);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>A settings round trip has to keep a step that names a plugin's field.</summary>
    [Fact]
    public void AStepAboutAPluginFieldSurvivesTheSettingsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var settings = new AppSettings();
            settings.CategoryFolders.Add(new CategoryConsolidation
            {
                Category = "EBook",
                Folder = @"D:\Books",
                Rules =
                {
                    new ConsolidationRule { FieldName = "Pages", Prefer = RulePreference.Greater }
                }
            });
            settings.Save(path);

            var step = AppSettings.Load(path).RulesFor("EBook").Single();

            Assert.Equal("Pages", step.FieldName);
            Assert.True(step.IsPluginField);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // --- The XML itself ------------------------------------------------------

    [Theory]
    [InlineData("Year published", "YearPublished")]
    [InlineData("number_of_pages", "NumberOfPages")]
    [InlineData("  author  ", "Author")]
    [InlineData("3D model", "DModel")]     // a name has to start with a letter to be written
    [InlineData("!!!", "")]
    public void FieldNamesBecomeSomethingARuleCanWrite(string given, string expected) =>
        Assert.Equal(expected, PluginXml.Identifier(given));

    [Theory]
    [InlineData("epub", ".epub")]
    [InlineData(".EPUB", ".epub")]
    [InlineData("*.epub", ".epub")]
    [InlineData(" ", "")]
    public void ExtensionsAreWrittenTheOneWay(string given, string expected) =>
        Assert.Equal(expected, PluginXml.NormaliseExtension(given));

    /// <summary>A plugin whose whole answer is three extensions should not have to build a document.</summary>
    [Fact]
    public void APlainListOfExtensionsIsUnderstood()
    {
        var types = PluginXml.ParseFileTypes(".epub, .mobi .azw3");

        Assert.Equal(new[] { ".epub", ".mobi", ".azw3" }, types.Select(t => t.Extension));
    }

    [Fact]
    public void AFieldWrittenAsItsOwnElementIsUnderstood()
    {
        var values = PluginXml.ParseValues(
            "<file><Author>Iain M. Banks</Author><Pages>471</Pages></file>");

        Assert.Equal("Iain M. Banks", values.Single(v => v.Name == "Author").Value);
        Assert.Equal("471", values.Single(v => v.Name == "Pages").Value);
    }

    [Fact]
    public void ADescriptionThatIsNotXmlSaysSoRatherThanThrowing() =>
        Assert.Throws<PluginException>(() => PluginXml.ParseManifest("not xml at all"));

    // --- Test plugins --------------------------------------------------------

    /// <summary>Declares one field and returns two, to prove the undeclared one is dropped.</summary>
    public class ChattyPlugin
    {
        public string Describe() =>
            "<plugin name='Chatty' media='Chat'><fields><field name='Declared' type='text'/></fields></plugin>";

        public string FileTypes() => ".chat";

        public string Read(string path) =>
            "<file><field name='Declared' value='yes'/><field name='Undeclared' value='no'/></file>";
    }

    /// <summary>Tries to take a name the built-in rules already own.</summary>
    public class GreedyPlugin
    {
        public string Describe() =>
            "<plugin name='Greedy' media='Greedy'><fields>" +
            "<field name='Size' type='number'/><field name='Binding' type='text'/>" +
            "</fields></plugin>";

        public string FileTypes() => ".greedy";

        public string Read(string path) => "<file/>";
    }

    /// <summary>Nothing expensive can be done to a book, and nothing here tries.</summary>
    private sealed class Nothing : IRuleScriptServices
    {
        public Task ProbeAsync(MediaFile file, CancellationToken ct) => Task.CompletedTask;
        public Task DeepScanAsync(MediaFile file, CancellationToken ct) => Task.CompletedTask;
        public Task FingerprintAsync(MediaFile file, CancellationToken ct) => Task.CompletedTask;
    }

    // --- A real EPUB, made here ---------------------------------------------

    /// <summary>
    /// A small but genuine EPUB: a zip holding the container that points at the OPF, and an
    /// OPF holding the Dublin Core metadata and a three-document spine.
    /// </summary>
    private static string WriteEpub(string fileName)
    {
        var path = Path.Combine(Path.GetTempPath(), fileName);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        Add("META-INF/container.xml",
            """
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf"
                media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """);

        Add("OEBPS/content.opf",
            """
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Consider Phlebas</dc:title>
                <dc:creator>Iain M. Banks</dc:creator>
                <dc:date>1987-04-23</dc:date>
                <dc:publisher>Macmillan</dc:publisher>
                <dc:language>en</dc:language>
                <dc:subject>Science Fiction</dc:subject>
              </metadata>
              <spine>
                <itemref idref="c1"/><itemref idref="c2"/><itemref idref="c3"/>
              </spine>
            </package>
            """);

        return path;

        void Add(string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }
}
