# DTO entity references

**Decision:** network DTOs may carry live `Customer`, `Worker`, and `InteractableObject` fields
for messages that reference an entity that already exists. The wire carries the identity module's
existing stable identity; the global converters translate between the live object and that
identity. Existing identity spaces are unchanged.

This is deliberately **not** a canonical-Guid migration. NPC identity remains
`(kind, pool index, incarnation)`, placement identity remains the packed placement key, and
packaging boxes keep their Guid. Game-save indices and type enums are not entity identities.

## Message taxonomy

- **Reference messages** target an object that must already exist on the receiver. They carry the
  live Unity object field only; there is no entity-ID fallback outside `GameObjectIdentity`. A
  message whose object is null/unresolved is dropped by its handler (an ordinary unknown-entity
  branch, not a protocol error).
- **Lifecycle messages** create or remove an entity, or may arrive before the receiver has it.
  They are owned by `GameObjectIdentity` and carry a polymorphic `EntityDescriptor`; they never
  rely on a live object field.
- **Correlation IDs** such as `ActionId`, baseline ids, card/data indices, and UI list indices
  are not entity identities and stay unchanged.

## Wire conversion

`Net/WireSettings.cs` registers the global converters:

- `GameObjectEntityJsonConverter` for `Customer`, `Worker`, and every `InteractableObject`
  subclass.
- `GameObjectIdJsonConverter` for the structured `GameObjectId` value.
- `EntityDescriptorJsonConverter` for the polymorphic lifecycle descriptor hierarchy.
- `ExtendedInformationValueJsonConverter` for typed extended-information payloads.
- `PlayerRefJsonConverter` for a session-player reference. The wire carries the host-canonical
  player id and the sender's name snapshot; `IsHost` is derived from id 0 and `IsSelf` is local.

`Modules/GameObjectIdentity/GameObjectIdentityCache` is the single object/identity cache. The
converters ask it for an object's ID on write and ask it to resolve an ID on read. Per-kind
adapters live in `Modules/GameObjectIdentity/GameObjectIdentityNpc.cs`,
`GameObjectIdentityPlacement.cs`, `GameObjectIdentityBox.cs`, and
`GameObjectIdentityDecoration.cs`; they derive/resolve the existing identity for their game type and
do not own a second cache.

An unresolved object writes `null`. A well-formed identity string that resolves to no live object
reads as `null`. A malformed identity (wrong JSON shape) fails the frame; an identity that is
well-formed but invalid (for example `kind: 0`) reads as the default `GameObjectId`, and an entity
field then maps that default to `null`. Callers never receive a half-valid entity.

`PlayerRef` is the player identity for the wire: id 0 is the host, 1..N are clients in the ids the
host assigned. The sender's name snapshot travels with the id; `IsHost` is derived from id 0 and
`IsSelf` is computed locally, so a reference is usable before the roster syncs. `PeerConnection` is
transport only: its id is a local view (a client calls the host 1; the host calls its first client
1), so it must not be used as a shared player identity.

## Lifecycle ownership

`Modules/GameObjectIdentity` is the only owner of replicated object lifecycle:

- creation/removal binding and descriptors
- `EntityAddedMessage` / `EntityRemovedMessage`
- `EntityBaselineMessage`
- per-kind host Harmony hooks
- per-kind client materialisation/destruction
- the single object/identity cache

Feature modules keep state, animation, action replay, holds, contents, inventory, UI, and other
gameplay updates. They do not carry spawn/despawn messages, join lifecycle baselines, identity
allocators, object↔identity maps, or creation/removal Harmony hooks.

Lifecycle descriptors are polymorphic. `EntityDescriptor` is the base; the concrete types are
`NpcEntityDescriptor`, `PlacementEntityDescriptor`, `DecorationEntityDescriptor`, and
`PackagingBoxEntityDescriptor`. Each carries only the fields its object kind actually has.
`EntityRemovedMessage.Replacement` is polymorphic, so a box-up/unbox or decoration move is one
atomic remove-and-replace.

## Threading

`JsonConverter.ReadJson` resolves live scene objects, so application JSON decoding runs on the
Unity main thread. The KCP framing worker only validates the envelope and payload slice and returns
a `WireFrame`; `KcpSessionManager.TryDequeueIncoming` deserializes it on the caller (Unity) thread.
`CoopCore` interleaves one decode and one dispatch at a time so a spawn handler binds an entity
before the next message that references it is decoded.

`LagTransport` delays `WireFrame`s, not decoded DTOs, and never re-encodes a message for byte
accounting. Entity-bearing DTOs must be constructed and sent on the Unity thread;
`MainThreadGuard` enforces both boundaries.

## Warehouse entries

Warehouse state is deliberately split between a live entity and a stored value:

- A **live warehouse box** is an `InteractablePackagingBox` and uses the normal packaging-box
  identity/lifecycle.
- A **stored warehouse entry** is a value in a `ShelfCompartment`. It has no live object, no POCO
  entity, and no fake GameObject. It is addressed by `WarehouseShelfBoxRef`: the live
  `WarehouseShelf`, the `CompartmentIndex`, and a slot hint.
- `WarehouseCompartmentState` carries `ItemType` and `IsBig` once for the whole compartment.
  `WarehouseBoxEntry.Amount` is the mutable value; `WarehouseBoxEntry.Box` is only an optional live
  representation and is null for record-backed entries.
- `WarehouseStoreMessage` requires a live shelf and live box. `WarehouseTakeMessage.Source` names the
  stored top entry and `WarehouseTakeMessage.CreatedBox` is the allowed creation payload for the
  local live box.
