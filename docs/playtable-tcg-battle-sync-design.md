# Playable TCG battle sync — design (co-op duels + PvP)

Status: proposed design, 2026-10-13. Supersedes the F3 "untouched" decision in
`docs/1.00-followups.md` (§5 decision 1). Vanilla architecture facts live in
`docs/playtable-tcg-vanilla-architecture.md`; this document only states the design.

Scope confirmed with the user: **both** independent co-op duels (each player may duel a waiting
customer) **and** direct player-vs-player duels at the same table. Tournament play in co-op is
phase 3.

## 1. Goals and non-goals

Goals:

1. A guest can challenge a waiting customer and play the full duel, with the outcome, table fee
   payout, tournament result, report counters, and gift consistent with the host's world.
2. Two players can duel each other at the same play table, with the vanilla duel UI, animations,
   and rules on both machines.
3. No desync: deterministic replay with replicated randomness, turn-checkpoint hashes, and
   authoritative repair when they disagree.
4. All house rules hold: stable IDs on the wire, host-authoritative, one intent before local
   mutation, one baseline per join, generic rollback only, no polling/timers/ACKs.

Non-goals (first release):

- Spectating another player's AI duel (the same event stream enables it later; not built now).
- Hidden-information protection against memory inspection — consistent with the mod's trust
  model (cheats are already blocked as a client-side policy, F4). The duel UI never reveals the
  opponent's hand/deck/guardians, so full-state messages are safe to send to duel participants.
- Two duels on one machine (vanilla `PlayTableGame` is a singleton; vanilla forbids it too).
- Cross-build handshakes (already a global non-goal: exact version handshake, both players update).

## 2. Two duel modes, one module

All of this lives in `Modules/PlayTable` (extending the existing lease/visual module).

### Mode A — AI duel (player vs customer)

The dueling player's machine **simulates the duel locally**, exactly as vanilla does today. The
opponent is a data-driven AI whose randomness is local and whose state is ephemeral (vanilla never
saves duels), so nothing about the duel's interior crosses the wire. What crosses:

- The duel **result** (win/lose/draw, conceded), sent as one intent after vanilla's own
  `ReportWinner`/`FinishLeaveGame` — the same post-hoc result pattern the Trade module uses
  ("the actor's own vanilla run produces the result; the host records and broadcasts it").
