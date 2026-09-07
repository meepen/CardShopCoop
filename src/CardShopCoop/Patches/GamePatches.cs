using System;
using System.Reflection;
using CardShopCoop.Sync;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Patches
{
    /// <summary>
    /// All Harmony patches, registered one-by-one so a single signature change in a game
    /// update degrades one feature instead of killing the whole plugin.
    ///
    /// Client-role philosophy: the joining player lives inside a mirrored copy of the
    /// host's world. The host's simulation is the only real one, so on the client we
    /// suppress every local mutation source (customers, workers, day-end) and protect
    /// the player's own save slots.
    /// </summary>
    public static class GamePatches
    {
        private static readonly FieldInfo FiLightDayEnded =
            AccessTools.Field(typeof(LightManager), "m_HasDayEnded");

        public static void ApplyAll(Harmony h)
        {
            // Client saves always land in the co-op slot, never the player's own slots.
            Try(h, typeof(CGameManager), "SaveGameData",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(SaveGuardPrefix)));

            // No local customer simulation on the client (host streams the real economy).
            Try(h, typeof(CustomerManager), "Update",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(ClientBlockPrefix)));
            Try(h, typeof(Customer), "Update",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(CustomerUpdatePrefix)));
            Try(h, typeof(Customer), "ActivateCustomer",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(CustomerActivatePrefix)));

            // No local workers on the client either.
            Try(h, typeof(WorkerManager), "ActivateWorker",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(ClientBlockPrefix)));
            Try(h, typeof(Worker), "PlayWorkerActionAnim",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(WorkerActionPostfix)));

            // The client's clock follows the host; its own day must never end.
            Try(h, typeof(CEventManager), "QueueEvent",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(DayEndBlockPrefix)));

            // ...and the joiner must never OPEN the day-end recap himself. The mirrored
            // clock parks at 21:00, where LightManager.Update re-latches m_HasDayEnded true
            // every frame (our DayTime mirror only clears it on the host's 2s beat), so a
            // guest who presses Enter before the host does runs the whole vanilla
            // ShowGoNextDayScreen: local ShelfManager/WorkerManager.OnPressGoNextDay (staff
            // sent home, shelves swept on a mirror that owns neither) plus an
            // EndOfDayReportScreen the host never broadcast - whose close then files a
            // zeroed (or yesterday's) day into his phone history and swallows the real
            // recap when it finally arrives. On a client the ONLY thing allowed to raise
            // that screen is ReportSync's open-screen mirror.
            Try(h, typeof(InteractionPlayerController), "ShowGoNextDayScreen",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(GoNextDayScreenBlockPrefix)));

            // Alternate switch implementations and mods may call LightManager directly;
            // route those client requests through the host as well.
            Try(h, typeof(LightManager), "ToggleShopLight",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(ClientLightTogglePrefix)));
            Try(h, typeof(LightManager), "Update",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ClientLightUpdatePostfix)));
            Try(h, typeof(LightManager), "ToggleShopLight",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(LightStateChangedPostfix)));
            Try(h, typeof(LightManager), "Init",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(LightStateChangedPostfix)));
            Try(h, typeof(LightManager), "UpdateLightTimeData",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(LightStateChangedPostfix)));

            // Shared card collection: every add/remove on either side mirrors to the other,
            // so the joiner's pack pulls land in the real binder (and vice versa).
            Try(h, typeof(CPlayerData), "AddCard",
                prefix: null, postfix: new HarmonyMethod(typeof(GamePatches), nameof(AddCardPostfix)));
            Try(h, typeof(CPlayerData), "ReduceCard",
                prefix: null, postfix: new HarmonyMethod(typeof(GamePatches), nameof(ReduceCardPostfix)));

            // Shared card pricing: SetCardPrice writes the price table and fires the UI
            // refresh event itself, so mirroring the call keeps tags in step on both sides.
            Try(h, typeof(CPlayerData), "SetCardPrice",
                prefix: null, postfix: new HarmonyMethod(typeof(GamePatches), nameof(SetCardPricePostfix)));

            // The joiner's restock ORDERS spawn on the HOST (officially, visible to all,
            // mirrored back by BoxSync) instead of as local phantoms.
            Try(h, typeof(RestockManager), "SpawnPackageBoxItemMultipleFrame",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(OrderPrefix)));

            // The client is a pure box mirror. RestockManager.Update's out-of-bounds /
            // warehouse-lock sweep teleports stray boxes to an INDEPENDENT random spawn
            // point; run on the guest it scatters boxes to spots that diverge from the host
            // (then get adopted as authoritative). Keep the OOB timer pinned below its 5s
            // threshold on the client so that sweep never fires, while the spawn-drip and
            // delayed-reset work earlier in Update still run.
            Try(h, typeof(RestockManager), "Update",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(RestockUpdatePrefix)));

            // The client must NOT roll its own card BASE market prices while the host's are
            // here. RestockManager.Init calls GenerateCardMarketPrice per expansion, which for
            // every card index whose GetCardMarketPrice is still 0 rolls a fresh base with the
            // LOCAL Unity Random (RestockManager.cs GenerateCardMarketPrice ~237 guard then
            // ~255-271 Random.Range -> SetCardGeneratedMarketPrice ~314). The host had that same
            // index at 0 too, so both sides roll DIFFERENT bases, leaving guest and host binder
            // totals silently diverged. Normally the join-time save transfer writes
            // m_GenCardMarketPriceList directly (not through this method) and the prefix skips
            // the roll; it lets the roll through ONLY for an expansion the transfer left
            // all-zero, which MarketSync then repairs. See the prefix.
            Try(h, typeof(RestockManager), "GenerateCardMarketPrice",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(GenerateCardMarketPriceBlockPrefix)));

            // The shop already has a name (the host's); the joiner's copy must neither
            // prompt for one nor let it be changed.
            Try(h, typeof(ShopRenamer), "ShowRenameShopScreen",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(RenamerBlockPrefix)));

            // The joiner's FURNITURE purchases spawn on the host (as the official delivery
            // box); the placed object mirrors back through the population sync.
            Try(h, typeof(ShelfManager), "SpawnInteractableObjectInPackageBox",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(FurnitureOrderPrefix)));

            // Furniture SELL is host-only for now (money-printer guard). On the guest,
            // InteractionPlayerController.ConfirmSellFurniture credits the SHARED wallet via
            // CEventPlayer_AddCoin(price/2) - which our DayEndBlockPrefix forwards to the host
            // as a real coin gain - then destroys only the GUEST-side box. The host never sees
            // that box (FurnBoxSync finds no match), so the real furniture survives while the
            // wallet is credited = a repeatable printer. ConfirmSellFurniture only sees
            // m_CurrentHoldingBoxShelf (whatever boxed furniture the guest is holding) and can't
            // tell a host-placed shelf from a guest-bought-but-unplaced one, so we block ALL
            // guest furniture sells; the toast explains. Host selling is untouched (Role check).
            Try(h, typeof(InteractionPlayerController), "ConfirmSellFurniture",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(SellFurnitureBlockPrefix)));

            // Deco-object PLACEMENT is host-only for now. On the guest,
            // PlaceDecoUIScreen.StartPlaceDecoItem is a pure local action with no forward path:
            // it decrements inventory via AddDecoItemToInventory(-1) and spawns into the guest's
            // kind-5 m_DecoObjectList. The host's PopulationSync roster then destroys that orphan
            // deco (inventory lost for good) and its settled ObjMoveRequest can teleport a
            // DIFFERENT host deco sharing the same index. Block before the decrement; the toast
            // explains. Host placement is untouched (Role check).
            Try(h, typeof(PlaceDecoUIScreen), "StartPlaceDecoItem",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(PlaceDecoBlockPrefix)));

            // Handheld deodorant spray: the guest's hold-spray loop only ever hits the
            // LOCAL customer list - inert puppets on a client - so a guest could never
            // clean a smelly customer. Intercept the per-customer check and forward one
            // spray-tick op to the host, which replays it against its REAL customers
            // (CoopCore's SprayHit handler); the NpcSync smelly-flag mirror echoes the
            // cleaned state back automatically.
            Try(h, typeof(Customer), "DeodorantSprayCheck",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(DeodorantSprayPrefix)));

            // A joiner's forwarded furniture spawn runs BoxUpObject -> OnPlacedMovedObject,
            // whose interactive-move teardown DisableMoveObjectPreviewMode dereferences
            // m_MoveObjectPreviewModel. With no live player-move preview (there isn't one
            // for a programmatic spawn) that's null and NRE'd, aborting the spawn mid-way
            // so the delivery box never finished and the table never arrived - money gone
            // (field report: PlayTable). Skip the teardown when there's nothing to tear down.
            Try(h, typeof(ShelfManager), "DisableMoveObjectPreviewMode",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(DisablePreviewGuardPrefix)));

            // A trashed box must die on the host too, or the next broadcast resurrects
            // it at its old spot on the ground.
            Try(h, typeof(InteractablePackagingBox_Item), "OnDestroyed",
                prefix: new HarmonyMethod(typeof(GamePatches), nameof(BoxDestroyedPrefix)));

            // Selling/trashing a placed object re-indexes the ShelfManager list (every
            // index-keyed mirror keys off it). On the host the list is already shifted by
            // the time the subclass override reaches base.OnDestroyed - so this postfix is
            // the moment to tell the client to re-align its roster BEFORE the ~0.75s
            // content syncs broadcast shifted-index deltas that would otherwise land on the
            // wrong shelves (see NotifyHostStructureChanged).
            Try(h, typeof(InteractableObject), "OnDestroyed",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(InteractableObjectDestroyedPostfix)));

            // Mutation-driven fast path. The synchronizers still hash/coalesce their
            // snapshots, but these postfixes remove the normal polling latency after a
            // completed action. They intentionally run after vanilla has committed the
            // change, so the next co-op frame reads authoritative state.
            Try(h, typeof(InteractableObject), "PlaceMovedObject",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(InteractablePackagingBox_Item), "StartHoldBox",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(InteractablePackagingBox_Item), "ThrowBox",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(InteractablePackagingBox_Item), "DropBox",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(InteractablePackagingBox_Item), "FillBoxWithItem",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(InteractablePackagingBox_Item), "DispenseItem",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(InteractablePackagingBox_Item), "RemoveItemFromShelf",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(InteractablePackagingBox_Item), "SetOpenCloseBox",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(ShelfCompartment), "AddBox",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(ShelfCompartment), "RemoveBox",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(ShelfCompartment), "AddItem",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(ShelfCompartment), "RemoveItem",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(ShelfCompartment), "SpawnItem",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            // Removing a shelf label (right-click on the tag, when the compartment is empty)
            // only clears m_ItemType - none of the Add/Remove/Spawn paths above fire, so
            // without this hook the label change waited for the slow poll (and the far side
            // kept its label). Nudge the world sync immediately.
            Try(h, typeof(ShelfCompartment), "RemoveLabel",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(InteractableCardCompartment), "SetCardOnShelf",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(InteractableCardCompartment), "RemoveCardFromShelf",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));
            Try(h, typeof(InteractableCardCompartment), "DisableAllCard",
                postfix: new HarmonyMethod(typeof(GamePatches), nameof(ObjectMutationPostfix)));

            // Product licenses are shared: bought by either player, unlocked for both.
            // Identity travels as (itemType + box size), never a restock-list index -
            // modded restock lists can be ordered differently per machine.
            Try(h, typeof(CPlayerData), "SetUnlockItemLicense",
                prefix: null, postfix: new HarmonyMethod(typeof(GamePatches), nameof(LicenseUnlockPostfix)));

            // Workbench crafting and storage quick-fill remove cards through a direct
            // array write that never routes through ReduceCard - without this mirror
            // the shared binder silently keeps cards the other side already consumed.
            Try(h, typeof(CPlayerData), "ReduceCardUsingIndex",
                prefix: null, postfix: new HarmonyMethod(typeof(GamePatches), nameof(ReduceCardIndexPostfix)));

            // Graded cards leave the album ONLY via RemoveGradedCard (a direct list edit
            // ReduceCard never sees). Mirror it so a graded card traded/donated/re-graded
            // away on one side also leaves the other side's album instead of ghosting.
            Try(h, typeof(CPlayerData), "RemoveGradedCard",
                prefix: null, postfix: new HarmonyMethod(typeof(GamePatches), nameof(RemoveGradedCardPostfix)));

            // The pause menu sets Time.timeScale = 0, which zeroes Time.deltaTime for the
            // WHOLE process - freezing the co-op tick's sends and the other player's world
            // ("if host pauses, everything freezes on guest"). In a session, keep the world
            // running under the menu so nobody's pause freezes the shared shop.
            Try(h, typeof(PauseScreen), "OpenScreen",
                prefix: null, postfix: new HarmonyMethod(typeof(GamePatches), nameof(PauseNoFreezePostfix)));

            // Domain sync modules register their own patch sets (each guarded internally).
            TryModule("staff", Sync.StaffSync.ApplyPatches, h);
            TryModule("shopstate", Sync.ShopStateSync.ApplyPatches, h);
            TryModule("settings", Sync.SettingsSync.ApplyPatches, h);
            TryModule("market", Sync.MarketSync.ApplyPatches, h);
            TryModule("report", Sync.ReportSync.ApplyPatches, h);
            TryModule("containers", Sync.ContainerSync.ApplyPatches, h);
            TryModule("tournament", Sync.TournamentSync.ApplyPatches, h);
            TryModule("grading", Sync.GradingSync.ApplyPatches, h);
            TryModule("trades", Sync.TradeServe.ApplyPatches, h);
            TryModule("register", Sync.RegisterSync.ApplyPatches, h);
            TryModule("playtables", Sync.PlayTableSync.ApplyPatches, h);
            TryModule("cardboxes", Sync.CardBoxSync.ApplyPatches, h);
            TryModule("furnboxes", Sync.FurnBoxSync.ApplyPatches, h);
        }

        private static void TryModule(string name, Action<Harmony> apply, Harmony h)
        {
            try { apply(h); }
            catch (Exception e) { CoopPlugin.Log.LogWarning($"Module patches failed ({name}): {e.Message}"); }
        }

        public static void ReduceCardIndexPostfix(int index, ECardExpansionType expansionType, bool isDestiny, int reduceAmount)
        {
            if (ApplyingRemoteCards || CoopCore.Role == CoopRole.None) return;
            try
            {
                var card = CPlayerData.GetCardData(index, expansionType, isDestiny);
                if (card != null)
                    CoopCore.Instance?.ForwardCardDelta(card, reduceAmount, isAdd: false);
            }
            catch { }
        }

        public static bool BoxDestroyedPrefix(InteractablePackagingBox_Item __instance)
        {
            if (!BoxSync.ApplyingRemote) BoxSync.LocalBoxDestroyed?.Invoke(__instance);
            return true;
        }

        /// <summary>Host only: a placed object's OnDestroyed fired after the ShelfManager
        /// list was already re-indexed (a shelf was sold or trashed). Every index-keyed
        /// mirror goes stale from this moment, so tell the host to force the population
        /// broadcast for the next frame - the client reconciles its roster (position-aware)
        /// before the content syncs can emit shifted-index deltas. Scene-reload teardown
        /// (DestroyAllObject) is excluded: that isn't a mid-session removal, and the client
        /// is reloading anyway. Cards on display and packaging boxes are destroyed constantly
        /// during play but are NOT roster entries (they aren't in the ShelfManager lists the
        /// mirrors key off), so they're excluded too - otherwise every card sold would spam a
        /// population broadcast.</summary>
        public static void InteractableObjectDestroyedPostfix(InteractableObject __instance)
        {
            if (CoopCore.Role != CoopRole.Host) return;
            if (CoopCore.ClientReloading || __instance == null) return;
            // a placed object carries a real (or deco) object type; cards/boxes don't.
            if (__instance is InteractablePackagingBox) return;
            if (__instance is InteractableCard3d) return;
            if (__instance.m_ObjectType == EObjectType.None
                && __instance.m_DecoObjectType == EDecoObject.None) return;
            try { CoopCore.Instance?.NotifyHostStructureChanged(); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("OnDestroyed structure change: " + e.Message); }
        }

        public static void ObjectMutationPostfix()
        {
            if (CoopCore.Role != CoopRole.None)
                CoopCore.RequestImmediateObjectSync();
        }

        public static bool ApplyingRemoteLicense;

        public static void LicenseUnlockPostfix(int index)
        {
            if (ApplyingRemoteLicense || CoopCore.Role == CoopRole.None) return;
            try { CoopCore.Instance?.ForwardLicense(index); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("LicenseUnlockPostfix forward failed: " + e.Message); }
        }

        public static bool FurnitureOrderPrefix(EObjectType objType, UnityEngine.Vector3 spawnPos, UnityEngine.Quaternion spawnRot)
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            CoopCore.Instance?.ForwardFurniture((int)objType, spawnPos, spawnRot);
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "furniture delivered at the host's shop";
                CoopCore.Instance.RegisterLineTimer = 4f;
            }
            return false;
        }

        public static bool OrderPrefix(int restockIndex, int count)
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            CoopCore.Instance?.ForwardOrder(restockIndex, count);
            return false; // no local phantom boxes; the host's delivery mirrors back
        }

        private static readonly System.Reflection.FieldInfo FiOobTimer =
            AccessTools.Field(typeof(RestockManager), "m_OutofBoundCheckTimer");

        public static void RestockUpdatePrefix(RestockManager __instance)
        {
            // client only: hold the OOB timer under its 5s trigger so the teleport sweep
            // (RestockManager.Update, guarded by `if (!(m_OutofBoundCheckTimer > 5f)) return`)
            // never runs; the host owns box placement and re-broadcasts it every 1.5s.
            if (CoopCore.Role != CoopRole.Client) return;
            try { FiOobTimer?.SetValue(__instance, 0f); } catch { }
        }

        private static bool s_warnedNoCardBases;

        /// <summary>Client only: skip the local card-base-price roll WHEN the host's bases are
        /// actually here. On the host this runs and defines the authoritative bases; on the
        /// guest, running it would roll a DIFFERENT base (local Random) for every index the host
        /// also had at 0, so returning false leaves m_GenCardMarketPriceList as the join-time
        /// save transfer wrote it (the host's real bases).
        ///
        /// But that transfer can fail to land: CGameData.PropagateLoadData restores all seven
        /// card price tables behind ONE gate (decompiled/CGameData.cs:758-773), so when that gate
        /// reads false every base in every table stays 0. Vanilla's own repair is this very
        /// method (it fills only indices still at zero, decompiled/RestockManager.cs:237), so
        /// blocking it unconditionally left the guest at $0.00 on every card forever. With an
        /// all-zero table we now let vanilla roll, and MarketSync's next snapshot overwrites
        /// those local rolls with the host's bases - which only became safe once
        /// generatedMarketPrice went on the wire.
        ///
        /// The test is PER EXPANSION because the repair is: RestockManager.Init calls this once
        /// per expansion and each call writes only its OWN list
        /// (CPlayerData.SetCardGeneratedMarketPrice, decompiled/CPlayerData.cs:1157-1186).
        /// Testing one table for all of them would let the first call's fresh rolls read as
        /// "the host's bases arrived" and block the other five.</summary>
        public static bool GenerateCardMarketPriceBlockPrefix(ECardExpansionType expansionType)
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            bool landed;
            switch (expansionType)
            {
                case ECardExpansionType.Tetramon: landed = AnyCardBase(CPlayerData.m_GenCardMarketPriceList); break;
                case ECardExpansionType.Destiny: landed = AnyCardBase(CPlayerData.m_GenCardMarketPriceListDestiny); break;
                // one Ghost call fills BOTH halves - it doubles its range and splits them with
                // isDestiny (RestockManager.cs:214-226 -> CPlayerData.cs:1167-1175) - and the
                // load gate restores the pair together, so either one filled means both landed
                case ECardExpansionType.Ghost: landed = AnyCardBase(CPlayerData.m_GenCardMarketPriceListGhost)
                                                     || AnyCardBase(CPlayerData.m_GenCardMarketPriceListGhostBlack); break;
                case ECardExpansionType.Megabot: landed = AnyCardBase(CPlayerData.m_GenCardMarketPriceListMegabot); break;
                case ECardExpansionType.FantasyRPG: landed = AnyCardBase(CPlayerData.m_GenCardMarketPriceListFantasyRPG); break;
                case ECardExpansionType.CatJob: landed = AnyCardBase(CPlayerData.m_GenCardMarketPriceListCatJob); break;
                // An expansion we can't name has no table among MarketSync's seven either, so a
                // local roll into it could never be corrected by the host's snapshot. Keep
                // blocking: visible $0.00 beats prices that silently disagree with the host.
                default: return false;
            }
            if (landed) return false;
            if (!s_warnedNoCardBases)
            {
                s_warnedNoCardBases = true;
                CoopPlugin.Log.LogWarning("card base prices did not arrive with the join world (every generatedMarketPrice is 0) - letting vanilla roll local bases so cards aren't $0.00; MarketSync's next snapshot replaces them with the host's");
            }
            return true;
        }

        /// <summary>Any non-zero base means the join world's card price block landed for this
        /// table: vanilla never rolls a base of 0 (RestockManager.cs:314 always passes a
        /// positive multiplier), so 0 everywhere can only be the untouched initial state
        /// (CPlayerData.cs:563-565 seeds the lists with blank MarketPrice objects).</summary>
        private static bool AnyCardBase(System.Collections.Generic.List<MarketPrice> list)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].generatedMarketPrice != 0f) return true;
            return false;
        }

        /// <summary>Skip the move-preview teardown when there's no preview to tear down
        /// (a programmatically-spawned forwarded furniture order). The vanilla body
        /// dereferences m_MoveObjectPreviewModel unconditionally and NRE'd there,
        /// aborting the furniture spawn - so the table never arrived but was paid for.
        /// Runs only inside the game's own DisableMoveObjectPreviewMode call, so the
        /// ShelfManager provably exists (no fake-singleton hazard).</summary>
        public static bool DisablePreviewGuardPrefix()
        {
            try
            {
                var sm = CSingleton<ShelfManager>.Instance;
                // no live preview model = no interactive move in progress = nothing to
                // clean up; skipping avoids the NRE and lets the spawn finish
                if (sm == null || sm.m_MoveObjectPreviewModel == null) return false;
            }
            catch { return false; }
            return true; // real interactive move: run the vanilla teardown
        }

        public static bool RenamerBlockPrefix()
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "the host names the shop";
                CoopCore.Instance.RegisterLineTimer = 3f;
            }
            return false;
        }

        /// <summary>Client only: block the guest from selling any boxed furniture. The vanilla
        /// ConfirmSellFurniture would fire CEventPlayer_AddCoin(price/2) into the SHARED wallet
        /// (forwarded to the host as a real gain) and then destroy only the guest-side box, which
        /// the host never mirrors - so the furniture survives while the wallet is paid = a
        /// repeatable money printer. Returning false BEFORE the coin event stops the printer.
        /// We can't distinguish a host-placed shelf from a guest-bought-unplaced one here (the
        /// method only sees m_CurrentHoldingBoxShelf), so this blocks all guest furniture sells;
        /// the toast tells the guest to ask the host. Host is unaffected (Role check). Same
        /// block+toast idiom as RenamerBlockPrefix.</summary>
        public static bool SellFurnitureBlockPrefix()
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "selling furniture is host-only for now - ask the host";
                CoopCore.Instance.RegisterLineTimer = 3f;
            }
            return false;
        }

        /// <summary>Client only: block the guest from placing deco objects. The vanilla
        /// StartPlaceDecoItem is a pure local action with no forward path - it decrements deco
        /// inventory (AddDecoItemToInventory(-1)) and spawns an orphan into the guest's kind-5
        /// deco list that the host's PopulationSync roster then destroys (inventory lost), and
        /// whose settled move-request can teleport a DIFFERENT host deco at the same index.
        /// Returning false BEFORE the inventory decrement keeps the guest's deco intact. Host is
        /// unaffected (Role check). Same block+toast idiom as RenamerBlockPrefix.</summary>
        public static bool PlaceDecoBlockPrefix()
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "deco placement is host-only for now";
                CoopCore.Instance.RegisterLineTimer = 3f;
            }
            return false;
        }

        // ---- handheld deodorant spray (guest -> host forward) -----------------
        // The vanilla hold-spray loop (InteractionPlayerController.RaycastHoldSprayState
        // ~1612-1631) calls DeodorantSprayCheck once PER CUSTOMER per spray tick with the
        // SAME position, so dedupe to one forward per frame. And it must only forward for
        // the HANDHELD path: the guest's mirrored auto-cleanser calls this same method
        // locally too, and forwarding those would double-spray on top of the host's own
        // cleanser sim - so gate on the local player actually holding the spray with the
        // trigger down (both fields verified in decompiled InteractionPlayerController:
        // m_IsHoldingMouseDown ~1614, m_CurrentHoldSprayItem ~1621).
        private static InteractionPlayerController _sprayIpc; // fake-null safe: re-resolved when null
        private static int _sprayForwardFrame = -1;
        private static readonly System.Reflection.FieldInfo FiHoldSprayItem =
            AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldSprayItem");
        private static readonly System.Reflection.FieldInfo FiHoldingMouseDown =
            AccessTools.Field(typeof(InteractionPlayerController), "m_IsHoldingMouseDown");

        /// <summary>Client only: puppets are inert, so the local spray does nothing anyway -
        /// skip it and forward one op per spray tick to the host, whose real customers take
        /// the hit. The cleaned state echoes back through the NpcSync smelly mirror.</summary>
        public static bool DeodorantSprayPrefix(UnityEngine.Vector3 sprayPos, float range, int potency)
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            try
            {
                if (_sprayIpc == null) _sprayIpc = UnityEngine.Object.FindObjectOfType<InteractionPlayerController>();
                var ipc = _sprayIpc;
                bool handheld = ipc != null
                    && FiHoldSprayItem?.GetValue(ipc) != null
                    && FiHoldingMouseDown?.GetValue(ipc) is bool held && held;
                if (handheld && UnityEngine.Time.frameCount != _sprayForwardFrame)
                {
                    _sprayForwardFrame = UnityEngine.Time.frameCount;
                    CoopCore.Instance?.ForwardSprayHit(sprayPos, range, potency);
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("spray forward: " + e.Message); }
            return false; // local puppets stay untouched either way
        }

        public static bool ApplyingRemotePrice;

        public static void SetCardPricePostfix(CardData cardData, float priceSet)
        {
            if (ApplyingRemotePrice || CoopCore.Role == CoopRole.None) return;
            try
            {
                // Same encoded-grade swap AddCardPostfix does, and for the same reason - but
                // here it's also ORDERING-critical. Grading Overhaul's own SetCardPrice PREFIX
                // transiently DECODES cardData.cardGrade in place (encoded 370009134 -> bare 9)
                // and its POSTFIX restores it; our postfix can run in that window, so a naive
                // forward would ship the bare 9 and the host would file the price under the
                // wrong (ungraded) key - exactly why regular cards synced but graded ones didn't.
                // GradingInterop.Encoded reads GO's registry (GetEncodedOrCurrent), which still
                // returns the TRUE encoded value even while the field is transiently decoded, so
                // reading it here is ordering-proof. Restore the game's live object afterwards.
                int enc = Util.GradingInterop.Present ? Util.GradingInterop.Encoded(cardData) : cardData.cardGrade;
                if (enc > 10 && cardData.cardGrade <= 10 && cardData.cardGrade != 0)
                {
                    int saved = cardData.cardGrade;
                    cardData.cardGrade = enc;
                    try { CoopCore.Instance?.ForwardCardPrice(cardData, priceSet); }
                    finally { cardData.cardGrade = saved; } // never leave the game's object mutated
                }
                else CoopCore.Instance?.ForwardCardPrice(cardData, priceSet);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("SetCardPricePostfix forward failed: " + e.Message); }
        }

        /// <summary>True while we're applying a card delta that came over the network,
        /// so the postfixes don't echo it back forever.</summary>
        public static bool ApplyingRemoteCards;

        // These postfixes run INSIDE the game's own AddCard/ReduceCard calls - including
        // the manual pack-open loop (CardOpeningSequence.OpenScreen) which AddCards all 7
        // cards BEFORE building its reveal lists. A throw escaping here aborts that loop
        // half-built, and the per-frame reveal then IndexOutOfRanges forever = the guest
        // "freezes on the first card". Never let a forward failure escape into the caller.
        public static void AddCardPostfix(CardData cardData, int addAmount)
        {
            if (ApplyingRemoteCards || CoopCore.Role == CoopRole.None) return;
            try
            {
                // Forward the ENCODED grade for a graded card. Some display/album paths
                // transiently set cardData.cardGrade to the bare 1-10 while the true encoded
                // value lives in Grading Overhaul's registry; forwarding a bare 1-10 would
                // make the other side treat it as an ungraded card. Read the registry and
                // send the encoded value, restoring the game's live object afterwards.
                int enc = Util.GradingInterop.Present ? Util.GradingInterop.Encoded(cardData) : cardData.cardGrade;
                if (enc > 10 && cardData.cardGrade <= 10 && cardData.cardGrade != 0)
                {
                    int saved = cardData.cardGrade;
                    cardData.cardGrade = enc;
                    try { CoopCore.Instance?.ForwardCardDelta(cardData, addAmount, isAdd: true); }
                    finally { cardData.cardGrade = saved; } // never leave the game's object mutated
                }
                else CoopCore.Instance?.ForwardCardDelta(cardData, addAmount, isAdd: true);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("AddCardPostfix forward failed: " + e.Message); }
        }

        public static void ReduceCardPostfix(CardData cardData, int reduceAmount)
        {
            if (ApplyingRemoteCards || CoopCore.Role == CoopRole.None) return;
            try { CoopCore.Instance?.ForwardCardDelta(cardData, reduceAmount, isAdd: false); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ReduceCardPostfix forward failed: " + e.Message); }
        }

        /// <summary>Mirror host-side graded-card REMOVALS (trade-in of a wanted-back graded
        /// card, grading re-submit, donation). Graded cards live in a separate inventory
        /// (m_GradedCardInventoryList) that ReduceCard never touches, so without this the
        /// other side keeps a ghost graded card the owner no longer has - the "graded card
        /// turned fake" report. Runs inside the game's RemoveGradedCard; must never throw.</summary>
        public static void RemoveGradedCardPostfix(CardData cardData)
        {
            if (ApplyingRemoteCards || CoopCore.Role == CoopRole.None) return;
            if (cardData == null || cardData.cardGrade <= 0) return;
            try
            {
                // The SAME encoded-grade swap AddCardPostfix does, for the same reason and with
                // the same restore - a removal is only useful to the peer if it names the card in
                // the identity the peer FILED it under, and for a graded card that identity IS the
                // encoded grade (CPlayerData.RemoveGradedCard matches on amount == cardGrade,
                // decompiled :1552-1571). Shipping a bare 1-10 that a display path transiently
                // swapped in makes the removal match nothing on the other side, and the ghost the
                // add direction was fixed to prevent comes straight back on the remove direction.
                int enc = Util.GradingInterop.Present ? Util.GradingInterop.Encoded(cardData) : cardData.cardGrade;
                if (enc > 10 && cardData.cardGrade <= 10)
                {
                    int saved = cardData.cardGrade;
                    cardData.cardGrade = enc;
                    try { CoopCore.Instance?.ForwardGradedRemoval(cardData); }
                    finally { cardData.cardGrade = saved; } // never leave the game's object mutated
                }
                else CoopCore.Instance?.ForwardGradedRemoval(cardData);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("RemoveGradedCardPostfix forward failed: " + e.Message); }
        }

        private static void Try(Harmony h, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"Patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"Patch failed for {type.Name}.{method}: {e.Message}");
            }
        }

        /// <summary>Keep the world running while the pause menu is open during co-op, so a
        /// host (or guest) opening pause doesn't zero Time.timeScale and freeze the other
        /// player's shop + the network tick. The menu still shows; only the freeze is undone.</summary>
        public static void PauseNoFreezePostfix()
        {
            if (CoopCore.Role != CoopRole.None) UnityEngine.Time.timeScale = 1f;
        }

        public static bool SaveGuardPrefix()
        {
            // A joiner is a visitor in the HOST's save: it re-downloads the world at every
            // join, so client-side saving is pure waste - and worse, it would overwrite the
            // guest's OWN save slot with the host's shop. Block a save whenever we're a
            // client OR still holding a borrowed (host) world - the latter covers the
            // window AFTER a mid-session disconnect (Role back to None) while the guest is
            // still standing in the host's world, where a day-end autosave or a quit-save
            // used to slip through and pollute the guest's slot. The host's save is the
            // single source of truth. (No ref param: we only ever block, never reroute.)
            return CoopCore.Role != CoopRole.Client && !CoopCore.GuestBorrowedWorld;
        }

        public static bool ClientBlockPrefix()
        {
            return CoopCore.Role != CoopRole.Client;
        }

        public static bool CustomerActivatePrefix()
        {
            return CoopCore.Role != CoopRole.Client || RegisterSync.AllowClientCustomerLifecycle;
        }

        public static bool CustomerUpdatePrefix(Customer __instance)
        {
            return CoopCore.Role != CoopRole.Client;
        }

        public static void WorkerActionPostfix(Worker __instance)
        {
            Sync.NpcSync.RecordWorkerAction(__instance);
        }

        /// <summary>Client only: the joiner never opens his own end-of-day recap. Vanilla
        /// InteractionPlayerController.Update reaches ShowGoNextDayScreen on any Enter press
        /// while LightManager.GetHasDayEnded() is true - which on a mirrored 21:00 clock is
        /// nearly every frame - and the body then runs ShelfManager.OnPressGoNextDay,
        /// WorkerManager.OnPressGoNextDay and EndOfDayReportScreen.OpenScreen locally. The
        /// recap is a HOST-driven broadcast here (ReportSync.TryOpenReportScreen); an
        /// un-mirrored open shows numbers the host never sent and files them into the
        /// joiner's report history on close. Same Role-check idiom as ClientBlockPrefix.</summary>
        public static bool GoNextDayScreenBlockPrefix()
        {
            return CoopCore.Role != CoopRole.Client;
        }

        public static bool ClientLightTogglePrefix()
        {
            if (CoopCore.Role != CoopRole.Client) return true;
            if (!ShopStateSync.ApplyingRemote) ShopStateSync.RequestLightToggle();
            return false;
        }

        public static void ClientLightUpdatePostfix(LightManager __instance)
        {
            if (CoopCore.Role == CoopRole.Client)
                FiLightDayEnded?.SetValue(__instance, false);
        }

        public static void LightStateChangedPostfix(LightManager __instance)
        {
            CoopCore.Instance?.ObserveHostLightState(__instance);
        }

        /// <summary>Set by CoopCore right before it mirrors a host day-change, so exactly
        /// one OnDayStarted gets through to refresh the HUD/day label on the client.</summary>
        public static bool AllowNextDayStarted;

        public static bool DayEndBlockPrefix(CEvent evt)
        {
            if (CoopCore.Role != CoopRole.Client) return true;

            // The client's clock follows the host; its own day must never end.
            if (evt is CEventPlayer_OnDayEnded) return false;
            if (evt is CEventPlayer_OnDayStarted)
            {
                if (AllowNextDayStarted) { AllowNextDayStarted = false; return true; }
                return false;
            }

            // Shared-wallet contribution: gains/spends the JOINER earns are forwarded to
            // the host (who banks them for real) instead of applying to the mirrored copy.
            // Only Add*/Reduce* are intercepted - the Set* events the sync itself uses
            // pass through, so there is no feedback loop.
            if (evt is CEventPlayer_AddCoin addCoin)
            {
                if (RegisterSync.SuppressClientRegisterEvents) return false;
                CoopCore.Instance?.ForwardContribution(1, (float)addCoin.m_CoinValue);
                return false;
            }
            if (evt is CEventPlayer_ReduceCoin reduceCoin)
            {
                if (RegisterSync.SuppressClientRegisterEvents) return false;
                CoopCore.Instance?.ForwardContribution(2, (float)reduceCoin.m_CoinValue);
                return false;
            }
            if (evt is CEventPlayer_AddShopExp addExp)
            {
                if (RegisterSync.SuppressClientRegisterEvents) return false;
                CoopCore.Instance?.ForwardContribution(3, addExp.m_ExpValue);
                return false;
            }
            if (evt is CEventPlayer_AddFame addFame)
            {
                if (RegisterSync.SuppressClientRegisterEvents) return false;
                CoopCore.Instance?.ForwardContribution(4, addFame.m_FameValue);
                return false;
            }
            // The joiner set an item price: apply locally AND tell the host, whose price
            // table is authoritative and echoes to everyone (guarded against the echo).
            if (evt is CEventPlayer_ItemPriceChanged priceEvt && !ApplyingRemotePrice)
            {
                CoopCore.Instance?.ForwardItemPrice(priceEvt.m_ItemType, priceEvt.m_Price);
                return true; // let it apply locally too - instant feedback
            }
            return true;
        }
    }
}
