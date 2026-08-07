using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Relocation;

/// <summary>What to do about a file already occupying the destination name.</summary>
public enum CollisionChoice
{
    /// <summary>Leave this file alone and carry on with the rest of the batch.</summary>
    Skip = 0,
    /// <summary>Keep the file already at the destination; the incoming one does not move.</summary>
    KeepExisting,
    /// <summary>Keep the incoming file; the one at the destination makes way for it.</summary>
    KeepIncoming,
    /// <summary>Keep both — the incoming file arrives under a free name.</summary>
    KeepBoth,
    /// <summary>Abandon the whole operation.</summary>
    Cancel,

    /// <summary>
    /// Keep the copy the user picked out of the list, whichever of the two sides it belongs
    /// to — and that includes the copies sitting somewhere else entirely. Two files with one
    /// name are rarely the only two in the picture, and the best of them is quite often
    /// neither the one being moved nor the one already at the destination.
    /// </summary>
    KeepSelected
}

/// <summary>
/// A name collision put to the user: this file cannot move to that name because something
/// is already there. Carries both sides and every known copy of either, so the choice can
/// be made on the whole picture rather than on two paths.
/// </summary>
/// <param name="Incoming">The file being moved.</param>
/// <param name="DestinationPath">The path it cannot have, and what is sitting there.</param>
/// <param name="Existing">The catalogue entry for the destination file, when there is one.</param>
/// <param name="IncomingDuplicates">Other catalogued copies of the incoming file.</param>
/// <param name="ExistingDuplicates">Other catalogued copies of the destination file.</param>
/// <param name="SameContent">True when the two are byte-for-byte the same file.</param>
/// <param name="Operation">
/// What was being done, as a past participle — "moved", "consolidated" — so the dialog can
/// name the operation the user actually started rather than guessing at it.
/// </param>
public record CollisionRequest(
    MediaFile Incoming,
    string DestinationPath,
    MediaFile? Existing,
    IReadOnlyList<MediaFile> IncomingDuplicates,
    IReadOnlyList<MediaFile> ExistingDuplicates,
    bool SameContent,
    string Operation = "moved");

/// <param name="DeleteDuplicates">
/// Delete every other copy of both files once the keeper has been decided — the tidy-up
/// that makes resolving a collision worth doing rather than just getting past it.
/// </param>
/// <param name="ApplyToRemaining">Answer the rest of the batch the same way, without asking again.</param>
/// <param name="Selected">
/// The copy the user picked, for <see cref="CollisionChoice.KeepSelected"/>. It is the file
/// that ends up at the destination, whether it is one of the two in the collision or a copy
/// that was sitting somewhere else all along.
/// </param>
public record CollisionResolution(
    CollisionChoice Choice,
    bool DeleteDuplicates = false,
    bool ApplyToRemaining = false,
    MediaFile? Selected = null)
{
    public static readonly CollisionResolution Cancelled = new(CollisionChoice.Cancel);

    /// <summary>Keeping both is what the program did before anyone was asked.</summary>
    public static readonly CollisionResolution KeepBoth = new(CollisionChoice.KeepBoth);
}