- The host then applies, host-side only: customer table-fee payout
  (`Customer.PlayTableGameEnded`), tournament round registration (`InteractablePlayTable.
  StopTableGame`'s `CustomerTournamentData` writes + `CustomerManager.OnCustomerFinishTournamentRound`),
  report counters, and the **gift roll** (host RNG) with a host-side grant to the dueling player.
- The client suppresses the local copies of those mutations (see §5).

Rationale: the customer AI, deck generation, and shuffle all consume global `UnityEngine.Random`
on the simulating machine; replicating them would mean streaming the whole duel for zero gameplay
benefit, since the opponent is not a person.

### Mode B — PvP duel (player vs player)

The **host simulates; both machines replay deterministically.** Both machines run the vanilla
`PlayTableGame`/`PlayCardSet` pair as the rules engine and renderer, driven by an identical
ordered action stream, with the duel's randomness replicated instead of rolled:

- The host deals the duel: it rolls the coin flip and builds both decks, and sends the deck lists
  and first-turn result in the duel-start packet. Both machines seed their local `PlayCardSet`
  zones from that packet, so the Fisher-Yates shuffle and coin-flip RNG sites are overridden, not
  seeded (seed-only lockstep stays dead — global RNG is shared with unrelated systems).
- From then on every player decision is an action message. Both machines apply the same actions in
  the same order through the same public vanilla APIs (`ReleaseDraggingCard`,
  `OnClickElementButtonArea`, `OnPressEndTurn`, `SelectionMulliganOrKeep`, `OnPressConfirm*`), so
  vanilla's own rules, effect queues, and animations produce the same state locally.
- Ordering is safe without a sequencer: the duel is strictly single-actor (turn flags +
  `m_IsWaitingResponse` gate forced decisions), so the host validates "is it this player's
  decision" and applies actions one at a time; two players can never legally act concurrently.
- Divergence insurance, not replication: the host piggybacks a zone-count + hash checkpoint on
  each turn-switch action. On mismatch the guest reconciles from an authoritative full-state
  message (the normal "host sends authoritative state with empty prediction id" pattern, not a
  new rejection type).

Why replay instead of result-streaming: vanilla's state transitions are coroutine- and
animation-gated (`WaitUntil`, `m_IsStoppingAction`), and every mutation rides the cosmetic draw
queue drained in `Update`. Driving the *same* vanilla code with replicated inputs keeps the
presentation correct for free; streaming results would fight the animation gates and cost far
more wire per duel.

## 3. Identity and state

- **Duel id**: reuse the existing `PlayTableMatchEntry.MatchId` lease (`"playtable-<conn>-<nonce>"`);
  the lease gains phases `StateDuelActive` beyond `StateStarted` so the existing release-on-
  disconnect/leave paths keep working unchanged.
- **Table identity**: the existing stable placement key (never `m_Index`).
- **Card identity**: a stable per-duel card instance id, assigned by the duel's dealer at
  `PlayCardSet.GetNewCard3d` (the single point where duel cards materialize), kept in a
  network-id → `InteractableCard3d` map (the explicitly allowed lookup-table state). Wire identity
  is `(cardSaveIndex, expansionType, isDestiny)` — the same compact form `CPlayerData.
  GetCardSaveIndex/GetCardData` round-trips — plus the instance id. No heuristics; every card a
  peer references by id was materialized from a packet that carried its full identity.
- **Canonical sides**: the duel has canonical Side A/B; each machine maps canonical → local
  (`PlayCardSetPlayer` is always the local human). All wire payloads speak canonical sides; only
  the mapping at apply time is local. This is the classic local-relative bug source and gets its
  own test.
- No new persistent state: duels remain unsaved (vanilla behavior). The only added persisted
  concept would be nothing — open-duel and lease state die with the session, like vanilla.

## 4. Message inventory (patch-version bump; namespace `CardShopCoop.Modules.PlayTable`)

| Message | Direction | Contents / purpose |
|---|---|---|
| `PlayTableDuelBaselineMessage` | host → joining peer, once (`[OnFullyJoined]`) | Open-duel tables (table key + waiting player), live duel summaries (mode, participants, phase). Extends the join baseline, not a second baseline path. |
| `PlayTableDuelOpenIntentMessage` | client → host | Open/cancel an open duel at a table (the "I'll take a challenger" state). Host validates the table is free, marks it, publishes a seat/visual delta with the new waiting flag; suppresses customer booking on that table while open. |
| `PlayTableDuelStartMessage` | host → duel participants | Mode B handshake: duel id, table key, canonical side per connection, both deck lists (compact card refs), coin-flip first-turn result. |
| `PlayTableDuelActionIntentMessage` | actor → host | One player decision: mulligan keep/mulligan, play card instance id → element area, end turn, confirm action-resolve / element-area / search selection (index lists), concede. |
| `PlayTableDuelActionMessage` | host → participants | The validated action, applied on both machines in order; carries the actor's canonical side. Doubles as the intent's echo for prediction reconciliation. |
| `PlayTableDuelCheckpointMessage` | host → participants | Piggybacked on turn-switch actions: per-side zone counts + state hash. On mismatch the receiver requests repair by sending nothing — the host detects nothing; the *client* detects and the host's next authoritative state message repairs (below). |
| `PlayTableDuelStateMessage` | host → participant (empty prediction id) | Authoritative full duel zones (ordered card identity lists per zone, HP, shield, freeze, boosts, turn flags). Used for join-mid-duel (not supported — late joiner waits outside), checkpoint-mismatch repair, and post-rollback reconciliation. |
| `PlayTableDuelResultIntentMessage` | dueling client → host | Mode A result: duel id, outcome, conceded, play time. Host applies payout/tournament/report/gift; client has already suppressed its local copies. |

Rejection uses the one generic `PredictionRollbackMessage` plus an immediate authoritative
`PlayTableDuelStateMessage` — no new rejection types, per house rules.

## 5. Vanilla seams (hook map)

Existing module patches keep their jobs (`OnRightMouseButtonUp` postfix, `FinishLeaveGame`
postfix, `StopTableGame` postfix, `EvaluateEndGameGift` prefix, `ShelfManager.InitPlayTable`
postfix). New or changed:

| Seam | Patch | Purpose |
|---|---|---|
| `InteractablePlayTable.OnRightMouseButtonUp` | prefix | Mode B: route the second player into the duel handshake instead of vanilla's NPC-only gate; Mode A unchanged. Also implements the open-duel entry (no occupant yet). |
| `PlayCardSet.m_IsAIControlled` | reflection set at duel setup | Mode B: force `false` on the remote-player set on both machines; restore after. (Vanilla never assigns it in code — it is prefab-only.) |
| `PlayCardSet.EvaluateEnemyAI` | prefix skip | Mode B: the enemy set is human-driven; never let the AI tick. |
| `PlayCardSet.ResetBoard` | prefix/postfix | Mode B: build the non-local set's deck from the replicated deck list instead of `CPlayerData`/AI recipe; Mode A untouched. |
| `PlayCardSet.ShuffleDeck` | prefix | Mode B mirror: apply the replicated deck order instead of the local Fisher-Yates. |
| `PlayTableGame.OnPressTurnSelectCard` | prefix | Mode B mirror: force the replicated first-turn result instead of the local `Random.Range(0,100)`. |
| `PlayTableGame.SelectionMulliganOrKeep`, `OnPressConfirm*`, `PlayCardSet.OnPressEndTurn`, `ReleaseDraggingCard`, `OnClickElementButtonArea` | prefix (actor, send intent + predict) / apply-from-network (mirror) | The action stream. Actor-side sends one intent before local mutation; mirror-side suppresses direct input routing when it is not the local player's decision and applies the networked action through the same public method. |
| `PlayTableGame.ReportWinner` | postfix | Mode A: emit the result intent. Mode B: host emits the end packet; client suppresses local win-counter mutation (`CPlayerData.m_GameReportDataCollect.duelWinCount`, `AchievementManager.OnDuelWin`) — host applies. |
| `PlayTableGame.EvaluateEndGameGift` | prefix (extend existing) | Mode A client + Mode B client: always suppress the local gift roll/spawn; the host rolls and grants. Host: unchanged. |
| `InteractablePlayTable.StopTableGame` | postfix (extend existing) | Suppress the client-side customer payout (`Customer.PlayTableGameEnded` — economy mutation on a mirrored customer) and the customer win/lose flavor roll; Mode A host applies the payout from the result intent; Mode B has no customer. |
| `CustomerManager` tournament result path | host only | Mode A guest tournament rounds: `StopTableGame`'s `CustomerTournamentData` writes must run on the host from the result intent, not on the client's mirrored customer. |
| `Customer` booking (`IsLookingForPlayer` / `AttemptFindPlayTable`) | host decision layer (Npc module owns decisions) | Exclude tables in open-duel or PvP state from customer booking while the state lasts. |

RNG audit gate before implementation: enumerate every `Random` call reachable from duel rules on
a non-AI `PlayCardSet` and prove each is either overridden (shuffle, coin flip), AI-only, or
cosmetic — the vanilla study's inventory (shuffle `PlayCardSet.cs:1069`, coin
`PlayTableGame.cs:1586`, AI sites, gift, cosmetic SFX) is the starting list. Anything found later
goes on the replicated-randomness list, not a seed.

