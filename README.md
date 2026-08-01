# MediaCatalog

![platform](https://img.shields.io/badge/platform-Windows-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![license](https://img.shields.io/badge/license-MIT-green)

A Windows (.NET 8 / WPF) desktop app that spiders your attached drives, catalogues
audio and video files, finds duplicates — **including the same content re-encoded
differently, via perceptual fingerprints** — classifies video as Movie / TV / Other,
renames media to a consistent scheme, and safely relocates files. Everything persists
to XML.

> **Platform:** Windows only (WPF). Built and tested on Windows 11 with .NET 8.

## Screenshots

<!-- Run the app, then drop a PNG here and reference it:
![MediaCatalog main window](docs/screenshot.png)
-->
_Add a screenshot of the main window here._

## Undo
The last **ten** operations are reversible from the **Undo** button: moves and
consolidations (files go back where they came from), renames, title / category /
season-episode edits, and deletes that went to the Recycle Bin. Deletes that bypassed the
bin are gone for good and are not offered.

## Requirements
- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (to build) — or the
  .NET 8 Desktop Runtime (to just run a published build). A self-contained build needs
  neither.

## Build & run
```powershell
dotnet build MediaCatalog.sln
dotnet run --project MediaCatalog.App
```

## How to use
1. Tick the drives you want to scan on the left, then click **Scan**.
2. The grid fills with every audio/video file found. Use the **View** dropdown to filter
   (All / Video / Audio / Movies / TV / Duplicates / Problems).
3. Files that are exact duplicates are flagged **DUP**; the status bar shows how much
   space you could reclaim.
4. To move files: select one or more rows, click **Relocate selected…**, pick a
   destination folder. Files are **copied and hash-verified** first; the original is
   only deleted if you tick *Delete original after verify* (and only after the copy
   verifies successfully). Moving **within one volume** skips all that: the file is
   renamed in place, so a terabyte lands as fast as a byte. That decision is taken
   **before anything is read** — hashing a 20 GB file to verify a copy that is never going
   to happen costs about as much as the copy would, which is the whole wait the rename is
   there to avoid. The volume is identified by its GUID rather than by drive letter, so a
   volume mounted at two letters — or into a folder — is still recognised as one drive.

All data is written to **the folder the app runs from** (portable-app style) — the
catalogue (`catalog.xml`), tool paths (`tools.xml`), and scan state — not to
`%APPDATA%`. Copy the app folder to another machine or drive and its catalogue comes
with it. (Run it from a writable location, not read-only media.)

### Pausing & resuming long scans
Scanning terabytes can take hours, so scans are fully interruptible and survive an
application restart:
- **Pause** stops at the current file, saves everything done so far, and records the
  session. On the next launch the app offers to **Resume** from where it left off.
- **Resume across restarts without re-scanning** — the file enumeration is serialized
  to disk (`enumeration.xml`), so resuming does **not** re-walk the drives (itself slow
  on multi-TB volumes); it restores the saved file list and continues from the exact
  index. Files already hashed are skipped.
- **Crash-safe** — the scan checkpoints catalogue + position to disk every ~30 seconds,
  and a leftover session from an unexpected shutdown is offered for resume too, not just
  an explicit pause. A crash never costs more than the last half-minute of hashing.
- **Cancel** stops *and* discards the resumable session and enumeration cache.

Files that vanished from disk are only pruned from the catalogue once a scan runs to
full completion — a pause never deletes anything.

## What's implemented (Phases 1–3)
- ✅ Spider all attached drives, catalogue audio + video
- ✅ **Pause / resume scanning across application restarts** — the file enumeration is
  serialized to disk, with periodic crash-safe checkpoints; built for multi-terabyte
  libraries where a scan may run for hours
- ✅ **Portable storage** — all data lives in the app's own folder, not `%APPDATA%`
- ✅ Exact-duplicate detection (SHA-256 content hash, not just name/size)
- ✅ **Near-duplicate detection across different encodings** via perceptual fingerprints
- ✅ Movie / TV / Other classification from filenames (offline, no API key)
- ✅ Relocate files with copy-and-verify, optional delete
- ✅ Consistent metadata extraction (title / year / season / episode)
- ✅ Rename to a consistent scheme, with a preview-and-confirm dialog
- ✅ **Consolidate** TV/films into a structured library (`<dir>\<A–Z or #>\<Show>\Season NN\`),
  reusing an existing show folder, preferring the **highest-quality** copy, with a
  **suggestions** view (current → new location, collisions, duplicates)
- ✅ **Editable categories** — per-file / per-folder / parent-folder overrides, custom
  categories, each with its own consolidation folder
- ✅ **Local IMDb title data** — `title.basics.tsv` boiled down to a title/year extract that
  validates films *and* programmes with no rate limit, no API key and no network; TMDb is
  only asked what IMDb cannot answer
- ✅ **TMDb validation** of TV names — v4 Read Token *or* v3 API Key — rate-limited, cached,
  with folder-name fallback
- ✅ **Audio-only / video-only scans** that accumulate into one catalogue
- ✅ **Minimum / maximum file size** limits for scans (no limits by default)
- ✅ **Exclude folders (incl. wildcards `?:\Windows`, `*\Cache\*`) / ignore file types**
- ✅ **Column filters** — wildcard (`*`/`?`), **negation** (`not`), and **multiple stacked
  filters**; plus **hide/show columns** and horizontal scrolling
- ✅ **Watch for new files** (auto-add + taskbar notification) and **start with Windows**
- ✅ **Duplicate manager** — open any duplicated file to copy/move/delete its copies
- ✅ **Open file / open containing folder** — double-click or right-click a result
- ✅ **Remove from results** — select rows and press Delete to drop them from the view
  (the file on disk is untouched)
- ✅ **Resilient scans** — missing files never abort a scan; they're reported afterwards
- ✅ **Schema migration** — older catalogues are upgraded to the current format on load
- ✅ Integrity flags: zero-byte + in-progress downloads (`.part`/`.crdownload`), plus
  **deep corrupt-file detection** via full FFmpeg decode
- ✅ XML persistence (catalogue, tool settings, app settings, scan state, TMDb cache)

### Renaming (Phase 2)
Click **Rename…** to preview proposed names before anything is touched:
- Movies → `Title (Year).ext`
- TV → `Title - S01E02.ext`
- Audio → `Title.ext`

It uses the current selection, or the whole visible list if nothing is selected.
Each proposal has a tick box, so you can exclude any you don't want. Files without
enough metadata (or already matching the scheme) are skipped. Renames happen **in
place** (same folder) — moving between drives is what *Relocate* is for.

## External tools (needed only for fingerprinting + deep checks)
The core features (cataloguing, exact duplicates, classification, rename, relocate)
work with **no extra installs**. The advanced content-analysis features shell out to
two free, portable tools:

| Tool | Provides | Download |
|------|----------|----------|
| **FFmpeg** | `ffmpeg.exe` + `ffprobe.exe` — video fingerprints & deep corrupt checks | ffmpeg.org (gyan.dev Windows builds) |
| **Chromaprint** | `fpcalc.exe` — audio fingerprints | acoustid.org/chromaprint |

**Easiest setup (recommended):** create a folder named `tools` next to
`MediaCatalog.App.exe` and drop the three `.exe` files into it. They're detected
automatically — no PATH editing, no admin rights.

The app also auto-detects them on the system PATH and in common install folders
(winget, chocolatey, `C:\ffmpeg\bin`, …). Or click **Tools…** in the app to point at
them manually; the status bar shows `ffmpeg ✓ ffprobe ✓ fpcalc ✓` for what it found.

### Using the analysis features
- **Fingerprint / analyse** — computes perceptual fingerprints for audio/video, then
  flags near-duplicates (same content, different encoding) with a `~dup` tag. Use the
  **Near-duplicates** view to review them. Already-fingerprinted files are skipped.
- **Deep integrity check** — fully decodes files with FFmpeg to catch corruption/
  truncation that the quick scan can't. Thorough but slow — select suspect files first.

> Near-duplicate video matching is perceptual and therefore **fuzzy** — treat matches
> as strong candidates to confirm, not absolute proof.

## Library management

**Categories** — right-click files in the grid → *Set category* (built-in or your own),
*Set category for this folder…* (applies to a whole folder, optionally its subfolders),
or *Add new category…*. Overrides win over the auto-detected category, and setting one on a
file sets it on **every exact duplicate of that file** too, so the same content is never
filed two different ways. A season/episode code beats the extension: anything that says
`S02E05` — whatever it is called or however it is packaged — is categorised as **TvShow**.

> **Everything a file knows lives in the catalogue.** Setting a category or title on a
> folder writes it onto each of that folder's files rather than leaving a rule behind in
> the settings. Rules saved by earlier versions are migrated the same way by **Refresh
> catalogue**, and each one is retired as soon as its files have been labelled outright.
> (A rule whose folder has not been scanned yet is kept, since dropping it would lose the
> instruction.) Folder rules are on their way out and may go entirely in a later release.

**Titles** — right-click → *Edit title…* to correct a title by hand; the box starts from
the current title, or from the file name without its extension when there isn't one. The
correction is applied to the selected file, **to every other file that had the same
title**, and **to its exact duplicates** — so one edit fixes a whole show, and a copy that
never had a title gets one too. *Set title for this folder…* names a whole folder (or a
parent) at once: the rule sticks, so files scanned into it later inherit the title, and a
rule on a season folder beats one on the show above it. TMDb validation does the same with the name it confirms. A
hand-typed title counts as validated (shown as ✎ in the TMDb column; ✓ means TMDb).

**Extras** — specials, featurettes, deleted scenes and behind-the-scenes material are
detected (by folder name, Plex/Kodi `-featurette` suffixes, or a season-zero code) and
categorised as **TvExtra** / **MovieExtra**. Each extra is *linked* to the film or episode
it belongs to: it adopts that title and travels with it whenever the main file is
consolidated.

**Consolidate** — select TV/film files and click **Consolidate…** to move (or copy) them
into a tidy library:
- TV → `<TV dir>\<A–Z or #>\<Show>\Season NN\NN - name.ext`
- Films → `<Film dir>\<A–Z or #>\<Title (Year)>\`
- Extras → the same show/film folder, under `\Extras\`

Seasons are left-padded to ≥2 digits, and episodes are **prefixed with their episode
number** so a season folder sorts into broadcast order in any file manager. Files that are
already in the consolidation location are **never copied twice**: the redundant source is
reported instead, and you are offered the chance to delete it. A file with **no title** has
nowhere to be filed, so you are asked for one before the move rather than having it quietly
skipped. Target folders are set per category in **Settings…** — any number of categories,
each with its own folder. Uses the same copy-and-verify as Relocate, with a progress bar and
an ETA.

**Suggest consolidation** — click **Suggest consolidation…** to scan the catalogue and get a
reviewable list of proposed moves: current location → new location, with name-collision and
duplicate flags. When several copies of the same film/episode exist, the **highest-quality**
one (2160 > 1080 > 720 > 480) is preferred; TV items must have a TMDb-validated title and a
season/episode to be recommended. Items already sitting in the consolidation location are
flagged as such rather than proposed for another copy. Tick the ones to apply.

**Filtering** — the filter bar matches any column with wildcards (`*` = any run, `?` = one
char; plain text = contains). Tick **not** to exclude matches (e.g. *Category not Audio*),
and **Add filter** to stack several filters at once. The grid scrolls horizontally.

The **view, the filter box and every stacked filter are remembered**, written out as they
change rather than only at exit, so they come back whatever happened to the app. Turn it
off with *Remember the view and filters* in Settings.

**Columns** — click a header to sort (**Size** sorts by actual file size, not by its
printed text). Right-click a header to *set its width in pixels*, fit it to its contents,
hide it, or open the column chooser. Widths and visibility are remembered between runs.
**Every header has a tooltip** explaining what the column means and what its values stand
for — hover over *Dup*, *TMDb* or *Integrity* to see what each flag is telling you.

**Settings** — the settings window is **non-modal**: it can stay open while you keep
scanning, filtering and working in the main window. *Save* applies the changes immediately
and closes it.

**Watching & startup** — in **Settings…**, enable *Watch for new files* to have new media
auto-added to the catalogue with a taskbar notification, and *Start with Windows* to launch
at sign-in. You can tick **exactly which drives to watch** — useful when only one or two of
the scanned drives ever gain new files. Leaving them all unticked watches everything that
was scanned.

Started at sign-in, the app comes up **in the notification area with no window**, ready to
catch new files without getting in the way; double-click the tray icon (or *Open Media
Catalog* on its menu) to bring it up, and *Exit* to quit properly. While watching is on,
closing the window hides it back to the tray rather than quitting.

Two more window options in **Settings…**:
- *Always start minimised to the notification area* — a quiet start however the app was
  launched, not only when Windows started it;
- *Minimising sends the window to the notification area instead of the taskbar* — the
  minimise button puts it in the tray, with a one-off notification the first time so it
  doesn't just vanish.

**Scanning scope** — the toolbar dropdown beside **Scan** chooses between *All*,
*VideoOnly* and *AudioOnly*. A filtered scan **never prunes the kind it wasn't looking
for**, so an audio scan followed by a video scan (or the other way round) builds a single
combined catalogue rather than each one wiping the other's results.

**Size limits** — *Settings… → Scanning* can leave out files below a minimum or above a
maximum size. Write bytes or a size like `50MB`, `1.5 GB`, `700 KB`; leave either box empty
for no limit, which is the default for both. Changing the limits and re-scanning both drops
what now falls outside them and picks up what now falls inside.

**Progress text** — hashing thousands of small files a second makes a trailing file name
flicker the whole status line about, and the counter never lands twice in the same place.
*File name* in Settings puts it on the **Left** (the default) so `Hashing & classifying:
5/1000` holds still on the right, or **Hidden** to leave it out entirely.

**Files that could not be hashed** — a file that is locked, unreadable or refused gets no
hash, and without one it is invisible to duplicate detection. Rather than let that pass
quietly, the scan collects them and puts the list up when it finishes, offering to **read
them again**, **deep check** them to find out whether they are actually damaged, or
**delete** them. Select rows to act on some, or leave the selection empty to act on all.
The **Unhashed files…** button keeps the list reachable afterwards.

**Files still downloading** — the watcher can spot a file long before it has finished
arriving, so a newly seen file is hashed only once its size has stopped changing and it can
be opened for reading. Anything that never settled is flagged, and **Re-hash pending**
refreshes size and hash for all of them in one go — duplicate detection depends on it.

**Scan folder…** — add a single folder (a downloads folder, say) to an existing catalogue
without re-walking whole drives. Nothing outside it is touched or pruned, and the folder is
remembered: it is watched along with the drives and listed in Settings.

**Filed** — the grid's *Filed* column ticks once a file lives in its consolidation
location, and the **View** dropdown can show just what is *Consolidated* or *Not
consolidated*, so it is easy to see what is left to sort out.

**Progress and ETA** — long jobs (consolidating, moving, deep checking, re-hashing) show a
progress bar with an estimated time remaining in the status bar. For copies and deep checks
the estimate is driven by **bytes**, not file count: a 20 GB remux and a 200 MB episode are
not the same job.

**Excluding** — right-click → *Exclude this folder…* (optionally including subfolders) or
*Ignore this file type* to drop files from results and skip them in future scans. Manage
these lists in **Settings…**, where a rule can be either a real folder or a **pattern**:
`*\Windows\*` excludes every Windows folder on every drive, `?:\$Recycle.Bin` the bin on
all of them. A pattern also prunes the scan, so excluded trees are never walked. A plain
path that doesn't exist (and has no wildcard) is confirmed before it is added, since that
is usually a typo. **Exclude system directories** — on by default — covers Windows,
Program Files, ProgramData, `$Recycle.Bin`, System Volume Information and friends.

**Deleting files** — right-click → *Delete file(s) from disk…*. Files go to the **Recycle
Bin** by default. Bypassing the bin is offered as a tick, and it arms a second confirmation
checkbox that must be ticked **every time** before the delete button becomes available.
(Plain **Delete** on the keyboard still only removes rows from the results and leaves the
files alone.)

A refusal is not taken at face value:
- a **read-only** file has the attribute cleared and the delete retried;
- a file **held open by another application** is reported with *which* applications are
  holding it (via the Restart Manager, the same mechanism installers use), so you know
  what to close;
- a **permissions** refusal is offered a retry with administrative rights, which relaunches
  the program elevated for that job alone;
- anything still refusing is explained in full — file, reason, holders and path.

**Moving files** — right-click → *Move to folder…* picks a destination, and can take the
**rest of the containing folder** along with the selection, which is what you usually want
for a download folder holding a film plus its subtitles and extras.

**Renaming, season/episode** — right-click → *Rename file…* renames on disk (and re-derives
what the name implies), and *Edit season / episode…* sets or clears the numbering by hand.
Both apply to duplicates of the same content as well.

Episode numbering is read from any of the ways people write it — `S01E02`, `s01.e02`,
`1x02`, `S04 E 01`, `Season 1 Episode 01`, `Series 1 Episode 1`, `S1 Episode 1`,
`Season 2 Ep 3` — while still leaving `Cars 3 (2017)` alone. Compact codes are read too:
`123` is season 1 episode 23, and a four-digit `1102` is **season 11 episode 2** rather
than episode 102 of season 1, because shows reach an eleventh season far more often than a
hundred-and-second episode. Resolutions (`720`, `1080`, `2160`, …) are never mistaken for
episode codes.

**The path counts as metadata.** A well-filed library already says everything needed, so
`T:\TV\K\King Of The Hill\Season 04\1.avi` is read as *King Of The Hill*, **S04E01** — the
show name from the folder above the season folder (skipping one-letter A–Z buckets), the
season from `Season 04`, the episode from the file name.

**The file name wins.** A `Season NN` folder only ever *fills a gap* the name left; it never
overrules it. A name carrying no season of its own — `1.avi`, `12.avi`, `E07.mkv` — takes
the folder's season, but anything that states a season keeps it, so `1102.avi` inside
`Season 04` is **S11E02**, not S04E02. The file was named deliberately; the folder it
happens to be sitting in may just be where someone dropped it. (Three- and four-digit names
are compact codes and state a season: `104.avi` is S01E04 wherever it sits.)

Whatever any copy of a file works out is shared with its duplicates, whether it came from a
scan, a catalogue refresh or an edit by hand.

**Refresh catalogue** — re-derives what can be re-derived from data already in the
catalogue, without re-scanning or re-hashing anything. It covers three things:

- entries that predate the current feature set — new categories, extras linking, better
  title parsing;
- **programmes with no season/episode yet**, which are re-parsed *every* time with the
  current rules, so a release that learns a new naming convention picks them up without a
  drive scan;
- **titles nothing has confirmed**, which are looked up in the local IMDb data and then, only
  for what that cannot answer, TMDb — filling in missing years along the way.

It also **writes any leftover folder rules onto the files themselves** and retires the rule
once it has (see below). The status bar says on startup when there is anything waiting.

**Opening files** — double-click a result to open it with its associated application, or
right-click → *Open file* / *Open containing folder* (Explorer opens with the file selected).

**Remove from results** — select one or more rows and press **Delete** (or right-click →
*Remove from results*) to drop them from the view. This only removes them from the
catalogue/results; the actual files are left on disk (a later scan re-adds them unless the
folder or type is excluded).

**Duplicates** — matched purely on the **SHA-256 of the contents**, so two copies are one
duplicate set however differently they are named. A file that is renamed or moved is
recognised as the same file (same size and timestamp) and keeps its hash, title and
season/episode rather than being re-read as a stranger — and whatever any copy knows is
shared with the others, so naming one episode names its twin. Files that have no hash yet
can't be compared at all, so the status bar says how many there are and **Re-hash pending**
fixes them.

Right-click → *Show duplicates* opens a manager listing every identical
copy of a file, with copy / move / **consolidate** / delete. *Consolidate selected* files
the chosen copies straight into the library, asking for a title first if they have none.

**Deep check a folder** — **Deep check folder…** decodes every media file under a folder
and its subfolders, whether or not they are catalogued (new ones are added so their verdict
is kept). Useful for vetting a freshly copied drive in one pass.

### Backwards compatibility
Catalogues from earlier versions load unchanged. New fields are optional, and anything the
serialiser can't absorb is handled by a schema migration that upgrades the file on load and
saves it back once — so an old library keeps working with the current program.

**Missing files** — if files vanish between enumeration and scanning, the scan keeps going;
ones under a `Temp` folder are ignored, the rest are listed via the **Missing files…**
button afterwards.

### IMDb title data (local, free, no rate limit)
IMDb publish their catalogue as a gzipped TSV. Download
[`title.basics.tsv.gz`](https://datasets.imdbws.com/title.basics.tsv.gz) and drop it in the
program folder — gzipped or unpacked, either is read as-is.

The first time titles are verified, the file is boiled down to **`IMDBData.tsv`**, keeping
only the two columns that matter: **primary title** and **year**. The source is over a
gigabyte, so it is streamed a line at a time and never loaded into memory. IMDb's
placeholder rows for untitled episodes — `Episode #1.4`, `Episode dated 3 May 1999`,
`Episode 12` — are dropped, since they would only ever match by accident. If `IMDBData.tsv`
is already there the raw file is left alone.

**Verify titles** then confirms film and programme names against it and fills in any
**missing years**. There is no rate limit and no network involved, so the whole catalogue is
answered in a single pass; **TMDb is only asked about what IMDb could not identify**, which
matters when TMDb allows one query every two seconds. Titles are matched ignoring case,
punctuation and spacing, so `King Of The Hill` finds *King of the Hill*; where a name has
been used more than once, the earliest year wins.

By default the extract is **held in memory** for fast lookups (a few hundred megabytes).
Turn *Keep the IMDb data in memory* off in **Settings…** and it is read from disk instead —
slower, but free. Even then a whole run is answered in one pass over the file, not one pass
per title.

### TMDb (themoviedb.org) TV validation
Enter a free TMDb **v4 Read Access Token** *or* **v3 API Key** in **Settings…** (the token is
preferred if both are given), then **Validate TV (TMDb)** confirms show names against TMDb. Lookups are **rate-limited to one every two seconds** and **cached**
(`tmdb-cache.xml`) so names are never queried twice. If the episode title doesn't match,
the containing folder names are tried in turn (e.g. `…\Bewitched\Season 01\ep.avi` falls
back to "Bewitched"). **Every** folder up to the drive root is tried, each one also without
its trailing decoration — `Yes Minister (1980)` is offered as `Yes Minister` too — with
season folders and single-letter buckets kept for last rather than skipped, since a show
really can be called *Ed*. Validated titles show a ✓ in the TMDb column (✎ marks one you
typed yourself). A confirmed name is also **shared with every file that had the same
title**, so one lookup fixes — and spares a query for — the rest of the show.

## Versioning
The build carries a Windows **file version of `0.0.<major>.<minor>`** — `0.0.1.9` for
v1.9 — with the product version kept as the number people talk about (`1.9`). Major and
minor stay at `0`; the release rides in the build and revision fields.

Both numbers are set in one place, [`Directory.Build.props`](Directory.Build.props), and
every project in the solution picks them up — bump them there once per release. **About**
in the toolbar shows both, next to the program icon.

## Roadmap / possible extensions
- Acting on near-duplicate groups directly (keep-best / bulk delete) from the UI.
- Fetching `title.basics.tsv.gz` from within the app rather than having it dropped in the
  program folder by hand.
- Retiring folder rules altogether, once existing catalogues have migrated off them.

## Project layout
- `MediaCatalog.Core` — engine (scanning, hashing, classification, duplicates,
  relocation, fingerprinting, integrity, XML persistence). No UI dependency, so the
  logic is unit-testable in isolation.
- `MediaCatalog.App` — WPF front end (MVVM, no external packages).

## How it works (the interesting bits)
- **Exact duplicates** — streamed SHA-256 content hashes, grouped.
- **Audio near-duplicates** — [Chromaprint](https://acoustid.org/chromaprint)
  acoustic fingerprints (`fpcalc`), compared by bit-error-rate over the aligned prefix.
- **Video near-duplicates** — 16 frames sampled across the file via FFmpeg, each reduced
  to a 9×8 grayscale dHash; signatures compared by average per-frame Hamming distance.
  Matches are clustered with union-find. This is perceptual, so results are *candidates*.
- **Corrupt detection** — quick header/duration probe with ffprobe, plus an optional
  full-decode pass with FFmpeg for thorough truncation/corruption checks.

## Third-party tools & attribution
This app calls out to two external tools for the advanced features; it does **not**
bundle or redistribute them (the user supplies them), so this project stays MIT-licensed
without inheriting their licenses:
- **[FFmpeg](https://ffmpeg.org/)** — `ffmpeg` / `ffprobe`, licensed under LGPL/GPL.
- **[Chromaprint / AcoustID](https://acoustid.org/chromaprint)** — `fpcalc`, LGPL.

## License
[MIT](LICENSE) © 2026 Samuele Voltan.

## Credits
From an idea by **Phreak** — original concept and feature brief.
Implementation and architecture built out from that brief.
