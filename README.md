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
1. Click **Scan…** and work through the wizard: what to do with the existing catalogue,
   which drives and folders to walk, what to pick up. On a first run it opens by itself,
   since an empty catalogue is the one state the app can't do anything useful with.
2. The grid fills with every audio/video file found. Use the **View** dropdown to filter
   (All / Video / Audio / Movies / TV / Duplicates / Problems).
3. Files that are exact duplicates are flagged **DUP**; the status bar shows how much
   space you could reclaim.
4. To move files: select one or more rows, click **Relocate…**, pick a
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

### The scan wizard
Choosing what to scan is a decision made now and then, so it lives in a wizard rather than
in a panel occupying a third of the window at all times:

1. **What should this scan do?** — add to the existing catalogue, or start again from
   nothing. Starting over is the honest choice after changing the size limits or the media
   filter, since the old catalogue was built under the old rules. It discards typed titles,
   set categories and computed fingerprints, so it asks twice.
2. **Where should it look?** — drives and extra folders. A folder already sitting on a
   ticked drive is covered by it and skipped rather than walked twice.
3. **What should it pick up?** — audio/video filter and the minimum/maximum file size.
4. **Ready** — a summary of the lot, plus what to do about any drive that isn't connected.

A scan only prunes entries **within the roots it actually walked**. Scanning `C:` says
nothing about what is on `D:`, and a drive that never turned up says nothing at all — so
"add to the existing catalogue" really does add rather than quietly replace.

### Drives that aren't plugged in
An external drive that is part of the library but not currently attached is treated as
**unknown, not empty**. Nothing catalogued on it is touched.

When you **Resume** an interrupted scan and one of its drives is missing, you're told, and
offered three ways out — cancel (the default, so you can go and plug it in), carry on
without it, or carry on **and wait**: the scan finishes everything it can reach, then
watches for the drive and scans it the moment it appears. Cancel stops the wait at any
point. The same choice is offered up front in the wizard.

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

## New in v2.1
- ✅ **Seasons written in words** — `Season Three` reads as season 03, `Series twenty one`
  as 21, and a folder that names the show *and* the season together — `Yes Minister,
  Season Three` — gives up both halves
- ✅ **An episode number leading a file name** — `01. Equal Opportunities.avi` under that
  folder is S03E01 of *Yes Minister*: the season from the path, the episode from the name,
  and the title from the folder, because what follows the number is the *episode's* name
- ✅ **Release years that have not happened yet are passed over** —
  `Blade.Runner.2049.2017.1080p.mkv` is a 2017 film, not a 2049 one
- ✅ **Titles capitalised** — every word gets its initial capital, so `the.matrix.1999.mkv`
  reads *The Matrix*. Words that already carry capitals of their own keep them
- ✅ **Length and Quality columns** — how long each file runs, and the one number that means
  something for its kind: picture height for video, bitrate for audio. Read during a scan
  from the container header, or on demand with *Verify* on the right-click menu
- ✅ **Possible duplicates** — the same film downloaded twice from two different releases:
  identical in what they claim to be, different in every byte, and therefore invisible to a
  content hash. Found by title, year and numbering; deep-checked and resolved side by side
- ✅ **Purge filed duplicates** — clear out every stray copy of everything already in the
  library in one pass, to the Recycle Bin or permanently
- ✅ **Skip the Recycle Bin, wherever you delete** — every delete now goes through the one
  confirmation, which lists every file and offers the choice. A setting arms it by default,
  with a frank warning about why that is a bad idea
- ✅ **Empty folders offered for removal** after the last file in them is deleted or moved,
  along with any parent they empty in turn
- ✅ **Misfiled folders renamed rather than copied out** — a show folder spelled wrongly is
  put right by moving the folder, which costs nothing whatever it holds and leaves nothing
  standing behind it
- ✅ **Season/episode stripped from anything that is not a programme** — categorise a file as
  a film and the numbering goes, because it came from a number that meant something else
