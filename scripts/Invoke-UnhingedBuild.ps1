<#
.SYNOPSIS
    Builds the project's editor binaries and publishes them for the team to fetch.

.DESCRIPTION
    Any machine with a matching engine and a working toolchain can build and
    publish; there is no longer a single build host that owns the publish root.

    The publish root is the folder Syncthing replicates, so every write has to be
    conflict-free. Each build appends exactly ONE new file named after the
    (commit, machine) pair and never edits a file another machine owns. There is
    no index.json any more -- readers enumerate records/*.json and merge.

    Publish root layout:

        <root>/
          <project>-<target>-<platform>-<config>-<commit>.zip
          records/<commit>-<MACHINE>.json     one per (commit, machine), append-only
          claims/<commit>-<MACHINE>.claim     in-flight build, best effort, deleted on exit
          logs/<commit>.log

    The payload file list is derived from UnrealBuildTool's own .target manifest
    (BuildProducts filtered to $(ProjectDir)) rather than a hand-written glob,
    so it cannot drift out of sync with what the build actually produced.

    PDBs stay local. Every build produces them and none of them are ever
    published: the binaries zip measures 9.5 MB and the PDBs 780 MB, and
    Syncthing replicates whatever lands in the publish root to every subscriber
    whether or not they own a debugger. A programmer who needs to debug builds
    the commit locally and uses the PDBs that build leaves in Binaries\.

.PARAMETER NoSync
    Skip 'dv update'. Builds whatever the workspace currently has.

.PARAMETER Clean
    Force a full rebuild rather than an incremental one.

.PARAMETER Publish
    Write to the publish root (zip, log, record, retention). Off by default:
    compiling without publishing is the common case on a dev box.

.PARAMETER DryRun
    Do everything except write to the publish root. Wins over -Publish.

.EXAMPLE
    ./Invoke-UnhingedBuild.ps1 -Publish
    ./Invoke-UnhingedBuild.ps1 -NoSync -DryRun
#>

# Windows PowerShell 5.1 cannot run this script: Set-Content has no utf8NoBOM there, and
# the JSON records written below must be BOM-free for the app to parse them. Left to itself
# 5.1 fails partway through a build with a parameter-binding error that looks like a project
# problem. This makes it refuse before running a single line instead.
#
# Placement matters: this must come AFTER the help block above, not before it. PowerShell
# stops recognising comment-based help for a script if anything, including #Requires,
# precedes it, which silently turns Get-Help into bare auto-generated syntax.
#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $NoSync,
    [switch] $Clean,
    [switch] $Publish,
    [switch] $DryRun,
    [string] $ConfigPath,
    [string] $PublishRoot,
    [string] $ProjectRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---------------------------------------------------------------- paths / config

# The project must be stated, not inferred. The app ships this script inside its own
# executable and extracts it to a cache folder, so $PSScriptRoot says nothing at all
# about where the project is. Deriving from the script's location only works when the
# script is being run in place from a project checkout, which is the fallback below.
if (-not $ProjectRoot) { $ProjectRoot = $env:UNHINGEDSYNC_PROJECT_ROOT }
if (-not $ProjectRoot) {
    $ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
$ToolsDir = Join-Path $ProjectRoot 'Tools'

if (-not $ConfigPath) {
    foreach ($name in @('unhingedsync.json', 'lahoresync.json')) {
        $candidate = Join-Path $ToolsDir $name
        if (Test-Path -LiteralPath $candidate) { $ConfigPath = $candidate; break }
    }
}
if (-not $ConfigPath -or -not (Test-Path -LiteralPath $ConfigPath)) {
    throw @"
No configuration found for the project at:
    $ProjectRoot

Expected $(Join-Path $ToolsDir 'unhingedsync.json').

If you are running this script directly, pass -ProjectRoot <project folder>, or
-ConfigPath <file>. The app always passes both.
"@
}
$Config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json

function Resolve-PublishRoot {
    param([string] $Override)

    if ($Override)                             { return $Override }
    if ($env:UNHINGEDSYNC_PUBLISH_ROOT)          { return $env:UNHINGEDSYNC_PUBLISH_ROOT }

    $localCfg = Join-Path $env:LOCALAPPDATA 'UnhingedSync\config.local.json'
    if (Test-Path -LiteralPath $localCfg) {
        $local = Get-Content -LiteralPath $localCfg -Raw | ConvertFrom-Json
        if ($local.PSObject.Properties.Name -contains 'publishRoot' -and $local.publishRoot) {
            return $local.publishRoot
        }
    }
    return $Config.publishRootDefault
}

$PublishRootResolved = Resolve-PublishRoot -Override $PublishRoot
$ProjectFile         = Join-Path $ProjectRoot $Config.projectFile

# -DryRun always wins: it exists so a machine can rehearse a publish without
# touching the replicated folder at all.
$WillPublish = ($Publish -and -not $DryRun)

# ---------------------------------------------------------------- logging

$script:LogLines = [System.Collections.Generic.List[string]]::new()

function Write-Step {
    param([string] $Message, [string] $Colour = 'Cyan')
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $Message
    $script:LogLines.Add($line)
    Write-Host $line -ForegroundColor $Colour
}

function Write-Detail {
    param([string] $Message)
    $script:LogLines.Add($Message)
    Write-Host "    $Message" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- json helpers

function Get-JsonProperty {
    <#
        Strict mode turns a missing property into a terminating error, and record
        files written by an older script (or a newer one) will not always have
        every field. Read them through here.
    #>
    param([object] $Object, [string] $Name, $Default = $null)

    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name) {
        return $Object.$Name
    }
    return $Default
}

function Set-JsonProperty {
    param([object] $Object, [string] $Name, $Value)

    # Assigning an existing NoteProperty keeps its position in the JSON; only a
    # genuinely new field has to be appended.
    if ($Object.PSObject.Properties.Name -contains $Name) {
        $Object.$Name = $Value
    } else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function Write-JsonAtomic {
    param([string] $Path, [object] $Value)

    # Write-then-move so a Syncthing peer replicating this folder never picks up
    # a half-written file (it would replicate the partial bytes happily).
    $tmpPath = "$Path.tmp"
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $tmpPath -Encoding utf8NoBOM
    Move-Item -LiteralPath $tmpPath -Destination $Path -Force
}

# ---------------------------------------------------------------- engine

function Resolve-EngineDir {
    <#
        Maps the .uproject's EngineAssociation to an install directory. Launcher
        engines use a version string ("5.8") and are registered in
        LauncherInstalled.dat and/or the Epic registry key.
    #>
    $uproject   = Get-Content -LiteralPath $ProjectFile -Raw | ConvertFrom-Json
    $assoc      = $uproject.EngineAssociation
    Write-Detail "EngineAssociation = '$assoc'"

    $regPath = "HKLM:\SOFTWARE\EpicGames\Unreal Engine\$assoc"
    if (Test-Path $regPath) {
        $installed = (Get-ItemProperty -Path $regPath -ErrorAction SilentlyContinue).InstalledDirectory
        if ($installed -and (Test-Path -LiteralPath $installed)) {
            return $installed
        }
    }

    $datPath = Join-Path $env:ProgramData 'Epic\UnrealEngineLauncher\LauncherInstalled.dat'
    if (Test-Path -LiteralPath $datPath) {
        $dat = Get-Content -LiteralPath $datPath -Raw | ConvertFrom-Json
        $match = $dat.InstallationList |
            Where-Object { $_.AppName -eq "UE_$assoc" } |
            Select-Object -First 1
        if ($match -and (Test-Path -LiteralPath $match.InstallLocation)) {
            return $match.InstallLocation
        }
        # Fall back to any artifact registered against this engine version.
        $match = $dat.InstallationList |
            Where-Object { $_.AppVersion -like "$assoc*" -and (Test-Path -LiteralPath $_.InstallLocation) } |
            Select-Object -First 1
        if ($match) { return $match.InstallLocation }
    }

    throw "Could not resolve an install directory for engine '$assoc'. Set it explicitly in %LOCALAPPDATA%\UnhingedSync\config.local.json as 'engineDir'."
}

function Get-EngineBuildId {
    param([string] $EngineDir)

    $versionFile = Join-Path $EngineDir 'Engine\Build\Build.version'
    $v = Get-Content -LiteralPath $versionFile -Raw | ConvertFrom-Json

    # UBT stamps modules with a BuildId derived from CompatibleChangelist, which
    # is why binaries survive patch bumps (5.8.0 / 5.8.1 -> same BuildId).
    $buildId = if ($v.CompatibleChangelist -and $v.CompatibleChangelist -ne 0) {
        [string] $v.CompatibleChangelist
    } else {
        [string] $v.Changelist
    }

    [pscustomobject]@{
        BuildId              = $buildId
        Version              = "$($v.MajorVersion).$($v.MinorVersion).$($v.PatchVersion)"
        Changelist           = $v.Changelist
        CompatibleChangelist = $v.CompatibleChangelist
        BranchName           = $v.BranchName
    }
}

# ---------------------------------------------------------------- diversion

function Invoke-Dv {
    param([string[]] $DvArgs, [int] $TimeoutSeconds = 3600)

    $output = & dv @DvArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "dv $($DvArgs -join ' ') failed with exit code $LASTEXITCODE`n$output"
    }
    return $output
}

function Get-WorkspaceCommit {
    $id = (Invoke-Dv @('status', '--commit-id-only')).Trim()
    if ($id -notmatch '^dv\.commit\.\d+$') {
        throw "Unexpected commit id from 'dv status --commit-id-only': '$id'"
    }
    return $id
}

function Get-CommitMeta {
    param([string] $CommitId)

    $lines   = Invoke-Dv @('log', '-n', '1', '--date', 'iso')
    $author  = ($lines | Where-Object { $_ -match '^Author:' } | Select-Object -First 1)
    $date    = ($lines | Where-Object { $_ -match '^Date:'   } | Select-Object -First 1)
    $message = ($lines | Where-Object { $_ -match '^\t' }      | Select-Object -First 1)

    $email = if ($author -match '<([^>]+)>') { $Matches[1] } else { '' }

    [pscustomobject]@{
        AuthorEmail = $email
        DateUtc     = if ($date -match 'Date:\s*(\S+)') { $Matches[1] } else { '' }
        Message     = if ($message) { $message.Trim() } else { '' }
    }
}

# ---------------------------------------------------------------- build

function Invoke-EditorBuild {
    param([string] $EngineDir)

    $buildBat = Join-Path $EngineDir 'Engine\Build\BatchFiles\Build.bat'
    if (-not (Test-Path -LiteralPath $buildBat)) {
        throw "Build.bat not found at $buildBat"
    }

    # Engine paths routinely contain spaces ("UE 5.8"); pass every path as a
    # single quoted argument and never build a command string by concatenation.
    $buildArgs = @(
        $Config.editorTarget
        $Config.platform
        $Config.configuration
        "-Project=$ProjectFile"
        '-WaitMutex'
    )

    # Pin the toolchain. UE 5.8 bans several shipped MSVC versions outright, and
    # letting UBT pick means one machine and another dev box can select different
    # compilers from the same source tree.
    if ($Config.PSObject.Properties.Name -contains 'toolchain') {
        if ($Config.toolchain.compilerVersion) {
            $buildArgs += "-CompilerVersion=$($Config.toolchain.compilerVersion)"
        }
        if (-not $Config.toolchain.useXge) {
            $buildArgs += '-NoXGE'
        }
    }

    if ($Clean) { $buildArgs += '-Clean' }

    Write-Step "Compiling $($Config.editorTarget) $($Config.platform) $($Config.configuration)"
    Write-Detail "$buildBat $($buildArgs -join ' ')"

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $buildBat @buildArgs 2>&1 | ForEach-Object {
        $script:LogLines.Add([string] $_)
        Write-Host "    $_" -ForegroundColor DarkGray
    }
    $exit = $LASTEXITCODE
    $sw.Stop()

    [pscustomobject]@{
        ExitCode        = $exit
        DurationSeconds = [int] $sw.Elapsed.TotalSeconds
    }
}

function Get-TargetManifestPath {
    <#
        UBT's manifest for the editor target. Everything that needs to know what
        the build produced resolves it through here, so nothing can disagree about
        which file counts as "you have built this".
    #>
    return (Join-Path $ProjectRoot "Binaries\$($Config.platform)\$($Config.editorTarget).target")
}

function Get-PayloadStem {
    <#
        The one place that decides what a published zip is called, so the record,
        the log line and the file on disk cannot drift apart.
    #>
    param([string] $CommitShort)

    return "$($Config.projectName)-$($Config.editorTarget)-$($Config.platform)-$($Config.configuration)-$CommitShort"
}

function Get-PayloadFiles {
    <#
        Ground truth for what to ship: UBT's own BuildProducts list, narrowed to
        the project (engine products come from the team's launcher install) and
        with symbol files dropped unconditionally -- PDBs are never published, so
        there is no switch to turn them back on. Assert-NoSymbolsInPayload checks
        the result of this filter again just before anything is written.
    #>
    $targetFile = Get-TargetManifestPath
    if (-not (Test-Path -LiteralPath $targetFile)) {
        throw "Build manifest missing: $targetFile"
    }

    $manifest = Get-Content -LiteralPath $targetFile -Raw | ConvertFrom-Json
    $products = @($manifest.BuildProducts) |
        Where-Object { $_.Path -like '*$(ProjectDir)*' } |
        Where-Object { $_.Type -ne 'SymbolFile' }

    $files = foreach ($p in $products) {
        $relative = ($p.Path -replace '^\$\(ProjectDir\)[\\/]*', '') -replace '/', '\'
        $full     = Join-Path $ProjectRoot $relative
        if (Test-Path -LiteralPath $full) {
            [pscustomobject]@{
                RelativePath = $relative -replace '\\', '/'
                FullPath     = $full
                Type         = $p.Type
                Length       = (Get-Item -LiteralPath $full).Length
            }
        } else {
            Write-Detail "WARNING: manifest lists a product that is not on disk: $relative"
        }
    }

    # The .target manifest itself is what the client uses to verify a payload.
    $selfRelative = "Binaries/$($Config.platform)/$($Config.editorTarget).target"
    if (-not ($files | Where-Object { $_.RelativePath -eq $selfRelative })) {
        $files += [pscustomobject]@{
            RelativePath = $selfRelative
            FullPath     = $targetFile
            Type         = 'RequiredResource'
            Length       = (Get-Item -LiteralPath $targetFile).Length
        }
    }

    return @($files)
}

# This is an invariant and not a convention because the publish root is not a
# folder anyone reviews: Syncthing pushes whatever appears in it to every
# teammate's machine within minutes, unprompted. A 780 MB PDB that slips into the
# payload is therefore on a dozen disks before the first person notices, and the
# only way to take it back is to delete it and hope every peer has caught up. A
# convention in Get-PayloadFiles holds until somebody edits Get-PayloadFiles;
# this refuses to write the zip at all.
function Assert-NoSymbolsInPayload {
    param([object[]] $Files)

    # Belt and braces. The manifest Type is the real signal, but a malformed or
    # hand-edited .target can mistype a PDB as something else, so the extension is
    # checked too. -ieq spells out the case-insensitivity both comparisons rely on:
    # 'symbolfile' and '.PDB' have to be caught as well.
    $offenders = @($Files | Where-Object {
        $_.Type -ieq 'SymbolFile' -or
        [System.IO.Path]::GetExtension([string] $_.RelativePath) -ieq '.pdb'
    })
    if ($offenders.Count -eq 0) { return }

    # Cap the listing: a manifest that has gone wrong wholesale would otherwise
    # bury the explanation under a few hundred paths.
    $shown = @($offenders | Select-Object -First 10)
    $lines = @($shown | ForEach-Object { "  $($_.RelativePath)" })
    if ($offenders.Count -gt $shown.Count) {
        $lines += "  +$($offenders.Count - $shown.Count) more"
    }

    throw @"
PDBs must never be published. The binaries zip is built from UnrealBuildTool's
.target manifest with symbol files filtered out, so finding one here means that
filter has been bypassed or the manifest is malformed.

Your DLLs are identical with or without PDBs, so nothing needs rebuilding --
the payload just must not contain them. Remove the symbol entries and retry.

Symbol files found in the payload ($($offenders.Count)):
$($lines -join "`n")
"@
}

function New-PayloadZip {
    param(
        [object[]] $Files,
        [string]   $ZipPath
    )

    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }

    $stream  = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew)
    $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in $Files) {
            # Explicit entry names keep the archive project-relative with forward
            # slashes, so the client can map entries onto local paths verbatim.
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $f.FullPath, $f.RelativePath,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally {
        $archive.Dispose()
        $stream.Dispose()
    }

    return (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ---------------------------------------------------------------- records
#
# Append-only store. One file per (commit, machine):
#
#     records/<commitShort>-<MACHINE>.json
#
# Two machines publishing the same commit produce two sibling files rather than
# two conflicting edits to one index, which is what Syncthing turns into
# .sync-conflict-* copies and silently lost records. Nothing in this script ever
# writes a record file belonging to a machine other than this one.

function Get-RecordsDir {
    param([string] $Root)
    return (Join-Path $Root 'records')
}

function Get-RecordFileName {
    param([string] $CommitShort)
    return "$CommitShort-$($env:COMPUTERNAME).json"
}

function Write-BuildRecord {
    param([string] $Root, [object] $Record)

    $dir = Get-RecordsDir -Root $Root
    $null = New-Item -ItemType Directory -Path $dir -Force

    $path = Join-Path $dir (Get-RecordFileName -CommitShort $Record.commitShort)
    Write-JsonAtomic -Path $path -Value ([pscustomobject] $Record)
    return $path
}

function Read-BuildRecords {
    <#
        Every record in the store, wrapped with the file it came from so callers
        can rewrite their own and only their own.
    #>
    param([string] $Root)

    $dir = Get-RecordsDir -Root $Root
    if (-not (Test-Path -LiteralPath $dir)) { return @() }

    # Enumerate with -LiteralPath (publish roots live under paths with spaces)
    # and match the extension exactly, so a .json.tmp mid-replication is skipped.
    $files = Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Extension -eq '.json' }

    $records = foreach ($file in $files) {
        try {
            [pscustomobject]@{
                File   = $file.FullName
                Name   = $file.Name
                Record = (Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)
            }
        } catch {
            # A half-replicated or hand-edited record must not fail a build.
            Write-Detail "WARNING: skipping unreadable record $($file.Name): $($_.Exception.Message)"
        }
    }
    return @($records)
}

# ---------------------------------------------------------------- claims
#
# claims/<commitShort>-<MACHINE>.claim marks a build in flight. This is a hint,
# not a lock: Syncthing propagation is measured in seconds to minutes, so two
# machines can always start the same commit before either sees the other's claim.
# That is tolerable -- two builds of one commit produce equivalent binaries and
# equivalent zips, so the cost is wasted CPU, not a wrong result.

$script:ClaimMaxAgeMinutes = 90

function Get-ClaimsDir {
    param([string] $Root)
    return (Join-Path $Root 'claims')
}

function Get-ClaimFiles {
    param([string] $Root, [string] $CommitShort)

    $dir = Get-ClaimsDir -Root $Root
    if (-not (Test-Path -LiteralPath $dir)) { return @() }

    $files = Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Extension -eq '.claim' }
    if ($CommitShort) {
        $files = $files | Where-Object { $_.Name -like "$CommitShort-*" }
    }
    return @($files)
}

function Get-ClaimMachine {
    param([System.IO.FileInfo] $File, [string] $CommitShort)

    # "<commitShort>-<MACHINE>.claim". Machine names may themselves contain '-',
    # so strip the known prefix rather than splitting on the separator.
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($File.Name)
    if ($CommitShort -and $stem.StartsWith("$CommitShort-")) {
        return $stem.Substring($CommitShort.Length + 1)
    }
    return $stem
}

function Get-ClaimAgeMinutes {
    param([System.IO.FileInfo] $File)

    # Prefer the claim's own startedUtc; fall back to the file timestamp for a
    # claim we cannot parse (truncated, mid-replication, hand-made).
    $started = $null
    try {
        $claim = Get-Content -LiteralPath $File.FullName -Raw | ConvertFrom-Json
        $value = Get-JsonProperty -Object $claim -Name 'startedUtc'
        if ($value -is [datetime]) {
            # ConvertFrom-Json already turns an ISO-8601 string into a DateTime.
            # Take it as-is: formatting it back to a string drops the Kind and
            # the reparse then shifts the value by the local UTC offset.
            $started = $value
        } elseif ($value) {
            # AssumeUniversal because the field is a UTC timestamp by contract;
            # an explicit 'Z' or offset in the string still wins.
            $started = [datetimeoffset]::Parse(
                [string] $value,
                [cultureinfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::AssumeUniversal).UtcDateTime
        }
    } catch {
        $started = $null
    }
    if ($null -eq $started) { $started = $File.LastWriteTimeUtc }

    return ([datetime]::UtcNow - $started.ToUniversalTime()).TotalMinutes
}

function Test-ForeignClaim {
    <#
        Warn if another machine looks to be building this commit right now.
        Returns $true when one was found; the caller builds anyway.
    #>
    param([string] $Root, [string] $CommitShort)

    $found = $false
    foreach ($file in (Get-ClaimFiles -Root $Root -CommitShort $CommitShort)) {
        $machine = Get-ClaimMachine -File $file -CommitShort $CommitShort
        if ($machine -eq $env:COMPUTERNAME) { continue }

        $age = Get-ClaimAgeMinutes -File $file
        if ($age -ge $script:ClaimMaxAgeMinutes) { continue }

        Write-Step "WARNING: '$machine' claimed commit $CommitShort $([int] $age) minute(s) ago and has not released it yet." 'Yellow'
        Write-Detail "Claims are best effort only - Syncthing latency makes real locking impossible. Building anyway; a duplicate build of the same commit is wasteful but harmless because it produces equivalent binaries."
        $found = $true
    }
    return $found
}

function Remove-StaleOwnClaim {
    <#
        Opportunistic cleanup of claims this machine failed to release (power cut,
        killed process). Never touches another machine's file, however stale.
    #>
    param([string] $Root)

    foreach ($file in (Get-ClaimFiles -Root $Root -CommitShort '')) {
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
        if (-not $stem.EndsWith("-$($env:COMPUTERNAME)")) { continue }
        if ((Get-ClaimAgeMinutes -File $file) -lt $script:ClaimMaxAgeMinutes) { continue }

        Write-Detail "Claim: dropping our own stale claim $($file.Name)"
        Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue
    }
}

function New-BuildClaim {
    param([string] $Root, [string] $CommitShort)

    $dir = Get-ClaimsDir -Root $Root
    $null = New-Item -ItemType Directory -Path $dir -Force

    Remove-StaleOwnClaim -Root $Root

    $path = Join-Path $dir "$CommitShort-$($env:COMPUTERNAME).claim"
    Write-JsonAtomic -Path $path -Value ([ordered]@{
        machine    = $env:COMPUTERNAME
        startedUtc = (Get-Date).ToUniversalTime().ToString('o')
        pid        = $PID
    })
    Write-Detail "Claim: $([System.IO.Path]::GetFileName($path))"
    return $path
}

function Remove-BuildClaim {
    param([string] $Path)

    if (-not $Path) { return }
    if (-not (Test-Path -LiteralPath $Path)) { return }

    Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    Write-Detail "Claim released: $([System.IO.Path]::GetFileName($Path))"
}

# ---------------------------------------------------------------- retention

function Invoke-Retention {
    <#
        Keep zips for the newest $Keep successful commits and delete the rest,
        working over records/*.json instead of an in-memory index.
    #>
    param([string] $Root, [int] $Keep)

    $records = Read-BuildRecords -Root $Root
    if ($records.Count -eq 0) { return }

    # IMPORTANT, and the client depends on it: a record claiming 'success' is only
    # a live build while its zip is still on disk. Retention deletes zips for the
    # whole team but may only rewrite THIS machine's record files, so another
    # machine's record can keep saying 'success' long after its zip is gone --
    # rewriting it here is exactly what would produce .sync-conflict-* copies.
    # Every reader must therefore treat "status == success but zip file absent"
    # as expired. This function applies the same rule so that the number of zips
    # it keeps matches the number of builds a reader considers available.
    $live = @($records |
        Where-Object { (Get-JsonProperty -Object $_.Record -Name 'status') -eq 'success' } |
        Where-Object {
            $zipName = Get-JsonProperty -Object $_.Record -Name 'zipName'
            $zipName -and (Test-Path -LiteralPath (Join-Path $Root $zipName))
        })

    # One commit is one build for retention purposes even when several machines
    # built it: their zips share a name, so they are the same artifact.
    $groups = @($live |
        Group-Object -Property { [int] (Get-JsonProperty -Object $_.Record -Name 'commitOrdinal' -Default 0) } |
        Sort-Object -Property { [int] $_.Name } -Descending)

    if ($groups.Count -le $Keep) { return }

    foreach ($group in ($groups | Select-Object -Skip $Keep)) {
        foreach ($entry in $group.Group) {
            $rec         = $entry.Record
            $commitShort = [string] (Get-JsonProperty -Object $rec -Name 'commitShort' -Default $group.Name)

            $zipName = [string] (Get-JsonProperty -Object $rec -Name 'zipName')
            if ($zipName) {
                $zipPath = Join-Path $Root $zipName
                if (Test-Path -LiteralPath $zipPath) {
                    Write-Detail "Retention: removing $zipName"
                    Remove-Item -LiteralPath $zipPath -Force
                }
            }

            $logName = [string] (Get-JsonProperty -Object $rec -Name 'logName' -Default "logs/$commitShort.log")
            $logPath = Join-Path $Root $logName
            if ($logName -and (Test-Path -LiteralPath $logPath)) {
                Write-Detail "Retention: removing $logName"
                Remove-Item -LiteralPath $logPath -Force
            }

            # Only ever rewrite our own record file (see the note above).
            if ((Get-JsonProperty -Object $rec -Name 'builtBy') -ne $env:COMPUTERNAME) { continue }

            Set-JsonProperty -Object $rec -Name 'status'  -Value 'expired'
            Set-JsonProperty -Object $rec -Name 'zipName' -Value $null
            Write-JsonAtomic -Path $entry.File -Value $rec
            Write-Detail "Retention: marked $($entry.Name) expired"
        }
    }
}

# ---------------------------------------------------------------- main

$overallSw  = [System.Diagnostics.Stopwatch]::StartNew()
$claimPath  = $null
$recordPath = $null

Write-Step "UnhingedSync build" 'Green'
Write-Detail "Project root : $ProjectRoot"
Write-Detail "Publish root : $PublishRootResolved$(if ($DryRun) { '  (DRY RUN - nothing will be written)' } elseif (-not $Publish) { '  (-Publish not set - nothing will be written)' })"
Write-Detail "Machine      : $env:COMPUTERNAME"

$engineDir = Resolve-EngineDir
$engine    = Get-EngineBuildId -EngineDir $engineDir
Write-Step "Engine $($engine.Version) (BuildId $($engine.BuildId), CL $($engine.Changelist))"
Write-Detail "Location: $engineDir"

if ($Config.engine.enforceBuildIdMatch -and $Config.engine.expectedBuildId -and
    $engine.BuildId -ne $Config.engine.expectedBuildId) {
    throw @"
Engine BuildId mismatch.
  This host : $($engine.BuildId)  (UE $($engine.Version))
  Expected  : $($Config.engine.expectedBuildId)

Binaries built here would not load for the team. Either install the matching
engine version, or update 'engine.expectedBuildId' in Tools/unhingedsync.json if
the whole team is moving to this engine together.
"@
}

if (-not $NoSync) {
    # 'accept-incoming' discards local changes. That is correct for a dedicated
    # build host whose workspace nobody edits, and destructive anywhere else --
    # so require an explicit opt-in and refuse to run over a dirty workspace.
    $isBuildHost = $false
    $localCfg = Join-Path $env:LOCALAPPDATA 'UnhingedSync\config.local.json'
    if (Test-Path -LiteralPath $localCfg) {
        $local = Get-Content -LiteralPath $localCfg -Raw | ConvertFrom-Json
        if ($local.PSObject.Properties.Name -contains 'isBuildHost') {
            $isBuildHost = [bool] $local.isBuildHost
        }
    }

    if (-not $isBuildHost) {
        throw @"
Refusing to sync: this machine is not marked as a build host.

'dv update' here would discard uncommitted local changes. If this really is the
build host, create %LOCALAPPDATA%\UnhingedSync\config.local.json containing:

    { "isBuildHost": true, "publishRoot": "C:\\ProjectBinaries" }

To build the current workspace without syncing, pass -NoSync.
"@
    }

    Write-Step "Checking workspace is clean before sync"
    $dirty = @(Invoke-Dv @('status', '--no-limit') |
        Where-Object { $_ -match '^\s*[AMDR]\s+\S' })
    if ($dirty.Count -gt 0) {
        throw "Workspace has $($dirty.Count) uncommitted change(s); refusing to overwrite them. Commit or reset first, or pass -NoSync."
    }

    Write-Step "Syncing workspace (dv update)"
    Invoke-Dv @('update', '--conflict_resolution', 'accept-incoming') | ForEach-Object { Write-Detail $_ }
} else {
    Write-Step "Skipping sync (-NoSync)"
}

$commitId    = Get-WorkspaceCommit
$commitShort = $commitId -replace '^dv\.commit\.', ''
$commitMeta  = Get-CommitMeta -CommitId $commitId
$branch      = (Invoke-Dv @('branch-name')).Trim()

Write-Step "Building commit $commitId on '$branch'"
Write-Detail "$($commitMeta.AuthorEmail) - $($commitMeta.Message)"

try {
    # Claim before compiling so peers have the longest possible window to see it.
    # Reading the claims folder is harmless even when we are not publishing, so
    # warn about a concurrent build either way.
    if (Test-Path -LiteralPath $PublishRootResolved) {
        $null = Test-ForeignClaim -Root $PublishRootResolved -CommitShort $commitShort
    }
    if ($WillPublish) {
        $claimPath = New-BuildClaim -Root $PublishRootResolved -CommitShort $commitShort
    }

    $build = Invoke-EditorBuild -EngineDir $engineDir

    $record = [ordered]@{
        commitId             = $commitId
        commitShort          = $commitShort
        commitOrdinal        = [int] $commitShort
        branch               = $branch
        commitMessage        = $commitMeta.Message
        commitAuthor         = $commitMeta.AuthorEmail
        commitDateUtc        = $commitMeta.DateUtc
        status               = if ($build.ExitCode -eq 0) { 'success' } else { 'failed' }
        target               = $Config.editorTarget
        platform             = $Config.platform
        configuration        = $Config.configuration
        engineBuildId        = $engine.BuildId
        engineVersion        = $engine.Version
        engineChangelist     = $engine.Changelist
        zipName              = $null
        zipBytes             = 0
        zipSha256            = $null
        fileCount            = 0
        builtUtc             = (Get-Date).ToUniversalTime().ToString('o')
        builtBy              = $env:COMPUTERNAME
        buildDurationSeconds = $build.DurationSeconds
        logName              = "logs/$commitShort-$($env:COMPUTERNAME).log"
    }

    if ($build.ExitCode -ne 0) {
        Write-Step "BUILD FAILED (exit $($build.ExitCode)) after $($build.DurationSeconds)s" 'Red'
    } else {
        Write-Step "Compiled in $($build.DurationSeconds)s" 'Green'

        # @() so a hypothetical single-file payload still answers .Count under
        # strict mode -- that has bitten this file three times already.
        $payload = @(Get-PayloadFiles)
        $bytes   = ($payload | Measure-Object -Property Length -Sum).Sum
        Write-Step "Payload: $($payload.Count) files, $([math]::Round($bytes / 1MB, 1)) MB"

        $stem    = Get-PayloadStem -CommitShort $commitShort
        $zipName = "$stem.zip"

        if ($WillPublish) {
            $null = New-Item -ItemType Directory -Path $PublishRootResolved -Force
            $null = New-Item -ItemType Directory -Path (Join-Path $PublishRootResolved 'logs') -Force

            $zipPath = Join-Path $PublishRootResolved $zipName

            # Last gate before anything reaches the replicated folder.
            Assert-NoSymbolsInPayload -Files $payload

            Write-Step "Publishing $zipName"
            $sha = New-PayloadZip -Files $payload -ZipPath $zipPath

            $record.zipName   = $zipName
            $record.zipBytes  = (Get-Item -LiteralPath $zipPath).Length
            $record.zipSha256 = $sha
            $record.fileCount = $payload.Count
            Write-Detail "$([math]::Round($record.zipBytes / 1MB, 1)) MB compressed, sha256 $($sha.Substring(0,12))..."
        } else {
            $record.fileCount = $payload.Count
            $why = if ($DryRun) { 'Dry run' } else { 'Not publishing (-Publish not set)' }
            Write-Step "${why}: would publish $zipName" 'Yellow'
        }
    }

    if ($WillPublish) {
        $null = New-Item -ItemType Directory -Path (Join-Path $PublishRootResolved 'logs') -Force

        # Per-machine log name, so two machines building the same commit cannot
        # collide on it. Retention resolves the log from each record's own
        # 'logName' field rather than reconstructing it, so nothing else changes.
        $script:LogLines | Set-Content -LiteralPath (Join-Path $PublishRootResolved $record.logName) -Encoding utf8NoBOM

        $recordPath = Write-BuildRecord -Root $PublishRootResolved -Record $record
        Write-Step "Record written: $([System.IO.Path]::GetFileName($recordPath))"

        Invoke-Retention -Root $PublishRootResolved -Keep ([int] $Config.retainBuilds)
    }
} finally {
    # Release our claim on every path out of here: success, build failure, or a
    # thrown error. Only ever our own file.
    Remove-BuildClaim -Path $claimPath
}

$overallSw.Stop()
Write-Step "Done in $([int] $overallSw.Elapsed.TotalSeconds)s" $(if ($build.ExitCode -eq 0) { 'Green' } else { 'Red' })

if ($build.ExitCode -eq 0) {
    # Machine-readable handshake for the C# GUI, which shells out to this script
    # and parses this line. It must stay a single line and the last thing written
    # to stdout, so nothing may Write-Output after it.
    $result = [ordered]@{
        status     = $record.status
        commitId   = $record.commitId
        zipName    = $record.zipName
        zipSha256  = $record.zipSha256
        zipBytes   = $record.zipBytes
        recordPath = $recordPath
    }
    Write-Output "UNHINGEDSYNC_RESULT $($result | ConvertTo-Json -Depth 8 -Compress)"
}

exit $build.ExitCode
