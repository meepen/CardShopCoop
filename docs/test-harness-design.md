# Automated two-instance test harness — design

Status: **design, not implemented.** This document specifies a testing framework that runs two
real game instances (host + joiner) over the mod's own loopback IP transport and asserts on
synchronized state. It builds on the feasibility findings in
`docs/nucleus-splitscreen-integration.md`. Items that must be proven by a spike before they can be
relied on are marked **[VERIFY]**.

The guiding constraints:

- **The harness must never ship to players and must be inert unless explicitly launched for a
  test.** Every production-side change it needs must be justified on its own merits, not smuggled
  in "for testing".
- **Tests must be editable without recompiling and restarting the game.** The in-game assembly is a
  fixed *interpreter shell*; the tests themselves are scripts on disk, read fresh on every run.
- **Observation must never suspend a live peer.** The harness reads state in-process on the main
  thread, so a co-op session is not frozen and the 10 s presence window
  (`Runtime/CoopRuntimeContext.cs:78`) cannot trip.

---

## 1. Goals and non-goals

### Goals

- Run a **host + joiner pair of the real game** with the real mod, on one machine, over the real
  wire protocol (`-coopautohost` / `-coopautojoin` on loopback).
- Drive scenarios **deterministically enough to be repeatable**: load a world, host, join, perform
  a scripted interaction, assert both peers converge.
- **Edit tests without rebuilding or restarting the game.** Change a script file, re-run it.
- Observe state and the wire trace so a failure says *what diverged and when*, not just "failed".
- Make a failure easy to debug: archive both instances' logs, and support attaching a managed
  debugger for triage.
- Be usable both interactively on this dev machine and, eventually, in CI on a Windows runner.
- Cover the mod's real risk surface: join handshake, world transfer, per-module sync, prediction
  rollback, lag/jitter robustness, and regression tests tied to fixed bugs.

### Non-goals

- **Unit tests of pure logic.** Those belong in an ordinary xUnit project (none exists today) and
  are complementary. This harness is for integration behavior that only appears with two live
  processes.
- **Replacing manual playtesting** for feel, performance, or visuals.
- **Testing the Game Pass / Unity 6 build as a peer.** The handshake rejects differing catalogs
  (`EObjectType` etc.), so only the same-build pair is fully testable. Cross-build is exercised
  only for the *rejection* path.
- **A debugger as the driver.** See §9 and §10.

---

## 2. Architecture

Two channels, deliberately separate:

1. **Session channel** — the mod's real transport (KCP over UDP, loopback). Tests exercise the
   actual wire and the actual handshake. Nothing test-specific rides on it.
2. **Control channel** — a loopback TCP server inside each instance's harness plugin, used only by
   the external runner to load scripts, evaluate expressions, and read observations. It is **not**
   part of the mod's wire protocol and is invisible to gameplay.

```
Developer machine / Windows CI
┌────────────────────────────────────────────────────────────────┐
│ CardShopCoop.TestRunner  (external .NET console process)        │
│  · provisions isolated instance dirs, launches game processes   │
│  · runs the COORDINATOR SCRIPT (Lua) — the test itself          │
│  · drives both instances via the control channel                │
│  · collects results, writes the report                          │
│  · on failure: archives logs, offers debugger attach            │
└──────────────▲──────────────────────────┬──────────────────────┘
               │ control (loopback TCP)    │ control (loopback TCP)
    ┌──────────┴──────────┐    ┌──────────┴───────────┐
    │ Instance: HOST      │    │ Instance: JOINER      │
    │ Card Shop Simulator │    │ Card Shop Simulator   │
    │ + CardShopCoop      │    │ + CardShopCoop        │
    │ + CoopTestHarness   │    │ + CoopTestHarness     │
    │   (interpreter shell)│   │   (interpreter shell) │
    └──────────┬──────────┘    └──────────┬───────────┘
               └──── CardShopCoop IP transport (UDP loopback) ────┘
```

The control channel is separate from the session channel for the same reason the harness is
test-only: mixing test control into the game protocol would change the handshake/catalog the tests
verify, and would ship test surface to players.

---

## 3. Components

### 3.1 `CardShopCoop.TestHarness` — the interpreter shell (never shipped)

A small BepInEx plugin that loads alongside CardShopCoop in each instance. It is a **fixed shell**:
the code here changes only when a new *primitive* is needed, not when a test changes.

