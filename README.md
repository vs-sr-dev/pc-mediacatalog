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
   verifies successfully).

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
- ✅ Integrity flags: zero-byte + in-progress downloads (`.part`/`.crdownload`), plus
  **deep corrupt-file detection** via full FFmpeg decode
- ✅ XML persistence (catalogue *and* tool settings)

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

## Roadmap / possible extensions
- Online metadata (TMDb/TVDb) for exact titles — deliberately omitted (no API key).
- Folder-restructuring rename (e.g. `Show/Season 01/…`) in addition to in-place rename.
- Acting on near-duplicate groups directly (keep-best / bulk delete) from the UI.

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
