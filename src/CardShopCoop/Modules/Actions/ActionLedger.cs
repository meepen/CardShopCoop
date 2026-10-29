using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Api;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Actions
{
    /// <summary>
    /// The whole action layer. The game performs every action through its own code path; a
    /// action only records how to redo (<c>apply</c>) and undo (<c>undo</c>) that game change,
    /// both through the game's own methods, so a rejection can be reconciled in layers back to the
    /// target state.
    ///
    /// Host accepted = retire, nothing else (the game already applied it), unless the host message
    /// carries authoritative absolute state the optimistic run did not produce: then
    /// <see cref="Confirm(Guid, Action)"/> retires and re-applies it, or
    /// <see cref="ApplyAuthoritative(Guid, Action)"/> reconciles it in layers so newer in-flight
    /// local edits on the same key survive.
    /// Host rejected = <see cref="Rollback(Guid)"/>, which undoes the rejected action and every
    /// still-pending action queued after it on the same key, then replays the survivors.
    /// </summary>
    public static class ActionLedger
    {
        private sealed class ActionRecord
        {
            internal Guid Id;
            internal string Key;
            internal Action Apply;
            internal Action<ActionResponseMessage> Undo;
            internal Action<ActionResponseMessage> Merge;
            internal Action Rejected;
            internal ActionResponseMessage Response;
        }

        private static readonly Dictionary<Guid, ActionRecord> ById = new();
        private static readonly Dictionary<string, List<ActionRecord>> ByKey = new(StringComparer.Ordinal);
        // Actions registered by a game-path hook while a reconcile is running. The reconcile
        // iterates snapshots of its key list, so a live append from inside an undo/apply would
        // otherwise race the loop; these are registered in order once the reconcile finishes.
        private static readonly List<ActionRecord> Deferred = new();
        private static bool _active;
        private static bool _reconciling;
        private static int _applying;

        // A retirement is a one-shot fact whose cleanup must run exactly once, so it is delivered
        // through a queue that preserves every id: a handler that retires another action during
        // this delivery queues it for the next pass instead of re-entering the publisher (which
        // nested the cleanup handlers and could recurse). Delivery order is the retirement order.
        private static readonly CoalescedQueue<Guid> Retired = new();
        private static readonly CoalescedQueue<Guid> Reconciled = new();

        /// <summary>Raised when an action leaves the tracker.</summary>
        public static event Action<Guid> ActionSettled
        {
            add => Retired.Handlers += value;
            remove => Retired.Handlers -= value;
        }

        /// <summary>Raised after a layered reconcile (undo/replay around an authoritative state)
        /// has completely finished. A module that deferred work while a local action was pending can
        /// now re-read its authoritative store safely.</summary>
        public static event Action<Guid> ActionReconciled
        {
            add => Reconciled.Handlers += value;
            remove => Reconciled.Handlers -= value;
        }

        /// <summary>True while a rejection is being layered (undo/replay), so the hook code that
        /// forwards game changes can skip the changes this reconciliation itself performs.</summary>
        public static bool IsReconciling => _reconciling;

        /// <summary>True while a client action session is active (a client is in a session).</summary>
        public static bool IsActive => _active;

        /// <summary>True while a game-path apply/replay is running.</summary>
        public static bool IsApplying => _applying > 0;

        internal static void Start()
        {
            Clear();
            _active = true;
        }

        internal static void Stop()
        {
            _active = false;
            Clear();
        }

        /// <summary>Records an action the game already performed and sends it. The action only
        /// stores how to replay (<paramref name="apply"/>) and reverse (<paramref name="undo"/>) the
        /// change; it never mutates anything now. The game owns every local mutation.</summary>
        public static Guid Submit(string key, Action<Guid> send, Action apply, Action undo)
            => Submit(key, send, apply, undo, null);

        public static Guid Submit(string key, Action<Guid> send, Action apply, Action undo,
            Action rejected)
            => Record(key, send, apply, undo == null ? null : _ => undo(), rejected, null);

        /// <summary>Submits a typed action message to the host. The framework allocates the
        /// action id and fills it in on the message; no raw send closure is involved.</summary>
        internal static Guid Submit(string key, ActionMessage message, Action apply, Action undo)
            => Submit(key, message, apply, undo == null ? null : _ => undo(), null);

        internal static Guid Submit(string key, ActionMessage message, Action apply,
            Action<ActionResponseMessage> undo, Action rejected = null)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var runtime = CoopCore.Instance?.LiveContext;
            if (runtime == null)
            {
                return Guid.Empty;
            }

            var context = (ICoopContext)runtime;
            var host = context.HostConnection;
            if (host == null || !runtime.InGame())
            {
                return Guid.Empty;
            }

            return Record(key,
                actionId =>
                {
                    message.ActionId = actionId;
                    context.Send(host, message);
                }, apply, undo, rejected, null);
        }

        /// <summary>Submits an action message using its own apply/undo/merge hooks and the entity
        /// key derived from its live object fields.</summary>
        internal static Guid Submit(ActionMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var runtime = CoopCore.Instance?.LiveContext;
            if (runtime == null)
            {
                return Guid.Empty;
            }

            var context = (ICoopContext)runtime;
            var host = context.HostConnection;
            if (host == null || !runtime.InGame())
            {
                return Guid.Empty;
            }

            return Record(AutoKey(message),
                actionId =>
                {
                    message.ActionId = actionId;
                    context.Send(host, message);
                }, message.OnApply, message.OnUndo, null, message.OnMerge);
        }

        /// <summary>Applies an authoritative value around its action: an accepted response retires
        /// without undoing the target; a rejected response reverses the target first.</summary>
        internal static void Apply(IActionValue value, Action apply)
        {
            if (apply == null)
            {
                throw new ArgumentNullException(nameof(apply));
            }

            if (value == null)
            {
                RunApplying(apply);
                return;
            }

            Apply(value.ActionId, value.Rejected, apply);
        }

        internal static void Apply(Guid actionId, bool rejected, Action apply)
        {
            if (apply == null)
            {
                throw new ArgumentNullException(nameof(apply));
            }

            if (rejected)
            {
                ApplyAuthoritative(actionId, apply);
                return;
            }

            if (actionId != Guid.Empty)
            {
                ApplyConfirmed(actionId, apply);
                return;
            }

            RunApplying(apply);
        }

        internal static string KeyFor(object entity)
            => entity is UnityEngine.Object unity && unity != null
                ? "entity:" + unity.GetInstanceID() : null;

        /// <summary>True when a local action is pending on this entity.</summary>
        internal static bool IsPending(object entity)
        {
            var key = KeyFor(entity);
            return key != null && IsPending(key);
        }

        /// <summary>True when this exact pending action targets this entity.</summary>
        internal static bool IsPending(object entity, Guid actionId)
        {
            var key = KeyFor(entity);
            return key != null && IsPending(key, actionId);
        }

        private static string AutoKey(ActionMessage message)
        {
            var type = message.GetType();
            var fields = EntityFields(type);
            if (fields.Length == 0)
            {
                return "message:" + type.FullName;
            }

            var ids = new List<int>(fields.Length);
            for (var i = 0; i < fields.Length; i++)
            {
                if (fields[i].GetValue(message) is UnityEngine.Object unity && unity != null)
                {
                    ids.Add(unity.GetInstanceID());
                }
            }

            if (ids.Count == 0)
            {
                return "message:" + type.FullName;
            }

            ids.Sort();
            return "entity:" + string.Join(",", ids);
        }

        private static readonly Dictionary<Type, FieldInfo[]> EntityFieldsByType = new();

        private static FieldInfo[] EntityFields(Type type)
        {
            if (EntityFieldsByType.TryGetValue(type, out var cached))
            {
                return cached;
            }

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            var entityFields = new List<FieldInfo>();
            for (var i = 0; i < fields.Length; i++)
            {
                if (typeof(UnityEngine.Object).IsAssignableFrom(fields[i].FieldType))
                {
                    entityFields.Add(fields[i]);
                }
            }

            var result = entityFields.ToArray();
            EntityFieldsByType[type] = result;
            return result;
        }

        private static Guid Record(string key, Action<Guid> send, Action apply,
            Action<ActionResponseMessage> undo, Action rejected,
            Action<ActionResponseMessage> merge)
        {
            if (!_active)
            {

                throw new InvalidOperationException("Client action is not active.");
            }


            if (string.IsNullOrEmpty(key))
            {

                throw new ArgumentException("An action key is required.", nameof(key));
            }


            if (send == null)
            {
                throw new ArgumentNullException(nameof(send));
            }


            var action = new ActionRecord
            {
                Id = Guid.NewGuid(),
                Key = key,
                Apply = apply,
                Undo = undo,
                Merge = merge,
                Rejected = rejected,
            };
            ById.Add(action.Id, action);
            if (_reconciling)
            {
                // A game-path hook driven by the reconcile's own undo/apply must not append to the
                // live key list the reconcile is iterating. Hold the new action and register it
                // in order once the reconcile completes.
                Deferred.Add(action);
            }
            else
            {
                GetOrAdd(key).Add(action);
            }

            try
            {
                send(action.Id);
            }
            catch
            {
                Remove(action);
                throw;
            }

            return action.Id;
        }

        /// <summary>The host accepted this action. The game already performed the action through
        /// its own path, so there is nothing to apply - retire it.</summary>
        public static void Ack(Guid actionId)
        {
            if (actionId == Guid.Empty)
            {
                return;
            }

            if (ById.TryGetValue(actionId, out var action))
            {
                Remove(action);
            }
        }

        /// <summary>Either this message confirms our own action (retire it; the game already did
        /// the change) or it is a remote change (apply it through the game path).</summary>
        public static void AckOrApply(Guid actionId, Action apply)
        {
            if (apply == null)
            {

                throw new ArgumentNullException(nameof(apply));
            }


            if (actionId != Guid.Empty && ById.TryGetValue(actionId, out var action))
            {
                Remove(action);
                return;
            }

            RunApplying(apply);
        }

        /// <summary>This message resolves our own action (retire it, without running anything
        /// from the retire) and then ALWAYS applies the authoritative state through the game path.
        /// Use it when the host's message supersedes the client's optimistic run - the optimistic
        /// apply alone did not produce the host's values (host-computed fields, canonical ids, or a
        /// full authoritative state-set). The apply is idempotent with respect to the optimistic run.
        /// An empty id just applies.</summary>
        public static void Confirm(Guid actionId, Action apply)
        {
            if (apply == null)
            {

                throw new ArgumentNullException(nameof(apply));
            }


            if (actionId != Guid.Empty && ById.TryGetValue(actionId, out var action))
            {
                Remove(action);
            }


            RunApplying(apply);
        }

        /// <summary>This message carries the host's authoritative absolute state that supersedes our
        /// optimistic run. Unlike <see cref="Confirm(Guid, Action)"/>, which retires the target and
        /// applies over whatever newer local edits are still live, this reconciles in layers: when
        /// the id is a live action it undoes the target and every still-pending newer action on
        /// the same key (newest first), retires the target, applies the host state, then replays the
        /// surviving newer actions. That folds a concurrent host change AND preserves an overlapping
        /// in-flight local edit instead of transiently clobbering it. This is an ACCEPT, so no
        /// rejection callback fires; an empty or already-retired id just applies.</summary>
        public static void ApplyAuthoritative(Guid actionId, Action apply)
        {
            if (apply == null)
            {

                throw new ArgumentNullException(nameof(apply));
            }


            if (actionId != Guid.Empty && ById.TryGetValue(actionId, out var action))
            {
                Reconcile(action, apply);
                return;
            }

            RunApplying(apply);
        }

        /// <summary>This message is the host's echo of our own action. Retire the action
        /// WITHOUT running its undo (the game already performed it and the authority includes it),
        /// undo and replay any newer local actions on the same key in layers, then apply authority.
        /// Use this for an accepted action; use <see cref="ApplyAuthoritative(Guid, Action)"/> for a
        /// rejection re-anchor, which must reverse the target. An empty or already-retired id just
        /// applies.</summary>
        public static void ApplyConfirmed(Guid actionId, Action apply)
        {
            if (apply == null)
            {

                throw new ArgumentNullException(nameof(apply));
            }


            if (actionId != Guid.Empty && ById.TryGetValue(actionId, out var action))
            {
                Reconcile(action, apply, undoTarget: false);
                return;
            }

            RunApplying(apply);
        }

        private static void RunApplying(Action apply)
        {
            _applying++;
            try
            {
                apply();
            }
            finally
            {
                _applying--;
            }
        }

        /// <summary>True while the local action is still awaiting a host decision.</summary>
        public static bool IsPending(Guid actionId)
            => actionId != Guid.Empty && ById.ContainsKey(actionId);

        /// <summary>True when any local action on this key is still awaiting its host decision.
        /// A module uses this to keep a pending local action owning its entity: an older
        /// uncorrelated host state must not be applied over it.</summary>
        public static bool IsPending(string key)
            => !string.IsNullOrEmpty(key) && ByKey.TryGetValue(key, out var actions)
                && actions.Count > 0;

        /// <summary>True when this exact action is pending on this key. A state carrying this
        /// id is that action's own echo or re-anchor.</summary>
        public static bool IsPending(string key, Guid actionId)
        {
            if (string.IsNullOrEmpty(key) || actionId == Guid.Empty
                || !ByKey.TryGetValue(key, out var actions))
            {
                return false;
            }

            for (var i = 0; i < actions.Count; i++)
            {
                if (actions[i].Id == actionId)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Host side: rejects one client action with the single generic rollback.</summary>
        public static void Reject(ICoopContext context, int connectionId, Guid actionId)
        {
            if (actionId == Guid.Empty)
            {
                return;
            }


            if (context == null)
            {

                throw new ArgumentNullException(nameof(context));
            }


            try
            {
                context.Send(connectionId, new ActionRollbackMessage
                {
                    ActionId = actionId,
                    Undo = true,
                });
            }
            catch (Exception error)
            {
                // The peer is normally already disconnecting when its send path rejects this
                // courtesy notification. A failure here must not escape a message-rejection
                // handler and turn a keep-the-session rejection into a dispatch disconnect.
                CoopPlugin.Log.LogWarning("action rollback could not be sent for conn "
                    + connectionId + " id=" + actionId + ": " + error.Message);
            }
        }

        /// <summary>Host side: the action was applied, but its handler failed before publishing
        /// authority. Tell the client to retire the optimistic action without undoing it.</summary>
        public static void Accept(ICoopContext context, int connectionId, Guid actionId)
        {
            if (actionId == Guid.Empty)
            {
                return;
            }
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            try
            {
                context.Send(connectionId, new ActionRollbackMessage
                {
                    ActionId = actionId,
                    Undo = false,
                });
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("action acknowledgement could not be sent for conn "
                    + connectionId + " id=" + actionId + ": " + error.Message);
            }
        }

        /// <summary>Client side: an accepted response arrived. Retire the action and merge the
        /// host's authoritative extra data into the local prediction.</summary>
        internal static void Accept(Guid actionId, ActionResponseMessage response)
        {
            if (actionId == Guid.Empty || !ById.TryGetValue(actionId, out var action))
            {
                return;
            }

            var merge = action.Merge;
            Remove(action);
            if (merge != null)
            {
                RunApplying(() => merge(response));
            }
        }

        public static void Rollback(ICoopContext context, int connectionId, Guid actionId)
        {
            if (actionId == Guid.Empty)
            {
                return;
            }


            if (context == null)
            {

                throw new ArgumentNullException(nameof(context));
            }


            CoopPlugin.Log.LogInfo("action rollback -> conn " + connectionId + " id=" + actionId);
            Reject(context, connectionId, actionId);
        }

        /// <summary>Client side: the host rejected an action. Layered revert to the target state:
        /// undo every still-pending action at or after this one (newest first), retire the rejected
        /// action, then replay the later actions on top so the survivors land where they should. A
        /// rollback can cross with the authoritative message that already resolved the action,
        /// so an unknown id is ignored rather than treated as a protocol error.</summary>
        internal static void Rollback(Guid actionId, ActionResponseMessage response = null)
        {
            if (actionId == Guid.Empty)
            {
                return;
            }


            if (!ById.TryGetValue(actionId, out var rejected))
            {
                CoopPlugin.Log.LogInfo("action rollback ignored for already-resolved action "
                    + actionId + ".");
                return;
            }

            CoopPlugin.Log.LogInfo("action rollback client key=" + rejected.Key
                + " id=" + actionId + ".");
            rejected.Response = response;
            var rejectedCallback = rejected.Rejected;
            Reconcile(rejected, null, response: response);
            rejectedCallback?.Invoke();
        }

        private static void Reconcile(ActionRecord action, Action authoritative,
            bool undoTarget = true, ActionResponseMessage response = null)
        {
            if (!ByKey.TryGetValue(action.Key, out var actions))
            {

                throw new InvalidOperationException("ActionRecord key is inconsistent: " + action.Id + ".");
            }


            var index = actions.IndexOf(action);
            if (index < 0)
            {

                throw new InvalidOperationException("ActionRecord key is inconsistent: " + action.Id + ".");
            }

            // Snapshot the followers queued after the target before running any undo/apply. A nested
            // Submit() during undo/replay registers a new action that was never applied
            // locally; it must not be replayed here as if it were one of the original followers.

            var followers = new List<ActionRecord>(actions.Count - index - 1);
            for (var i = index + 1; i < actions.Count; i++)
            {
                followers.Add(actions[i]);
            }

            // Snapshot the whole undo set (the target plus every follower) and remove the target
            // from the live key list BEFORE any undo runs. A hook driven by an undo can append to
            // or remove from the same key list; iterating the snapshot keeps that from shifting an
            // index out of range or undoing an entry twice.

            var undo = new List<ActionRecord>(followers.Count + 1);
            if (undoTarget)
            {
                undo.Add(action);
            }

            undo.AddRange(followers);

            _reconciling = true;
            try
            {
                actions.RemoveAt(index);
                ById.Remove(action.Id);
                Retired.Raise(action.Id);

                for (var i = undo.Count - 1; i >= 0; i--)
                {
                    var record = undo[i];
                    record.Undo?.Invoke(record == action ? response : record.Response);
                }


                authoritative?.Invoke();

                for (var i = 0; i < followers.Count; i++)
                {
                    var follower = followers[i];
                    if (!ById.ContainsKey(follower.Id))
                    {
                        continue;
                    }


                    try
                    {
                        follower.Apply?.Invoke();
                    }
                    catch (Exception error)
                    {
                        // A follower whose game subject is gone can no longer be replayed. Retire it
                        // with a log so one broken replay cannot abort the batch and tear down the
                        // session on the reliable lane.
                        CoopPlugin.Log.LogWarning("ActionRecord follower replay failed; retiring id="
                            + follower.Id + " key=" + follower.Key + ": " + error);
                        Remove(follower);
                    }
                }
            }
            finally
            {
                _reconciling = false;
                if (actions.Count == 0)
                {
                    ByKey.Remove(action.Key);
                }


                FlushDeferred();
                Reconciled.Raise(action.Id);
            }
        }

        /// <summary>Registers every action held back while a reconcile was running. Called once
        /// the reconcile (including its finally cleanup) has finished so the key lists are stable
        /// again. An action already removed mid-reconcile is dropped.</summary>
        private static void FlushDeferred()
        {
            if (Deferred.Count == 0)
            {
                return;
            }


            for (var i = 0; i < Deferred.Count; i++)
            {
                var action = Deferred[i];
                if (ById.ContainsKey(action.Id))
                {
                    GetOrAdd(action.Key).Add(action);
                }

            }

            Deferred.Clear();
        }

        private static List<ActionRecord> GetOrAdd(string key)
        {
            if (!ByKey.TryGetValue(key, out var actions))
            {
                actions = new List<ActionRecord>();
                ByKey.Add(key, actions);
            }

            return actions;
        }

        private static void Remove(ActionRecord action)
        {
            if (ById.Remove(action.Id))
            {
                Retired.Raise(action.Id);
            }
            // An action still held back by a reconcile was never appended to a key list.

            if (Deferred.Remove(action))
            {
                return;
            }


            if (!ByKey.TryGetValue(action.Key, out var actions))
            {
                return;
            }


            actions.Remove(action);
            if (actions.Count == 0)
            {
                ByKey.Remove(action.Key);
            }

        }

        private static void Clear()
        {
            ById.Clear();
            ByKey.Clear();
            Deferred.Clear();
            // A retirement queued but not yet delivered is session-scoped state too: without this a
            // stale id would be handed to the next session's ActionSettled subscribers.
            Retired.Clear();
            Reconciled.Clear();
            _reconciling = false;
            _applying = 0;
        }
    }
}
