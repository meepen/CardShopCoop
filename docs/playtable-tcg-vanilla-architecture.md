# Playable TCG (Tetramon Duel Masters) — vanilla architecture rundown

Research baseline: `decompiled\1.02` (Steam public build 25381942). Cross-checked against
`decompiled\1.0-25315983` and the Unity 6 `decompiled\1.00` baseline: the battle engine is
byte-identical between 25315983 and 1.02 (only controller-overlay polish, Ascension packs in the
gift pool, and a tournament-round validation fix differ), and the 1.00 deltas are decompiler
noise (`StartCoroutine` vs `base.StartCoroutine`) plus the same fixes. **One implementation can
serve all three builds the mod supports.** Line numbers below are from `decompiled\1.02`.

## 1. What shipped

The 1.0 update (2026-09-15) made the shop's card game playable:

- A lone customer waiting at a play table can be challenged (RMB on the table). 3-5 minute
  match while the shop keeps running.
- Deck building (50 cards, ≤4 copies per monster type) at the workbench; rulebook there too.
- The player can sign up to their own hosted tournament and play bracket rounds at numbered
  tables.
- Rewards: physical gift card-pack items after each duel (win-weighted) and duel-win counters /
  achievements. No direct coin/XP/fame for a duel.

## 2. Rules digest (as implemented)

| Rule | Value | Enforced by |
|---|---|---|
| Deck size | 50 | `GameInstance.GetMaxDeckCardCount()` (`GameInstance.cs:208`), re-checked at sit and at `PlayTableGame.SetPlayTable:209` |
| Copy limit | ≤4 per `monsterType` (all printings share the count) | `PlayCardGameManager.CheckValidDeckSameMonsterTypeCardCount:135`, `DeckEditScreen:372` |
| Opening hand | 5, one mulligan (hand → deck bottom, redraw 5, reshuffle) | `m_StartHandCardAmount=5` (`PlayTableGame.cs:132`); `PlayCardSet.ActionResolve_Mulligan:1117` |
| Guardians | 4 face-down; 1 breaks per 100 HP lost (500/(4+1)) | `m_StartGuardianCardAmount=4` (`:134`); `GetTakeGuardianCardHpThreshold:1622`; `PlayCardSet.EvaluatePostDamage:800` |
| Tamer Points | 500 each; lose at 0; double KO = draw | `m_StartingHP=500` (`:136`); win check `DelaySwitchTurn:1105-1116` |
| Elemental areas | 4 (Fire/Earth/Wind/Water index 0-3), stack ≤4 cards (basic + evos), one new basic per area per turn | `PlayCardSet.Awake:261`, `AddToElementArea:2800`, `m_IsNewSummonList` gate in `GetValidElementAreaIndexList:2022` |
| Evolution | next card `PreviousEvolution` must equal top card's `monsterType`; blocked on new summons unless QuickEvolve | `EvaluatePlaceCardOnElementArea:2063`; `m_CanQuickEvolveList` |
| Play effects | trigger immediately on placement unless `m_IsNoPlayEffect` debuff | `AddToElementArea:2830` → `PlayTableGame.TriggerPlayEffect:531` → `OnPlayEffectTriggered:572` (dispatch over 36 `PlayEffectType` + 20 `PlayEffectTypeSecondary`) |
| Combat | end of turn, every occupied area attacks; damage = element power + boosts; shield absorbs first; freeze (8 counters → skip attack) | `DelaySwitchTurn:1000-1081`; `PlayCardSet.TakeDamage:761`; `AddFreezeCounter:868` |
| Element power | `MonsterData.BaseStats.GetElementPower(i)`; non-elemental cards use `Strength/Vitality/Spirit/Magic / 2` | `PlayCardSet.GetAtkPowerOnElement:3229` |
| Deck source | player: `CPlayerData.m_DeckCompactCardDataList[m_CurrentSelectedDeckIndex]`; AI: `m_PlayCardEffect_SO.m_PlayCardDeckDataList[m_DeckIndex]` recipe, cards generated at match start | `PlayCardSet.ResetBoard:415-498` |

