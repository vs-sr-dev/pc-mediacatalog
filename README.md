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
dotnet test MediaCatalog.Tests
dotnet run --project MediaCatalog.App
```

## How to use
1. Click **Scan…** and work through the wizard: what to do with the existing catalogue,
   which drives and folders to walk, what to pick up. On a first run it opens by itself,
   since an empty catalogue is the one state the app can't do anything useful with.
2. The grid fills with every audio/video file found. Use the **View** dropdown to filter
   (All / Video / Audio / Movies / TV / Duplicates / Problems), and the filter bar to narrow
   it by any column — name, path, either title, genre, year, season and episode.
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
5. **A move that fails says why.** An unplugged drive, a share that has gone, a folder you
   have no permission to write to, a disk with no room left, a file held open — and by
   which application — a path Windows will not take: each is named, with the path it is
   about. The unreachable cases are caught *before* anything is copied, and a copy that
   fails part-way is removed rather than left looking like the file it is not.

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
   ticked drive is covered by it and skipped rather than walked twice. **Remove** takes a
   folder off the list for good — including one marked *(not found)*, which is a folder
   since deleted or one on a drive that is not connected. Those start unticked rather than
   being walked for and then reported as never having turned up.
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

## New in v2.7

**The rules already in place**
- ✅ **The built-in consolidation rules are shown.** A third tab in **Settings… → Library →
  Rules…** is the judgement the program makes for itself when a category has none of its own —
  written out both as ordered steps and in the little language, with the length tolerance
  really set for that category, and a button to copy either into the tabs beside it. Until now
  it was a paragraph of prose and several hundred lines of code nobody outside the project
  will ever read, and anybody writing their first rules was being asked to start from nothing
- ✅ **The difference between the two forms is the lesson.** The steps are the *choosing*; the
  rules are the choosing plus the two places the program refuses to choose — copies that do
  not look like the same thing, copies too far apart in length to be the same cut. A step
  compares one thing and knows nothing about any other, so it always chooses, and the tab says
  so rather than letting somebody find out when their library starts filing things it used to
  ask about
- ✅ **The language grew enough to say it.** `SameContent()` is the real test the built-in run
  uses — two copies of different lengths are re-sampled over the stretch they have in common,
  so a minute of credits does not make one look like a different film. That the built-in
  judgement can be expressed at all is now a test rather than a claim: the day the language
  cannot say what the program itself relies on, it has stopped being able to say the things
  that matter

**Rules built by dragging, properly**
- ✅ **A block already placed can be dragged again** — anywhere in the rule it is in, or into
  a different rule altogether. Taking blocks out and dropping fresh ones back in, which is
  what v2.6 asked of everybody, is not building; it is retyping
- ✅ **A whole rule can be dragged by its grip** to change where it comes in the order, which
  matters rather a lot in a language where the first rule that holds is the one that decides

**What would happen**
- ✅ **Every field a rule can read is now in the worked example** — date modified, what a deep
  check has already found, what one *would* find if a rule asked for one, whether either copy
  has a fingerprint, and a tick for whether the two really are the same thing. A demonstration
  that shows four of the ten fields can answer four tenths of the questions you have, and the
  ones it could not answer were exactly the interesting ones: what happens when the best copy
  is the one that will not decode
- ✅ **Add a copy** puts a third and a fourth rival in, which is how to watch the tournament
  actually play them off against each other rather than take it on trust

**Plugins**
- ✅ **Teach it about file types nobody wrote it to handle.** A plugin is a DLL with three
  public methods that take and return strings of XML: what it is, which extensions it claims,
  and what it makes of one file. They are found *by name* rather than through an interface, so
  a plugin needs no reference to this program, can be written in anything that produces a .NET
  assembly, and cannot be broken by a version of this that ships next year
- ✅ **Dropped in and picked up.** Anything in the `plugins` folder beside the application is
  loaded at startup; anything else can be named on **Settings… → Plugins**, by file or by
  folder, and switched off there without being deleted. A plugin that will not load says why,
  where you can see it, rather than failing quietly in the middle of a scan
- ✅ **What it brings is folded in everywhere.** The extensions it claims become things a scan
  picks up. The fields it declares become columns in the results, values in the filter bar,
  lines in the file's details — and things a consolidation rule can compare two copies on,
  both as a step (*keep the greater number of pages*) and in the language
  (`if (File1.Author == "Iain M. Banks") Consolidate(File1)`). The media type it declares
  becomes a real category, with its own consolidation folder, naming pattern and rules
- ✅ **The example plugin ships with it.** An e-book plugin that reads EPUB metadata out of
  the archive, counts a PDF's pages and falls back on the file name for the rest. It is in
  `plugins-available` rather than `plugins`, deliberately: it claims `.pdf`, and deciding to
  catalogue every PDF on somebody's machine is not a thing to do on their behalf
- ✅ **Nothing a plugin does is trusted.** One that throws on a file has not stopped working —
  a malformed e-book is a thing that exists — so the failure belongs to the file. One that
  claims an extension the program already knows does not get it. One that tries to take a
  field name the built-in rules use is refused, because `File1.Size` has to mean the size on
  disk for every file there has ever been

## New in v2.6

**Rules of your own, written as rules**
- ✅ **A small language for choosing between two copies.** The ordered steps added in v2.5
  answer almost everybody's question, and there is one shape they cannot say at all: anything
  conditional. *The better picture, unless it fails a decode.* *The longer one, but only when
  they are more than a minute apart.* A step compares one thing and knows nothing about any
  other, so that sentence has nowhere to go. **Settings… → Library → Rules… → Rules of your
  own** is where it goes now: `if (File1.Quality >= File2.Quality AND NOT File1.Corrupt)
  Consolidate(File1)`, as many lines as you like, read in order
- ✅ **Built by dragging, not by typing.** Every piece of the language is a block in a palette
  — what each of the two files is (`File1.Size`, `File2.Quality`, `File1.AlreadyFiled`), how
  to compare them, how to join two comparisons up — and a rule is built by dropping them into
  it. What can be dragged out is exactly what can be written, so a rule built this way always
  reads. **Write it out instead** shows the same rules as text, and typing there puts the
  blocks back the way the text says: they are one thing, not two
- ✅ **You can ask it to find something out mid-comparison.** `DeepScan(File1)` decodes that
  file end to end and fills in what the next line reads. `FingerprintFiles()` fingerprints
  both. `LengthDifferent(60)` is false when the two run the same length to within a minute and
  true when they do not. **Nothing expensive is ever done twice** — a copy that keeps winning
  turns up in every comparison there is, and decoding it once per round is the difference
  between an evening and a week
- ✅ **`Consolidate(File1)` ends the comparison and names the keeper.** With more than two
  different copies of one thing they are played off against each other two at a time — the
  winner carried forward as `File1`, the next copy as `File2` — until one is left standing.
  Only **unique** copies are compared: how many physical files there are does not matter if
  only two of them are different, and where several copies of one of them exist the one on the
  library's own drive is the one that goes forward
- ✅ **`Undecided` is a thing you can say.** A script that has just established the two files
  are not the same content at all should stand aside and let you look, and it can say so —
  along with the line that did it. A script that simply runs out does the same
- ✅ **The worked example works your rules too.** The two sample copies at the bottom of the
  wizard are put through the script by the same code the real run uses, and it says which
  would be filed, which line decided it, and what getting there would have cost in decodes

**Two files, one name**
- ✅ **Consolidate the copy you pick.** That dialog has never only held the two files in the
  collision — it lists every other known copy of either of them, which is the whole reason it
  is a list. Now you can pick one and say **Consolidate selected**: that copy goes to the
  contested name, the file already there makes way for it, and the one that started it all
  stays where it is. Often the best copy is neither of the two you were asked about

**Episode numbers**
- ✅ **`Sabrina, The Teenage Witch [01-01] Pilot [Dvdrip SAiNTS].avi` is S01E01.** A season and
  an episode in brackets with nothing marking either of them. The brackets are what makes it
  safe to read: `[2009-2012]` cannot match, and neither can `(1-2)`, which is a part number
- ✅ **`bull.2016.101.hdtv-lol[ettv].mkv` is S01E01 again**, and so is
  `01 - the.flash.2014.101.hdtv-lol.mp4`. The year names the programme rather than dating a
  film, and what follows it is the episode code — read as episode 1 of season 1 rather than
  episode 1 of season 10, because shows reach a first season rather more often than a tenth.
  A year with nothing else beside it is still the signature of a film, and
  `Blade.Runner.2049.2017.1080p` is still two years and not season 20
- ✅ **Fixing doubled episode numbers reaches the whole tree.** *Library → Fix doubled episode
  numbers in a folder…* walks every folder underneath the one you point it at, all the way
  down, and no longer consults the scan exclusions on the way. A library is a tree — the
  programme, then the season, then the episodes — and an exclusion is somebody saying what a
  *scan* should not waste time on, which is no reason to refuse to repair a folder they have
  just picked out by hand

## New in v2.5

**A menu, instead of a wall of buttons**
- ✅ **Four menus where there were twenty-odd toolbar buttons.** Everything about scanning is
  under **Scan**, everything that acts on the library is under **Library**, the measuring
  tools are under **Tools**. The headline features used to sit between *Re-hash pending* and
  *Deep check folder*, and whichever of them the window was too narrow for disappeared into
  an overflow chevron. Only *Consolidate…* and *Auto-consolidate…* stay on show, beside the
  filters — those are what a session is spent doing
- ✅ **A *Redundant* folder on the Tools menu** for the commands something else now does
  better: *Relocate…*, *Suggest consolidation…*, *Fingerprint everything* and *Validate TV
  (TMDb)*. Nothing is deleted, and hovering any of them says **why it is expected to go** —
  which is a rather more useful thing to be told than finding it missing in a later version
- ✅ **The settings explain themselves on request.** The explanations were worth having and
  there were four hundred words of them on a tab somebody opened to tick one box, which meant
  the paragraph that mattered got skipped with the rest. Each group now folds its prose away
  behind a **Why?** button, hovering any setting gives you a line about it, and *Explain
  everything* at the top opens the lot
- ✅ **TMDb is out of the way.** It is deprecated, it is only consulted when the local IMDb
  extract is missing, and it answers one query every two seconds. The credentials and the
  *Validate TV* command are hidden until you ask for them on **Settings… → Data sources**. A
  key already entered goes on working — hiding something is not switching it off

**Consolidation rules of your own**
- ✅ **You can say how a category chooses between two copies.** A wizard on **Settings… →
  Library** builds the steps: *keep the greater length, ignoring differences under 60
  seconds*, then *keep the greater quality*, then *keep the lesser size* — as few or as many
  as you like, applied in order, the first that can tell the copies apart deciding. Length,
  quality, size, date, integrity, whether a copy is already filed, even the length of the
  name
- ✅ **A worked example that is the wizard, not a decoration.** Two sample copies sit at the
  bottom with figures you can change, and every edit says which of the two would be filed and
  **which step decided it** — worked out by the same code the real run uses. Nobody can tell
  from a rule list alone what will happen to the files on their disk
- ✅ **What counts as two copies of one thing is yours too**: identical bytes only, the same
  name wherever the files are, the same title and episode, or the built-in judgement
- ✅ **Fingerprinting and a full decode can be made part of it**, per category, and only what
  the steps actually ask for is measured — a decode is minutes a file, and running one to
  settle rules that never mention integrity is minutes spent learning nothing
- ✅ **Rules the steps cannot separate still come to you.** A rule set that runs out is not a
  licence to pick one at random

**Long jobs you can stop**
- ✅ **Pause and resume a consolidation, and it survives the program closing.** Filing a
  library of thousands is an hours-long job that moves real bytes, and until now the only way
  to stop one was to cancel it and start again from the top. *Pause* on the **Scan** menu
  finishes the file it is moving and writes down the rest; *Resume consolidation* picks it up
  where it stopped, tomorrow if you like. What is remembered is the work **left**, so nothing
  is examined twice
- ✅ **An interrupted run is offered back to you at the next start.** A crash, a power cut or
  somebody closing the window leaves the library half filed and only the job knows which half

**Filing**
- ✅ **A–Z folders for any category, and none for any category.** Films and programmes have
  always been sorted into a first-letter folder — A to Z, or `#` for a title beginning with a
  digit — and every other category went straight into its folder. Both are now a tick-box per
  category, and an unset one goes on doing exactly what it always did
