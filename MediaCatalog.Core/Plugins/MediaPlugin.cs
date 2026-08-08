using System.Reflection;
using System.Runtime.Loader;

namespace MediaCatalog.Core.Plugins;

/// <summary>
/// One plugin that has been loaded: the assembly, the object inside it, and the three
/// questions it answers.
///
/// A plugin is a .NET assembly holding a public class with three public methods, all of
/// them taking and returning strings:
///
/// <code>
/// public string Describe();            // what I am, and what I can tell you about a file
/// public string FileTypes();           // the extensions a scan should pick up for me
/// public string Read(string fullPath); // what I make of this one file
/// </code>
///
/// They are found by name rather than through an interface of ours, which is the point: a
/// plugin needs no reference to this program to be written, cannot be broken by a version of
/// it that ships later, and can be written in anything that produces a .NET assembly. The
/// XML those three strings carry is <see cref="PluginXml"/>, and it is the whole contract.
///
/// Nothing a plugin does is trusted. Every call is wrapped, a plugin that throws is set
/// aside with the reason rather than taking a scan down with it, and one that answers
/// nonsense is refused at load with a message the user can act on.
/// </summary>
public sealed class MediaPlugin
{
    private readonly object? _instance;
    private readonly MethodInfo? _describe;
    private readonly MethodInfo? _fileTypes;
    private readonly MethodInfo? _read;

    private MediaPlugin(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
    }

    private MediaPlugin(
        string path, object? instance, MethodInfo describe, MethodInfo fileTypes, MethodInfo read)
        : this(path)
    {
        _instance = instance;
        _describe = describe;
        _fileTypes = fileTypes;
        _read = read;
    }

    /// <summary>Where the DLL is.</summary>
    public string Path { get; }

    /// <summary>Just the DLL's name, which is how a plugin is identified in the settings.</summary>
    public string FileName { get; }

    /// <summary>What the plugin says about itself. Null when it would not load.</summary>
    public PluginManifest? Manifest { get; private set; }

    /// <summary>The extensions it claims. Empty when it would not load.</summary>
    public IReadOnlyList<PluginFileType> FileTypesClaimed { get; private set; } =
        Array.Empty<PluginFileType>();

    /// <summary>What is wrong with it, or an empty string when it is fine.</summary>
    public string Problem { get; private set; } = string.Empty;

    /// <summary>A plugin that never got off the ground, and the sentence that says why.</summary>
    private static MediaPlugin Broken(string path, string problem) =>
        new(path) { Problem = problem };

    private readonly List<string> _clashes = new();

    /// <summary>
    /// Extensions this plugin claimed that another had already taken. Not a fault of either
    /// one, but the user has to be told: the file is being read by the other plugin, and
    /// nothing on screen would otherwise say so.
    /// </summary>
    public IReadOnlyList<string> Clashes => _clashes;

    internal void ClearClashes() => _clashes.Clear();

    internal void NoteClash(string extension, string owner) =>
        _clashes.Add($"{extension} is already handled by {owner}");

    /// <summary>True when the plugin loaded and answered both of its opening questions.</summary>
    public bool IsUsable => Problem.Length == 0 && Manifest != null;

    /// <summary>The plugin's name, falling back on the file's when it would not say.</summary>
    public string Name => Manifest?.Name is { Length: > 0 } name ? name : FileName;

    /// <summary>The category files this plugin handles are filed under — "EBook".</summary>
    public string MediaType => Manifest?.MediaType ?? string.Empty;

    public IReadOnlyList<PluginField> Fields =>
        Manifest?.Fields ?? (IReadOnlyList<PluginField>)Array.Empty<PluginField>();

    /// <summary>How it reads in a list: "E-books 1.0 — 4 file types, 6 fields".</summary>
    public string Summarise()
    {
        if (!IsUsable) return Problem.Length > 0 ? Problem : "would not load";

        var version = Manifest!.Version is { Length: > 0 } v ? " " + v : "";
        var clash = _clashes.Count > 0 ? $" ({string.Join("; ", _clashes)})" : "";
        return $"{Name}{version} — {Count(FileTypesClaimed.Count, "file type")}, " +
               $"{Count(Fields.Count, "field")}, filed as '{MediaType}'{clash}";

        static string Count(int n, string what) => n == 1 ? $"one {what}" : $"{n} {what}s";
    }

    // --- Asking it about a file ---------------------------------------------

    /// <summary>
    /// What the plugin makes of one file, or an empty list when it would not say. A plugin
    /// that throws on a file is not a plugin that has stopped working — a malformed e-book is
    /// a thing that exists — so the failure belongs to the file, not to the plugin.
    /// </summary>
    public IReadOnlyList<(string Name, string Value)> Read(string fullPath)
    {
        if (!IsUsable || _read == null) return Array.Empty<(string, string)>();

        try
        {
            var answer = _read.Invoke(_instance, new object?[] { fullPath }) as string;
            var values = PluginXml.ParseValues(answer);

            // Only what the plugin said it would return. A field nobody declared has no
            // column, no tooltip and no place in a rule, so carrying it would be carrying
            // something that can never be seen.
            var declared = Fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            return values
                .Where(v => declared.ContainsKey(v.Name))
                .Select(v => (declared[v.Name].Name, v.Value))
                .ToList();
        }
        catch
        {
            return Array.Empty<(string, string)>();
        }
    }

