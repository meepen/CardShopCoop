# NPC Module Structure

This document describes the structure and responsibilities of the NPC module in
`src/CardShopCoop/Modules/Npc`. It is intended as a reference for restructuring
other modules to follow the same shape.

## Files and responsibilities

```text
Modules/Npc/
├── NpcHostBehaviour.cs    Host-only collection, authority, and Harmony hooks
├── NpcClientBehaviour.cs   Client-only suppression, puppets, interpolation, and handlers
└── NpcMessages.cs          Shared network message and DTO definitions
```

The module is role-separated at the type level:

- `NpcHostBehaviour` is marked `[ServerBehaviour]`.
- `NpcClientBehaviour` is marked `[ClientBehaviour]`.
- `NpcMessages.cs` contains no Unity behaviour and is available to both sides.

The runtime behaviour registry creates only the behaviour appropriate to the
current co-op role. The two behaviours derive from `CoopBehaviour`, which gives
them access to the `CoopRuntimeContext` and the Unity lifecycle.

## Overall data flow

```text
Host game NPCs
    │
    ▼
NpcHostBehaviour.HostCollect
    │  bounded collection, change/name tracking, chunking
    ▼
NpcStateMessage (transient)
    │
    ▼
NpcClientBehaviour.HandleState / ApplyBatch
    │
    ├── update existing customer mirrors
    ├── create/update visual puppets
    ├── apply animation and prop state
    └── interpolate movement in Update

Host popup hooks ──► NpcSpeechMessage / NpcMoneyPopupMessage ──► client popup handlers
```

The host remains authoritative. Clients do not simulate customer or worker AI;
they render snapshots and cosmetic events received from the host.

## Host side: `NpcHostBehaviour`

### Lifecycle

`OnEnable` performs one-time setup:

1. Captures `RuntimeContext` and sets the static `_active` instance.
2. Creates a module-specific Harmony instance.
3. Installs the worker-action, speech-popup, and money-popup patches.

`Update` exits unless the session is in-game. It calls `HostCollect(Time.deltaTime)`
and broadcasts every returned chunk through `BroadcastTransient`.

`Shutdown` is idempotent. It clears `_active` when appropriate and unpatches
the module's Harmony instance. `ResetState` clears collection, identity, pending
chunk, and manager state when the co-op session or game state is reset.

### Collection and scheduling

`HostCollect` is the host's public collection boundary. It:

- maintains a 0.125 second send beat;
- periodically clears name-send tracking every five seconds so unreliable
  delivery and late joins eventually receive names again;
- samples customers and workers incrementally rather than scanning the whole
  crowd in one frame;
- uses a cursor over the combined customer/worker slots;
- accrues fractional collection credit based on `deltaTime`;
- caps both accumulated credit and per-frame work;
- flushes accumulated entries into size-bounded `NpcStateMessage` chunks only
  when the send beat is due.

This is a gradual snapshot stream, not an unbounded full-crowd scan every frame.
The collection cap intentionally stretches recovery after a hitch instead of
creating another large hitch.

### Host state and identity

The host keeps two dictionaries:

- `_npcStates`: authoritative per-slot bookkeeping, including active state,
  pooled-object generation, customer grab sequence, and worker action sequence.
- `_sentStates`: last name and identity sent for each `(kind, index)` pair.

`NpcKey` combines the NPC kind and list index. There are two kinds:

```text
KindCustomer = 0
KindWorker   = 1
```

The `Generation`/`Identity` value distinguishes a newly pooled NPC from the
previous NPC that occupied the same list slot. This prevents delayed unreliable
packets from dressing or animating the wrong incarnation.

`GetCustomerGeneration` and `TryGetCustomerIdentity` expose this identity to
other systems, particularly register and trade interactions that need to bind a
real customer carrier to the correct visual customer.

### Snapshot contents

Customer collection records:

- active state and generation;
- character name;
- transform position and yaw;
- movement speed;
- animator flags;
- smell and trade-exclamation visual state;
- grab/action sequence and action kind.

Worker collection records the same visual state plus:

- female-prefab selection;
- held-box state;
- held-box size and item type;
- worker action sequence and action kind.

Animator parameter names are converted to cached hashes once. The private worker
hold-item field is accessed through reflection, and a clear error is logged once
if that field is unavailable.

### Chunking

`BeginChunk`, `WriteEntry`, and `FlushChunk` build the wire payload:

- each chunk carries one host timestamp;
- entries are capped at 255 per chunk;
- the JSON-serialized length is measured using `WireCodec`;
- entries are moved to a new chunk before the soft payload limit is exceeded;
- one oversized entry is still sent intact.

The pending chunk list is swapped with a reusable output list after each send
beat, avoiding unnecessary list allocation while the host broadcasts.

### Host Harmony hooks

The nested patches are kept in the host behaviour because they produce
authoritative events:

- `WorkerActionPatch` records a worker action sequence after
  `Worker.PlayWorkerActionAnim`.
- `SpeechPatch` finds the active price-popup object and broadcasts a
  `NpcSpeechMessage` bound to the customer's index and generation.
- `MoneyPatch` broadcasts a `NpcMoneyPopupMessage` for positive price popups.

The hooks resolve the target NPC through the host's authoritative lists rather
than trusting a client-provided object reference.

## Client side: `NpcClientBehaviour`

### Lifecycle and suppression

`OnEnable` captures the runtime context, sets `_active`, registers attributed
message handlers, and installs client Harmony patches. `Shutdown` unregisters
handlers and unpatches Harmony. `Reset` hides and clears mirrors and puppets.

The client patches suppress vanilla NPC simulation while this behaviour is
active:

- `CustomerManager.Update` is blocked;
- `Customer.Update` is blocked;
- `WorkerManager.ActivateWorker` is blocked;
- `Customer.ActivateCustomer` is blocked except when
  `RegisterSync.AllowClientCustomerLifecycle` explicitly permits it.

`Sweep` runs every 0.25 seconds while in-game. It hides unexpected real customer
and worker objects that escaped suppression, while preserving register carrier
customers and tracked mirrors.

### Message handlers

The three handlers are deliberately small routing methods:

- `NpcStateMessage` → `ApplyBatch`;
- `NpcSpeechMessage` → `ShowSpeech`;
- `NpcMoneyPopupMessage` → `ShowMoneyPopup`.

All handlers check the current in-game state before rendering. Missing visual
targets are ignored for cosmetic popup messages; a popup must not keep stale
object references alive.

### Client state containers

The client has two distinct representations:

1. **Puppets** (`_puppets`): inert visual clones used for normal remote NPCs.
2. **Existing customer mirrors** (`_existing`): real pooled customer objects
   temporarily used as interactive register/trade carriers.

Both representations maintain a small ring buffer of timestamped snapshots,
last-seen time, identity, animation state, and visual flags. The separate mirror
path lets a carrier remain interactive while its matching puppet stays buffered
and ready to reveal when the interaction ends.

`SuppressedCustomer` records customer indices whose puppet must remain hidden
because a real carrier is currently rendering that customer.

### Applying a state batch

`ApplyBatch` performs these operations for each entry:

1. Maps host time to the client's local clock. The offset is initialized or
   re-based for large jumps, otherwise low-pass filtered to absorb jitter.
2. Ignores rendering while outside the game.
3. Rejects duplicate, reordered, or older-generation packets before mutating
   buffers, timestamps, flags, or animations.
4. Routes customer entries to an existing carrier mirror when one is attached.
5. Otherwise creates or updates a keyed puppet.
6. Re-dresses on name changes and recreates the object when the gender prefab
   changes.
7. Adds the position/yaw snapshot to the ring buffer.
8. Applies action sequence changes as animation triggers.
9. Updates last-seen time and carrier suppression visibility.

Names are sent only when changed or during the host's periodic name refresh.
This reduces bandwidth while still allowing a missing name packet to repair
itself.

### Puppet creation and safety

`Spawn` clones a fully initialized live pooled customer where possible. Workers
prefer the live worker instance so appearance-mod replacements, sockets, and
animators survive; customers prefer a live pooled customer over a template
prefab because the live object has the correct wardrobe data.

