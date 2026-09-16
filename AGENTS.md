# AGENTS.md

Contributor and agent notes for CardShopCoop.

## Project

CardShopCoop is the marketed name of the `CardShopCoop` BepInEx 5 / Harmony
plugin for **TCG Card Shop Simulator**, a Unity
2021.3 Mono game. The mod references the game's managed assemblies at build time and
patches game behavior at runtime. The game's logic is in `Assembly-CSharp.dll`.

## Versioning and wire compatibility

There is one version source: `CardShopCoopVersion` in `Directory.Build.props`. The
plugin project uses it for assembly metadata, and the build generates the compile-time
`CoopPlugin.Version` constant used by BepInEx and the network handshake. **Bump the
version only in `Directory.Build.props`.**

Use this versioning rule:

- **Patch**: changes that do not alter the wire contract, such as bug fixes, UI/text,
  tuning, configuration, and documentation.
- **Minor**: a new `MsgType` or substantial logic change behind an existing message,
  including field layout, encoding, semantics, or routing changes.
- **Major**: a breaking compatibility change that requires a deliberate major protocol
  transition.

`Msg.WireVersion` is derived from `major * 100 + minor`; patch releases leave it
unchanged. The current plugin identity is `com.zwhit.cardshopcoop`, while the shipped
DLL remains `CardShopCoop.dll`. The handshake also requires exact plugin-version equality,
so peers normally must run the identical CardShopCoop version.

The mod must work on **both** live game builds from one DLL:

- **The public/default branch** is the legacy build (Unity 2021.3.38f1, Steam buildid `25315983`).
  It has **no** `StoredBoxRecord`/`PackageBoxCandidate`/animation-instancing, and warehouse storage
  is live `InteractablePackagingBox_Item` objects (the "live backend").
- **The `1.00` / `1.0newrender` beta** is Unity 6000.0.66f2 and stores warehouse boxes as
  `StoredBoxRecord`s (the "record backend").

`decompiled/` is git-ignored, so which baseline directories exist locally — and which one is the
public/default branch — differs machine to machine and is recorded in the machine-local notes
rather than here. Name the baseline you verified against when reporting.

Anything present in only one of them must be reached by reflection (`WarehouseBoxSync.Probe` is the
house pattern), never a direct reference, or the DLL will fail to load or run on the other build.

`CHANGELOG.md` is maintained in the repository as the player-facing release record. Add a
section for every release using a version heading and a short, plain-language summary in the
same friendly tone as the existing entries. Describe the player-visible symptom and the fix,
mention important safety or compatibility notes, and thank reporters by name when applicable.
Keep implementation details out unless they explain a user-visible behavior. Git history and
GitHub releases remain the authoritative exact-diff record. When the wire version or required
mod set changes, end the release section with **Both players must update.**

## Sync scheduling: no periodic full resends

**Target state, and the rule for anything new.** We do **not** re-send a module's full state on a
timer during normal play. A periodic full resend serializes and allocates the entire state and spikes
bandwidth for every client at the same instant, which reads as hitches and needless traffic.

This applies to a module's *full* state. Two things that look similar are not covered and are
correct as they are: `BoxEngine`'s 10 Hz dirty-list flush (only changed boxes, and the only full send
is a join/heal `RequestFullSnapshot`), and the 1 Hz client possession heartbeat (a crash-safety lease
renewal, not a state resend).

Instead:

- **Push on change.** A game-side mutation fires a Harmony hook, which sends the new state right
  there. Nothing is sent when nothing changed.
- **Gradual re-assertion for eventual correctness.** `CoopModule.PeriodicUpdate(float delta)` runs
  once per co-op frame for every module in the per-frame tick pipeline (it is not role-gated or
  in-game-gated, so a module must guard itself). A module that wants a safety net re-asserts ONE
  slice of its state per call, round-robin (the warehouse sends a single compartment every few
  seconds), so the whole state refreshes over time without a burst. Keep it cheap - it runs every
  frame and must do nothing on most of them.
- **Full sends are per-connection, never periodic.** `CoopModule.FullUpdate(int connId)` sends a
  module's complete state to ONE connection; that is the join catch-up / explicit re-baseline path.
- `ForceResend()` stays the event-driven "our baseline is stale" trigger (join, heal request). It is
  not a timer.

When adding a synced subsystem, prefer change hooks plus a `PeriodicUpdate` slice over any
interval-based full broadcast. A subsystem that genuinely needs a full periodic resend is an
exception and must be justified in review.

**Migration status.** These are migrated to change hooks + a bounded, **unconditional** round-robin
slice sweep + a per-connection `FullUpdate`: `WarehouseBoxSync`, `StaffSync`, `ShopStateSync`,
`SettingsSync`, `ReportSync`, `PlayTableSync`, `TournamentSync`, `GradingSync`, `TradeServe`,
`ContainerSync`, `TvSync`, `WorldSync`, `CardShelfSync`, `ObjMoveSync`, `PopulationSync`,
`RegisterSync`. No module constructs a `SnapshotGate` any more (the type is still defined in
`CoopModule.cs`, now unused). The `CoopCore` light / card-price loops keep their own cadences and
are the remaining backlog.

Two rules that are easy to get wrong — both were, in that migration:

- **The sweep must be UNCONDITIONAL.** It is the eventual-correctness safety net, so it re-sends its
  slice even when nothing has changed since. A sweep gated on a change hash can never repair a
  frame the client dropped: the host's recorded hash already matches, so that slice is never
  re-sent and the client stays wrong until a rejoin. Push on change supplies the immediacy; the
  sweep supplies the correctness.