There is **no elemental weakness system**, **no separate action-card type** (every card is a
`MonsterData` with `PlayEffectData`), and `EAttackType`/`EDamageType`/`EStatusEffect`/
`EMonsterTraits`/`ETagTeamMembership` are declared but never referenced — dead enums.

## 3. Runtime architecture (who owns what)

```
InteractablePlayTable (per placed table; seats/booking/fee/tournament number/save)
    │  RMB → OnRightMouseButtonUp()            (InteractablePlayTable.cs:147)
    ▼
PlayCardGameManager : CSingleton<>  (thin façade: deck-list/rulebook screens, deck clipboard)
    │  static SetPlayTable(table, isSideA)     (PlayCardGameManager.cs:60)
    ▼
PlayTableGame : MonoBehaviour  (ONE global instance — the match referee)
    │  owns: turn/mulligan/response flags, start constants, win counts, gift logic,
    │        DelayStart / DelaySwitchTurn / DelayExit coroutines, ALL battle screens
    ├── PlayCardSet m_PlayCardSetPlayer   (per-side state machine; m_IsAIControlled=false)
    └── PlayCardSet m_PlayCardSetEnemy    (per-side state machine; m_IsAIControlled=true)
         zones: deck (ordered CardData) / hand / guardian / discard / 4 element areas,
         all holding InteractableCard3d; HP, tamer shield, freeze, boosts, draw queue;
         AI driver: private EvaluateEnemyAI() (PlayCardSet.cs:3428)

PlayCardEffect_ScriptableObject — static effect DB + AI deck recipes (read-only at runtime)
CPlayerData (static) — player decks, tournament data, card identity helpers, win counters
```

- `PlayCardSet.m_IsAIControlled` is a serialized bool with **no code assignment anywhere** —
  the enemy set is AI purely via the prefab. This is the natural PvP seam: flip it false and
  drive the set through the same public API the UI uses.
- The opponent in vanilla is **always an NPC `Customer`**: entry requires the opposite seat
  occupied (`InteractablePlayTable.cs:177-221`). There is no second-human path.
