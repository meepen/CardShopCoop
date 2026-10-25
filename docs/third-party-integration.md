# Integrating with CardShopCoopCommunity

CardShopCoopCommunity exposes a small public API, **`CardShopCoop.Api`**, so your mod can add
co-op behaviour without depending on CardShopCoopCommunity's internals. With it you can:

- send typed messages from a host to its joiners, and from a joiner to the host;
- keep a host-authoritative value in sync, including for players who join late;
- attach typed data to a placed object without designing a message;
- send a client command and reconcile it with the host as an action.

The complete, buildable reference is
[`samples/CardShopCoop.SampleMod`](../samples/CardShopCoop.SampleMod). It is a shared counter:
a client intent, a host-authoritative broadcast, and a join baseline.

## 1. Set up

### Reference the API

```xml
<Reference Include="CardShopCoop.Api">
  <HintPath>...\BepInEx\plugins\CardShopCoop\CardShopCoop.Api.dll</HintPath>
  <Private>false</Private>
</Reference>
```

`CardShopCoop.Api.dll` ships in the CardShopCoop plugin folder, in the core release zip, and as the
standalone `CardShopCoop.Api.zip`. **Never ship your own copy** — a second copy can shadow the
installed one and the types will not line up.

### Declare a soft dependency

```csharp
[BepInDependency("dev.meepen.cardshopcoop", BepInDependency.DependencyFlags.SoftDependency)]
```

Soft means your plugin still loads (and does nothing co-op related) when CardShopCoopCommunity is
not installed. It is also how CardShopCoopCommunity discovers your mod: declaring the dependency is
the registration. There is no list to edit and no call to make.

If you cannot declare the dependency, `CoopApi.Register(assembly)` is the explicit alternative.
Call it from `Awake` on the main thread, guard with `CoopApi.IsAvailable` first, and remember that a
late registration only applies from the next session; its `[PersistentBehaviour]`s are not
instantiated at all. Auto-discovery is preferred because BepInEx then guarantees load order.

### Mark your code

- message DTOs: `[NetworkMessage]`
- behaviours: `[ServerBehaviour]`, `[ClientBehaviour]`, or `[PersistentBehaviour]`
- handlers: `[MessageHandler]`, `[ExtendedInformationHandler]`, `[GlobalInformationHandler]`,
  `[IntentValidator]`, `[IntentRejected]`
- lifecycle: `[OnClientJoined]`, `[OnClientDisconnected]`, `[OnFullyJoined]`, `[OnSessionStarted]`,
  `[OnSessionStopped]`

CardShopCoop binds every attributed handler on a behaviour **before `OnEnable`**, when it creates
the behaviour. You do not call anything:

```csharp
[ServerBehaviour]
public sealed class MyHost : CoopBehaviour
{
    private ICoopContext _context;

    private void OnEnable()
    {
        _context = Context;
    }

    [MessageHandler]
    private void OnSomeMessage(CoopMessageContext context, SomeMessage message)
    {
        // already bound
    }
}
```

If you create a component yourself instead of marking it for discovery, bind it explicitly with
`context.Messages.RegisterAttributedHandlers(component)` and unregister it when you destroy it.
A handler on such a component never runs until it is bound; the log warns when a message arrives for
an unbound handler.

**Both players must install your mod, at the same version.** The join compares the mod set and the
message types both sides expose, and refuses the join with a mismatch reason if they differ.

## 2. Your first sync: a host-owned value

The rule is the same for every feature: **the host owns the value; a client sends an intent; the
host applies it, broadcasts the result, and sends one baseline to each player who joins.**

The snippets omit `using` directives. The namespaces are `CardShopCoop.Api`,
`CardShopCoop.Attributes`, `CardShopCoop.Net`, and `CardShopCoop.Net.Connection`; custom transports
also use `CardShopCoop.Net.Networking` and `CardShopCoop.Net.Datagrams`. The sample has the full set.

### The messages

A message is a `[NetworkMessage]` class that implements `INetMessage`, with a parameterless
constructor:

