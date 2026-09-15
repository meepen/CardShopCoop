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

The mod is tested only against the newest game version available when documented here:
**TCG Card Shop Simulator 1.00** (Unity 6000.0.66f2).

`CHANGELOG.md` is maintained in the repository as the player-facing release record. Add a
section for every release using a version heading and a short, plain-language summary in the
same friendly tone as the existing entries. Describe the player-visible symptom and the fix,
mention important safety or compatibility notes, and thank reporters by name when applicable.
Keep implementation details out unless they explain a user-visible behavior. Git history and
GitHub releases remain the authoritative exact-diff record. When the wire version or required
mod set changes, end the release section with **Both players must update.**

## Build and CI

Build the plugin with:

```powershell
dotnet build src\CardShopCoop\CardShopCoop.csproj -c Release -p:Deploy=true
```

Deployment is opt-in: `Deploy` defaults to `false` in `Directory.Build.props`. Always use
`-p:Deploy=true` for a local build when you want the DLL copied to the game's BepInEx
plugins directory; omit it in CI or while the game is running. The repository has no
solution file.

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

Existing baselines: `decompiled\0.70.3` (pre-1.0) and `decompiled\1.00` (the 1.0 release;
the game's `Application.version`/`PlayerSettings.bundleVersion` is exactly `1.00`). To see
what a game update changed, diff two version directories directly
(`git diff --no-index decompiled\0.70.3\Assembly-CSharp decompiled\1.00\Assembly-CSharp`).

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
