using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>Debug-only local spectator copy of the playable card table. This is deliberately
    /// a MonoBehaviour rather than a co-op module: it has no wire state and is useful in solo
    /// games too. RuntimeInitializeOnLoadMethod keeps the normal module catalog untouched.</summary>
    internal sealed class PlayTableSpectatorProbe : MonoBehaviour
    {
        private const float PassSeconds = 2f;
        private readonly Dictionary<string, InteractableCard3d> _cards =
            new Dictionary<string, InteractableCard3d>();
        private readonly Dictionary<string, Card3dUIGroup> _uis =
            new Dictionary<string, Card3dUIGroup>();
        private float _timer;
        private PlayTableGame _match;
        private Transform _target;
        private Transform _fallbackRoot;
        private Vector3 _targetPosition;
        private Quaternion _targetRotation;
        private bool _announced;
        private bool _wasInMatch;
        private bool _roundPlayerWin;
        private bool _roundDraw;
        private int _maxTurnSeen;
        private bool _waitingEndResult;
        private float _waitingEndResultSince;
        private PlayTableGame _resultMatch;
        private bool _noTargetAnnounced;
        private static PlayTableSpectatorProbe _instance;

        private static FieldInfo _mode, _sideA, _currentTable;
        private static FieldInfo _playerTurn;
        private static FieldInfo _win, _draw, _endConversation, _pool, _allPool;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("CardShopCoop.PlayTableSpectatorProbeBootstrap");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<Bootstrap>();
        }

        private sealed class Bootstrap : MonoBehaviour
        {
            private void Update()
            {
                bool enabled = CoopPlugin.PlayTableSpectatorProbe != null &&
                    CoopPlugin.PlayTableSpectatorProbe.Value;
                if (!enabled)
                {
                    if (_instance != null)
                        DisableInstance();
                    return;
                }
                if (_instance == null)
                    CreateInstance();
                EnsureReflection();
                var manager = SceneRef<PlayCardGameManager>.Get();
                var match = manager != null ? manager.m_PlayTableGame : null;
                bool inMatch = match != null && ReadBool(ref _mode, match, "m_IsPlayTableGameMode");
                _instance.ObserveMode(match, inMatch);
                _instance.RunFrame(Time.unscaledDeltaTime, inMatch, match);
            }
        }

        private static void CreateInstance()
        {
            if (_instance != null)
                return;
            CoopPlugin.Log.LogInfo("[table-probe] enabled; installing local spectator probe");
            var go = new GameObject("CardShopCoop.PlayTableSpectatorProbe");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<PlayTableSpectatorProbe>();
            _instance._timer = PassSeconds;
        }

        private static void DisableInstance()
        {
            var instance = _instance;
            _instance = null;
            if (instance.HasState())
                instance.Teardown();
            try
            {
                Destroy(instance.gameObject);
            }
            catch (Exception e)
            {
                Swallow.Log(e);
            }
        }

        private void RunFrame(float delta, bool inMatch, PlayTableGame match)
        {
            _timer += delta;
            if (_timer < PassSeconds)
                return;
            _timer = 0f;
            if (_target == null)
            {
                _target = null;
                if (_fallbackRoot == null)
                    _fallbackRoot = null;
            }
            GuardedPass(inMatch, match);
        }

        private void ObserveMode(PlayTableGame match, bool inMatch)
        {
            if (inMatch)
            {
                if (_waitingEndResult)
                {
                    _waitingEndResult = false;
                    _resultMatch = null;
                    _wasInMatch = false;
                }
                if (!_wasInMatch)
                {
                    _wasInMatch = true;
                    ResetRoundState();
                }
                return;
            }
            if (_wasInMatch && !_waitingEndResult)
            {
                _resultMatch = match ?? _match;
                _waitingEndResult = true;
                _waitingEndResultSince = Time.unscaledTime;
                if (HasState())
                    Teardown();
            }
        }

        private void GuardedPass(bool inMatch, PlayTableGame match)
        {
            try
            {
                if (!inMatch)
                {
                    if (_waitingEndResult)
                    {
                        PollEndResult(match ?? _resultMatch);
                        return;
                    }
                    if (_wasInMatch && match != null)
                    {
                        _resultMatch = match;
                        _waitingEndResult = true;
                        _waitingEndResultSince = Time.unscaledTime;
                        if (HasState())
                            Teardown();
                        LogPassStatus("waiting-end-start");
                        return;
                    }
                    _wasInMatch = false;
                    if (HasState())
                        Teardown();
                    LogPassStatus("no-match");
                    return;
                }

                if (_waitingEndResult)
                {
                    _waitingEndResult = false;
                    _resultMatch = null;
                    _wasInMatch = false;
                    ResetRoundState();
                }

                if (!_wasInMatch)
                {
                    _wasInMatch = true;
                    _roundPlayerWin = false;
                    _roundDraw = false;
                    _maxTurnSeen = 0;
                    _announced = false;
                }

                int turnCount = match.GetTurnCount();
                if (turnCount < _maxTurnSeen)
                {
                    _roundPlayerWin = false;
                    _roundDraw = false;
                    _announced = false;
                    _maxTurnSeen = turnCount;
                }
                else if (turnCount > _maxTurnSeen)
                    _maxTurnSeen = turnCount;

                var live = ReadObject<InteractablePlayTable>(ref _currentTable, match,
                    "m_CurrentInteractablePlayTable");
                var sm = SceneRef<ShelfManager>.Get();
                bool fallbackNeeded;
                Transform candidate = FindTargetCandidate(sm, live, out fallbackNeeded);
                if (candidate == null && !fallbackNeeded)
                {
                    if (HasState())
                        Teardown();
                    if (!_noTargetAnnounced)
                    {
                        CoopPlugin.Log.LogInfo("[table-probe] no target available");
                        _noTargetAnnounced = true;
                    }
                    LogPassStatus("no-target");
                    return;
                }
                bool sameTarget = fallbackNeeded
                    ? ReferenceEquals(_target, _fallbackRoot)
                    : ReferenceEquals(_target, candidate);
                if (_match != match || !sameTarget)
                {
                    if (HasState())
                        Teardown();
                    _match = match;
                    _announced = false;
                }
                if (fallbackNeeded)
                {
                    EnsureFallbackRoot();
                    _target = _fallbackRoot;
                    _target.position = live.transform.position + live.transform.right * 5f;
                    _target.rotation = live.transform.rotation;
                    if (!ReadBool(ref _sideA, match, "m_IsSideA"))
                        _target.Rotate(0f, 180f, 0f, Space.Self);
                    _targetPosition = _target.position;
                    _targetRotation = _target.rotation;
                }
                else
                {
                    _target = candidate;
                    _targetPosition = candidate.position;
                    _targetRotation = candidate.rotation;
                    if (!ReadBool(ref _sideA, match, "m_IsSideA"))
                        _targetRotation *= Quaternion.Euler(0f, 180f, 0f);
                }
                Apply(match, turnCount);
            }
            catch (Exception e)
            {
                LogPassStatus($"failed error={e.GetType().Name}");
                ModuleGuard.Log("table-probe:pass", e);
            }
        }

        private void LogPassStatus(string status)
        {
            CoopPlugin.Log.LogInfo($"[table-probe] pass={status}");
        }

        private void PollEndResult(PlayTableGame match)
        {
            bool ended = match != null && ReadBool(ref _endConversation, match, "m_IsEndGameConversationDone");
            float elapsed = Time.unscaledTime - _waitingEndResultSince;
            if (ended)
            {
                _roundPlayerWin = ReadBool(ref _win, match, "m_IsPlayerWin");
                _roundDraw = ReadBool(ref _draw, match, "m_IsDraw");
                CoopPlugin.Log.LogInfo($"[table-probe] waiting-end flag=true t={elapsed:F1}s result={(_roundDraw ? "draw" : _roundPlayerWin ? "win" : "loss")}");
                _waitingEndResult = false;
                _resultMatch = null;
                _wasInMatch = false;
                ResetRoundState();
                return;
            }
            CoopPlugin.Log.LogInfo($"[table-probe] waiting-end flag=false t={elapsed:F1}s");
            if (Time.unscaledTime - _waitingEndResultSince >= 120f)
            {
                CoopPlugin.Log.LogWarning("[table-probe] end conversation not observed");
                _waitingEndResult = false;
                _resultMatch = null;
                _wasInMatch = false;
                ResetRoundState();
            }
        }

        private void ResetRoundState()
        {
            _roundPlayerWin = false;
            _roundDraw = false;
            _maxTurnSeen = 0;
            _announced = false;
            _noTargetAnnounced = false;
        }

        private Transform FindTargetCandidate(ShelfManager sm, InteractablePlayTable live, out bool fallbackNeeded)
        {
            fallbackNeeded = false;
            if (live == null)
                return null;
            // An invalid live table means match discovery is stale or mid-transition. Keep the
            // existing fallback-root behavior rather than selecting another table as its peer.
            if (!live.IsValidObject())
            {
                fallbackNeeded = true;
                return null;
            }
            if (sm != null && sm.m_PlayTableList != null)
                for (int i = 0; i < sm.m_PlayTableList.Count; i++)
                    if (sm.m_PlayTableList[i] != null && sm.m_PlayTableList[i] != live &&
                        sm.m_PlayTableList[i].IsValidObject())
                        return sm.m_PlayTableList[i].transform;
            fallbackNeeded = true;
            return null;
        }

        private void Apply(PlayTableGame match, int turnCount)
        {
            var watch = Stopwatch.StartNew();
            var wanted = new HashSet<string>();
            var pending = new List<PendingCard>();
            int visible = 0;
            var sample = new StringBuilder();
            AddSide(match.m_PlayCardSetPlayer, "P", wanted, pending, ref visible, sample);
            AddSide(match.m_PlayCardSetEnemy, "E", wanted, pending, ref visible, sample);
            var dead = new List<string>();
            foreach (var pair in _cards)
                if (pair.Value == null)
                    dead.Add(pair.Key);
            for (int i = 0; i < dead.Count; i++)
                Remove(dead[i]);
            var stale = new List<string>();
            foreach (var pair in _cards)
                if (!wanted.Contains(pair.Key))
                    stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++)
                Remove(stale[i]);
            for (int i = 0; i < pending.Count; i++)
                Place(pending[i].Key, pending[i].Data, pending[i].Slot, match);

            int hpP = match.m_PlayCardSetPlayer != null ? match.m_PlayCardSetPlayer.GetCurrentHP() : 0;
            int hpE = match.m_PlayCardSetEnemy != null ? match.m_PlayCardSetEnemy.GetCurrentHP() : 0;
            int holdP = Count(match.m_PlayCardSetPlayer, "m_HoldCard3dList");
            int holdE = Count(match.m_PlayCardSetEnemy, "m_HoldCard3dList");
            int deckP = match.m_PlayCardSetPlayer?.m_DeckCardDataList?.Count ?? 0;
            int deckE = match.m_PlayCardSetEnemy?.m_DeckCardDataList?.Count ?? 0;
            int discardP = Count(match.m_PlayCardSetPlayer, "m_DiscardCard3dList");
            int discardE = Count(match.m_PlayCardSetEnemy, "m_DiscardCard3dList");
            int guardianP = Count(match.m_PlayCardSetPlayer, "m_GuardianCard3dList");
            int guardianE = Count(match.m_PlayCardSetEnemy, "m_GuardianCard3dList");
            bool playerWin = ReadBool(ref _win, match, "m_IsPlayerWin");
            bool drawResult = ReadBool(ref _draw, match, "m_IsDraw");
            bool ended = ReadBool(ref _endConversation, match, "m_IsEndGameConversationDone");
            if (ended)
            {
                _roundPlayerWin = playerWin;
                _roundDraw = drawResult;
            }
            CoopPlugin.Log.LogInfo($"[table-probe] visible={visible} sample={sample} hidden=hand={holdP}/{holdE} deck={deckP}/{deckE} discard={discardP}/{discardE} guardian={guardianP}/{guardianE} hp={hpP}/{hpE} shield={Shield(match.m_PlayCardSetPlayer)}/{Shield(match.m_PlayCardSetEnemy)} freeze={Freeze(match.m_PlayCardSetPlayer)}/{Freeze(match.m_PlayCardSetEnemy)} turn={turnCount} owner={(ReadBool(ref _playerTurn, match, "m_IsPlayerTurn") ? "P" : "E")} result={(ended ? (_roundDraw ? "draw" : _roundPlayerWin ? "win" : "loss") : "ongoing")} pool={PoolCount(_pool)}/{PoolCount(_allPool)} ms={watch.Elapsed.TotalMilliseconds:F1}");
        }

        private struct PendingCard
        {
            public string Key;
            public CardData Data;
            public Transform Slot;
        }

        private void AddSide(PlayCardSet set, string side, HashSet<string> wanted,
            List<PendingCard> pending, ref int visible, StringBuilder sample)
        {
            if (set == null || set.m_ElementAreaCardList == null)
                return;
            // The game's PlayCardSet element-area lists are the face-up board zone: cards are
            // inserted there by its play-resolution methods and hidden cards are moved to the
            // hold/deck/discard lists. This invariant is present in both PlayCardSet baselines
            // (m_ElementAreaCardList declaration at PlayCardSet.cs:103 and the corresponding
            // element-area insertion/layout operations around PlayCardSet.cs:2817-2835 in
            // 1.00; legacy insertion/layout operations are PlayCardSet.cs:2800-2827 and
            // removal operations are PlayCardSet.cs:2851-2918; 1.00 removal operations are
            // PlayCardSet.cs:2859-2926).
            for (int area = 0; area < set.m_ElementAreaCardList.Count; area++)
            {
                var list = set.m_ElementAreaCardList[area]?.cardList;
                var positions = set.m_ElementAreaPosList != null && area < set.m_ElementAreaPosList.Count ? set.m_ElementAreaPosList[area]?.transformList : null;
                if (list == null)
                    continue;
                for (int slot = 0; slot < list.Count; slot++)
                {
                    var source = list[slot];
                    if (source == null || source.m_Card3dUI == null || source.m_Card3dUI.m_CardUI == null)
                        continue;
                    CardData data = source.m_Card3dUI.m_CardUI.GetCardData();
                    if (data == null || positions == null || slot >= positions.Count || positions[slot] == null)
                        continue;
                    string key = side + ":" + area + ":" + slot + ":" + Identity(data);
                    wanted.Add(key);
                    visible++;
                    if (sample.Length < 100)
                        sample.Append(sample.Length == 0 ? "" : ",").Append(Identity(data));
                    pending.Add(new PendingCard { Key = key, Data = data, Slot = positions[slot] });
                }
            }
        }

        private void Place(string key, CardData data, Transform slot, PlayTableGame match)
        {
            if (!_cards.TryGetValue(key, out var card) || card == null)
            {
                if (_uis.TryGetValue(key, out var staleUi) && staleUi != null)
                {
                    try
                    {
                        staleUi.DisableCard();
                    }
                    catch (Exception e)
                    {
                        Swallow.Log(e);
                    }
                }
                _uis.Remove(key);
                Card3dUIGroup ui = null;
                try
                {
                    ui = SceneRef<Card3dUISpawner>.Get().GetCardUI();
                    card = ShelfManager.SpawnInteractableObject(EObjectType.Card3d).GetComponent<InteractableCard3d>();
                    ui.m_IgnoreCulling = true;
                    ui.m_CardUI.SetFoilCullListVisibility(true);
                    ui.SetSimplifyCardDistanceCull(false);
                    ui.m_CardUI.ResetFarDistanceCull();
                    var renderData = new CardData();
                    renderData.CopyData(data);
                    ui.m_CardUI.SetCardUI(renderData);
                    ui.transform.SetPositionAndRotation(card.transform.position, card.transform.rotation);
                    card.SetCardUIFollow(ui);
                    card.SetEnableCollision(false);
                    _cards[key] = card;
                    _uis[key] = ui;
                    ui.m_IgnoreCulling = false;
                    if (!_announced)
                    {
                        CoopPlugin.Log.LogInfo("[table-probe] applying first visible board copy for match");
                        _announced = true;
                    }
                }
                catch (Exception original)
                {
                    Swallow.Log(original);
                    if (card != null)
                    {
                        try
                        {
                            card.OnDestroyed();
                        }
                        catch (Exception e)
                        {
                            Swallow.Log(e);
                        }
                    }
                    if (ui != null)
                    {
                        try
                        {
                            ui.DisableCard();
                        }
                        catch (Exception e)
                        {
                            Swallow.Log(e);
                        }
                    }
                    throw;
                }
            }
            Transform rig = match.m_Grp != null ? match.m_Grp.transform : match.transform;
            Vector3 slotLocalPosition = rig.InverseTransformPoint(slot.position);
            Quaternion slotLocalRotation = Quaternion.Inverse(rig.rotation) * slot.rotation;
            Vector3 position = _targetPosition + _targetRotation * slotLocalPosition;
            Quaternion rotation = _targetRotation * slotLocalRotation;
            card.transform.SetPositionAndRotation(position, rotation);
            if (_uis.TryGetValue(key, out var group) && group != null)
                group.transform.SetPositionAndRotation(position, rotation);
        }

        private void Remove(string key)
        {
            bool cardAlive = _cards.TryGetValue(key, out var card) && card != null;
            bool destroyedOk = false;
            if (cardAlive)
            {
                try
                {
                    card.OnDestroyed();
                    destroyedOk = true;
                }
                catch (Exception e)
                {
                    Swallow.Log(e);
                }
            }
            if (!destroyedOk && _uis.TryGetValue(key, out var ui) && ui != null)
            {
                try
                {
                    ui.DisableCard();
                }
                catch (Exception e)
                {
                    Swallow.Log(e);
                }
            }
            _cards.Remove(key);
            _uis.Remove(key);
        }

        private void Teardown()
        {
            CoopPlugin.Log.LogInfo("[table-probe] tearing down copied board");
            var keys = new List<string>(_cards.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                try
                {
                    Remove(keys[i]);
                }
                catch (Exception e)
                {
                    Swallow.Log(e);
                    _cards.Remove(keys[i]);
                    _uis.Remove(keys[i]);
                }
            }
            try
            {
                if (_fallbackRoot != null)
                    Destroy(_fallbackRoot.gameObject);
            }
            catch (Exception e)
            {
                Swallow.Log(e);
            }
            finally
            {
                _cards.Clear();
                _uis.Clear();
                _fallbackRoot = null;
                _match = null;
                _target = null;
                _announced = false;
            }
        }

        private bool HasState()
        {
            return _cards.Count != 0 || _fallbackRoot != null || _target != null;
        }

        private static string Identity(CardData data)
        {
            var sb = new StringBuilder(data.GetType().Name);
            foreach (var f in data.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                sb.Append('|').Append(f.Name).Append('=').Append(f.GetValue(data));
            return sb.ToString();
        }
        private static int Count(PlayCardSet set, string field) => set == null ? 0 : (ReadObject(field, set) as System.Collections.ICollection)?.Count ?? 0;
        private static int Shield(PlayCardSet set) => set == null ? 0 : set.GetTamerShieldHP();
        private static int Freeze(PlayCardSet set) => set == null ? 0 : set.GetFreezeCounterAmount();
        private static T ReadObject<T>(ref FieldInfo field, object owner, string name) where T : class
        {
            field ??= AccessTools.Field(owner.GetType(), name);
            return field?.GetValue(owner) as T;
        }
        private static object ReadObject(string name, object owner) => AccessTools.Field(owner.GetType(), name)?.GetValue(owner);
        private static bool ReadBool(ref FieldInfo field, object owner, string name) => field != null ? (bool)field.GetValue(owner) : ((field = AccessTools.Field(owner.GetType(), name)) != null && (bool)field.GetValue(owner));
        private static int PoolCount(FieldInfo field)
        {
            var spawner = SceneRef<Card3dUISpawner>.Get();
            return spawner == null || field == null ? 0 : (field.GetValue(spawner) as System.Collections.ICollection)?.Count ?? 0;
        }

        private static void EnsureReflection()
        {
            var type = typeof(PlayTableGame);
            _mode ??= AccessTools.Field(type, "m_IsPlayTableGameMode");
            _sideA ??= AccessTools.Field(type, "m_IsSideA");
            _currentTable ??= AccessTools.Field(type, "m_CurrentInteractablePlayTable");
            _win ??= AccessTools.Field(type, "m_IsPlayerWin");
            _draw ??= AccessTools.Field(type, "m_IsDraw");
            _playerTurn ??= AccessTools.Field(type, "m_IsPlayerTurn");
            _endConversation ??= AccessTools.Field(type, "m_IsEndGameConversationDone");
            _pool ??= AccessTools.Field(typeof(Card3dUISpawner), "m_Card3dUIList");
            _allPool ??= AccessTools.Field(typeof(Card3dUISpawner), "m_AllCard3dUIList");
        }

        private void EnsureFallbackRoot()
        {
            if (_fallbackRoot != null)
                return;
            var go = new GameObject("CardShopCoop.PlayTableSpectatorTarget");
            go.hideFlags = HideFlags.HideAndDontSave;
            _fallbackRoot = go.transform;
        }
    }
}