- `PlayCard`/`PlayCardPool` are dead code for this engine.
- **No event bus anywhere in the battle code** — all communication is direct synchronous calls
  plus `Update()` input polling (`PlayCardSet.Update:531`, `PlayCardSetUI.Update/LateUpdate`,
  each screen's `RunUpdate`).
- There is **no formal battle state enum**; phases are private bools + coroutines
  (`m_IsWaitingResponse`, `m_HasFinishMulligan`, `m_IsPlayerTurn`, `m_IsTriggeringPlayEffect`…).

## 4. Match lifecycle

1. **Entry**: RMB on table (`InteractionPlayerController.cs:1114` dispatch) →
   `OnRightMouseButtonUp` validates tournament state, deck completeness, side (dot product of
   player↔table vector against `table.right`), opposite-seat occupancy →
   `PlayCardGameManager.SetPlayTable(table, isSideA)`.
2. **Setup**: `PlayTableGame.SetPlayTable:199` re-validates deck, sets sides
   (`SetIsSideA`), positions the board, enters `EGameState.PlayTableGameState`
   (`InteractionPlayerController.OnEnterPlayTableGameMode:3270`: movement stopped, mouse-look
   off, cursor confined; normal pause suppressed — `PauseGame` becomes the concede screen).
3. **`DelayStart()` coroutine (`PlayTableGame.cs:247-380`)**: enemy deck re-roll (Random) →
   `ResetBoard` both sides (builds decks) → who-goes-first coin flip
   (`OnPressTurnSelectCard:1578`, internally `Random.Range(0,100) < 50`; the card you pick is
   cosmetic) → `ShuffleDeck` both (`PlayCardSet.cs:1049`, Fisher-Yates over global RNG) → draw
   5 → mulligan prompt (AI decides via `GetEvoCardCountOnHand() >= Random.Range(3,5)`) → draw
   4 guardians → first turn draw.
4. **Turn loop**: play cards via drag (`StartDragCardOnHand:1922` → `ReleaseDraggingCard:1951`)
   or click (`SelectCardOnHand:2096` → `OnClickElementButtonArea:2303`); placement validity in
   `EvaluatePlaceCardOnElementArea:2009`; mutation lands in `AddToElementArea:2800` (sets power,
   new-summon flags, triggers effects). Effect queues resolve through
   `OnPlayEffectTriggered` → per-side `ActionResolve_*` coroutines → `OnPlayEffectTriggerEnded:947`.
   Forced decisions surface as screens: `_ActionResolve`, `_SelectElementArea`,
   `_SearchCardSelection` (+ discard-pile variant), `_InspectBoard`.
5. **End turn**: `PlayCardSet.OnPressEndTurn:2393` → `PlayTableGame.SwitchTurn:502` →
   `DelaySwitchTurn:994` (whole board attacks, damage/guardian math, HP lerp) → turn flip →
   active side draws 1 → win check.
6. **End**: `ReportWinner(isPlayerWin, isDraw):1119` (win counters + `AchievementManager.
   OnDuelWin` + `WinLoseScreen`) → Leave/Rematch (`FinishLeaveGame:1150` / `Rematch:1166`) →
   `DelayExit:434` (closes screens, `EvaluateEndGameGift:1177` spawns 1-4 physical pack items
   weighted by shop level, `EndGameConversation`) → `InteractablePlayTable.ExitPlayerCardGame`
   → `StopTableGame:289` (customer payout `Customer.PlayTableGameEnded`, seat cleanup, save).
   Concede = `QuitBattleScreen` → `FinishLeaveGame(false)`.

## 5. Decision choke points (Harmony surfaces)

There is no single "player submitted action" funnel. The narrowest per-decision public methods:

| Decision | Method |
|---|---|
| Who goes first | `PlayTableGame.OnPressTurnSelectCard(int)` :1578 |
| Mulligan keep/mulligan | `PlayTableGame.SelectionMulliganOrKeep(bool, bool)` :382 |
| Play card (drag) | `PlayCardSet.ReleaseDraggingCard(bool)` :1951 / `AddToElementArea(card, area)` :2800 |
| Play card (click) | `PlayCardSet.OnClickElementButtonArea(int)` :2303 |
| End turn | `PlayCardSet.OnPressEndTurn()` :2393 |
| Forced discard/selection | `PlayTableGame.OnPressConfirmActionResolve()` :1363 |
| Forced area target | `PlayTableGame.OnPressConfirmSelectElementArea()` :1417 → `ConfirmSelectElementArea(bool, bool)` :1422 |
| Search selection | `PlayTableGame.OnPressConfirmSearchCardSelection(List<int>, List<int>, bool)` :1476 |
| Concede / leave / rematch | `FinishLeaveGame(bool)` :1150 / `Rematch(bool)` :1166 |
| Winner | `PlayTableGame.ReportWinner(bool, bool)` :1119 |
| Shuffle | `PlayCardSet.ShuffleDeck(bool)` :1049 |
| Draw (intent / result) | `QueueCardDrawFromDeck(...)` :970 / `AddToHoldCard(...)` :2921 |
| Card instantiation (ID injection point) | `PlayCardSet.GetNewCard3d(CardData)` :2972 |

Screen lifecycle hooks: `UIScreenBase.OpenScreen/CloseScreen` (`UIScreenBase.cs:75,92`) catch
every battle popup; `OnOpenScreen`/`OnCloseScreen` are `protected virtual`.

## 6. World integration

- **Tables**: per-seat parallel lists (`m_IsSeatBooked/Occupied/PlayerSeat/QueueOccupied`,
  `m_OccupiedCustomer`, `m_TableGameItemSetDataList`, `m_PlayTableFee`), tournament number, save
  data (`PlayTableSaveData` — position/rot/boxed/seat lists/accessories/fee/tournament; no duel
  state). Registered in `ShelfManager.m_PlayTableList` via `InitPlayTable:1393` /
  `RemovePlayTable:1402`; **`m_Index` is positional and reassigned on every add/remove — not a
  stable ID** (the mod already addresses tables by placement key instead).
- **Customers**: state machine `WantToPlayGame → WalkToPlayTable → (QueueingPlayTable) →
  PlayingAtTable → EndingPlayTableGame` (`ECustomerState`). One game per visit
  (`m_HasPlayedGame`), fee gate, −25 bias toward tables where a player is waiting
  (`Customer.DetermineShopAction:868-1004`), partner pairing through
  `CustomerManager.OnCustomerSitDownAtPlaytable:788` + `Customer.FindPlaytableWithWaitingCustomer:1584`.
  AI arrival lands in `InteractablePlayTable.CustomerHasReached:581`, which starts the
  customer-vs-customer game (`m_HasStartPlay`) when both seats fill.
- **Tournaments**: player sign-up consumes a bracket slot
  (`HostTournamentScreen.OnPressPlayerSignUpTournament:417`); the player is represented as a
  **`null` entry** in `CustomerManager.m_TournamentCustomerList`/`m_TournamentSortedCustomerList`
  (`SpawnGameStartCustomer:338-423`); table number = `sortedIndex / 2 + 1`; the player must sit
  at the table with the matching number (`PlayAtWrongTableNumber` gate). Results register in
  `StopTableGame:348-371`; bracket re-sorts by WinCount → OMW → OOMW each round
  (`CustomerManager.OnCustomerFinishTournamentRound:1228`).
- **Decks/persistence**: decks are `CPlayerData.m_DeckCompactCardDataList` +
  `m_CurrentSelectedDeckIndex`, serialized inside `CGameData` →
  `savedGames_Release{slot}.json` (JsonUtility; `CSaveLoad.cs:49/112`). Cards move directly
  between collection and deck (`CPlayerData.AddCard/ReduceCard`). **No in-progress duel is ever
  saved**; customers seated at tables are skipped in `CustomerManager.SaveCustomerData:1219` and
  recreated from the table's save entry on load (`InteractablePlayTable.LoadData:459-490`).
- **Rewards split**: customer-vs-customer tables pay fee × minutes + rating⁴ XP
  (`Customer.PlayTableGameEnded:3150`); the player's duel pays only gift packs + win counters
  (`ReportWinner` / `EvaluateEndGameGift`). `PriceChangeManager.AddTransaction` is an empty stub.

## 7. RNG inventory (netcode-critical)

Everything uses the **global, unseeded `UnityEngine.Random`**, interleaved with unrelated
systems (customer spawns, VFX) — seed-only lockstep is not viable. Gameplay-affecting sites:

- Deck shuffle: `PlayCardSet.cs:1069`
- AI deck generation: `PlayCardSet.cs:439-492`; enemy deck pick: `PlayTableGame.cs:195,255`
- AI decisions: `PlayCardSet.cs:3428-4228` (many), AI mulligan: `PlayTableGame.cs:343,350`
- Coin flip (first turn): `PlayTableGame.cs:1586`
- Gift rolls: `PlayTableGame.cs:1211-1222`; customer win/lose flavor: `InteractablePlayTable.cs:319`
- Cosmetic-only (safe to diverge): SFX pitch/variant `PlayCardSet.cs:755,3342,3361`

House pattern therefore: **host-rolled results carried in state messages** (same as
WorldMarket/container rolls and trade minting), not shared seeds.

## 8. Single-player assumptions (the landmine list)

1. Opponent must be a seated NPC; no second-human path (`InteractablePlayTable.cs:177-221`).
2. One global `PlayTableGame` instance per machine (`PlayCardGameManager.cs:8`).
3. No stable card instance IDs — zones hold `InteractableCard3d` references; card identity is
   only `CardData` (expansionType, monsterType, borderType, isFoil, isDestiny) inside the visual.
4. No event bus; input polled in `Update`; coroutine/timing-gated transitions
   (`WaitUntil`/`WaitForSeconds`, `m_IsStoppingAction` animation gates).
5. Cosmetic queues with state side effects (`m_CardDrawQueueData` drained in `PlayCardSet.Update`).
6. Rendering is local-camera-only (`Card3dUISpawner.Update` culls to the local camera); the board
   UI has exactly two sides (player/enemy) — no spectator geometry.
7. Hidden information (deck order, hands, guardians) lives on each side's lists.
8. Rewards (`EvaluateEndGameGift`) spawn physical items on whoever runs the leave path.
9. Tournament bracket encodes "the player" as a null list entry.

## 9. Mod status (what Modules/PlayTable does today)

The current module is **table-state + match-lease sync only**:

- Synced: table occupied/boxed, per-seat accessories, `isPlayerSeat`; host-authoritative lease
  (`PlayTableMatchEntry`: OwnerConn/TableKey/Seat/SideA/Phase) validated on sit; kick/box
  intents; release on leave/disconnect; end-of-day report aggregates.
- NOT synced: any battle state, decks, hands, AI opponent progression, duel rewards, win/lose.
  A guest can sit and run a **fully local duel** against the (host-simulated) customer; the
  customer's duel logic runs only on the host and is never streamed.
- Seams already occupied: `OnRightMouseButtonUp` postfix (sit observation),
  `FinishLeaveGame` postfix (release), `EvaluateEndGameGift` prefix (rollback suppression),
  `StopTableGame` postfix, `ShelfManager.InitPlayTable` postfix, `PlayTableGame.Awake` postfix.
- Docs previously recorded "playable TCG untouched" (F3 in `docs/1.00-followups.md`); parts of
  those docs are stale relative to the current module rewrite.

## 10. Design direction for co-op play + PvP

- **Model**: host-authoritative, host-rolled results (house pattern). Each machine keeps running
  the vanilla `PlayTableGame`/`PlayCardSet` locally for presentation; the host owns the duel
  state and streams outcomes (shuffled deck order, draws, placements, damages, winner).
- **Messages** (module conventions): a `PlayTableBattleBaselineMessage` once per joining peer,
  per-decision **intent** messages from the acting client (template:
  `PlayTableMatchIntentMessage`), host **delta** messages with `ActionMessage` ids, and
  per-connection **filtered** hand/deck state (hidden info). Patch-version bump only.
- **Opponent substitution**: for PvP, set the enemy `PlayCardSet.m_IsAIControlled = false` on
  the machine that does not own the seat, prefix-skip `EvaluateEnemyAI`, and drive the set via
  the public API (§5) from mirrored remote intents. For the seated customer in co-op duels, the
  host remains the AI owner and streams its decisions the same way.
- **Seating**: extend the entry gate so a remote player can occupy the opposite seat (Npc module
  already mirrors seat occupancy; suppress customer booking on remote-player seats).
- **Rewards**: host computes the gift once and broadcasts the item list; each machine spawns
  locally (container-record pattern). Suppress the local roll on clients.
- **Card identity**: introduce stable card instance IDs at `PlayCardSet.GetNewCard3d` injection
  and carry `(cardSaveIndex, expansionType, isDestiny)` compact identity on the wire
  (`CPlayerData.GetCardSaveIndex/GetCardData` round-trips).
- **Tournament**: give the remote player an explicit participant record instead of piggy-backing
  on the null-entry convention; both `CustomerTournamentData` shapes already serialize.
