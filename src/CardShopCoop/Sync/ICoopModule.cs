using System;
using CardShopCoop.Net;

namespace CardShopCoop.Sync
{
    /// <summary>Common lifecycle contract for a session-owned co-op subsystem.
    /// The registry owns these calls; per-frame ordering lives in <see cref="ITickableCoopModule"/>.</summary>
    public interface ICoopModule : IDisposable
    {
        string Name
        {
            get;
        }

        /// <summary>Called when the module becomes part of a live session.</summary>
        void Start();
        void OnConnect(Connection connection);
        void OnFullyJoined(Connection connection);
        void OnDisconnect(Connection connection, DisconnectInfo info);

        /// <summary>Clears state associated with the current world or save.</summary>
        void ResetState();

        /// <summary>Invalidates change gates so the next host tick sends a baseline.</summary>
        void ForceResend();

        /// <summary>Gradual, spread-out maintenance, called once per co-op frame for every module
        /// in the per-frame tick pipeline (not role-gated, not in-game-gated - the module must
        /// guard itself). A module that wants to guarantee eventual correctness re-asserts ONE
        /// slice of its state here (round-robin), never a full resend. A full periodic resend
        /// costs a burst of serialization/allocation and a bandwidth spike across every client at
        /// once; that is exactly what this hook exists to avoid. See AGENTS.md
        /// ("Sync scheduling: no periodic full resends").</summary>
        void PeriodicUpdate(float delta);

        /// <summary>Host: send this module's COMPLETE state to one connection. This is the
        /// catch-up / re-baseline path (a peer that just finished joining, or an explicit
        /// resync), never a periodic one. Runs after <see cref="ForceResend"/>, so a module can
        /// rely on its baseline having been armed first.</summary>
        void FullUpdate(Connection connection);
    }

    /// <summary>A module that participates in the per-frame co-op sync pipeline. CoopCore
    /// declares the host/client order explicitly because the two roles need different
    /// sequences (market flush first on the client, module pipelines on the host).</summary>
    public interface ITickableCoopModule : ICoopModule
    {
        /// <summary>Runs once per co-op frame.</summary>
        void Tick(in SyncFrame frame);
    }
}
