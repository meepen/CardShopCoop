---
name: Decompile
description: How to decompile TCG Card Shop Simulator into readable C# with the repo's tools/Decomp tool - the exact command, the versioned decompiled/<gameversion> baseline convention, and how to diff two game builds. Use when the game's shipped bytecode needs to be read, a new game update must be decompiled, or two builds need to be diffed.
---

# Decompiled game source

The game ships no source, only bytecode. The repo bundles `tools/Decomp` (ICSharpCode.Decompiler)
to turn the managed assemblies into readable C#. Decompiled output is git-ignored and must never
be committed.

## Running it

If the tool's Release output is missing, build it once:

```powershell
dotnet build tools\Decomp\Decomp.csproj -c Release
```

Then run it with an assembly path and an output directory:

```powershell
dotnet tools\Decomp\bin\Release\net9.0\Decomp.dll `
  "<GamePath>\Card Shop Simulator_Data\Managed\Assembly-CSharp.dll" `
  "<repo-root>\decompiled\<gameversion>\Assembly-CSharp"
```

The game's logic is in `Assembly-CSharp.dll`; other managed assemblies decompile the same way when
a patch target or dependency needs reading.

## Baselines are versioned by game build

Output goes under `decompiled\<gameversion>\`, keyed by the game version the assembly came from,
so a new game update never overwrites the baseline used for older diffs. To see what an update
changed, diff the two version directories directly:

```powershell
git diff --no-index decompiled\<old>\Assembly-CSharp decompiled\<new>\Assembly-CSharp
```

`<gameversion>` is `Application.version` (Unity's `PlayerSettings.bundleVersion`). The repo helper
reads it straight from `globalgamemanagers` without launching the game and prints the suggested
directory name:

```powershell
python scripts\game-version.py            # game + Unity version, and the suggested dir name
python scripts\game-version.py --quiet     # just the version, for scripts/CI
```

Which builds the mod must support, and the rule for reaching build-specific game types by
reflection, live in the root `AGENTS.md`. The per-machine inventory of which baselines exist on
this PC is in the `local-install` skill.
