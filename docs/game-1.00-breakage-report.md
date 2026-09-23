# CardShopCoop — Game 1.00 Compatibility / Breakage Report & Fix Plan

Prepared from a decompile-and-diff of the game update. Companion baselines:
`decompiled/0.70.3/Assembly-CSharp` and `decompiled/1.00/Assembly-CSharp`
(full patch: `decompiled/1.00/diff-vs-0.70.3.patch`).

## TL;DR

The 1.0 update did **not** break the mod's binding surface: it still compiles against the
new DLL with 0 errors, every Harmony/reflection target still exists, no patched method
changed signature, and every enum only appends members (numeric IDs stable).

What it breaks is **behaviour and coverage**:

| # | Severity | Area | One-line impact |
|---|---|---|---|
| 1 | **BREAK** | Warehouse stored boxes | 1.0 turns stored boxes into `StoredBoxRecord`s and destroys the live object; the mod has no wire form for records and enumerates only live boxes, so stored warehouse stock is invisible to the peer (and the client mirror is destroyed on apply). |
| 2 | **BREAK** | Ascension card market | The game added an 8th generated-price table (`…Ascension`); the mod syncs only 7. Ascension card values drift (or stay $0 on the guest). |
| 3 | **BREAK** | Tournament player sign-up | New sign-up/sign-out actions are unpatched/unsynced; a guest mutates local tournament state only. |
| 4 | **GAP** | Playable TCG (PvP) | New `InteractablePlayTable` player seats / win-draw / `PlayCardGameManager` state is not synced at all. |
| 5 | RISK | Tournaments / cheats / save fields | `m_IsPlayerRegisteredForTournament`, `m_PlayerTournamentData`, `EntitlementsBonus`, `PlayTableSaveData.isPlayerSeat`, native saves not covered. |
| 6 | GAP | New systems | Cheats, decks, screenshots, skins are untouched by co-op. |

## 1. Scope of the update

