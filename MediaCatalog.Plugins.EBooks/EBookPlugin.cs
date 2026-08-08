using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MediaCatalog.Plugins.EBooks;

/// <summary>
/// Teaches Media Catalog about e-books.
///
/// This is the worked example of a plugin as much as it is a useful one. The whole contract
/// is three public methods that take and return strings — no interface to implement, no
/// assembly of the host's to reference, nothing to keep in step with a version of the program
/// that ships next year:
///
/// <code>
/// string Describe();             what I am, and the fields I can fill in
/// string FileTypes();            the extensions a scan should pick up on my account
/// string Read(string fullPath);  what I make of one file
/// </code>
///
/// The strings are XML, and the shapes are below. Everything the program does with what comes
/// back — a column in the results, a value in the filter, something a consolidation rule can
/// compare two copies on — follows from the fields declared in <see cref="Describe"/>. A
/// field returned by <see cref="Read"/> that was never declared is ignored, which is
/// deliberate: a field nobody declared has no label, no type and no place to be shown.
///
/// What it actually reads: an EPUB properly, out of the OPF metadata inside the archive; a
/// PDF's page count by counting page objects; and for the rest, what the file name and the
/// file system can say. That last part is not a failure of the plugin so much as the honest
/// answer for formats whose metadata lives somewhere this has no business going.
/// </summary>
public class EBookPlugin
{
    /// <summary>
    /// What this plugin is, and every field it can fill in.
    ///
    /// <c>media</c> is the category e-books are filed under. It becomes a real category in the
    /// program: it turns up in the category dropdown, it can be given a consolidation folder
    /// and a naming pattern, and it can have consolidation rules of its own written against
    /// the very fields declared here.
    /// </summary>
    public string Describe() => """
        <plugin name="E-books" version="1.0" media="EBook">
          <description>
            Catalogues e-books. Reads EPUB metadata out of the archive, counts a PDF's pages,
            and falls back on the file name for the rest.
          </description>
          <fields>
            <field name="BookName"      label="Book name"        type="text"
                   meaning="The title as the book itself gives it, not as the file is named." />
            <field name="Author"        label="Author"           type="text"
                   meaning="Whoever the book says wrote it. Several are joined with commas." />
            <field name="YearPublished" label="Year published"   type="number"
                   meaning="The publication year the book carries, which is not always the year of this edition." />
            <field name="Publisher"     label="Publisher"        type="text"
                   meaning="The imprint, when the book names one." />
            <field name="BookGenre"     label="Book genre"       type="text"
                   meaning="The subjects the book lists, joined with commas. Nobody agrees on these." />
            <field name="Language"      label="Language"         type="text"
                   meaning="The language code the book declares - en, it, fr." />
            <field name="Chapters"      label="Number of chapters" type="number"
                   meaning="How many documents the book's reading order holds. A chapter in practice, though a book that splits its chapters across files will read high." />
            <field name="Pages"         label="Number of pages"  type="number"
                   meaning="Pages, for a format that has any. An EPUB reflows and has none, so this is empty for one." />
          </fields>
        </plugin>
        """;

    /// <summary>
    /// The extensions a scan should pick up for this plugin. Anything the program already
    /// knows — .mp4, .flac — is never given away, so a plugin cannot take video off it.
    /// </summary>
    public string FileTypes() => """
        <fileTypes>
          <type extension=".epub" description="EPUB e-book" />
          <type extension=".mobi" description="Mobipocket e-book" />
          <type extension=".azw"  description="Kindle e-book" />
          <type extension=".azw3" description="Kindle KF8 e-book" />
          <type extension=".fb2"  description="FictionBook e-book" />
          <type extension=".djvu" description="DjVu document" />
          <type extension=".pdf"  description="PDF document" />
        </fileTypes>
        """;

    /// <summary>
    /// What this plugin makes of one file.
    ///
    /// Never throws. A malformed e-book is a thing that exists, and a plugin that falls over
    /// on one would be a plugin that stops a scan of forty thousand files over a single bad
    /// archive. What cannot be read is simply not returned, and a field that is not returned
    /// is empty rather than wrong.
    /// </summary>
    public string Read(string fullPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var extension = Path.GetExtension(fullPath).ToLowerInvariant();

            if (extension == ".epub") ReadEpub(fullPath, values);
            else if (extension == ".pdf") ReadPdf(fullPath, values);

            FromFileName(fullPath, values);
        }
        catch
        {
            // Whatever was gathered before it went wrong is still worth having.
        }

