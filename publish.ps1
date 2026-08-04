<#
.SYNOPSIS
    Builds the standalone release: one self-contained .exe, the README beside it, and the
    zip that goes on the GitHub release.

.DESCRIPTION
    The whole recipe for a release, kept in the repo rather than in somebody's memory. It
    was reconstructed by hand for v2.3 and there is no reason to do that twice.

    What comes out is a portable folder: a single self-contained executable (no .NET runtime
    to install), the README, and an empty 'tools' folder with a note in it saying what to
    drop there. The program writes its catalogue and settings beside itself, so unzipping
    the folder anywhere and running it is the whole installation.

    The build is refused on a dirty working tree by default, and that is not fussiness. The
    version stamped into the executable carries the commit it was built from - the v2.3 zip
    reads '2.3+1a665306...' - so a build made from uncommitted changes ships a binary
    claiming to be a commit that does not contain it. Use -AllowDirty for a throwaway build
    you are not going to publish.

    This script is deliberately ASCII only. Windows PowerShell 5.1 reads a .ps1 as ANSI
    unless it carries a byte-order mark, so a stray em-dash in a comment is enough to make
    the whole file fail to parse on somebody else's machine.

.PARAMETER Runtime
    The .NET runtime identifier. win-x64 unless you have a reason.

.PARAMETER AllowDirty
    Build even with uncommitted changes. The version stamp will name a commit whose contents
    are not what was built, so never use this for anything you intend to publish.

.PARAMETER SkipBuild
    Re-package what is already in dist\publish without rebuilding. Useful when the zip is
    the only thing you got wrong.

.EXAMPLE
    .\publish.ps1

.NOTES
    A Microsoft Store build is a different package - MSIX, signed, with its own identity and
    its own version rules - and is deliberately not this script's job. If the Store becomes
    the way this is distributed, the zip below stops being the artefact that matters, but
    the publish step underneath it does not change, so this is the right place to grow that
    from.
#>

[CmdletBinding()]
param(
    [string] $Runtime = 'win-x64',
    [switch] $AllowDirty,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$project    = 'MediaCatalog.App\MediaCatalog.App.csproj'
$publishDir = 'dist\publish'
$stageDir   = 'dist\MediaCatalog'
$zipPath    = 'MediaCatalog-app.zip'

function Write-Step([string] $Text) {
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

# --- The working tree has to match what the version will claim ------------------

if (-not $AllowDirty) {
    Write-Step 'Checking the working tree'
    $dirty = git status --porcelain
    if ($dirty) {
        Write-Host $dirty
        throw ('There are uncommitted changes. The version stamped into the executable ' +
               'names the commit it was built from, so a build made from these would claim ' +
               'a commit that does not contain them. Commit first, or pass -AllowDirty for ' +
               'a throwaway build.')
    }
    Write-Host 'Clean.'
}

# --- Build ----------------------------------------------------------------------

if (-not $SkipBuild) {
    Write-Step "Publishing $Runtime, self-contained, single file"

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    # Compression is off on purpose: it saves perhaps a fifth of the download and costs
    # every user several seconds of extraction on every single launch.
    dotnet publish $project `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false `
        -o $publishDir `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}

$exe = Join-Path $publishDir 'MediaCatalog.App.exe'
if (-not (Test-Path $exe)) {
    throw "No executable at $exe. Run without -SkipBuild."
}

# --- Assemble the folder people unzip -------------------------------------------

Write-Step 'Assembling the release folder'

if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stageDir 'tools') -Force | Out-Null

# The .pdb files stay behind: they are debugging symbols, and nobody unzipping a release
# has anything to do with them.
Copy-Item $exe        -Destination $stageDir
Copy-Item 'README.md' -Destination $stageDir

@'
Drop ffmpeg.exe, ffprobe.exe and fpcalc.exe in this folder.

They are found automatically. Without them the Length and Quality columns stay blank,
and fingerprinting and deep integrity checks are unavailable.

  FFmpeg (ffmpeg.exe + ffprobe.exe)  https://ffmpeg.org  (the gyan.dev builds)
  Chromaprint (fpcalc.exe)          https://acoustid.org/chromaprint

Both are free and portable - just unzip.
'@ | Out-File -FilePath (Join-Path $stageDir 'tools\PUT_TOOLS_HERE.txt') -Encoding utf8

# --- Zip -------------------------------------------------------------------------

Write-Step 'Zipping'

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

# --- Say what came out ------------------------------------------------------------

$info   = (Get-Item (Join-Path $stageDir 'MediaCatalog.App.exe')).VersionInfo
$zipMb  = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
$commit = git rev-parse --short HEAD

Write-Step 'Done'
Write-Host ('  file version     ' + $info.FileVersion)
Write-Host ('  product version  ' + $info.ProductVersion)
Write-Host ('  built from       ' + $commit)
Write-Host ('  zip              ' + $zipPath + '  (' + $zipMb + ' MB)')

# The product version embeds the commit, so this is a real check rather than a restatement
# of what was just built: it catches a stale dist\publish being re-packaged by -SkipBuild.
if ($info.ProductVersion -notlike "*$commit*") {
    Write-Warning ('The executable was built from a different commit than HEAD (' + $commit +
                   '). Re-run without -SkipBuild.')
}

Write-Host ''
Write-Host 'To publish it:' -ForegroundColor Cyan
Write-Host '  gh release create v<version> --target main --title "..." --notes-file <notes.md>'
Write-Host ('  gh release upload v<version> ' + $zipPath)