- **Project:** `tests/CardShopCoop.TestHarness/` (`net472`), same reference layout as the sample
  (`samples/CardShopCoop.SampleMod/CardShopCoop.SampleMod.csproj`): project reference to
  `CardShopCoop.Api` with `Private=false`, plus `BepInEx` / `UnityEngine` references from
  `$(ManagedPath)` / `$(BepInExPath)`.
- **Integration:** `[BepInPlugin]` + `[BepInDependency("dev.meepen.cardshopcoop",
  SoftDependency)]`, like the sample. Discovered automatically; no registration call.
- **Inert by default:** activates only when launched with a harness argument (e.g.
  `-cooptest=127.0.0.1:<port>`). With the DLL present but no such argument it does nothing — no
  listener, no hooks, no frame work.

Shell internals:

- **ControlServer** — a loopback TCP listener. Newline-delimited JSON or length-prefixed frames.
  Runs on a background thread; **all Unity/game access is marshalled to the main thread** using the
  same pattern as `Runtime/MainThreadDispatcher.cs`. It never touches Unity off-thread.
- **ScriptHost** — embeds a small interpreter (§4) and evaluates code **on the main thread**. It
  supports *yielding*: a `wait_for(predicate)` call suspends the running script and the shell
  resumes it on the next frame boundary when the predicate holds. This is a Unity coroutine in
  spirit — frame-driven, not a wall-clock timer.
- **Reflection bridge** — the generic escape hatch. Exposes `type`, `static`, `get`, `set`, `call`,
  `invoke` so a coordinator script can reach any game/mod member without a DLL change. Follows the
  `DebugTools` reflection-only, build-safe pattern (`Modules/DebugTools/DebugToolsRuntime.cs`).
- **Curated primitives** — a small, typed, versioned convenience layer for common actions
  (`game.place_warehouse_box`, `session.host`, `session.join`, …), so scripts aren't all raw
  reflection strings. Optional sugar over the reflection bridge.
- **Tracer** — an ordered event log (message in/out, state apply, rollback, connection
  transitions) with timestamps and `Time.frameCount`. Implemented as **test-only Harmony patches
  inside the harness assembly**, so there is **zero production change**. Harmony is "proven and not
  the issue" (`AGENTS.md`).
- **Clock control** — pause/step the player loop and seed RNG (§6).
- **Log/exception capture** — forwards Unity log lines and unhandled exceptions to the runner.

### 3.2 `CardShopCoop.TestRunner` — the external orchestrator (never shipped)

- **Project:** `tests/CardShopCoop.TestRunner/` — a normal .NET console app (dev machine / Windows
  CI; no Unity references). It also hosts the same interpreter for the coordinator script.
