#Requires -Version 7.0
<#
.SYNOPSIS
    Resets this machine's Unhinged Sync setup so the first-run experience can be
    walked through again. Leaves Syncthing completely alone.

.DESCRIPTION
    Clears the per-machine state the app builds up: the known project list, the
    publish-root choice, the engine choice, and the extracted script cache. Next
    launch then behaves exactly as it would for a teammate opening the zip for the
    first time.

    Everything removed is backed up first, and -Restore puts it back.

    WHAT THIS DOES NOT TOUCH:
      * Syncthing - its config, folders, device ID and pairings all survive.
      * The published binaries in the share.
      * The project's own committed config, unless you pass -IncludeProjectConfig.

    ON -IncludeProjectConfig: that removes Tools/unhingedsync.json so you can watch
    the app generate one from scratch. Be careful -- the regenerated file gets a
    freshly DERIVED syncthingFolderId, and if your live Syncthing folder uses a
    different id (an older share will), the app and Syncthing will no longer agree on
    which folder they mean. It is the right switch for testing a brand-new project and
    the wrong one for rehearsing a teammate's setup, because a teammate receives that
    file from version control and never generates it.

.PARAMETER ProjectRoot
    Project to reset. Defaults to the one this script sits in.

.PARAMETER IncludeProjectConfig
    Also remove the project's committed config. Read the warning above.

.PARAMETER Restore
    Put the most recent backup back and exit.

.EXAMPLE
    ./Reset-Ftue.ps1
    ./Reset-Ftue.ps1 -IncludeProjectConfig
    ./Reset-Ftue.ps1 -Restore
