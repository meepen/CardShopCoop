# Adversarial Review — `v1.1.0` → `1.2.0` (branch `1.2.0-squash`)

- **Range:** `git diff v1.1.0..HEAD` — tag `v1.1.0` = `0f2391b`, HEAD = `6a37432` (13 commits, ~12.5k insertions / ~9.4k deletions).
- **Method:** two independent adversarial review passes with complementary lenses (foreground: module lifecycle/routing/transport/UI/util/build + wire/versioning; background: box/possession/transfer protocol + hand escrow), followed by direct source verification by the orchestrator of every High finding. Read-only; no source files were modified.
- **Overall verdict: REJECT** — one verified high-severity lifecycle regression breaks staff/settings sync for every session after the first in a game process. The transfer-protocol High findings are real but lower-likelihood; they should be hardened before the subsystem is considered done.

---

## Verified findings (orchestrator-confirmed from source)

### V1 — HIGH — `SettingsSync.Instance` / `StaffSync.Instance` stay `null` after the first teardown; all later-session staff/settings actions silently no-op

Brought by: foreground pass. **Verification: confirmed directly.**

- `SettingsSync.cs:73-75` sets `Instance` only in the constructor; it has no `Start()` override. `Dispose()` (`:102-107`) nulls `Instance`.
- `StaffSync.cs:70-72` same pattern; `Dispose()` (`:135+`) nulls `Instance`.
- Modules are constructed **once** as `readonly` fields (`CoopCore.cs:167-185`). Each session builds a fresh `CoopModuleRegistry` over the same instances (`CoopCore.cs:1742+`, `CreateModuleRegistry`), and `CoopModuleRegistry.Start()` calls `module.Start()` — but `CoopModule.Start()` is an empty `virtual` (`CoopModule.cs:21-23`).
- `Shutdown()` disposes the registry and sets it to `null` (`CoopCore.cs:3410-3417`); `DeactivateLiveModuleHooks()` does the same on aborted starts (`:1839-1844`).

**Failure scenario:** In one game launch — host once, stop hosting, host again (or any failed join/host attempt followed by a retry). From the second session on, `SettingsSync.Instance`/`StaffSync.Instance` remain `null`:
- Host clicking a worker does nothing (`StaffSync.WorkerMousePressPrefix`).
- Guest Hire/bonus/fire/task/price/pack ops are dropped with `"SendOp not wired - ignored"`.
- Deco buy/place/remove, equip, game-event fee, cashier, table-number changes send nothing and are reverted by the host's ~1.5 s settings heal.

Modules that do it correctly (`ShopStateSync`, `CardShelfSync`, `TvSync`, `PlayTableSync`) override `Start()` to re-publish their instance.

**Fix:** add `public override void Start() => Instance = this;` to both `SettingsSync` and `StaffSync`.

---

### V2 — HIGH — A take that could not be escrowed (`EscrowToken == 0`) is unreconcilable; a host rejection duplicates items

Brought by: background pass. **Verification: confirmed directly.**

- `HandEscrow.ReserveTake` returns `0` when it cannot find exactly `count` unreserved hand items of the type (`HandEscrow.cs:17-43`).
- `PendingTransferLedger.Begin` still records the entry with `EscrowToken = 0`, only logging (`PendingTransferLedger.cs:47-51`).
- `HandEscrow.ResolveTake` returns immediately for `token <= 0` (`HandEscrow.cs:45-48`).
- `BoxEngine.ClientApplyTransferResult` calls `_transfers.ResolveTake(pending, msg.AcceptedDelta)` unconditionally (`BoxEngine.cs:963-977`) with no token-0 compensation.

**Scenario:** client takes N items; reservation fails (stale baseline/type, or the matching hand items are already reserved); the `BoxUpdate` still goes out. If the host rejects/clamps (`AcceptedDelta == 0`), the client's phantom items are never removed while the host still holds them — duplication. The ledger itself warns `"rejection will not be reconciliable"`, confirming this is a known gap.

**Fix:** on a token-0 take, either reconcile a reject-all locally or refuse to send the transfer and leave the box unchanged.

---

### V3 — HIGH — A take whose result never arrives is never re-synced (TTL duplication)

Brought by: background pass. **Verification: confirmed directly.**

- `BoxEngine` constructor's ledger `Expired` callback only sets `_resyncRequested = true` when `pending.RequestedDelta > 0` or a snapshot was suppressed (`BoxEngine.cs:191-205`).
- For a **take** (`RequestedDelta < 0`) with no suppression, `PendingTransferLedger.Prune` removes the entry and `ExpireTake` merely unreserves the items (`PendingTransferLedger.cs:101-121`, `HandEscrow.cs:83-92`). No resync is requested.
- The `BoxTransferResultMessage` route's failure `heal` is wallet/progress only (`CoopCore.Routing.cs:385`), so the retry-exhausted path does not re-baseline the box either.

**Scenario:** the host never answers a take (handler faults through all retries, or the result is lost). After the 15 s TTL the client keeps the items in hand and its box mirror stays reduced; the host box still holds them. Duplication with no correcting snapshot.

**Fix:** on an expired take, force an authoritative box resync and reconcile/destroy the escrowed delta; make `BoxTransferResult`'s `heal` request a box resync/full snapshot.

---

### V4 — MEDIUM — Reliability `heal` callbacks for box messages target the wrong subsystem

Brought by: background pass. **Verification: confirmed directly.**