```csharp
[NetworkMessage]
public sealed class ValueChanged : INetMessage
{
    public int Value;
}

[NetworkMessage]
public sealed class AddValue : INetMessage
{
    public int Delta;
}
```

Use simple fields: numbers, strings, bools, enums, `Vector3`/`Quaternion`, and arrays/lists of
those. Renaming or moving a DTO changes the wire contract; adding a field is safe only because both
players run the same build of your mod.

### The host

```csharp
[ServerBehaviour]
public sealed class ValueHost : CoopBehaviour
{
    private ICoopContext _context;
    private int _value;

    private void OnEnable()
    {
        _context = Context;
    }

    private void OnDisable()
    {
        _context = null;
    }

    // One baseline per joining player, once they are fully in game.
    [OnFullyJoined]
    private void SendBaseline(PeerConnection connection)
        => _context.Send(connection, new ValueChanged { Value = _value });

    [MessageHandler]
    private void HandleAdd(CoopMessageContext context, AddValue intent)
    {
        // validate intent.Delta here; the host is the only writer of _value
        _value += intent.Delta;
        _context.Broadcast(new ValueChanged { Value = _value });
    }
}
```

### The client

```csharp
[ClientBehaviour]
public sealed class ValueClient : CoopBehaviour
{
    private ICoopContext _context;
    private int _value;

    private void OnEnable()
    {
        _context = Context;
    }

    private void OnDisable()
    {
        _context = null;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.K))
        {
            var host = _context.HostConnection;
            if (host != null)
            {
                _context.Send(host, new AddValue { Delta = 1 });
            }
        }
    }

    [MessageHandler]
    private void HandleChanged(CoopMessageContext context, ValueChanged message)
    {
        // authoritative state from the host
        _value = message.Value;
        ApplyValueToUi(_value);   // your own presentation path
    }
}
```

`[MessageHandler]` infers the message type from the parameter that implements `INetMessage`; the
parameter order does not matter. The old `[MessageHandler(typeof(...))]` form still compiles but is
deprecated.

## 3. Choosing the right mechanism

| What you have | Use |
| --- | --- |
| A value the host owns (price, counter, setting) | an intent + a state message + an `[OnFullyJoined]` baseline |
| A session-wide value with no object (setting, tax rate) | `[GlobalInformation]` + `Global<T>` |
| A one-shot event (someone won, a card was graded) | a `[NetworkMessage]` event |
| A value attached to one placed object, NPC, or box | **extended information** (section 5) |
| A client action that must feel instant | an `ActionMessage` (section 6) |
| An object your mod creates | not supported: CardShopCoop owns replicated object lifecycle. Create it locally on both sides through the game's normal path, or attach data to an existing object |
| A custom network transport | `INetworkingBackend` + `IDatagramTransport` (section 9) |

Do not invent a second identity for an object CardShopCoop already replicates. Put the live
`InteractableObject`/`Customer`/`Worker` in the message and let the API translate it (section 7).

## 4. Behaviours and lifecycle

| Attribute | Exists | Handlers receive |
| --- | --- | --- |
| `[ServerBehaviour]` | host, for the session | host-side state and intents |
| `[ClientBehaviour]` | joiner, for the session | authoritative state from the host |
| `[PersistentBehaviour]` | process lifetime | whatever you bind; use for UI or input that outlives a session |

| Method attribute | Signature | When |
| --- | --- | --- |
| `[OnClientJoined]` | `(PeerConnection)` | a client is admitted; **not** in game yet — bookkeeping only |
| `[OnFullyJoined]` | `(PeerConnection)` | the peer is fully in game; send baselines here |
| `[OnClientDisconnected]` | `(PeerConnection, DisconnectInfo)` | a peer leaves |
| `[OnSessionStarted]` | `()` | this peer's session starts |
| `[OnSessionStopped]` | `()` | this peer's session ends |

A `[PersistentBehaviour]` is created once at plugin load but receives `[OnSessionStarted]` and
`[OnSessionStopped]` once for each session while it survives across them. It is the right place for
UI/input that must exist between sessions.

