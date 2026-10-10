# Furniture Overhaul × CardShopCoop — compatibility audit

**Audited:** 2026-10-10 · **Furniture Overhaul v1.6.1** (`com.qolmods.furnitureoverhaul`,
by Evers665 / QOL Mods), installed at
`BepInEx\plugins\FurnitureOverhaul\FurnitureOverhaul.dll`
(SHA-256 `F5644443483507726B4B7224C5218C8A993FA935932FD3DDFA8A8A3E4CD883DC`).
Repo state: `5c58f12`. Decompiled copy: `decompiled/FurnitureOverhaul` (git-ignored).

**Method.** Static analysis only: decompiled both sides, compared every Harmony patch target, and
traced the purchase, placement/boxing, save-transfer and co-op-bridge flows. The game had **not**
been launched with Furniture Overhaul at audit time (`com.qolmods.furnitureoverhaul.cfg` does not
exist yet), so nothing is runtime-verified unless marked otherwise. Findings marked
**needs runtime test** are code-confirmed but not exercised in game.

Furniture Overhaul adds ~44 "Diamond Collection" pieces using raw `(EObjectType)9205…9285`
constants (never real enum members), replaces the phone furniture shop page with its own UI
(default on), and integrates co-op only with **QOL Co-op** (`com.qolmods.coop`), a different mod.

---

## Verdict

1. **Release-blocking for co-op:** with FO installed, *every* furniture purchase a **client**
   makes through the phone shop pays shared money but the host never creates the item
   (section 1.1). Default configuration, vanilla furniture included.
2. **Data corruption:** CardShopCoop and FO both store state in
   `InteractableBulkDonationBox.m_CompactCardDataAmountList` with different encodings; sync
   scrambles bargain-bin contents/prices between peers (section 1.4).
3. **Unsynced FO state** (finishes, rugs, display-only, bargain prices, claw machine, scanner —
   some gameplay-affecting) because FO's state channels target QOL Co-op only (section 1.3).
4. **Everything else is structurally compatible:** placement/move/box/warehouse/customer/save/load
   paths have no breaking patch conflicts. FO's skipping prefixes are strictly gated to FO types
   or the cart; its custom object types travel through CardShopCoop's wire/save machinery as raw
   ints (identity) and are first-class placement entities (sections 2–5).

**Status (2026-10-10):** items 1 and 2 are fixed in this change set (section 6); items 3 and 4
remain as documented limitations.

---

## 1. Confirmed breakages

### 1.1 A client buying furniture through Furniture Overhaul's shop page pays shared money and never receives the piece

**Blast radius:** the replacement page is the default configuration
(`FurnitureShopHook.cs` — `NewFurnitureShop` binds `true`), and it sells vanilla furniture too
(ALL / VANILLA ITEMS tabs). So *every* client furniture purchase made through the phone goes
through the uncaptured path, not only Diamond Collection pieces.

CardShopCoop captures furniture purchases on exactly one method:
`FurnitureShopConfirmPurchaseScreen.OnPressConfirmCheckout`
(`src/CardShopCoop/Modules/Purchasing/PurchasingClientBehaviour.cs:555` — prefix captures the
request and enters the charge scope; postfix observes; finalizer releases).
Furniture Overhaul never calls it. Its BUY button runs:

- `FurnitureShopScreen.OnBuy` → `Purchase.Buy(selected, qty)` —
  `decompiled/FurnitureOverhaul/FurnitureOverhaul/FurnitureShopScreen.cs:2790`
- `Purchase.Buy` → `val.EvaluateCartCheckout(it.Price, index)` per unit —
  `Purchase.cs:132`, where `val` is the native `FurnitureShopUIScreen`
  (`Catalogue.PhoneShopScreen()`); the native confirm screen is never opened
  (`Shop.cs:388` only *reads* `m_ConfirmPurchaseScreen.IsScreenOpened()`).
- `Patch_PhoneBuyFurniture.cs:9-13` also returns `false` from a prefix on
  `UI_PhoneScreen.OnPressBuyFurnitureBtn`, skipping the vanilla open of the furniture panel, so
  the confirm screen has no reachable entry point at all.

What happens on a **client** (guest):

