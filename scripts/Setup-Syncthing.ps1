<#
.SYNOPSIS
    Sets up Syncthing on this machine for the project's binaries share.

.DESCRIPTION
    Installs Syncthing (winget), configures the shared folder with the right role,
    registers autostart, writes the UnhingedSync local config, and prints this
    machine's device ID.

    Everything project-specific is read from the shared config, Tools/unhingedsync.json
    (the pre-rename file name is still accepted, see Find-SharedConfig), so the same
    script serves any Unreal project.

    Device pairing cannot be fully automated -- two machines have to exchange device
    IDs, and each side has to accept the other. This script does everything up to
    that point and tells you exactly which ID to send where. Pass -PeerDeviceId to
    add the other side in one go once you have their ID.

    Syncthing runs in your user session rather than as a service: the config path and
    REST API stay predictable, and it can reach the same drives you can. For a
    dedicated always-on build box with no one logged in, install
    'BillStewart.SyncthingWindowsSetup' instead and re-run this with -SkipInstall.

.PARAMETER Role
    artist     - Receive Only. Gets binaries, never publishes. Ignores symbol zips.
    programmer - Send & Receive. Can build and publish.
    buildhost  - Send & Receive, plus marks this machine as a build host for the
                 build script's 'dv update' guard.

.PARAMETER PublishRoot
    Local path for the shared folder. Defaults to publishRootDefault from
    Tools/unhingedsync.json.

.PARAMETER PeerDeviceId
    Device ID of someone already in the share. Adds them and shares the folder with
    them. They still have to accept on their side.

.PARAMETER PeerIsIntroducer
    Treat the device given by -PeerDeviceId as the team's hub. Get the direction the
    right way round: marking THEM as introducer means THIS machine accepts the devices
    THEY introduce to it, so every spoke marks the one hub and the hub itself needs no
    flag. With a hub, onboarding 30 people is 30 pairings instead of 435.

    Also sets autoAcceptFolders for that one device, so the folder the hub offers is
    accepted without another manual step.

.PARAMETER FolderId
    Syncthing folder ID. Must match on every machine, so it is read from
    'syncthingFolderId' in the shared config unless you pass it here. There is no
    built-in default: a guessed ID that differs between machines syncs nothing.

.PARAMETER SkipInstall
    Assume Syncthing is already installed.

.PARAMETER NoAutostart
    Do not register the logon task.

.PARAMETER DryRun
    Print every action without changing anything.

.EXAMPLE
    ./Setup-Syncthing.ps1 -Role artist
    ./Setup-Syncthing.ps1 -Role programmer -PeerDeviceId ABCD123-...
    ./Setup-Syncthing.ps1 -Role artist -PeerDeviceId ABCD123-... -PeerIsIntroducer
    ./Setup-Syncthing.ps1 -Role buildhost -PublishRoot D:\ProjectBinaries
#>