- ✅ **An episode number is never added twice.** A file that already starts with `01` does not
  become `01 - 01 - Wheel Of Fortune.mkv`, whether this program put the first number there or
  the file arrived that way — and a naming pattern that numbers a name which numbers itself
  now says it once
- ✅ **…and the ones it happened to before can be put right.** *Library → Fix doubled episode
  numbers in a folder…* looks through a folder for them and proposes the name each goes back
  to. It reads the folder from disk rather than the catalogue, so it reaches a library that
  was filed before it was catalogued
- ✅ **A featurette in the season's Extras folder counts as filed.** Nothing about a
  featurette says which season it belongs to, so the layout offers it two homes — the show's
  Extras folder and the season's — and both are right. Insisting on the exact one the plan
  named meant such a file was reported as unfiled for ever, and consolidating it shuffled it
  from one correct place to another

**Bugs**
- ✅ **Anything can be renamed, whatever it is filed as.** A file filed as a featurette but
  categorised *TvShow* by hand could not be renamed at all: the naming scheme has nothing to
  say about either, so nothing was proposed and a corrected title never reached the disk. The
  fallback puts the title in front of the name the file already has — *Behind The Scenes.mkv*
  becomes *Burn Notice - Behind The Scenes.mkv* — and running it again does not add it twice
- ✅ **A title typed onto an extra stays typed.** Linking an extra to the film or episode it
  belongs to copied the owner's title over it, which quietly undid the correction before the
  rename that should have followed could see it. A category you set by hand is now the last
  word on what a file is, and a title you typed is never overwritten
- ✅ **A rename reaches the catalogue.** The by-path index went stale after any rename or
  move, so everything that looks an entry up by path — a collision, a folder tidy-up, the
  next scan — was answering from a link to a file that had gone. Every operation that changes
  a path now says so, as it goes, rather than at the end
- ✅ **Three more ways of writing an episode number.** `Home Improvement 5-26 Games Flames And
  Automobiles.avi` is S05E26; `Dexter (s8 – 1) A Beautiful Day.FLV` is S08E01 and
  `Dexter (s8 – ep 3)` is S08E03 — including when the dash has been through an encoding that
  could not carry it and comes back as `?`
- ✅ **Re-check**, which is *Verify titles* under a name that says what it does. It re-derives
  the **season and episode** from the name and its folders with the current rules before it
  confirms the title, which is how a file catalogued before a parsing rule existed gets the
  benefit of it — `The Dead Zone - 01 01 - Wheel Of Fortune.mkv` among them. Numbering you
  typed yourself is left exactly as you entered it

## New in v2.4

**Knowing what you are missing**
- ✅ **Missing episodes.** *Missing episodes…* looks through the consolidated programmes and
  says what is not there. Two different holes, and they are worth telling apart: a gap in the
  middle of a season — 1, 2, 3, 5 — needs nothing but the files to find, and the **missing
  tail** needs knowing how long the season actually ran. A folder holding episodes 1 to 12
  looks complete from the inside, and only IMDb's episode data can say that there were
  thirteen. Without that data the tail is not checked and the report **says so** rather than
  implying a clean bill of health it cannot give. Each missing episode is listed by **name**
  where there is one to give, so the list is something you can go and act on
- ✅ **Seasons you have none of are listed separately** from the gaps. Owning one season of a
  programme is a perfectly ordinary thing to do, and it is not the same kind of news as being
  one episode short

**Two titles, not one**
- ✅ **A primary title and a secondary title.** The primary title is the old *Title* under a
  clearer name — the programme's name, the film's — and is still what decides where a file is
  filed. The secondary title is the name underneath it: *Go Get Mommy's Bra* under *Two and a
  Half Men*, *Lost in New York* under *Home Alone 2*, and in time the track under the band.
  Existing catalogues carry straight over: nothing about the data changed, only what it is
  called. Episode names are **filled in for you** by the missing-episode scan, since it has
  looked the season up anyway and the names are sitting beside the numbers
- ✅ **`{secondarytitle}` in the naming patterns**, so a library can file
  `04 - Go Get Mommy's Bra.mkv` if that is how you read it

**Genres**
- ✅ **Genres in the catalogue, and a column and a filter for them.** Filled in by *Verify
  titles* from the local IMDb data, alongside the titles and years it already fills in — the
  answers were in the same rows all along. The filter offers the genres your data actually
  holds, since nobody remembers whether IMDb writes *Sci-Fi* or *Science Fiction*

