# Unhinged Sync

Distributes compiled Unreal editor binaries to a team, so nobody needs Visual Studio to
open the project. Sync the latest commit, get the matching binaries, open the editor.

Works on **any** Unreal project — point it at a folder with a `.uproject` and it
configures itself. A build is around **10 MB**, so a full ten-build history costs about
100 MB. There is no build server: anyone with a C++ toolchain can build a commit and
publish it, everyone else just downloads. Most of the team only ever presses one button.

Requires **Diversion** for version control, and **Windows**. See *Limits* at the end.

## For someone who was sent the zip

1. Unzip anywhere. It is a single `UnhingedSync.exe` — the .NET runtime and every script
   it needs are inside it. Nothing to install for the app itself.
2. Install and sign in to **Diversion**, and sync the project.
3. Install the **Unreal Engine** version the project uses (Epic Games Launcher).
4. Run `UnhingedSync.exe` and point it at your project folder — the one containing the
   `.uproject`. That's the only question it asks. Binaries go in `%USERPROFILE%\UnhingedShare`
   unless you set `UNHINGEDSYNC_PUBLISH_ROOT`.
5. Open **Sharing…**, copy your device ID, send it to whoever runs the share. When they
   add you, accept their request in the same window and answer **yes** to *"is this your
   team's hub?"*.
6. Press **Sync & Ensure Binaries**.

Steps 1–5 are once per machine. Step 6 is the daily routine.

## Onboarding 20–30 people: use a hub

Syncthing pairing is mutual, so a full mesh of 30 people is 435 pairings. That does not
scale, so **one machine is the hub** — ideally two, for redundancy.

Everyone pairs with the hub and ticks *"this machine is the team's hub"*. Syncthing's
introducer mechanism then tells them about everyone else automatically: **30 pairings
instead of 435**, and a new joiner only ever talks to one person.

The direction is easy to invert, so to be explicit: marking a device as introducer **on
your machine** means *you* accept devices *they* introduce. Spokes mark the hub; the hub
itself needs no flag. Ticking the box also lets the hub offer you new folders, which is
why it should only ever be a machine you'd trust to run the share.

Everything stays peer-to-peer — the hub is only an address book. Once introduced, people
sync builds directly with each other and the hub can be offline.

## Using the app

Each project opens in its own **tab** with independent state. `Add project…` opens
another; `Close project` forgets it without touching disk.

**Sync & Ensure Binaries** does the whole job:

1. Pulls the latest commit from Diversion.
2. Looks for published binaries matching that commit *and* your engine.
3. Found → installs them.
4. Not found → builds locally, publishes **only if the build succeeds**, then installs.
5. Can't build here (no compiler) → says so and tells you who to wait for. It never
   installs mismatched binaries.

If someone else is already building that commit you'll be told rather than duplicating the
work. Claims are best-effort hints — replication latency makes real locking impossible —
so a duplicate build is possible and harmless.

Other controls: **Refresh** re-reads state. **Fetch Selected** installs a specific commit's
binaries, warning if it doesn't match your workspace. **Build Log** opens the published
compiler output for the selected commit — that's how you find out *why* a red badge is red.
**Build Locally** compiles here instead of downloading. **Open Editor** launches the
project.

### Badges

| | Meaning |
|---|---|
| `●` green | Binaries published and ready |
| `▶` blue | Someone is building this now |
| `◔` amber | Still replicating to your machine |
| `✕` red | Build failed — open the log |
| `○` grey | Expired (retention removed the zip) |
| `–` | Nobody has built this commit |

`▶` in the left column marks your workspace commit; `✓` marks the binaries you have.

### The engine selector

Picks which installed engine to build with. The **version** a project targets is a team
decision and comes from the `.uproject`; this only chooses between installs on *your*
machine, so the choice is stored per machine.

Two guard rails, because most cross-version choices genuinely don't work:

- Choose a different version from the `.uproject` and you get a hard warning. Assets are
  versioned to the engine that wrote them — building fails, and *opening* the project with
  a newer engine can upgrade assets irreversibly.
- Choose a same-version engine whose **BuildId** differs from the team's — a source build
  sitting next to a launcher build, say — and that is caught too. Its binaries would not be
  interchangeable with everyone else's.