- `WarehouseDeltaMessage` is authoritative state, not an intent, and its `Box` may be null for a
  record-backed or worker take.

`WarehouseShelfBoxRef` has no public Guid property. `WarehouseShelfBoxRefJsonConverter` serializes
the live shelf through the entity converter plus the ints, and internally reads/writes the Guid from
`Modules/GameObjectIdentity/WarehouseBoxLocations`. That registry is the only warehouse
Guid/location map:

- Guid -> live `InteractablePackagingBox` or stored `(WarehouseShelf, CompartmentIndex, Slot)`
- `(WarehouseShelf, CompartmentIndex, Slot)` -> Guid

It stores identity/location only, never item contents. `GameObjectIdentityCache` remains the one
live object/identity cache; stored entries are never bound there. An unresolved shelf reference is
deferred and retried on the world-ready/placement-structure signals.

The record backend keeps stored values as game records. After every baseline, store, remote apply,
or take it re-runs the 1.02 amount-descending canonical order (newest insertion first on ties) so
the top entry and insertion slot match the live-box backend. Takes are always top pops.

## Extended information

Modules can attach typed data to an object, or to the session as a whole, without changing the DTO
wire shape:

```csharp
public sealed class InteractableObjectColor
    : ExtendGameObjectInformation<InteractableObject>
{
    public byte R, G, B;
}

var color = shelf.GetExtendedInformation<InteractableObjectColor>();
shelf.SetExtendedInformation(new InteractableObjectColor { R = 1, G = 2, B = 3 });
shelf.RemoveExtendedInformation<InteractableObjectColor>();
```

The store is keyed by `(GameObjectId, information type)`. The owner must be an entity the identity
module replicates: an NPC, a packaging box, or an `InteractableObject` placed through the game's
shelf lists (decorations included). `Get`/`Set`/`Remove` throw `ArgumentException` for any other
type, and an entity with no identity is logged when the operation is attempted.

`T` must be the value's concrete type. Passing a base type (for example
`ColorInfo info = new RgbColorInfo()` followed by `SetExtendedInformation(info)`) throws: the
entry is keyed and serialized by the declared type, so the derived fields would exist locally and
be missing on every other peer. See the worked example in
`docs/third-party-integration.md`.

### Global information

A value that belongs to the session rather than to an object is a class marked
`[GlobalInformation]`. It uses the same store, value codec, authority flow and catalog, keyed by
`GameObjectId.Global()` and the information type:

```csharp
[GlobalInformation]
public sealed class ShopTaxRate
{
    public int Percent { get; init; }
}

Global<ShopTaxRate>.Value = new ShopTaxRate { Percent = 5 };
var tax = Global<ShopTaxRate>.Value;
Global<ShopTaxRate>.Value = null;
```

`Global<T>.Value` is the only public read/write surface (the `CoopApi` global methods are internal
plumbing); `Mutate` is read-modify-publish. Keep the value immutable: `Value` returns the stored
object, and a nested field write cannot be observed. `[GlobalInformationHandler]` takes the value as
its only parameter and receives null on removal; reactions do not belong in an `OnUpdate` on the
value. `GameObjectIdentityCache.Reset` (scene rebuild) removes object entries but keeps globals; a
session end clears both.

### Session catalog

Type keys are `FullName + ", " + assembly name`. At session start both peers exchange the exact,
ordinally sorted key list in the handshake (`HelloMessage.ExtendedInformationCatalog` /
`WelcomeMessage.ExtendedInformationCatalog`) and refuse the join on any difference. Messages then
carry the zero-based index of that order as a `ushort` (`ExtendedInformationValue.TypeId`,
`ExtendedInformationRemovedMessage.TypeId`), exactly like the message catalog's compact ids. The
key remains the identity; the index is session-local shorthand.

The catalog is discovered from the core assembly and every integrated external mod assembly before
the session freezes it. A type first registered during a session applies from the next session, and
a send for a type outside the frozen catalog throws rather than putting an unusable id on the wire.
An incoming id the build does not know is logged once and its entries are dropped; it never breaks
the transport frame.

Extended-information values travel in normal wire messages through the same `WireSettings`
serializer as every DTO. Gated enums use `CatalogIdMap`, vectors are compacted, entity fields use
`GameObjectId`, unknown fields are rejected during decode, and the normal message size/framing
policy applies (there is no separate extended-information size limiter).

The host applies and broadcasts `ExtendedInformationSetMessage` /
`ExtendedInformationRemovedMessage`; a client sends the same operation as an intent and applies
the host echo. A join baseline carries every current entry. Entries are removed when the identity
cache unbinds the object (including a dead scene object discovered during a resolve) or when the
identity module publishes a permanent removal; a stored warehouse transition keeps the same
`GameObjectId` and its information.

A handler parameter may be the raw value or `InformationChange<T>`:

- raw value: the current value, null on removal; no previous value.
- `InformationChange<T>`: `Previous`/`Current`; `Previous` is null for the first value and a join
  baseline; `Current` is null on removal. `Removed` and `IsFirstValue` express those cases.

The same two forms apply to `[GlobalInformationHandler]`. Use the change form when the handler needs
to diff, do a full apply on the baseline, or clean up from the old value.

## Handler rule

A reference-message handler receives the live entity, or null when the entity was removed or could
not be resolved. Null takes the module's ordinary unknown-entity path (drop that update); it is not
a protocol error and must not be retried as one. Lifecycle is never handled by feature modules:
`GameObjectIdentity` creates and destroys the object before any reference message is decoded.