**The IMDb extract, rebuilt**
- ✅ **Far smaller, and it now knows about episodes.** The download is mostly repetition, and
  none of it was being thrown away: every identifier written as `tt0369179` where the number
  would do, every row spelling out `tvEpisode` in full, every row spelling out its genres,
  every title written twice as *primaryTitle* and *originalTitle*, and three columns nothing
  here reads. Now the identifiers keep their number, the type and the genres become numbers
  with a small table saying what each means — **built from the data on every extraction**, so
  a genre IMDb adds next year is picked up rather than quietly filed as unknown — and
  *originalTitle*, *isAdult* and *runtimeMinutes* are dropped. The running time in particular
  is better read from your own file than believed from a database
- ✅ **`title.episode.tsv` is read too**, into a table of which episode of which programme each
  identifier is. That is what makes a missing last episode findable at all. *Download
  episodes* on **Settings… → Data sources** fetches it; it is optional and everything else
  works without it
- ✅ **A year window on the extraction, 1950 by default.** The dataset reaches back to the
  1890s and hardly anybody is cataloguing that. Clear the box to keep every year there is, or
  set an end year as well. A title with **no** year is kept whichever way it is set: a missing
  date is not a date outside the range

**Consolidating**
- ✅ **Copies of different lengths are compared properly at last.** A video fingerprint is
  sixteen frames spread across the whole file, which is what makes it comparable between two
  encodings — and is also why it fell apart the moment two files were of different lengths:
  put a minute of credits on one and every sample lands at a different moment, so two
  complete copies of one film looked like nothing alike. They are now compared **over the
  stretch they have in common**, which puts every sample back on the same moment. It costs
  sixteen frames of decoding, and only where the ordinary comparison has already failed and
  the lengths explain why
- ✅ **The longer copy wins, at equal or better quality.** A copy with the credits on it holds
  everything the shorter one holds and something besides. Quality still comes first — a
  longer copy at a worse resolution is a worse copy with more of itself — and among copies of
  one quality *and* one length the smallest still wins, since there the extra bytes are
  padding
- ✅ **A length tolerance per category.** Within it, two copies are the same thing and are
  settled by the ordinary rules; beyond it they come to you, because at some point a longer
  copy is a different cut. Per category because a minute means opposite things in each: sixty
  seconds between two rips of a film is the credits and decides nothing, sixty seconds between
  two copies of a song is a different recording. Video starts at 60 seconds and audio at 2
- ✅ **You are only asked when the rules genuinely cannot choose.** An episode already in the
  library under a different name used to be put to you every time; most of those pairs answer
  themselves — same content or not, better copy or not — and are now settled the same way an
  automatic run settles any other pair of rivals. The same goes for the same-title twins
  before a manual consolidation: what can be decided is decided, and only the sets that
  cannot are put in front of you
- ✅ **Emptied folders simply go.** Every folder in that list is either empty or holds less
  than the size set for its category; none holds a catalogued file you have not filed yet, and
  none is a folder you have named in the settings. Those three tests *are* the judgement, and
  they have already been made — a question whose answer is always yes is not a question. Turn
  it off on **Settings… → General** to be shown the list and asked first

**Smaller things**
- ✅ **The *Filed* column is called *Consolidated***, which is what everything else in the
  program calls it. Remembered widths and saved filters follow the rename rather than
  silently pointing at a column that no longer exists

## New in v2.3

**Consolidating without being asked anything**
- ✅ **Auto-consolidate.** One button files everything the program can decide about on its
  own, and hands you a list of what it could not. The rules are the ones a careful person
  would follow, in that order: a file that does not yet say what it is (no title, no year on
  a film, no episode number on an episode, no consolidation folder for its category) is set
  aside untouched with the reason spelled out; a file with no other copy is simply filed;
  copies that are byte-for-byte identical decide themselves, keeping the one already in the
  library because that means moving nothing and deleting the rest; and genuinely different
  files claiming to be the same thing are **fingerprinted and compared**, then ranked by
  quality — best picture first, and among equals the smallest, since at one resolution the
  extra bytes are padding — then **decoded end to end**, and only a copy that survives that
  is kept. A copy that fails the decode is removed along with its byte-identical twins,
  which are damaged by definition, and the next best is tried until one survives or none is
  left. If the fingerprints disagree, nothing is touched: one of the two is mislabelled, and
  only you can say which
- ✅ **Nothing is deleted until the copy replacing it has arrived.** A run that fails or is
  stopped part-way leaves everything it had not yet reached exactly where it was
- ✅ **Fingerprints allow for copies that don't start together.** An extra second of
  distributor logo used to make two rips of one film look like nothing alike, because every
  frame after it was compared against the wrong one. The comparison now slides one against
  the other and keeps the best alignment — and pays for that only where there is a
  disagreement to explain, so nothing gets slower for the copies that already agreed

**A file that has been filed is never left with copies lying about**
- ✅ **Every consolidation ends with a duplicate sweep**, whatever route got there. This was
  easiest to miss in the case where nothing moved: a whole season put right by renaming its
  folder is filed just as surely as a file copied one at a time, and used to leave every
  stray copy of those episodes where it was, unmentioned
- ✅ **Consolidating a file that has a same-title twin asks first.** Both copies claim to be
  the same thing without being the same bytes, and only one belongs in the library — so
  every copy is put in front of you with the facts that decide it, you choose, and the rest
  are deleted before anything is filed

**Subtitles**
- ✅ **Subtitles travel with their video.** `The Film.mkv` and `The Film.eng.srt` are tied
  together by name and by nothing else, so a rename that left the subtitles behind broke
  them on the spot. A rename now takes them along and renames them to match; consolidating
  either brings them or — if you would rather the library held only the media — deletes
  them, so nothing dead is left in the source folder. The language tag is kept exactly as it
  was, and a subtitle that merely *starts* with the same name (`The Film 2.srt`) is never
  claimed

**Folders left behind**
- ✅ **A folder holding only scraps can go with the file that was in it.** After a film is
  filed its old folder often still holds a sample clip, a screenshot and a readme — litter,
  not content. Below a size you set the folder goes too. The size is **per category**,
  because the same three megabytes mean opposite things: left where a film used to be it is
  a sample, and in a music folder it is very probably a track. Video categories start at
  25 MB and audio at nothing
- ✅ **Two things override the size, always.** A catalogued file that has not been filed yet
  protects its whole folder however small it is — that is work you have not finished. And a
  folder you have named in the settings (one you scan, one you watch, a consolidation
  folder) is never removed however empty it ends up: a download folder is empty most of the
  time, and that is what it is for
- ✅ **Emptied folders are deleted outright** rather than sent to the Recycle Bin. Safe in a
  way that deleting a *file* permanently is not — what goes has already been judged to be
  nothing

**Reading names**
- ✅ **`The Dead Zone - 04 01 - Broken Circle (2).mkv` is S04E01.** A season and an episode
  written as two plain numbers with no letter marking either of them. Only the shape that
  means it is read — the pair fenced off by a dash on each side, in the slot between the
  programme's name and the episode's — so a lone year or a resolution between dashes is
  still left alone
- ✅ **A title that has been used more than once is flagged.** A remake, a reboot, a series
  and the film it came from all answer to one name, and nothing in a file name says which.
  The most recent is taken, because the copy somebody has is far more often the current
  release — and the year is shown as `2017 ?` to say plainly that it is a guess. The
  **UncertainYear** view lists them; setting the year yourself clears the mark

**Editing**
- ✅ **Set folder details.** Title, year and category for a whole folder at once, replacing
  the two separate folder dialogs. The year is the reason it exists: a series whose file
  names carry the year of the season rather than of the show ends up with twelve episodes
  filed under a year that is nobody's idea of right, and correcting that an episode at a
  time is not a reasonable thing to ask. A field you leave alone is not written, so
  correcting the year does not re-stamp the title
- ✅ **Set the title for many files at once**, across folders. A show whose episodes ended up
  in three different places is named once rather than three times

**Smaller things**
- ✅ **The version is in the title bar.** Asking somebody which build they are running should
  not mean sending them to a dialog
- ✅ **Possible duplicates opens on the file you clicked**, with that copy already picked
  out, and has a **name filter** — a library of any size produces hundreds of these sets
- ✅ **Ignored file types take wildcards.** `?` is one character and `*` is any run of them,
  so `.mp?` covers .mp3 and .mp4 and `.m*` covers every extension beginning with m. The
  whole extension has to match, so `.mp3` does not also ignore `.mp3x`. They are written
  **tab-separated** in one box now rather than down a scrolling column
- ✅ **The order of the "Set category" menu is yours**, from the Categories tab. Somebody
  whose library is nine tenths television should not walk past Movie every time
- ✅ **The naming pattern is filled in for you** on a fresh install, with a **Suggest** button
  beside every box — a pattern language is far easier to learn from a working example than
  from a list of fields. Existing libraries are left alone, since filling the box in for one
  already filed under the built-in naming would quietly rename all of it