### Why the version banner matters

If the banner says your binaries and workspace disagree, **don't open the editor**.
Mismatched C++ and content is the one failure here that can lose work: if a `UPROPERTY`
changed between the two commits, assets serialised against the newer code can silently drop
data when resaved. Everything else fails safely — worst case you compile locally, which is
where you started.

## PDBs are never published

Measured: the binaries zip is **9.5 MB**, the symbols are **780 MB**. Replication sends
everything to every subscriber, so publishing PDBs would cost every artist ~8 GB for a
debugger they never open.

So they aren't published. Not by default, not on request, not behind a flag — the publish
path has a hard invariant that refuses any payload containing a `.pdb`.

Nothing is lost: **every local build produces its own PDBs**, so a programmer who needs to
debug presses **Build Locally** and uses theirs. And symbols could never be shared usefully
anyway. A PDB is bound to the exact DLL its linker produced — both carry a shared CodeView
GUID, and the debugger refuses a mismatch:

```
UnrealEditor-Lahore.dll  →  wants PDB dfab4233-cea4-4812-8f13-ff181ec6d85d
UnrealEditor-Lahore.pdb  →  contains that GUID at offset 20492
```

Relink and you get a new DLL *and* a new PDB with a new GUID, so a locally-built PDB can
never load against someone else's binaries. There is no version of this that works.

## The publish root

One folder, replicated by [Syncthing](https://syncthing.net) (free, open source, no licence
to worry about).

```
<publish root>/
  <Project>-<Target>-<Platform>-<Config>-<commit>.zip
  records/<commit>-<MACHINE>.json    one per build, append-only
  claims/<commit>-<MACHINE>.claim    a build in flight
  logs/<commit>-<MACHINE>.log
  App/UnhingedSync.exe               the tool distributes itself
```

There is deliberately **no shared index file**. Several people publish into the same
replicated folder, and a single mutable file would produce `sync-conflict` copies and lose
records. Every write is either uniquely named or append-only; readers enumerate
`records/*.json` and merge.

Because retention deletes zips but only the publishing machine rewrites its own record, a
reader treats *"record says success but the zip is gone"* as **expired**, and *"zip is
present but the wrong size"* as **still syncing**. Both are handled for you.

**Don't put the share inside your project.** It would sit in the Diversion workspace where
`dv clean` deletes ignored files, and it would be wiped without warning. The app refuses
that choice.

## Configuration

`Tools/unhingedsync.json` inside the project. **Commit it** — it is how the team agrees on
these values. It's generated on first open, derived from the `.uproject`.

| Key | Notes |
|---|---|
| `projectName`, `projectFile` | From the `.uproject`. |
| `editorTarget` | From `Source/*Editor.Target.cs` if present, else `<Project>Editor`. |
| `syncthingFolderId` | **Must be byte-identical on every machine** — that's how Syncthing decides two peers mean the same folder. Generated as `unhinged-<slug>-<hash>`, deterministically, so nobody has to coordinate. |
| `retainBuilds` | How many successful builds keep their zips. Older ones become `expired`. |
| `engine.expectedBuildId` | The team's engine build. Recorded on first open; update it in the same commit as an engine upgrade. |
| `toolchain.compilerVersion` | `"Latest"`, or an exact MSVC version for reproducibility. |
| `toolchain.useXge` | Incredibuild. Off by default. |

Machine-specific settings live in `%LOCALAPPDATA%\UnhingedSync\config.local.json` and are
never committed: the publish root, the known project list, and the per-project engine
choice.

UE **bans** some shipped MSVC versions outright; see `BannedVisualCppVersions` in
`Engine/Config/Windows/Windows_SDK.json` in your engine install.

## When something is wrong

```bash
UnhingedSync.exe --selftest %TEMP%\selftest.json
```

Exercises config, the project list, engine resolution, the embedded scripts, the Diversion
CLI, the record store, the install marker and build capability, then writes a JSON report.
Exit 0 means everything passed. **Run this first.**

```bash
UnhingedSync.exe --syncthing
UnhingedSync.exe --fetch
UnhingedSync.exe --fetch dv.commit.52
```