# Windows PowerShell 5.1 cannot run this script: Set-Content has no utf8NoBOM there, and a
# BOM on the first line of .stignore silently breaks Syncthing's first ignore rule.
# Must stay AFTER the help block above; anything before it stops Get-Help finding the help.
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('artist', 'programmer', 'buildhost')]
    [string] $Role,

    [string] $PublishRoot,
    [string] $ProjectRoot,
    [string] $PeerDeviceId,
    [switch] $PeerIsIntroducer,
    [string] $FolderId,
    [switch] $SkipInstall,
    [switch] $NoAutostart,
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Stated, not inferred. The app extracts this script to a cache folder and launches it
# from there, so $PSScriptRoot reveals nothing about where the project is; it passes the
# project through the environment instead. Deriving from the script's own location is
# only correct when running in place from a project checkout.
if (-not $ProjectRoot) { $ProjectRoot = $env:UNHINGEDSYNC_PROJECT_ROOT }
if (-not $ProjectRoot) { $ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$ToolsDir = Join-Path $ProjectRoot 'Tools'

function Write-Step   { param([string] $m) Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Detail { param([string] $m) Write-Host "    $m" -ForegroundColor DarkGray }
function Write-Ok     { param([string] $m) Write-Host "    $m" -ForegroundColor Green }
function Write-Warn   { param([string] $m) Write-Host "    $m" -ForegroundColor Yellow }

function Find-Syncthing {
    <#
        winget installs Syncthing as a portable package: the exe lands under
        WinGet\Packages and a shim under WinGet\Links, and neither is guaranteed to be
        on the PATH of an already-running shell. Look in all three places so a re-run
        finds what the first run installed.
    #>
    if ($found = Find-Executable 'syncthing') { return $found }

    $shim = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\syncthing.exe'
    if (Test-Path -LiteralPath $shim) { return $shim }

    $packages = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    if (Test-Path -LiteralPath $packages) {
        $candidate = Get-ChildItem -LiteralPath $packages -Directory -Filter 'Syncthing.Syncthing*' -EA SilentlyContinue |
            ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Recurse -Filter 'syncthing.exe' -File -EA SilentlyContinue } |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    return $null
}

function Find-Executable {
    # Strict mode makes '(Get-Command x -EA SilentlyContinue).Source' throw when the
    # command is absent, which is precisely the case we need to detect.
    param([string] $Name)

    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Invoke-Action {
    <# Single choke point so -DryRun genuinely covers every mutation. #>
    param([string] $Description, [scriptblock] $Action)

    if ($DryRun) { Write-Warn "WOULD: $Description"; return $null }
    Write-Detail $Description
    return & $Action
}

function Find-SharedConfig {
    <#
        The current name comes first; the older one stays so a project synced before
        the rename still sets up, and a fleet mid-rollout does not hard-fail.
    #>
    foreach ($name in @('unhingedsync.json', 'lahoresync.json')) {
        $candidate = Join-Path $ToolsDir $name
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

function Get-ConfigValue {
    # Strict mode turns a missing property into a terminating error, and a config
    # written by an older version of the app will not have every key.
    param([object] $Object, [string] $Name)

    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name) {
        return $Object.$Name
    }
    return $null
}

function Find-UnrealProject {
    <#
        The project is whatever .uproject sits in the root: the file name differs in
        every project, so nothing here may hard-code one.
    #>
    param([string] $Path)

    $found = @(Get-ChildItem -LiteralPath $Path -Filter '*.uproject' -File -EA SilentlyContinue)
    if ($found.Count -gt 0) { return $found[0] }
    return $null
}

function Get-SafeFileNamePart {
    <#
        The project name comes out of a config file and ends up in a .lnk file name,
        so drop anything Windows will not accept in one.
    #>
    param([string] $Text)

    $invalid = [System.IO.Path]::GetInvalidFileNameChars()
    $clean   = (@($Text.ToCharArray() | Where-Object { $invalid -notcontains $_ }) -join '').Trim()
    if (-not $clean) { $clean = 'project' }
    return $clean
}

# ---------------------------------------------------------------- config

$configPath = Find-SharedConfig
if (-not $configPath) {
    throw "No shared configuration found. Expected $(Join-Path $ToolsDir 'unhingedsync.json')."
}
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json

$uprojectFile = Find-UnrealProject $ProjectRoot

# Human-facing labels only, so a config without a name is not worth failing over.
$projectName = [string] (Get-ConfigValue $config 'projectName')
if (-not $projectName) {
    $projectName = if ($uprojectFile) { $uprojectFile.BaseName } else { 'Unreal' }
}

$TaskName    = "Syncthing ($projectName)"
$folderLabel = "$projectName Binaries"
$startupName = "Syncthing ($(Get-SafeFileNamePart $projectName)).lnk"
$peerName    = "$projectName teammate"

# The ID has to be byte-identical on every machine, so there is nothing sensible to
# default it to here: the app generates one and writes it to the shared config.
if (-not $FolderId) { $FolderId = [string] (Get-ConfigValue $config 'syncthingFolderId') }
if (-not $FolderId) {
    throw @"
No Syncthing folder ID.

The folder ID must be IDENTICAL on every machine in the share - Syncthing matches
folders by ID, and two machines with different IDs sync nothing while looking fine.
It is not something to invent per machine, so there is no default.

Run the app once (it generates the ID and writes 'syncthingFolderId' to
$configPath), or pass -FolderId <id> with the value the rest of the team already uses.
"@
}

if (-not $PublishRoot) {
    # Reuse whatever this machine already chose, so running setup after the app (or
    # twice) does not silently move the share.
    $localCfg = Join-Path $env:LOCALAPPDATA 'UnhingedSync\config.local.json'
    if (Test-Path -LiteralPath $localCfg) {
        $local = Get-Content -LiteralPath $localCfg -Raw | ConvertFrom-Json
        if ($local.PSObject.Properties.Name -contains 'publishRoot' -and $local.publishRoot) {
            $PublishRoot = $local.publishRoot
        }
    }
}
if (-not $PublishRoot) { $PublishRoot = $config.publishRootDefault }

# There is no default any more: the location is per machine, so it has to be stated.
if (-not $PublishRoot) {
    throw @"
No location for the shared binaries.

Pass -PublishRoot <folder>, or run the app once and let it ask you. The folder must
NOT be inside the project: it would sit in the Diversion workspace where 'dv clean'
deletes ignored files, and it would be wiped without warning.
"@
}

$folderType = if ($Role -eq 'artist') { 'receiveonly' } else { 'sendreceive' }

Write-Host "Unhinged Sync - Syncthing setup" -ForegroundColor White
Write-Detail "Project      : $projectName"
Write-Detail "Role         : $Role"
Write-Detail "Folder       : $FolderId ($folderType)"
Write-Detail "Local path   : $PublishRoot"
if ($PeerIsIntroducer -and -not $PeerDeviceId) {
    Write-Warn '-PeerIsIntroducer does nothing without -PeerDeviceId; ignoring it.'
}
if ($DryRun) { Write-Warn 'DRY RUN - nothing will be changed.' }

# ---------------------------------------------------------------- install

Write-Step 'Syncthing'

$syncthing = Find-Syncthing
if (-not $syncthing -and -not $SkipInstall) {
    if (-not (Find-Executable 'winget')) {
        throw 'winget was not found. Install Syncthing manually from syncthing.net, then re-run with -SkipInstall.'
    }

    $wingetOutput = Invoke-Action 'winget install Syncthing.Syncthing' {
        # Deliberately NOT checking the exit code: winget reports "already installed"
        # as a failure, and this script has to be safe to re-run. Whether it worked is
        # decided below by looking for the binary, which is the thing we actually need.
        & winget install --exact --id Syncthing.Syncthing `
            --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1
    }

    # winget updates PATH for new processes, not this one.
    $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                [Environment]::GetEnvironmentVariable('PATH', 'User')
    $syncthing = Find-Syncthing

    if (-not $syncthing -and -not $DryRun) {
        throw "Syncthing could not be installed or located.`n$($wingetOutput -join "`n")"
    }
}

if (-not $syncthing) {
    if ($DryRun) { Write-Warn 'Syncthing not installed yet; continuing dry run with assumed paths.' }
    else { throw 'Syncthing is still not on PATH. Open a new terminal and re-run with -SkipInstall.' }
} else {
    Write-Ok "Found: $syncthing"
}

# ---------------------------------------------------------------- config dir

Write-Step 'Locating the Syncthing configuration'

function Resolve-SyncthingConfigDir {
    <#
        Syncthing 2.x dropped '--paths', so there is nothing to interrogate. Rather
        than guess at defaults that have moved between versions, we pick the home
        directory ourselves and pass --home on every invocation. Deterministic, and it
        keeps config.xml where the app expects to read the API key from.
    #>
    if (Test-Path -LiteralPath (Join-Path $env:APPDATA 'Syncthing\config.xml')) {
        # Honour a pre-existing v1-era install rather than orphaning its config.
        return (Join-Path $env:APPDATA 'Syncthing')
    }
    return (Join-Path $env:LOCALAPPDATA 'Syncthing')
}

$configDir  = Resolve-SyncthingConfigDir
$configXml  = Join-Path $configDir 'config.xml'
Write-Detail "Home dir: $configDir"

if (-not (Test-Path -LiteralPath $configXml)) {
    Invoke-Action 'syncthing generate (create initial config without starting)' {
        # --home sets config and data together. Passing --config alone is an error in
        # 2.x ("either both or none of --config and --data must be given").
        $output = & $syncthing generate --home $configDir 2>&1
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $configXml)) {
            # Show what Syncthing actually said. Swallowing this once already turned a
            # one-line flag mistake into a misleading "did not produce config.xml".
            throw "syncthing generate failed (exit $LASTEXITCODE):`n$($output -join "`n")"
        }
    }
} else {
    Write-Ok 'Existing configuration found; it will be updated, not replaced.'
}

# ---------------------------------------------------------------- autostart

Write-Step 'Autostart'

# A shortcut in the user's Startup folder rather than a scheduled task: registering a
# task needs privileges this script should not require (it fails with "Access is
# denied" under default policy on a non-elevated shell), and a per-user shortcut is the
# right shape for something that has to run in the user's session anyway.
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) $startupName

if ($NoAutostart) {
    Write-Warn 'Skipped (-NoAutostart).'
} elseif (Test-Path -LiteralPath $startupLink) {
    Write-Ok 'Startup shortcut already present.'
} else {
    Invoke-Action "create startup shortcut '$(Split-Path $startupLink -Leaf)'" {
        $shell    = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($startupLink)
        $shortcut.TargetPath       = $syncthing
        $shortcut.Arguments        = "serve --no-browser --home `"$configDir`""
        $shortcut.WorkingDirectory = Split-Path -Parent $syncthing
        $shortcut.Description      = "Syncthing for the $projectName binaries share"
        $shortcut.WindowStyle      = 7   # minimised
        $shortcut.Save()

        if (-not (Test-Path -LiteralPath $startupLink)) {
            throw "Could not create $startupLink"
        }
    }
}

# ---------------------------------------------------------------- start + API

Write-Step 'Starting Syncthing'

function Get-ApiCredentials {
    [xml] $xml = Get-Content -LiteralPath $configXml -Raw
    [pscustomobject]@{
        ApiKey  = $xml.configuration.gui.apikey
        Address = $xml.configuration.gui.address
    }
}

$api = if (Test-Path -LiteralPath $configXml) { Get-ApiCredentials } else { $null }
$baseUri = if ($api -and $api.Address) {
    $addr = $api.Address -replace '^0\.0\.0\.0', '127.0.0.1'
    "http://$addr"
} else { 'http://127.0.0.1:8384' }
Write-Detail "REST endpoint: $baseUri"

function Test-SyncthingUp {
    if (-not $api) { return $false }
    try {
        $null = Invoke-RestMethod -Uri "$baseUri/rest/system/ping" `
            -Headers @{ 'X-API-Key' = $api.ApiKey } -TimeoutSec 3
        return $true
    } catch { return $false }
}

if (Test-SyncthingUp) {
    Write-Ok 'Already running.'
} else {
    Invoke-Action 'start syncthing' {
        Start-Process -FilePath $syncthing `
            -ArgumentList @('serve', '--no-browser', '--home', $configDir) `
            -WindowStyle Hidden
    }
    if (-not $DryRun) {
        $deadline = (Get-Date).AddSeconds(45)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 700
            $api = Get-ApiCredentials   # the key can be generated on first start
            if (Test-SyncthingUp) { break }
        }
        if (-not (Test-SyncthingUp)) { throw "Syncthing did not answer on $baseUri within 45s." }
        Write-Ok 'Running.'
    }
}

function Invoke-Api {
    param([string] $Path, [string] $Method = 'GET', $Body)

    $params = @{
        Uri         = "$baseUri/rest/$Path"
        Method      = $Method
        Headers     = @{ 'X-API-Key' = $api.ApiKey }
        TimeoutSec  = 20
    }
    if ($null -ne $Body) {
        $params.Body        = ($Body | ConvertTo-Json -Depth 10 -Compress)
        $params.ContentType = 'application/json'
    }
    return Invoke-RestMethod @params
}

# ---------------------------------------------------------------- device id

Write-Step 'This machine'

$myId = if ($DryRun -and -not (Test-SyncthingUp)) { '<unknown in dry run>' }
        else { (Invoke-Api 'system/status').myID }
Write-Ok "Device ID: $myId"

# ---------------------------------------------------------------- folder

Write-Step "Shared folder '$FolderId'"

$null = Invoke-Action "create $PublishRoot" { New-Item -ItemType Directory -Path $PublishRoot -Force }

$deviceIds = @($myId)
if ($PeerDeviceId) { $deviceIds += $PeerDeviceId }

$folder = @{
    id      = $FolderId
    label   = $folderLabel
    path    = $PublishRoot
    type    = $folderType
    devices = @($deviceIds | Where-Object { $_ -and $_ -ne '<unknown in dry run>' } |
                 ForEach-Object { @{ deviceID = $_ } })
}

Invoke-Action "PUT folder '$FolderId' as $folderType" {
    Invoke-Api "config/folders/$FolderId" -Method 'PUT' -Body $folder | Out-Null
} | Out-Null

if ($PeerDeviceId) {
    $peerDevice = @{
        deviceID = $PeerDeviceId
        name     = $peerName
    }

    if ($PeerIsIntroducer) {
        # Direction matters and is easy to invert: 'introducer' on THIS device entry
        # means this machine accepts the devices that THAT machine introduces to it.
        # So spokes mark the hub, the hub marks nobody, and onboarding 30 people is
        # 30 pairings rather than 435.
        $peerDevice['introducer'] = $true

        # Deliberately scoped to the introducer alone: autoAcceptFolders lets that
        # device create folders on this disk, which would be reckless to hand an
        # arbitrary peer. For the hub it just saves a manual accept per teammate.
        $peerDevice['autoAcceptFolders'] = $true
    }

    $peerNote = if ($PeerIsIntroducer) { ' (introducer, auto-accepts its folders)' } else { '' }
    Invoke-Action "add peer device $PeerDeviceId$peerNote" {
        Invoke-Api 'config/devices' -Method 'POST' -Body $peerDevice | Out-Null
    } | Out-Null
}

# ---------------------------------------------------------------- stignore

if ($Role -eq 'artist') {
    Write-Step 'Ignoring symbol archives'
    Invoke-Action "write .stignore in $PublishRoot" {
        @(
            '// Debug symbols are ~780 MB per build and only useful to programmers.'
            '// Remove this line if you need to debug native code.'
            '*-symbols.zip'
        ) | Set-Content -LiteralPath (Join-Path $PublishRoot '.stignore') -Encoding utf8NoBOM
    } | Out-Null
}

# ---------------------------------------------------------------- UnhingedSync config

Write-Step 'UnhingedSync local configuration'

Invoke-Action 'write %LOCALAPPDATA%\UnhingedSync\config.local.json' {
    $dir  = Join-Path $env:LOCALAPPDATA 'UnhingedSync'
    $path = Join-Path $dir 'config.local.json'
    $null = New-Item -ItemType Directory -Path $dir -Force

    # Merge, so a projectRoot chosen on first run is not thrown away.
    $existing = if (Test-Path -LiteralPath $path) {
        Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    } else { [pscustomobject]@{} }

    $merged = @{}
    foreach ($p in $existing.PSObject.Properties) { $merged[$p.Name] = $p.Value }
    $merged['publishRoot'] = $PublishRoot
    $merged['isBuildHost'] = ($Role -eq 'buildhost')

    # The app reads this to decide who may grant introducer trust: an artist's machine
    # has no business auto-adding devices it has never seen.
    $merged['role'] = $Role
    if ($uprojectFile) {
        $merged['projectRoot'] = $ProjectRoot
    }

    $merged | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
} | Out-Null

# ---------------------------------------------------------------- next steps

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host ""
Write-Host "  Your device ID:" -ForegroundColor White
Write-Host "    $myId" -ForegroundColor Cyan
Write-Host ""

if ($PeerDeviceId) {
    Write-Host "  Added peer $PeerDeviceId. They must accept your device on their side," -ForegroundColor White
    Write-Host "  and share the '$FolderId' folder with you, before anything syncs." -ForegroundColor White
    if ($PeerIsIntroducer) {
        Write-Host "  They are marked as your introducer, so once they accept you the rest of" -ForegroundColor White
        Write-Host "  the team arrives on its own - you never pair with anyone else." -ForegroundColor White
    }
} else {
    Write-Host "  Send that ID to whoever already has the share, and ask them to add you" -ForegroundColor White
    Write-Host "  to the '$FolderId' folder. Then re-run with -PeerDeviceId <their ID>," -ForegroundColor White
    Write-Host "  or accept their request in the Syncthing UI." -ForegroundColor White
}
Write-Host ""
Write-Host "  Syncthing UI : $baseUri" -ForegroundColor DarkGray
Write-Host "  Shared folder: $PublishRoot" -ForegroundColor DarkGray
if ($Role -ne 'artist') {
    Write-Host "  You can publish builds from this machine." -ForegroundColor DarkGray
}
Write-Host ""
