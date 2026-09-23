# Module development

Modules are feature boundaries. Use one independent namespace and folder per feature, following
the existing `Modules/Light`, `Modules/GameTime`, and `Modules/Npc` modules. A feature may use the
conventional `Messages`, `Interop`, `HostBehaviour`, and `ClientBehaviour` files/folders as
needed; not every feature needs every file.

## Conventions and lifecycle

- Mark network DTOs with `[NetworkMessage]` and behaviour classes with `[ServerBehaviour]` or
  `[ClientBehaviour]`; use `[OnFullyJoined]` for late-join initialization where appropriate.
- Behaviours are discovered automatically by `CoopBehaviourRegistry`; do not add manual catalog
  registration. Keep handler registration, Harmony patches, subscriptions, and network resources
  owned by the module, and make unpatch/shutdown idempotent.
- Keep feature DTOs, hooks, and state inside the module. Put only generic infrastructure outside
  it, and expose narrow, intentional cross-module APIs rather than reaching into another module's
  internals.

## Synchronization and compatibility

- Keep modules simple. Reliable ordered delivery belongs to the transport; modules must not add
  delivery ACKs, retries, outboxes, send-capacity schedulers, result-replay ledgers, polling, or
  periodic reconciliation.
- Role ownership is structural. `[ServerBehaviour]` types contain host hooks and handlers;
  `[ClientBehaviour]` types contain client hooks and handlers. Do not branch on runtime role inside
  normal feature behaviours.
- The host is authoritative. A host hook normally broadcasts the resulting state, and a client
  handler applies it. A client hook normally sends one intent before local mutation; the host
  validates and applies it, then broadcasts the resulting state. Add a semantic rejection only
  when gameplay or UI actually needs one.
- Send once through the fail-loud `Send`/`Broadcast` API. A send failure is a connection failure,
  not feature-level backpressure.
- Send one baseline when a peer joins. If a local scene object is not ready, retain only the latest
  authoritative state and apply it from that object's lifecycle hook; do not poll or add a network
  readiness handshake.
- Normal play uses the smallest useful authoritative delta; full snapshots are join baselines only.
- Clients predict interactions immediately. Record the local apply/undo actions with one generic
  `PredictionId`, send the intent before applying the prediction, and reconcile when the accepted
  host delta returns. A rejected intent uses the one generic prediction-rollback message. Do not
  add module-specific rejection protocols, correction snapshots, revisions, or recovery messages.
- Authoritative host messages are trusted. Client handlers apply them directly without caps,
  normalization, duplicate filtering, sender checks, or malformed-host validation. Authentication
  and resource limits belong to central routing and transport.
- Modules must not introduce central DTO allowlists or module-specific exceptions into generic
  network code.
- Verify game interactions against both required baselines: legacy `decompiled\1.0-25315983`
  and beta `decompiled\1.00`. Anything present in only one build must be accessed by reflection
  (use the established `WarehouseBoxSync.Probe` pattern); never directly reference such types.
- During migrations, delete feature-specific legacy code after the new module owns the flow,
  including relevant `Sync`, `Patches/GamePatches.cs`, `CoopCore*`, and generic `Net/Messages`
  portions when no longer needed. Do not leave duplicate active paths.
- Follow repository versioning rules: bump only `CardShopCoopVersion` in `Directory.Build.props`,
  using patch for non-wire changes, minor for message/semantics changes, and major only for a
  deliberate breaking protocol transition. Update `CHANGELOG.md` only as required by the root
  instructions; describe player-visible behavior and compatibility/safety notes, not internals.
