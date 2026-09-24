using System;
using CardShopCoop.Modules.Prediction;

namespace CardShopCoop.Modules.World
{
    /// <summary>Small adapter keeping World intents and authoritative deltas on the generic
    /// prediction path. World code owns only the minimal game-state apply/undo closures.</summary>
    internal static class WorldPrediction
    {
        internal const string CardsScope = "world.cards";
        internal const string BoxesScope = "world.boxes";
        internal const string ShelvesScope = "world.shelves";
        internal const string WarehouseScope = "world.warehouse";
        internal const string ContainersScope = "world.containers";
        internal const string BoxStateScope = "world.boxstate";
        internal const string CardDisplayScope = "world.carddisplay";

        internal static Guid Predict(string scope, WorldMessage intent, Action apply, Action undo,
            bool applyLocally = true)
            => PredictionApi.Predict(scope,
                predictionId => WorldClientBehaviour.SendClientIntent(intent, predictionId),
                apply, undo, applyLocally);

        internal static void ApplyAuthoritative(WorldMessage message, Action apply)
            => PredictionApi.ApplyAuthoritative(message?.PredictionId ?? Guid.Empty, apply);

        /// <summary>Applies a world result that confirms the predicted action, retiring the
        /// prediction without rolling it back first. See <see cref="PredictionApi.ApplyConfirmed"/>.</summary>
        internal static void ApplyConfirmed(WorldMessage message, Action apply)
            => PredictionApi.ApplyConfirmed(message?.PredictionId ?? Guid.Empty, apply);
    }
}
