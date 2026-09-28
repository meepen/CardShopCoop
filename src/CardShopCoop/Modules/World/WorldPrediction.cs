using System;
using CardShopCoop.Modules.Prediction;

namespace CardShopCoop.Modules.World
{
    /// <summary>Small adapter keeping World intents and authoritative deltas on the generic
    /// prediction path. World code owns only the game-path apply/undo closures.</summary>
    internal static class WorldPrediction
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
        /// prediction only stores how to replay (<paramref name="apply"/>) and reverse
        /// (<paramref name="undo"/>) the change and sends the intent; it never mutates anything
        /// now.</summary>
        internal static Guid Predict(string key, WorldMessage intent, Action apply, Action undo)
            => PredictionApi.Predict(key,
                predictionId => WorldClientBehaviour.SendClientIntent(intent, predictionId),
                apply, undo);

        /// <summary>World intent with an explicit host-rejection callback. The callback fires only
        /// for THIS intent's refusal, never for a cascade undo.</summary>
        internal static Guid Predict(string key, WorldMessage intent, Action apply, Action undo,
            Action rejected)
            => PredictionApi.Predict(key,
                predictionId => WorldClientBehaviour.SendClientIntent(intent, predictionId),
                apply, undo, rejected);

        /// <summary>The game already applied our action; the host accepted it, so retire the
        /// prediction without touching game state.</summary>
        internal static void Ack(WorldMessage message)
            => PredictionApi.Ack(message?.PredictionId ?? Guid.Empty);

        /// <summary>Either this message confirms our own prediction (retire it; the game already
        /// performed the change) or it is a remote change (apply it through the game path).</summary>
        internal static void AckOrApply(WorldMessage message, Action apply)
            => PredictionApi.AckOrApply(message?.PredictionId ?? Guid.Empty, apply);

        /// <summary>This message resolves our own prediction (retire it) and then ALWAYS applies the
        /// host's authoritative state, because the client's optimistic run did not produce it (for
        /// example a canonical id or a full record state-set).</summary>
        internal static void Confirm(WorldMessage message, Action apply)
            => PredictionApi.Confirm(message?.PredictionId ?? Guid.Empty, apply);

        /// <summary>This message carries the host's authoritative absolute state and may share its
        /// key with newer in-flight local edits. Reconciles in layers (undo the target and newer
        /// followers, apply the host state, replay survivors) instead of dropping those newer edits;
        /// an empty or already-retired id just applies.</summary>
        internal static void ApplyAuthoritative(WorldMessage message, Action apply)
            => PredictionApi.ApplyAuthoritative(message?.PredictionId ?? Guid.Empty, apply);
    }
}