- Responsibilities:
  - Parse the test manifest (which coordinator script, which instances).
  - **Provision isolated instance directories** from the game install (per-instance copy or
    symlinks; exclude `BepInEx` config, and give each its own config so `PlayerName` / `Port` /
    module toggles don't clobber). This is the isolation the Nucleus doc's §4 table requires.
  - Launch instances with the right args and control ports, and stagger them so the host is ready
    before the joiner dials.
  - Run the **coordinator script**, exposing `host` / `joiner` handles that evaluate code on each
    instance and return results.
  - Evaluate cross-peer assertions, write a report (console + JUnit XML + a markdown summary), set
    the exit code.
  - Tear down instances and archive `BepInEx/CardShopCoopCommunity_{pid}.log` per instance (already
    per-PID — `src/CardShopCoop/Util/FileLog.cs:24-32`) plus Unity `Player.log`.
  - On failure, optionally enable/attach the managed debugger (§9).

### 3.3 Repository layout

```
tests/CardShopCoop.TestHarness/     # BepInEx plugin (interpreter shell), NOT in any release package
tests/CardShopCoop.TestRunner/      # external orchestrator, NOT shipped
tests/tests/*.lua                   # coordinator scripts (the tests)
tests/manifests/*.json              # which instances + which script
docs/test-harness-design.md         # this document
```

**Packaging safety:** `scripts/build.sh` builds an explicit project list
(`CardShopCoop.Api`, `CardShopCoop`, `CardShopCoop.CustomTv`) and copies named zips — it does not
glob `src/`, so adding the harness under `tests/` cannot leak into `dist/`. Add a CI assertion that
no `CoopTestHarness` DLL appears in `dist/`.

---

## 4. The interpreter shell: fixed code, variable tests

This is the core of the design and the reason a debugger is not needed.

**Fixed in the DLL (changes rarely):** the control server, main-thread scheduler, interpreter,
reflection bridge, curated primitives, tracer, clock control.

**Variable on disk (changes freely):** the tests, written as scripts read fresh on every run. Edit a
script, re-run it — **no rebuild, no restart, no redeploy.**

### 4.1 Script engine choice

- **Recommended: MoonSharp (Lua 5.2).** Pure managed C# (`net472` / netstandard2.0), no native
  dependency, designed for embedding and sandboxing, easy .NET interop, and widely used in Unity
  modding. Ships as one small `MoonSharp.Interpreter.dll` next to the test-only harness.
- **Alternative: Jint (JavaScript).** Also pure managed; heavier, but familiar syntax if preferred.
- **Fallback with no third-party dependency:** a minimal built-in step interpreter. Less
  expressive (no real control flow/functions); only if a test-only NuGet dependency is
  unacceptable.

### 4.2 Two levels of control

1. **Instance shell (in-game).** A generic, main-thread-safe "evaluate this" server:

   | command | purpose |
   | --- | --- |
   | `eval(code) -> value` | run code on the main thread; return a serializable result |
   | `wait_for(predicate, timeout_ms) -> bool` | yield the running script until the predicate holds at a frame boundary |
   | `snapshot(spec) -> data` | read a named set of game/mod values |
   | `trace() -> events` | the recorded wire/event log |
   | `advance_frames(n)` | step the loop a fixed number of frames (§6) |
   | `set_lag(ms, jitter_ms)` | set `ArtificialLagMs` / `ArtificialJitterMs` |
   | `log(line)` / `status` | diagnostics |

2. **Coordinator script (runner-side Lua).** The test itself. It holds the sequencing and the
   cross-peer logic, driving each instance through its shell:

   ```lua
   -- tests/tests/warehouse_box_add.lua
   host:wait_for("ctx.in_game()", 120000)
   host:eval("ctx.host()")
   joiner:wait_for("ctx.in_session()", 60000)
   joiner:wait_for("ctx.in_game()", 120000)

   host:set_lag(60, 0)
   local box = host:eval("game.place_warehouse_box(0)")   -- returns the stable box id
   host:advance_frames(120)

   -- cross-peer assertion, evaluated by the runner from both snapshots
   assert_converged("warehouseBox", box)
   ```

The split is deliberate (Separation of Concerns): the in-game shell knows *how to touch the game
safely on the main thread*; the coordinator knows *what the test does*. New game actions are almost
always expressible with the reflection bridge from a script — so the DLL does not change.

### 4.3 Driving: in-process API calls, not OS input

Actions are performed by evaluating **the game's and mod's own APIs on the main thread** (the same
calls the game makes), including the game's event bus and Unity's Input System where appropriate.
This is reliable, addressable (by object/id), and independent of window focus — unlike OS-level
synthetic input, which only reaches the focused window and would require per-instance input routing
(Nucleus-style API hooks, which are themselves injected DLLs). If a specific test genuinely needs
end-to-end input-device coverage, that is a separate, narrower concern layered on top.

---

## 5. Observation seams

Preference order, least to most invasive:

1. **Public `CoopApi` surface** (`src/CardShopCoop.Api/CoopApi.cs`, `ICoopContext` in
   `CoopContext.cs`): role, in-session, in-game, peer ids, send/broadcast. Enough for lifecycle
   assertions.
2. **Reflection** into core internals for module state, via the shell's reflection bridge (the
   `DebugTools` pattern). `SaveTransferApi.ForceLoadSlot` is already public
   (`Modules/SaveTransfer/SaveTransferApi.cs:49`).
3. **Harmony tracing in the harness** for the wire/event trace: patch `MessageRouter` dispatch and
   the state-apply/rollback points *from the harness assembly only*, so there is **zero production
   change** and it is trivially removable.
4. **An explicit test observer hook in core** — only if Harmony tracing proves too brittle. This is
   a real production change and would be a patch release to the mod; treat it as a last resort and
   justify it independently.

Existing affordances the harness reuses rather than reinventing:

- `-coopautohost=` / `-coopautojoin=` (`src/CardShopCoop/CoopCore.cs:327-348`, `2153-2189`).
- `ArtificialLagMs` / `ArtificialJitterMs` (`src/CardShopCoop/CoopPlugin.cs:137-140`).
- The `DebugTools` module + gated cheat manager for fast state setup (`Modules/DebugTools/*`,
  host-only, gated behind two hidden config flags).
