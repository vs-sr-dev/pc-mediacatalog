using System.Text;
using System.Xml.Linq;

namespace MediaCatalog.Core.Plugins;

/// <summary>What sort of thing a field a plugin returns holds, so it can be compared sensibly.</summary>
public enum PluginFieldType
{
    /// <summary>Words. Compared as text, and sorted as text.</summary>
    Text = 0,

    /// <summary>A figure. Compared as a number, so 9 is less than 10 rather than more.</summary>
    Number = 1,

    /// <summary>A date. Compared as a date, whatever order the plugin wrote it in.</summary>
    Date = 2,

    /// <summary>Yes or no.</summary>
    Truth = 3
}

/// <summary>
/// One thing a plugin can say about a file — "Author", "Number of pages" — as the plugin
/// declared it.
/// </summary>
/// <param name="Name">
/// The identifier the rules refer to it by: <c>File1.Author</c>. Letters, digits and
/// underscores only, because it has to be writable in a rule.
/// </param>
/// <param name="Label">What it is called on screen, which may be any words at all.</param>
/// <param name="Meaning">What it means, for the tooltip. Empty when the plugin said nothing.</param>
public sealed record PluginField(
    string Name, string Label, PluginFieldType Type, string Meaning)
{
    /// <summary>The plugin this field came from, filled in as it is registered.</summary>
    public string PluginName { get; init; } = string.Empty;

    /// <summary>The media type it belongs to — "EBook" — which is also its category.</summary>
    public string MediaType { get; init; } = string.Empty;
}

/// <summary>A file type a plugin claims: the extension a scan should pick up, and what it is.</summary>
public sealed record PluginFileType(string Extension, string Description);

/// <summary>Everything a plugin says about itself, once its XML has been read.</summary>
public sealed record PluginManifest(
    string Name,
    string Version,
    string MediaType,
    string Description,
    IReadOnlyList<PluginField> Fields);

/// <summary>What was wrong with what a plugin returned, or with the plugin itself.</summary>
public sealed class PluginException : Exception
{
    public PluginException(string message) : base(message) { }
    public PluginException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The XML a plugin talks in, read and written in one place.
///
/// Everything crossing the boundary is a string, and deliberately: a plugin is somebody
/// else's assembly, built at some other time against some other version of this program,
/// and the moment a call passes a type of ours between the two there is a version of that
/// type on each side that has to agree forever. A string does not have that problem, and XML
/// is a string with a shape — which is exactly what is wanted from something a person is
/// going to write by hand in a plugin of forty lines.
///
/// Everything here is forgiving on the way in and strict on the way out. A plugin that
/// writes <c>&lt;type ext=".epub"/&gt;</c> rather than <c>extension</c>, or that hands back
/// a bare list of extensions instead of any XML at all, is understood rather than refused:
/// the point of a plugin format is that somebody writes one in an afternoon.
/// </summary>
public static class PluginXml
{
    // --- Reading what a plugin says about itself ----------------------------

    /// <summary>
    /// Read a plugin's <c>Describe()</c>. The shape is:
    ///
    /// <code>
    /// &lt;plugin name="E-books" version="1.0" media="EBook"&gt;
    ///   &lt;description&gt;Catalogues e-books.&lt;/description&gt;
    ///   &lt;fields&gt;
    ///     &lt;field name="Author" label="Author" type="text"/&gt;
    ///     &lt;field name="Pages"  label="Number of pages" type="number"/&gt;
    ///   &lt;/fields&gt;
    /// &lt;/plugin&gt;
    /// </code>
    /// </summary>
    public static PluginManifest ParseManifest(string? xml)
    {
        var root = Root(xml, "the plugin's description");

        var name = Attribute(root, "name", "title") ?? "(unnamed plugin)";
        var version = Attribute(root, "version") ?? string.Empty;
        var media = Attribute(root, "media", "mediaType", "category", "type")
                    ?? Identifier(name);
        var description = root.Element(Name(root, "description"))?.Value.Trim()
                          ?? Attribute(root, "description") ?? string.Empty;

        var mediaType = Identifier(media);
        if (mediaType.Length == 0)
            throw new PluginException(
                "The plugin does not say what sort of media it handles. Put a media=\"…\" on " +
                "its <plugin> element — \"EBook\", say — which is the category its files are " +
                "filed under.");

        var fields = new List<PluginField>();
        foreach (var element in Descendants(root, "field"))
        {
            var field = ParseField(element, mediaType, name);
            if (field != null) fields.Add(field);
        }

        return new PluginManifest(name.Trim(), version.Trim(), mediaType, description, fields);
    }