- ✅ **TvExtra and MovieExtra no longer offer a consolidation folder.** A special belongs
  beside the film or episode it is a special of, in an `\Extras\` subfolder of that, so a
  destination of its own was a setting that could only ever be ignored
- ✅ **New files are announced once, not five times.** A folder copied in writes forty files
  within a second of each other; the first starts a short wait and everything landing during
  it joins the same message
- ✅ **Nothing is said when the window goes to the notification area.** Minimising is
  something you just did on purpose, to a window you configured to behave that way

## New in v2.2

**Consolidating never leaves the library holding the same thing twice**
- ✅ **An episode already in the library is never filed a second time.** Two releases of one
  episode carry two different names, so the old name-collision check saw nothing to stop —
  and the library quietly gained a duplicate. An episode is now identified by its show,
  season and episode number, whatever the file is called. Byte-identical copies settle
  themselves: the library keeps the one it has, and the arrival goes with every other copy
  of it. Genuinely different files — a different release, a different quality — are put in
  front of you side by side, with sizes, lengths, qualities and deep-check verdicts, and
  one of them stays
- ✅ **Consolidating is always a move.** A file already on the destination's drive is renamed
  into place without being copied; a whole folder in the wrong place is renamed rather than
  emptied out one file at a time; only a genuine cross-drive move copies, verifies against
  the original and then permanently deletes it. One copy, in the library
- ✅ **A misnamed folder with subfolders is renamed too** — a film folder with its `\Extras\`
  beside it is moved as a unit now, instead of falling back to copying every file out

**Naming**
- ✅ **Double episodes.** `Burn.Notice.S06E11E12.mp4` is episodes 11 *and* 12 of season 6,
  and `The.Librarians.US.S01E01-E02.avi` is episodes 1 and 2 of season 1. Shown as
  `S06E11-E12`, filed as `11-12 - name.ext`, and — since a double is not either of the
  episodes it holds — never mistaken for a duplicate of one of them
- ✅ **Custom file names per category.** Write a pattern like `{episode:00} - {title} -
  {numbering}` or `{title} ({year}) [{quality}]` against a consolidation folder and its
  files are named that way, with an example shown beside the box as you type. Fields that
  have nothing to say come out empty and the punctuation around them is tidied up. The
  extension never changes — nothing here re-encodes anything

**Possible duplicates**
- ✅ **A programme is only a possible duplicate of another programme with the same show
  title *and* the same season and episode number.** Two episodes of one series share a
  title, a year and very nearly a name without being remotely the same thing. The year is
  no longer part of it for TV either: two copies of one episode routinely disagree about
  which year it is
- ✅ **Deep checking says how far along it is** — which file of how many, how far into that
  file the decode has reached, and how many are left, with a bar and a Stop button. It was
  minutes of silence before

**Editing**
- ✅ **A season/episode you type in is never cleared.** A film has neither — so somebody
  typing one in is saying the file was identified wrongly, not asking for the correction to
  be thrown away. The numbering stays and changing the category to *TvShow* is offered
- ✅ **A "to episode" box** for the double-episode case

**Filtering**
- ✅ **Enter in the filter box adds the filter**
- ✅ **Columns with a fixed set of values offer them** — Dup, Kind, Filed, Category,
  Integrity and TMDb are picked from a list rather than typed from memory, and **(blank)**
  is one of the choices, so "every file that is *not* a duplicate" is finally a filter you
  can write. Open-ended columns are typed into exactly as before

**Elsewhere**
- ✅ **A failed move says what went wrong** — an unplugged drive, no write permission, a
  full disk, a file held open and by what — instead of a count of failures. The unreachable
  cases are caught before a single byte is copied
- ✅ **Watch a particular folder**, not the whole drive it is on
- ✅ **The file-name position setting applies everywhere** a file goes past, not only during
  a scan: verifying, re-hashing, moving, consolidating, analysing
- ✅ **The scan wizard opens tall enough to show its buttons**, and a folder can always be
  removed from its list — including one marked *(not found)*, which used to come back the
  next time it was opened
- ✅ **No notification when the window is closed to the notification area** — it was about
  something you had just done on purpose
- ✅ **The Recycle Bin warning is now one sentence.** Deleting for good already takes three
  deliberate acts and the option is off unless you turn it on; a paragraph of alarm on top
  of that was shouting at the wrong moment
- ✅ **The results update as soon as anything changes them**, consolidation included

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
- ✅ **Local IMDb title data** — `title.basics.tsv` boiled down to a compact extract of
  identifiers, titles, years, types and genres that validates films *and* programmes with no
  rate limit, no API key and no network; TMDb is only asked what IMDb cannot answer
- ✅ **Local IMDb episode data** — `title.episode.tsv` boiled down the same way, so a season
  can be checked against the number of episodes actually broadcast
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
*Set folder details…* (applies to a whole folder, optionally its subfolders), or *Add new
category…*. Overrides win over the auto-detected category, and setting one on a file sets it
on **every exact duplicate of that file** too, so the same content is never filed two
different ways. A season/episode code beats the extension: anything that says `S02E05` —
whatever it is called or however it is packaged — is categorised as **TvShow**.

The order the categories appear in is yours, from **Settings… → Categories**: somebody whose
library is nine tenths television should not have to walk past *Movie* every time. A category
added later joins the bottom of the list rather than disappearing from it.

> **Everything a file knows lives in the catalogue.** Setting a category or title on a
> folder writes it onto each of that folder's files rather than leaving a rule behind in
> the settings. Rules saved by earlier versions are migrated the same way by **Refresh
> catalogue**, and each one is retired as soon as its files have been labelled outright.
> (A rule whose folder has not been scanned yet is kept, since dropping it would lose the
> instruction.) Folder rules are on their way out and may go entirely in a later release.

**Titles come in two.** The **primary title** is the programme's name or the film's — the
field that used to be called simply *Title*, under a name that says what it is now that there
is a second one. It is still what decides where a file is filed. The **secondary title** is
the name underneath it: an episode's own title (*Go Get Mommy's Bra* under *Two and a Half
Men*), a film's tag line or extended name (*Lost in New York* under *Home Alone 2*), and in
time a track's name under the band's. Most files have only the first, and the second decides
nothing — so a wrong one costs a wrong word on the screen and nothing else. Existing
catalogues carry straight over: only the name of the field changed.

**Genres** are recorded against a file by *Re-check*, from the same IMDb rows that
supply the titles and years — the answers were already in hand. They have a column of their
own and can be filtered on. A genre you type in by hand is left alone by later runs.

**Edit details** — right-click → *Edit details…* opens every field of an entry at once:
primary title, secondary title, genres, year, season, episode, *to episode* (for a double
episode), category, file name, **modified date**, integrity and kind.
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
gets one too — and to nothing else. *Set title for the selected files…* names everything you
have selected at once, **wherever those files live**: a show whose episodes ended up in three
different folders is named once rather than three times. *Set folder details…* names a whole
folder (or a parent) at once, again reaching the copies of those files wherever they live. A
hand-typed title counts as validated (shown as ✎ in the TMDb column; ✓ means confirmed by
IMDb or TMDb).

**Set folder details** — one dialog for everything a folder can be told: the title, the year
and the category. The year is the reason it exists. A series whose file names carry the year
of each season rather than of the show ends up with twelve episodes filed under a year that
is nobody's idea of right, and correcting that one episode at a time is not a reasonable
thing to ask. A field you leave alone is not written at all, so correcting the year does not
quietly re-stamp the title as hand-typed; the year has its own tick-box, because *leave it
alone* and *it has no year* are different instructions and an empty box cannot say both.

**A year that could be the wrong one is marked.** One title can belong to several things — a
remake, a reboot, a series and the film it came from — and nothing in a file name says which.
The most recent is taken, because the copy somebody has is far more often the current release
than the fifty-year-old one, and the Year column shows `2017 ?` to say plainly that it is a
guess rather than a fact. The **UncertainYear** view lists every one of them; setting the
year yourself clears the mark, since a year you typed is not a guess.

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

**Numbering you type in is the exception, and is never cleared.** A film has no season and
no episode — so somebody typing one into a file filed as a film is telling us the file was
*identified* wrongly, not asking for their correction to be thrown away. The numbering
stays, and the editor offers to change the category to *TvShow*, which is what the numbering
almost certainly means. It survives rescans and catalogue refreshes too: it is not a guess
to be made again.

**Double episodes.** `Burn.Notice.S06E11E12.HDTV.x264-2HD.[VTV].mp4` holds episodes 11 *and*
12 of season 6, and `The.Librarians.US.S01E01-E02.HDTV.XviD-FUM.avi` holds episodes 1 and 2
of season 1. Both forms are read — as are `S03E07-08` and `2x05x06` — and shown in the
**S/E** column as `S06E11-E12`. The second number has to follow the first and stay close to
it, so a resolution or a year sitting next to an episode code is not mistaken for one. A
double is filed as `11-12 - name.ext`, and is a different thing from either of the episodes
it contains: it is never taken for a duplicate of episode 11, and episode 11 is never taken
for a duplicate of it. Type a *to episode* in *Edit details…* to correct one by hand.

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

**Consolidate** — select TV/film files and click **Consolidate…** to move them into a tidy
library:
- TV → `<TV dir>\<A–Z or #>\<Show>\Season NN\NN - name.ext`
- Films → `<Film dir>\<A–Z or #>\<Title (Year)>\`
- Extras → the same show/film folder, under `\Extras\`

Seasons are left-padded to ≥2 digits, and episodes are **prefixed with their episode
number** so a season folder sorts into broadcast order in any file manager — `11-12 - …`
for a double episode. A file with **no title** has nowhere to be filed, so you are asked
for one before the move rather than having it quietly skipped. Target folders are set per
category in **Settings… → Library** — any number of categories, each with its own folder.

**Consolidating is always a move**, and takes the cheapest route that does the job:

1. **Rename the folder** when a whole folder is simply in the wrong place — one operation,
   whatever it holds, nothing left behind.
2. **Rename the file into place** when it is already on the destination's drive. The data
   never moves, so a terabyte lands as fast as a byte, and nothing is duplicated.
3. **Copy, verify, delete** only when the file genuinely has to cross drives. The copy is
   hash-checked against the original, and only then is the original permanently deleted.

Consolidating exists to leave exactly one copy, in the library, which is why there is no
"copy instead" option: the *Delete after verify* tick-box applies to **Relocate**, where
copying somewhere is a reasonable thing to want.

**Every consolidation ends with a duplicate sweep.** A file that has just been filed must not
be left with copies of it lying about — that is the one thing consolidating is for. This was
easiest to miss in the case where nothing moved: a whole season put right by renaming its
folder is filed just as surely as a file copied one at a time, and used to leave every stray
copy of those episodes exactly where it was, unmentioned. Now the copies are gathered up
whichever route filed them, and you are asked once how firmly they should go.

**A same-title twin is settled first.** If something you are consolidating has another copy
claiming to be the same thing without being the same bytes, only one of them belongs in the
library — and a content hash cannot choose between them. Every copy is put in front of you
with the facts that decide it (size, length, quality, and whether it still decodes), you
choose, the rest are deleted, and the survivor goes on to be filed with the rest of the
selection.

### Auto-consolidate

**Auto-consolidate…** files everything the program can decide about on its own and hands you
a list of what it could not, with the reason for each. Before it starts you are shown the
shape of the job — how many files need no decision, how many need their copies comparing, and
how many are missing something only you can supply — because those three numbers are what the
run is really about.

The rules are the ones a careful person would follow, in that order:

1. **Not enough is known** — no title, no year on a film, no episode number on an episode, no
   consolidation folder for its category, or the file is still downloading. Each of these
   decides *where the file goes*, so filing without it means filing it in the wrong place and
   having to do it again. Set aside untouched, with the reason spelled out.
2. **No other copy** — nothing to decide. Filed by the ordinary rules above.
3. **Only byte-identical copies** — which one survives changes nothing about what the library
   ends up holding, so the one already in the library wins (that means moving nothing), or
   failing that one on the library's own drive (that means a rename rather than a copy).
   Every other copy is deleted once the survivor is safely in place.
4. **Genuinely different copies of one thing** — a real question, settled by looking. One file
   stands for each distinct set of bytes; each is **fingerprinted and compared**, allowing for
   one starting a second or two after another and for the two running to different lengths
   (see below). If they really are the same content, they are ranked **best picture first,
   then the longest, then the smallest**: a copy with the credits on it holds everything the
   shorter one holds and something besides, and among copies of one quality *and* one length
   the extra bytes are padding rather than detail. The best is then **decoded end to end**; if
   it is damaged it and its byte-identical twins are removed — the same bytes cannot be sound
   in one place and broken in another — and the next best is tried, until one survives or none
   is left. If the fingerprints *disagree*, nothing is touched: one of the two is mislabelled,
   and only you can say which.

**Copies of different lengths.** A video fingerprint is sixteen frames spread evenly across
the whole file, which is exactly what makes it comparable between two encodings of one film —
and exactly why it fell apart the moment the two files were of different lengths. Put a
minute of credits on the end of one and every one of its sixteen samples lands at a different
moment, so two complete copies of one film looked like nothing alike. That is why they were
never consolidated automatically: not because anything had decided they were different, but
because nothing could tell.

They are now compared **over the stretch they have in common** — fingerprinting both as
though each were only as long as the shorter one, which puts every sample back on the same
moment. It costs sixteen frames of decoding per file, and it is only paid where the ordinary
comparison has already failed and the lengths explain why. Audio needs none of it: an
acoustic fingerprint is taken from the first two minutes by the clock, so how long the file
runs makes no difference to it.

Whether two copies of different lengths are the same *cut* is a separate question, and you
answer it in advance with a **length tolerance per category** on **Settings… → Library**.
Within it they are the same thing and are settled by the rules above; beyond it they come to
you, because at some point a longer copy is a different cut rather than the same one. The
figure is per category because a minute means opposite things in each: sixty seconds between
two rips of a film is the credits and nobody cares which copy has them, while sixty seconds
between two copies of a song is a different recording. Video starts at **60 seconds** and
audio at **2**; zero means the lengths have to match exactly.

**You are only asked when the rules cannot choose.** An episode already in the library under a
different name used to be put in front of you every time, which is the wrong instinct — most
of those pairs answer themselves. They are the same content or they are not; one is a better
copy or it is not. Both are questions the program can answer by looking, and it now does,
using the same rules as any other pair of rivals. The same applies to same-title twins before
a manual consolidation: what can be decided is decided, and only the sets that genuinely
cannot — copies that do not look alike, copies too far apart in length to be the same cut, or
no external tools with which to tell — are put to you.

**Nothing is deleted until the copy replacing it has actually arrived in the library.** A run
that fails or is stopped part-way leaves everything it had not yet reached exactly where it
was, and the whole thing is on the Undo stack.

Without FFmpeg and ffprobe, step 4 cannot compare or decode anything, so those items are
listed for you rather than guessed at. Everything else still runs.

### Rules of your own

The ordered steps on **Settings… → Library → Rules…** are the right answer for almost every
library: a list of things to compare, applied in order, the first that can tell two copies
apart deciding. What they cannot express is anything conditional, because a step compares one
thing and knows nothing about any other.

The second tab of that wizard, **Rules of your own**, is a small language for the rest. Each
rule is one line — a condition, and what to do when it holds — and they are read in order:

```
FingerprintFiles()
if (NOT FingerprintsMatch()) Undecided
if (LengthDifferent(60)) Undecided
if (File1.Quality > File2.Quality) Consolidate(File1)
if (File2.Quality > File1.Quality) Consolidate(File2)
if (File1.Size <= File2.Size) Consolidate(File1)
Consolidate(File2)
```

Rules are **built by dragging blocks** out of a palette rather than typed; the text box under
it shows the same rules written out, and typing there puts the blocks back the way the text
says. A line beginning with `#` is a note to yourself.