The clone is made inert:

- AI, navigation, interaction, and physics behaviours are disabled;
- colliders are disabled;
- rigidbodies become kinematic and stop detecting collisions;
- only the worker interaction surface needed to open the normal worker UI is
  retained;
- visual references such as bags, cash, cards, smell, exclamation markers, and
  worker hold anchors are captured before stripping.

The `CharacterCustomization` component remains enabled so `ReDress` can apply a
new wardrobe in place. Worker UI data is refreshed from authoritative staff save
data after the puppet has been registered, ensuring UI calls can find the puppet.

Worker box visuals are cosmetic clones only. The synchronized gameplay box is
never attached to a puppet; `SetWorkerBoxVisual` creates or releases a stripped
visual prop based on the snapshot's size and item type.

### Rendering and timeout behaviour

`TickPuppets` advances the client clock only while in-game, then renders at an
interpolation delay of 0.15 seconds. It uses:

- ring-buffer interpolation between bracketing snapshots;
- capped, exponentially decaying extrapolation when the buffer is dry;
- frame-rate-independent exponential smoothing for position, yaw, and animation
  speed;
- teleport detection for large position differences;
- a six-second last-seen timeout for puppets and mirrors.

The generous timeout is intentional because NPC state uses an unreliable,
transient channel. A short timeout would make an entire crowd blink during a
brief packet-loss burst.

## Shared wire contract: `NpcMessages.cs`

`NpcMessages.cs` is the sole shared DTO layer for this module.

### `NpcStateMessage`

Host-to-client, client-only, transient message. It contains `HostTime` and a
list of `NpcEntry` values. Each instance represents one size-bounded chunk from
`HostCollect`.

### `NpcEntry`

One NPC snapshot containing:

```text
Kind, Index, Identity
HasName, CharName
Position, Yaw, Speed
Flags
ActionSequence, ActionKind
HoldBig, HoldItemType
```

`Flags` is the wire byte for the private client-side `NpcFlags` enum. The host
and client must keep its bit assignments synchronized.

### `NpcSpeechMessage`

Host-to-client reliable one-shot message. It identifies a customer by kind,
index, and generation, then carries popup text and vertical offset.

### `NpcMoneyPopupMessage`

Host-to-client reliable one-shot message. It identifies a customer by index and
generation and carries the amount and vertical offset.

The message classes use `[NetworkMessage]` attributes for routing and implement
`INetMessage`. The DTOs contain no collection, rendering,
or Unity lifecycle logic beyond the `Vector3` value used by the snapshot.

## Integration points

Other systems use the NPC module through narrow static methods rather than
reaching into its private collections:

- `RegisterSync` adds/removes suppressed customers and attaches/detaches real
  customer carriers.
- `TradeServe` uses the same carrier/mirror API for trade interactions.
- `StaffSync` obtains worker puppets and refreshes their UI data.
- `CoopCore` resets both behaviours during global session/state reset and uses
  their diagnostic counters for logging.

These integrations preserve ownership boundaries: register/trade systems own
the interaction workflow, while the NPC client behaviour owns visual handoff,
suppression, buffering, and puppet lifetime.

## Structure to copy when restructuring another module

Use the NPC module as the target shape:

1. Put host authority and host-only Harmony hooks in a `[ServerBehaviour]` class.
2. Put client rendering, suppression, and message handlers in a
   `[ClientBehaviour]` class.
3. Put all shared network DTOs in a dedicated messages file.
4. Register and unregister handlers in the behaviour lifecycle.
5. Make reset and shutdown idempotent, and unpatch the module's Harmony instance.
6. Keep the wire DTO layer free of module state and orchestration.
7. Give pooled/reused game objects an explicit identity or generation, not just
   a list index.
8. Separate authoritative gameplay objects from inert client visuals.
9. Bound per-frame collection and packet sizes; use transient state snapshots for
   high-frequency movement and reliable one-shot messages for cosmetic events.
10. Expose narrow integration methods for other modules instead of sharing
    private dictionaries or lifecycle details.
