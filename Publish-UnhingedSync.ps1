<#
.SYNOPSIS
    Packages Unhinged Sync into a single self-contained folder with one launchable exe.

.DESCRIPTION
    Produces a folder containing UnhingedSync.exe and nothing else it needs -- the .NET
    runtime is bundled, so a teammate needs no SDK, no runtime install and no DLLs
    sitting beside it. Double-clicking the exe is the whole story.

    By default it publishes into <publish root>\App, which is the Syncthing-replicated
    folder, so the tool distributes itself alongside the binaries it fetches.

.PARAMETER OutputDir
    Where to write the folder. Defaults to <publish root>\App, falling back to
    %LOCALAPPDATA%\UnhingedSync\dist when the share is not reachable.

.PARAMETER Configuration
    Release (default) or Debug.

.EXAMPLE
    ./Publish-UnhingedSync.ps1
    ./Publish-UnhingedSync.ps1 -OutputDir 'D:\Share\UnhingedSync'
#>
[CmdletBinding()]
param(
    [string] $OutputDir,
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$CsProj   = Join-Path $RepoRoot 'src\UnhingedSync\UnhingedSync.csproj'

if (-not (Test-Path -LiteralPath $CsProj)) { throw "Project not found: $CsProj" }

# ---------------------------------------------------------------- output location

if (-not $OutputDir) {
    # This repo is not a game project, so there is no shared config to read a default
    # from. Fall back to whatever share this machine is configured for, then to a local
    # folder -- and -OutputDir always wins.
    $publishRoot = $env:UNHINGEDSYNC_PUBLISH_ROOT

    $localCfg = Join-Path $env:LOCALAPPDATA 'UnhingedSync\config.local.json'
    if (Test-Path -LiteralPath $localCfg) {
        $local = Get-Content -LiteralPath $localCfg -Raw | ConvertFrom-Json
        if ($local.PSObject.Properties.Name -contains 'publishRoot' -and $local.publishRoot) {
            $publishRoot = $local.publishRoot
        }
    }

    $OutputDir = if ($publishRoot -and (Test-Path -LiteralPath $publishRoot)) {
        Join-Path $publishRoot 'App'
    } else {
        Write-Host "Publish root '$publishRoot' is not reachable; writing locally instead." -ForegroundColor Yellow
        Join-Path $env:LOCALAPPDATA 'UnhingedSync\dist'
    }
}

Write-Host "Packaging Unhinged Sync ($Configuration)" -ForegroundColor Cyan
Write-Host "  Output: $OutputDir" -ForegroundColor DarkGray

# ---------------------------------------------------------------- publish

# A staging directory keeps the destination untouched until the build succeeds, so a
# failed publish never leaves the team with a half-written exe.
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "unhingedsync-publish-$PID"
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }

try {
    & dotnet publish $CsProj `
        -c $Configuration `
        -o $staging `
        --nologo `
        -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $exe = Join-Path $staging 'UnhingedSync.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "Publish produced no UnhingedSync.exe in $staging" }

    # Symbols are useful to whoever maintains the tool, not to the team running it.
    Get-ChildItem -LiteralPath $staging -Filter '*.pdb' -File | Remove-Item -Force

    @"
Unhinged Sync
===========

Double-click UnhingedSync.exe.

The .NET runtime is bundled -- nothing else to install for the tool itself.

First run asks you to point at your Lahore project folder (the one containing
Lahore.uproject) and remembers it afterwards.

You do need these installed separately, because the tool drives them:
  * Diversion  -- signed in, so 'dv' works
  * Syncthing  -- subscribed to the shared binaries folder
  * Unreal Engine 5.8 from the Epic Games Launcher

Then press "Sync & Ensure Binaries".

Full documentation: Tools/README.md in the project.
Verify a machine:   UnhingedSync.exe --selftest %TEMP%\unhingedsync-selftest.json
"@ | Set-Content -LiteralPath (Join-Path $staging 'README.txt') -Encoding utf8NoBOM

    $null = New-Item -ItemType Directory -Path $OutputDir -Force
    Get-ChildItem -LiteralPath $staging -File | Copy-Item -Destination $OutputDir -Force

    Write-Host ""
    Write-Host "Packaged:" -ForegroundColor Green
    Get-ChildItem -LiteralPath $OutputDir -File |
        ForEach-Object { "  {0,-24} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
    Write-Host ""
    Write-Host "Launch: $(Join-Path $OutputDir 'UnhingedSync.exe')" -ForegroundColor Cyan
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