- Game events via `CEventManager` (as `CoopCore.RegisterEventListeners` does).

---

## 6. Determinism

A real-time Unity game is not naturally deterministic; the harness aims for *repeatable enough*, not
bit-exact.

- **Frame stepping:** pause the player loop between steps and advance a fixed number of frames.
  `Time.captureFramerate` (fixed `deltaTime`) plus `Time.timeScale` is the likely mechanism.
  **[VERIFY]** the exact combination that yields stable fixed-step behavior in this build.
- **Yield-based waits, not sleeps:** `wait_for` resumes on a frame boundary when its predicate
  holds. No wall-clock delays.
- **Network:** KCP is reliable-ordered, so *ordering* per stream is deterministic; *timing* is not.
  Assertions key off events/state, not arrival time. For robustness runs, set `ArtificialJitterMs`
  to 0 and a fixed `ArtificialLagMs`.
- **RNG:** seed `UnityEngine.Random` / `System.Random` via reflection where the game exposes it.
  Host-authoritative outcomes mean host seeding is usually sufficient for convergence assertions.
- **Startup ordering:** the coordinator waits on the host's readiness *event* before triggering the
  joiner's join step, rather than relying on the auto-args' built-in delays.

---

## 7. Headless strategy

Three tiers, cheapest and safest first:

1. **Windowed, positioned off-screen (baseline).** Two normal windows moved off the visible area.
   The mod already forces `Application.runInBackground = true` (`CoopCore.cs:1323`) and has no focus
   handler, so an unfocused instance keeps simulating. Works on any GPU machine. **Default.**
2. **`-batchmode` (windowless, GPU-backed).** **[VERIFY]** whether the game boots, loads a save,
   and hosts/joins. There is no batchmode code path in `Assembly-CSharp` (grep: zero hits) and no
   server flag in `boot.config`.
3. **`-nographics` (true headless).** **[VERIFY, likely to fail]** the game is render-heavy (URP,
   Cinemachine, post-processing) and touches `Screen.width/height` (e.g.
   `decompiled/1.02/AspectRatioPositionOffset.cs:37`). Only pursue if tiers 1–2 prove insufficient.

**CI implication:** the current runner is Linux Docker
(`self-hosted-runner/docker/Dockerfile`, `FROM myoung34/github-runner:debian-trixie`) and only
builds/packages. It cannot run the Windows Unity game. Runtime tests need a **Windows** runner with
a GPU or a virtual display — separate infrastructure work, its own milestone.

---

## 8. Test taxonomy

- **Session lifecycle:** host start, join accept, join reject (version/catalog/hash mismatch),
  disconnect, reconnect, rehost.
- **World transfer:** host snapshot → joiner borrowed slot −100; save/sidecar integrity.
- **Per-module sync (golden scenarios):** warehouse boxes, shelf label/item deltas, furniture
  place/move/box, register trade, card tables, workbench, NPC, etc. — one script per module,
  asserting host↔joiner convergence by **stable id**.
- **Prediction/rollback:** force a rejection and assert the client reconciles to authority instead
  of inverting a snapshot.
- **Lag/jitter robustness:** replay the sync scripts under `ArtificialLagMs` / `JitterMs`.
- **Cross-build:** only the *rejection* path (differing `EObjectType` catalog) is testable today.
- **Regression pack:** one script per fixed bug, named after the CHANGELOG entry.

Entities are always referenced by the mod's **stable ids**, never by heuristic matching — the same
rule the mod itself follows (`AGENTS.md` code standard 5).

---

## 9. Debugger integration (triage, not orchestration)

The enabler is already installed: **Doorstop 4.3.0** exposes a Mono soft-debugger server through
`doorstop_config.ini`:

```ini
[UnityMono]
debug_enabled = true
debug_address = 127.0.0.1:10000   # use a distinct port per instance
debug_suspend = false             # true to break at first line
```

With it on, dnSpy / JetBrains Rider / VS Code can attach to a **retail** build.

- The shipped `Assembly-CSharp.dll` has **no `.mdb`/`.pdb`** (verified: only the DLL under
  `Card Shop Simulator_Data/Managed`), so there is no line-level stepping in *game* code. Field and
  type **names are still readable** (they live in metadata), so inspection works; build the harness
  and the mod with symbols (`DebugType=portable`) so *mod* code steps normally.