1. `EvaluateCartCheckout` runs the native spend+spawn locally: `CEventManager.QueueEvent(new
   CEventPlayer_ReduceCoin(cost))` and `SpawnInteractableObjectInPackageBox`.
2. Because no charge scope is active, the HUD module treats the coin reduction as a guest
   contribution and forwards it: `HudClientBehaviour.EconomyQueuePatch`
   (`HudClientBehaviour.cs:460`) → `ForwardObserved` → `HudContributionIntent` to the host
   (`HudClientBehaviour.cs:137-154`). The host accepts and **the shared wallet is debited**.
3. No `PurchaseIntentMessage` is sent (only `PurchasingClientBehaviour.CaptureFurniture` sends
   it, and it never ran), so the host never executes
   `ShelfManager.SpawnInteractableObjectInPackageBox` for this type, never assigns a host box
   id, and never broadcasts a descriptor.
4. The client keeps a **client-only package box** minted with a fresh client Guid via the
   `RestockManager.SpawnPackageBoxShelf` rejection path
   (`WorldClientBehaviour.BoxNetworkInteraction.cs:174-182`, id minted at `:722-745`).

Net effect: the player paid real, shared money for furniture that exists on their machine only;
no other peer sees the box or piece; picking up/placing that box will hand the host an unknown
box id (rejection expected — **needs runtime test**).

On the **host** the same purchase works correctly: host-side hooks live on the factory
(`WorldHostBehaviour.BoxNetworkInteraction.cs:388`, `ShelfManager.SpawnInteractableObjectInPackageBox`)
and the report module observes `FurnitureShopUIScreen.EvaluateCartCheckout`
(`ReportHostBehaviour.cs:576`), so the host's own FO purchases replicate normally.

### 1.2 Quantity mismatch built into the FO flow

FO buys up to 10 pieces per click via a loop over `EvaluateCartCheckout` (`Purchase.cs:10,52,94`),
while CardShopCoop's furniture intent contract is one box per intent
(`PurchasingClientBehaviour.cs:318`; host requires `Count == 1`,
`PurchasingHostBehaviour.cs:1105`). When 1.1 is fixed, capture must be per `EvaluateCartCheckout`
call (one intent per unit), not per click. Currently moot because no intent is sent at all.

### 1.3 Furniture Overhaul's co-op sync is inert under CardShopCoop (state stays local)

`CoopBridge.cs:30-34` links only to **QOL Co-op** (`com.qolmods.coop`, assembly `QOLCoop`,
static `QOLCoop.CoopApi`). With no `QOLCoop` assembly the bridge no-ops cleanly (every guard is
null-safe; verified) and registers channels `fo.paint`, `fo.rug`, `fo.mode`, `fo.bargain`,
`fo.claw`, `fo.claw.play`, `fo.paint.live`, `fo.order` against that other API. It never looks
for `dev.meepen.cardshopcoop`.

Consequence in a CardShopCoop session (both peers have FO): no FO state channel is shared.

| FO feature | Channel(s) | Effect in a CardShopCoop session |
|---|---|---|
| Finishes/refinish (P) | `fo.paint`, `fo.paint.live` | visual only — colors differ per machine |
| Purchase "orders" (finish chosen at buy) | `fo.order` | visual — a bought piece gets its finish only on the buyer's machine |
| Rug (Custom) resize/paid area | `fo.rug` | visual size differs; currency already applied locally |
| Display-only switch | `fo.mode` | **gameplay-adjacent** — the other machine keeps the old sell/display mode (CardShopCoop has no sync for this state) |
| Bargain Bin/Table prices | `fo.bargain` | **gameplay** — prices diverge, simulated customers pay different prices per machine |
| Claw machine | `fo.claw`, `fo.claw.play` | **gameplay** — plays/wins counters and prize inventory evolve independently |
| Card scanner/loupe/crack bench | `GradingCoop` channels (api v2) | **gameplay** — no one-player-per-scanner gate; trays/output/vise diverge |
| Checkout display contents | companion file (not a channel) | other machine's built-in showcase loads empty |