## 6. Flow walkthroughs

**Guest challenges a customer (Mode A)** — mostly today's behavior plus consistency:
1. Guest RMBs the table (customer seated host-side, mirrored guest-side). Vanilla runs the duel
   locally on the guest; the host's lease is already reserved via `PlayTableMatchIntentMessage`.
2. Duel ends: vanilla `ReportWinner` runs locally (guest sees Win/Lose normally);
   `EvaluateEndGameGift` is suppressed (existing prefix, widened); `StopTableGame`'s payout and
   customer rolls are suppressed; the result intent goes out.
3. Host applies: customer payout + XP on the real customer, tournament round registration,
   report counters, gift roll → host-side grant to the guest through the established
   item-grant path (same family as `ClientCollect`/container grants — verify plumbing, §8).
4. Lease releases; table visual delta as today.

**Two players duel (Mode B)**:
1. Player A RMBs an empty table → open-duel intent → host marks the table; customers stop
   booking it; peers see the waiting state in the baseline/visual deltas.
2. Player B RMBs the same table → the client sends a duel-accept intent; host validates (table
   open, B not in another duel, A still waiting), deals the duel (decks from each player's
   selected `CPlayerData` deck, coin flip), sends `PlayTableDuelStartMessage` to both, and both
   machines enter vanilla's play-table mode via `PlayCardGameManager.SetPlayTable` with their
   local side mapping. A's lease-style reservation converts to the duel lease.
