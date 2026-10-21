---
name: Local install
description: This development machine's CardShopCoop install layout - the Steam game install, the Game Pass install, and the mirror paths, deploy targets, log locations, the live test pair, and the local decompiled baselines. Use when building, deploying, or reading logs for CardShopCoop on this PC.
---

# Local install (this development machine)

Machine-specific facts for building, deploying, and reading logs on this PC. The general
instructions in the root `AGENTS.md` stay free of machine paths; this skill carries the ones that
only apply here.

## Installs

- **Real game install** (`GamePath`): `Z:\SteamLibrary\steamapps\common\TCG Card Shop Simulator`
  — the Steam **public** branch (buildid `25381942`), currently game `1.02` on Unity
  `2021.3.38f1` (the legacy build). `Directory.Build.user.props` pins `GamePath` to it, so a plain
  `-p:Deploy=true` build deploys here and nowhere else. This is the only Deploy target.
- **Game Pass install**: `Z:\XboxApps\TCG Card Shop Simulator\Content`
  — the Xbox / Game Pass (GDK, packaged MSIX) copy, game `1.00` on Unity `6000.0.66f2` (the Unity 6
  build). `Z:\XboxApps` is this drive's Xbox game root (see `Z:\.GamingRoot`); the installed
  package `OPNEONGAMES.TCGCardShopSimulator_1.1.4.0_x64__19j6by82ahhzr` is junctioned to `Content`
  from `C:\Program Files\WindowsApps\...`, so writing under `Content` writes the live install. The
  package is full-trust and unvirtualized (`runFullTrust`, `unvirtualizedResources`,
  `FileSystemWriteVirtualization=disabled`), so it takes the same Doorstop + BepInEx layout as the
  Steam install. It is **not a build target** — it holds a hand-synced mirror of the Steam
  install's mod set (Doorstop + `BepInEx\core`, `patchers`, `config`, `plugins`).
- **Mirrored copy**: `C:\Sandbox\meep\New_Box\drive\Z\SteamLibrary\steamapps\common\TCG Card Shop Simulator`.
  A separate copy that mirrors the real install whenever a file is missing. It carries
  `DONT-USE.TXT` markers and must **never** be deployed to or written to.

## Deploy and logs

- **Standing preference on this machine: after EVERY code change, build and deploy to BOTH
  installs, not just a local Release build.** A change is not finished until it is in both:
  ```powershell
  dotnet build CardShopCoop.sln -c Deploy                                                          # Steam
  dotnet build CardShopCoop.sln -c Deploy -p:DeployPath="Z:\XboxApps\TCG Card Shop Simulator\Content"  # Game Pass
  ```
  The solution form is preferred over the single `CardShopCoop.csproj` build because it also emits
  and copies `CardShopCoop.CustomTv.dll`. `-c Deploy` is Release plus the copy to `$(DeployPath)`,
  so the global `-p:DeployPath=` override reaches every plugin project. A locked DLL means the game
  is running — say so instead of silently skipping the copy, and re-run once it is closed.

- Deployed plugins (from `dotnet build CardShopCoop.sln -c Deploy`), under `<install>`:
  - `<install>\BepInEx\plugins\CardShopCoop\CardShopCoop.dll`
  - `<install>\BepInEx\plugins\CardShopCoop\CardShopCoop.Api.dll`
  - `<install>\BepInEx\plugins\CardShopCoop.CustomTv.dll` (separate download; inert unless RTCGO
    Custom TV is installed)