        return Write(values);
    }

    // --- EPUB ---------------------------------------------------------------

    /// <summary>
    /// An EPUB is a zip. Inside it, META-INF/container.xml points at the OPF, and the OPF
    /// holds Dublin Core metadata — title, creator, date, publisher, subject, language — and
    /// the spine, which is the reading order.
    /// </summary>
    private static void ReadEpub(string path, IDictionary<string, string> values)
    {
        using var archive = ZipFile.OpenRead(path);

        var container = archive.GetEntry("META-INF/container.xml");
        if (container == null) return;

        var rootFile = Load(container)?.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "rootfile")
            ?.Attribute("full-path")?.Value;
        if (string.IsNullOrWhiteSpace(rootFile)) return;

        var opfEntry = archive.GetEntry(rootFile) ??
                       archive.Entries.FirstOrDefault(e =>
                           string.Equals(e.FullName, rootFile, StringComparison.OrdinalIgnoreCase));
        if (opfEntry == null) return;

        var opf = Load(opfEntry);
        if (opf == null) return;

        Set(values, "BookName", First(opf, "title"));
        Set(values, "Author", string.Join(", ", All(opf, "creator")));
        Set(values, "Publisher", First(opf, "publisher"));
        Set(values, "Language", First(opf, "language"));
        Set(values, "BookGenre", string.Join(", ", All(opf, "subject")));

        if (Year(First(opf, "date")) is { } year)
            Set(values, "YearPublished", year.ToString(CultureInfo.InvariantCulture));

        // The spine is the order the reader walks the documents in, which is as close to a
        // chapter count as an EPUB gets. A book that splits one chapter across three files
        // reads high, and there is nothing in the format that would let anybody know.
        var spine = opf.Descendants()
            .Where(e => e.Name.LocalName == "itemref")
            .Count();
        if (spine > 0) Set(values, "Chapters", spine.ToString(CultureInfo.InvariantCulture));
    }

    private static XDocument? Load(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }
        catch { return null; }
    }

    private static string First(XDocument document, string name) =>
        All(document, name).FirstOrDefault() ?? string.Empty;

    private static List<string> All(XDocument document, string name) =>
        document.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // --- PDF ----------------------------------------------------------------

    /// <summary>
    /// A PDF's page count, by counting the page objects.
    ///
    /// Crude, and knowingly so: a proper answer means parsing the cross-reference table and
    /// walking the page tree, which is a library rather than a method. Counting "/Type /Page"
    /// is right for the overwhelming majority of files and wrong in a way that is obvious
    /// rather than subtle. The file is only read up to a limit, because a scan should not
    /// stop to read a four-hundred-megabyte scanned atlas end to end.
    /// </summary>
    private static void ReadPdf(string path, IDictionary<string, string> values)
    {
        const int limit = 64 * 1024 * 1024;

        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > limit) return;

        string text;
        try { text = Encoding.Latin1.GetString(File.ReadAllBytes(path)); }
        catch { return; }

        var pages = Regex.Matches(text, @"/Type\s*/Page[^s]").Count;
        if (pages > 0) Set(values, "Pages", pages.ToString(CultureInfo.InvariantCulture));

        // The document information dictionary, when there is one and it is not compressed.
        Set(values, "BookName", Field(text, "Title"));
        Set(values, "Author", Field(text, "Author"));

        static string Field(string source, string key)
        {
            var match = Regex.Match(source, $@"/{key}\s*\(((?:[^()\\]|\\.)*)\)");
            return match.Success ? match.Groups[1].Value.Replace(@"\)", ")").Replace(@"\(", "(") : "";
        }
    }

    // --- The file name, for everything else ---------------------------------

    /// <summary>
    /// What the name says, for the formats this cannot open and the fields the ones it can
    /// did not fill in. "Iain M. Banks - Consider Phlebas (1987).epub" is a great deal of
    /// what anybody wanted, and it is right there.
    /// </summary>
    private static void FromFileName(string path, IDictionary<string, string> values)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        var year = Regex.Match(name, @"[\(\[]?\b(1[5-9]\d{2}|20\d{2})\b[\)\]]?");
        if (year.Success) Set(values, "YearPublished", year.Groups[1].Value);

        var withoutYear = year.Success ? name.Remove(year.Index, year.Length) : name;

        // "Author - Title" is how most of the world names a book file, and the dash is the
        // only thing in the name that means anything structural.
        var parts = withoutYear.Split(" - ", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            Set(values, "Author", Tidy(parts[0]));
            Set(values, "BookName", Tidy(string.Join(" - ", parts.Skip(1))));
        }
        else Set(values, "BookName", Tidy(withoutYear));

        static string Tidy(string s) =>
            Regex.Replace(s.Replace('_', ' '), @"\s{2,}", " ").Trim(' ', '-', '.', '(', ')', '[', ']');
    }

    // --- Odds and ends ------------------------------------------------------

    /// <summary>Only fills a field that is still empty: what the book said beats what its name said.</summary>
    private static void Set(IDictionary<string, string> values, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (values.TryGetValue(name, out var already) && already.Length > 0) return;
        values[name] = value.Trim();
    }

    private static int? Year(string? text)
    {
        var match = Regex.Match(text ?? string.Empty, @"\b(1[5-9]\d{2}|20\d{2})\b");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The answer, in the shape the program reads.</summary>
    private static string Write(IEnumerable<KeyValuePair<string, string>> values) =>
        new XElement("file",
            values.Select(v => new XElement("field",
                new XAttribute("name", v.Key),
                new XAttribute("value", v.Value)))).ToString();
}
