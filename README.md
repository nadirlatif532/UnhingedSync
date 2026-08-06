# Unhinged Sync

Unhinged Sync distributes compiled Unreal editor binaries to a team, so that nobody needs
Visual Studio installed just to open the project. You sync the latest commit, the tool
fetches the matching binaries, and you open the editor. One button does all of it.

It works on **any** Unreal project. Point it at a folder containing a `.uproject` and it
configures itself.

## The problem this solves

On a C++ Unreal project, the editor will not open until the project's C++ has been
compiled for the exact commit you are on. That normally means every artist, designer and
animator needs Visual Studio, a working toolchain, and 20 to 40 minutes whenever a
programmer touches code.

Unhinged Sync makes compiling one person's job. Anyone with a toolchain can build a commit
and publish the result. Everyone else downloads it in a few seconds. A build is roughly
10 MB, so keeping the last ten builds costs about 100 MB of disk.

There is no build server and nothing to host. The binaries travel over
[Syncthing](https://syncthing.net), which is free, open source, peer to peer, and needs no
account.

## How it works

1. A programmer presses **Sync & Ensure Binaries**. No binaries exist for the current
   commit, so the tool compiles them locally and publishes a zip into a shared folder.
2. Syncthing replicates that folder to everyone on the team.
3. An artist presses the same button. Binaries already exist, so the tool installs them
   and never compiles anything.

The shared folder is the only moving part. There is no database and no server.

## Requirements

| | |
|---|---|
| Windows | Paths, engine lookup and the startup shortcut are all Windows specific. |
| [Diversion](https://www.diversion.dev) | Version control. Commit identity comes from `dv`. |
| Unreal Engine | Installed via the Epic Games Launcher, matching the version the project targets. |
| PowerShell 7 | Only needed to **build** or to run Syncthing setup. Not needed to download binaries. The app offers to install it for you. |
| Visual Studio with "Game development with C++" | Only needed to build. Artists do not need it. |

Windows PowerShell 5.1, the version preinstalled on Windows, is **not** sufficient for
building. The scripts refuse to run under it rather than failing halfway through.

## Setup

### If someone sent you the zip

1. Unzip anywhere. It is a single `UnhingedSync.exe`. The .NET runtime and every script it
   needs are inside it, so there is nothing to install for the app itself.
2. Install [Diversion](https://www.diversion.dev), sign in, and sync the project.
3. Install the Unreal Engine version the project uses, through the Epic Games Launcher.
4. Run `UnhingedSync.exe`. It asks one question: where your project folder is, meaning the
   folder that contains the `.uproject`.
5. Open **Sharing...**, copy your device ID, and send it to whoever runs the share. When
   they add you, come back to this window, accept their request, and answer **yes** to
   *"is this your team's hub?"*.
6. Press **Sync & Ensure Binaries**.

Steps 1 to 5 happen once per machine. Step 6 is the daily routine.

If you are going to compile, also say yes when the app offers to install PowerShell 7.

### If you are setting the team up

Someone has to publish the first build and act as the hub that everyone else pairs with.
That machine should be a programmer's machine or a dedicated build box.

1. Do the six steps above.
2. Open **Sharing...** and run the Syncthing setup, choosing **Programmer** or
   **Dedicated build machine**.
3. Press **Sync & Ensure Binaries**. Since nothing is published yet, this compiles and
   publishes the first build.
4. Commit `Tools/unhingedsync.json` to Diversion. This is important: it is how the whole
   team agrees on the editor target and, critically, on the Syncthing folder ID.
5. Send teammates the zip and your device ID.

### Onboarding 20 to 30 people: use a hub

Syncthing pairing is mutual, so a full mesh of 30 people means 435 separate pairings. That
does not scale, so **one machine acts as the hub**, ideally two for redundancy.

Everyone pairs with the hub and ticks *"this machine is the team's hub"*. Syncthing's
introducer mechanism then tells them about everyone else automatically. That is 30 pairings
instead of 435, and a new joiner only ever exchanges IDs with one person.

The direction is easy to get backwards, so to be explicit: marking a device as introducer
**on your machine** means *you* accept the devices *they* introduce. Spokes mark the hub.
The hub itself needs no flag.

Ticking that box also lets the hub offer you new folders, which is why only machines set up
as programmer or build host are allowed to tick it. Everything stays peer to peer. The hub
is only an address book, and once you have been introduced you sync builds directly with
everyone else even when the hub is offline.

## Daily usage

**Sync & Ensure Binaries** is the whole job:

1. Pulls the latest commit from Diversion.
2. Looks for published binaries matching that commit and your engine.
3. If found, installs them.
4. If not found, compiles locally and publishes, but only if the build succeeds.
5. If this machine cannot build, it says so and tells you who to wait for. It never
   installs mismatched binaries.

If someone else is already building that commit you are told, rather than duplicating 30
minutes of work. Claims are best effort hints, because replication latency makes real
locking impossible, so a duplicate build is possible and harmless.

### The other buttons

| Button | What it does |
|---|---|
| **Refresh** | Re-reads Diversion and the shared folder. |
| **Sharing...** | Syncthing pairing: your device ID, invite a teammate, accept requests. |
| **Manage Binaries...** | See every published build and free up disk space. |
| **Fetch Selected** | Installs a specific commit's binaries. Warns first if it does not match your workspace. |
| **Build Locally** | Compiles here instead of downloading. This is how you get debuggable symbols. |
| **Build Log** | Opens the published compiler output for the selected commit. This is how you find out why a red badge is red. |
| **Copy Diagnostics** | Copies version, engine, paths and recent log to the clipboard. Paste this when reporting a problem. |
| **Open Editor** | Launches the project. |
| **Check for updates** | Asks GitHub for a newer release now. |

### Reading the commit list

| Badge | Meaning |
|---|---|
| Green dot | Binaries published and ready to install |
| Blue triangle | Someone is building this commit right now |
| Amber part-circle | Still replicating to your machine |
| Red cross | The build failed. Open the Build Log |
| Grey circle | Expired, meaning retention removed the zip |
| Plain dash | Nobody has built this commit |

In the left column, a triangle marks your workspace commit and a tick marks the binaries
you currently have installed.

### Why the version banner matters

If the banner says your binaries and your workspace disagree, **do not open the editor**.

Mismatched C++ and content is the one failure here that can actually lose work. If a
`UPROPERTY` changed between the two commits, assets serialised against the newer code can
silently drop data when they are resaved. Everything else fails safely, and the worst case
is that you compile locally, which is where you started.

## Roles

The Syncthing setup asks what kind of machine this is. The choice sets how the shared
folder behaves.

| Role | Folder type | Can publish | Notes |
|---|---|---|---|
| Artist or designer | receive only | No | Receives builds. Pick this if you do not compile C++. |
| Programmer | send and receive | Yes | Can build and publish for the team. |
| Dedicated build machine | send and receive | Yes | Also lets the build script sync the workspace unattended. Only for a machine nobody works in. |

The role is recorded per machine and is not committed. It affects two things beyond
publishing: only programmers and build hosts may grant introducer trust, and only they can
delete shared builds.

## Managing disk space

Builds accumulate. Two things clean them up.

**Automatically.** After every successful publish, retention keeps the newest
`retainBuilds` builds (ten by default) and deletes the older zips. Their rows become grey
"expired" entries.

**On demand.** **Manage Binaries...** lists every build with its size, and lets you delete
specific ones or keep only the newest few.

One thing to understand before using it: the shared folder is replicated, so **deleting a
build there removes it for the whole team**, not just for you. Anyone who still needs that
commit would have to compile it again. The window states this before it deletes anything,
and it warns you if you are about to delete the build you currently have installed or one
that somebody is building right now.

On an artist machine the delete controls are **disabled**, and this is deliberate rather
than an oversight. A receive only folder does not send local deletions to anyone, so
deleting there would free space on that one machine, leave the folder permanently flagged
as out of sync, and offer a Revert button that downloads everything straight back. Ask a
programmer or the build host to clean up instead.

## Keeping the tool updated

The app checks GitHub for a newer release when it starts, at most once every four hours. If
one exists you are offered a one click update: it downloads in the background, swaps the
executable, and restarts. **Check for updates** does the same on demand and also tells you
when you are already current.

If you decline a version you will not be asked about it again, but you will be offered the
next one. Use **Check for updates** if you change your mind.

A copy of the tool running from inside the shared folder does not self update, because
Syncthing already distributes the new executable there. Whoever publishes the tool updates
that copy.

If a teammate reports behaviour you do not see, **compare the version in the top right
corner first**. Mismatched copies of the tool are the single most likely explanation.

## PDBs are never published

Measured on a real project: the binaries zip is 9.5 MB and the symbols are 780 MB.
Replication sends everything to every subscriber, so publishing PDBs would cost every
artist roughly 8 GB for a debugger they never open.

So they are not published. Not by default, not on request, not behind a flag. The publish
path has a hard invariant that refuses any payload containing a `.pdb`.

Nothing is lost by this, for two reasons. First, **every local build produces its own
PDBs**, so a programmer who needs to debug presses **Build Locally** and uses theirs.
Second, symbols could never be usefully shared anyway. A PDB is bound to the exact DLL its
linker produced, both carry a matching CodeView GUID, and the debugger refuses a mismatch:

```
UnrealEditor-Lahore.dll  ->  wants PDB dfab4233-cea4-4812-8f13-ff181ec6d85d
UnrealEditor-Lahore.pdb  ->  contains that GUID at offset 20492
```

Relinking produces a new DLL *and* a new PDB with a new GUID, so a locally built PDB can
never load against somebody else's binaries. There is no version of this that works.

## Troubleshooting

Start here:

```bash
UnhingedSync.exe --selftest %TEMP%\selftest.json
```

This exercises config, the project list, engine resolution, the embedded scripts, the
Diversion CLI, the record store, the install marker and build capability, then writes a
JSON report. Exit code 0 means everything passed.

| Symptom | Likely cause |
|---|---|
| "Binary share not reachable" | Syncthing is not running, or the folder path does not exist. |
| "This app and Syncthing disagree about where builds live" | Two different folders are in play. Fix `publishRoot` in `config.local.json`, or re-point the folder in Syncthing. |
| The build list is empty but teammates see builds | Usually the same path disagreement, or Syncthing has not finished the first sync. |
| "Syncthing is running but will not let this app in" | Syncthing is using a different config directory than the one the app found. Open the Syncthing UI to see which one is live. |
| A red badge | Open **Build Log** for the actual compiler output. |
| Compile fails with errors inside untouched engine headers | Run the engine integrity check below. |

Other headless modes:

```bash
UnhingedSync.exe --syncthing
UnhingedSync.exe --fetch
UnhingedSync.exe --fetch dv.commit.52
```

`--syncthing` reports what the app can see of the local Syncthing: device ID, peers, sync
percentage. `--fetch` installs binaries for the current commit with no window. It never
syncs and never builds, so it is safe to run blind.

### Before blaming the tool for a failed compile

```bash
pwsh -File "%LOCALAPPDATA%\UnhingedSync\scripts\<version>\Test-EngineIntegrity.ps1"
```

Replace `<version>` with the version shown in the app's top right corner.

A partially applied engine patch leaves `Engine/Source` newer than the UnrealHeaderTool
output shipped beside it, and every `UCLASS` line number shifts. The compiler then reports
errors inside engine headers nobody touched, looking nothing like the real cause. This
exact state once cost a full day. Exit code 0 is clean. Exit code 1 names the files and
tells you to run Verify in the Epic Games Launcher.

## The shared folder

One folder, replicated by Syncthing.

```
<publish root>/
  <Project>-<Target>-<Platform>-<Config>-<commit>.zip
  records/<commit>-<MACHINE>.json    one per build, append only
  claims/<commit>-<MACHINE>.claim    a build in flight
  logs/<commit>-<MACHINE>.log
  App/UnhingedSync.exe               optional, so the tool can distribute itself
```

There is deliberately **no shared index file**. Several people publish into the same
replicated folder, and a single mutable file would produce `sync-conflict` copies and lose
records. Every write is either uniquely named or append only, and readers enumerate
`records/*.json` and merge them.

Because retention deletes zips but only the publishing machine ever rewrites its own
record, a reader treats *"the record says success but the zip is gone"* as **expired**, and
*"the zip is present but the wrong size"* as **still syncing**. Both cases are handled for
you.

**Do not put the shared folder inside your project.** It would sit in the Diversion
workspace, where `dv clean` deletes ignored files, and it would be wiped without warning.
The app will not choose such a location itself.

## Configuration

### Committed, shared by the team

`Tools/unhingedsync.json` inside the project. **Commit this file.** It is generated on
first open, derived from the `.uproject`.

| Key | Notes |
|---|---|
| `projectName`, `projectFile` | Taken from the `.uproject`. |
| `editorTarget` | From `Source/*Editor.Target.cs` if present, otherwise `<Project>Editor`. |
| `syncthingFolderId` | **Must be byte identical on every machine.** It is how Syncthing decides two peers mean the same folder. Generated deterministically as `unhinged-<slug>-<hash>` so nobody has to coordinate. |
| `retainBuilds` | How many successful builds keep their zips. Older ones become expired. |
| `engine.expectedBuildId` | The team's engine build. Recorded on first open. Update it in the same commit as an engine upgrade. |
| `engine.enforceBuildIdMatch` | Whether a mismatched engine build blocks installing. |
| `toolchain.compilerVersion` | `"Latest"`, or an exact MSVC version for reproducibility. |
| `toolchain.useXge` | Incredibuild. Off by default. |

Unreal **bans** some shipped MSVC versions outright. See `BannedVisualCppVersions` in
`Engine/Config/Windows/Windows_SDK.json` in your engine install.

### Per machine, never committed

`%LOCALAPPDATA%\UnhingedSync\config.local.json` holds the publish root, the known project
list, this machine's role, the per project engine choice, and update check bookkeeping.

Environment overrides, useful for scripting: `UNHINGEDSYNC_PROJECT_ROOT` and
`UNHINGEDSYNC_PUBLISH_ROOT`.

### The engine selector

This picks which installed engine to build with. The *version* a project targets is a team
decision and comes from the `.uproject`, so this only chooses between installs on **your**
machine, and the choice is stored per machine.

There are two guard rails, because most cross version choices genuinely do not work:

- Choosing a different version from the `.uproject` gives you a hard warning. Assets are
  versioned to the engine that wrote them, so building fails, and *opening* the project
  with a newer engine can upgrade assets irreversibly.
- Choosing a same version engine whose **BuildId** differs from the team's, such as a
  source build sitting next to a launcher build, is also caught. Its binaries would not be
  interchangeable with everyone else's.

## For maintainers

### Repository layout

```
src/UnhingedSync/           the WPF app
scripts/                    PowerShell, embedded into the exe at build time
.github/workflows/          the release pipeline
```

```bash
dotnet build src/UnhingedSync/UnhingedSync.csproj
```

The scripts under `scripts/` are compiled into the executable as embedded resources and
extracted at runtime to `%LOCALAPPDATA%\UnhingedSync\scripts\<version>`. Two consequences
are worth knowing before editing them:

- **A script cannot infer which project it operates on.** It runs from that cache folder,
  so `$PSScriptRoot` says nothing about the project. The app passes `-ProjectRoot` and
  `-PublishRoot` explicitly. The scripts also honour `UNHINGEDSYNC_PROJECT_ROOT`, and only
  fall back to deriving from their own location when run in place from a checkout.
- **Editing a script does nothing until you rebuild.** The exe carries its own copy.

Every script begins with `#Requires -Version 7.0`. Windows PowerShell 5.1 has no
`utf8NoBOM` encoding, and the JSON records here must be BOM free for the app to parse them,
so 5.1 has to refuse at parse time rather than fail halfway through a build.

Nothing in this repository is specific to any one game project. Per project settings live
in that project's own `Tools/unhingedsync.json`, which the app generates and the team
commits.

### Cutting a release

1. Bump `<Version>` in `src/UnhingedSync/UnhingedSync.csproj` to match the tag you are
   about to push.
2. Commit.
3. Tag and push:

```bash
git tag v1.1.0 && git push origin v1.1.0
```

The workflow in `.github/workflows/release.yml` derives the version from the tag, stamps it
into the build, verifies the built executable reports it, zips the result and publishes a
GitHub Release. Every teammate's app is then offered that release automatically.

The tag is the source of truth for the version, and CI fails the release if the built
executable disagrees with it. That guard exists because a release whose exe under reports
its own version offers itself to the whole team forever, since installing it never changes
what they report.

The repository is public specifically so the update check needs no token. The alternative
was distributing and rotating a GitHub access token to everybody on the team purely to read
release metadata.

To build a zip by hand, for local testing or before any tag exists:

```bash
dotnet publish src/UnhingedSync/UnhingedSync.csproj -c Release -o dist
Compress-Archive -Path dist/UnhingedSync.exe -DestinationPath UnhingedSync.zip -Force
```

**The first release has to be delivered by hand.** Anyone running a build made before the
updater existed has no update code in their copy, so they need one zip sent to them
directly. After that, updates are automatic.

### Verifying a change

```bash
UnhingedSync.exe --selftest    # services against the real machine
UnhingedSync.exe --uitest      # builds every window and forces a layout pass
```

`--uitest` exists because parsing a XAML template and *applying* one are different things.
A template can load fine from the resource dictionary and still throw when a control uses
it, and nothing else in the headless modes instantiates a window. It catches theming
regressions that `--selftest` cannot see.

Neither of them proves a feature works. They prove the plumbing is connected.

## Limits

- **Diversion only.** The version control integration is isolated behind one class, but
  commit identity and ordering are Diversion shaped. On a non Diversion project the app
  starts, configures itself, and then fails clearly on the `dv` checks.
- **Windows only.** Paths, the engine lookup and the startup shortcut are all Win32.
- **One engine version per project**, as the `.uproject` dictates. That is Unreal's
  constraint, not this tool's.
- **Editor binaries only.** No packaged or cooked builds. That would be a separate feature
  on the same build host.
- **No automatic refresh.** The commit list updates when you press Refresh or run an
  action, not on a timer. If a teammate is building something you need, you have to check
  back.
