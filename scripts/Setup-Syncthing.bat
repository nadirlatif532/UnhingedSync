@echo off
setlocal EnableDelayedExpansion
title Unhinged Sync - Syncthing setup

rem Double-clickable wrapper around Setup-Syncthing.ps1. With no arguments it asks
rem which kind of machine this is; with arguments it passes them straight through,
rem e.g.  Setup-Syncthing.bat -Role artist -PeerDeviceId ABCD123-...

set "SCRIPT=%~dp0Setup-Syncthing.ps1"
if not exist "%SCRIPT%" (
    echo ERROR: Setup-Syncthing.ps1 not found next to this file.
    echo Expected: %SCRIPT%
    goto :fail
)

rem PowerShell 7 if present, otherwise Windows PowerShell.
set "PS=pwsh"
where pwsh >nul 2>&1 || set "PS=powershell"

if not "%~1"=="" (
    "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
    goto :done
)

echo.
echo   Unhinged Sync - Syncthing setup
echo   =============================
echo.
echo   This installs Syncthing, joins the shared binaries folder, and sets
echo   this machine up to receive editor builds.
echo.
echo   What is this machine?
echo.
echo     [1]  Artist / designer
echo          Receives builds. Never publishes. Skips the 780 MB symbol
echo          archives. Pick this if you do not compile C++.
echo.
echo     [2]  Programmer
echo          Receives and publishes. Can build binaries for the team.
echo.
echo     [3]  Dedicated build machine
echo          Same as programmer, plus allows the build script to sync the
echo          workspace unattended. Only for a box nobody works in.
echo.
echo     [4]  Show me what it would do, change nothing  (dry run, artist)
echo.
echo     [Q]  Quit
echo.

:choose
set "ROLE="
set "EXTRA="
set /p "PICK=  Choose [1/2/3/4/Q]: "

if /i "!PICK!"=="Q" goto :cancelled
if "!PICK!"=="1" set "ROLE=artist"
if "!PICK!"=="2" set "ROLE=programmer"
if "!PICK!"=="3" set "ROLE=buildhost"
if "!PICK!"=="4" (
    set "ROLE=artist"
    set "EXTRA=-DryRun"
)
if not defined ROLE (
    echo   Please enter 1, 2, 3, 4 or Q.
    goto :choose
)

echo.
set /p "PEER=  Device ID of someone already in the share (Enter to skip): "
if not defined PEER goto :peerdone
set "EXTRA=!EXTRA! -PeerDeviceId !PEER!"

echo.
echo   Is that device the team's hub - the one machine everybody pairs with?
echo   If it is, it can introduce you to the rest of the team, so you only ever
echo   exchange IDs with the hub. Answer N for an ordinary teammate.
echo.
:askhub
set "HUB="
set /p "HUB=  Is that the hub? [Y/N]: "
if /i "!HUB!"=="Y" (
    set "EXTRA=!EXTRA! -PeerIsIntroducer"
    goto :peerdone
)
if /i "!HUB!"=="N" goto :peerdone
echo   Please enter Y or N.
goto :askhub

:peerdone
echo.
echo   Where should the shared binaries live on this machine?
echo   Roughly 100 MB for ten builds. Do NOT pick a folder inside the project -
echo   it would sit in the Diversion workspace, where 'dv clean' deletes it.
echo.
:askroot
set "ROOT="
set /p "ROOT=  Folder: "
if not defined ROOT (
    echo   A folder is required - there is no default, this is per machine.
    goto :askroot
)
set "EXTRA=!EXTRA! -PublishRoot "!ROOT!""

echo.
echo   Running: -Role !ROLE! !EXTRA!
echo.

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Role !ROLE! !EXTRA!

:done
if errorlevel 1 goto :fail
echo.
echo   Setup finished.
echo.
pause
exit /b 0

:cancelled
echo.
echo   Cancelled - nothing was changed.
echo.
pause
exit /b 0

:fail
echo.
echo   Setup did not complete. Nothing further was changed.
echo.
pause
exit /b 1
