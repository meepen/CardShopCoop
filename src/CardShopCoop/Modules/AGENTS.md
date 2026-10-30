# Module development

Modules are feature boundaries. Use one independent namespace and folder per feature, following
the existing `Modules/Light`, `Modules/GameTime`, and `Modules/Npc` modules. A feature may use the
conventional `Messages`, `Interop`, `HostBehaviour`, and `ClientBehaviour` files/folders as
needed; not every feature needs every file.

## Conventions and lifecycle

- Mark network DTOs with `[NetworkMessage]` and behaviour classes with `[ServerBehaviour]` or
  `[ClientBehaviour]`; use `[OnFullyJoined]` for late-join initialization where appropriate.
  These attributes and the message contract come from the `CardShopCoop.Api` assembly.
- Behaviours are discovered automatically by `CoopBehaviourRegistry`; do not add manual catalog
  registration. The registry binds every attributed handler before `OnEnable` and unbinds it on
  disposal. There is no manual handler-registration API; never add registration calls. Keep
  Harmony patches, subscriptions, and network resources owned by the module, and make
  unpatch/shutdown idempotent.
- Built-in modules are keyed by their `CardShopCoop.Modules.*` namespace for the enable/disable
  checkbox; an external mod is keyed by its assembly name (`ModuleCatalog.BindExternal`). Keep
  feature DTOs, hooks, and state inside the module. Put only generic infrastructure outside
  it, and expose narrow, intentional cross-module APIs rather than reaching into another module's
  internals.
- **Replicated object lifecycle belongs to `Modules/GameObjectIdentity`.** Host creation/removal
  hooks, entity identity allocation/binding, spawn/despawn messages, join lifecycle baselines, and
  per-kind object↔identity maps must not be added to a feature module. Feature modules keep only
  state, animation, action prediction/resolution, holds, contents, inventory, UI, and gameplay
  updates. A feature
  module that needs to create/remove a replicated object calls the identity adapter/lifecycle API;
  it does not broadcast its own spawn/despawn message.
- **Feature messages carry live Unity object references, not entity IDs.** A message outside
  `Modules/GameObjectIdentity` must use `Customer`, `Worker`, `InteractableObject`, or the concrete
  live type. If the reference resolves to null, log and drop the message. Correlation IDs, data
  indices, UI indices, and enums are not entity references and may remain.
- **Warehouse entries are not all entities.** A live warehouse box is a Unity
  `InteractablePackagingBox`; a stored stack entry is a value in a `ShelfCompartment` and has no
  live object. Address one with `WarehouseShelfBoxRef` (live `WarehouseShelf` + compartment index +
  slot hint), never a fake GameObject, a POCO entity, or a raw Guid field. `ItemType`/`IsBig` travel
  once per `WarehouseCompartmentState` header and `Amount` is the mutable per-entry value.
  `Modules/GameObjectIdentity` owns the Guid/location registry and the wire converter; feature code
  must not keep its own warehouse identity map.

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
- Send through `ICoopContext.Send`, `ICoopContext.Broadcast`,
  `ICoopContext.BroadcastExcept`, or `ICoopPeer.Send`. `CoopCore.SendMessage` is the single
  internal send path. Never pass or expose a connection id; address a peer by the `ICoopPeer`
  object. A send failure is a connection failure, not feature-level backpressure.
- `ActionMessage` is for a predicted, validated, undoable command. `INetMessage` is for one-way,
  non-predicted messages and continuous state. A discrete world interaction another player can
  interfere with is an `ActionMessage`.
- Send one baseline when a peer joins. If a local scene object is not ready, retain only the latest
  authoritative state and apply it from that object's lifecycle hook; do not poll or add a network
  readiness handshake.
- Normal play uses the smallest useful authoritative delta; full snapshots are join baselines only.
- Clients act immediately through the game's own path. A client command derives from
  `ActionMessage`; `Context.Send` fills the one `ActionId` and records it for the response. The
  host handler always applies the command itself and returns `ActionOutcome.Applied(response)` or
  `ActionOutcome.Reject(reason, response)`, where the response carries the host's post-action state.
  On the client, `OnUndo` reverses the prediction and `OnMerge` applies the response state exactly
  once on every resolution (after `OnUndo` when rejected). Do not use `OnApply`, keys, follower
  replay, or per-module rejection protocols. A predicted action that changes shared state must
  answer with its post-action state on both accept and reject.
- A client action that is still awaiting its host decision owns its local artifact until it
  resolves. Track the artifact by `ActionId` in the module's own map. An older authoritative state
  may arrive first; it must not destroy the pending artifact. The action's `OnMerge` is the
  authoritative settle point.
- A client action that is still awaiting its host decision owns the entity until it resolves. An
  authoritative hold, descriptor, or pose that reaches the client before that decision crossed the
  local action on the wire and describes its older state, so it must not be applied over the
  pending action (for example, a granted box hold must not pull a box the player has already thrown
  back into their hand, and a box descriptor must not re-pose it). Track in-flight local moves by
  their action id and let the action's own echo or rollback settle the state.
- Authoritative host messages are trusted. Client handlers apply them directly without caps,
  normalization, duplicate filtering, sender checks, or malformed-host validation. Authentication
  and resource limits belong to central routing and transport.
- Modules must not introduce central DTO allowlists or module-specific exceptions into generic
  network code.
- Entity-bearing DTO fields are main-thread only. A message that references an entity that already
  exists may carry a raw `Customer`/`Worker`/`InteractableObject` field; the global wire converter
  maps it to the identity module's stable id. A message that creates or removes an entity belongs to
  `GameObjectIdentity` and carries a polymorphic `EntityDescriptor`, never a live object and never a
  second identity. A reference handler treats a null object as the normal unknown-entity drop.
- Extended-information types must be concrete `ExtendGameObjectInformation<T>` subclasses whose
  owner is an NPC, packaging box, decoration, or placed `InteractableObject`. Global values are
  classes marked `[GlobalInformation]` and use the same catalog. They join the session handshake
  catalog; never add a string type name to the wire.
- Verify game interactions against both required live baselines: the Steam public build and the
  Xbox / Game Pass build (regenerating or diffing baselines is covered by the `decompile` skill;
  the `local-install` skill lists the current decompiled directories for this PC). Anything
  present in only one build must be accessed by reflection (use the established
  `WarehouseBoxSync.Probe` pattern); never directly reference such types.
- During migrations, delete feature-specific legacy code after the new module owns the flow,
  including relevant `Sync`, `Patches/GamePatches.cs`, `CoopCore*`, and generic `Net/Messages`
  portions when no longer needed. Do not leave duplicate active paths.
- Follow repository versioning rules: bump only `CardShopCoopVersion` in `Directory.Build.props`.
  Minor is reserved for join-handshake changes; new or changed network messages (and their
  content) are a patch - the handshake checks the message catalog at join time. Major only for a
  deliberate breaking protocol transition. Update `CHANGELOG.md` only as required by the root
  instructions; describe player-visible behavior and compatibility/safety notes, not internals.
