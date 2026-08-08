using MediaCatalog.Core.Models;
using MediaCatalog.Core.Storage;

namespace MediaCatalog.Core.Plugins;

/// <summary>
/// Every plugin the program has found, and everything the rest of the program asks of them.
///
/// Plugins are loaded once, when the application starts, and again when the user adds or
/// removes one. What they bring — file types a scan picks up, fields a file carries, a
/// category those files are filed under — is folded into the program as though it had always
/// been there: the grid gets columns, the filter gets values, the consolidation rules get
/// something new to compare on, and none of those places knows or cares that a plugin is
/// where it came from.
///
/// Static, and deliberately: the set of plugins is a property of the installation rather
/// than of any one window, and an extension either is a media file or is not — there is no
/// sensible answer to "is .epub media?" that varies by who is asking.
/// </summary>
public static class MediaPlugins
{
    private static readonly List<MediaPlugin> _plugins = new();
    private static readonly Dictionary<string, MediaPlugin> _byExtension =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every plugin found, whether it loaded or not — the broken ones have to be shown.</summary>
    public static IReadOnlyList<MediaPlugin> All => _plugins;

    /// <summary>The ones that loaded and made sense.</summary>
    public static IReadOnlyList<MediaPlugin> Usable => _plugins.Where(p => p.IsUsable).ToList();

    /// <summary>Every extension any plugin claims.</summary>
    public static IReadOnlyCollection<string> Extensions => _byExtension.Keys;

    /// <summary>True when a plugin handles this extension, so a scan should pick the file up.</summary>
    public static bool Handles(string extension) =>
        !string.IsNullOrEmpty(extension) && _byExtension.ContainsKey(extension);

    /// <summary>The plugin that handles an extension, or null.</summary>
    public static MediaPlugin? For(string extension) =>
        string.IsNullOrEmpty(extension) ? null
        : _byExtension.TryGetValue(extension, out var plugin) ? plugin
        : null;

    /// <summary>The categories plugins bring with them — one per plugin, in load order.</summary>
    public static IReadOnlyList<string> Categories =>
        Usable.Select(p => p.MediaType)
              .Where(m => m.Length > 0)
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToList();

    /// <summary>Every field every plugin declares, in one list.</summary>
    public static IReadOnlyList<PluginField> Fields =>
        Usable.SelectMany(p => p.Fields).ToList();

    /// <summary>The fields belonging to one category, for a rules builder scoped to it.</summary>
    public static IReadOnlyList<PluginField> FieldsFor(string category) =>
        string.IsNullOrWhiteSpace(category)
            ? Array.Empty<PluginField>()
            : Fields.Where(f => string.Equals(f.MediaType, category, StringComparison.OrdinalIgnoreCase))
                    .ToList();

