using System;
using System.Collections.Generic;
using CardShopCoop.Net.Messages;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// The graded-returns card box's non-possession op: OPEN/COLLECT. Possession itself
    /// is owned by <see cref="BoxEngine"/>; this only handles the authoritative collect
    /// recipe the host must run (AddCard + achievements + despawn).
    /// </summary>
    public static class CardBoxOps
    {
        public static Func<InteractablePackagingBox_Card, bool> IsLocallyCarried = _ => false;
        public static Action<BoxCollectMessage> SendCollect;
        public static Action<int, BoxCollectResultMessage> SendResult;
        public static bool ApplyingRemote;

        public static void ApplyPatches(Harmony h)
        {
            Try(h, typeof(InteractablePackagingBox_Card), "OnPressOpenBox",
                prefix: new HarmonyMethod(typeof(CardBoxOps), nameof(OpenBoxPrefix)));
            Try(h, typeof(InteractablePackagingBox_Card), "OnDestroyed",
                prefix: new HarmonyMethod(typeof(CardBoxOps), nameof(DestroyedPrefix)));
        }

        public static bool OpenBoxPrefix(InteractablePackagingBox_Card __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            try
            {
                ClientCollect(__instance);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxOps collect: " + e.Message); }
            return false; // never vanilla on the client
        }

        public static bool DestroyedPrefix(InteractablePackagingBox_Card __instance)
        {
            // world-(re)load cleanup destroys are not player actions; a collect already
            // told the host and let the mirror retire via the absent-id sweep.
            return true;
        }

        public static void ClientCollect(InteractablePackagingBox_Card box)
        {
            if (box == null || SendCollect == null)
                return;
            var engine = CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;
            if (engine == null || !engine.TryGetClientId(box, out ushort id))
                return;
            var cards = SafeCards(box);
            int hash = HashCards(cards);
            SendCollect(new BoxCollectMessage
            {
                Id = id,
                CardCount = (byte)Mathf.Min(cards.Count, 255),
                CardsHash = hash,
            });
            // The host mints the cards (CardDelta mirrors them) and retires the box; the
            // absent-id sweep removes this mirror on the next snapshot.
        }

        public static void HostApplyCollect(BoxCollectMessage msg, int connId)
        {
            if (CoopCore.Role != CoopRole.Host || msg == null)
                return;
            var engine = CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;
            if (engine == null || !engine.TryGetHostBox(msg.Id, out var baseBox)
                || !(baseBox is InteractablePackagingBox_Card box))
            {
                SendResult?.Invoke(connId, new BoxCollectResultMessage { Id = msg.Id });
                return;
            }
            if (IsLocallyCarried(box))
                return; // host holds it: he opens it himself
            if (engine.HostBoxHeldByOther(msg.Id, connId))
            {
                CoopPlugin.Log.LogInfo($"CardBoxOps: collect rejected connId={connId} id={msg.Id} (held by another connection)");
                SendResult?.Invoke(connId, new BoxCollectResultMessage { Id = msg.Id });
                return;
            }
            var cards = SafeCards(box);
            if (HashCards(cards) != msg.CardsHash)
            {
                SendResult?.Invoke(connId, new BoxCollectResultMessage { Id = msg.Id });
                return;
            }
            CoopPlugin.Log.LogInfo($"CardBoxOps: collect accepted connId={connId} id={msg.Id}");
            try
            {
                for (int i = 0; i < cards.Count; i++)
                {
                    if (cards[i] == null)
                        continue;
                    if (!CoopCore.CardSetInstalledHere(cards[i]))
                    {
                        CoopCore.WarnRefusedCard(cards[i], "card-box");
                        continue;
                    }
                    if (cards[i].cardGrade > 10 && Util.GradingInterop.Present)
                        Util.GradingInterop.Remember(cards[i]);
                    CPlayerData.AddCard(cards[i], 1);
                    if (Util.GradingInterop.Actual(cards[i].cardGrade) == 10)
                        CPlayerData.m_GameReportDataCollectPermanent.gemMintCardObtained++;
                }
                AchievementManager.OnCheckGemMintCardCount(CPlayerData.m_GameReportDataCollectPermanent.gemMintCardObtained);
                AchievementManager.OnCheckCollectedGradedCardSet();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxOps collect apply: " + e.Message); }

            ApplyingRemote = true;
            try
            {
                box.OnDestroyed();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("CardBoxOps collect despawn: " + e.Message); }
            finally { ApplyingRemote = false; }
            engine.ForceNextTick();
        }

        public static void ClientApplyResult(BoxCollectResultMessage msg)
        {
            if (msg == null || msg.Accepted)
                return;
            // Rejected: the host kept the box, so the still-live mirror is correct as-is.
            CoopPlugin.Log.LogInfo($"CardBoxOps: collect rejected for box id {msg.Id}");
        }

        private static List<CardData> SafeCards(InteractablePackagingBox_Card box)
        {
            try
            {
                return box.GetCardDataList() ?? new List<CardData>();
            }
            catch (System.Exception e) { Swallow.Log(e); return new List<CardData>(); }
        }

        private static int HashCards(List<CardData> cards)
        {
            int h = 17;
            if (cards == null)
                return h;
            h = h * 31 + cards.Count;
            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                if (c == null)
                    continue;
                h = h * 31 + (int)c.monsterType;
                h = h * 31 + (int)c.expansionType;
                h = h * 31 + (c.isFoil ? 1 : 0);
            }
            return h;
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
    }
}