**A block already placed can be dragged again** — anywhere in the rule it is in, or into a
different rule altogether — and a whole rule can be picked up by the grip at its left and
dropped where it should come in the order. Building a rule by taking blocks out and dropping
fresh ones back in, which is what the first version of this asked of everybody, is not
building: it is retyping. Clicking a block still takes it out.

**Two files at a time, always.** `File1` is the copy that has won every comparison so far and
`File2` is the next one. With more than two genuinely different copies of one thing they are
played off against each other until one is left standing, so a language that only ever talks
about two files can settle any number of them. **Only unique copies are compared** — how many
physical files there are does not matter if only two of them are different — and where a
piece of content has several identical copies the one already in the library, or on the
library's drive, is the one that goes forward.

**About each file** — `Size` (bytes), `Length` (seconds), `Quality` (picture height, or
bitrate for audio), `Modified`, `NameLength`, `AlreadyFiled`, `DeepCheckIntegrity`, `Corrupt`,
`Checked`, `HasFingerprint`. Written `File1.Size`, `File2.Quality`, and so on.

**Comparing and joining** — `>` `>=` `<` `<=` `==` `!=`, joined with `AND`, `OR` and `NOT`,
and bracketed where the order matters.

**Finding something out**

| | |
|---|---|
| `DeepScan(File1)` | Decode that file end to end and record what it found in its `DeepCheckIntegrity`, `Corrupt` and `Checked`, for the lines below to read. Answers true when the file came back sound |
| `FingerprintFiles()` | Fingerprint both files, if they do not already have one |
| `FingerprintsMatch()` | True when the two fingerprints are close enough to call the same content |
| `SameContent()` | True when the two really are the same thing, allowing for one running longer than the other. This is the test the built-in rules use: a copy with a minute of credits on the end samples every frame at a different moment, so a plain fingerprint comparison says no about a film they both hold in full |
| `LengthDifferent(60)` | False when the two run the same length to within sixty seconds, true when they are further apart |

