using System;
using CardShopCoop.Net.Connection;

namespace CardShopCoop.Modules.SaveTransfer
{
    /// <summary>
    /// Narrow Core-facing seam. Authentication, Welcome, and connection transitions stay in Core;
    /// all save-transfer state and data ownership stays in this module.
    /// </summary>
    public static class SaveTransferApi
    {
        public static int CoopSlot => SaveTransferStorage.CoopSlot;
        public static int HostSnapshotSlot => SaveTransferStorage.HostSnapshotSlot;
        public static bool ClientReloading => SaveTransferRuntime.ClientReloading;
        public static bool PreloadHold => SaveTransferRuntime.ClientReloading;
        public static bool GuestBorrowedWorld => SaveTransferRuntime.GuestBorrowedWorld;
        public static void SetGuestBorrowedWorld(bool borrowed)
            => SaveTransferRuntime.SetGuestBorrowedWorld(borrowed);

        public static void ClearBorrowedWorldIfSafe(bool inGame)
            => SaveTransferRuntime.ClearBorrowedWorldIfSafe(inGame);

        public static void ConfigureCore(Func<CoopRole> role = null,
            Func<int> generation = null, Func<int, bool> isGenerationCurrent = null,
            Func<Action, bool> enqueueMainThread = null, object transferLock = null,
            Action fullyJoinedEmitter = null, Action<string> failureReporter = null,
            Action<PeerConnection, string> transferFailure = null)
            => SaveTransferRuntime.ConfigureCore(role, generation, isGenerationCurrent,
                enqueueMainThread, transferLock, fullyJoinedEmitter, failureReporter,
                transferFailure);

        public static bool TryAuthorizeAndSend(PeerConnection target,
            Func<SaveTransferOffer, bool> sendHandshakeOffer, Action afterOfferQueued = null)
            => SaveTransferHostBehaviour.TryAuthorizeAndSend(target, sendHandshakeOffer,
                afterOfferQueued);

        public static bool TryBeginClientTransfer(int saveLength, int bundleLength,
            bool sidecarsComplete, string sidecarWarning)
            => SaveTransferClientBehaviour.TryBeginClientTransfer(saveLength, bundleLength,
                sidecarsComplete, sidecarWarning);

        internal static bool TryConsumeHostTransferAcknowledgement(PeerConnection connection)
            => SaveTransferHostBehaviour.TryConsumeTransferAcknowledgement(connection);

        internal static bool TryConsumeClientTransferAcknowledgement(PeerConnection connection)
            => SaveTransferClientBehaviour.TryConsumeFullyJoinedAcknowledgement(connection);

        public static byte[] BuildHostPayload() => SaveTransferStorage.BuildHostPayload();
        public static void ForceLoadSlot(int slot) => SaveTransferStorage.ForceLoadSlot(slot);
    }
}