- Where `Deploy` copies is `$(DeployPath)`, which defaults to `$(GamePath)`. So a plain
  `-c Deploy` / `-p:Deploy=true` still targets the Steam install, **and the deploy directory can be
  redirected from the CLI**. To build against the Steam install but drop the plugins into the Game
  Pass install, override it:
  ```powershell
  dotnet build src\CardShopCoop\CardShopCoop.csproj -c Release -p:Deploy=true `
    -p:DeployPath="Z:\XboxApps\TCG Card Shop Simulator\Content"
  ```
  That is the supported replacement for the old "copy the `BepInEx` tree by hand" step. All deploy
  targets honour it: core + `.Api` (`BepInEx\plugins\CardShopCoop`), the Steam transport (same
  folder; the core build forwards `DeployPath` to it), Custom TV and the sample
  (`BepInEx\plugins`).
- Do **not** use `-p:GamePath="<other install>"` to redirect the deploy: `GamePath` also feeds
  `ManagedPath`/`BepInExPath`, so the build would compile against that install's game assemblies
  (on the Game Pass mirror, the Unity 6 / 1.00 ones) instead of the Steam install. `DeployPath`
  exists precisely so the deploy target and the reference root are separate.
  (`Directory.Build.user.props` sets `GamePath` unconditionally, so the `CARDSHOP_GAMEPATH`
  environment variable never applies on this machine — a CLI `-p:` global property is the only
  override that wins.)
- Host log (either install): `<install>\BepInEx\LogOutput.log` — i.e.
  `Z:\SteamLibrary\steamapps\common\TCG Card Shop Simulator\BepInEx\LogOutput.log` for Steam, or
  `Z:\XboxApps\TCG Card Shop Simulator\Content\BepInEx\LogOutput.log` for Game Pass.
- Unity `Player.log` and `Application.persistentDataPath`:
  `%USERPROFILE%\AppData\LocalLow\OPNeonGames\Card Shop Simulator\`. The Game Pass package disables
  write virtualization, so it shares this folder with the Steam install (same `Player.log`,
  `savedGames_Release*.json`, and mod sidecars). Game Pass *cloud* saves live in the Xbox container
  `%LOCALAPPDATA%\Packages\OPNEONGAMES.TCGCardShopSimulator_19j6by82ahhzr\SystemAppData\wgs\`.
- Client log (mirrored copy): `<mirror>\BepInEx\LogOutput.log`. The mirror's
  `AppData\LocalLow` may not exist locally — read the BepInEx log instead.

## Test setup

- The live test pair is a **single-build** one: host & client = `1.02` on the Steam install.
- The Game Pass install is a second, **different-build** copy (`1.00` / Unity 6). It is modded
  identically to the Steam install, but third-party mods built for the legacy build may not load on
  Unity 6, and it is not part of the `1.02` test pair.

## Decompiled baselines on this machine

`decompiled/` is git-ignored, so this inventory is local to this PC and will not match another
machine. Present here:

| directory | what it is |
|---|---|
| `decompiled\0.70.3` | pre-1.0 |
| `decompiled\1.00` | the 1.0 release (`Application.version` is exactly `1.00`), Unity 6000.0.66f2 |
| `decompiled\1.00-newrender` | the `1.0newrender` beta |
| `decompiled\1.00-gamepass` | **the installed Xbox / Game Pass 1.00 build** (Unity 6000.0.66f2) — same lineage as `1.00` but adds the `GA\Gamecore*` Xbox namespace and drops `GradedCardOpeningSequence` |
| `decompiled\1.0` | a 1.0-era build |
| `decompiled\1.0-25119539`, `decompiled\1.0-25314665` | intermediate builds of the public branch |
| `decompiled\1.0-25315983` | **the public/default branch** (Steam buildid `25315983`) |
| `decompiled\1.01`, `decompiled\1.02` | later public-branch builds; `1.02` is the installed Steam build (no `GA\`) |
| `decompiled\EnhancedPrefabLoader`, `decompiled\PhoneOverhaul` | third-party mod sources kept for reference |
| `decompiled-grading\` | Grading Overhaul reference output |

The two builds the plugin must support, and which of the above maps to each:
- public/default legacy (Unity 2021.3.38f1, no `StoredBoxRecord`, live-box warehouse) →
  `decompiled\1.0-25315983`
- `1.00` beta (Unity 6000.0.66f2, `StoredBoxRecord` warehouse) → `decompiled\1.00`

The installed Steam build is `1.02` (Steam buildid `25381942`), decompiled at `decompiled\1.02`
(639 files, matches the live install). The installed Game Pass build is `1.00` / Unity 6,
decompiled at `decompiled\1.00-gamepass`. The Game Pass build ships a 4 KB **stub**
`com.rlabrecque.steamworks.net` (no real Steamworks types) — see the Steam isolation in
`Net/ISteamBridge.cs`.