**Words, as well as numbers.** Anything a plugin hands back that is not a quantity — an
author, a genre — is compared as words: `if (File1.Author == "Iain M. Banks")
Consolidate(File1)`. Either quote opens a run and doubling one inside stands for it, so
`'Frankie''s'` needs no escape anybody has to learn. Case is ignored.

**Ending a comparison** — `Consolidate(File1)` or `Consolidate(File2)` names the copy that is
kept; nothing after it runs, and every copy still to be looked at is compared against it.
`Undecided` stops and puts the copies to you, which is the honest answer when the rules have
just established that the two are not the same thing at all. A script that runs out without
naming a copy does the same rather than guessing.

**Nothing expensive is done twice.** A file that has been decoded, fingerprinted or measured
once stays that way for the rest of the run, however many comparisons it goes on to appear
in — which matters, because a copy that keeps winning appears in every comparison there is.
Lengths and qualities are measured up front, once each, and only when the rules mention them.

A category with rules of your own uses them instead of the steps; the steps are kept, not
thrown away, so clearing the script brings them back. Rules that do not read are refused when
you press **Use these rules**, with the line and what is wrong with it.

### The rules already in place

The third tab of that wizard, **What the built-in rules do**, is the judgement the program
makes for itself when a category has no rules of its own — written out in both of the ways
you can write yours, with a button to copy either into the tabs beside it.

It is there because the best starting point for a set of rules is a working set of rules.
Until now, "the built-in judgement" was a paragraph of prose and several hundred lines of
code nobody outside the project will ever read, and anybody sitting down to write their first
rules was being asked to start from nothing — with no way of telling whether what they were
about to write was better or worse than what they already had.

**As steps**, it is four lines: keep the greater integrity, then the greater quality, then the
greater length, then the lesser size. **As rules of your own**, it is the whole of it,
including the two places the program refuses to choose at all:

```
FingerprintFiles()
if (NOT SameContent()) Undecided
if (LengthDifferent(60)) Undecided
if (File1.Quality > File2.Quality AND DeepScan(File1)) Consolidate(File1)
if (File2.Quality > File1.Quality AND DeepScan(File2)) Consolidate(File2)
if (File1.Length > File2.Length AND DeepScan(File1)) Consolidate(File1)
if (File2.Length > File1.Length AND DeepScan(File2)) Consolidate(File2)
if (File1.Size <= File2.Size AND DeepScan(File1)) Consolidate(File1)
if (DeepScan(File2)) Consolidate(File2)
Undecided
```

The tolerance on line three is the one really set for that category, so what is on screen is
your library's rules rather than a specimen.

The shape of the last six lines is worth reading twice. `AND DeepScan(File1)` is not so much
a second condition as an order of work: the comparison decides which copy is in front, and
only that one is decoded. A copy that fails its decode fails the rule and falls through — and
since every later rule that would have kept it is guarded by its now-known state, it cannot
win any of those either. That is exactly what the built-in run does, and it is why nothing is
decoded that did not need to be.

The difference between the two columns is the lesson. The steps are the *choosing*; the rules
are the choosing plus the two places the program stands aside. A step compares one thing and
knows nothing about any other, so it always chooses — which is why somebody who copies the
steps and changes nothing ends up with a category that files rather more readily than the
built-in judgement does. The wizard says so rather than leaving them to find out.

That these two really are the built-in judgement is a test rather than a claim. If the little
language ever stops being able to express the rules the program itself relies on, it has
stopped being able to say the things that matter, and that is where it turns up.

### What would happen

At the bottom of the wizard sit sample copies with **every figure a rule can read** — name,
length, quality, size, date modified, what a deep check has already found, what one *would*
find if a rule asked for one, whether either has been fingerprinted, whether either is already
filed, and a box for each field a plugin adds for that category. Under them is a tick for
whether the two really are the same thing, which is what `SameContent()` and
`FingerprintsMatch()` are answered from.

Change any of them and the sentence below changes with it: which copy would be filed, which
rule decided it, which files would go, and what getting there would have cost in decodes and
fingerprints. It is worked out by the same code the real run uses — the samples are the same
objects a real file is — and **Add a copy** puts a third and a fourth in, which is how to
watch the tournament play several rivals off against each other.

A demonstration that lets you change four of the ten things a rule can ask about can answer
four tenths of the questions you have, and the ones it cannot answer are exactly the ones
worth asking: what happens when the best copy is the one that will not decode, what happens
when nothing has fingerprinted either of them.

## Plugins

The program handles audio and video itself. **A plugin is how it is taught about anything
else** — e-books, comics, whatever you have — and what a plugin brings is folded in as though
it had always been there: a scan picks the files up, the results grid gets a column for every
field, the filter bar offers them, the file's details show them, a category appears that can
be given a consolidation folder and a naming pattern, and the consolidation rules can compare
two copies on any of it.

**Where they come from.** Anything in the `plugins` folder beside the application is picked up
on its own. Anything else can be named on **Settings… → Plugins**, either a DLL or a folder to
look in, and each one can be switched off there without deleting it. The list says what each
plugin is, which extensions it claims and which fields it fills in — and, for one that will
not load, why.

> **A plugin is a program.** It runs inside this one, with everything this one can reach:
> every drive it can read, every file it can delete. Add plugins you trust, and nothing else.

**The contract is three methods.** A plugin is a .NET assembly holding a public class with
three public methods, each taking and returning a string:

```csharp
public string Describe();             // what I am, and the fields I can fill in
public string FileTypes();            // the extensions a scan should pick up for me
public string Read(string fullPath);  // what I make of this one file
```

They are found **by name rather than through an interface**, and that is the point: a plugin
needs no reference to this program to be written, cannot be broken by a version of it that
ships next year, and can be written in anything that produces a .NET assembly. Every string
that crosses the boundary is XML, which is a string with a shape — the right amount of
structure for something somebody is going to write by hand in an afternoon.

`Describe()` says what the plugin is and declares its fields:

```xml
<plugin name="E-books" version="1.0" media="EBook">
  <description>Catalogues e-books.</description>
  <fields>
    <field name="BookName"      label="Book name"          type="text"   meaning="…" />
    <field name="Author"        label="Author"             type="text"   meaning="…" />
    <field name="YearPublished" label="Year published"     type="number" meaning="…" />
    <field name="Chapters"      label="Number of chapters" type="number" meaning="…" />
    <field name="Pages"         label="Number of pages"    type="number" meaning="…" />
  </fields>
</plugin>
```

`media` is the category those files are filed under, and it becomes a real one — it turns up
in the category dropdown and can be configured like any other. `name` is what a rule writes
(`File1.YearPublished`) and `label` is what everything on screen calls it (*Year published*);
`type` is `text`, `number`, `date` or `truth`, and decides how two copies are compared, so
that nine pages is fewer than ten rather than more.

`FileTypes()` claims extensions — `<fileTypes><type extension=".epub"/></fileTypes>`, or just
`.epub .mobi .azw3` for a plugin whose whole answer is three extensions. `Read(path)` hands
back what it made of one file: `<file><field name="Author" value="Iain M. Banks"/></file>`,
or a field per element if that is what you reach for. A field that was never declared is
dropped, since a field nobody declared has no label, no type and nowhere to be shown.

**Everything is forgiving on the way in.** A plugin that writes `ext=` rather than
`extension=`, or names a field *Year published* where a rule needs one word, is understood
rather than refused — the name becomes `YearPublished` and the label stays as it was written.
Nothing a plugin does is trusted: a plugin that throws on a file is not a plugin that has
stopped working (a malformed e-book is a thing that exists), so the failure belongs to the
file; one that will not load at all is set aside with the reason rather than taking a scan
down with it; and one claiming an extension another already has is told so on the settings
page rather than quietly losing.