FO's per-slot companion files (`FurnitureOverhaulPaint`, `FurnitureOverhaulClaw`,
`FurnitureOverhaulCheckout`) live in `Application.persistentDataPath` and are **not** part of the
transferred save, so on a CardShopCoop client every FO `PrepareLoad`/`Restore` loads nothing and
FO state defaults. FO gates its *own* customer AI on `CoopBridge.IsClient`
(`BargainCustomers.cs:212`, `ClawCustomers.cs:104`); with QOL Co-op absent this is always
`false`, so FO does not self-suppress on a co-op client — CardShopCoop's Npc module happens to
cover it by suppressing client-side customer simulation (`NpcClientBehaviour.cs:463`).

### 1.4 Bargain Bin = BulkDonationBox: one field, two encodings, corrupted in sync

CardShopCoop treats every `InteractableBulkDonationBox` as a donation container and replicates
its raw `m_CompactCardDataAmountList` (`WorldContainerInteraction.cs:518-522` host record;
client send at `:2162-2187`; remote apply clears and replaces the list at `:1273-1274,1387-1401`).
Furniture Overhaul's Bargain Bin stores its multi-compartment state **in the same field**,
encoding closed compartments as negative-`gradedCardIndex` sentinel rows
(`BargainStore.cs:388-401`, sentinel at `:380`; prices live in a side structure). Neither side
knows about the other:

- A peer editing a bargain bin sends/overwrites the raw list; FO's `BargainStore` re-derives its
  bin from whatever arrived, so compartment stock and the other compartments' prices are lost or
  scrambled on the far side.
- CardShopCoop does not carry FO's closed-compartment rows/prices, so the machines end with
  different bin contents and prices.
- Two postfixes also compete on `InteractableBulkDonationBox.RemoveRandomCardFromShelf`
  (CardShopCoop `DonationRandomCardPostfix`, FO `Patch_BargainDonationPick`).
- If a peer does not run FO, CardShopCoop forwards the sentinel rows as real donation contents
  (negative graded indexes land as damaged entries).

**Needs runtime test** with both players editing the same bargain bin, but the field collision is
confirmed in code.

---

## 2. Mechanical measure: shared Harmony targets

