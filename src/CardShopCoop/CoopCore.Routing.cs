using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using CardShopCoop.Sync;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        private void RegisterDomainRoutes()
        {
            _messageRouter.Register<ShelfDeltaMessage>((context, message) =>
            {
                // Dropping deltas while not in the game scene is safe (the world just
                // loaded from the host's save; the host keeps re-diffing changes) and
                // avoids touching scene managers that don't exist yet.
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is ShelfDeltaMessage shelfDelta)
                {
                    if (shelfDelta.Entries == null)
                        return;
                    _world.ApplyRemote(shelfDelta.Entries);
                }
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<ShelfRequestMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is ShelfRequestMessage shelfRequest)
                {
                    if (shelfRequest.Entries == null)
                        return;
                    var applied = _world.ApplyRequest(shelfRequest.Entries, context.ConnectionId);
                    // applying updates the host's diff baseline, so its own tick
                    // never re-detects this change - with 3+ players the OTHER
                    // clients must be told explicitly, with the HOST's merged state
                    // (not the requester's absolute) so a concurrent take is not undone.
                    if (_net.ConnectionCount > 1 && applied != null && applied.Count > 0)
                        Broadcast(new ShelfDeltaMessage { Entries = applied });
                }
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _world.RequestResync?.Invoke());
            _messageRouter.Register<ShelfTransferResultMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is ShelfTransferResultMessage result)
                    _world.ApplyTransferResult(result);
                return;
            },
                MessagePolicy.ClientOnlyInGame, true, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<PriceListMessage>((context, message) =>
            {
                if (Role != CoopRole.Client)
                    return;
                if (message is PriceListMessage priceList)
                {
                    Patches.GamePatches.ApplyingRemotePrice = true; // don't echo these back
                    try
                    {
                        _incomingPriced.Clear();
                        int changed = 0;
                        if (priceList.Prices == null)
                            return;
                        for (int k = 0; k < priceList.Prices.Count; k++)
                        {
                            var priceEntry = priceList.Prices[k];
                            // Both sets below are keyed by LOCAL item ids (they are compared
                            // against our own catalog and against _myItemPriceEdits), so the
                            // translation has to happen here, before anything is recorded.
                            bool known = Util.EnumMap.TryFromWire(Util.EnumKind.ItemType, priceEntry.ItemType, out int i);
                            float v = priceEntry.Price;
                            // A price for a product only the HOST has: there is nothing local
                            // to price, and - critically - it must NOT be recorded as priced,
                            // because every unmappable id would collapse onto the same None
                            // sentinel and the clear pass below reads this set.
                            if (!known)
                                continue;
                            _incomingPriced.Add(i);
                            // GetItemPrice/SetItemPrice index m_SetItemPriceList[(int)itemType];
                            // an id outside it (an unmapped/modded local id) would throw.
                            var priceSlots = CPlayerData.m_SetItemPriceList;
                            if (i < 0 || priceSlots == null || i >= priceSlots.Count)
                                continue;
                            // this table was built BEFORE our own ItemPriceContrib landed:
                            // for a few seconds our fresh edit outranks it (it still counts
                            // as "priced" above, so the clear pass below leaves it alone)
                            if (HeldLocalItemPrice(i, v))
                                continue;
                            // write through the game's WOVEN SetItemPrice: raw list
                            // writes for modded types land in a shadow list the game
                            // never reads (EPL routes those rows to its own save
                            // data), which kept joiner tags at "-" while the value
                            // "applied" - and SetItemPrice fires the tag-repaint
                            // event itself
                            float cur = 0f;
                            try
                            {
                                cur = CPlayerData.GetItemPrice((EItemType)i, preventZero: false);
                            }
                            catch (System.Exception e) { Swallow.Log(e); }
                            if (Math.Abs(cur - v) > 0.0001f)
                            {
                                try
                                {
                                    CPlayerData.SetItemPrice((EItemType)i, v);
                                    changed++;
                                }
                                catch (System.Exception e) { Swallow.Log(e); }
                            }
                        }
                        // stale-price reports were undiagnosable: applies were silent
                        if (changed > 0)
                            CoopPlugin.Log.LogInfo($"price apply: {changed} price(s) updated from host");
                        if (priceList.Full)
                        {
                            // a price the host CLEARED is absent from the full sparse set.
                            // BOTH sets hold LOCAL ids now, so this stays an apples-to-apples
                            // comparison. A host-only product can never be zeroed here: it never
                            // resolved, so it was never added above and so cannot be in
                            // _clientPriced either. One-sided content packs keep their prices.
                            foreach (int i in _clientPriced)
                                if (!_incomingPriced.Contains(i) && i >= 0 && i <= 500000)
                                {
                                    // a clear is an overwrite too: the host simply hasn't seen
                                    // our brand-new price yet
                                    if (HeldLocalItemPrice(i, 0f))
                                        continue;
                                    float cur = 0f;
                                    try
                                    {
                                        cur = CPlayerData.GetItemPrice((EItemType)i, preventZero: false);
                                    }
                                    catch (System.Exception e) { Swallow.Log(e); }
                                    if (cur != 0f)
                                    {
                                        try
                                        {
                                            CPlayerData.SetItemPrice((EItemType)i, 0f);
                                        }
                                        catch (System.Exception e) { Swallow.Log(e); }
                                    }
                                }
                            var tmp = _clientPriced;
                            _clientPriced = _incomingPriced;
                            _incomingPriced = tmp;
                        }
                        else
                        {
                            // Partial: only the listed entries changed (a clear arrives as an
                            // explicit 0 entry). Record the ids so a later full table can
                            // still reconcile any the host drops.
                            _clientPriced.UnionWith(_incomingPriced);
                        }
                    }
                    finally { Patches.GamePatches.ApplyingRemotePrice = false; }
                }
                return;
            },
                MessagePolicy.ClientOnly, false, heal: () => { });
            _messageRouter.Register<DayTimeMessage>((context, message) =>
            {
                if (Role != CoopRole.Client)
                    return;
                var timeMessage = message as DayTimeMessage;
                if (timeMessage == null)
                    return;
                {
                    int day = timeMessage.Day;
                    int hour = timeMessage.Hour;
                    int min = timeMessage.Minute;
                    float minFloat = timeMessage.MinuteFloat;
                    bool shopOnceOpen = timeMessage.ShopOnceOpen;
                    if (!_loggedTimeLink)
                    {
                        _loggedTimeLink = true;
                        CoopPlugin.Log.LogInfo($"Time link active (Day {day} {hour:00}:{min:00})");
                    }
                    HostTimeLine = $"Day {day + 1}  {hour:00}:{min:00}"; // HUD shows day+1
                    bool dayChanged = day != CPlayerData.m_CurrentDay;
                    CPlayerData.m_CurrentDay = day;
                    // Match the host's clock gate. Forcing this true made a client advance
                    // through a new morning while the host was still waiting to open shop.
                    CPlayerData.m_IsShopOnceOpen = shopOnceOpen;
                    if (dayChanged)
                        MarkClientDayResetPending();
                    try
                    {
                        if (_lightManager == null)
                            _lightManager = FindObjectOfType<LightManager>();
                        if (_lightManager != null)
                        {
                            EnforceClientClock(_lightManager);
                            // Always apply the host clock, even while a morning reset is
                            // pending. The old gate let the local clock run unchecked until
                            // it reached night, then dropped every lighting correction.
                            FiTimeHour?.SetValue(_lightManager, hour);
                            FiTimeMin?.SetValue(_lightManager, min);
                            FiTimeMinFloat?.SetValue(_lightManager, minFloat);
                            MiEvaluateTimeClock?.Invoke(_lightManager, null);
                            if (hour < 21)
                                FiHasDayEnded?.SetValue(_lightManager, false);

                            if (dayChanged || _clientDayResetPending)
                                TryStartClientDayReset();
                        }
                    }
                    catch (Exception e) { CoopPlugin.Log.LogWarning("day-time apply: " + e.Message); }
                }
                return;
            },
                MessagePolicy.ClientOnly, false, heal: null);
            _messageRouter.Register<RelayTagMessage>((context, message) =>
            {
                if (Role != CoopRole.Client)
                    return;
                var relayTag = message as RelayTagMessage;
                if (relayTag == null)
                    return;
                {
                    int senderId = relayTag.SenderId;
                    byte kind = relayTag.Kind;
                    int extra = (int)relayTag.Extra;
                    if (senderId == _selfId)
                        return;
                    if (kind == 0)
                        _avatars.ShowEmote(1000 + senderId);
                    else
                    {
                        _avatars.ShowTag(1000 + senderId, "opening a pack!", 3f);
                        _avatars.ShowPackOpen(1000 + senderId, extra);
                    }
                }
                return;
            },
                MessagePolicy.ClientOnly, false, heal: null);
            _messageRouter.Register<CardDeltaMessage>((context, message) =>
            {
                if (message is CardDeltaMessage cardDelta)
                {
                    if (!ApplyOrHoldCardDelta(cardDelta.IsAdd, cardDelta.Amount, cardDelta.Card, out bool relayAnyway) && !relayAnyway)
                        return; // held for the level load, or genuinely refused: don't propagate
                                // relayAnyway == this PC lacks the content pack but the delta is sound;
                                // a third player who HAS it still needs it, so fall through to the relay.
                                // Shared collection: a card a guest gained/lost has to reach the OTHER
                                // guests too, or their binder totals drift out of sync in 3+ player
                                // sessions. Forward to everyone except the sender.
                    RelayToOthers(context.ConnectionId, message);
                }
                return;
            },
                MessagePolicy.Any, true, heal: () => CoopPlugin.Log.LogError("Binder heal deferred: CardDelta requires a full binder resync message, which is a wire change."));
            _messageRouter.Register<CardDeltaBatchMessage>((context, message) =>
            {
                // the host only needs the per-delta rebuild when it actually has other
                // guests to relay to; snapshot BEFORE applying, because the game's AddCard
                // path may mutate the CardData we hand it
                bool needFiltered = Role == CoopRole.Host && _net != null && _net.ConnectionCount > 1;
                _batchRelayBuf.Clear();
                int total = 0, applied = 0, relayedOnly = 0;
                if (message is CardDeltaBatchMessage batchMessage)
                {
                    total = batchMessage.Deltas?.Count ?? 0;
                    if (total < 0 || total > CardDeltaBatchMax)
                    {
                        CoopPlugin.Log.LogWarning($"card delta batch: bogus count {total} - dropped");
                        return;
                    }
                    for (int i = 0; i < total; i++)
                    {
                        var delta = batchMessage.Deltas[i];
                        bool isAdd = delta.IsAdd;
                        int amount = delta.Amount;
                        CardData card = delta.Card;
                        var relayCopy = needFiltered ? SnapshotCard(card) : null;
                        bool ok, relayAnyway = false;
                        try
                        {
                            ok = ApplyOrHoldCardDelta(isAdd, amount, card, out relayAnyway);
                        }
                        catch (Exception e)
                        {
                            CoopPlugin.Log.LogWarning($"card delta batch: delta {i + 1}/{total} failed to apply ({e.Message}) - skipped");
                            continue; // one bad delta costs one delta, not the batch
                        }
                        // A delta this PC can't hold because it lacks the content pack still
                        // belongs in the relay set (its snapshot was taken BEFORE the apply,
                        // same as the applied ones) - a third player may have that pack.
                        if (!ok && !relayAnyway)
                            continue;
                        if (ok)
                            applied++;
                        else
                            relayedOnly++;
                        if (needFiltered)
                            _batchRelayBuf.Add(new PendingCard { IsAdd = isAdd, Amount = amount, Card = relayCopy });
                    }
                }
                // Same shared-collection fan-out as CardDelta, and it runs on EVERY exit
                // path above (clean, read-fault, apply-fault). The ORIGINAL bytes go out
                // untouched only when every delta applied here and none was merely forwarded
                // (encoded grades verbatim); otherwise the filtered rebuild carries the
                // accepted deltas PLUS the ones this PC lacks the content for - a delta we
                // genuinely refused (corrupt grade, would-go-negative, throw) must never
                // spread, exactly as in the single-delta case above.
                if (applied == total && relayedOnly == 0 && total > 0)
                    RelayToOthers(context.ConnectionId, message);
                else if (_batchRelayBuf.Count > 0)
                    RelayCardDeltaBatchToOthers(context.ConnectionId, _batchRelayBuf);
                return;
            },
                MessagePolicy.Any, true, heal: () => CoopPlugin.Log.LogError("Binder heal deferred: CardDeltaBatch requires a full binder resync message, which is a wire change."));
            _messageRouter.Register<NpcStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client)
                    return;
                if (message is NpcStateMessage npcState)
                    _npcs.ApplyBatch(npcState, InGameLevel());
                return;
            },
                MessagePolicy.ClientOnly, false, heal: () => { });
            _messageRouter.Register<NpcSpeechMessage>((context, message) =>
            {
                if (Role != CoopRole.Client)
                    return;
                if (message is NpcSpeechMessage npcSpeech)
                    _npcs.ShowSpeech(npcSpeech, InGameLevel());
                return;
            },
                MessagePolicy.ClientOnly, false, heal: () => { });
            _messageRouter.Register<NpcMoneyPopupMessage>((context, message) =>
            {
                if (Role != CoopRole.Client)
                    return;
                if (message is NpcMoneyPopupMessage npcMoney)
                    _npcs.ShowMoneyPopup(npcMoney, InGameLevel());
                return;
            },
                MessagePolicy.ClientOnly, false, heal: null);
            _messageRouter.Register<CardShelfDeltaMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is CardShelfDeltaMessage cardShelfDelta)
                {
                    if (cardShelfDelta.Entries == null)
                        return;
                    _cardShelves.ApplyRemote(cardShelfDelta.Entries, cardShelfDelta.Echo);
                }
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<CardShelfRequestMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is CardShelfRequestMessage cardShelfRequest)
                {
                    var entries = cardShelfRequest.Entries;
                    if (entries == null)
                        return;
                    _cardShelves.ApplyRemote(entries);
                    // ALWAYS echo the true post-apply state back - including to the sender in a
                    // 2-player session. The echo (MsgType.CardShelfDelta, Echo=true) is what lets
                    // the placing client tell "host has the card" from "host could not apply it":
                    // without it the client cannot distinguish a stale in-flight snapshot from a
                    // rejection, and the old code chose to delete the card. Other guests get the
                    // same state as a normal delta.
                    var echo = _cardShelves.ReadEntries(entries);
                    if (echo.Count > 0)
                        Broadcast(new CardShelfDeltaMessage { Echo = true, Entries = echo });
                }
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _cardShelves.ForceNextTick());
            _messageRouter.Register<BoxUpdateMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is BoxUpdateMessage boxUpdate)
                    _boxEngine.HostApplyUpdate(boxUpdate, context.ConnectionId);
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _boxEngine?.ForceNextTick());
            _messageRouter.Register<BoxTransferResultMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is BoxTransferResultMessage result)
                    _boxEngine.ClientApplyTransferResult(result);
                return;
            },
                MessagePolicy.ClientOnlyInGame, true, heal: () => _boxEngine?.RequestResyncCoalesced());
            _messageRouter.Register<BoxSnapshotMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is BoxSnapshotMessage boxSnap)
                    _boxEngine.ClientApplySnapshot(boxSnap);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _boxEngine?.RequestResyncCoalesced());
            _messageRouter.Register<JoinResyncRequestMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                // The guest asks after its world exists; re-emit every authoritative
                // baseline, not just boxes that happen to have a periodic scan. The shelf
                // engine's ForceResend only re-arms a diff scan, so broadcast its full shelf
                // state explicitly as well.
                ModulesForceResend();
                ModulesFullUpdate(context.Connection);
                _world.RequestResync?.Invoke();
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => { _boxEngine?.RequestFullSnapshot(); _market.ForceResend(); _warehouse.ForceResend(); });
            _messageRouter.Register<FullyJoinedMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || context.Connection == null)
                    return;
                // The peer must have passed Hello and completed the world transfer. A
                // duplicate is a protocol violation, not a request to replay metadata.
                // FullyJoined is valid once the authenticated handshake has entered the
                // transfer phase.  The client may still be finishing its scene load here;
                // gating this signal on InSessionWorld strands otherwise valid joins.
                // Every other phase is an idempotently ignored late, duplicate, or
                // unauthenticated signal.
                if (context.Connection.State != ConnectionState.Transferring)
                {
                    CoopPlugin.Log.LogWarning("Ignoring late/duplicate FullyJoined from connection "
                        + context.Connection.Id + " in phase " + context.Connection.State);
                    return;
                }
                _moduleRegistry?.OnFullyJoined(context.Connection);
                ModulesForceResend();
                ModulesFullUpdate(context.Connection);
                Send(context.ConnectionId, new FullyJoinedAckMessage());
                // Keep the host in Transferring while the ordered baseline and ACK are
                // queued.  This is what lets the client consume the baseline before its
                // transition, and makes the ACK the final handshake frame.
                if (!context.Connection.TryTransition(ConnectionState.FullyJoined))
                    CoopPlugin.Log.LogWarning("Could not complete FullyJoined transition for connection "
                        + context.Connection.Id + " in phase " + context.Connection.State);
            }, MessagePolicy.HostOnly);
            _messageRouter.Register<FullyJoinedAckMessage>((context, message) =>
            {
                if (Role == CoopRole.Client && context.Connection != null)
                {
                    if (context.Connection.State != ConnectionState.Transferring)
                    {
                        CoopPlugin.Log.LogWarning("Ignoring late/duplicate FullyJoinedAck from connection "
                            + context.Connection.Id + " in phase " + context.Connection.State);
                        return;
                    }
                    if (context.Connection.TryTransition(ConnectionState.FullyJoined))
                        _moduleRegistry?.OnFullyJoined(context.Connection);
                }
            }, MessagePolicy.ClientOnly);
            _messageRouter.Register<DisconnectMessage>((context, message) =>
            {
                var reason = message as DisconnectMessage;
                if (reason == null)
                    return;
                if (context.Connection == null)
                {
                    CoopPlugin.Log.LogWarning("Ignoring disconnect control without a connection");
                    return;
                }
                // Phase is advisory peer data.  Normalize only that field; retain the
                // bounded remote code/reason so malformed peers still explain the close.
                var phase = Enum.IsDefined(typeof(ConnectionState), reason.Phase)
                    ? (ConnectionState)reason.Phase : ConnectionState.Disconnecting;
                var info = new DisconnectInfo(reason.Reason, true, reason.Code,
                    reason.Retryable, phase);
                // A disconnect is transport-owned on both sides. In particular, hosts must
                // not ignore a client that is leaving. The first reason atomically wins in the
                // connection, so Shutdown/Stop cannot replace this structured remote detail.
                // The transport already completed this remote disconnect, including the
                // exactly-once terminal event and connection removal.
                if (Role == CoopRole.Client)
                    Shutdown("remote: " + info.Reason, info);
            }, MessagePolicy.Any);
            _messageRouter.Register<ShelfBoxPullMessage>((context, message) =>
            {
                _world.HostApplyBoxPull(message, context.ConnectionId, _boxEngine);
            }, MessagePolicy.HostOnlyInGame, false, heal: () =>
            {
                _boxEngine?.RequestFullSnapshot();
                _world.RequestResyncCoalesced();
            });
            _messageRouter.Register<BoxCollectMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is BoxCollectMessage boxCollect)
                    CardBoxOps.HostApplyCollect(boxCollect, context.ConnectionId);
                return;
            },
                MessagePolicy.HostOnlyInGame, false, heal: () => _boxEngine?.ForceNextTick());
            _messageRouter.Register<BoxCollectResultMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is BoxCollectResultMessage boxResult)
                    CardBoxOps.ClientApplyResult(boxResult);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _boxEngine?.RequestResyncCoalesced());
            _messageRouter.Register<BoxMotionMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is BoxMotionMessage boxMotion)
                    _boxEngine.HostApplyMotion(boxMotion, context.ConnectionId);
                return;
            },
                MessagePolicy.HostOnlyInGame, false, heal: () => _boxEngine?.ForceNextTick());
            _messageRouter.Register<BoxMotionStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is BoxMotionStateMessage boxMotionState)
                    _boxEngine.ClientApplyMotion(boxMotionState);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<PopStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is PopStateMessage popState)
                {
                    // Hand the WHOLE message to the module: it owns the Full/Index slice
                    // semantics now (a partial carries one kind, a full carries every kind).
                    // The old list overload + "Entries.Count == KindCount" guard assumed every
                    // PopState was a complete 16-kind roster, which is no longer true.
                    _population.ClientApply(popState);
                }
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<ToastMessage>((context, message) =>
            {
                if (Role != CoopRole.Client)
                    return;
                if (message is ToastMessage toast)
                {
                    RegisterLine = toast.Text ?? "";
                    RegisterLineTimer = 8f;
                    // support gold: the on-screen line vanishes in 8s, the log keeps it
                    CoopPlugin.Log.LogInfo("host says: " + RegisterLine);
                }
                return;
            },
                MessagePolicy.ClientOnly, false, heal: null);
            _messageRouter.Register<CatalogDigestMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is CatalogDigestMessage catalogDigest)
                    CompareCatalogs(catalogDigest, context.ConnectionId);
                return;
            },
                MessagePolicy.HostOnlyInGame, false, heal: null);
            _messageRouter.Register<GradedDigestMessage>((context, message) =>
            {
                // BOTH roles: the host compares a guest's digest, and a guest compares the
                // one the host sends back when it found a difference. On the client the peer
                // is always the host, so the diff is filed under conn 1 - the same id the
                // client sends to - rather than whatever the transport labelled the frame.
                if (Role == CoopRole.None || !InGameLevel())
                    return;
                bool amHost = Role == CoopRole.Host;
                if (message is GradedDigestMessage gradedDigest)
                    CompareGradedDigests(gradedDigest, amHost ? context.ConnectionId : 1, amHost);
                return;
            },
                MessagePolicy.InGameOnly, false, heal: null);
            _messageRouter.Register<LicenseUnlockMessage>((context, message) =>
            {
                if (!InGameLevel())
                    return;
                if (message is LicenseUnlockMessage licenseUnlock)
                {
                    int itemType = (int)licenseUnlock.ItemType;
                    bool isBig = licenseUnlock.IsBig;
                    string rdName = licenseUnlock.RestockName;
                    // Charge/product coupling (host only, charge-first): honor this
                    // cart's charge verdict (accept -> process, decline -> drop with
                    // toast) or hold a rare product-first straggler. Host-gated so the
                    // host->clients echo (Role==Client on the receiver) is untouched.
                    // LicenseUnlock is now a host broadcast; legacy inbound messages remain harmless.
                    bool ok = ApplyLicenseUnlock(itemType, isBig, rdName);
                    if (Role == CoopRole.Host)
                    {
                        if (ok) // echo to the other clients + confirm to the buyer
                        {
                            Broadcast(new LicenseUnlockMessage
                            {
                                ItemType = (EItemType)itemType,
                                IsBig = isBig,
                                RestockName = rdName
                            });
                            Send(context.ConnectionId, new ToastMessage { Text = $"license unlocked for everyone: {rdName}" });
                        }
                        else
                            Send(context.ConnectionId, new ToastMessage { Text = $"'{rdName}' license couldn't unlock on the host (product missing) - match your content packs" });
                    }
                }
                return;
            },
                MessagePolicy.InGameOnly, true, heal: null);
            _messageRouter.Register<LicenseStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is LicenseStateMessage licenseState)
                {
                    bool scanner = licenseState.Scanner;
                    var wanted = new HashSet<long>();
                    var wantedNames = new HashSet<long>();
                    if (licenseState.Entries == null)
                        return;
                    for (int i = 0; i < licenseState.Entries.Count; i++)
                    {
                        var e = licenseState.Entries[i];
                        // The name key below is already machine-independent, but the ID key
                        // is NOT a redundant spare: it is OR'd in, and matched against our
                        // OWN rl[i].itemType, so an untranslated modded id from a permuted
                        // registry is a false POSITIVE - it unlocks whichever local product
                        // happens to wear that number. Translate it, and when the host has a
                        // product we don't, add no id key at all rather than letting every
                        // unmappable one collapse onto the None sentinel and match together.
                        bool known = Util.EnumMap.TryFromWire(Util.EnumKind.ItemType, (int)e.ItemType, out int t);
                        bool big = e.Big;
                        int nameFnv = e.NameFnv;
                        if (known)
                            wanted.Add(((long)t << 1) | (big ? 1L : 0L));
                        wantedNames.Add(((long)(uint)nameFnv << 1) | (big ? 1L : 0L));
                    }
                    // don't re-lock during the window where our own purchase is
                    // still round-tripping to the host
                    bool allowLock = UnityEngine.Time.realtimeSinceStartupAsDouble
                        - _lastLicenseBuyTime > 12.0;
                    Guarded("license-apply", () =>
                    {
                        CPlayerData.m_IsScannerRestockUnlocked |= scanner;
                        var rl = Inv().m_StockItemData_SO.m_RestockDataList;
                        var flags = CPlayerData.m_IsItemLicenseUnlocked;
                        if (rl == null || flags == null)
                            return;
                        bool anyUnlocked = false;
                        for (int i = 0; i < rl.Count && i < flags.Count; i++)
                        {
                            if (rl[i] == null)
                                continue;
                            long big = rl[i].isBigBox ? 1L : 0L;
                            bool should = wanted.Contains(((long)(int)rl[i].itemType << 1) | big)
                                || wantedNames.Contains(((long)(uint)Fnv(rl[i].name ?? "") << 1) | big);
                            if (should && !flags[i])
                            {
                                Patches.GamePatches.ApplyingRemoteLicense = true;
                                try
                                {
                                    CPlayerData.SetUnlockItemLicense(i);
                                }
                                finally { Patches.GamePatches.ApplyingRemoteLicense = false; }
                                anyUnlocked = true;
                                try
                                {
                                    if (rl[i].itemType == EItemType.BasicCardBox)
                                        TutorialManager.AddTaskValue(ETutorialTaskCondition.UnlockBasicCardBox, 1f);
                                }
                                catch (System.Exception e) { Swallow.Log(e); }
                            }
                            else if (!should && flags[i] && i != 0 && allowLock)
                            {
                                // scrambled save-transfer flag (index order differs
                                // between machines): host truth says locked
                                flags[i] = false;
                            }
                        }
                        if (anyUnlocked)
                        {
                            try
                            {
                                GameInstance.m_IsItemLicenseUnlocked = true;
                            }
                            catch (System.Exception e) { Swallow.Log(e); }
                            RefreshLicensePanels();
                        }
                    });
                }
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: null);
            _messageRouter.Register<StaffOpMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is StaffOpMessage staffOp)
                    _staff.HostApplyOp(staffOp, context.ConnectionId);
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _staff.ForceResend());
            _messageRouter.Register<StaffInteractMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is StaffInteractMessage staffInteract)
                    StaffSync.ClientInteractionMessage(staffInteract);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<StaffStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is StaffStateMessage staffState)
                    _staff.ClientApplyState(staffState);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<ShopOpMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is ShopOpMessage shopOp)
                    _shopState.HostApplyOp(shopOp);
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _shopState.ForceResend());
            _messageRouter.Register<TutorialCreditMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is TutorialCreditMessage tutorialCredit)
                    _shopState.HostApplyTutorialCredit(tutorialCredit);
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _shopState.ForceResend());
            _messageRouter.Register<ShopStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is ShopStateMessage shopState)
                    _shopState.ClientApplyState(shopState);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<SettingsOpMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is SettingsOpMessage settingsOp)
                    _settings.HostApplyOp(settingsOp);
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _settings.ForceResend());
            _messageRouter.Register<SettingsStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is SettingsStateMessage settingsState)
                    _settings.ClientApplyState(settingsState);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<TvOpMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is TvOpMessage tvOp)
                    _tv.HostApplyOp(tvOp, context.ConnectionId);
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _tv.ForceResend());
            _messageRouter.Register<TvStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is TvStateMessage tvState)
                    _tv.ClientApplyState(tvState);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<MarketStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client)
                    return;
                if (message is MarketStateMessage marketState)
                    _market.ClientApplyOrBuffer(marketState, InGameLevel());
                return;
            },
                MessagePolicy.ClientOnly, false, heal: () => { CoopPlugin.Log.LogError("Market heal: requesting authoritative market state from host"); _market.ForceResend(); });
            _messageRouter.Register<ReportStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is ReportStateMessage reportState)
                    _report.ClientApplyState(reportState);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<ContainerOpMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is ContainerOpMessage containerOp)
                    _containers.HostApplyOp(containerOp, context.ConnectionId);
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _containers.ForceResend());
            _messageRouter.Register<ContainerStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is ContainerStateMessage containerState)
                    _containers.ClientApplyState(containerState);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<WarehouseStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is WarehouseStateMessage warehouseState)
                    _warehouse.ClientApplyState(warehouseState);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<WarehouseOpMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is WarehouseOpMessage warehouseOp)
                    _warehouse.HostApplyOp(warehouseOp, context.ConnectionId);
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _warehouse.ForceResend());
            _messageRouter.Register<WarehouseTakeResultMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is WarehouseTakeResultMessage takeResult)
                    _warehouse.ClientApplyTakeResult(takeResult);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<ContainerBoxTakeMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is ContainerBoxTakeMessage containerTake)
                    _containers.ClientApplyTakeAccepted(containerTake);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<ContainerPackClaimMessage>((context, message) =>
            {
                if (Role == CoopRole.Client && message is ContainerPackClaimMessage claim)
                    _containers.ClientApplyPackClaim(claim);
                return;
            },
                MessagePolicy.ClientOnly, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<TournamentStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is TournamentStateMessage tournamentState)
                    _tournament.ClientApplyState(tournamentState);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<FurnitureBoxOpMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is FurnitureBoxOpMessage furnOp)
                    FurnitureBoxOps.HostApplyOp(furnOp, context.ConnectionId);
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _boxEngine?.ForceNextTick());
            _messageRouter.Register<GradingOpMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is GradingOpMessage gradingOp)
                    _grading.HostApplyOp(gradingOp, context.ConnectionId);
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _grading.ForceResend());
            _messageRouter.Register<GradingStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is GradingStateMessage gradingState)
                    _grading.ClientApplyState(gradingState);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<TradeOpMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is TradeOpMessage tradeOp)
                    _trades.HostApplyOp(tradeOp, context.ConnectionId);
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _trades.ForceResend());
            _messageRouter.Register<TradeStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is TradeStateMessage tradeState)
                    _trades.ClientApplyState(tradeState);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _trades.ForceResend());
            _messageRouter.Register<TableStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is TableStateMessage tableState)
                    _tables.ClientApplyState(tableState);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<PlayTableMatchRequest>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is PlayTableMatchRequest request)
                    RoutePlayTableMatchRequest(request, context.ConnectionId);
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _tableMatches.ForceResend());
            _messageRouter.Register<PlayTableMatchResult>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is PlayTableMatchResult result)
                    RoutePlayTableMatchResult(result, context.ConnectionId);
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _tableMatches.ForceResend());
            _messageRouter.Register<PlayTableMatchState>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is PlayTableMatchState matchState)
                    _tableMatches.ClientApplyState(matchState);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<PlayerIntentMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is PlayerIntentMessage intent)
                    _intents.HostApplyOp(intent);
                return;
            },
                MessagePolicy.HostOnlyInGame, false, heal: null);
            _messageRouter.Register<LightStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is LightStateMessage lightState)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(lightState.LightJson))
                        {
                            _world.RequestResyncCoalesced();
                            return;
                        }
                        LightTimeData data;
                        try
                        {
                            data = JsonUtility.FromJson<LightTimeData>(lightState.LightJson);
                        }
                        catch (Exception e)
                        {
                            CoopPlugin.Log.LogWarning("light state parse: " + e.Message);
                            _world.RequestResyncCoalesced();
                            return;
                        }
                        if (data == null)
                            return;
                        // Reject an older heartbeat that was already queued before a
                        // rollover. A newer day heartbeat is the fallback for a missed
                        // DayTime packet and must schedule the same full environment reset.
                        if (lightState.HasDay)
                        {
                            if (lightState.Day < CPlayerData.m_CurrentDay)
                                return;
                            if (lightState.Day > CPlayerData.m_CurrentDay)
                            {
                                CPlayerData.m_CurrentDay = lightState.Day;
                                MarkClientDayResetPending();
                                TryStartClientDayReset();
                                return;
                            }
                        }
                        if (_lightManager == null)
                            _lightManager = FindObjectOfType<LightManager>();
                        if (_lightManager == null)
                            return;
                        EnforceClientClock(_lightManager);
                        int localIdx = FiTimeOfDayIdx?.GetValue(_lightManager) is int idx ? idx : -1;
                        int localHour = FiTimeHour?.GetValue(_lightManager) is int h ? h : -1;
                        int localMin = FiTimeMin?.GetValue(_lightManager) is int m2 ? m2 : 0;
                        int driftMin = Math.Abs((data.m_TimeHour * 60 + data.m_TimeMin) - (localHour * 60 + localMin));
                        bool phaseDiffer = localIdx != data.m_TImeOfDayIndex;

                        // LightState is the complete authoritative snapshot. Apply its
                        // clock before evaluating brightness so a client that already ran
                        // into night cannot remain dark while the reset latch is pending.
                        CPlayerData.m_LightTimeData = data;
                        FiTimeHour?.SetValue(_lightManager, data.m_TimeHour);
                        FiTimeMin?.SetValue(_lightManager, data.m_TimeMin);
                        FiTimeMinFloat?.SetValue(_lightManager, data.m_TimeMinFloat);
                        FiTimeOfDayIdx?.SetValue(_lightManager, data.m_TImeOfDayIndex);
                        bool groupsDiffer = _lightManager.m_NightlightGrp == null
                            || _lightManager.m_ShoplightGrp == null
                            || _lightManager.m_SunlightGrp == null
                            || _lightManager.m_NightlightGrp.activeSelf != data.m_IsNightLightOn
                            || _lightManager.m_ShoplightGrp.activeSelf != data.m_IsShopLightOn
                            || _lightManager.m_SunlightGrp.activeSelf != data.m_IsSunlightOn;

                        // Apply every authoritative light group, not just the shop-light
                        // switch. This also repairs mods that change the groups directly.
                        if (groupsDiffer)
                        {
                            _lightManager.m_NightlightGrp?.SetActive(data.m_IsNightLightOn);
                            _lightManager.m_ShoplightGrp?.SetActive(data.m_IsShopLightOn);
                            _lightManager.m_SunlightGrp?.SetActive(data.m_IsSunlightOn);
                        }
                        if (groupsDiffer || phaseDiffer)
                            MiEvaluateWorldUIBrightness?.Invoke(_lightManager, null);
                        ApplyClientLightSwitchModels(data.m_IsShopLightOn);
                        // re-run the game's own lighting restore only when the sky
                        // phase actually differs (avoids music/blend churn)
                        if (phaseDiffer || driftMin > 4)
                        {
                            FiFinishLoading?.SetValue(_lightManager, false);
                            MiLightInit?.Invoke(_lightManager, null);
                            CoopPlugin.Log.LogInfo($"lighting re-synced (phase {localIdx}->{data.m_TImeOfDayIndex}, drift {driftMin}min)");
                        }
                        FiHasDayEnded?.SetValue(_lightManager, false);
                    }
                    catch (Exception e) { CoopPlugin.Log.LogWarning("light apply: " + e.Message); }
                }
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<ItemPriceContribMessage>((context, message) =>
            {
                if (Role != CoopRole.Host)
                    return;
                if (message is ItemPriceContribMessage itemPriceContrib)
                {
                    // Host side, so this is the identity function. An unmappable id would
                    // arrive as EItemType.None (-1), which the existing range guard below
                    // already refuses.
                    int itemType = (int)itemPriceContrib.ItemType;
                    float price = itemPriceContrib.Price;
                    if (itemType >= 0 && itemType <= 500000)
                    {
                        // Resolvability first, same runtime-enum oracle as the card guards:
                        // a type this host has never heard of can't be priced here at all.
                        if (!Enum.IsDefined(typeof(EItemType), (EItemType)itemType))
                        {
                            CoopPlugin.Log.LogWarning($"item price for unknown item type {itemType} skipped - host missing content pack?");
                            // tell the SENDER too: the host log is invisible to the guest,
                            // who otherwise watches its price silently revert on the next
                            // PriceList with nothing anywhere explaining why
                            Send(context.ConnectionId, new ToastMessage { Text = "the host couldn't apply that price - it may be missing that product" });
                            return;
                        }
                        // WOVEN SetItemPrice, never the raw list: EPL routes modded
                        // rows to its own save data, and a raw write is a shadow
                        // entry the game (and our own woven-read broadcast) never
                        // sees. Fires the tag-repaint event itself.
                        Patches.GamePatches.ApplyingRemotePrice = true;
                        // a bare catch here swallowed the whole failure: the guest saw its
                        // price "accepted" and nothing anywhere said otherwise
                        bool priceThrew = false;
                        try
                        {
                            CPlayerData.SetItemPrice((EItemType)itemType, price);
                        }
                        catch (Exception e)
                        {
                            priceThrew = true;
                            CoopPlugin.Log.LogWarning($"item price apply ({(EItemType)itemType}): " + e.Message);
                        }
                        finally { Patches.GamePatches.ApplyingRemotePrice = false; }
                        // same reason as the unknown-type branch: the guest has to hear it
                        if (priceThrew)
                            Send(context.ConnectionId, new ToastMessage { Text = "the host couldn't apply that price - it may be missing that product" });
                        // the periodic PriceList broadcast echoes this to every client
                    }
                }
                return;
            },
                MessagePolicy.HostOnly, true, heal: () => { _coinHeal = 999f; _progressHeal = 999f; });
            _messageRouter.Register<ObjMoveDeltaMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is ObjMoveDeltaMessage objMoveDelta)
                {
                    if (objMoveDelta.Entries == null)
                        return;
                    _objMoves.ApplyRemote(objMoveDelta.Entries);
                }
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<ObjMoveRequestMessage>((context, message) =>
            {
                if (Role != CoopRole.Host || !InGameLevel())
                    return;
                if (message is ObjMoveRequestMessage objMoveRequest)
                {
                    var entries = objMoveRequest.Entries;
                    if (entries == null)
                        return;
                    // host authority: never let a client's (possibly stale) move-request
                    // override an object the host is actively dragging - that echo is what
                    // snapped placed machines back to their old spot every guest tick
                    // Relay only poses the host actually accepted. Broadcasting the
                    // original request made a rejected stale move teleport other guests.
                    var accepted = _objMoves.ApplyRemote(entries, dropIfHostMoving: true);
                    if (_net.ConnectionCount > 1 && accepted.Count > 0) // see ShelfRequest note
                        Broadcast(new ObjMoveDeltaMessage { Entries = accepted });
                }
                return;
            },
                MessagePolicy.HostOnlyInGame, true, heal: () => _objMoves.ForceNextTick());
            _messageRouter.Register<CardPriceSetMessage>((context, message) =>
            {
                if (message is CardPriceSetMessage cardPriceSet)
                {
                    var card = cardPriceSet.Card;
                    float price = cardPriceSet.Price;
                    if (!InGameLevel())
                    {
                        _pendingCardPrices.Add(new KeyValuePair<CardData, float>(card, price));
                        return;
                    }
                    // IN-FLIGHT EDIT GATE. The host's price heal rebroadcasts its own
                    // GetCardPrice for every displayed card; when our own CardPriceSet was
                    // lost, that heal used to stomp the guest's fresh edit right back to the
                    // old value. While we are still chasing an edit for this card, only OUR
                    // value is allowed in - the retry loop keeps working until the host
                    // confirms it (or gives up loudly).
                    string key = CardPriceKey(card);
                    if (key != null && _myCardPrices.TryGetValue(key, out var mine))
                    {
                        if (Math.Abs(mine.Value - price) <= CardPriceEpsilon)
                        {
                            mine.Acked = true;   // the other side is holding our value: ack
                            _myCardPrices[key] = mine;
                        }
                        else if (!mine.Acked)
                        {
                            return;               // stale heal racing our edit: ignore it
                        }
                        else
                        {
                            mine.Value = price;  // they legitimately re-priced it; adopt, or
                            _myCardPrices[key] = mine; // our heals would war with theirs
                        }
                    }
                    bool applied;
                    float actual;
                    bool relayAnyway;
                    Patches.GamePatches.ApplyingRemotePrice = true;
                    // graded (>10 encoded) prices route through Grading Overhaul's own store
                    // (register the card, then GO's SetCardPrice patch handles it); ungraded
                    // prices use the vanilla path; no-op for a modded grade without GO.
                    string who = PeerNames.TryGetValue(context.ConnectionId, out var pn) ? pn : ("conn " + context.ConnectionId);
                    try
                    {
                        applied = ApplyRemoteCardPrice(card, price, who, out actual, out relayAnyway);
                    }
                    finally { Patches.GamePatches.ApplyingRemotePrice = false; }
                    if (!applied)
                    {
                        // The HOST couldn't store it, but the price itself is fine (content
                        // pack missing here, encoded grade with no Grading Overhaul here,
                        // store rejected the write). Forward the ORIGINAL (card, price) once
                        // - a PURE RELAY, never the read-back, which would be this machine's
                        // wrong value. It doubles as the sender's ack (it sees its own number
                        // come back and stops retrying instead of burning 12 attempts) and
                        // lets guests that DO have the content pack converge.
                        // Loop-safe: only the host ever re-broadcasts, and a host's own
                        // Broadcast never comes back to it.
                        if (relayAnyway && Role == CoopRole.Host)
                        {
                            var passCard = card;
                            float passValue = price;
                            Broadcast(new CardPriceSetMessage { Card = passCard, Price = passValue });
                        }
                        return;
                    }
                    // HOST: the READ-BACK value goes straight back out to every client. That
                    // one broadcast is both the sender's ACK (this handler acked nothing
                    // before) and the 3+ player relay (it reached nobody but the host).
                    if (Role == CoopRole.Host)
                    {
                        _cardPriceHealDirty = true;
                        var echoCard = card;
                        float echoValue = actual;
                        Broadcast(new CardPriceSetMessage { Card = echoCard, Price = echoValue });
                    }
                }
                return;
            },
                MessagePolicy.Any, false, heal: () => { _coinHeal = 999f; _progressHeal = 999f; });
            _messageRouter.Register<RegisterStateMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is RegisterStateMessage registerState)
                    _register.ClientApplyState(registerState);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<RegisterCartMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                if (message is RegisterCartMessage registerCart)
                    _register.ClientApplyCart(registerCart);
                return;
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<RegisterRejectedMessage>((context, message) =>
            {
                if (Role != CoopRole.Client || !InGameLevel())
                    return;
                _register.ClientApplyRejected(message);
            },
                MessagePolicy.ClientOnlyInGame, false, heal: () => _world.RequestResyncCoalesced());
            _messageRouter.Register<RegisterOpMessage>((context, message) =>
            {
                if (!InGameLevel())
                    return;
                if (Role == CoopRole.Host)
                {
                    _register.HostApplyOp(message, context.ConnectionId);
                    return;
                }
                if (Role == CoopRole.Client)
                    _register.ClientApplyChange(message);
            },
                MessagePolicy.InGameOnly, true, heal: () => _register.ForceResend());
        }

        private static readonly FieldInfo FiPlayTableMode =
            AccessTools.Field(typeof(PlayTableGame), "m_IsPlayTableGameMode");
        private static readonly FieldInfo FiCurrentPlayTable =
            AccessTools.Field(typeof(PlayTableGame), "m_CurrentInteractablePlayTable");
        private readonly Dictionary<string, int> _playTableResultNonces =
            new Dictionary<string, int>();
        private int _playTableNonceGeneration = -1;
        private float _lastPlayTableMatchRejectLog = -1f;

        private void LogPlayTableMatchReject(string message)
        {
            if (Time.realtimeSinceStartup - _lastPlayTableMatchRejectLog < 1f)
                return;
            _lastPlayTableMatchRejectLog = Time.realtimeSinceStartup;
            CoopPlugin.Log.LogWarning(message);
        }

        private void RoutePlayTableMatchRequest(PlayTableMatchRequest request, int ownerConn)
        {
            _tableMatches.PruneExpiredHostReservations();
            EnsurePlayTableNonceSession();
            if (request.Op == PlayTableMatchRequest.OpCancel)
            {
                if (_tableMatches.IsAuthenticatedCancel(request.TableKey, request.MatchId, ownerConn,
                    request.Epoch, out var cancelled))
                {
                    // A released match is an authenticated tombstone. It is an explicit
                    // idempotent no-op; there is no live entry to inspect or release.
                    if (cancelled == null)
                    {
                        CoopPlugin.Log.LogInfo($"play-table match cancel already applied: table={request.TableKey} owner={ownerConn}");
                        return;
                    }
                    if (!_tableMatches.TryAcceptRequestSequence(ownerConn, request.RequestSequence))
                    {
                        LogPlayTableMatchReject($"play-table match stale cancel ignored: table={request.TableKey} sequence={request.RequestSequence}");
                        return;
                    }
                    CoopPlugin.Log.LogInfo($"play-table match cancel: table={request.TableKey} owner={ownerConn}");
                    // Once STARTED has been accepted, the host advances the phase revision.
                    // A leave packet may contain the pre-start revision, so authenticate it by
                    // match/owner/epoch and make repeated cancels harmless. Reserved cancels
                    // still require the observed revision when one was supplied.
                    if (cancelled.Phase == PlayTableMatchEntry.StateReserved
                        && request.Revision != 0 && request.Revision != cancelled.Revision)
                    {
                        LogPlayTableMatchReject($"play-table match stale cancel ignored: table={request.TableKey} revision={request.Revision}");
                        return;
                    }
                    _tableMatches.Release(request.TableKey, request.MatchId, "cancelled");
                }
                else
                    LogPlayTableMatchReject($"play-table match cancel rejected: table={request.TableKey} owner={ownerConn} not authenticated");
                return;
            }
            if (!_tableMatches.TryAcceptRequestSequence(ownerConn, request.RequestSequence))
            {
                LogPlayTableMatchReject($"play-table match replay rejected: owner={ownerConn} sequence={request.RequestSequence}");
                return;
            }
            if (request.Op == 2)
            {
                if (!_tableMatches.TryGetHostMatch(request.TableKey, out var started)
                    || started.OwnerConn != ownerConn
                    || started.MatchId != request.MatchId)
                {
                    LogPlayTableMatchReject($"play-table match started rejected: table={request.TableKey} owner={ownerConn} match={request.MatchId} not owner");
                    return;
                }
                // Started is a transition of this exact reservation. Zero was previously
                // treated as "no revision supplied", allowing a delayed/forged launch to
                // advance a newer reservation.
                if (request.Epoch <= 0 || request.Revision <= 0
                    || request.Epoch != started.Epoch || request.Revision != started.Revision
                    || (started.Phase != PlayTableMatchEntry.StateReserved
                        && started.Phase != PlayTableMatchEntry.StateStarted))
                {
                    LogPlayTableMatchReject($"play-table match started rejected: table={request.TableKey} match={request.MatchId} stale reservation");
                    return;
                }
                bool accepted = started.Phase == PlayTableMatchEntry.StateStarted
                    ? _tableMatches.RenewHostMatchLease(request.TableKey, request.MatchId,
                        request.Epoch, request.Revision, ownerConn)
                    : _tableMatches.MarkHostMatchStarted(request.TableKey, request.MatchId, request.Revision);
                if (!accepted)
                    LogPlayTableMatchReject($"play-table match started rejected: table={request.TableKey} match={request.MatchId} no live reservation");
                else if (started.Phase == PlayTableMatchEntry.StateReserved)
                    CoopPlugin.Log.LogInfo($"play-table match started: table={request.TableKey} owner={ownerConn}");
                return;
            }
            if (request.Op != PlayTableMatchRequest.OpStart)
            {
                LogPlayTableMatchReject($"play-table match rejected: owner={ownerConn} invalid op={request.Op}");
                return;
            }
            var sm = SceneRef<ShelfManager>.Get();
            var tables = sm != null ? sm.m_PlayTableList : null;
            if (tables == null || request.TableIndex >= tables.Count || tables[request.TableIndex] == null)
            {
                LogPlayTableMatchReject($"play-table match rejected: owner={ownerConn} table index={request.TableIndex} unavailable");
                return;
            }
            var table = tables[request.TableIndex];
            if (!PlacedObjectIdentity.TryMakeObjectKey(6, table, out int computedKey)
                || request.TableKey != computedKey)
            {
                LogPlayTableMatchReject($"play-table match rejected: owner={ownerConn} table key mismatch table={request.TableKey} computed={computedKey}");
                return;
            }
            if (_tableMatches.TryGetHostMatch(request.TableKey, out _))
            {
                LogPlayTableMatchReject($"play-table match rejected: table={request.TableKey} already reserved");
                return;
            }
            if (_tableMatches.HasHostReservationByOwner(ownerConn))
            {
                LogPlayTableMatchReject($"play-table match rejected: owner={ownerConn} already has a match");
                return;
            }
            if (HostPlayingAt(table))
            {
                LogPlayTableMatchReject($"play-table match rejected: host is playing table={request.TableKey}");
                return;
            }
            if (request.Seat > 1)
            {
                LogPlayTableMatchReject($"play-table match rejected: table={request.TableKey} invalid seat={request.Seat}");
                return;
            }
            var occupied = table.m_IsSeatOccupied;
            int customerSeat = request.Seat == 0 ? 1 : 0;
            if (occupied == null || occupied.Count <= request.Seat || occupied[request.Seat]
                || occupied.Count <= customerSeat || !occupied[customerSeat])
            {
                LogPlayTableMatchReject($"play-table match rejected: table={request.TableKey} requested seat={request.Seat} must be free and customer seat={customerSeat} must be occupied");
                return;
            }
            if (request.SideA != (request.Seat == 0))
            {
                LogPlayTableMatchReject($"play-table match rejected: table={request.TableKey} seat={request.Seat} sideA={request.SideA} geometry mismatch");
                return;
            }
            int maxDeck = GameInstance.GetMaxDeckCardCount();
            if (request.DeckCardCount < 1 || request.DeckCardCount > maxDeck)
            {
                LogPlayTableMatchReject($"play-table match rejected: table={request.TableKey} deck={request.DeckCardCount} max={maxDeck}");
                return;
            }
            if (string.IsNullOrEmpty(request.MatchId) || request.MatchId.Length > 64)
            {
                LogPlayTableMatchReject($"play-table match rejected: owner={ownerConn} invalid match id length={request.MatchId?.Length ?? 0}");
                return;
            }
            if (_tableMatches.TryGetHostReservationByMatch(request.MatchId, out _, out int matchTableKey))
            {
                LogPlayTableMatchReject($"play-table match rejected: match={request.MatchId} already reserved table={matchTableKey}");
                return;
            }
            CoopPlugin.Log.LogInfo($"play-table match accepted: table={request.TableKey} owner={ownerConn} seat={request.Seat} sideA={request.SideA}");
            _tableMatches.Reserve(request, ownerConn);
        }

        private void RoutePlayTableMatchResult(PlayTableMatchResult result, int sender)
        {
            if (result == null)
            {
                CoopPlugin.Log.LogWarning("play-table result rejected: null payload");
                return;
            }
            if (!IsFinite(result.DurationSeconds))
            {
                CoopPlugin.Log.LogWarning($"play-table result rejected: match={result.MatchId} non-finite duration={result.DurationSeconds}");
                return;
            }
            if (result.Result < PlayTableMatchResult.ResultWin || result.Result > PlayTableMatchResult.ResultDraw)
            {
                CoopPlugin.Log.LogWarning($"play-table result rejected: match={result.MatchId} invalid result={result.Result}");
                return;
            }
            if (result.Gifts == null)
            {
                CoopPlugin.Log.LogWarning($"play-table result rejected: match={result.MatchId} gifts payload is null");
                return;
            }
            _tableMatches.PruneExpiredHostReservations();
            if (!_tableMatches.TryGetHostReservationByMatch(result.MatchId, out int reservationOwner, out int reservationTableKey)
                || reservationOwner != sender
                || !_tableMatches.TryGetHostMatch(reservationTableKey, out var reservation)
                || reservation.TableKey != reservationTableKey)
            {
                CoopPlugin.Log.LogWarning($"play-table result rejected: match={result.MatchId} sender={sender} not owner");
                return;
            }
            EnsurePlayTableNonceSession();
            if (_playTableResultNonces.TryGetValue(result.MatchId, out int lastNonce)
                && result.Nonce <= lastNonce)
            {
                CoopPlugin.Log.LogWarning($"play-table result ignored: match={result.MatchId} nonce={result.Nonce} last={lastNonce}");
                return;
            }
            if (result.DurationSeconds <= 0f)
            {
                CoopPlugin.Log.LogWarning($"play-table result rejected: match={result.MatchId} invalid duration={result.DurationSeconds}");
                return;
            }
            if (result.DurationSeconds > 7200f)
            {
                CoopPlugin.Log.LogWarning($"play-table result duration clamped: match={result.MatchId} duration={result.DurationSeconds}");
                result.DurationSeconds = 7200f;
            }
            if (result.Gifts != null && result.Gifts.Count > 10)
            {
                CoopPlugin.Log.LogWarning($"play-table result rejected: match={result.MatchId} gifts={result.Gifts.Count}");
                return;
            }
            _playTableResultNonces[result.MatchId] = result.Nonce;
            CoopPlugin.Log.LogInfo($"play-table result accepted: match={result.MatchId} result={result.Result} duration={result.DurationSeconds} gifts={result.Gifts?.Count ?? 0}");
            _tableMatches.ApplyResult(result, sender);
            _playTableResultNonces.Remove(result.MatchId);
        }

        private bool HostPlayingAt(InteractablePlayTable table)
        {
            var manager = SceneRef<PlayCardGameManager>.Get();
            var game = manager != null ? manager.m_PlayTableGame : null;
            return game != null && FiPlayTableMode != null && (bool)FiPlayTableMode.GetValue(game)
                && ReferenceEquals(FiCurrentPlayTable?.GetValue(game), table);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private void EnsurePlayTableNonceSession()
        {
            int generation = SessionGeneration;
            if (_playTableNonceGeneration != generation)
            {
                _playTableResultNonces.Clear();
                _playTableNonceGeneration = generation;
            }
        }
    }
}
