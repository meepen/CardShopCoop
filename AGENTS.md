# AGENTS.md

Notes for AI agents and contributors working in this repo.

## What this repo is

A BepInEx 5 / Harmony mod (`CardShopCoop`) for **TCG Card Shop Simulator**, a Unity
2021.3 **Mono** game (not IL2CPP). The game's managed assemblies live in the install's
`Card Shop Simulator_Data\Managed\` folder; the game logic is in `Assembly-CSharp.dll`.

The mod references those game assemblies at build time and patches them at runtime via
Harmony. To understand what the game actually does (for features or bug reports) you read
its **decompiled source** — the game ships no source, only bytecode.

## The decompile tool

`tools/Decomp` is a thin ILSpy wrapper (`ICSharpCode.Decompiler`, `WholeProjectDecompiler`)
that turns a managed assembly into a full C# project of `.cs` files.

Build it once:

```powershell
dotnet build tools\Decomp\Decomp.csproj -c Release
```

Run it (args: `<assembly.dll>` `<output-dir>`):

```powershell
dotnet tools\Decomp\bin\Release\net9.0\Decomp.dll `
  "Z:\SteamLibrary\steamapps\common\TCG Card Shop Simulator\Card Shop Simulator_Data\Managed\Assembly-CSharp.dll" `
  "C:\Users\meep\Desktop\CardShopCoop\decompiled\Assembly-CSharp"
```

## Game path (`GamePath`)

The build resolves the install path via repo-root `Directory.Build.props`, in this order
(high to low precedence):

1. `Directory.Build.user.props` (git-ignored; copy `Directory.Build.user.props.example`)
2. `CARDSHOP_GAMEPATH` environment variable
3. the committed `D:\SteamLibrary\steamapps\common\TCG Card Shop Simulator` default

`dotnet build -p:GamePath=...` overrides all of these for a one-off/CI build. Do **not**
hard-code a path in the csproj. The game on this machine is at `Z:\SteamLibrary\...`.

## Decompile workflow

1. Locate the assemblies: `<GamePath>\Card Shop Simulator_Data\Managed\`.
2. Decompile `Assembly-CSharp.dll` (the game logic) with the tool above.
3. Output goes to `decompiled\` (git-ignored — regenerate locally; never commit it).
4. Search/read the generated `.cs` files. They are plain C# with real method bodies
   (ILSpy produces full implementations, not stubs).

Useful extra assemblies to decompile when needed (same command):
- `Heathen.Core.dll` / `Heathen.Steamworks.dll` — Steam integration
- `AstarPathfindingProject.dll` — pathfinding
- `DOTween.dll` — tweens
- `Unity.TextMeshPro.dll`, `UnityEngine.UI.dll` — UI/rendering (usually only via references)

## When to use the decompiled source

**Developing a feature** (e.g. syncing a new game system):
- Find the game class that owns the data/flow — `decompiled\Assembly-CSharp\*.cs`.
- Read the method bodies to learn the exact fields, events, and call order the mod must
  hook (e.g. `CEventManager.QueueEvent(...)` calls, `CSingleton<T>.Instance` usage, the
  save path through `CGameData`/`CSaveLoad`).
- The mod patches against the *public API surface* of these types; the decompiled source
  is the ground truth for what that surface is.

**Investigating a bug report**:
- Grep the decompiled source for the class/method named in the report or stack trace.
- Confirm expected behavior from the actual bytecode, then decide whether the bug is in
  the game or in the mod's patch.
- Cross-check the mod's patch against the game method it patches to find mismatch (wrong
  signature, patched method being a different overload, the method being patched when the
  game calls a private inline copy, etc.).

## Pointers

- The game's event bus is `CEventManager`; scene/save flow is `CGameManager`, `CGameData`,
  `CSaveLoad`; gameplay managers are `ShelfManager`, `CustomerManager`, `WorkerManager`,
  `RestockManager`, `UnlockRoomManager`.
- Many singletons are `CSingleton<T>` and auto-create instances — check `CGameManager.cs`
  for patterns (see `Patches\GamePatches.cs` and git history for known pitfalls).
