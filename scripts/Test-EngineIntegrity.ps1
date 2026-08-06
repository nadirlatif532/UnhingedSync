#Requires -Version 7.0
<#
.SYNOPSIS
    Detects an internally inconsistent Unreal Engine install before a build wastes
    90 seconds producing a thousand lines of misleading compiler errors.

.DESCRIPTION
    An installed engine ships both its C++ headers and the UnrealHeaderTool output
    generated from them. UCLASS() expands to a macro whose name embeds the LINE
    NUMBER it appears on:

        #define UCLASS(...) BODY_MACRO_COMBINE(CURRENT_FILE_ID,_,__LINE__,_PROLOG)

    so UCLASS on line 215 of Package.h expands to FID_..._Package_h_215_PROLOG,
    which only exists if Package.generated.h was generated from that exact file.

    When a patch updates Engine/Source but fails to refresh
    Engine/Intermediate/Build/**/Inc, every line number shifts and the expansion
    resolves to nothing. The compiler then reports errors deep inside untouched
    engine headers -- C4430 "missing type specifier" on the UCLASS line and C2143
    on the class below it -- which look nothing like the real problem.

    This script compares the two directly and names the real cause.

.PARAMETER EngineDir
    Engine install root. Defaults to the engine the project is associated with.

.PARAMETER Sample
    Cap on headers deep-checked. 0 (the default) means no cap. Only headers newer
    than their generated counterpart are candidates, so a full scan is already
    cheap -- capping this risks skipping the very file that is broken.

.EXAMPLE
    ./Test-EngineIntegrity.ps1
    ./Test-EngineIntegrity.ps1 -Sample 0
