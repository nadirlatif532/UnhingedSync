# Unhinged Sync

Unhinged Sync distributes compiled Unreal editor binaries to a team, so that nobody needs
Visual Studio installed just to open the project. You sync the latest commit, the tool
fetches the matching binaries, and you open the editor. One button does all of it.

It works on **any** Unreal project. Point it at a folder containing a `.uproject`.

## The problem this solves

On a C++ Unreal project the editor will not open until the project's C++ has been compiled
for the exact commit you are on. That normally means every artist, designer and animator
needs Visual Studio, a working toolchain, and twenty to forty minutes whenever a programmer
touches code.

Unhinged Sync makes compiling one person's job. Anyone with a toolchain can build a commit
and publish it. Everyone else downloads it in a few seconds. A build is roughly 10 MB.

## How it works

1. Someone with a C++ toolchain presses **Build Locally**. It compiles the current commit
   and uploads the result to a bucket.
2. Everyone else presses **Sync & Ensure Binaries**. It pulls the latest commit and installs
   the matching binaries.
3. Nobody waits on anybody's machine being switched on.

Builds live in a Cloudflare R2 bucket, which is S3 compatible. There is no server to run, no
pairing, no peer to peer setup, and no machine that has to stay online. R2 charges nothing
for egress, and distribution is the whole workload here, so a team of thirty fits inside the
free tier with room to spare.

## Requirements