3. Mulligan: both machines open the vanilla mulligan screen; each player's choice is an action.
   Mirror machines apply the remote choice via `SelectionMulliganOrKeep`.
4. Turns proceed as actions; checkpoints ride turn switches; a mismatch repairs from
   `PlayTableDuelStateMessage`.
5. End: host `ReportWinner` (host-side counters/achievement), end packet to both, gift roll once
   by the host with a grant to the winner, both machines run the vanilla win/lose → conversation
   → exit presentation, tables revert.

**Disconnect mid-duel**: the departed player's lease releases (existing path); the host
ends the duel as a concession to the remaining player (Mode B) or, for a vanished guest's Mode A
duel, simply releases the lease — the guest's local duel dies with their machine and nothing was
shared. Host disconnect ends the session by definition.

## 7. Build compatibility

All referenced game types (`PlayTableGame`, `PlayCardSet`, `PlayCardGameManager`, the screens,
`InteractablePlayTable`) exist with identical signatures in both required baselines
(`1.0-25315983`, `1.00`) and in live `1.02` — verified by hash diff of the whole playtable
surface (engine files byte-identical 25315983↔1.02; 1.00 deltas are decompiler artifacts plus the
shared fixes). Direct references are therefore acceptable; anything that fails to resolve on a
baseline at build time gets the `WarehouseBoxSync.Probe` reflection treatment, no exceptions.

## 8. Verification items (prove before/during implementation)

1. **RNG audit** (§5): full `Random` enumeration reachable from non-AI duel rules on both
   baselines; classify each site: replicated / AI-only / cosmetic.
2. **Gift grant plumbing**: confirm the host can grant card-pack items to a remote player's
   holdings the same way container/card grants work, and that a locally spawned (suppressed)
   gift leaves no residue.
3. **`m_CurrentPlayTime` semantics**: confirm when the table's play timer ticks during player
   duels (fee is time-based for Mode A payouts; the result intent carries play time, but the
   source must be the same on both machines).
4. **Customer payout on guest duels today**: confirm the current build's guest duel
   double-applies or mis-applies `Customer.PlayTableGameEnded` on the mirrored customer
   (economy mutation on the client) — expected pre-existing bug this design fixes.
5. **Tournament guest round**: confirm `StopTableGame`'s tournament writes on a guest currently
   mutate the guest's mirrored tournament state (expected pre-existing bug this design fixes).
6. **Side mapping test**: canonical Side A/B → local set mapping for all four host/guest ×
   side-A/side-B combinations, including board rotation.
7. **Checkpoint hash scope**: freeze the exact field list hashed (zone identity lists in order,
   HP, shield, freeze, boost lists, turn flags) so both machines compute identically.

## 9. Phasing

1. **Phase 0 — co-op-safe duels (Mode A)**: result intent, payout/tournament/gift/report
   dedup, verification items 2-5. Shippable alone; makes today's local guest duels correct.
2. **Phase 1 — open-duel seating + PvP core (Mode B)**: open-duel state, duel handshake,
   replicated randomness, action stream, checkpoints/repair, concede handling. Verification
   items 1, 6, 7 gate this phase.
3. **Phase 2 — hardening**: disconnect edge cases, duel across day-end/shop-close (vanilla
   stops table games on day end — decide: end duels as concession), stress the checkpoint repair.
4. **Phase 3 — tournaments + spectating**: PvP bracket rounds (explicit remote-player
   participant records replacing the null-entry convention), and optionally a spectator view
   reusing the Mode B stream.

## 10. Versioning

New messages and changed lease phases are network-message changes → **patch** bump of
`CardShopCoopVersion` (the join handshake compares the exact version and message catalog, so both
players update together regardless). No handshake-semantics change is proposed, so minor is not
warranted.
