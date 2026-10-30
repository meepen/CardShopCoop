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
  — the Steam **public** branch (buildid `26320596`), currently game `1.06` on Unity
  `6000.0.66f2` (Unity 6, record backend). `Directory.Build.user.props` pins `GamePath` to it, so a
  plain `-p:Deploy=true` build deploys here and nowhere else. This is the default Deploy target.
- **Game Pass install**: `Z:\XboxApps\TCG Card Shop Simulator\Content`
  — the Xbox / Game Pass (GDK, packaged MSIX) copy, game `1.04` on Unity `6000.0.66f2` (the same
  Unity 6 codebase plus the Xbox `GA.Gamecore*` types). `Z:\XboxApps` is this drive's Xbox game
  root (see `Z:\.GamingRoot`); the installed
  package `OPNEONGAMES.TCGCardShopSimulator_1.1.4.0_x64__19j6by82ahhzr` is junctioned to `Content`
  from `C:\Program Files\WindowsApps\...`, so writing under `Content` writes the live install. The
  package is full-trust and unvirtualized (`runFullTrust`, `unvirtualizedResources`,
  `FileSystemWriteVirtualization=disabled`), so it takes the same Doorstop + BepInEx layout as the
  Steam install. It is **not the default build target**; deploy to it explicitly with
  `-p:DeployPath` (see below). It holds a hand-synced mirror of the Steam install's mod set
  (Doorstop + `BepInEx\core`, `patchers`, `config`, `plugins`).
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

- The live test pair is a **single-build** one: host & client = `1.06` on the Steam install.
- The Game Pass install is a second, **different-build** copy (`1.04` / Unity 6). It is modded
  identically to the Steam install, but the two builds differ in content/enum values and have
  diverged further since (1.05 and 1.06 shipped only on Steam), so a cross-build join is normally
  refused even with `AllowCrossBuildJoin` on; it is not part of the `1.06` test pair.

## Decompiled baselines on this machine

`decompiled/` is git-ignored, so this inventory is local to this PC and will not match another
machine. Present here:

| directory | what it is |
|---|---|
| `decompiled\0.70.3` | pre-1.0 |
| `decompiled\1.00` | the 1.0 release (`Application.version` is exactly `1.00`), Unity 6000.0.66f2 |
| `decompiled\1.00-newrender` | the `1.0newrender` beta |
| `decompiled\1.00-gamepass` | historical Game Pass 1.00 build (Unity 6000.0.66f2) — same lineage as `1.00` but adds the `GA\Gamecore*` Xbox namespace and drops `GradedCardOpeningSequence` |
| `decompiled\1.0` | a 1.0-era build |
| `decompiled\1.0-25119539`, `decompiled\1.0-25314665` | intermediate builds of the old public branch |
| `decompiled\1.0-25315983` | historical public 2021.3 build (no longer supported) |
| `decompiled\1.01`, `decompiled\1.02` | historical public 2021.3 builds (no longer supported) |
| `decompiled\1.03` | public 1.03 build (Steam buildid `26249007`), Unity 6000.0.66f2, record backend — no longer installed |
| `decompiled\1.04` | public 1.04 build (Steam buildid `26274531`), decompiled 10/29 |
| `decompiled\1.05` | public 1.05 build (Steam buildid `26302800`), decompiled 10/29 — the previous baseline |
| `decompiled\1.06` | **the installed Steam 1.06 build** (Steam buildid `26320596`), decompiled 10/30; adds the mouse-vs-gamepad camera priority and the price-tag amount/half-size fixes over 1.05 |
| `decompiled\1.04-gamepass` | **the installed Xbox / Game Pass 1.04 build**, same 1.00 lineage plus `GA\Gamecore*` |
| `decompiled\EnhancedPrefabLoader`, `decompiled\PhoneOverhaul` | third-party mod sources kept for reference |
| `decompiled-grading\` | Grading Overhaul reference output |

The two builds the plugin must support, and which of the above maps to each:
- public/default (game `1.06`, Unity 6000.0.66f2, record backend) → `decompiled\1.06`
- Xbox / Game Pass (game `1.04`, Unity 6000.0.66f2, record backend) → `decompiled\1.04-gamepass`

The installed Steam build is `1.06` (Steam buildid `26320596`), decompiled at `decompiled\1.06`.
The installed Game Pass build is `1.04`, decompiled at `decompiled\1.04-gamepass`. Both are Unity 6
and use the record backend; the 2021.3 baselines above are historical only. The Game Pass build ships a 4 KB **stub**
`com.rlabrecque.steamworks.net` (no real Steamworks types) — see the Steam isolation in
`Net/ISteamBridge.cs`.