#>
[CmdletBinding()]
param(
    [string] $EngineDir,
    [string] $ProjectRoot,
    [int]    $Sample = 0,
    [string] $Target = 'UnrealEditor',
    [string] $Platform = 'Win64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- engine location

if (-not $EngineDir) {
    # Stated, not inferred: this script is shipped inside the app and extracted to a
    # cache folder, where its own location says nothing about the project.
    if (-not $ProjectRoot) { $ProjectRoot = $env:UNHINGEDSYNC_PROJECT_ROOT }
    $projectRoot = if ($ProjectRoot) { $ProjectRoot }
                   else { Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
    $uproject    = Get-ChildItem -LiteralPath $projectRoot -Filter '*.uproject' | Select-Object -First 1
    if (-not $uproject) { throw "No .uproject found in $projectRoot" }

    $assoc   = (Get-Content -LiteralPath $uproject.FullName -Raw | ConvertFrom-Json).EngineAssociation
    $regPath = "HKLM:\SOFTWARE\EpicGames\Unreal Engine\$assoc"
    if (-not (Test-Path $regPath)) { throw "Engine '$assoc' is not registered on this machine." }

    $EngineDir = (Get-ItemProperty -Path $regPath).InstalledDirectory
}

$sourceDir = Join-Path $EngineDir 'Engine\Source'
$incDir    = Join-Path $EngineDir "Engine\Intermediate\Build\$Platform\$Target\Inc"

foreach ($dir in @($sourceDir, $incDir)) {
    if (-not (Test-Path -LiteralPath $dir)) { throw "Missing engine directory: $dir" }
}

Write-Host "Engine : $EngineDir" -ForegroundColor Cyan
$version = Get-Content -LiteralPath (Join-Path $EngineDir 'Engine\Build\Build.version') -Raw | ConvertFrom-Json
Write-Host "Version: $($version.MajorVersion).$($version.MinorVersion).$($version.PatchVersion) (CL $($version.Changelist))" -ForegroundColor Cyan

# ---------------------------------------------------------------- index generated headers

# Basenames are unique enough in practice and this avoids walking Source twice.
$generated = @{}
foreach ($file in Get-ChildItem -LiteralPath $incDir -Recurse -Filter '*.generated.h' -File) {
    $stem = $file.Name -replace '\.generated\.h$', ''
    if (-not $generated.ContainsKey($stem)) { $generated[$stem] = $file.FullName }
}
Write-Host "Indexed $($generated.Count) generated headers." -ForegroundColor DarkGray

# ---------------------------------------------------------------- compare

$checked = 0
$mismatched = [System.Collections.Generic.List[object]]::new()

# A mismatch is only possible where the source header is NEWER than the generated
# header built from it. Filtering on that first makes a complete scan cheap -- and
# sampling would be worse than useless here, since it can silently skip the one
# broken file (which is exactly what a 200-header sample did during development).
$headers = Get-ChildItem -LiteralPath $sourceDir -Recurse -Filter '*.h' -File |
    Where-Object {
        $stem = $_.Name -replace '\.h$', ''
        $generated.ContainsKey($stem) -and
        $_.LastWriteTimeUtc -gt (Get-Item -LiteralPath $generated[$stem]).LastWriteTimeUtc
    }

Write-Host "$($headers.Count) header(s) are newer than their generated counterpart." -ForegroundColor DarkGray

foreach ($header in $headers) {
    if ($Sample -gt 0 -and $checked -ge $Sample) { break }

    $stem     = $header.Name -replace '\.h$', ''
    $genPath  = $generated[$stem]
    $lines    = Get-Content -LiteralPath $header.FullName
    $declared = @()

    # __LINE__ resolves to the line where the macro invocation CLOSES, not where it
    # opens, and UCLASS specifiers frequently wrap across several lines. Balancing
    # parentheses is therefore load-bearing: keying off the opening line reports
    # false mismatches on every multi-line UCLASS in the engine.
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch '^UCLASS\s*\(') { continue }

        $depth = 0
        for ($j = $i; $j -lt $lines.Count; $j++) {
            # Strip string literals first so a ')' inside a description is ignored.
            $text = $lines[$j] -replace '"(\\.|[^"\\])*"', '""'
            $depth += ([regex]::Matches($text, '\(')).Count
            $depth -= ([regex]::Matches($text, '\)')).Count
            if ($depth -le 0) { $declared += ($j + 1); $i = $j; break }
        }
    }
    if ($declared.Count -eq 0) { continue }

    $genText = Get-Content -LiteralPath $genPath -Raw
    $fileId  = if ($genText -match '#define CURRENT_FILE_ID\s+(\S+)') { $Matches[1] } else { $null }
    if (-not $fileId) { continue }

    $checked++
    $missing = @($declared | Where-Object { $genText -notmatch "#define\s+${fileId}_$_`_PROLOG" })

    if ($missing.Count -gt 0) {
        # Report what the generated header DOES describe, so the offset is obvious.
        # @() matters: a single match is a scalar, and strict mode rejects .Count on it.
        $present = @([regex]::Matches($genText, "#define\s+$([regex]::Escape($fileId))_(\d+)_PROLOG") |
            ForEach-Object { [int] $_.Groups[1].Value })

        $mismatched.Add([pscustomobject]@{
            Header      = $header.FullName.Substring($sourceDir.Length + 1)
            SourceLines = ($missing -join ', ')
            GeneratedAt = (($present | Sort-Object) -join ', ')
            Drift       = if ($present.Count -gt 0) {
                              # A uniform offset means the file simply gained or lost
                              # lines above the declarations.
                              $offsets = @(0..([Math]::Min($missing.Count, $present.Count) - 1) |
                                  ForEach-Object {
                                      (@($missing | Sort-Object)[$_]) - (@($present | Sort-Object)[$_])
                                  })
                              if (@($offsets | Select-Object -Unique).Count -eq 1) {
                                  '{0:+#;-#;0} lines' -f $offsets[0]
                              } else { 'varies' }
                          } else { 'no PROLOG at all' }
        })
    }
}

# ---------------------------------------------------------------- verdict

Write-Host ""
Write-Host "Checked $checked UCLASS-bearing headers; $($mismatched.Count) inconsistent." `
    -ForegroundColor $(if ($mismatched.Count -eq 0) { 'Green' } else { 'Red' })

if ($mismatched.Count -eq 0) {
    Write-Host "Engine headers and generated headers agree." -ForegroundColor Green
    exit 0
}

Write-Host ""
$mismatched | Select-Object -First 12 |
    Format-Table -AutoSize -Property Header, SourceLines, GeneratedAt

Write-Host @"
This engine install is internally inconsistent: Engine/Source has been updated but
Engine/Intermediate/Build/$Platform/$Target/Inc still holds UnrealHeaderTool output
generated from the previous version. Every UCLASS line number is off, so project
code that includes these headers cannot compile -- and the errors point at engine
files that are not actually the problem.

FIX: Epic Games Launcher -> Unreal Engine -> 5.8 -> ... -> Verify

That repairs the mismatched files. A plain re-patch will not: the launcher already
believes the install is current. Re-run this script afterwards to confirm.
"@ -ForegroundColor Yellow

exit 1