    private static PluginField? ParseField(XElement element, string mediaType, string plugin)
    {
        var label = Attribute(element, "label", "title", "name") ?? element.Value.Trim();
        if (string.IsNullOrWhiteSpace(label)) return null;

        var name = Identifier(Attribute(element, "name") ?? label);
        if (name.Length == 0) return null;

        return new PluginField(
            name,
            label.Trim(),
            ParseFieldType(Attribute(element, "type", "kind")),
            Attribute(element, "meaning", "description", "help") ?? string.Empty)
        {
            PluginName = plugin.Trim(),
            MediaType = mediaType
        };
    }

    /// <summary>A field's type, defaulting to text — the answer that is never wrong, only vague.</summary>
    public static PluginFieldType ParseFieldType(string? type) => (type ?? "").Trim().ToLowerInvariant() switch
    {
        "number" or "int" or "integer" or "double" or "decimal" or "num" => PluginFieldType.Number,
        "date" or "datetime" or "time" => PluginFieldType.Date,
        "truth" or "bool" or "boolean" or "yesno" or "flag" => PluginFieldType.Truth,
        _ => PluginFieldType.Text
    };

    // --- Reading the file types a plugin claims -----------------------------

    /// <summary>
    /// Read a plugin's <c>FileTypes()</c>: what a scan should pick up on its account.
    ///
    /// <code>
    /// &lt;fileTypes&gt;
    ///   &lt;type extension=".epub" description="EPUB e-book"/&gt;
    /// &lt;/fileTypes&gt;
    /// </code>
    ///
    /// A plain list — <c>.epub .mobi .azw3</c> — is read too, since a plugin whose whole
    /// answer is three extensions should not have to build a document to say so.
    /// </summary>
    public static IReadOnlyList<PluginFileType> ParseFileTypes(string? text)
    {
        var source = (text ?? string.Empty).Trim();
        if (source.Length == 0) return Array.Empty<PluginFileType>();

        var types = new List<PluginFileType>();

        if (source.StartsWith("<", StringComparison.Ordinal))
        {
            var root = Root(source, "the plugin's file types");
            foreach (var element in Descendants(root, "type", "fileType", "extension"))
            {
                var extension = Attribute(element, "extension", "ext", "name") ?? element.Value;
                Add(extension, Attribute(element, "description", "label", "meaning") ?? string.Empty);
            }
        }
        else
        {
            foreach (var part in source.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
                Add(part, string.Empty);
        }

        return types;

        void Add(string extension, string description)
        {
            var ext = NormaliseExtension(extension);
            if (ext.Length == 0) return;
            if (types.Any(t => string.Equals(t.Extension, ext, StringComparison.OrdinalIgnoreCase)))
                return;
            types.Add(new PluginFileType(ext, description.Trim()));
        }
    }

    /// <summary>".EPUB", "epub" and " .epub " are all the same extension, written the one way.</summary>
    public static string NormaliseExtension(string? extension)
    {
        var ext = (extension ?? string.Empty).Trim().Trim('*').Trim();
        if (ext.Length == 0) return string.Empty;
        if (!ext.StartsWith('.')) ext = "." + ext;
        return ext.Length <= 1 ? string.Empty : ext.ToLowerInvariant();
    }

    // --- Reading what a plugin found in one file ----------------------------

    /// <summary>
    /// Read a plugin's <c>Read(path)</c>: what it made of one file.
    ///
    /// <code>
    /// &lt;file&gt;
    ///   &lt;field name="Author" value="Iain M. Banks"/&gt;
    ///   &lt;field name="Pages"&gt;471&lt;/field&gt;
    /// &lt;/file&gt;
    /// </code>
    ///
    /// A plugin that writes each field as its own element — <c>&lt;Author&gt;…&lt;/Author&gt;</c>
    /// — is read the same way, because that is the shape half of everybody will reach for.
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> ParseValues(string? xml)
    {
        var source = (xml ?? string.Empty).Trim();
        if (source.Length == 0) return Array.Empty<(string, string)>();

        var root = Root(source, "what the plugin read from the file");
        var values = new List<(string Name, string Value)>();

        foreach (var element in root.Elements())
        {
            var local = element.Name.LocalName;
            var named = string.Equals(local, "field", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(local, "attribute", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(local, "value", StringComparison.OrdinalIgnoreCase);

            var name = Identifier(named ? Attribute(element, "name", "field", "key") ?? "" : local);
            if (name.Length == 0) continue;

            var value = Attribute(element, "value", "text") ?? element.Value;
            values.Add((name, (value ?? string.Empty).Trim()));
        }

        return values;
    }

    // --- Writing, for the sample plugin and the documentation ---------------

    /// <summary>A manifest written back out, so what is shown and what is read cannot differ.</summary>
    public static string Write(PluginManifest manifest)
    {
        var fields = new XElement("fields",
            manifest.Fields.Select(f => new XElement("field",
                new XAttribute("name", f.Name),
                new XAttribute("label", f.Label),
                new XAttribute("type", f.Type.ToString().ToLowerInvariant()),
                f.Meaning.Length > 0 ? new XAttribute("meaning", f.Meaning) : null)));

        return new XElement("plugin",
            new XAttribute("name", manifest.Name),
            new XAttribute("version", manifest.Version),
            new XAttribute("media", manifest.MediaType),
            new XElement("description", manifest.Description),
            fields).ToString();
    }

    /// <summary>File types written back out.</summary>
    public static string Write(IEnumerable<PluginFileType> types) =>
        new XElement("fileTypes",
            types.Select(t => new XElement("type",
                new XAttribute("extension", t.Extension),
                new XAttribute("description", t.Description)))).ToString();

    /// <summary>One file's values written back out — what a plugin's Read returns.</summary>
    public static string Write(IEnumerable<(string Name, string Value)> values) =>
        new XElement("file",
            values.Select(v => new XElement("field",
                new XAttribute("name", v.Name),
                new XAttribute("value", v.Value ?? string.Empty)))).ToString();

    // --- Odds and ends ------------------------------------------------------

    /// <summary>
    /// A name turned into something a rule can be written with: "Year published" becomes
    /// <c>YearPublished</c>. A rule reads <c>File1.YearPublished</c>, so the name has to be
    /// one word of letters and digits whatever the plugin author called it on screen.
    /// </summary>
    public static string Identifier(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        var capitalise = false;

        foreach (var c in text.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(capitalise ? char.ToUpperInvariant(c) : c);
                capitalise = false;
            }
            else capitalise = builder.Length > 0;
        }

        // A name has to start with a letter to be told apart from a number in a rule.
        while (builder.Length > 0 && !char.IsLetter(builder[0])) builder.Remove(0, 1);
        if (builder.Length == 0) return string.Empty;

        builder[0] = char.ToUpperInvariant(builder[0]);
        return builder.ToString();
    }

    private static XElement Root(string? xml, string what)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new PluginException($"The plugin returned nothing for {what}.");

        try { return XDocument.Parse(xml).Root ?? throw new PluginException($"{what} is empty."); }
        catch (PluginException) { throw; }
        catch (Exception ex)
        {
            throw new PluginException($"{what} is not readable XML: {ex.Message}", ex);
        }
    }

    /// <summary>An attribute under any of the names people write it under, case regardless.</summary>
    private static string? Attribute(XElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var found = element.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, name,
                    StringComparison.OrdinalIgnoreCase));
            if (found != null && !string.IsNullOrWhiteSpace(found.Value)) return found.Value;
        }
        return null;
    }

    private static XName Name(XElement context, string local) =>
        context.Name.Namespace + local;

    /// <summary>Elements by local name, wherever they sit and whatever namespace they carry.</summary>
    private static IEnumerable<XElement> Descendants(XElement root, params string[] names) =>
        root.Descendants().Where(e =>
            names.Any(n => string.Equals(e.Name.LocalName, n, StringComparison.OrdinalIgnoreCase)));
}
