using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Persistence;

/// <summary>
/// Upgrades a loaded <see cref="Catalog"/> to the current schema version so older files
/// keep working with the current program. New optional fields are handled transparently
/// by the XML serialiser (old files simply lack them); this exists for changes that the
/// serialiser can't absorb on its own, applied in order until the catalogue is current.
/// </summary>
public static class CatalogMigrator
{
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Migrate <paramref name="catalog"/> up to <see cref="CurrentVersion"/>.
    /// Returns true if anything changed, so the caller can persist the upgrade.
    /// </summary>
    public static bool Migrate(Catalog catalog)
    {
        var changed = false;

        // Pre-versioned files (written before a Version element existed) deserialise as 0.
        // Stamp them as v1 — their fields already match the v1 shape.
        if (catalog.Version < 1)
        {
            catalog.Version = 1;
            changed = true;
        }

        // Future breaking changes go here, e.g.:
        //   while (catalog.Version < CurrentVersion)
        //   {
        //       switch (catalog.Version) { case 1: UpgradeV1ToV2(catalog); break; }
        //       catalog.Version++;
        //       changed = true;
        //   }

        return changed;
    }
}