    /// <summary>A field by the name a rule refers to it by, or null.</summary>
    public static PluginField? Field(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A field by what it is called on screen, which is how a grid column and a filter refer
    /// to it. The label first, since that is what the user is looking at, and then the name,
    /// so a plugin that declared no label is still findable.
    /// </summary>
    public static PluginField? FieldByLabel(string label) =>
        Fields.FirstOrDefault(f => string.Equals(f.Label, label, StringComparison.OrdinalIgnoreCase))
        ?? Field(label);

    /// <summary>Where plugins are picked up from without anybody having to say so.</summary>
    public static string DefaultFolder => AppPaths.PluginsDir;

    // --- Loading ------------------------------------------------------------

    /// <summary>
    /// Find and load every plugin: the ones sitting in the plugins folder beside the
    /// application, and any the user has added by hand. Called at startup and whenever that
    /// list changes.
    ///
    /// Loading is never partial. The whole registry is rebuilt, so a plugin the user has just
    /// switched off stops claiming its extensions the moment they say so rather than at the
    /// next restart.
    /// </summary>
    public static void Reload(AppSettings settings)
    {
        _plugins.Clear();
        _byExtension.Clear();

        foreach (var path in Discover(settings))
        {
            if (settings.IsPluginDisabled(System.IO.Path.GetFileName(path))) continue;
            _plugins.Add(MediaPlugin.Load(path));
        }

        Register();
    }

    /// <summary>
    /// Take a set of already-loaded plugins as the registry. For the tests, and for anything
    /// that wants to try a plugin out without putting it on disk first.
    /// </summary>
    public static void Use(IEnumerable<MediaPlugin> plugins)
    {
        _plugins.Clear();
        _byExtension.Clear();
        _plugins.AddRange(plugins);
        Register();
    }

    /// <summary>Forget every plugin — what the tests leave behind them.</summary>
    public static void Clear() => Use(Array.Empty<MediaPlugin>());

    /// <summary>
    /// Work out who owns which extension, and tell the rest of the program what a file can
    /// now be asked about.
    ///
    /// Two plugins claiming one extension is settled first come, first served, and the second
    /// is told so: an .epub read two different ways is two different sets of fields on one
    /// file, and there is no version of that which is not a mess.
    /// </summary>
    private static void Register()
    {
        foreach (var plugin in _plugins.Where(p => p.IsUsable))
        {
            plugin.ClearClashes();
            foreach (var type in plugin.FileTypesClaimed)
                if (!_byExtension.TryAdd(type.Extension, plugin))
                    plugin.NoteClash(type.Extension, _byExtension[type.Extension].Name);
        }

        // Fields the built-in rules already have a name for cannot be taken: a plugin field
        // called "Size" would make File1.Size mean one thing for an e-book and another for a
        // film, and a rule that means two things is a rule nobody can read.
        var taken = new HashSet<string>(
            Consolidation.RuleScriptVocabulary.BuiltInProperties.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        var accepted = new List<PluginField>();
        foreach (var field in _plugins.Where(p => p.IsUsable).SelectMany(p => p.Fields))
            if (taken.Add(field.Name)) accepted.Add(field);

        Consolidation.RuleScriptVocabulary.UsePluginFields(accepted);
    }

    /// <summary>
    /// Every DLL worth trying, in the order they will be tried: the plugins folder beside the
    /// application first, then whatever the user has added.
    /// </summary>
    public static IReadOnlyList<string> Discover(AppSettings settings)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in Folders(settings))
        {
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(folder)) continue;
                files = Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly);
            }
            catch { continue; }

            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                if (seen.Add(System.IO.Path.GetFullPath(file)))
                    found.Add(file);
        }

        // A plugin named outright is taken even if it sits nowhere in particular.
        foreach (var file in settings.PluginFiles)
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) continue;
            if (seen.Add(System.IO.Path.GetFullPath(file))) found.Add(file);
        }

        return found;
    }

    /// <summary>The folders looked in: the built-in one, then the user's.</summary>
    public static IReadOnlyList<string> Folders(AppSettings settings)
    {
        var folders = new List<string> { DefaultFolder };
        folders.AddRange(settings.PluginFolders.Where(f => !string.IsNullOrWhiteSpace(f)));
        return folders;
    }

    // --- What a plugin makes of a file --------------------------------------

    /// <summary>
    /// Fill in what a plugin can say about a file, if one handles it. Does nothing at all for
    /// the audio and video the program handles itself, which is the overwhelming majority of
    /// every scan and must not pay for a feature it does not use.
    /// </summary>
    /// <returns>True when something was read and the entry changed.</returns>
    public static bool Enrich(MediaFile file)
    {
        if (For(file.Extension) is not { } plugin) return false;

        var values = plugin.Read(file.FullPath);
        if (values.Count == 0 && file.PluginFields.Count == 0) return false;

        file.PluginFields = values
            .Select(v => new MediaFileField { Name = v.Name, Value = v.Value })
            .ToList();
        return true;
    }

    /// <summary>
    /// The category a file belongs to on a plugin's account — the plugin's media type — or an
    /// empty string when no plugin handles it.
    /// </summary>
    public static string CategoryOf(MediaFile file) => For(file.Extension)?.MediaType ?? string.Empty;
}
