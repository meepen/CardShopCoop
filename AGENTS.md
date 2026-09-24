## Project

CardShopCoop is the marketed name of the `CardShopCoop` BepInEx 5 / Harmony
plugin for **TCG Card Shop Simulator**, a Unity
2021.3 Mono game. The mod references the game's managed assemblies at build time and
patches game behavior at runtime. The game's logic is in `Assembly-CSharp.dll`.

## Versioning and wire compatibility

There is one version source: `CardShopCoopVersion` in `Directory.Build.props`.

Use this versioning rule:

- **Patch**: changes that do not alter the wire contract, such as bug fixes, UI/text,
  tuning, configuration, and documentation.

Do not automatically update the wire version, but do recommend it to the user.

The mod must work on **both** live game builds from one DLL:

- **The public/default branch** is the legacy build (Unity 2021.3.38f1, Steam buildid `25315983`).
  It has **no** `StoredBoxRecord`/`PackageBoxCandidate`/animation-instancing, and warehouse storage
  is live `InteractablePackagingBox_Item` objects (the "live backend").
- **The `1.00` / `1.0newrender` beta** is Unity 6000.0.66f2 and stores warehouse boxes as
  `StoredBoxRecord`s (the "record backend").

Anything present in only one of them must be reached by reflection (`WarehouseBoxSync.Probe` is the
house pattern), never a direct reference, or the DLL will fail to load or run on the other build.

`CHANGELOG.md` is maintained in the repository as the player-facing release record. Add a
section for every release using a version heading and a short, plain-language summary in the
same friendly tone as the existing entries. Describe the player-visible symptom and the fix.

## Build and CI

Build the plugin with:

```powershell
dotnet build src\CardShopCoop\CardShopCoop.csproj -c Release -p:Deploy=true
```

Before submitting changes, check formatting from the repository root:

```powershell
dotnet restore src\CardShopCoop\CardShopCoop.csproj
dotnet format src\CardShopCoop\CardShopCoop.csproj whitespace --verify-no-changes --no-restore
```

## Decompiled game source

The game ships no source, only bytecode. Use `tools/Decomp` to generate readable C# from
the managed assemblies; the output is git-ignored and must not be committed.

Run it with an assembly path and output directory. Decompiled output is versioned by the
game build it came from, so a new game update never overwrites the baseline used for
diffs:

```powershell
dotnet tools\Decomp\bin\Release\net9.0\Decomp.dll `
  "<GamePath>\Card Shop Simulator_Data\Managed\Assembly-CSharp.dll" `
  "<repo-root>\decompiled\<gameversion>\Assembly-CSharp"
```

Two things are project-wide. Decompiled output is versioned by the game build it
came from, so a new game update never overwrites the baseline used for diffs; to see what an
update changed, diff two version directories directly
(`git diff --no-index decompiled\<a>\Assembly-CSharp decompiled\<b>\Assembly-CSharp`).

The repo bundles a helper to get a game version from a game path:

```powershell
python scripts\game-version.py            # game + Unity version, and the suggested dir name
python scripts\game-version.py --quiet     # just the version, for scripts/CI
```

## Code standards
1. No polling or timers. Prefer event driven always.
2. Harmony is proven and not the issue.