- ✅ **Sort "The Simpsons" under S** as `…\S\Simpsons (The)\` — off by default
- ✅ **Pick which redundant exclusion rules to drop**, with Select all / Select none, instead
  of all-or-nothing
- ✅ **Consolidation folders checked as they are set** — a drive that isn't there is refused,
  a folder on a drive that is there is created
- ✅ **Double-click does what you want** — play the file, or open Edit details
- ✅ **TMDb is only consulted when `IMDBData.tsv` does not exist**, and is deprecated
- ✅ **Settings grouped into boxes**, with the two data sources separated
- ✅ **Three dialogs retired** — Edit title, Edit season/episode and Rename file all did less
  than Edit details, which now does the renaming they were kept for

## What's implemented (Phases 1–3)
- ✅ **Scan wizard** — start fresh or add to the catalogue, pick drives and folders, set the
  media filter and size limits, and decide what to do about drives that aren't connected
- ✅ **Tabbed settings**, ordered by how often each thing changes — General first, API keys
  last — with the external-tool paths folded in as a tab of their own
- ✅ **Name-collision dialog on moves and consolidation** — both files side by side with every
  known copy of either, sizes, dates and integrity, a deep check on demand, rename/replace/
  keep-both/skip, and the option to delete all the duplicates of both once you've decided
- ✅ **Titles travel by content hash**, never by matching title — two files called *xyz* may
  be two different things, and one of them may already be right
- ✅ **Films identified from their folder** when the file name cannot do it, against IMDb and
  now TMDb's film index too
- ✅ **Re-consolidation after a title correction** — a file filed under the wrong title stops
  counting as filed and is moved to where the corrected title puts it
- ✅ **"Already consolidated"** rather than a second copy — with the option to delete every
  other copy, or to nominate a different copy as the keeper and have it filed instead
- ✅ **Edit every field** of an entry, the modified date included — written to the file on
  disk too, so the next scan doesn't read the old one back
- ✅ **Rename on title change** — correcting a title renames the file to match the scheme
- ✅ **One delete path** — read-only files have the attribute cleared before the attempt,
  everywhere that deletes
- ✅ **Redundant exclusion rules** are spotted and offered for removal — ask, remove
  automatically, or leave alone
- ✅ **IMDb data downloaded from within the app**, from an address you can correct
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
(winget, chocolatey, `C:\ffmpeg\bin`, …). Or open **Settings… → External tools** to point
at them manually; that tab shows what was resolved, and the status bar shows
`ffmpeg ✓ ffprobe ✓ fpcalc ✓` for what it found.

### Using the analysis features
- **Fingerprint / analyse** — computes perceptual fingerprints for audio/video, then
  flags near-duplicates (same content, different encoding) with a `~dup` tag. Use the
  **Near-duplicates** view to review them. Already-fingerprinted files are skipped.
- **Deep integrity check** — fully decodes files with FFmpeg to catch corruption/
  truncation that the quick scan can't. Thorough but slow — select suspect files first.
- **Verify** (right-click) — reads a file's **length and quality** from its container
  header. A moment per file rather than the minutes a deep check takes, because it reads
  the header rather than the file.

> Near-duplicate video matching is perceptual and therefore **fuzzy** — treat matches
> as strong candidates to confirm, not absolute proof.

### Length and Quality
Two columns describing what the file actually is, rather than what its name claims:

- **Length** — how long it runs, as `h:mm:ss`, sorted by real duration.
- **Quality** — the one number that means something for its kind: **picture height** for
  video (`720p`, `1080p`, `2160p`) and **bitrate** for audio (`320 kbps`).

Both are filled in **during a scan**, from the container header — which costs a fraction of
what hashing the same file costs, and is skipped entirely for entries that already know, so
re-scanning a measured library costs nothing. They need `ffprobe`; without it the columns
stay blank and nothing else changes. Turn scan-time reading off with *Read each file's
length and quality during a scan* on **Settings… → Scanning**, and fill individual files in
on demand with **Verify** on the right-click menu.

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

**Edit details** — right-click → *Edit details…* opens every field of an entry at once:
title, year, season, episode, category, file name, **modified date**, integrity and kind.
What the content *is* (title, year, numbering, category) is written to every byte-identical
copy, because they are the same thing; what the *file* is (its name, its date, what a decode
made of it) belongs to that one file. A corrected date is written to the file on disk as
well as to the catalogue — left in the catalogue alone, the next scan would read the old one
straight back and treat the file as changed.

> This is now the **only** per-file editor. *Edit title*, *Edit season / episode* and
> *Rename file* each did a fraction of what it does and have been retired; correcting a
> title here renames the file exactly as *Edit title* used to, and clearing a category's
> numbering is handled by the category rather than by hand.

**Titles** — correct a title in *Edit details…*; the box starts from the current title, or
from the file name without its extension when there isn't one. The correction is applied to
the selected file and **to its byte-identical copies** — so a copy that never had a title
gets one too — and to nothing else. *Set title for this folder…* names a whole folder (or a
parent) at once, again reaching the copies of those files wherever they live. A hand-typed
title counts as validated (shown as ✎ in the TMDb column; ✓ means confirmed by IMDb or TMDb).

Titles worked out from a file name get **an initial capital on every word**, so
`the.matrix.1999.mkv` reads *The Matrix*. Only the first letter is touched: a word that
already carries capitals of its own — *MASH*, *iCarly* — keeps them, because there is no way
to tell a deliberate capital from an accidental one. Turn it off with *Capitalise the first
letter of every word in a title* on **Settings… → General**. Titles confirmed against IMDb,
or typed by you, are left exactly as they were spelled either way.

**Season and episode numbers belong to television.** Categorise a file as anything but
*TvShow* or *TvExtra* and its numbering is cleared: it was read out of a number in the name
that meant something else — the 13 in *Apollo 13*, a track numbered 104 — and keeping it
would only file the thing wrongly. Nothing is lost that was ever right.

> **Titles travel by hash, never by matching title.** Two files can both be called *xyz* and
> still be two different things — and one of them may already be correct. Sharing a name is
> no evidence of sharing an identity; sharing a content hash is. (The one title-based spread
> that remains is verification replacing a guessed spelling with the canonical spelling of
> *the same* name — `king of the hill` becoming *King of the Hill* — which is a correction
> every file carrying that guess wants.)

Correcting a title also **renames the file on disk** to match the scheme for its category —
`Show - S01E02.mkv`, `Title (Year).mkv` — because a corrected title that leaves the old name
in place is only half a correction, and the old name is exactly what the next scan would
read the title back out of. Turn it off with *Rename files on disk when their title changes*
on **Settings… → General**. Extras keep the names they were given: the naming scheme has
nothing better to say about "Behind the scenes" than its own name does.

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
number** so a season folder sorts into broadcast order in any file manager. A file with
**no title** has nowhere to be filed, so you are asked for one before the move rather than
having it quietly skipped. Target folders are set per category in **Settings… → Library** —
any number of categories, each with its own folder. Uses the same copy-and-verify as
Relocate, with a progress bar and an ETA.

**Filed means "in the right place", not "somewhere in the library".** A file counts as
consolidated only when it sits at the exact path its category, title, year and numbering
give it. That distinction is what makes a corrected title work properly: a file filed as
*Burn Notce* stops being filed the moment the title is fixed, and consolidating it again
**moves it under the corrected name** instead of announcing that it is already in the
library.

**A misnamed folder is renamed, not copied out of.** A file already inside the library but
under a wrongly spelled show folder does not want copying anywhere — it wants its folder put
right. When every catalogued file in a folder agrees on where it should go, the *folder* is
moved or renamed: one operation, no matter what it holds, and nothing left standing behind
it. Only when the folder disagrees with itself, or something already occupies the
destination, does it fall back on relocating the files one at a time. Folders emptied on the
way out are offered for removal afterwards, parents included.

**Already consolidated** — a file that really is exactly where it belongs is never copied
onto itself. If no other copy of it exists you are simply told so. If copies do exist, this
is the natural moment to deal with them: **delete all duplicates**, or **pick which copy to
keep** — and if the one you pick isn't the library copy, the others go and the keeper is
moved into the library in its place. **Deep check** decodes the copies first: they are the
same bytes, but not necessarily on the same quality of disk, and "which of these still
reads" is exactly the question that decides which to keep. Tick *do the same for the rest*
and the questions stop there — every remaining group's copies are gathered into a **single
delete confirmation** that lists all of them at once.

**Purge filed duplicates** — the same tidy-up over the whole catalogue in one pass. Every
file that is correctly filed in the library keeps its library copy; every stray copy of it
anywhere else is listed for deletion, to the Recycle Bin or permanently. Sets with no filed
copy at all are left alone: choosing between scattered copies is a decision, not a tidy-up.

**Possible duplicates** — the duplicates a hash cannot find. The same film downloaded twice
from two different releases is identical in what it *is* and different in every byte, so it
never groups by content. These are found by title, year and — for a programme — season and
episode, and put side by side with their size, length, quality and integrity so the choice
of which to keep is an informed one. A **deep check** decodes them first, so a damaged copy
is not the one that survives. Files carrying a `title` flag in the **Dup** column are these;
the **SameTitle** view lists them.

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

The tabs run **from what changes often to what changes once**, which is why they are in this
order: *General* (startup, watching, behaviour) → *Scanning* → *Library* → *Exclusions* →
*Categories* → *External tools* → *Data sources*. An API key is typed in on the day it is
obtained and rarely looked at again, so it sits at the far end; which folders to watch is
revisited regularly, so it comes first. The external-tool paths, which used to be a separate
*Tools…* dialog on the toolbar, are a tab here now — the main window is for the catalogue,
not for configuration.

**Moving or consolidating into a name that is taken** — when the destination name already
exists,
you are shown **both files side by side**, together with **every catalogued copy of either**:
sizes, dates and integrity, so there is something to decide on. A **deep check** can be run
on any of them from that dialog — whether a file actually decodes is often exactly the fact
that settles which of two identically named copies is worth keeping. Then choose to keep the
arriving file, keep the one already there, keep both (the arrival is renamed to a free name),
skip it, or cancel the batch. Tick *Delete every other copy of both files* and the losers and
all their copies go to the Recycle Bin once the choice is made; tick *Answer the rest the same
way* and you're only asked once.

**Consolidation asks the same question.** Filing into the library used to give up on a
clash and report a failure; now it offers exactly the same choice — rename, replace, keep
both or skip. A copy of the *same* file already sitting at the destination is not treated as
a clash: that is the "already in the library" case, which offers to delete the redundant
source instead.

**Deleting** — every delete in the program goes through **one** implementation, so the
results grid, the duplicate manager, the unhashed-files list and a verified move discarding
its original all behave identically and gain the same fixes. **Read-only files have the
attribute cleared before the delete is attempted** (and again if it still refuses), which is
what used to make deleting from the duplicate manager fail. Files go to the Recycle Bin by
default; skipping it demands a separate confirmation tick. Anything refused for permission
reasons can be retried with administrative rights, and anything held open reports **which
application is holding it**.

Because there is one implementation, there is one confirmation: **every** delete — the
grid, both duplicate managers, *keep this one and delete the rest*, the already-consolidated
tidy-up, the purge — now lists exactly what is about to go and offers the same choice
between the Recycle Bin and a permanent delete. Nowhere silently picks one for you.

**Redundant exclusion rules** — excluding `D:\Media` makes an existing rule for
`D:\Media\Films` pointless. When a new rule covers older ones you are shown what has been
superseded and **tick which of them to drop** — all, some or none, with *Select all* and
*Select none* for when the answer really is all or nothing. "All or nothing" was never the
right question: a broad rule can supersede a dozen narrow ones, and wanting ten gone and two
kept is perfectly reasonable. *Settings… → Exclusions* can change the whole thing to removing
them automatically without stopping, or to leaving them alone. Patterns count too —
`*\Windows\*` supersedes `C:\Windows` — and *Find redundant rules* sweeps the whole list at
once, including anything the built-in system-folder list already covers.

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

**Scanning scope** — the scan wizard (and *Settings… → Scanning*) chooses between *All*,
*VideoOnly* and *AudioOnly*. A filtered scan **never prunes the kind it wasn't looking
for**, so an audio scan followed by a video scan (or the other way round) builds a single
combined catalogue rather than each one wiping the other's results.

**Size limits** — the wizard and *Settings… → Scanning* can leave out files below a minimum
or above a maximum size. Write bytes or a size like `50MB`, `1.5 GB`, `700 KB`; leave either
box empty for no limit, which is the default for both. Changing the limits and re-scanning
both drops what now falls outside them and picks up what now falls inside.

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

*Skip the Recycle Bin (delete permanently)* can be **armed by default** with a setting on
**Settings… → General**. It sits under a frank warning: the bin is the one thing standing
between a mis-click and a file that is simply gone, and a recycled delete is the only kind
Undo can put back. The destructive confirmation still starts clear every single time — the
setting arms the dialog, never the confirmation.

When a delete takes the **last file out of a folder**, you are asked whether the now-empty
folder should go too, and any parent it empties in turn goes with it — a season folder that
was the last season of a show takes the show folder with it. Turn the offer off with *After
deleting the last file in a folder…* in Settings.

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
hundred-and-second episode.

**Encoding details are not episode numbers.** A number sitting inside a codec or resolution
token describes the file, not the programme, so nothing reads it: `x264` is a codec and not
season 2 episode 64, `h265` is not S02E65, and the `1920` in `1920x1080` is not a year. The
same guard covers `x265`, `h264`, `10bit`, the bare resolutions (`720`, `1080`, `2160`, …)
and the rest of the release-noise vocabulary — one list, used both for cleaning up titles
and for deciding what a number is allowed to mean. An explicit `S01E02` still wins over
everything, codec tokens included.

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
IMDb publish their catalogue as a gzipped TSV. If neither the extract nor the raw download
is present, **Verify titles** offers to fetch it — or press *Download now* on
**Settings… → Data sources**. The address is a setting (defaulting to
[`title.basics.tsv.gz`](https://datasets.imdbws.com/title.basics.tsv.gz)) so a move on
IMDb's side can be corrected there rather than waiting for a new build. The download
streams to disk and is only kept once it has arrived whole, and what arrives is checked
for being a gzip at all — a proxy answering with a login page is a failed download, not a
successful one.

You can equally drop the file in the program folder yourself — gzipped or unpacked, either
is read as-is.

The first time titles are verified, the file is boiled down to **`IMDBData.tsv`**, keeping
only the two columns that matter: **primary title** and **year**. The source is over a
gigabyte, so it is streamed a line at a time and never loaded into memory. IMDb's
placeholder rows for untitled episodes — `Episode #1.4`, `Episode dated 3 May 1999`,
`Episode 12` — are dropped, since they would only ever match by accident. So are the
**broadcast timestamps** some feeds leave in the title column — rows reading
`22. sep. 2016 kl. 07:30` — which are a transmission slot rather than the name of anything,
are numerous, and match nothing anyone will ever search for. If `IMDBData.tsv` is already
there the raw file is left alone.

