## Project

CardShopCoopCommunity is the marketed name of the `CardShopCoop` BepInEx 5 / Harmony
plugin for **TCG Card Shop Simulator**, a Unity
2021.3 Mono game. The mod references the game's managed assemblies at build time and
patches game behavior at runtime. The game's logic is in `Assembly-CSharp.dll`.

## Versioning and wire compatibility

There is one version source: `CardShopCoopVersion` in `Directory.Build.props`.

Use this versioning rule:

- **Minor**: only when the join handshake itself changes - the Hello/Welcome exchange, the
  version/wire-revision negotiation, or the catalog and reliability validation carried in it.
- **Patch**: every other change, including new or changed network messages and their content.
  Message parity is checked by the handshake at join time, so message changes ride in patch
  releases. Bug fixes, UI/text, tuning, configuration, and documentation are patches too.

Do not automatically bump the version; recommend the right bump to the user instead.

The handshake compares the exact `CardShopCoopVersion` and message catalog, so any release -
patch included - requires both players to update together.

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

Or open `CardShopCoop.sln` and build the **Deploy** configuration, which builds every plugin project
and copies them into the game install resolved from `GamePath`:

```powershell
dotnet build CardShopCoop.sln -c Deploy
```

`-p:Deploy=true` still works for a single project, and `-p:DeploySample=true` additionally deploys
the sample mod. The Deploy configuration only changes where output goes; it is otherwise a normal
build.

Before submitting changes, check formatting from the repository root:

```powershell
dotnet restore src\CardShopCoop\CardShopCoop.csproj
dotnet format src\CardShopCoop\CardShopCoop.csproj whitespace --verify-no-changes --no-restore
```

## Local install

Machine-specific setup — this PC's real game install and mirror paths, deploy targets, log
locations, the live test pair, and the decompiled baseline inventory — is documented in the
`local-install` skill (`.opencode/skills/local-install/SKILL.md`), not here. Load it with
`@local-install` when you need to build, deploy, or read logs on this machine.

## Code standards
1. No polling or timers. Prefer event driven always.
2. Harmony is proven and not the issue.
3. Do not store game-specific state. Only acceptable state we store is external information we add to the game. For example, network ID to Game Object lookups. Reuse the game data when possible.
4. Do not do least-effort patches. Go for full fixes.
5. No heuristical assignments. Always assign stable IDs over the network on box creation and utilize stable IDs over the network.