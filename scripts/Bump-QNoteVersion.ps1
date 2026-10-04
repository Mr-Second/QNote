#Requires -Version 7.0
<#
.SYNOPSIS
    Bump the QNote version across csproj + appxmanifest in one shot.

.DESCRIPTION
    Permanent release-runbook tool (2026-10-04, born after the umpteenth manual
    bump stripped the appxmanifest's UTF-8 BOM via a Set-Content pipe — the
    byte-level pitfall from the 1.5.0 session). Replaces the version in BOTH
    release-version sources:

      - src/QNote/QNote.csproj           <Version>X</Version> + <FileVersion>X</FileVersion>
                                          (exe VERSIONINFO — the portable channel's
                                          single version source, read via FileVersionInfo)
      - src/QNote/Package.appxmanifest   Identity Version="X" (packaged channel)

    Each file keeps its ORIGINAL BOM state (the manifest ships a BOM, the csproj
    does not) — writes go through [System.IO.File]::WriteAllText with an
    encoding chosen from the file's own first bytes, never Set-Content.

    Fails loudly (non-zero exit) when a version source cannot be read or the
    literal replacement matches zero occurrences — a silent half-bumped tree is
    the one failure mode this tool exists to prevent.

.PARAMETER Version
    Target version, 1–4 dot-separated numeric parts ("1.6.0", "1.6.0.0",
    "1.6.0.1"). Short forms are zero-padded to four parts; the canonical
    four-part value is written to every source.

.PARAMETER RepoRoot
    Repository root (defaults to the parent of this script's directory).

.EXAMPLE
    PS> ./scripts/Bump-QNoteVersion.ps1 -Version 1.6.0
    Bumps everything to 1.6.0.0 and prints the suggested tag (v1.6.0).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------- normalize the target to the canonical four-part form ----------

$parts = $Version.Split('.')
if ($parts.Count -gt 4 -or $parts -notmatch '^\d+$') {
    throw "Invalid version '$Version' (expected 1-4 numeric parts, e.g. 1.6.0 or 1.6.0.0)."
}
$target = ($parts + @('0') * (4 - $parts.Count)) -join '.'

# ---------- helpers: literal, BOM-preserving file rewrite ----------

function Read-Text([string]$Path) {
    return @{
        Text   = [System.IO.File]::ReadAllText($Path)
        HasBom = [System.IO.File]::ReadAllBytes($Path)[0..2] -join ',' -eq '239,187,191'
    }
}

function Write-TextPreservingBom([string]$Path, [string]$Text, [bool]$HasBom) {
    # The 1.5.0 lesson: Set-Content/-replace pipes strip the UTF-8 BOM and the
    # manifest then diffs 4 lines instead of 1. Always write via WriteAllText.
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($HasBom))
}

function Replace-Required([string]$Text, [string]$From, [string]$To, [string]$Label) {
    $old = $Text
    $Text = $Text.Replace($From, $To)
    if ($Text -eq $old) {
        throw "No occurrence of $Label ('$From') — aborting so the tree stays consistent."
    }
    return $Text
}

# ---------- bump the two files ----------

$csproj = Join-Path $RepoRoot 'src/QNote/QNote.csproj'
$manifest = Join-Path $RepoRoot 'src/QNote/Package.appxmanifest'

# Current version comes from the csproj <Version> — the single source both
# files are expected to agree on before a bump.
$cs = Read-Text $csproj
if ($cs.Text -notmatch '<Version>(?<v>\d+\.\d+(\.\d+){0,2})</Version>') {
    throw "Cannot read the current <Version> from $csproj."
}
$current = $Matches.v
if ($current -eq $target) {
    "Already at $target — nothing to do."
    return
}

$csprojText = $cs.Text
$csprojText = Replace-Required $csprojText "<Version>$current</Version>" "<Version>$target</Version>" 'csproj <Version>'
$csprojText = Replace-Required $csprojText "<FileVersion>$current</FileVersion>" "<FileVersion>$target</FileVersion>" 'csproj <FileVersion>'
Write-TextPreservingBom $csproj $csprojText $cs.HasBom
"csproj      $current -> $target (Version + FileVersion, BOM preserved: $($cs.HasBom))"

$mf = Read-Text $manifest
$manifestText = Replace-Required $mf.Text "Version=`"$current`"" "Version=`"$target`"" 'appxmanifest Identity Version'
Write-TextPreservingBom $manifest $manifestText $mf.HasBom
"appxmanifest $current -> $target (Identity Version, BOM preserved: $($mf.HasBom))"

# ---------- suggest the release steps that follow (tag = 3-part v-form) ------

$threePart = ($target.Split('.')[0..2] -join '.')
""
"Next steps: commit the bump, then: git tag v$threePart && git push origin main v$threePart"