- **A "slice" must not serialize the whole state.** Payloads are JSON (`WireCodec`), which writes
  every populated public field, so a slice builder that fills every field of a flat DTO turns every
  "slice" into a full-state send — one module reached ~85 KB/s that way. Populate only the slice's
  fields, populate none of the others, and merge only that slice's fields on the client. Where a
  message carries an incomplete roster, the receiver must treat omission as "unchanged", never as
  "removed".

The per-module pass windows differ (from ~4 s for a small roster to ~30 s for the warehouse); a
module whose state outgrows its slice budget lengthens its pass rather than sending a burst.

## Build and CI

Build the plugin with:

```powershell
dotnet build src\CardShopCoop\CardShopCoop.csproj -c Release -p:Deploy=true
```

Deployment is opt-in: `Deploy` defaults to `false` in `Directory.Build.props`. Always use
`-p:Deploy=true` for a local build when you want the DLL copied to the game's BepInEx
plugins directory; omit it in CI or while the game is running. The repository has no
solution file.

Deploy target is always a single directory: `$(GamePath)/BepInEx/plugins/CardShopCoop`. Deploy
only to the game install `GamePath` resolves to. Never deploy to a second, mirrored copy of
the same install (a sandbox or a shadow copy): such a directory syncs itself from the real
install, so writing to it fights the mirroring and is never wanted. If you find one, leave it
alone.

Machine-specific paths (the real `GamePath`, any mirrored/sandbox copy, and how to tell them
apart) belong in `Directory.Build.user.props` and in the machine's global OpenCode
instructions (`~/.config/opencode/AGENTS.md`), never in this file — it is committed and shared.

Before submitting changes, check formatting from the repository root:

```powershell
dotnet restore src\CardShopCoop\CardShopCoop.csproj
dotnet format src\CardShopCoop\CardShopCoop.csproj whitespace --verify-no-changes --no-restore
```

The command must finish with no formatting errors. If it reports formatting changes, apply
them with the same command without `--verify-no-changes`, inspect the result, and rerun the
verification command. CI runs this same formatting check against the root `.editorconfig`.
Full compile CI is deferred until a permitted source for the game's reference assemblies is
available.

## Game path

`Directory.Build.props` resolves `GamePath` in this order:

1. `Directory.Build.user.props` (git-ignored; copy `Directory.Build.user.props.example`).
2. The `CARDSHOP_GAMEPATH` environment variable.
3. The committed default in `Directory.Build.props`.

For a one-off build, `dotnet build -p:GamePath=...` overrides those settings. Do not
hard-code a local game path in a project file.

## Decompiled game source

The game ships no source, only bytecode. Use `tools/Decomp` to generate readable C# from
the managed assemblies; the output is git-ignored and must not be committed.

Build the tool once:

```powershell
dotnet build tools\Decomp\Decomp.csproj -c Release
```

Run it with an assembly path and output directory. Decompiled output is versioned by the
game build it came from, so a new game update never overwrites the baseline used for
diffs:

```powershell
dotnet tools\Decomp\bin\Release\net9.0\Decomp.dll `
  "<GamePath>\Card Shop Simulator_Data\Managed\Assembly-CSharp.dll" `
  "<repo-root>\decompiled\<gameversion>\Assembly-CSharp"
```

Baselines are git-ignored, so the set that exists locally differs machine to machine. Which
directories are present, which game build each came from, and which one is the public/default
branch are recorded in the machine-local notes, not here.

Two things are project-wide regardless. Decompiled output is versioned by the game build it
came from, so a new game update never overwrites the baseline used for diffs; to see what an
update changed, diff two version directories directly
(`git diff --no-index decompiled\<a>\Assembly-CSharp decompiled\<b>\Assembly-CSharp`). And
sibling builds of the same branch are **not** interchangeable — their file sets are
near-identical but a handful of files differ in content between buildids, so verify against
the baseline you actually mean and say which one you used.

The game version is `Application.version` (Unity `PlayerSettings.bundleVersion`). It can be
read offline from `Card Shop Simulator_Data\globalgamemanagers` with a Unity serialized-file
parser (e.g. Python `UnityPy`: read the `PlayerSettings` object and take `bundleVersion`).
The repo bundles a ready-made cross-platform helper:

```powershell
python scripts\game-version.py            # game + Unity version, and the suggested dir name
python scripts\game-version.py --quiet     # just the version, for scripts/CI
```

It auto-detects the install via `--game-path`, `CARDSHOP_GAMEPATH`, the repo's `Directory.Build`
props, or the usual Steam libraries on Windows/Linux/macOS.

Other useful assemblies include `Heathen.Core.dll`, `Heathen.Steamworks.dll`,
`AstarPathfindingProject.dll`, and `DOTween.dll`; Unity assemblies are usually needed
only as references.

When developing a feature, find the game class that owns the relevant flow and inspect
the real decompiled method bodies, fields, events, and call order. When investigating a
bug, verify the expected behavior in the decompiled source and confirm that the patch
targets the exact method and overload the game calls.

Useful game systems to search first include `CEventManager`, `CGameManager`, `CGameData`,
`CSaveLoad`, `ShelfManager`, `CustomerManager`, `WorkerManager`, `RestockManager`, and
`UnlockRoomManager`. Many managers use `CSingleton<T>` and auto-create their instances;
check the decompiled implementation before relying on singleton state.