#>
[CmdletBinding()]
param(
    [string] $ProjectRoot,
    [switch] $IncludeProjectConfig,
    [switch] $Restore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ToolsDir  = Split-Path -Parent $PSScriptRoot
if (-not $ProjectRoot) { $ProjectRoot = Split-Path -Parent $ToolsDir }

$StateDir   = Join-Path $env:LOCALAPPDATA 'UnhingedSync'
$LocalConfig = Join-Path $StateDir 'config.local.json'
$ScriptCache = Join-Path $StateDir 'scripts'
$BackupRoot  = Join-Path $StateDir 'ftue-backups'

function Write-Step   { param([string] $m) Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Detail { param([string] $m) Write-Host "    $m" -ForegroundColor DarkGray }
function Write-Ok     { param([string] $m) Write-Host "    $m" -ForegroundColor Green }
function Write-Warn   { param([string] $m) Write-Host "    $m" -ForegroundColor Yellow }

function Get-ProjectConfigPath {
    foreach ($name in @('unhingedsync.json', 'lahoresync.json')) {
        $candidate = Join-Path $ToolsDir $name
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

# ---------------------------------------------------------------- restore

if ($Restore) {
    Write-Step 'Restoring the most recent backup'

    if (-not (Test-Path -LiteralPath $BackupRoot)) { throw "No backups in $BackupRoot" }

    $latest = Get-ChildItem -LiteralPath $BackupRoot -Directory |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $latest) { throw "No backups in $BackupRoot" }

    Write-Detail "From: $($latest.FullName)"

    $savedLocal = Join-Path $latest.FullName 'config.local.json'
    if (Test-Path -LiteralPath $savedLocal) {
        $null = New-Item -ItemType Directory -Path $StateDir -Force
        Copy-Item -LiteralPath $savedLocal -Destination $LocalConfig -Force
        Write-Ok 'config.local.json restored'
    }

    $savedProject = Get-ChildItem -LiteralPath $latest.FullName -Filter '*sync.json' |
        Where-Object { $_.Name -ne 'config.local.json' } | Select-Object -First 1
    if ($savedProject) {
        Copy-Item -LiteralPath $savedProject.FullName `
            -Destination (Join-Path $ToolsDir $savedProject.Name) -Force
        Write-Ok "$($savedProject.Name) restored to the project"
    }

    Write-Host "`nRestored." -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------- reset

Write-Host 'Unhinged Sync - reset first-run experience' -ForegroundColor White
Write-Detail "Project : $ProjectRoot"
Write-Detail "State   : $StateDir"

$stamp  = (Get-Date).ToString('yyyyMMdd-HHmmss')
$backup = Join-Path $BackupRoot $stamp
$null   = New-Item -ItemType Directory -Path $backup -Force

Write-Step 'Backing up'
Write-Detail $backup

if (Test-Path -LiteralPath $LocalConfig) {
    Copy-Item -LiteralPath $LocalConfig -Destination $backup -Force
    Write-Ok 'config.local.json'
} else {
    Write-Warn 'config.local.json was already absent'
}

$projectConfig = Get-ProjectConfigPath
if ($IncludeProjectConfig -and $projectConfig) {
    Copy-Item -LiteralPath $projectConfig -Destination $backup -Force
    Write-Ok (Split-Path $projectConfig -Leaf)
}

Write-Step 'Clearing per-machine state'

if (Test-Path -LiteralPath $LocalConfig) {
    Remove-Item -LiteralPath $LocalConfig -Force
    Write-Ok 'Forgot the project list, publish root and engine choice'
}

if (Test-Path -LiteralPath $ScriptCache) {
    # Regenerated from inside the exe on next launch; clearing it proves that works.
    #
    # Careful: this script is itself embedded in the exe, so it may well be RUNNING from
    # the very folder being deleted. Deleting the file out from under the interpreter is
    # asking for trouble, so leave our own copy behind and let the next launch overwrite
    # it, which it does unconditionally.
    $selfDir = $null
    if ($PSCommandPath) { $selfDir = Split-Path -Parent $PSCommandPath }

    $runningFromCache = $selfDir -and (
        [System.IO.Path]::GetFullPath($selfDir).TrimEnd('\') -like
        ([System.IO.Path]::GetFullPath($ScriptCache).TrimEnd('\') + '*'))

    if ($runningFromCache) {
        Get-ChildItem -LiteralPath $ScriptCache -Recurse -Force -File |
            Where-Object { $_.FullName -ne $PSCommandPath } |
            Remove-Item -Force -ErrorAction SilentlyContinue
        Write-Ok 'Cleared the extracted script cache (kept this script, which is in use)'
    } else {
        Remove-Item -LiteralPath $ScriptCache -Recurse -Force
        Write-Ok 'Cleared the extracted script cache'
    }
}

if ($IncludeProjectConfig) {
    if ($projectConfig) {
        $oldFolderId = ''
        try {
            $parsed = Get-Content -LiteralPath $projectConfig -Raw | ConvertFrom-Json
            if ($parsed.PSObject.Properties.Name -contains 'syncthingFolderId') {
                $oldFolderId = [string] $parsed.syncthingFolderId
            }
        } catch { }

        Remove-Item -LiteralPath $projectConfig -Force
        Write-Ok "Removed $(Split-Path $projectConfig -Leaf) - the app will generate a new one"

        if ($oldFolderId) {
            Write-Warn "Its syncthingFolderId was '$oldFolderId'."
            Write-Warn "The regenerated file will DERIVE a new id. If Syncthing still has a folder"
            Write-Warn "called '$oldFolderId', the two will no longer refer to the same folder."
            Write-Warn "Either put the old id back into the new file, or update the folder in Syncthing."
        }
    } else {
        Write-Warn 'The project had no config to remove'
    }
}

Write-Step 'Left untouched, deliberately'
Write-Detail 'Syncthing: config, folders, device id and pairings all intact'
Write-Detail 'The published binaries in the share'
if (-not $IncludeProjectConfig) {
    Write-Detail "The project's committed config (pass -IncludeProjectConfig to remove it too)"
}

Write-Host ""
Write-Host 'Done. On next launch the app will:' -ForegroundColor Green
Write-Host '  1. ask which project folder to open' -ForegroundColor White
Write-Host '  2. ask where to keep the shared binaries' -ForegroundColor White
Write-Host '  3. re-extract its scripts' -ForegroundColor White
if ($IncludeProjectConfig) {
    Write-Host '  4. generate a fresh project config' -ForegroundColor White
}
Write-Host ""
Write-Host "Undo with:  ./Reset-Ftue.ps1 -Restore" -ForegroundColor DarkGray
Write-Host ""