| | |
|---|---|
| Windows | Paths and the engine lookup are Windows specific. |
| [Diversion](https://www.diversion.dev) | Version control. Commit identity comes from `dv`. |
| Unreal Engine | Installed via the Epic Games Launcher, matching the version the project targets. |
| PowerShell 7 | Only needed to **build**. Not needed to download binaries. The app offers to install it. |
| Visual Studio with "Game development with C++" | Only needed to build. |

Windows PowerShell 5.1, the version preinstalled on Windows, cannot run the build scripts.
They refuse to start under it rather than failing halfway through a compile.

## Setup

### If someone sent you the zip

1. Unzip anywhere. It is a single `UnhingedSync.exe` with the .NET runtime and every script
   inside it, so there is nothing to install for the app itself.
2. Install [Diversion](https://www.diversion.dev), sign in, and **sync the project**. This
   matters: the project carries the configuration that tells the app where builds live.
3. Install the Unreal Engine version the project uses.
4. Run `UnhingedSync.exe` and point it at your project folder, the one containing the
   `.uproject`. That is the only question it asks.
5. Press **Sync & Ensure Binaries**.

That is the whole thing. There is no pairing step, no device ID to exchange, and nothing to
configure per machine. If you are going to compile, also say yes when the app offers to
install PowerShell 7.

If step 4 warns that a configuration was generated, the project has not finished syncing.
Wait for Diversion, delete the generated `Tools/unhingedsync.json`, and reopen.

### If you are setting the team up

You need a bucket and a token once, then the whole team is configured by version control.

1. **Create the bucket.** Cloudflare dashboard, then Storage & databases, then R2, then
   Create bucket. Pick a location hint near your team.
2. **Create the token.** On the R2 overview page, under Account Details, next to API Tokens,
   select Manage, then Create Account API token.
   - Permission **Object Read & Write**. Not Admin: nothing here creates or deletes buckets.
   - Scope it to **this bucket only**. The keys go in a file your whole team can read, so the
     token must not be able to reach anything else in your account.
   - Choose an **Account** token, not a User token. A User token stops working if that person
     is removed from the account, which is a poor property for a credential the team depends on.
   - Copy the **Secret Access Key** immediately. Cloudflare shows it once.
3. **Fill in `Tools/unhingedsync.json`** in the project, and commit it. See below.
4. **Set the lifecycle rules** on the bucket. Settings, then Object Lifecycle Rules.
   - `claims/` expire after **1 day**
   - everything else expires after **30 days**
5. **Verify**, then publish the first build:

```bash
UnhingedSync.exe --storagetest --write
UnhingedSync.exe --build
```

6. Send teammates the zip. They need nothing else.

Do step 4 from the dashboard rather than expecting the app to do it. Bucket configuration
needs admin level permission, and the token above is deliberately scoped to objects only.

## The storage configuration

`Tools/unhingedsync.json` inside the project. **Commit it.** It is how the team agrees on
everything below, and it is what makes a teammate's setup a single step.

```json
"storage": {
  "provider": "r2",
  "accountId": "your-cloudflare-account-id",
  "bucket": "your-bucket",
  "accessKeyId": "from the R2 API token",
  "secretAccessKey": "from the R2 API token",
  "endpointUrl": "",
  "prefix": ""
}
```

`endpointUrl` stays empty for R2, where it is derived from the account ID. Set it to point at
any other S3 compatible service. `prefix` lets one bucket hold several projects.

**This file is private and must never reach the Unhinged Sync repository, which is public.**
The repository's `.gitignore` refuses any stray copy, but the habit matters more than the guard.

### Both keys are in there on purpose

One committed file means a teammate who syncs the project is configured with nothing further
to do. The trade, stated plainly rather than left to be discovered: **anyone who can open the
project can publish and delete builds.** The storage layer enforces nothing, and the
confirmations in the app are guards against mistakes rather than against people.

That is a reasonable trade here because binaries are regenerable. The worst case is that
somebody recompiles. If you ever need real enforcement, split it into a read only token in
this file and a write token per publishing machine in `config.local.json`. Note that R2's
token permissions are coarse, so *can publish* and *can delete* are the same capability
either way.

### The rest of the file

| Key | Notes |
|---|---|
| `projectName`, `projectFile` | Taken from the `.uproject`. |
| `editorTarget` | From `Source/*Editor.Target.cs` if present, otherwise `<Project>Editor`. |
| `retainBuilds` | How many builds the manual clean up keeps. Routine cleanup is the lifecycle rule. |
| `engine.expectedBuildId` | The team's engine build. Update it in the same commit as an engine upgrade. |
| `engine.enforceBuildIdMatch` | Whether a mismatched engine build blocks installing. |
| `toolchain.compilerVersion` | `"Latest"`, or an exact MSVC version for reproducibility. |
| `toolchain.useXge` | Incredibuild. Off by default. |

Unreal **bans** some shipped MSVC versions outright. See `BannedVisualCppVersions` in
`Engine/Config/Windows/Windows_SDK.json` in your engine install.

Machine specific settings live in `%LOCALAPPDATA%\UnhingedSync\config.local.json`: the known
project list, the per project engine choice, and update check bookkeeping.

## Daily usage

**Sync & Ensure Binaries** is the whole job:

1. Pulls the latest commit from Diversion.
2. Looks for published binaries matching that commit and your engine.
3. Downloads them, verifies the checksum, and installs. It never installs mismatched binaries.
4. If none exist, it stops and tells you why. It does **not** compile.

**It will never build for you, and that is deliberate.** Compiling on a miss makes the button
succeed no matter what, which hides the failure that matters. A thirty minute compile ending
in a working editor looks like success, so a genuine problem goes unnoticed until it reaches
somebody who cannot compile at all.

So when binaries are missing you get told which situation it is: nothing published for this
commit, or the bucket could not be reached. If it is the former and you have a toolchain,
press **Build Locally**.

### The other buttons

| Button | What it does |
|---|---|
| **Refresh** | Re-reads Diversion and the bucket. |
| **Manage Binaries...** | Every published build with its size. Delete specific ones, or keep the newest few. |
| **Fetch Selected** | Installs a specific commit's binaries. Warns first if it does not match your workspace. |
| **Build Locally** | Compiles here and publishes. This is also how you get debuggable symbols. |
| **Build Log** | Downloads the published compiler output for the selected commit. How you find out why a red badge is red. |
| **Copy Diagnostics** | Puts version, engine, paths, bucket state and recent log on the clipboard. Paste when reporting a problem. |
| **Open Editor** | Launches the project. Asks for confirmation if your binaries do not match your workspace. |
| **Check for updates** | Asks GitHub for a newer release now. |

### Reading the commit list

| Badge | Meaning |
|---|---|
| Green dot | Published and ready to install |
| Blue triangle | Someone is building this commit right now |
| Red cross | The build failed. Open the Build Log |
| Grey circle | Expired. The lifecycle rule removed it |
| Plain dash | Nobody has built this commit |

A triangle in the left column marks your workspace commit, a tick marks the binaries you have
installed. Columns sort on what they mean rather than on their text, so commit numbers sort
numerically and dates chronologically.

There is no "still downloading" state, because there is no partially arrived one. A build is
in the bucket or it is not, and a download either completed and verified its checksum or left
nothing behind.

### Claims: when someone else is already building

Two people on the same commit with no binaries could both start a thirty minute compile for
identical output. So before building, a machine writes a small marker saying it is building
that commit, and everyone else is told rather than duplicating the work.

Claims never block anything. **Build Locally ignores them entirely**, so it is always the way
past one. The app also discards any claim older than 90 minutes, and the lifecycle rule
deletes them after a day, so a machine that crashed mid build stops advertising a phantom
build on its own. Messages include how long ago a build started, because a claim four minutes
old means wait and one eighty minutes old probably means that machine died.

### Why the mismatch warning matters

If your installed binaries are not from your workspace commit, **do not open the editor**.

Mismatched C++ and content is the one failure here that can lose work. If a `UPROPERTY`
changed between the two commits, assets serialised against the newer code can silently drop
data when they are resaved. Open Editor asks for confirmation and names both commits.
Everything else fails safely, and the worst case is that you compile locally.

## PDBs are not published

Measured on a real build: the binaries zip is 9.5 MB and the symbols are 780 MB.

Every local build produces its own PDBs and they stay on that machine, so a programmer who
needs to debug presses **Build Locally** and uses theirs. The publish path has a hard
invariant that refuses any payload containing a `.pdb`.

Worth being precise about why, because the reason changed. Under peer to peer replication
this was a hard constraint: everything in the shared folder went to every subscriber, so
publishing symbols would have cost every artist about 8 GB for a debugger they never open.
Downloads are on demand now, so that constraint is gone and this is a **choice** rather than
a limit. Publishing symbols for on demand download is a viable feature, and it is simply not
built yet.

What has not changed is that symbols can only ever be used with the exact DLLs they were
linked against. Both carry a matching CodeView GUID and the debugger refuses a mismatch:

```
UnrealEditor-Lahore.dll  ->  wants PDB dfab4233-cea4-4812-8f13-ff181ec6d85d
UnrealEditor-Lahore.pdb  ->  contains that GUID at offset 20492
```

Relinking produces a new DLL and a new PDB with a new GUID, so a locally built PDB can never
load against somebody else's binaries.

## What is in the bucket

```
<prefix>/
  <Project>-<Target>-<Platform>-<Config>-<commit>.zip
  records/<commit>-<MACHINE>.json
  claims/<commit>-<MACHINE>.claim
  logs/<commit>-<MACHINE>.log
```

There is exactly **one binary per commit**. The zip key carries no machine name, so a second
build of a commit overwrites the first rather than adding a duplicate. Records and logs are
pruned to one each per commit after a successful publish, and only after a success, so a
failed build can never delete the record of a working one.

Every download is verified against the SHA256 in its record before it is used, and lands at
its real filename only once verified.

### Cleanup

Two mechanisms, deliberately different:

- **The lifecycle rules do routine cleanup**, server side, needing no credential and no app
  running. They also cannot race anybody, which matters: the old count based retention pass
  could delete the zip a teammate was midway through downloading.
- **Manage Binaries** does cleanup on demand, including "keep the newest N".

Note that lifecycle rules are time based only. There is no "keep the newest twenty" rule in
S3 or R2, which is why count based cleanup is a client side action.

Cost is not the reason to prune. The free tier is 10 GB, which at roughly 10 MB a build is
about a thousand of them.

## When something is wrong

```bash
UnhingedSync.exe --selftest
```

Exercises config, the project list, engine resolution, the embedded scripts, the Diversion
CLI, the bucket, the install marker and build capability, then prints a summary and writes a
JSON report. Exit 0 means everything passed. **Run this first.**

```bash
UnhingedSync.exe --storagetest            # bucket and token, read only
UnhingedSync.exe --storagetest --write    # also proves publishing works
UnhingedSync.exe --fetch                  # install binaries for the current commit
UnhingedSync.exe --fetch dv.commit.52     # install a specific commit
UnhingedSync.exe --build                  # compile and publish, no window
```

`--storagetest` exists because every way the bucket can be misconfigured otherwise surfaces
as the same "no builds found". It separates a wrong bucket name, a token scoped elsewhere, a
read only token, and a bad account ID.

`--build` is the same path the button uses, so a build machine can run it from a scheduled
task without a second implementation to drift out of step.

| Symptom | Likely cause |
|---|---|
| "No bucket configured for this project" | The `storage` block is empty. If your team already uses this tool, the project has not finished syncing. |
| "A blank configuration was generated" | Same cause, caught earlier. Sync the project, delete the generated file, reopen. |
| "Could not reach the build store" | Network, or the token was revoked or expired. Run `--storagetest`. |
| A red badge | Open **Build Log** for the compiler output. |
| Compile fails inside untouched engine headers | Run the engine integrity check below. |

### Before blaming the tool for a failed compile

```bash
pwsh -File "%LOCALAPPDATA%\UnhingedSync\scripts\<version>\Test-EngineIntegrity.ps1"
```

Replace `<version>` with the version in the app's top right corner.

A partially applied engine patch leaves `Engine/Source` newer than the UnrealHeaderTool
output shipped beside it, and every `UCLASS` line number shifts. The compiler then reports
errors inside engine headers nobody touched, looking nothing like the real cause. This exact
state once cost a full day. Exit 0 is clean. Exit 1 names the files and tells you to run
Verify in the Epic Games Launcher.

**If a teammate sees behaviour you do not, compare the app version** in the top right first.
Mismatched copies of the tool are the single most likely explanation.

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

The scripts under `scripts/` are compiled in as embedded resources and extracted at runtime
to `%LOCALAPPDATA%\UnhingedSync\scripts\<version>`. Three consequences before editing them:

- **A script cannot infer which project it operates on.** It runs from that cache folder, so
  `$PSScriptRoot` says nothing about the project. The app passes `-ProjectRoot` explicitly.
- **A script never sees a credential.** The build script writes into a local staging folder
  and the app uploads from there. Passing a secret as a script parameter would put it in the
  process command line for any local process to read.
- **Editing a script does nothing until you rebuild.** The exe carries its own copy.

Every script begins with `#Requires -Version 7.0`, placed **after** the comment based help
block. Before it, PowerShell stops recognising the help and silently drops every documented
parameter.

### Cutting a release

1. Bump `<Version>` in `src/UnhingedSync/UnhingedSync.csproj` to match the tag.
2. Commit, then tag and push:

```bash
git tag v1.2.0 && git push origin v1.2.0
```

CI derives the version from the tag, stamps it into the build, verifies the built executable
reports it, zips the result and publishes a GitHub Release. Every teammate's app is then
offered that release automatically. The verification step exists because a release whose exe
under reports its own version offers itself to the whole team forever, since installing it
never changes what they report.

The repository is public specifically so the update check needs no token.

**The first release has to be delivered by hand.** Anyone running a build made before the
updater existed has no update code in their copy.

### Verifying a change

```bash
UnhingedSync.exe --selftest    # services against the real machine and bucket
UnhingedSync.exe --uitest      # builds every window and forces a layout pass
```

`--uitest` exists because parsing a XAML template and applying one are different things. A
template can load from the resource dictionary and still throw when a control uses it, and
nothing else instantiates a window. It has caught two real regressions, including one
introduced by the test itself.

Neither proves a feature works. They prove the plumbing is connected. A change to the
publish or install path should be verified with a real `--build` followed by a real `--fetch`.

## Limits

- **Diversion only.** The version control integration is isolated behind one class, but
  commit identity and ordering are Diversion shaped.
- **Windows only.** Paths and the engine lookup are Win32.
- **One engine version per project**, as the `.uproject` dictates. That is Unreal's
  constraint, not this tool's.
- **Editor binaries only.** No packaged or cooked builds yet. On demand download makes that
  practical now; it is simply not built.
- **No automatic refresh.** The commit list updates when you press Refresh or run an action,
  not on a timer. If somebody is building something you need, you have to check back.