**Verify titles** then confirms film and programme names against it and fills in any
**missing years**. A name that cannot be identified from the file itself is looked up under
**the folder the file sits in**, and then under each folder above it — which is usually the
only place a film's name survives, a release called `xvid-grp.avi` sitting inside
`Blade Runner (1982)`. Folder names are tried undecorated as well: `(1982)`, `[1080p]` and a
bare trailing year are each stripped for an extra attempt, always *after* the full name, so
a film genuinely called *Blade Runner 2049* is not mistaken for *Blade Runner*. There is no rate limit and no network involved, so the whole catalogue is
answered in a single pass; **TMDb is only asked about what IMDb could not identify**, which
matters when TMDb allows one query every two seconds. Titles are matched ignoring case,
punctuation and spacing, so `King Of The Hill` finds *King of the Hill*; where a name has
been used more than once, the earliest year wins.

By default the extract is **held in memory** for fast lookups (a few hundred megabytes).
Turn *Keep the IMDb data in memory* off in **Settings…** and it is read from disk instead —
slower, but free. Even then a whole run is answered in one pass over the file, not one pass
per title.

### TMDb (themoviedb.org) validation — deprecated

> **TMDb is only used when `IMDBData.tsv` does not exist.** It answers one query every two
> seconds, so a library of any size spends hours there to reach an answer the local extract
> gives in a single pass over a local file — which makes "use both" a choice nobody would
> knowingly make. Once the extract is present TMDb is not consulted at all, and *Validate
> TV (TMDb)* says so rather than starting a job that had a local answer all along. It is
> expected to be removed in a future release.