| | Value |
|---|---|
| Previous baseline | `decompiled/0.70.3/Assembly-CSharp` (437 `.cs`) |
| New baseline | `decompiled/1.00/Assembly-CSharp` (494 `.cs`) |
| Decompiler | `tools/Decomp` (`ICSharpCode.Decompiler 9.1.0.7988`) — same build for both, so the diff is low-noise |
| Baseline version | `Application.version` = **`1.00`** (Unity `PlayerSettings.bundleVersion`; read offline from `globalgamemanagers`). Previous = `0.70.3` |
| Game release | 1.0 ("Tetramon Duel Masters" playable TCG), Steam buildid `25304508`, 2026-09-14 |
| Engine | **Unity `6000.0.66f2`** — upgraded from `2021.3.38f1` (confirmed via the mod's pre-update logs) |
| Diff size | **239 files changed, +32,502 / −1,697; 59 top-level types added; 2 removed** (`SeekerTest`, `SetRandomCard`) |

Reproduce:

```powershell
git diff --no-index -- decompiled/0.70.3/Assembly-CSharp decompiled/1.00/Assembly-CSharp
```

## 2. Binding surface is intact (verified)

- `dotnet build src\CardShopCoop\CardShopCoop.csproj -c Release` against the **new** game DLL:
  **0 errors** (88 obsolete-API warnings).
- **151/151 Harmony patch targets present**, all literal `Required*/Optional*` reflection
  targets present, all `AccessTools.Field/Method` targets present. Checked against the new
  assembly's metadata with inheritance resolution (0 missing).
- Old↔new parameter-list comparison across every method target (patch + reflection +
  `AccessTools.Method`): **0 signature changes**.
- Every changed enum only appends before `MAX` (`ECardExpansionType.Ascension`,
  `EItemType/ECollectionPackType.AscensionCardPack`, 7 `EObjectType`, `EGameAction.PlayTCG`/
  `OpenHeldPack`, `ECustomerReviewType.TournamentReviewBomb`, +55 `EDecoObject`). Numeric IDs
  are stable, so existing wire IDs keep meaning.

Only compile-level drift to note: 1.0 deprecates `Object.FindObjectOfType`/`FindObjectsOfType`
and `TMP_Text.enableWordWrapping`; these are warnings now, not breaks.

## 3. BREAK — Warehouse stored boxes became data records

### What changed in 1.00

`InteractablePackagingBox_Item` gained a data-only storage path. Storing a box into a
warehouse rack now schedules the live object's destruction and banks a lightweight record:

```
// InteractablePackagingBox_Item.cs (1.00)
46:  private bool m_MarkForDataOnlyDestroy;
...
205:  m_IsStored = true;
209:  m_BoxStoredCompartment = targetItemCompartment;
210:  m_MarkForDataOnlyDestroy = true;
...
321:  if (m_MarkForDataOnlyDestroy)
325:      StoredBoxRecord record = new StoredBoxRecord { itemType = ..., amount = ..., isBigBox = ... };
331:      m_BoxStoredCompartment.AddStoredBoxRecord(record);
341:      OnDestroyed();
```

In 0.70.3, `OnFinishLerp` did **not** call `OnDestroyed`; it only called
`ArrangeBoxItemBasedOnItemCount`. So this destruction-on-store is new.

`ShelfCompartment` now keeps warehouse stock as records and a separate live list:

```
// ShelfCompartment.cs (1.00)
40:  private List<InteractablePackagingBox_Item> m_InteractablePackagingBoxList = ...;
42:  private List<StoredBoxRecord> m_StoredBoxRecordList = ...;
46:  public  List<InteractablePackagingBox_Item> m_LivePackagingBoxList = ...;
167: if (m_StoredBoxRecordList.Count == 0) ...
171: return itemType == m_StoredBoxRecordList[0].itemType;
```

`ShelfManager` save/load excludes stored live boxes and persists/restores records instead
(`ShelfManager.cs:550`, `:581-591`, `:841-843` — stored entries load as `null` spawn slots —
`:896` — records restored directly).

### Why the mod breaks

- The mod patches `InteractablePackagingBox_Item.OnDestroyed` and forwards any local destroy:
  `GamePatches.cs:274-275` → `GamePatches.cs:424-429` → `CoopCore.cs:614-622`.
  The only suppression is `ContainerSync.ConsumeSuppressedStorageDestroy`, armed **only** by
  `InteractableEmptyBoxStorage.StoreBox` (`ContainerSync.cs:1604`), not by warehouse racks.
- The client mirror apply path calls the game's own store recipe with no remote guard:
  `BoxEngine.cs:1425 (ApplyState)` → `ItemBoxFamily.cs:243 (ApplyStored)` →
  `ItemBoxFamily.cs:414 (b.DispenseItem(isPlayer:false, rack))`.
  `ApplyContent` does set `BoxShared.ApplyingRemote` (`ItemBoxFamily.cs:283-323`), but
  `ApplyStored` runs **after** it, unguarded.
- Therefore in 1.0, a guest applying the host's "this box is stored" snapshot runs
  `DispenseItem` → `m_MarkForDataOnlyDestroy` → `OnFinishLerp` → `OnDestroyed()` →
  `LocalBoxDestroyed` → `BoxEngine.ClientNotifyLocalDestroyed` **destroys its own mirror**
  and sends `Possession = Removed` (`BoxEngine.cs:351-369`).
  In the normal ordering this particular `Removed` is **dropped by the host as unknown**: the
  host ran the same store path first, so its own `OnDestroyed` already called
  `HostNotifyLocalDestroyed` → `ForgetHostBox` (`BoxEngine.cs:371-377`, `:223-247`), and
  `HostApplyUpdate` bails out at `BoxEngine.cs:824-830` before its Removed handling. So the
  snapshot-driven path does not delete host data — but the guest mirror is still wrongly
  destroyed. (A guest that stores its *own* held box locally could send `Removed` while the
  host still holds the id; whether that race is reachable needs an in-game test, not an
  assumption.)
- The mod's box engine enumerates only live boxes:
  `ItemBoxFamily.LiveBoxes()` ← `RestockManager.GetItemPackagingBoxList()`
  (`ItemBoxFamily.cs:125-133`). The game removes a stored box from that list in `OnDestroyed`,
  while the record lives in `m_StoredBoxRecordList`; the mod never references
  `StoredBoxRecord`/`AddStoredBoxRecord` and no co-op message carries one.

**Impact (provable):** record-only warehouse stock is invisible to the peer — the other
player never sees boxes that are racked in the warehouse — and a guest applying a "stored"
snapshot destroys its own mirror of the box. Boxes are not provably deleted or duplicated on
the host by the snapshot path. This is still the highest-priority regression and the P0-1
fix below remains required (representation + suppression).

## 4. BREAK — Ascension card market is not synced

The game added an eighth generated-price table:

- `CPlayerData.cs:221` — `m_GenCardMarketPriceListAscension`
- restored with the others in `CGameData.PropagateLoadData` (`CGameData.cs:830`)
- rolled at startup: `RestockManager.cs:187` `GenerateCardMarketPrice(Ascension)`, with
  Ascension-specific base logic at `:257`, `:351`, `:355`
- percent changes / crashes now run for Ascension:
  `PriceChangeManager.cs:101-102`, `:276`

The mod knows only the original seven:

- `Net/Messages/GameStateMessages.cs:62-68` (7 `MarketCardEntry` lists)
- `Sync/MarketSync.cs:273-303` (hash), `:355-361` (fill), `:435-441` (apply)
- `Patches/GamePatches.cs:787-830` — `GenerateCardMarketPriceBlockPrefix` handles
  Tetramon/Destiny/Ghost/Megabot/FantasyRPG/CatJob and `default: return false`, so a guest's
  Ascension repair roll is blocked. (`FoodieGO` was already in this `default` before 1.0.)

**Impact:** Ascension card values drift between host and guest (price tags, price graph,
trades, binder totals); on a guest whose join-transfer gate fails they stay `$0.00`. The
market checksum also ignores Ascension so it never self-corrects.

## 5. BREAK — Tournament player sign-up is local-only on a guest

New in 1.00 `HostTournamentScreen`: `m_PlayerSignUpButton`/`m_PlayerSignOutButton` (`:41`,`:43`)
and `OnPressPlayerSignUpTournament()` (`:417`) / `OnPressPlayerSignOutTournament()` (`:441`),
which mutate `CPlayerData.m_IsPlayerRegisteredForTournament` and
`m_TournamentData.m_TournamentSignedUpCustomerCount`.

`TournamentSync.ApplyPatches` blocks only `OnPressConfirm`, `OnPressCancel`,
`ConfirmCancelTournament`, `OnPressPrizeSetup` (`TournamentSync.cs:90-97`), and the state
message does not carry `m_IsPlayerRegisteredForTournament`. A guest's press is local-only and
fights the next host broadcast.

## 6. GAP — Playable TCG (player vs player) is unsynchronized

1.0 adds `InteractablePlayTable` player state (`m_IsPlayerSeat`, `m_IsPlayerWin`, `m_IsDraw`;
`InteractablePlayTable.cs:30-38`), `PlayCardGameManager`/`PlayTableGame`/`PlayCardSet` runtime
state, and changed `ExitPlayerCardGame()` → `ExitPlayerCardGame(bool,bool)`.

`PlayTableSync` only paints the host's **cosmetic customer** seats
(`PlayTableSync.cs:11-14`, `:246-267`, `:336-340`). Its `StartMoveObjectPrefix`
(`PlayTableSync.cs:271-290`) governs furniture **move-mode** — it lets a client proceed when
`GetHasStartPlayerPlayCard()` is true and otherwise sends a kick intent; it is **not** the
duel entry point. The actual player-duel start is
`InteractablePlayTable.OnRightMouseButtonUp` → `PlayCardGameManager.SetPlayTable`
(`InteractablePlayTable.cs:195-219`), which the mod does not patch. No match state, seats,
turns, or results are streamed, and the mod never references `PlayCardGameManager`,
`PlayTableGame`, `PlayCardSet`, or `PlayCardDeckData`.

## 6.5 Live-test findings (2026-09-14) — join loads to "Now Loading… 0%"

Two additional hard breaks found when a real 1.00 host/guest session was attempted. The
handshake succeeded and the world was transferred; the join then stalled.

### 6.5.1 BREAK — start scene renamed `Start` → `StartOptimized`

- New `CGameManager.cs:12` — `public const string k_StartSceneName = "StartOptimized";`
- Every vanilla load site uses it: `TitleScreen.cs:53/66/93`,
  `SaveLoadGameSlotSelectScreen.cs:115/186`.
- The mod hardcodes the old name in `SaveTransfer.ForceLoadSlot`
  (`SaveTransfer.cs:672` — `gm.LoadMainLevelAsync("Start", slot)`), so the joiner's scene
  load fails. `Player.log`:
  ```
  Scene 'Start' couldn't be loaded because it has not been added to the active build profile...
  NullReferenceException: ... at CGameManager+<LoadLobbySceneAsync>d__107.MoveNext()
  ```
  `LoadSceneAsync("Start")` returns null; the loading screen never advances.

**Fix:** resolve the scene name at runtime from `CGameManager.k_StartSceneName`
(reflection, fall back to `"Start"` for 0.70.3) instead of hardcoding it.

### 6.5.2 BREAK — out-of-band snapshot slot 6 now throws

- 1.00 added `CSaveLoad.Save` → `SaveLoadGameSlotSelectScreen.InvalidateCache()` and
  `UpdateSlot(slot)` (`CSaveLoad.cs:62`, `:70`), and
  `CGameManager.SaveGameData` → `UpdateSlot(saveSlotIndex)` in its non-native branch.
- `UpdateSlot` indexes the save-slot UI lists: `m_SaveLoadSlotPanelUIList[slot]` and
  `m_SavedDataList[slot]` (`SaveLoadGameSlotSelectScreen.cs:301`, `:305`). 0.70.3 had no
  such call.
- The mod deliberately snapshots to out-of-band `HostSnapshotSlot = 6`
  (`SaveTransfer.cs:56`), which is past the UI list. Host log:
  `coop: SaveGameData(6) threw: Index was out of range...` → falls back to
  `shipping world from slot file` (potentially stale).

**Fix:** make `SaveLoadGameSlotSelectScreen.UpdateSlot` a no-op for out-of-range slots
(prefix patch), and/or call `CGameData.instance.SaveGameData(slot)` directly instead of
`CGameManager.SaveGameData(slot)`; keep the completion-counter sampling.

## 7. RISK / coverage notes

### 7.1 Save fields added (whole-object transfer may cover some)

`CGameData` added `m_PlayerTournamentData`, `m_IsPlayerRegisteredForTournament`
(`CGameData.cs:89-91`), `m_CardCollectedListAscension`, `m_IsCardCollectedListAscension`,
`m_CardPriceSetListAscension`, `m_GradedCardPriceSetListAscension`,
`m_GenCardMarketPriceListAscension` (`:147-213`), and `m_EntitlementsBonus` (`:337`).

`SaveTransfer` serializes the live save with Unity `JsonUtility` (`SaveTransfer.cs:125`),
the same serializer the game uses for its `.json` slot, so these *should* ride along at join
time — but they are not live-synced afterwards, and the mod's save-identity probing may need
review. Verify in-game.

### 7.2 `PlayTableSaveData.isPlayerSeat`

New field (`PlayTableSaveData.cs:25`). `PlayTableSync.SeatState` only carries
`Active/PlayMat/DeckBox/Comic` (`PlayTableSync.cs:42-50`), so player-seat ownership can be
lost after save transfer/reconstruction.

### 7.3 Native saves

`CSaveLoad` now routes through `PlatformManager.Instance.UseNativeSaves()`
(`CSaveLoad.cs:32/63/140/223/237/358`; `SaveNative<T>` helper at `:437`) with
gzip+`JsonSerialization` for native platforms. The mod serializes the in-memory object, so
this is likely fine, but the Game Pass/native path should be smoke-tested with a join-time
world transfer.

### 7.4 New systems not covered (feature gaps, not regressions)

`CheatManager` (queues `CEventPlayer_AddCoin`/`AddShopExp`), decks (`PlayCardDeckData`),
`screenshots` (already excluded from sidecar transfer, `SidecarTransfer.cs:92`), and skins
(`SkinParts`). The mod references none of them. Cheats are the only one with economy impact.

**Graded-card opening sequence:** 1.0 rewrote `InteractablePackagingBox_Card.OnPressOpenBox`
to call the new `CSingleton<GradedCardOpeningSequence>.Instance.OpenScreen(m_StoredCardDataList)`
(`InteractablePackagingBox_Card.cs:87`); 0.70.3 called `EnterHoldCardMode()`. `CardBoxOps`
blocks `OnPressOpenBox` on clients (`CardBoxOps.cs:79-87`), so a guest silently misses the
entire new reveal UI (cards are still granted via `ClientCollect`, so nothing is lost — only
the presentation). Worth covering when the graded card flow is next touched.

### 7.5 Engine upgraded Unity 2021.3 → Unity 6

Old sessions logged `Unity 2021.3.38f1`; the 1.0 build is **`6000.0.66f2`**. The mod still
compiles (0 errors) and the whole binding surface resolves, but Unity 6:
- deprecates `Object.FindObjectOfType`/`FindObjectsOfType` and `TMP_Text.enableWordWrapping`
  (88 warnings, used heavily by the mod);
- may change runtime behavior of engine APIs the mod relies on (physics/`FindObjectOfType`
  ordering, `Rigidbody` semantics). The handshake also compares `Application.unityVersion`
  (`CoopCore.cs:4765`), which is fine for same-build peers but worth noting for the
  cross-build `AllowCrossBuildJoin` path.
Treat this as a RISK: do a broad in-game smoke test (join, boxes, shelves, NPCs, register,
save transfer), not just the itemized fixes.

## 8. Fix plan

Ordered by severity; all changes are in `src/CardShopCoop`.

### P0-1 — Handle the new warehouse `StoredBoxRecord` model

1. Patch `InteractablePackagingBox_Item.OnFinishLerp` (or read the new private
   `m_MarkForDataOnlyDestroy` once via reflection) so the `OnDestroyed()` it now issues is
   **suppressed** in `BoxDestroyedPrefix` the same way `ConsumeSuppressedStorageDestroy`
   suppresses the empty-box-station path. Simplest robust shape: a new
   `ContainerSync.SuppressDataOnlyStorageDestroy(box)` armed in an `OnFinishLerp` prefix
   immediately before the game calls `OnDestroyed`.
2. Guard the client mirror store path: set `BoxShared.ApplyingRemote = true` around
   `ItemBoxFamily.ApplyStored`'s `DispenseItem` (`ItemBoxFamily.cs:404-416`) so a host-driven
   "stored" apply can never look like local gameplay.
3. Add a wire representation for stored warehouse records — either a new
   `StoredBoxRecord`-shaped entry in the container/world message or a `BoxWire` "record" form —
   so `m_StoredBoxRecordList` syncs, and include record-only stock in `LiveBoxes()`/snapshot
   enumeration (today it reads only `RestockManager.GetItemPackagingBoxList()`).
4. Re-check the existing `ItemBoxFamily` store/move/Unhook paths
   against the new record lifecycle.

### P0-2 — Sync the 8th (Ascension) market table

Add `GenCardMarketPriceListAscension` to:
`Net/Messages/GameStateMessages.cs`, `Sync/MarketSync.cs` (hash, fill, apply, checksum),
and add an `Ascension` case to `GamePatches.GenerateCardMarketPriceBlockPrefix`.
This adds a message field → **wire change** (see §9).

### P0-3 — Tournament sign-up

Decide host-authoritative policy: either patch `OnPressPlayerSignUpTournament`/
`OnPressPlayerSignOutTournament` with `ScheduleBlockPrefix` on clients (consistent with the
other tournament buttons), or add `m_IsPlayerRegisteredForTournament` + count to the
tournament state message. The former is smaller and matches the current design.

### P0-4 — Playable TCG

Minimum viable fix: block a non-host from starting an authoritative player duel locally
(adjust `PlayTableSync.StartMoveObjectPrefix`) so peers cannot diverge. Full player-vs-player
sync (seats, hands, turns, results) is a large feature and should be scoped separately.

### P1 — Save/state fields

- Add `isPlayerSeat` to `PlayTableSync.SeatState`/wire.
- Verify `CGameData` Ascension collection/price-set and tournament fields survive the join
  transfer and are not silently dropped by the mod's save-identity probing.
- Smoke-test the native-save path.

### P2 — Coverage

- Sync or neutralize `CheatManager` economy effects in co-op.
- Decide whether deck data needs syncing (it is catalog/deck config today).

## 9. Versioning

P0-2 (new message field) and any new stored-box message are **wire-contract changes**:
bump `CardShopCoopVersion` **minor** in `Directory.Build.props` (per `AGENTS.md`) and end the
CHANGELOG release section with **Both players must update.** Bug-only fixes in this plan
(P0-1, P0-3, P0-4) are patch-level on their own, but shipping them alongside the wire change
makes the whole release minor.

## 10. Verification checklist

- [ ] `dotnet build src\CardShopCoop\CardShopCoop.csproj -c Release` (0 errors).
- [ ] `dotnet format ... whitespace --verify-no-changes`.
- [ ] Two clients: store a box on a warehouse rack (host and guest), confirm neither peer
      deletes/hides it and both show warehouse contents.
- [ ] Ascension prices match across peers; guest not stuck at $0.
- [ ] Tournament sign-up does not desync.
- [ ] Join-time world transfer on a 1.0 save (incl. native-save platform).
