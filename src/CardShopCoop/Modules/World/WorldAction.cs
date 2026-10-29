using System;
using CardShopCoop.Modules.Actions;

namespace CardShopCoop.Modules.World
{
    /// <summary>Small adapter keeping World intents and authoritative deltas on the generic
    /// action path. World code owns only the game-path apply/undo closures.</summary>
    internal static class WorldAction
    {
        internal const string CardsScope = "world.cards";
        internal const string BoxesScope = "world.boxes";
        internal const string ShelvesScope = "world.shelves";
        internal const string WarehouseScope = "world.warehouse";
        internal const string ContainersScope = "world.containers";
        internal const string WorkbenchScope = "world.workbench";
        internal const string BoxStateScope = "world.boxstate";
        internal const string CardDisplayScope = "world.carddisplay";

        /// <summary>Registers a world intent the game's own method already applied locally. The
        /// action only stores how to replay (<paramref name="apply"/>) and reverse
        /// (<paramref name="undo"/>) the change and sends the intent; it never mutates anything
        /// now.</summary>
        internal static Guid Submit(string key, WorldIntentMessage intent, Action apply, Action undo)
            => ActionLedger.Submit(key,
                actionId => WorldClientBehaviour.SendClientIntent(intent, actionId),
                apply, undo);

        /// <summary>World intent with an explicit host-rejection callback. The callback fires only
        /// for THIS intent's refusal, never for a cascade undo.</summary>
        internal static Guid Submit(string key, WorldIntentMessage intent, Action apply, Action undo,
            Action rejected)
            => ActionLedger.Submit(key,
                actionId => WorldClientBehaviour.SendClientIntent(intent, actionId),
                apply, undo, rejected);

        /// <summary>The game already applied our action; the host accepted it, so retire the
        /// action without touching game state.</summary>
        internal static void Ack(WorldStateMessage message)
            => ActionLedger.Ack(message?.ActionId ?? Guid.Empty);

        /// <summary>Either this message confirms our own action (retire it; the game already
        /// performed the change) or it is a remote change (apply it through the game path).</summary>
        internal static void AckOrApply(WorldStateMessage message, Action apply)
            => ActionLedger.AckOrApply(message?.ActionId ?? Guid.Empty, apply);

        internal static void AckOrApply(IPlayerBoxAction message, Action apply)
            => ActionLedger.AckOrApply(message?.ActionId ?? Guid.Empty, apply);

        internal static void AckOrApply(IShelfMutation message, Action apply)
            => ActionLedger.AckOrApply(message?.ActionId ?? Guid.Empty, apply);

        /// <summary>This message resolves our own action (retire it) and then ALWAYS applies the
        /// host's authoritative state, because the client's optimistic run did not produce it (for
        /// example a canonical id or a full record state-set).</summary>
        internal static void Confirm(WorldStateMessage message, Action apply)
            => ActionLedger.Confirm(message?.ActionId ?? Guid.Empty, apply);

        /// <summary>This message carries the host's authoritative absolute state and may share its
        /// key with newer in-flight local edits. Reconciles in layers (undo the target and newer
        /// followers, apply the host state, replay survivors) instead of dropping those newer edits;
        /// an empty or already-retired id just applies.</summary>
        internal static void ApplyAuthoritative(WorldStateMessage message, Action apply)
            => ActionLedger.ApplyAuthoritative(message?.ActionId ?? Guid.Empty, apply);
    }
}