CardShopCoop patches 190 distinct type+method targets; Furniture Overhaul patches 92.
**18 are the same method.** No Harmony id, unpatch, or dependency collisions exist (all ids are
namespaced; FO's `UnpatchSelf` uses its own id). A prefix returning `false` skips the original
and later prefixes but **not** postfixes (Harmony docs), which shapes each verdict below.

| Method | FO patches | CardShopCoop patches | Verdict |
|---|---|---|---|
| `InteractableObject.StartMoveObject` | ClawGame (prefix returns false, 9231 only), VendingMachine, RetailFixtures, ScannerMachine | WorldHost.PlacementHold, WorldClient.Placement | benign — FO never skips non-claw objects; captures are capture-only |
| `InteractableObject.PlaceMovedObject` | Patch_CartPark (prefix returns false while pushing a cart) | World* Box/Placement, Decoration* | benign — CS postfixes early-return while `GetIsMovingObject()`; no phantom publishes |
| `InteractableObject.BoxUpObject` | Patch_CartLoadedBoxing (prefix returns false, cart only — verified `CartDrive.cs` guard) | World* Box/PlacementHold, Decoration* | benign; cart-object skip cannot affect other types |
| `Shelf.BoxUpObject` (override) | ClawGame (prefix returns false while a claw game is live) | (CS patches the base method) | benign — all game overrides chain to `base` |
| `InteractableObject.AddObjectRotation` | Patch_SpeakerRoll (prefix returns false, FO speaker types only — verified) | World* PlacementHold | benign — postfix runs on a no-op |
| `InteractableObject.OnRaycasted` | CardTower hint, OneSidedReach, Refinish, RugResize | RegisterHost/Client | benign — capture-only observation |
| `InteractablePackagingBox_Item.OnHoldStateLeftMousePress` | ClawGame (prefix can skip, claw aim only) | World* PlayerShelfInteraction | benign — scope finalizer balanced on `__state` |
| `InteractablePackagingBox_Item.DispenseItem` | CartCompatibility (prefix can skip for cart bay) | World* Box | ordering risk only: a skip before CS's capture prefix yields a redundant box-contents message (harmless, noisy) — **needs runtime test** |
| `ShelfCompartment.AddItem` / `RemoveItem` | RetailStock / VendingMachine (void prefixes) | World* PlayerShelfInteraction | benign; claw-bay re-entrancy risk below |
| `ShelfCompartment.SetCompartmentItemType` | Patch_ClawMachineOneProductBay (postfix) | World* PlayerShelfInteraction | risk: FO's postfix can mutate claw-machine bays and re-enter CS shelf capture — **needs runtime test** |
| `Customer.Update` / `DetermineShopAction` / `DeactivateCustomer` / `OnReachedPathEnd` | ClawCustomers, VendingQueue, Patch_Bargain* | Npc*, Trade* | benign — CS suppresses client `CustomerManager.Update`; FO customer logic runs host-side (needs build-shape test) |
| `CEventManager.QueueEvent` | LoadTrace, Patch_BargainPayCapture | EconomyHost (`CEvent` overload), HudHost/HudClient | benign — different overloads / observers |
| `CSaveLoad.Load` | ClawSave, PaintSave, Checkout, DisplayOnly, Retirement | CatalogPatches (client-only SaveEnumRemap) | benign in matched mod sets; ordering note in section 4 |
| `InteractableCashierCounter.OnDestroyed` | Patch_CheckoutDestroyChild | RegisterHost | benign — observers |
| `NotEnoughResourceTextPopup.ShowText` | Patch_CheckoutRefusalReport | BillsHost | benign |

FO skip-prefixes that CardShopCoop must survive were individually reviewed; all are gated to FO
content (cart 9225, claw 9231, speakers, FO types). No FO patch skips a base-game path.

---

## 3. Object types, wire and save identity

- CardShopCoop reads object types as `int` and never serializes them as enums in placement.
  `PlacementWireConverters` maps through `CatalogIdMap`, whose miss path is identity
  (`CatalogIdMap.cs:147-173`). FO's `9205+` values are never defined enum members, so they cross
  the wire and the transferred save unchanged.
- The only `Enum.IsDefined` gate in the sync paths is `CardExpansion` settings — object types
  are not validated against membership anywhere.
- Placement is membership-based (`PlacementInterop.FindKind` scans native `ShelfManager` lists
  kinds 0–15), and FO pieces register in those lists through the game's own `Awake`/`Init`
  paths, so FO pieces are first-class placement entities: created, moved, boxed, recreated on
  the client via `ShelfManager.SpawnInteractableObject` + `Init()`.
- FO welds (decorative child objects, `Weld.cs`) rebuild from those same `Init`
  hooks, so parent-only sync suffices in the normal path.
- FO's `Patch_Spawn` / `Patch_FurnitureLookup` never alter base-game spawn/lookup behavior
  (both early-return for non-FO types — verified).
- `ShelfManager.SpawnInteractableObject` (FO) and `SpawnInteractableObjectInPackageBox`
  (CardShopCoop) nest cooperatively: FO builds the piece, CardShopCoop binds the box.
- **Mixed installs remain a real hazard:** a client without FO that joins a host with FO
  receives a save and placement baseline containing FO object types. CardShopCoop's join
  handshake compares only its own message/enum catalogs, so it admits the peer; the receiving
  game cannot build those pieces (FO's own changelog documents that the game's fixed
  object-type-indexed list crashed on such values until FO patched it). **Needs runtime test**;
  the player-facing checklist already requires identical mods on both PCs.

---

## 4. Save/load interactions (client borrowed-world load)

CardShopCoop's client join loads the host's save through the game's own `CSaveLoad.Load(-100)`
(`SaveTransferStorage.cs:410-428`). All FO `CSaveLoad.*` / `ShelfManager.Save/LoadInteractableObjectData`
hooks therefore fire on the connecting client:

- `Retirement.Strip` (postfix, `HarmonyPriority(0)`) mutates the loaded save lists and removes
  retired FO furniture/contents (`FO/Retirement.cs:10-34`).
- `PaintSave.PrepareLoad`/`Restore`, `CheckoutDisplay.PrepareLoad`, `ClawSave.Load`,
  `BargainStore.PatchSave`/`ScannerStore.PatchSave` run and find no companion file for slot
  `-100` on the client (companions are not transferred), so client FO state defaults.
- `BargainStore`/`ScannerStore` read and rewrite the same `m_BulkDonationSaveDataList[*]`
  compact-card lists CardShopCoop's container code touches (section 1.4).
- `SaveGuard` (prefix on save) and `SaveRepair` (prefix `HarmonyPriority(800)` on load)
  remove/re-register list entries; they also fire when the host runs CardShopCoop-driven saves
  after purchases (`PurchasingHostBehaviour.cs:540,1068`) and expansions.

**Ordering:** CardShopCoop's only `CSaveLoad.Load` patch is the client-only `SaveEnumRemap`
postfix (`CatalogPatches.cs:44`); catalog/pricing rebuild runs on
`CGameData.PropagateLoadData` / `CPlayerData.ResetData`, which FO does not patch. No
"rebuild before FO registers" hazard. The one shared mutation surface is `EObjectType` ids
inside the loaded save (FO `Retirement` fixed-id checks vs. CardShopCoop's name-map remap,
identity for FO values). **Needs runtime test** for exotic enum-numbering cases only.

**Customer count:** FO's `CustomerCountFix` prefix **skips**
`CustomerManager.EvaluateMaxCustomerCount` and reimplements it. CardShopCoop never calls that
method — it suppresses client `CustomerManager.Update` and mirrors the host's population — so no
sync conflict; FO's version shapes the host's customer volume, which CardShopCoop mirrors. OK.

---

## 5. Placement / move / box: runtime risks (code-confirmed, unexercised)

1. **Integrated checkout display (9207).** FO keeps it as a child of the checkout counter
   (`Patch_CheckoutChildValidity`), but it is registered as a `CardShelf` in
   `m_CardShelfList`, so CardShopCoop's baseline enumerates it as a standalone placement entity.
   Test: host places a Diamond Checkout, client joins, host moves/boxes the checkout — watch for
   an orphaned or duplicated display.
2. **Spurious box-contents message on cart-guarded `DispenseItem`** when FO's prefix skips before
   CardShopCoop's capture prefix (ordering-dependent; harmless but noisy).
3. **Claw/vending bay re-entrancy into shelf sync:** FO's `SetCompartmentItemType` postfix
   (`ClawMachineBuild.ForceOneProduct`) mutates machine bays that CardShopCoop's shelf capture may
   mirror as label/item deltas. Test filling/moving machines on both peers.
4. **Boxing a claw machine mid-game:** FO refuses (`BoxGuard`) and CardShopCoop's host rejection
   path rolls the client back — correct, but the client may briefly show a predicted box.
5. **Move-preview coexistence:** FO repaints `ShelfManager` move-preview objects while
   CardShopCoop drives remote holds through the same preview. Test two simultaneous holds.
6. **Enum-identity parity** holds only while FO keeps raw int casts. If a future FO mints named
   members asymmetrically, the join handshake could reject a peer.

---

## 6. Fixes implemented (2026-10-10)

1. **P0 — the client furniture purchase capture now lives on the checkout engine.**
   `PurchasingClientBehaviour` patches `FurnitureShopUIScreen.EvaluateCartCheckout` instead of
   the confirmation screen's `OnPressConfirmCheckout`. Vanilla reaches the engine only from the
   confirmation screen (still open at that moment; it closes itself afterwards), and the capture
   records the open confirmation screen for the accepted-close / rejected-reopen bookkeeping; a
   replacement shop UI (Furniture Overhaul's page) calls the engine directly and gets the same
   forwarding with no screen attached. One capture point now covers every shop UI, vanilla and
   modded, and per-unit quantity loops map to one intent per call. Host side required no change
   (its factory hooks and `GetFurniturePurchaseData`/`GetSpawnInteractableObjectPrefab` lookups
   already resolve FO types).
2. **P1 — modded bulk donation boxes are excluded from the raw compact-list sync.**
   `WorldContainerInteraction` now treats only bulk donation boxes whose object type is a
   defined `EObjectType` member as syncable (`CatalogIdMap.IsDefined`; the `None` sentinel is
   refused explicitly). Furniture Overhaul's Bargain Bin (9274/9275) and Card Scanner (9283) are
   raw out-of-enum casts, so no key is enumerated for them, no record is built, no intent is
   accepted, sent, or applied, and their encoded compartment state is never overwritten. Vanilla
   donation boxes keep their exact raw index, wire ids, and apply semantics.
3. **Still open by design:** FO's own state channels target QOL Co-op only (finishes, rugs,
   display-only, bargain prices, claw machine, scanner stay local in a CardShopCoop session —
   see 1.3); mixed FO/no-FO installs remain unsupported (see 3). Both need matching mod sets
   and, for full FO sync, an integration agreement with that mod's author.

---

## 7. Runtime test checklist (when the game is next launched with FO)

- [x] Client buys vanilla furniture through FO's page — money debited once and both peers get
      the box (verifies fix 1 for the vanilla-item-through-modded-UI path).
- [x] Client buys Diamond furniture (including qty > 1) — piece delivered, finish stays local
      (see 1.3), money correct.
- [x] Vanilla-only session (no FO) — furniture purchase through the game's own confirm screen
      behaves exactly as before, including reject/reopen (verifies no regression).
- [x] Both peers edit the same Bargain Bin — each peer keeps its own coherent state, nothing
      scrambles (verifies fix 2; the bins are not shared without QOL Co-op by design).
- [x] Vanilla donation box still syncs across peers after the gate.
- [x] Host boxes/moves Diamond Checkout while a client watches (risk 1 in section 5).
- [ ] Client boxes a Claw Machine mid-game (risk 4 in section 5).
- [x] Client without FO joins a host with FO (section 3 mixed-install hazard — expect a load
      failure or missing furniture).
- [x] Check logs for FO warnings around `co-op link`, `spawn:`, `placement did not resolve`,
      and CardShopCoop rejection/rollback lines.
- [x] After deleting one placed register, the other player can still man and use the remaining
      registers (verifies the section 8 fixes).
- [x] Deleted furniture disappears on the other player's side — no invisible ghosts left in the
      shop.

---

## 8. In-game findings and fixes (2026-10-10, first co-op session)

Runtime testing (by the operator, this session) confirmed fixes 1 and 2 of section 6 — purchase
forwarding and the donation gate — and surfaced one more bug, triggered by deleting a placed
register.

**Symptom:** the guest deleted the shop's normal register, then could not man the Diamond
Checkout register; every claim was silently rejected and register deltas for the shifted counter
were deferred forever (`prediction rollback client key=register:1`, `[register] deferring delta
counter=0 counterGen=2`).

**Root cause (two layers):**

1. When a player trashes boxed furniture, the guest-side box capture
   (`BoxNetworkInteraction.CaptureClientDestroyed`) detaches the boxed object before the vanilla
   destroy — deliberately, so unboxing does not tear down the placed piece — and the placement
   `Remove` handler only forgot the placement identity. The host destroys the furniture in
   `HostDestroyRequested`, so the guest kept an **inactive ghost** inside the game's lists. With
   `m_CashierCounterList` = [ghost, Diamond] on the guest and [Diamond] on the host, the guest's
   claim named index 1 where the host had nothing — silently rejected.
2. The register module keys stations by list index + generation. After the shift, the host's
   `Observe` rebinds index 0 to the Diamond and bumps the generation, but the bump was never
   published, so the guest's generation for that index stayed stale and every non-ownership
   delta was deferred (`HasCounterGeneration`).

**Fixes (this change set):**

- `PlacementEntityState.Apply(Remove)` now destroys the local counterpart through the game's own
  `InteractableObject.OnDestroyed()` (list removal, UI teardown, GameObject destroy) instead of
  only forgetting its identity. Unbox/warehouse flows are unaffected — the host publishes a
  placement remove only for entities it genuinely destroyed.
- `RegisterHostBehaviour.ObservePublishing` publishes a prediction-free `CounterLifecycle`
  whenever a live observe rebinds an index to a different counter (a list shift or object
  replacement), so clients adopt the new generation and re-apply their deferred deltas. Used by
  `NewDelta`, `HandleIntent` and `PublishPaidAmount`; join baselines and the add/remove
  publishes keep their existing behavior.

---

## Appendix — fingerprints

| | |
|---|---|
| Furniture Overhaul DLL | `BepInEx\plugins\FurnitureOverhaul\FurnitureOverhaul.dll` |
| SHA-256 | `F5644443483507726B4B7224C5218C8A993FA935932FD3DDFA8A8A3E4CD883DC` |
| Version / GUID | 1.6.1 / `com.qolmods.furnitureoverhaul` |
| Decompiled copy | `decompiled/FurnitureOverhaul` (git-ignored) |
| CardShopCoop repo | `5c58f12`, version 2.0.0 |