`ICoopContext` is the live session:

- `IsHost`, `IsClient`, `InSession`, `InGame`
- `Connections` — the peers you can talk to (empty outside a session). Each is a
  `PeerConnection` with `Id`, `Name`, and `State`
- `Players` — every player in the session, host first, including this player; each is a
  `PlayerRef` with the host-canonical `Id`, `Name`, `IsHost`, and `IsSelf`
- `Self`, `Host` — the two `PlayerRef`s you usually need
- `HostConnection` — the host as a client sees it; null on the host. `LocalConnectionId` is
  this player's id in the host's roster
- `Send(connection, message)`, `Send(connectionId, message)`, `Broadcast(message)`
- `Messages.RegisterAttributedHandlers/UnregisterAttributedHandlers` (only for a component you
  create yourself; behaviours created by CardShopCoop are bound automatically)

`ConnectionIds` and `PeerName(id)` still exist for older integrations but are deprecated; the
connection object carries both values now.

`CoopMessageContext` describes an inbound message: `Connection` (the sender's `PeerConnection`,
with `Id`, `Name`, and `State`), `Sender` (the sender as a `PlayerRef`), `ConnectionId`,
`IsAuthenticated`, `InGame`.

`Broadcast` from the host fans out to all ready peers; from a client it sends to the host.
`Send` targets one reachable peer. On a client, `HostConnection` is the host and is null on the
host, which reaches its clients through `Connections`.

### Host intent validation

A `[ServerBehaviour]` can reject a client intent before its handler runs:

```csharp
[IntentValidator]
private IntentValidation ValidatePurchase(CoopMessageContext context, BuyIntent intent)
{
    if (intent.Quantity <= 0)
    {
        return IntentValidation.Deny("quantity must be positive");
    }

    if (!CanBuy(context.ConnectionId))
    {
        return IntentValidation.Deny("not your shop");
    }

    return IntentValidation.Allow();
}

[IntentRejected]
private void OnPurchaseRejected(
    CoopMessageContext context, BuyIntent intent, string reason)
{
    // Optional: publish authoritative state here so the client re-anchors
    // before the framework sends the action rollback.
}
```

- Runs **on the host only**, before the handler and before any mutation. Keep it
  side-effect-free; validate a read-only view of the game state.
- Return `IntentValidation`, not an exception. A denial is a normal outcome, not a protocol
  error; the connection stays up.
- On denial the handler does not run. The `[IntentRejected]` hook runs first, then the framework
  rolls back the intent's action id when the message derives from `ActionMessage`. The hook runs
  before the rollback so a module can re-anchor the client with authoritative state under the
  rejected action id.
- More than one validator or rejection hook may be registered for a message type; all validators
  must allow, and every rejection hook runs.
- The register module is the reference implementation: ownership, operation, phase, and slot
  validity all live in its `[IntentValidator]`; the handler applies a validated intent through the
  game's own methods, and the client renders one authoritative snapshot.

## 5. Extended information: data on an existing object

Use extended information when the data belongs to an object that already exists on both sides.
You do not design a DTO, send a baseline, or write a handler registration for the value itself.

```csharp
public sealed class InteractableObjectColor
    : ExtendGameObjectInformation<InteractableObject>
{
    public byte R, G, B;
}

// Host: applies locally and broadcasts. Client: sends an intent and applies the host echo.
shelf.SetExtendedInformation(new InteractableObjectColor { R = 1, G = 2, B = 3 });

var color = shelf.GetExtendedInformation<InteractableObjectColor>();  // null when absent
shelf.RemoveExtendedInformation<InteractableObjectColor>();
```

For an NPC, the same methods are extensions on `Customer` and `Worker` (a packaging box is an
`InteractableObject`, so the object extension above covers it):

```csharp
public sealed class CustomerMood : ExtendGameObjectInformation<Customer>
{
    public bool Happy;
}

customer.SetExtendedInformation(new CustomerMood { Happy = true });
var mood = customer.GetExtendedInformation<CustomerMood>();
customer.RemoveExtendedInformation<CustomerMood>();
```

