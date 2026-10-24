#Requires -Version 7.0
<#
.SYNOPSIS
    Verify and archive the NativeAOT symbols produced by the same publish.
.DESCRIPTION
    Windows NativeAOT already emits QNote.pdb. This script checks its GUID/age
    against QNote.exe using DbgHelp, then packages only that matching native PDB
    and build metadata. Store archives additionally verify the executable inside
    the MSIX, because a different publish of the same source need not match.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][ValidateSet('native-aot', 'store-aot')][string]$Channel,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PackagePath,
    [string]$Commit = $env:GITHUB_SHA
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'NativeAOT PDB verification requires Windows DbgHelp.' }
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Expected a four-part package version.' }
if (-not $Commit) { $Commit = (git -C $PSScriptRoot rev-parse HEAD).Trim() }
if (-not $Commit) { throw 'A source commit is required.' }
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$exePath = Join-Path $publishRoot 'QNote.exe'
$pdbPath = Join-Path $publishRoot 'QNote.pdb'
if (-not (Test-Path -LiteralPath $exePath) -or -not (Test-Path -LiteralPath $pdbPath)) {
    throw 'QNote.exe and QNote.pdb must both exist in the same publish directory.'
}
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).FileVersion -ne $Version) {
    throw 'The published executable does not match the requested version.'
}

if (-not ('QNote.SymbolIndex' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace QNote {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SymbolIndexInfo {
        public uint SizeOfStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 261)] public string File;
        [MarshalAs(UnmanagedType.Bool)] public bool Stripped;
        public uint Timestamp;
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 261)] public string DebugFile;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 261)] public string PdbFile;
        public Guid Guid;
        public uint Signature;
        public uint Age;
    }
    public static class SymbolIndex {
        [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SymSrvGetFileIndexInfoW(string file, ref SymbolIndexInfo info, uint flags);
        public static SymbolIndexInfo Read(string path) {
            var info = new SymbolIndexInfo { SizeOfStruct = (uint)Marshal.SizeOf<SymbolIndexInfo>() };
            if (!SymSrvGetFileIndexInfoW(path, ref info, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read native symbol identity: " + path);
            return info;
        }
    }
}
'@
}
$exeIndex = [QNote.SymbolIndex]::Read($exePath)
$pdbIndex = [QNote.SymbolIndex]::Read($pdbPath)
if ($exeIndex.Guid -eq [Guid]::Empty -or $exeIndex.Guid -ne $pdbIndex.Guid -or $exeIndex.Age -ne $pdbIndex.Age) {
    throw 'QNote.pdb does not match QNote.exe (native GUID/age mismatch).'
}
$exeHash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
$metadata = [ordered]@{
    version = $Version
    channel = $Channel
    commit = $Commit
    executable = 'QNote.exe'
    executableSha256 = $exeHash
    pdb = 'QNote.pdb'
    pdbSha256 = (Get-FileHash -LiteralPath $pdbPath -Algorithm SHA256).Hash.ToLowerInvariant()
    pdbGuid = $pdbIndex.Guid.ToString('D')
    pdbAge = $pdbIndex.Age
}
if ($Channel -eq 'store-aot' -and -not $PackagePath) { throw 'Store symbols require the matching MSIX path.' }
if ($PackagePath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packageFile = (Resolve-Path -LiteralPath $PackagePath).Path
    $package = [IO.Compression.ZipFile]::OpenRead($packageFile)
    try {
        $entry = $package.GetEntry('QNote.exe')
        if (-not $entry) { throw 'The MSIX does not contain QNote.exe.' }
        $stream = $entry.Open()
        try { $packageExeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($packageExeHash -ne $exeHash) { throw 'The MSIX executable differs from the symbol-producing publish.' }
    } finally { $package.Dispose() }
    $metadata.package = [IO.Path]::GetFileName($packageFile)
    $metadata.packageSha256 = (Get-FileHash -LiteralPath $packageFile -Algorithm SHA256).Hash.ToLowerInvariant()
}

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$stage = Join-Path $outputRoot ('symbols-stage-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    Copy-Item -LiteralPath $pdbPath -Destination (Join-Path $stage 'QNote.pdb')
    $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'symbols.json') -Encoding utf8
    @"
QNote $Version - $Channel native debugging symbols

Use this QNote.pdb only with the exact QNote.exe identified in symbols.json.
Check out source commit $Commit when inspecting source lines.
Add this directory to the Visual Studio or WinDbg symbol search path.
NativeAOT requires native debugging. Optimized Release code can limit local
variable inspection and source stepping.

Store: upload the MSIX from the same workflow run's store-msix artifact.
Locally rebuilding the same version does not guarantee matching symbols.
The symbol ZIP is for manual debugging, not an automatic Store .appxsym upload.
"@ | Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Encoding utf8
    $archive = Join-Path $outputRoot "QNote_${Version}_win-x64_${Channel}_symbols.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath ($archive + '.sha256') -Encoding utf8
    Write-Output "Verified native PDB: $($pdbIndex.Guid), age $($pdbIndex.Age)"
    Write-Output "Symbols archive: $archive"
} finally {
    # The generated, absolute stage must stay beneath this invocation's output.
    if (-not $stage.StartsWith($outputRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a symbol stage outside the output directory.'
    }
    Remove-Item -LiteralPath $stage -Recurse -Force
}
