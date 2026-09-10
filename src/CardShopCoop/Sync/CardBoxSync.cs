using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors graded-card RETURN boxes (InteractablePackagingBox_Card) between host
    /// and client. Grading matures host-side only (GradingSync blocks the client's
    /// RestockManager.OnDayStarted), and the result box lands in RestockManager's
    /// m_CardPackagingBoxList - a separate list from the item boxes BoxSync mirrors -
    /// so without this module the joiner never sees his graded cards come back.
    ///
    /// Architecture is BoxSync's: the host list is the truth, broadcast by index every
    /// 1.5s (hash-gated, slow heal); the client reconciles its live list to match,
    /// spawning through RestockManager.SpawnPackageBoxCard - which takes the exact
    /// CardData list and never re-rolls grades (rolls happen earlier, in OnDayStarted) -
    /// and despawning through the box's own OnDestroyed. Box contents are immutable
    /// after spawn, so the client only ever reports carried transitions and positions.
    ///
    /// Joiner collect: the vanilla InteractablePackagingBox_Card.OnPressOpenBox hands
    /// the card3ds to the player (AddHoldCard) and the cards reach the binder later,
    /// when the held cards are stored (InteractionPlayerController's store path calls
    /// CPlayerData.AddCard per card). Letting that run locally would AddCard on the
    /// joiner AND mirror through CardDelta AND leave the host's real box alive - a
    /// guaranteed duplicate. So the joiner's open is blocked BEFORE anything is handed
    /// over and forwarded as a collect op; the host AddCards each stored card itself
    /// (grade-10 report/achievement bookkeeping included, replicating OnPressOpenBox),
    /// which the existing CardDelta mirror carries into BOTH binders, then despawns
    /// its real box; the mirror follows on the next snapshot. Reveal choice: the
    /// vanilla reveal is hold-card mode, and hold-card mode's only exit paths call
    /// AddCard - there is no read-only way to invoke it - so the joiner gets the box
    /// "Open" animation + SFX locally plus a register toast instead.
    /// </summary>
    public class CardBoxSync
    {
        public struct Entry
        {
            public int Id;
            public List<CardData> Cards; // immutable identity of the box
            public bool Carried;         // in someone's hands: position is transient
            public Vector3 Pos;
            public float Yaw;
            public bool InFlight;
            public bool Moving;
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
            public bool Owned;
            public int OwnerId;
        }

        // Use a widened snapshot count so a large backlog cannot make the client
        // destroy valid boxes merely because they are past a small prefix cap.
        private const int MaxBoxes = 255;
        private const int MaxCards = 16; // vanilla sets carry up to 8 cards

        /// <summary>The live module instance, for the static Harmony patches.</summary>
        public static CardBoxSync Instance;

        /// <summary>Set by CoopCore: is this box currently in the LOCAL player's hands?
        /// (InteractionPlayerController.m_CurrentHoldingBoxCard / m_CurrentHoldingBox)</summary>
        public static Func<InteractablePackagingBox_Card, bool> IsLocallyCarried = _ => false;

        /// <summary>Set by CoopCore: client -> host op (MsgType.CardBoxOp).</summary>
        public Action<INetMessage> SendOp;

        /// <summary>Set by CoopCore: host -> clients state (MsgType.CardBoxState).</summary>
        public Action<INetMessage> BroadcastState;

        /// <summary>Set by CoopCore: host -> requesting client collect result.</summary>
        public Action<int, INetMessage> SendToClient;

        /// <summary>True while sync code itself destroys/spawns boxes, so the
        /// OnDestroyed patch doesn't mistake reconciliation for player action.</summary>
        public static bool ApplyingRemote;

        // op kinds on the CardBoxOp wire
        private const byte OpReport = 0;  // carried flags + positions, index-aligned
        private const byte OpCollect = 1; // joiner opened a box: index + identity hash
        private const byte OpRemoved = 2; // joiner's local copy died outside sync

        // m_StoredCardList is private; needed to hide the card3d UI followers along
        // with the box (Card3dUIGroup only tracks its card in LateUpdate, so a plain
        // SetActive(false) on the box would strand the card faces in mid-air)
        private static readonly FieldInfo FiStoredCards =
            AccessTools.Field(typeof(InteractablePackagingBox_Card), "m_StoredCardList");

        private readonly List<Entry> _lastApplied = new List<Entry>(); // client: host truth
        private readonly HashSet<int> _carriedLastTick = new HashSet<int>();
        private readonly HashSet<int> _remoteCarried = new HashSet<int>();   // host: client-held boxes
        private readonly Dictionary<int, double> _recentlyReleased = new Dictionary<int, double>(); // client: ignore stale carried echoes
        private readonly Dictionary<int, double> _locallyTouched = new Dictionary<int, double>();   // client: my recent moves beat stale echoes
        private readonly Dictionary<int, double> _recentlyCollected = new Dictionary<int, double>(); // client: cardsHash -> time; stale pre-collect snapshots must not resurrect the box
        private readonly Dictionary<InteractablePackagingBox_Card, int> _hostIds = new Dictionary<InteractablePackagingBox_Card, int>();
        private readonly HashSet<int> _snapshotErrors = new HashSet<int>();
        private int _nextHostId = 1;
        private float _timer;
        private int _lastHostHash;
        private float _hostHeal;
        private RestockManager _rm;
        private InteractionPlayerController _ipc; // NEVER CSingleton<>.Instance: it fabricates
                                                  // a fake manager if touched while no real one
                                                  // exists (see WorldSync.ResolveShelfManager)
        private Transform _spawnAnchor; // reusable Transform for SpawnPackageBoxCard

        public CardBoxSync()
        {
            Instance = this;
        }

        public void Reset()
        {
            _lastApplied.Clear();
            _carriedLastTick.Clear();
            _remoteCarried.Clear();
            _recentlyReleased.Clear();
            _locallyTouched.Clear();
            _recentlyCollected.Clear();
            _hostIds.Clear();
            _snapshotErrors.Clear();
            _nextHostId = 1;
            _timer = -8.4f; // staggered phase vs the other snapshot engines
            _lastHostHash = 0;
            _hostHeal = 0f;
            _rm = null;
            _ipc = null;
        }

        public void ForceNextTick()
        {
            _timer = 1.5f;
            _lastHostHash = 0;
        }

        public void ForceResend()
        {
            _lastHostHash = 0;
            _hostHeal = 999f; // beats the hash gate even if the real hash is 0
        }

        /// <summary>Host: a peer disconnected - release any card box still marked
        /// client-carried, or it stays in its carried state forever (the set-down
        /// report is never coming; a rejoining guest starts with an empty carry set).
        /// Reuses the module's own un-carry apply at the box's current pose, exactly
        /// what a normal set-down report would have done. Releases everything (the set
        /// isn't keyed by connection); a surviving guest still carrying re-asserts its
        /// carry on its next ~0.5s report.</summary>
        public void HostReleaseRemoteCarried()
        {
            if (_remoteCarried.Count == 0)
                return;
            // NEVER touch LiveBoxes() (a fabricating CSingleton accessor) when no real
            // RestockManager exists - a disconnect drained while the host is mid world-
            // load would otherwise mint the fake-manager landmine. Rm() resolves via
            // FindObjectOfType, which returns null without fabricating.
            if (Rm() == null)
            {
                _remoteCarried.Clear();
                return;
            }
            var boxes = LiveBoxes();
            int released = 0;
            foreach (int i in _remoteCarried)
            {
                if (i < 0 || i >= boxes.Count || boxes[i] == null)
                    continue;
                var box = boxes[i];
                ApplyToBox(box, new Entry
                {
                    Cards = null,
                    Carried = false,
                    Pos = BoxSync.PhysicsPosition(box),
                    Yaw = BoxSync.PhysicsRotation(box).eulerAngles.y,
                });
                released++;
            }
            _remoteCarried.Clear();
            if (released > 0)
            {
                CoopPlugin.Log.LogInfo($"CardBoxSync host: released {released} client-carried box(es) after a disconnect");
                ForceResend();
            }
        }

        private RestockManager Rm()
        {
            if (_rm == null)
                _rm = UnityEngine.Object.FindObjectOfType<RestockManager>();
            return _rm;
        }

        private InteractionPlayerController Ipc()
        {
            if (_ipc == null)
                _ipc = UnityEngine.Object.FindObjectOfType<InteractionPlayerController>();
            return _ipc;
        }

        private static List<InteractablePackagingBox_Card> LiveBoxes()
        {
            return RestockManager.GetCardPackagingBoxList();
        }

        private static bool InGameLevel()
        {
            var gm = CSingleton<CGameManager>.Instance;
            return gm != null && gm.m_IsGameLevel;
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // The joiner's collect: block BEFORE the card3ds are handed to the player
            // (the hold-card store path would AddCard locally = duplication).
            Try(h, typeof(InteractablePackagingBox_Card), "OnPressOpenBox",
                prefix: new HarmonyMethod(typeof(CardBoxSync), nameof(OpenBoxPrefix)));

            // OnDestroyed IS an override on InteractablePackagingBox_Card (it clears
            // its card3ds and calls RestockManager.RemoveCardPackageBox), so patching
            // it here catches ONLY card boxes - item boxes keep BoxSync's own patch.
            Try(h, typeof(InteractablePackagingBox_Card), "OnDestroyed",
                prefix: new HarmonyMethod(typeof(CardBoxSync), nameof(DestroyedPrefix)));
        }

        public static bool OpenBoxPrefix(InteractablePackagingBox_Card __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            try
            {
                Instance?.ClientCollect(__instance);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync collect: " + e.Message); }
            return false; // never vanilla on the client (see class comment)
        }

        public static bool DestroyedPrefix(InteractablePackagingBox_Card __instance)
        {
            // world-(re)load cleanup destroys are not player actions
            if (!ApplyingRemote && !CoopCore.ClientReloading)
                Instance?.OnLocalDestroyed(__instance);
            return true;
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

        // ---------------- host ----------------

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame || Rm() == null)
                return;
            _timer += dt;
            float cadence = 1.5f;
            if (_timer < cadence)
                return;
            _timer = 0f;
            try
            {
                var boxes = LiveBoxes();
                var staleIds = new List<InteractablePackagingBox_Card>();
                foreach (var pair in _hostIds)
                    if (!boxes.Contains(pair.Key))
                        staleIds.Add(pair.Key);
                for (int i = 0; i < staleIds.Count; i++)
                    _hostIds.Remove(staleIds[i]);
                var list = new List<Entry>(Mathf.Min(boxes.Count, MaxBoxes));
                bool sawError = false;
                for (int i = 0; i < boxes.Count && list.Count < MaxBoxes; i++)
                {
                    if (boxes[i] == null)
                        continue;
                    try
                    {
                        var e = Snapshot(boxes[i]);
                        e.Id = HostId(boxes[i]);
                        if (_remoteCarried.Contains(i))
                            e.Carried = true; // a client holds it
                        list.Add(e);
                    }
                    catch (Exception e)
                    {
                        sawError = true;
                        if (_snapshotErrors.Add(i))
                            CoopPlugin.Log.LogWarning($"CardBoxSync snapshot box {i}: {e.Message}");
                    }
                }
                if (sawError)
                    return; // do not broadcast an incomplete authoritative list
                int hash = 17;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    hash = hash * 31 + HashCards(e.Cards);
                    hash = hash * 31 + (e.Carried ? 1 : 0);
                    hash = hash * 31 + (e.InFlight ? 1 : 0) + (e.Moving ? 2 : 0);
                    hash = hash * 31 + (int)(e.Pos.x * 8f);
                    hash = hash * 31 + (int)(e.Pos.z * 8f);
                }
                _hostHeal += 1.5f;
                if (hash == _lastHostHash && _hostHeal < 10f)
                    return;
                _lastHostHash = hash;
                _hostHeal = 0f;
                var snap = list;
                var entries = new List<CardBoxEntry>(snap.Count);
                for (int i = 0; i < snap.Count; i++)
                    entries.Add(ToWire(snap[i]));
                BroadcastState?.Invoke(new CardBoxStateMessage { Entries = entries });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync host: " + e.Message); }
        }

        /// <summary>Host: dispatch a client op (report / collect / removed).</summary>
        public void HostApplyOp(CardBoxOpMessage message, int connId)
        {
            if (CoopCore.Role != CoopRole.Host)
                return;
            byte kind = message.Op;
            switch (kind)
            {
                case OpReport:
                    HostApplyReport(message);
                    break;
                case OpCollect:
                    HostApplyCollect(message, connId);
                    break;
                case OpRemoved:
                    HostApplyRemoved(message, connId);
                    break;
                default:
                    CoopPlugin.Log.LogWarning($"CardBoxSync: unknown op {kind}");
                    break;
            }
        }

        private void HostApplyReport(CardBoxOpMessage message)
        {
            var report = message.Report;
            int n = Mathf.Min(report.Count, MaxBoxes);
            var boxes = LiveBoxes();
            for (int i = 0; i < n; i++)
            {
                var entry = report[i];
                int cardCount = entry.CardCount;
                bool carried = entry.Carried;
                var pos = entry.Position;
                float yaw = entry.Yaw;
                if (i >= boxes.Count || boxes[i] == null)
                    continue;
                var box = boxes[i];
                // identity must match: indices may have shifted between snapshot and report
                if (SafeCards(box).Count != cardCount)
                    continue;
                if (IsLocallyCarried(box))
                    continue; // never stomp a box in the host's hands
                if (carried)
                    _remoteCarried.Add(i);
                else
                    _remoteCarried.Remove(i);
                ApplyToBox(box, new Entry { Cards = null, Carried = carried, Pos = pos, Yaw = yaw, InFlight = entry.InFlight, Moving = entry.Moving, Velocity = entry.Velocity, AngularVelocity = entry.AngularVelocity });
            }
        }

        /// <summary>Host: the joiner opened a graded-returns box. Replicates the
        /// collect side of InteractablePackagingBox_Card.OnPressOpenBox WITHOUT the
        /// hold-card step (cards can't be put in the host player's hands): AddCard
        /// per stored card - AddCard routes graded cards into the graded collection
        /// itself, and the CardDelta mirror carries every AddCard to the joiner, so
        /// BOTH binders receive them - plus the grade-10 report counter and the two
        /// achievement checks, then the box despawns and the mirror follows.</summary>
        private void HostApplyCollect(CardBoxOpMessage message, int connId)
        {
            int boxId = message.BoxId;
            int cardCount = message.CardCount;
            int cardsHash = message.CardsHash;

            var box = FindBoxById(boxId);
            if (box == null)
            {
                var live = LiveBoxes();
                var identities = new List<string>(live.Count);
                for (int i = 0; i < live.Count; i++)
                {
                    var candidate = live[i];
                    if (candidate == null)
                        continue;
                    var candidateCards = SafeCards(candidate);
                    identities.Add(HostId(candidate) + "@" + i + ":" + candidateCards.Count + "/" + HashCards(candidateCards));
                }
                CoopPlugin.Log.LogWarning("CardBoxSync: collect for unknown/mismatched box - ignored"
                    + " (requested id " + boxId + ":" + cardCount + "/" + cardsHash
                    + ", live [" + string.Join(",", identities.ToArray()) + "])");
                SendCollectRejected(connId, boxId, cardCount, cardsHash);
                ForceResend();
                return; // explicit rejection lets the client restore its mirror immediately
            }
            if (IsLocallyCarried(box))
                return; // host is holding it: let him open it himself

            try
            {
                var cards = SafeCards(box);
                for (int i = 0; i < cards.Count; i++)
                {
                    if (cards[i] == null)
                        continue;
                    // Wire-derived card: AddCard would mis-index (vanilla) or throw on
                    // CardCountList[-1] (EPL) for content this PC doesn't have. The box is
                    // destroyed below either way, so a skipped card is genuinely gone on this
                    // PC - it must at least say so.
                    if (!CoopCore.CardSetInstalledHere(cards[i]))
                    {
                        CoopCore.WarnRefusedCard(cards[i], "card-box");
                        continue;
                    }
                    // A grading-overhaul result carries an encoded grade. Register the
                    // certificate before AddCard so GO's anti-cheat prefix accepts the
                    // host-matured card instead of rewriting/rejecting it as an unknown
                    // or duplicate certificate.
                    if (cards[i].cardGrade > 10 && Util.GradingInterop.Present)
                        Util.GradingInterop.Remember(cards[i]);
                    CPlayerData.AddCard(cards[i], 1); // mirrored by CardDelta
                    if (Util.GradingInterop.Actual(cards[i].cardGrade) == 10)
                        CPlayerData.m_GameReportDataCollectPermanent.gemMintCardObtained++;
                }
                AchievementManager.OnCheckGemMintCardCount(CPlayerData.m_GameReportDataCollectPermanent.gemMintCardObtained);
                AchievementManager.OnCheckCollectedGradedCardSet();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync collect apply: " + e.Message); }

            ApplyingRemote = true;
            try
            {
                box.OnDestroyed();
            } // destroys its card3ds + RemoveCardPackageBox
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync collect despawn: " + e.Message); }
            finally { ApplyingRemote = false; }
            _remoteCarried.Clear(); // indices shifted; holds re-assert within a tick
            ForceResend();          // the joiner's mirror updates on the next tick
        }

        /// <summary>Tell the client that its optimistic local despawn was rejected. The
        /// following forced snapshot contains the still-live host box and recreates it.</summary>
        private void SendCollectRejected(int connId, int boxId, int cardCount, int cardsHash)
        {
            SendToClient?.Invoke(connId, new CardBoxCollectResultMessage
            {
                BoxId = boxId,
                CardCount = (byte)cardCount,
                CardsHash = cardsHash,
            });
        }

        /// <summary>Client: undo stale-snapshot protection for a collect the host did not
        /// accept. Otherwise even a forced authoritative snapshot can be ignored as the
        /// pre-collect echo.</summary>
        public void ClientApplyCollectRejected(CardBoxCollectResultMessage message)
        {
            int boxId = message.BoxId;
            int cardCount = message.CardCount;
            int cardsHash = message.CardsHash;
            _recentlyCollected.Remove(cardsHash);
            CoopPlugin.Log.LogWarning("CardBoxSync: host rejected collect; restoring the authoritative box"
                + " (id " + boxId + ", " + cardCount + "/" + cardsHash + ")");
        }

        private void HostApplyRemoved(CardBoxOpMessage message, int connId)
        {
            int boxId = message.BoxId;
            int cardCount = message.CardCount;
            int cardsHash = message.CardsHash;
            // shared budget with item/furn boxes: a reloading client's world-teardown
            // echoes ALL THREE box lists as removals in one burst
            if (BoxSync.RemovalFlooded(connId, "card-box"))
                return;
            var box = FindBoxById(boxId);
            if (box == null || IsLocallyCarried(box))
                return;
            ApplyingRemote = true;
            try
            {
                box.OnDestroyed();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync removal: " + e.Message); }
            finally { ApplyingRemote = false; }
            _remoteCarried.Clear();
            ForceResend();
        }

        /// <summary>Resolve a box by index, falling back to an identity scan - the
        /// index may have shifted between the client's snapshot and his op.</summary>
        private static InteractablePackagingBox_Card FindBox(int index, int cardCount, int cardsHash)
        {
            var boxes = LiveBoxes();
            if (index >= 0 && index < boxes.Count && boxes[index] != null)
            {
                var cards = SafeCards(boxes[index]);
                if (cards.Count == cardCount && HashCards(cards) == cardsHash)
                    return boxes[index];
            }
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i] == null)
                    continue;
                var cards = SafeCards(boxes[i]);
                if (cards.Count == cardCount && HashCards(cards) == cardsHash)
                    return boxes[i];
            }
            return null;
        }

        private InteractablePackagingBox_Card FindBoxById(int id)
        {
            if (id <= 0)
                return null;
            var boxes = LiveBoxes();
            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box != null && HostId(box) == id)
                    return box;
            }
            return null;
        }

        private int HostId(InteractablePackagingBox_Card box)
        {
            if (box == null)
                return 0;
            if (_hostIds.TryGetValue(box, out int id))
                return id;
            id = _nextHostId++;
            if (id <= 0)
                id = _nextHostId++;
            _hostIds[box] = id;
            return id;
        }

        // ---------------- client ----------------

        /// <summary>Client: reconcile the live card-box population to the host's
        /// snapshot. Spawns go through RestockManager.SpawnPackageBoxCard with the
        /// EXACT broadcast CardData list (UpdateCardData stores it as-is; grades were
        /// rolled host-side in OnDayStarted, never here).</summary>
        public void ClientApplyState(CardBoxStateMessage message)
        {
            var entries = message.Entries;
            var hostList = new List<Entry>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
                hostList.Add(ToEntry(entries[i]));
            ApplyingRemote = true;
            try
            {
                ClientApplyInner(hostList);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync apply: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(List<Entry> hostList)
        {
            if (Rm() == null)
                return;
            var boxes = LiveBoxes();
            double now = Time.realtimeSinceStartupAsDouble;

            // stale-snapshot guard: a snapshot generated BEFORE the host processed my
            // collect still contains the box I just despawned locally - spawning it
            // back for one tick would offer a ghost double-collect. Skip the whole
            // pass; the post-collect snapshot follows within 1.5s.
            for (int i = 0; i < hostList.Count; i++)
            {
                bool present = i < boxes.Count && boxes[i] != null
                    && SameCards(SafeCards(boxes[i]), hostList[i].Cards);
                if (!present && _recentlyCollected.TryGetValue(HashCards(hostList[i].Cards), out double tc)
                    && now - tc < 6.0)
                    return;
            }
            // expire old collect markers so a genuinely re-appearing box can heal
            if (_recentlyCollected.Count > 0)
            {
                List<int> dead = null;
                foreach (var kv in _recentlyCollected)
                    if (now - kv.Value > 12.0)
                        (dead ?? (dead = new List<int>())).Add(kv.Key);
                if (dead != null)
                    foreach (int k in dead)
                        _recentlyCollected.Remove(k);
            }

            // A snapshot at the wire ceiling may be truncated; its absent tail is
            // unknown and must not be destroyed. Only a complete snapshot authorizes
            // population retirement.
            if (hostList.Count < MaxBoxes)
            {
                for (int i = boxes.Count - 1; i >= hostList.Count; i--)
                {
                    try
                    {
                        if (boxes[i] != null)
                            boxes[i].OnDestroyed();
                    }
                    catch { }
                }
            }
            // grow / fix / update
            for (int i = 0; i < hostList.Count; i++)
            {
                var want = hostList[i];
                InteractablePackagingBox_Card box = i < boxes.Count ? boxes[i] : null;
                if (box != null && !SameCards(SafeCards(box), want.Cards))
                {
                    try
                    {
                        box.OnDestroyed();
                    }
                    catch { }
                    box = null;
                    boxes = LiveBoxes(); // list mutated
                }
                if (box == null)
                {
                    try
                    {
                        // UpdateCardData KEEPS the list reference (and prunes it), so
                        // hand it a private copy, never the applied-state one
                        box = RestockManager.SpawnPackageBoxCard(
                            new List<CardData>(want.Cards), SpawnAnchor(want.Pos, want.Yaw));
                        boxes = LiveBoxes();
                    }
                    catch (Exception e)
                    {
                        CoopPlugin.Log.LogWarning("CardBoxSync spawn: " + e.Message);
                        continue;
                    }
                }
                // a box in MY hands is mine until I put it down; a box in the HOST's
                // hands has a transient position we don't copy
                if (IsLocallyCarried(box) || box.GetIsMovingObject())
                    continue;
                // A thrower may receive the final carried snapshot before the
                // host receives the release report. Preserve the local flight.
                if (!BoxSync.PhysicsSettled(box))
                {
                    SetBoxVisible(box, true);
                    continue;
                }
                // a stale "carried" echo about a box I JUST released must not hide it
                if (want.Carried && _recentlyReleased.TryGetValue(i, out double t) && now - t < 6.0)
                    continue;
                // my own recent moves win over stale echoes; my report reaches the
                // host and the next echo agrees
                if (_locallyTouched.TryGetValue(i, out double touched) && now - touched < 6.0)
                    continue;
                ApplyToBox(box, want);
            }
            // remember the applied truth for local-change detection
            _lastApplied.Clear();
            _lastApplied.AddRange(hostList);
        }

        /// <summary>Client: detect the local player's carried transitions and box
        /// moves (contents are immutable client-side - collect is forwarded, never
        /// applied locally) and report them to the host.</summary>
        public void ClientTick(float dt, bool inGame)
        {
            if (!inGame || Rm() == null || _lastApplied.Count == 0)
                return;
            _timer += dt;
            if (_timer < 1.5f)
                return;
            _timer -= 1.5f;
            try
            {
                var boxes = LiveBoxes();
                bool changed = false;
                var list = new List<Entry>(_lastApplied.Count);
                for (int i = 0; i < _lastApplied.Count; i++)
                {
                    if (i < boxes.Count && boxes[i] != null)
                    {
                        if (BoxSync.IsRemoteMotion(boxes[i]))
                            continue; // interpolation is host reconciliation, not local intent
                        // while I'M carrying it: tell the host (so his copy hides) but
                        // keep reporting the last settled position
                        if (IsLocallyCarried(boxes[i]))
                        {
                            BoxSync.CancelRemoteMotion(boxes[i]);
                            var held = _lastApplied[i];
                            held.Carried = true;
                            if (_carriedLastTick.Add(i))
                                changed = true; // pickup transition
                            list.Add(held);
                            continue;
                        }
                        if (_carriedLastTick.Remove(i))
                        {
                            changed = true; // set-down transition
                            _recentlyReleased[i] = Time.realtimeSinceStartupAsDouble;
                        }
                        var nowSnap = Snapshot(boxes[i]);
                        var last = _lastApplied[i];
                        if ((nowSnap.Pos - last.Pos).sqrMagnitude > 0.01f
                            || Mathf.Abs(Mathf.DeltaAngle(nowSnap.Yaw, last.Yaw)) > 3f)
                        {
                            changed = true;
                            _locallyTouched[i] = Time.realtimeSinceStartupAsDouble;
                        }
                        nowSnap.Cards = last.Cards; // identity is the applied truth
                        list.Add(nowSnap);
                    }
                    else
                    {
                        // list shrank without OnDestroyed telling us - stop and let
                        // the next host snapshot re-align
                        break;
                    }
                }
                if (changed && SendOp != null)
                {
                    var report = new List<CardBoxReportEntry>(Mathf.Min(list.Count, MaxBoxes));
                    for (int i = 0; i < list.Count && i < MaxBoxes; i++)
                    {
                        var e = list[i];
                        report.Add(new CardBoxReportEntry
                        {
                            CardCount = (byte)Mathf.Min(e.Cards != null ? e.Cards.Count : 0, MaxCards),
                            Carried = e.Carried,
                            Position = e.Pos,
                            Yaw = e.Yaw,
                            InFlight = e.InFlight,
                            Moving = e.Moving,
                            Velocity = e.Velocity,
                            AngularVelocity = e.AngularVelocity,
                            Owned = e.Owned,
                            OwnerId = e.OwnerId,
                        });
                    }
                    SendOp(new CardBoxOpMessage { Op = OpReport, Report = report });
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync client: " + e.Message); }
        }

        /// <summary>Client: the joiner pressed open on a mirrored box. Forward the
        /// collect (identity-hashed, so the host never collects the wrong box), keep
        /// the open moment locally (anim + SFX + toast), and retire the local mirror
        /// on the vanilla 0.85s delay.</summary>
        private void ClientCollect(InteractablePackagingBox_Card box)
        {
            int idx = LiveBoxes().IndexOf(box);
            if (idx < 0)
                return;
            var cards = SafeCards(box);

            if (SendOp == null)
            {
                CoopPlugin.Log.LogWarning("CardBoxSync: no host link, open ignored");
                return;
            }
            int hash = HashCards(cards);
            int count = cards.Count;
            int boxId = idx < _lastApplied.Count ? _lastApplied[idx].Id : 0;
            SendOp(new CardBoxOpMessage
            {
                Op = OpCollect,
                BoxId = boxId,
                CardCount = (byte)Mathf.Min(count, MaxCards),
                CardsHash = hash,
            });
            _recentlyCollected[hash] = Time.realtimeSinceStartupAsDouble;

            // local index bookkeeping shifts once the mirror despawns
            if (idx < _lastApplied.Count)
                _lastApplied.RemoveAt(idx);
            _carriedLastTick.Clear();   // index-keyed trackers all shifted;
            _locallyTouched.Clear();    // they re-establish within a tick
            _recentlyReleased.Clear();

            // the reveal moment, minus the hold-card handout (see class comment)
            try
            {
                Ipc()?.OnExitHoldBoxMode();
            }
            catch { }
            try
            {
                box.m_BoxAnim.Play("Open");
            }
            catch { }
            try
            {
                SoundManager.PlayAudio("SFX_BoxOpen", 0.5f);
            }
            catch { }
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.RegisterLine = "graded cards collected - check the binder";
                CoopCore.Instance.RegisterLineTimer = 4f;
            }
            try
            {
                box.StartCoroutine(CollectDespawn(box));
            }
            catch { DespawnNow(box); }
        }

        private static IEnumerator CollectDespawn(InteractablePackagingBox_Card box)
        {
            yield return new WaitForSeconds(0.85f); // vanilla DelayResetOpenBox timing
            DespawnNow(box);
        }

        private static void DespawnNow(InteractablePackagingBox_Card box)
        {
            if (box == null)
                return;
            ApplyingRemote = true;
            try
            {
                box.gameObject.SetActive(false);
                box.OnDestroyed();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync local despawn: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        /// <summary>A card box died to LOCAL gameplay, not reconciliation. On the host
        /// the broadcast handles it (index-keyed client holds just reset); the client
        /// tells the host so the real box dies too and no echo resurrects it.</summary>
        private void OnLocalDestroyed(InteractablePackagingBox_Card box)
        {
            if (!InGameLevel())
                return;
            if (CoopCore.Role == CoopRole.Host)
            {
                _remoteCarried.Clear(); // indices shifted; holds re-assert within a tick
                return;
            }
            if (CoopCore.Role != CoopRole.Client)
                return;
            int idx = LiveBoxes().IndexOf(box);
            if (idx < 0)
                return;
            var cards = SafeCards(box);
            int hash = HashCards(cards);
            int count = cards.Count;
            int boxId = idx < _lastApplied.Count ? _lastApplied[idx].Id : 0;
            if (idx < _lastApplied.Count)
                _lastApplied.RemoveAt(idx);
            _carriedLastTick.Clear();
            _locallyTouched.Clear();
            _recentlyReleased.Clear();
            _recentlyCollected[hash] = Time.realtimeSinceStartupAsDouble;
            SendOp?.Invoke(new CardBoxOpMessage
            {
                Op = OpRemoved,
                BoxId = boxId,
                CardCount = (byte)Mathf.Min(count, MaxCards),
                CardsHash = hash,
            });
        }

        // ---------------- shared apply ----------------

        private static Entry Snapshot(InteractablePackagingBox_Card box)
        {
            return new Entry
            {
                Id = 0,
                Cards = SafeCards(box),
                Carried = IsLocallyCarried(box),
                InFlight = !IsLocallyCarried(box) && !box.GetIsMovingObject() && box.m_Rigidbody != null && !box.m_Rigidbody.IsSleeping(),
                Moving = box.GetIsMovingObject(),
                Velocity = box.m_Rigidbody != null ? box.m_Rigidbody.velocity : Vector3.zero,
                AngularVelocity = box.m_Rigidbody != null ? box.m_Rigidbody.angularVelocity : Vector3.zero,
                Owned = IsLocallyCarried(box) || box.GetIsMovingObject(),
                OwnerId = 0,
                Pos = BoxSync.PhysicsPosition(box),
                Yaw = BoxSync.PhysicsRotation(box).eulerAngles.y,
            };
        }

        private static List<CardData> SafeCards(InteractablePackagingBox_Card box)
        {
            try
            {
                return box.GetCardDataList() ?? EmptyCards;
            }
            catch { return EmptyCards; }
        }

        private static readonly List<CardData> EmptyCards = new List<CardData>();

        /// <summary>Apply carried visibility + position. Entry.Cards is ignored here
        /// (contents are spawn-time identity, never edited in place).</summary>
        private static void ApplyToBox(InteractablePackagingBox_Card box, Entry want)
        {
            try
            {
                if (want.InFlight || want.Moving)
                {
                    SetBoxVisible(box, true);
                    if (want.Moving)
                        box.SetPhysicsEnabled(false);
                    else
                    {
                        box.SetPhysicsEnabled(true);
                        BoxSync.ApplyPhysicsPose(box, want.Pos, want.Yaw);
                        if (box.m_Rigidbody != null)
                        {
                            box.m_Rigidbody.velocity = want.Velocity;
                            box.m_Rigidbody.angularVelocity = want.AngularVelocity;
                            box.m_Rigidbody.WakeUp();
                        }
                    }
                    if (want.Moving)
                        BoxSync.ApplyPhysicsPose(box, want.Pos, want.Yaw);
                    return;
                }
                // someone (remote) is carrying it: their avatar shows the box in hand,
                // so the world copy disappears until it's set down - including the
                // Card3dUIGroup followers, which don't live under the box transform
                // (they track their card3d in LateUpdate and would freeze in mid-air)
                if (want.Carried)
                {
                    SetBoxVisible(box, false);
                    return;
                }
                SetBoxVisible(box, true);
                var currentPos = BoxSync.PhysicsPosition(box);
                var currentYaw = BoxSync.PhysicsRotation(box).eulerAngles.y;
                if ((currentPos - want.Pos).sqrMagnitude > 0.01f
                    || Mathf.Abs(Mathf.DeltaAngle(currentYaw, want.Yaw)) > 3f)
                {
                    // card boxes have no price tag group (SpawnPriceTag is overridden
                    // empty), so a plain transform move carries everything: the stored
                    // card3ds are parented under m_StoredCardPosListGrp inside the box
                    BoxSync.ScheduleRemoteMotion(box, want.Pos, want.Yaw);
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxSync apply: " + e.Message); }
        }

        private static void SetBoxVisible(InteractablePackagingBox_Card box, bool visible)
        {
            if (box.gameObject.activeSelf == visible)
                return;
            box.gameObject.SetActive(visible);
            try
            {
                if (FiStoredCards?.GetValue(box) is List<InteractableCard3d> card3ds)
                {
                    for (int i = 0; i < card3ds.Count; i++)
                    {
                        var c = card3ds[i];
                        if (c != null && c.m_Card3dUI != null)
                            c.m_Card3dUI.gameObject.SetActive(visible);
                    }
                }
            }
            catch { }
        }

        private Transform SpawnAnchor(Vector3 pos, float yaw)
        {
            if (_spawnAnchor == null)
                _spawnAnchor = new GameObject("CoopCardBoxSpawnAnchor").transform;
            _spawnAnchor.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            return _spawnAnchor;
        }

        // ---------------- wire / hash ----------------

        private static Entry ToEntry(CardBoxEntry e)
        {
            return new Entry
            {
                Id = e.Id,
                Cards = e.Cards,
                Carried = e.Carried,
                InFlight = e.InFlight,
                Moving = e.Moving,
                Velocity = e.Velocity,
                AngularVelocity = e.AngularVelocity,
                Owned = e.Owned,
                OwnerId = e.OwnerId,
                Pos = e.Position,
                Yaw = e.Yaw,
            };
        }

        private static CardBoxEntry ToWire(Entry e)
        {
            return new CardBoxEntry
            {
                Id = e.Id,
                Cards = e.Cards,
                Position = e.Pos,
                Yaw = e.Yaw,
                Carried = e.Carried,
                InFlight = e.InFlight,
                Moving = e.Moving,
                Velocity = e.Velocity,
                AngularVelocity = e.AngularVelocity,
                Owned = e.Owned,
                OwnerId = e.OwnerId,
            };
        }

        /// <summary>Identity hash of a box's contents. Deliberately excludes isNew and
        /// gradedCardIndex (volatile bookkeeping the two sides may disagree on).
        /// Grading Overhaul temporarily decodes an encoded cardGrade while card UI code
        /// runs, so identity must use its registry-backed encoded value rather than the
        /// transient visible 1-10 grade.</summary>
        private static int HashCards(List<CardData> cards)
        {
            int h = 17;
            if (cards == null)
                return h;
            h = h * 31 + cards.Count;
            for (int i = 0; i < cards.Count && i < MaxCards; i++)
            {
                var c = cards[i];
                if (c == null)
                    continue;
                h = h * 31 + (int)c.monsterType;
                h = h * 31 + (int)c.expansionType;
                h = h * 31 + (int)c.borderType;
                h = h * 31 + ((c.isFoil ? 1 : 0) | (c.isDestiny ? 2 : 0) | (c.isChampionCard ? 4 : 0));
                h = h * 31 + CanonicalGrade(c);
            }
            return h;
        }

        /// <summary>Return the stable wire/identity grade. Grading Overhaul's card UI can
        /// temporarily expose the actual 1-10 grade through CardData.cardGrade even though
        /// the certificate-bearing encoded value remains in its registry.</summary>
        private static int CanonicalGrade(CardData card)
        {
            if (card == null)
                return 0;
            return Util.GradingInterop.Present
                ? Util.GradingInterop.Encoded(card)
                : card.cardGrade;
        }

        private static bool SameCards(List<CardData> a, List<CardData> b)
        {
            int ca = a != null ? a.Count : 0;
            int cb = b != null ? b.Count : 0;
            if (ca != cb)
                return false;
            return HashCards(a) == HashCards(b);
        }
    }
}
