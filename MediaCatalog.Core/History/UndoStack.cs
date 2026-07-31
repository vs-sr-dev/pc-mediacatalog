namespace MediaCatalog.Core.History;

/// <summary>One reversible operation: what it was, and how to put it back.</summary>
public sealed class UndoEntry
{
    public required string Description { get; init; }

    /// <summary>Performs the reversal and returns a message describing what happened.</summary>
    public required Func<Task<string>> UndoAsync { get; init; }

    public DateTime WhenUtc { get; } = DateTime.UtcNow;
}

/// <summary>
/// The last few operations, most recent first, each with the means to reverse it. Bounded
/// so it can never grow without limit — undoing something from twenty operations ago is
/// rarely what anyone wants, and holding the state to do so is not free.
/// </summary>
public sealed class UndoStack
{
    private readonly List<UndoEntry> _entries = new();

    public UndoStack(int capacity = 10) => Capacity = capacity;

    public int Capacity { get; }

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<UndoEntry> Entries => _entries;

    public bool CanUndo => _entries.Count > 0;

    /// <summary>What undoing now would reverse, for a menu label.</summary>
    public string? NextDescription => _entries.Count > 0 ? _entries[0].Description : null;

    public event Action? Changed;

    public void Push(string description, Func<Task<string>> undo)
    {
        _entries.Insert(0, new UndoEntry { Description = description, UndoAsync = undo });
        while (_entries.Count > Capacity) _entries.RemoveAt(_entries.Count - 1);
        Changed?.Invoke();
    }

    /// <summary>
    /// Reverse the most recent operation. The entry is dropped whatever the outcome: a
    /// reversal that half-worked must not be retried blindly on top of itself.
    /// </summary>
    public async Task<string> UndoAsync()
    {
        if (_entries.Count == 0) return "Nothing to undo.";

        var entry = _entries[0];
        _entries.RemoveAt(0);
        Changed?.Invoke();

        try { return await entry.UndoAsync(); }
        catch (Exception ex) { return $"Could not undo {entry.Description}: {ex.Message}"; }
    }

    public void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        Changed?.Invoke();
    }
}