The owner must be an object CardShopCoop replicates: an `InteractableObject` placed through the
game's shelf lists (decorations included), a `Customer`, a `Worker`, or a packaging box.

### Rules

- **`T` must be the value's concrete type.** `Set` throws `ArgumentException` if the value is a
  subclass of `T`, because the entry is keyed and serialized by `T`:

  ```csharp
  public class ColorInfo : ExtendGameObjectInformation<InteractableObject> { public byte R; }
  public sealed class RgbColorInfo : ColorInfo { public byte G; }

  ColorInfo info = new RgbColorInfo { R = 1, G = 2 };
  shelf.SetExtendedInformation(info);   // T inferred as ColorInfo -> throws
  ```

  Without the check the value would exist with `G` locally and lose `G` on every other peer. Call
  `SetExtendedInformation((RgbColorInfo)info)` or declare the variable as `RgbColorInfo`.

- **Client writes are echoed.** On a client, `Set` sends an intent; `Get` keeps returning the old
  value (or null) until the host's echo arrives. `Remove` returns true when the intent was sent.

- **The host validates the type and the owner, not the sender.** Any admitted player can write any
  replicated object's information unless the type is marked `[HostOnlyInformation]`. Use that
  attribute for host-authoritative gameplay state that has its own validated intent lane (the
  register snapshot is the reference): a client `Set` throws, and a client intent for the type is
  dropped by the host.

- **Both players need the same information types.** The join exchanges the type list and refuses a
  mismatch. A type first registered during a session applies from the next session.

- **The normal message size and framing limits apply.** There is no separate extended-information
  size limiter; keep values small.

### React to changes

```csharp
[ExtendedInformationHandler]
private void OnColorChanged(InteractableObject obj, InteractableObjectColor color)
{
    // color is null when the entry is removed
}
```

The information parameter may be the raw value or `InformationChange<T>`; either form is
inferred, and both can be registered as separate observers:

```csharp
[ExtendedInformationHandler]
private void OnColorChanged(InteractableObject obj, InformationChange<InteractableObjectColor> change)
{
    if (change.IsFirstValue)
    {
        ApplyFull(change.Current);      // first value or join baseline
    }
    else if (change.Removed)
    {
        Clear(change.Previous);
    }
    else
    {
        ApplyDelta(change.Previous, change.Current);
    }
}
```

Raw `T` means “current value only” (null on removal). `InformationChange<T>` adds `Previous`,
`Removed` and `IsFirstValue`; `Previous` is null for the first value and for a join baseline. Use
the change form when you need to diff, do a full apply on the baseline, or clean up from the old
value.

CardShopCoop binds it with the rest of the behaviour's attributed handlers; you do not register it
separately. It runs after the local host set, after a client receives the host echo or join
baseline. On removal the raw form receives null; the change form receives `Current = null` and
`Previous = old`.

### Global values (no object)

The same mechanism carries a session-wide value that belongs to no object. One value exists per
type. Mark the class with `[GlobalInformation]` and keep it immutable:

```csharp
[GlobalInformation]
public sealed class ShopTaxRate
{
    public int Percent { get; init; }
}
```

Read and write it through `Global<T>.Value`:

```csharp
Global<ShopTaxRate>.Value = new ShopTaxRate { Percent = 5 };
var tax = Global<ShopTaxRate>.Value;   // null when not set
Global<ShopTaxRate>.Mutate(t => new ShopTaxRate { Percent = (t?.Percent ?? 0) + 1 });
Global<ShopTaxRate>.Value = null;      // remove
```

The setter publishes through the normal host-authority flow; on a client it is an intent and the
value is replaced when the host echo arrives. Keep the value immutable: `Value` returns the stored
object, so a mutable value would let a caller edit it without publishing. `Mutate` is the
in-place-style form: it reads, applies your update, and publishes one new value. `init` needs the
standard `net472` polyfill in the declaring assembly:

```csharp
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
```

Keep the value as data. Reactions belong in a handler, not an `OnUpdate` on the value:

```csharp
[GlobalInformationHandler]
private void OnTaxChanged(ShopTaxRate tax)
{
    // tax is null when the entry is removed
}
```

The handler is bound automatically with the rest of the behaviour's attributed handlers. Its
parameter may be the raw `ShopTaxRate` (current only) or
`InformationChange<ShopTaxRate>` (previous/current/removed/first). The type joins the same
handshake catalog; a peer without it is refused at join.

## 6. Actions (optional)

A plain `INetMessage` is just a message. An `ActionMessage` is a **command**: the framework gives
it an action id, records how to replay and reverse it, sends it, and reconciles the host's answer.
There is no separate send or reconcile call.

```csharp
[NetworkMessage]
public sealed class SetPrice : ActionMessage
{
    public InteractableObject Shelf;
    public float Value;

    // The replay path. The client already performed the change in its game hook.
    public override void OnApply() => Shelf.SetPrice(Value);
}
```

Send the command normally. The framework fills `ActionId`; never set it yourself:

```csharp
// Client. The game action has already happened locally.
_context.Send(_context.HostConnection, new SetPrice { Shelf = shelf, Value = value });
```

On the host the router runs `OnApply()` and then your `[MessageHandler]` for the same message. The
handler publishes the authoritative result once:

```csharp
[ServerBehaviour]
public sealed class MyHost : CoopBehaviour
{
    [MessageHandler]
    private void HandleSetPrice(CoopMessageContext context, SetPrice message)
    {
        // OnApply has already set the price. Publish the resulting authoritative value under the
        // action id so the requesting client can retire its local action.
        shelf.SetExtendedInformation(new ShelfPrice
        {
            Value = message.Value,
            ActionId = message.ActionId,
        });
    }
}
```

Keep the authoritative result in an extended-information value that implements `IActionValue`
when the handler renders it:

```csharp
public sealed class ShelfPrice : ExtendGameObjectInformation<InteractableObject>, IActionValue
{
    public float Value;
    public Guid ActionId { get; init; }
    public bool Rejected { get; init; }
}
```