**A plugin cannot take a name the built-in rules use.** `File1.Size` has to mean the size on
disk for every file there has ever been, so a plugin field called `Size` is refused. Nor can
it take an extension the program already knows: `.mp4` is not available.

**The example plugin ships with the release.** `MediaCatalog.Plugins.EBooks.dll` is in
`plugins-available` in the zip — copy it into `plugins` to turn it on — and its source is
forty lines of documentation with a working plugin wrapped round it. It reads an EPUB's
metadata out of the archive, counts a PDF's pages, and falls back on the file name for the
rest. It is **not** on by default, deliberately: it claims `.pdf`, and quietly deciding to
catalogue every PDF on somebody's machine is not a thing to do to them on their behalf.

Once it is on, `Author` and `Number of pages` are columns you can sort and filter, and
`if (File1.Pages > File2.Pages) Consolidate(File1)` is a rule you can write — or, in the
steps: *keep the greater number of pages*.

### Subtitles

`The Film.mkv` and `The Film.eng.srt` are tied together by name and by nothing else, so a
rename that left the subtitles behind broke them on the spot. **A rename always takes them
along** and renames them to match, keeping the language tag exactly as it was.

Consolidating either brings them or removes them, as you choose on **Settings… → Library**.
Leaving them is not one of the choices: a subtitle whose film has moved away is matched to
nothing and will never be matched to anything again.

A subtitle that merely *starts* with the same name is never claimed. `The Film 2.srt` belongs
to `The Film 2.mkv`, and renaming or deleting it along with `The Film.mkv` would be destroying
somebody's file on the strength of a shared prefix. What follows the video's name has to look
like a language tag — a separator and then something beginning with a letter — so `.eng`,
`.en.forced` and ` - fr` are taken and ` 2` is not.

### Folders left behind

After a film is filed, its old folder often still holds a sample clip, a screenshot and a
readme. That is litter, not content, and **below a size you set the folder goes with the file
that was in it.**

The size is **per category**, because the same three megabytes mean opposite things: left
where a film used to be it is a sample, and in a music folder it is very probably a track.
Video categories start at 25 MB and audio at nothing, which is the old behaviour of only ever
removing a folder that is truly empty. Set them on **Settings… → Library**.

Two things override the size entirely:

- **A catalogued file that has not been filed yet** protects its whole folder, however small
  it is. That is work you have not finished, not a scrap.
- **A folder you have named in the settings** — one you scan, one you watch, a consolidation
  folder — is never removed however empty it ends up. A download folder is empty most of the
  time; that is what it is for.

Folders that do go are **deleted outright** rather than sent to the Recycle Bin, which is safe
in a way that deleting a *file* permanently is not: what goes has already been judged to be
nothing. Turn that off on **Settings… → General** if you would rather they were recoverable.

**They go without being asked about.** Every folder in that list is either empty or holds less
than the size set for its category; none holds a catalogued file waiting to be filed, and none
is a folder named anywhere in the settings. Those three tests *are* the judgement, and they
have already been made — so a dialog listing the folders and asking whether they should go is
a question whose answer is always yes. Turn *Take away the folders a consolidation empties
without asking* off on **Settings… → General** to be shown the list first.

**Custom file names.** Each consolidation folder has a *named* box under it. Leave it empty
for the built-in naming, or write a pattern:

| Field | Means |
| --- | --- |
| `{title}` | the primary title — the programme's or the film's name |
| `{secondarytitle}` | the second name, when there is one: the episode's own title, a film's tag line |
| `{year}` | year of release, blank when unknown |
| `{season}` | season number — `{season:00}` pads it to two digits |
| `{episode}` | episode number — `{episode:00}` pads it |
| `{episodeend}` | last episode of a double, blank otherwise |
| `{numbering}` | the whole code: `S01E02`, or `S06E11-E12` for a double |
| `{quality}` | `1080p` for video, `320 kbps` for audio, blank when unmeasured |
| `{name}` | the file's current name, without its extension |

So `{episode:00} - {title} - {numbering}` files *Burn Notice* S06E11E12 as
`11 - Burn Notice - S06E11-E12.mp4`, and `{title} ({year}) [{quality}]` gives
`Blade Runner (1982) [1080p].mkv`. A field with nothing to say comes out empty and the
punctuation stranded around it is tidied away, so one pattern copes with a film that has no
year. An example of what your pattern produces is shown beside the box as you type. **The
extension never changes** — nothing here re-encodes anything, so a name saying otherwise
would simply be lying about the contents.

**The library never holds the same episode twice.** Two releases of one episode carry two
different names, so nothing about the names says they are the same thing — which is exactly
how a consolidation location gains a duplicate. An episode is identified by its show, its
season and its episode number instead. When the two files are byte-identical there is
nothing to decide: the library keeps the copy it already has, and the arrival goes along
with every other copy of it. When they are genuinely different — a different release, a
different quality — both are put in front of you with their sizes, lengths, qualities and
integrity, a deep check a click away, and one of them stays.

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
never groups by content. Films are matched on title and year; **a programme is matched on
its show title and its season and episode number, and nothing else** — two episodes of one
series share a title, a year and very nearly a name without being remotely the same thing,
and two copies of one episode routinely disagree about which year it is. A double episode
is a third thing again, and is not a duplicate of either episode it holds. An episode whose
numbering could not be worked out is left out rather than guessed at.

The copies are put side by side with their size, length, quality and integrity, so the
choice of which to keep is an informed one. A **deep check** decodes them first, so a
damaged copy is not the one that survives — and it reports which file of how many it is on,
how far into that file it has got, and how many are left, with a **Stop** button.
Files carrying a `title` flag in the **Dup** column are these; the **SameTitle** view lists
them.