    /// <summary>True when this plugin is the one that handles a given extension.</summary>
    public bool Handles(string extension) =>
        FileTypesClaimed.Any(t => string.Equals(t.Extension, extension, StringComparison.OrdinalIgnoreCase));

    // --- Loading ------------------------------------------------------------

    /// <summary>
    /// Load one DLL. Never throws: a plugin that will not load comes back saying so, because
    /// a folder of plugins where one is broken should still give the user the other four and
    /// a sentence about the fifth.
    /// </summary>
    /// <summary>
    /// One plugin per DLL, for the lifetime of the program.
    ///
    /// An assembly load context cannot be taken back, so loading the same file twice would
    /// leave the first one in memory for good — and the settings page loads every plugin it
    /// finds each time it is drawn, which would make that a leak somebody could sit on. It is
    /// also simply correct: one DLL is one plugin, and asking it what it is twice should not
    /// be able to give two answers.
    /// </summary>
    private static readonly Dictionary<string, MediaPlugin> Loaded =
        new(StringComparer.OrdinalIgnoreCase);

    public static MediaPlugin Load(string path)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) { return Broken(path, $"would not load: {ex.Message}"); }

        lock (Loaded)
        {
            if (Loaded.TryGetValue(full, out var already)) return already;

            var plugin = Read(path, full);
            Loaded[full] = plugin;
            return plugin;
        }
    }

    private static MediaPlugin Read(string path, string full)
    {
        try
        {
            return Bind(path, new PluginLoadContext(full).LoadFromAssemblyPath(full));
        }
        catch (Exception ex)
        {
            return Broken(path, $"would not load: {Innermost(ex).Message}");
        }
    }

    /// <summary>
    /// Bind an assembly that is already loaded — used by the tests, which have no business
    /// writing a DLL to disk to find out whether the contract works.
    /// </summary>
    public static MediaPlugin Bind(string path, Assembly assembly) =>
        Bind(path, Candidates(assembly));

    /// <summary>Bind against particular types, for a test that wants to name its own.</summary>
    public static MediaPlugin Bind(string path, IEnumerable<Type> candidates)
    {
        var broken = new MediaPlugin(path);

        try
        {
            foreach (var type in candidates)
            {
                var describe = Method(type, "Describe", 0);
                var fileTypes = Method(type, "FileTypes", 0);
                var read = Method(type, "Read", 1);
                if (describe == null || fileTypes == null || read == null) continue;

                var instance = describe.IsStatic ? null : Activator.CreateInstance(type);
                var plugin = new MediaPlugin(path, instance, describe, fileTypes, read);
                plugin.Interrogate();
                return plugin;
            }

            broken.Problem =
                "holds no plugin: a plugin is a public class with public Describe(), " +
                "FileTypes() and Read(path) methods, each returning a string of XML.";
            return broken;
        }
        catch (Exception ex)
        {
            broken.Problem = $"would not load: {Innermost(ex).Message}";
            return broken;
        }
    }

    /// <summary>Ask the plugin the two questions that have to be answered before it is any use.</summary>
    private void Interrogate()
    {
        try
        {
            Manifest = PluginXml.ParseManifest(_describe!.Invoke(_instance, null) as string);
        }
        catch (Exception ex)
        {
            Problem = $"its description is no good: {Innermost(ex).Message}";
            return;
        }

        try
        {
            FileTypesClaimed = PluginXml.ParseFileTypes(_fileTypes!.Invoke(_instance, null) as string);
        }
        catch (Exception ex)
        {
            Problem = $"its file types are no good: {Innermost(ex).Message}";
            return;
        }

        if (FileTypesClaimed.Count == 0)
            Problem = "it claims no file types, so nothing a scan finds would ever reach it.";
    }

    private static IEnumerable<Type> Candidates(Assembly assembly)
    {
        Type?[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }

        return types
            .Where(t => t is { IsPublic: true, IsInterface: false })
            .Select(t => t!)
            // A type that says it is the plugin is taken first, so an assembly holding
            // several classes needn't rely on the order the runtime lists them in.
            .OrderByDescending(t => t.Name.Contains("Plugin", StringComparison.OrdinalIgnoreCase));
    }

    private static MethodInfo? Method(Type type, string name, int arity) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(m =>
                string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) &&
                m.ReturnType == typeof(string) &&
                m.GetParameters().Length == arity &&
                m.GetParameters().All(p => p.ParameterType == typeof(string)));

    private static Exception Innermost(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? Innermost(inner) : ex;

    /// <summary>
    /// A load context per plugin, so a plugin that brings its own copy of some library along
    /// gets its own copy rather than whichever version happened to be loaded first.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string path) : base(isCollectible: false) =>
            _resolver = new AssemblyDependencyResolver(System.IO.Path.GetFullPath(path));

        protected override Assembly? Load(AssemblyName name)
        {
            var path = _resolver.ResolveAssemblyToPath(name);
            return path == null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