- A debugger is stop-the-world and per-process. **Do not suspend a peer mid-session** — the 10 s
  presence window (`Runtime/CoopRuntimeContext.cs:78`) and KCP timeouts make it dangerous. The shell
  observes without suspending, which is exactly why it, not the debugger, is the driver.
- Practical behavior: on a failed step the runner archives logs, leaves the instances alive, and
  prints attach instructions (host port, joiner port).

---

## 10. Alternative considered: a debugger-only driver

Recorded so the decision is not relitigated. A debugger-only framework would attach two Mono
soft-debug sessions and drive the game by setting breakpoints and using `InvokeMethod` while
stopped, reading state by inspection. It is **technically possible** (the game is Mono, not IL2CPP)
but rejected because:

- **Invocation only works while stopped at a breakpoint** — no fire-and-forget; every action is
  stop → invoke → resume, which is slow and timing-fragile across two processes.
- **Suspending a live peer can trip the other side's timeouts** (§9) — the fatal problem for a live
  co-op session.
- **No game symbols** → game breakpoints are IL/token-based and break on every game update.
- **Automating it means scripting the SDB protocol** (`Mono.Debugger.Soft`) against two servers —
  more code than the shell, and less reliable.
- **The premise is already moot:** Doorstop, BepInEx, and the mod under test are all injected DLLs;
  the harness only adds one that is test-only and inert by default.

The interpreter shell delivers the property the debugger was wanted for — **read/act on demand
without recompiling or restarting** — while remaining main-thread-safe and non-suspending. The
debugger stays for triage.

---

## 11. Milestones

| # | Deliverable | Proves |
| --- | --- | --- |
| **M0** | Spike: two loopback instances windowed; then `-batchmode`; then Doorstop debugger attach | the three unknowns; picks the headless tier |
| **M1** | Shell + control channel (`ping`, `eval`, `status`, `snapshot`); runner launches 2 instances and evaluates code in both | orchestration + isolation |
| **M2** | Interpreter + yield-based `wait_for`; coordinator script engine; first test (load→host→join→fully-joined) | the scripting model end to end |
| **M3** | Tracer + snapshot diff; first per-module sync test | observability |
| **M4** | Determinism (frame stepping, RNG seed) + lag/jitter matrix | repeatability |
| **M5** | Debugger-on-failure, JUnit reporting, Windows CI runner | usable at scale |
| **M6** | Regression pack across modules | coverage |

---

## 12. Risks and open questions

| # | Risk | Mitigation |
| --- | --- | --- |
| 1 | Game may not run headless at all | off-screen windowed baseline; CI needs a display/GPU |
| 2 | Two-instance config/save isolation | runner provisions per-instance dirs; exclude `BepInEx` config |
| 3 | Script engine dependency / sandboxing | MoonSharp is pure managed; sandbox the script (no IO/OS access); it runs test-only code we author |
| 4 | Reflection bridge drift after game updates | curated primitives are versioned; generic bridge is best-effort; failures are loud |
| 5 | Real-time non-determinism | yield-based waits; fixed frame stepping; seeded RNG |
| 6 | Harness leaking into player packages | keep under `tests/`; CI assertion on `dist/` |
| 7 | Test-only core hooks creeping into production | prefer Harmony tracing; any core seam is a justified patch |
| 8 | Cross-build peers untestable | only same-build pair; cross-build limited to rejection path |
| 9 | Steamworks on a second same-account process | affects achievements only; IP session unaffected **[VERIFY]** |

---

## 13. Decisions grounded in repo conventions

- **Tests are data/scripts, hot-editable** — no rebuild or restart to change a test.
- **Event-driven, no polling/timers** in the shell's steady state; `wait_for` is frame-yielded.
- **No game-specific state stored** beyond what assertions read directly from the game/mod.
- **Stable ids only** — entities referenced by the mod's stable ids, never heuristics.
- **Fail-loud** — control-protocol errors, script errors, and assertion failures surface with
  context; no silent passes.
- **Reuse over reinvention** — `CoopApi`, the `MainThreadDispatcher` pattern, `DebugTools`,
  `SaveTransferApi`, the existing auto-args, and the existing lag injection.
- **No version bump for the harness itself.** If a production seam is ever added for it, that is a
  patch release of the mod per the versioning rules in `AGENTS.md`.