- `CoopCore.Routing.cs:377-385` registers `BoxTransferResultMessage` with `heal: () => { _coinHeal = 999f; _progressHeal = 999f; }`.
- `CoopCore.Routing.cs:386-394` registers `BoxSnapshotMessage` with the same wallet/progress heal; it is non-retryable, which is correct, but a throw inside `ClientApplySnapshot` therefore drops the authoritative snapshot with no box-level recovery.

**Fix:** `heal: () => _boxEngine?.RequestFullSnapshot()` for `BoxSnapshot`; a box resync for `BoxTransferResult`. Consider per-box fault containment inside `ClientApplySnapshot`.

---

## Agent-reported findings not yet independently verified line-by-line

These are plausible from the passes but should be confirmed before acting:

| # | Sev | Area | Summary |
|---|-----|------|---------|
| A1 | Medium | `CoopCore.cs:3799-3800` | Transport queue is drained into `_dispatchBuf` without a cap; only application work is budgeted (256/frame). A sustained inbound overload grows the buffer unbounded. Cap the drain / coalesce snapshot types. |
| A2 | Medium | `CardShelfSync.cs:497-520, 341-368` | After `PendingResendMax` the client keeps `_pendingPlacements` and stops resending; later host snapshots are ignored for that key, leaving a card on the display forever with no feedback. Bank/flag it. |
| A3 | Medium | `Net/Msg.cs:168-170` vs `MessageRegistry.cs:43-46`, `Msg.TryDecodeFrame:175-180` | Comment promises "once per type" unknown-message logging, but every occurrence (and every malformed frame) is logged — log-flood risk. Deduplicate. |
| A4 | Medium | `BoxEngine.HostApplyUpdate` / `WorldSync.ApplyTransferRequest` | Retryable box/shelf handlers mutate host state before replying and are not keyed by `TransferSeq`; a mid-handler throw then retry re-applies the delta. Add `(connId, TransferSeq)` dedupe or make handlers transactional. |
| A5 | Medium | `BoxEngine.cs:1170-1201`, `BoxIdentityMap.Reset` | Client id→object binding is not re-validated when an id is already bound; a host-only `Reset` that reuses id 1 can apply box A's state to box B. Latent unless a host reset can occur mid-session. |
| A6 | Low | `HandEscrow.Reset:168-195` | Queued `ToHand` items are destroyed at teardown if they could not attach; prefer banking them. |
| A7 | Low | `CardBoxOps.cs:90-91, 170-183` | Host-carried collect returns no result (client click is a silent no-op); all rejections print the same "graded certs have drifted" text. |
| A8 | Low | `FurnitureBoxOps.cs:114-163` | Local destroy before a retryable op with only `ForceNextTick` heal; if the host never processes it, re-mirroring may fail. |
| A9 | Low | `ContainerSync.cs:1548-1628` | `_suppressedStorageDestroy` armed in `StoreBoxPrefix` can leak if vanilla throws before the postfix clears it. |
| A10 | Low | `Util/Swallow.cs:51` | Logs type+message without the stack trace, contrary to the repo's loud-stack policy; now used broadly. |
| A11 | Low | `LagTransport.cs:135-138` | `Stop()` does not clear `_delay`/`_incoming`; harmless today (fresh instance per session) but a reuse hazard. |
| A12 | Low | `CoopCore.cs:162-176` | Nested duplicate `if (core.RegisterLine.Length > 0)` — dead code. |

---

## Wire / versioning compliance — PASS

Both passes independently confirmed, and the orchestrator agrees:

- `Directory.Build.props` bumped `1.1.0 → 1.2.0` (minor), justified by new/removed `MsgType`s (`BoxUpdate=89 … ShelfTransferResult=99`) and field-layout/semantics changes (byte→int ids, new `PlayerState` camera fields, etc.).
- `Msg.WireVersion` derives `major*100+minor` = `101 → 102` (`Net/Msg.cs`). Handshake enforces both wire version and exact plugin-version equality (`CoopCore.cs:4469-4484`, `4744-4749`).
- No patch-level wire change was smuggled into the range. Every `MsgType` has a DTO and a handler; removed enum values are unreferenced.
- CHANGELOG `## 1.2.0` ends with **Both players must update.** Minor gap: the LATENCY TESTING sliders are not mentioned.

## Build / CI / hygiene — PASS (foreground pass, independently executed by that agent)

- `dotnet build … -c Release -t:Rebuild` succeeded (0 warnings/0 errors) against the real game assemblies; `dotnet format … whitespace --verify-no-changes` exit 0; locked-mode restore exit 0.
- CI branch trigger correctly widened to `[master, main]`; committed `tools/Launcher/bin`+`obj` removed and `.gitignore` entries added; working tree clean.

## Test gaps

1. `Start → Dispose → Start` on the same module instance (would have caught V1).
2. Lost/duplicated `BoxTransferResult` (V2/V3), including token-0 takes.
3. Handler fault injection after content mutation (A4).
4. Host-only `Reset` with a live client (A5).
5. Disconnect mid-transfer; add-rejected while in a modal hand state then teardown.
6. A reflection test asserting every `MsgType` has a DTO + handler.

The new `LagTransport` (`ArtificialLagMs`/jitter) is the right harness for (2) and (5) but is not exercised by any automated test.

## Open questions

1. Is a host-only `BoxEngine.Reset` (scene load) possible while a client stays connected? If yes, A5 is live; if sessions always end on either side's world load, it is latent.
2. Is `Swallow` intentionally dropping stack traces, or should it log the full exception with the existing cooldown?
3. Should an unconfirmed card placement (A2) be banked with a user-visible notice rather than left diverged silently?
4. Is `LagTransport` intended to wrap production transports unconditionally, or only when lag/jitter is non-zero?