The framework applies `ActionId` around your `[ExtendedInformationHandler]`: an accepted echo
retires the local action, a `Rejected` re-anchor reverses the local action first, and newer local
actions on the same object are preserved in order. A `Rejected` value is the optional pre-intent
state published by `[IntentRejected]`; publish it there before returning if your checkpoint needs a
full re-anchor. When the EIM value is the whole state, `OnUndo` can stay empty; add it only for an
effect the value does not model (the register's coin event is one example).

That is the whole action surface: an `ActionMessage` command and an `IActionValue` authoritative
result. Nothing else is required.

## 7. Referring to an existing object in a DTO

For a message that targets an object that already exists on both peers, put the live object in the
DTO:

```csharp
[NetworkMessage]
public sealed class MachineOpened : INetMessage
{
    public InteractableObject Machine;
    public bool Open;
}
```

`InteractableObject` (including its subclasses), `Customer`, `Worker`, and `PlayerRef` fields are
translated to and from CardShopCoop's stable identity for you. A `PlayerRef` carries the
host-canonical player id and the sender's name snapshot; `IsHost` is derived from id 0 and `IsSelf`
is computed locally. It is a reference for naming and display, **not an authentication token**: the
authenticated sender of an inbound message is `CoopMessageContext.Sender` (or `context.Connection`).

```csharp
[NetworkMessage]
public sealed class PlayerNotice : INetMessage
{
    // context.Self, context.Host, or an entry from context.Players
    public PlayerRef Player;
    public string Text;
}
```

Rules:

- **Existing objects only.** Creation and destruction are CardShopCoop's; a message that creates an
  object belongs to its lifecycle, not to your DTO.
- **Null means “not here”.** If the receiver cannot resolve the object, the field is null. Drop that
  update; it is not a protocol error.
- **Use `PlayerRef` for naming, not authorization.** `PeerConnection` is a transport view: a client
  calls the host id `1`, while the host calls its first client id `1`, so the raw id is not a shared
  identity. `PlayerRef` is host-canonical (0 = host, 1..N = clients) and resolves on every peer. Its
  name is the sender's snapshot; `IsSelf` is computed locally. A DTO field is data supplied by the
  sender. For "who sent this", use `CoopMessageContext.Sender` or `context.Connection`.
- **Unity main thread.** Build and send these DTOs on the Unity thread, because the object is a
  scene reference.
- **Do not add your own object id field** alongside the object for the same purpose. The API already
  carries the identity on the wire.

## 8. Delivery and failure rules

- Messages are **reliable and ordered** by default. Use
  `[NetworkMessage(Reliability = Reliability.Transient)]` only for loss-tolerant state such as
  position or a preview. Transient messages are not fragmented and must stay small (roughly 1 KB).
- A handler exception drops that one message and keeps the session up; the receiving player sees an
  alert and the log names the failure. The peers can drift from that point, and a rejoin is the
  resync. **Do not throw from handlers.**
- A send failure is a connection failure. Do not build retries or acknowledgement schemes on top of
  messages; reliable delivery is already the transport's job.
- There is no polling or reconciliation loop in the API. Send when the game changes something.

## 9. Custom transports (optional)

A custom transport is two pieces:

- `INetworkingBackend` — the player-facing connect flow: fields, availability, host/join, status.
- `IDatagramTransport` — the bearer: move complete datagrams and report peer availability/closure.

You do **not** implement reliability, framing, peer identity, or authorization. The runtime owns all
of that. A backend creates its own bearer; it never creates a `PeerConnection` or a `ConnectionId`,
and it never touches the reliable-session implementation.

```csharp
public sealed class MyBackend : INetworkingBackend
{
    public void Connect(ConnectionId attempt, ConnectMode mode, FieldValues values,
        IBackendConnectCallbacks callbacks)
    {
        // Open your socket/relay/lobby here.
        var bearer = new MyDatagramBearer(/* ... */);
        callbacks.BearerReady(attempt, bearer); // the runtime takes ownership
        callbacks.Connected(attempt);           // the attempt is fully live
    }

    public void Disconnect(ConnectionId attempt)
    {
        // Release backend-owned state (leave the lobby, remove a port mapping).
        // Do not dispose the bearer; the runtime owns it.
    }
}
```

`Connect` receives a runtime-allocated `ConnectionId`. Use that exact value in every callback. The
runtime ignores a callback whose attempt is no longer current, so a late relay/Steam callback cannot
tear down a newer session.

`IDatagramTransport` only needs:

- `Start`, `Poll`, `Send`, `Close`, `Stop`, `Dispose`
- `PeerAvailable` / `PeerClosed`
- `MaxDatagramSize`

`Start`, `Poll`, `Send`, `Close`, `Stop`, and `Dispose` run on the network pump thread;
`PeerAvailable` / `PeerClosed` may be raised from any thread. `Send` must copy the segment before it
returns. `DatagramPeer` is a bearer-local token, not a player id and not a network address.

Register once:

```csharp
IDisposable handle = CoopApi.RegisterNetworkingBackend(new MyBackend());
```

Treat the registration as process-lifetime. A plugin component can be destroyed by the game while
the transport is still wanted, so **do not dispose the handle from `OnDestroy`**. Dispose it only
when you deliberately remove the transport for the rest of the run.
The co-op window falls back to `GetFields()`; implement `INetworkingBackendDiagnostics` if you want
extra hosting details in a support report.

The co-op window is event-driven: it re-reads `ConnectionInfo` and `Actions` when the session
changes. If your backend resolves presentation state later - a public address arriving, a lobby
becoming live, a busy step finishing - implement `INetworkingBackendNotifications` and raise its
`StateChanged` event whenever those values would now read differently. Raise it on any thread; the
runtime marshals it to the Unity thread. Without it, a button whose enabled state comes from that
late state can stay grey until some unrelated session event happens.

## 10. Compatibility and safety

- **Both players need CardShopCoopCommunity and your mod at the same versions.** The join refuses a
  mismatch instead of silently diverging.
- **Call the API from the Unity main thread.** The API reads and writes Unity objects, and the
  session callbacks already run there.
- **The API assembly is absent when CardShopCoopCommunity is absent.** Your plugin still loads, but
  any method that names an API type faults as soon as it is compiled. Keep API calls in a separate
  method and guard it:

  ```csharp
  private void Awake()
  {
      if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("dev.meepen.cardshopcoop"))
      {
          return;
      }

      InitCoop();
  }

  [System.Runtime.CompilerServices.MethodImpl(
      System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
  private void InitCoop()
  {
      // only here may you touch CoopApi / CoopLog / CoopReflection
  }
  ```

- **One broken behaviour does not take the session down.** A behaviour with an invalid lifecycle
  signature or a non-`MonoBehaviour` type is logged and skipped at session start.
- **Renaming or moving a DTO is a breaking change.** Bump your mod version and require both players
  to update.

## 11. Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| My handler never runs | The class is not marked `[ServerBehaviour]`/`[ClientBehaviour]`/`[PersistentBehaviour]`, the method is not `[MessageHandler]`, or the DTO is not `[NetworkMessage]` |
| Join refused: message catalog mismatch | The two players run different builds of your mod (or one lacks it) |
| Join refused: extended-information catalog mismatch | Same, for an `ExtendGameObjectInformation<T>` type |
| Works alone, does nothing in co-op | `CoopApi.Context` is null outside a session; check `IsHost`/`IsClient` and the soft dependency |
| `SetExtendedInformation` throws | `T` is a base class, the owner type is unsupported, or the type was registered too late |
| `GetExtendedInformation` returns null | No entry yet, the client is still waiting for the host echo, or the object has no replicated identity |
| The two players show different values | A client wrote locally instead of sending an intent; the host owns the value |
| A message with an object field is ignored | The object is not present on the receiver (null), the message was sent off the Unity thread, or a `PlayerRef` names a player the roster does not know yet |
| A transient message never arrives | It was larger than the transient budget; use the reliable lane |

## 12. API cheat sheet

| I want to… | Use |
| --- | --- |
| know whether co-op is present / in a session | `CoopApi.IsAvailable`, `CoopApi.Context` |
| send to the host / to everyone | `context.Send(context.HostConnection, msg)`, `context.Broadcast(msg)` |
| target one peer | `context.Send(connection, msg)` |
| know who I am / who is here | `Self`, `Host`, `Players` (`PlayerRef`), `LocalConnectionId` |
| name a player in a DTO | a `PlayerRef` field (resolved from the roster on both peers) |
| show the sender of a message | `CoopMessageContext.Sender` |
| run code when a player joins | `[OnClientJoined]`, `[OnFullyJoined]` |
| log from my integration | `CoopLog.Info/Warn/Error` |
| resolve an optional game type | `CoopReflection.OptionalType/Field/Method/Property` |
| attach typed data to an object | `SetExtendedInformation` / `GetExtendedInformation` / `RemoveExtendedInformation` |
| attach typed data to the session | `Global<T>.Value` / `Mutate` (assign null to remove) |
| react to that data | `[ExtendedInformationHandler]` / `[GlobalInformationHandler]` |
| reject a client intent before it runs | `[IntentValidator]` + `[IntentRejected]` |
| make a client action feel instant | an `ActionMessage` + an `IActionValue` response |
| add a transport | `INetworkingBackend` + `IDatagramTransport`, then `CoopApi.RegisterNetworkingBackend` |

## 13. Where to look next

- [`samples/CardShopCoop.SampleMod`](../samples/CardShopCoop.SampleMod) — the complete working
  integration.
- The XML documentation next to `CardShopCoop.Api.dll` — every public member and its contract.
- [`CHANGELOG.md`](../CHANGELOG.md) — API changes and the version they shipped in.
