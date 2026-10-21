---
name: Goal-focused development
description: A goal-first development loop - write the behaviour goals as an explicit checklist that is the only source of truth, make the smallest edit per goal, delete everything the goal makes obsolete, and run a broad adversarial review after each pass until it reports no critical or high findings. Includes the event-replay sync pattern (host thinks, client replays) as the worked reference. Use when driving a large migration or refactor to completion, and when the work must stop being a series of reactive patches.
---

# Goal-focused development

How to drive a large change to a real end state instead of accumulating reactive patches.

The loop is:

1. **Write the goals down** as a numbered behaviour checklist, in the user's own words, and declare it
   the **only** source of truth for scope.
2. **Make the smallest edit that completes the next goal.** Build, format, deploy after each step.
3. **Delete what the goal made obsolete** in the same change.
4. **Review broadly, fix, re-review** until the reviewer reports no critical or high findings.
5. **Stop when the checklist is green**, not when the diff looks plausible.

Everything below is the concrete form of that loop, using CardShopCoop's NPC sync as the worked
example.

## The checklist

- Number every behaviour in plain language ("walking to a shelf and picking up an item to buy"), not
  in code terms.
- Two boxes per item: **first = implemented**, **second = an independent review examined that
  mechanism and found it sound**. Neither box means runtime-tested; the live run is a separate gate.
- The document that holds it must say it is the only scope source. Anything not on it is out of
  scope; anything on it is not optional.
- When a new behaviour is discovered, add it to the list before implementing it - never in a commit
  message or in conversation only.

## The reference pattern: event replay sync

For syncing a host-driven entity to a client, the end state is: **the host is the only one that
thinks; the client replays.** A remote entity is a body the client moves. It runs **no game logic**:
no `Update` state machine, no decisions, no timers, no economy, no manager/report writes, no
re-path, no re-decide.

Everything it does is one of:

1. **walk** the host's path,
2. **face** the direction of travel, and then the host's arrival yaw,
3. **play** an animator write the host made,
4. **hold** the bag / carried object the host has,
5. **be** where the host put it (sit, stand, man a station, despawn).

If the host did not send it, the client does not do it.

### Three hard constraints

- **No polling.** The only per-frame work advances a path that was already fed. Iterate the entities
  that currently have a path, not the whole pool. Never ask the game for state.
- **No self-healing.** No retries, no deferred application, no retention, no re-derivation, no
  "apply when something becomes ready". A lost or misordered message is a bug to fix at the source.
- **Nothing else.** A remote entity writes only: transform, animator, carried objects.

If a fix needs an "if the client didn't get X" branch, the fix is wrong.

### Message set

One message per host event; no reconstruction payloads.

| Message | Carries |
|---|---|
| Spawn | identity, look, pose, speed, carried contents |
| Despawn | identity |
| State | presentation state enum only (animator bits) |
| Path | waypoints + arrival yaw |
| Contents | full bag / inventory after a take |
| Carry | held object id / size / open state |
| Animator | one parameter write |
| Sit | station key, seat, fee, started |
| Stand | station key, seat, elapsed, fee, result |
| Arrival | station key (the host's placement/transition point) |
| Popups | speech text, money amount |
| Baseline | spawns + current state/path/contents per active entity |

Fields only vanilla logic reads (target transforms, timer maxima, shelf/compartment references,
queue slot indices, decision enums beyond presentation) are **not** on the wire.

### Client implementation

One tick, three jobs:

1. advance each moving body along its fed path (`MoveTowards` at the fed speed) and face travel;
2. when the path ends, slerp to the fed arrival yaw;
3. drain a per-entity **arrival queue** of positional replays (sit, stand, arrival) in order, only
   when the path is done. That queue is what makes "replay after the previous event finished" work
   without timers.

Nothing else runs on the client for that entity.

### The deletion rule

When you suppress the driver, **delete everything that only existed to satisfy it** in the same
change:

- the suppression patches for methods that can no longer be called,
- the scoping guards that only protected those methods,
- the fields fed only to those methods (and their wire fields, host fills and client writes),
- the recomputation/derivation helpers (queue target re-aiming, target transforms, timer feeds),
- the retention/retry machinery.

Deletion is compile-driven: after suppressing the driver, every remaining reference either matters or
is an error. Prefer deleting a whole subsystem over leaving it inert.

## Work order

1. **Write the behaviour checklist first** (see above) and put it in the design doc.
2. **Make the smallest edit that makes the next behaviour work.** Build, format, deploy after each
   step. Never ship an intermediate state where the driver is suppressed but its replacement event
   does not exist yet.
3. **Cover the checklist**, then run the review loop.
4. **Adversarial review loop.** Spawn a read-only reviewer scoped to the **whole subsystem** — never
   to the change you just made. Do not tell it what you edited, do not list "fixes to verify", do not
   mention a diff. Its prompt describes the system and the behaviours that must work, and asks for
   defects. If a review prompt contains the words "just landed" or "verify this fix", it is the wrong
   prompt: it anchors the reviewer on your assumptions and misses whatever you did not think to ask.
   Require:
   - **CRITICAL/HIGH** — "NONE" only after enumerating the system; each finding needs `file:line`,
     the exact executable path, the player-visible symptom, the smallest correction, and
     PROVEN vs HYPOTHESIS.
   - **MEDIUM/LOW**.
   Fix every CRITICAL/HIGH, then re-run the same broad review. Repeat until it returns "NONE".

## Verification discipline

- Read the **decompiled game baselines** for every supported build. Use reflection for anything that
  exists on only one build; never a direct reference, or the DLL fails to load on the other.
- A grouped `[HarmonyPatch]` class installs only **one** target on this game's Harmony. Install each
  target individually (see `NpcPatchInstaller`) or split the class.
- Suppression correctness cannot be proven by reading alone. The checklist's second box is a code
  review, not a test; the live run is the gate.

## Anti-patterns that caused the original complexity

- Suppressing decision methods one at a time while the state machine still runs: every pass finds
  another branch. Suppress the driver instead.
- Feeding vanilla fields so its gates pass, instead of sending the outcome as an event.
- Per-frame scans of the whole entity pool.
- Retaining, retrying, deferring or re-deriving state the host already sent.
- Scoping economy at call sites instead of never running the economy on the client at all.
- Growing a pool-reuse field reset list instead of restoring a known-clean default.

## Reference implementation

`docs/npc-replay-plan.md` (behaviour checklist + design) and
`src/CardShopCoop/Modules/Npc` in CardShopCoop: host emitters in `NpcHostBehaviour.cs`, the replay
handlers and the tick in `NpcClientBehaviour.cs`, the wire DTOs in `NpcMessages.cs`.