Enter a free TMDb **v4 Read Access Token** *or* **v3 API Key** on **Settings… → Data
sources** (the token is preferred if both are given), then **Validate TV (TMDb)** confirms
show names against TMDb. **Verify titles** also falls back to TMDb for anything the local
IMDb data could not identify — films against the film index, programmes against the
programme one, since TMDb keeps them apart and *Fargo* is both. Lookups are **rate-limited to
one every two seconds** and **cached** (`tmdb-cache.xml`) so names are never queried twice;
the cache records which index answered, so a film's answer is never handed back for a
programme's question. Caches written by earlier versions are read as the TV lookups they were. If the episode title doesn't match,
the containing folder names are tried in turn (e.g. `…\Bewitched\Season 01\ep.avi` falls
back to "Bewitched"). **Every** folder up to the drive root is tried, each one also without
its trailing decoration — `Yes Minister (1980)` is offered as `Yes Minister` too — with
season folders and single-letter buckets kept for last rather than skipped, since a show
really can be called *Ed*. Validated titles show a ✓ in the TMDb column (✎ marks one you
typed yourself). A confirmed name is also **shared with every file that had the same
title**, so one lookup fixes — and spares a query for — the rest of the show.

## Versioning
The build carries a Windows **file version of `0.0.<major>.<minor>`** — `0.0.2.1` for
v2.1 — with the product version kept as the number people talk about (`2.1`). Major and
minor stay at `0`; the release rides in the build and revision fields.

Both numbers are set in one place, [`Directory.Build.props`](Directory.Build.props), and
every project in the solution picks them up — bump them there once per release. **About**
in the toolbar shows both, next to the program icon.

## Roadmap / possible extensions
- Acting on near-duplicate groups directly (keep-best / bulk delete) from the UI — the
  exact-duplicate manager and the same-title manager both do this; the perceptual groups
  do not yet.
- Removing TMDb entirely, now that the local IMDb extract supersedes it.
- Retiring folder rules altogether, once existing catalogues have migrated off them.
- Using a folder's year as well as its name when a film is identified from the folder.

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