`--syncthing` reports what the app can see of the local Syncthing: device ID, peers, sync
percentage. `--fetch` installs binaries for the current commit with no GUI — it never syncs
and never builds, so it is safe to run blind.

Before blaming the pipeline for a failed compile, check the engine itself:

```bash
pwsh -File %LOCALAPPDATA%\UnhingedSync\scripts\1.0.0\Test-EngineIntegrity.ps1
```

A partially-applied engine patch leaves `Engine/Source` newer than the UnrealHeaderTool
output shipped beside it, and every `UCLASS` line number shifts. The compiler then reports
errors inside untouched engine headers that look nothing like the real cause — this exact
state once cost a day. Exit 0 is clean; exit 1 names the files and tells you to run Verify
in the Epic launcher.

**If a teammate sees behaviour you don't, compare the app version** shown in the top right
first. Mismatched copies of the tool are the single most likely explanation.

## Releasing a new version

1. Bump `<Version>` in `UnhingedSync.csproj`. It shows in the app's top-right corner, keys
   the extracted script cache, and is what the update checker compares against — a
   teammate running an old copy is visible rather than mysterious.
2. Commit, then tag and push: `git tag v1.1.0 && git push origin v1.1.0`.
3. `.github/workflows/release.yml` builds the self-contained exe, zips it, and publishes a
   GitHub Release with that tag. Nothing to run locally.

Every teammate's app polls the repo's *latest release* on launch (at most once every 4
hours) and, if it's newer, offers a one-click update: download, swap the exe, relaunch.
The repo is public specifically so this needs no token — the update check would otherwise
require distributing and rotating a GitHub PAT to everyone on the team just to read release
metadata.

To build a zip by hand instead — for local testing, or before you have a tag pushed:

```bash
dotnet publish src/UnhingedSync/UnhingedSync.csproj -c Release -o dist
Compress-Archive -Path dist/UnhingedSync.exe -DestinationPath UnhingedSync.zip -Force
```

There used to be a wrapper script here. It resolved an output folder from per-machine
config, staged to temp, and deleted a 67 KB `.pdb` — ceremony around one `dotnet publish`,
and its guessed output folder was the most confusing thing in the repo. If you want the
tool to distribute itself, publish straight into the share instead:

```bash
dotnet publish src/UnhingedSync/UnhingedSync.csproj -c Release -o "<publish root>/App"
```

Prefer publishing to `dist` and copying in if peers are actively syncing that folder — a
61 MB exe written in place is visible to Syncthing mid-write.

## Repository layout

```
src/UnhingedSync/          the WPF app
scripts/                   PowerShell, embedded into the exe at build time
Publish-UnhingedSync.ps1   packages the single-file exe
```

```bash
dotnet build src/UnhingedSync/UnhingedSync.csproj
```

The scripts under `scripts/` are compiled into the executable as embedded resources and
extracted at runtime to `%LOCALAPPDATA%\UnhingedSync\scripts\<version>`. Two consequences
worth knowing before editing them:

- **A script cannot infer which project it operates on.** It runs from that cache folder,
  so `$PSScriptRoot` says nothing about the project. The app passes `-ProjectRoot` and
  `-PublishRoot` explicitly; the scripts also honour `UNHINGEDSYNC_PROJECT_ROOT`, and only
  fall back to deriving from their own location when run in place from a checkout.
- **Editing a script does nothing until you rebuild.** The exe carries its own copy.

Nothing in this repo is specific to any game project. Per-project settings live in that
project's own `Tools/unhingedsync.json`, which the app generates and the team commits.

## Limits

- **Diversion only.** The VCS integration is isolated behind one class, but commit identity
  and ordering are Diversion-shaped. On a non-Diversion project the app starts, configures
  itself, and then fails clearly on the `dv` checks.
- **Windows only.** Paths, the engine lookup and the Startup shortcut are all Win32.
- **One engine version per project**, as the `.uproject` dictates. That's Unreal's
  constraint, not this tool's.
- **Editor binaries only.** No packaged or cooked builds — that would be a separate feature
  on the same build host.