Opened from a row's *Show duplicates of this file…*, the dialog **starts on that file's set
with that copy already picked out** — you clicked a file, and being made to find it again in
a list of its own duplicates is no answer. A **name filter** narrows the list on the left
(wildcards work, and it searches the file names as well as the set's title), because a
library of any size produces hundreds of these sets.

**Suggest consolidation** — click **Suggest consolidation…** to scan the catalogue and get a
reviewable list of proposed moves: current location → new location, with name-collision and
duplicate flags. When several copies of the same film/episode exist, the **highest-quality**
one (2160 > 1080 > 720 > 480) is preferred; TV items must have a TMDb-validated title and a
season/episode to be recommended. Items already sitting in the consolidation location are
flagged as such rather than proposed for another copy. Tick the ones to apply.

**Filtering** — the filter bar matches any column with wildcards (`*` = any run, `?` = one
char; plain text = contains). Tick **not** to exclude matches (e.g. *Category not Audio*),
and **Add filter** — or just press **Enter** in the box — to stack several at once.

**Filters stack across columns as well as within one.** Filter by year and then by name, or
by path and then by Season/Episode: every clause has to match for a row to be shown, whatever
column each is on, and each can be negated on its own. They are listed under the bar as
*All of: …*, and clicking one takes it off. The grid scrolls horizontally.

Columns that hold a fixed set of values — **Dup**, **Kind**, **Consolidated**, **Category**,
**Integrity**, **Genres** and **TMDb** — offer them in the box's drop-down rather than asking
you to remember how `~dup` is spelled, and **(blank)** is one of the choices. That is what
makes "every file that is *not* a duplicate" a filter you can actually write: *Dup ~ (blank)*.
The genres offered are the ones your own data holds, since nobody remembers whether IMDb
writes *Sci-Fi* or *Science Fiction*. Open-ended columns — Name, Path, the two titles — are
typed into exactly as before.

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
*Tools…* dialog of its own, are a tab here now — the main window is for the catalogue,
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
the scanned drives ever gain new files — and list **particular folders** underneath, which
is usually what was meant: watching `E:\dump\` and watching the whole of `E:` are very
different propositions on a disk holding a hundred thousand files. Subfolders come with a
watched folder. Naming anything at all, a drive or a folder, means only what is named is
watched; naming nothing falls back on everything that was scanned.

**New arrivals are announced once, not once each.** Files come in handfuls — a folder copied
in writes forty of them within a second of each other, and forty notifications about that is
thirty-nine too many, because it is one thing that happened rather than forty. The first
arrival starts a short wait (twenty seconds by default, on the same tab) and everything
landing during it joins the same message. Later arrivals do not push the wait back, so news
of a long copy is not held until it finally finishes.

Started at sign-in, the app comes up **in the notification area with no window**, ready to
catch new files without getting in the way; double-click the tray icon (or *Open Media
Catalog* on its menu) to bring it up, and *Exit* to quit properly. While watching is on,
closing the window hides it back to the tray rather than quitting — quietly, since closing
the window is something you have just done on purpose.

Two more window options in **Settings…**:
- *Always start minimised to the notification area* — a quiet start however the app was
  launched, not only when Windows started it;
- *Minimising sends the window to the notification area instead of the taskbar* — the
  minimise button puts it in the tray, without a word about it. Minimising is something you
  just did on purpose, to a window you configured to behave that way; a notification
  confirming it is the program telling you what you already know.

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

**Consolidated** — the grid's *Consolidated* column ticks once a file lives in its
consolidation location, and the **View** dropdown can show just what is *Consolidated* or
*Not consolidated*, so it is easy to see what is left to sort out. (It was called *Filed*
before v2.4; remembered column widths and saved filters were carried over to the new name.)

### Missing episodes

**Missing episodes…** looks through the consolidated programmes and says which episodes are
not there. Consolidated ones only, and deliberately: files still scattered around a download
folder are half-finished by definition, and telling somebody that a season they have not
filed yet is incomplete is telling them what they already know.

Two kinds of hole are found, and the difference between them matters:

- **A gap in the middle of a season** — 1, 2, 3, 5 — needs nothing but the files. It is found
  whatever data you have.
- **A missing tail** needs knowing how long the season actually ran. A folder holding
  episodes 1 to 12 looks complete from the inside, and nothing in it says the season went to
  thirteen. Only the IMDb episode data can say that, and **without it the tail is not checked
  at all** — the report says as much rather than implying a clean bill of health it has no
  right to give.

Each missing episode is listed **by name** where the data has one, so the result is a list
you can act on rather than a column of numbers. **Copy the list** puts the whole report on
the clipboard.

**Seasons you have none of are listed apart from the gaps.** Owning one season of a programme
is a perfectly ordinary thing to do, and it is not the same kind of news as being one episode
short of a season you are collecting.

The same pass **fills in each held episode's own name** as its secondary title, since it has
looked the season up anyway.

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

**Ignored file types take the same wildcards**: `?` stands for exactly one character and `*`
for any run of them, so `.mp?` covers `.mp3` and `.mp4` while `.m*` covers every extension
beginning with m. The whole extension has to match — `.mp3` means `.mp3` and not `.mp3x` as
well. They are written **tab-separated** in a single box, so twenty of them read across two
lines rather than down a scrolling column; spaces, commas and new lines are accepted too,
since nobody should have to think about which separator a list of file extensions wants.

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
**Settings… → General**, under a one-line "we don't recommend this". Deleting a file for
good already takes three deliberate acts — choosing a delete, ticking the confirmation in
the dialog, and pressing the button — and the setting is off unless you turn it on, so a
paragraph of alarm on top of that was shouting at the wrong moment. The destructive
confirmation still starts clear every single time: the setting arms the dialog, never the
confirmation.

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
is present, **Re-check** offers to fetch it — or press *Download titles* on
**Settings… → Data sources**. The address is a setting (defaulting to
[`title.basics.tsv.gz`](https://datasets.imdbws.com/title.basics.tsv.gz)) so a move on
IMDb's side can be corrected there rather than waiting for a new build. The download
streams to disk and is only kept once it has arrived whole, and what arrives is checked
for being a gzip at all — a proxy answering with a login page is a failed download, not a
successful one.

You can equally drop the file in the program folder yourself — gzipped or unpacked, either
is read as-is.

The first time titles are verified, the file is boiled down to **`IMDBData.tsv`** and two
small tables beside it. The source is over a gigabyte, so it is streamed a line at a time and
never loaded into memory.

Most of that gigabyte is repetition, and none of it was being thrown away:

| What IMDb writes | What the extract keeps |
| --- | --- |
| `tt0369179` | `369179` — the "tt" and the leading zeros are on every row of both files |
| `titleType` as `tvEpisode` | a number, with **`IMDBTypes.tsv`** saying what each number means |
| `genres` as `Comedy,Romance` | numbers, with **`IMDBGenres.tsv`** saying what each means |
| `primaryTitle` **and** `originalTitle` | the primary title alone — the second repeats it on all but a handful of rows |
| `isAdult` | dropped |
| `runtimeMinutes` | dropped: your own file is a better authority on how long it runs than a database is |
| `startYear`, `endYear` | kept as they are |

The type and genre tables are **built from the data on every extraction** rather than fixed
in this program, because IMDb may add either at any time and a fixed list would quietly file
the new one as unknown.

IMDb's placeholder rows for untitled episodes — `Episode #1.4`, `Episode dated 3 May 1999`,
`Episode 12` — are dropped, since they would only ever match by accident. So are the
**broadcast timestamps** some feeds leave in the title column — rows reading
`22. sep. 2016 kl. 07:30` — which are a transmission slot rather than the name of anything,
are numerous, and match nothing anyone will ever search for. If `IMDBData.tsv` is already
there the raw file is left alone.

**A year window, 1950 by default.** The dataset reaches back to the 1890s, and the number of
people cataloguing media from every year there has been one is very small indeed; most
libraries are the last twenty or thirty years. Titles released outside the window are left
out, which makes the extract smaller, faster to load and quicker to answer for the rest of
its life. Clear the *from* box on **Settings… → Data sources** to keep every year there is,
and set the *to* box only if you want an upper limit — it is empty by default, which means
everything from the start year onwards. **A title with no year at all is kept whichever way
they are set**: a missing date is not a date outside the range.

**The episode data** — `title.episode.tsv`, which says which episode of which programme each
identifier is — is optional and is fetched by *Download episodes* on the same tab. It becomes
**`IMDBEpisodes.tsv`**, four numbers a row, and only for episodes the title extraction kept:
a row pointing at a title that is not there answers nothing. Rows that name neither a season
nor an episode are left out for the same reason — every chat-show instalment IMDb has never
been told the numbering of. This is the file that makes *Missing episodes* work.

An extract written by an earlier version is still read, so an existing install keeps working
until the day it is re-extracted. It simply has no genres and no episode links, because
neither is in the file; **Settings… → Data sources** says so.

**Re-check** then confirms film and programme names against it and fills in any
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
sources** (the token is preferred if both are given) after ticking *Show the TMDb settings*,
then **Tools → Redundant → Validate TV (TMDb)** confirms show names against TMDb.
**Re-check** also falls back to TMDb for anything the local
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
The build carries a Windows **file version of `0.0.<major>.<minor>`** — `0.0.2.7` for
v2.7 — with the product version kept as the number people talk about (`2.7`). Major and
minor stay at `0`; the release rides in the build and revision fields.

Both numbers are set in one place, [`Directory.Build.props`](Directory.Build.props), and
every project in the solution picks them up — bump them there once per release. The
**title bar** carries the product version, and **About** on the Help menu shows both, next
to the program icon.

## Roadmap / possible extensions
- Acting on near-duplicate groups directly (keep-best / bulk delete) from the UI — the
  exact-duplicate manager and the same-title manager both do this; the perceptual groups
  do not yet.
- Removing TMDb entirely, now that the local IMDb extract supersedes it.
- Retiring folder rules altogether, once existing catalogues have migrated off them.
- Letting auto-consolidate settle the fingerprint disagreements it still hands back. Copies of
  different lengths are now compared properly and no longer land here, but two copies that
  genuinely do not look alike remain a question for you.
- Secondary titles for music — the band as the primary title and the track as the second — and
  the naming patterns to go with it. The catalogue already carries both fields.
- Filling in film tag lines as secondary titles, as episode names are filled in now.
- A unit-test project. The engine has no UI dependency precisely so that it can be tested
  in isolation, and there is now a good deal in it worth pinning down.

## Project layout
- `MediaCatalog.Core` — engine (scanning, hashing, classification, duplicates,
  relocation, fingerprinting, integrity, XML persistence). No UI dependency, so the
  logic is unit-testable in isolation.
- `MediaCatalog.App` — WPF front end (MVVM, no external packages).
- `MediaCatalog.Plugins.EBooks` — the example plugin, and the documentation for writing one.
  It references nothing of the other two projects, which is the point: a plugin is found by
  the shape of its methods rather than by an interface it implements.
- `MediaCatalog.Tests` — xUnit tests over the engine: the episode-number parsing, the
  consolidation rules and their round trip through the settings file, the comparison language
  the rules-of-your-own wizard builds, that the built-in judgement can still be written in
  that language, the plugin contract (loaded off disk, through its own load context, exactly
  as the program loads one), the rename fallbacks, the extras linking, and the paused-job
  session. `dotnet test` runs them.

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
