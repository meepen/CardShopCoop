# Integrating a mod with CardShopCoop

`CardShopCoop.Api` adds co-op support to a BepInEx mod: typed messages, host-authoritative
state with late-join baselines, data attached to existing game objects, client actions, and
custom transports. The complete working example is
[`samples/CardShopCoop.SampleMod`](../samples/CardShopCoop.SampleMod).

The API lives in these namespaces: `CardShopCoop.Api`, `CardShopCoop.Attributes`,
`CardShopCoop.Net`, and `CardShopCoop.Net.Connection`.

## 1. Setup

Reference the API assembly and do not copy it into your release:

```xml
<Reference Include="CardShopCoop.Api">
  <HintPath>...\BepInEx\plugins\CardShopCoop\CardShopCoop.Api.dll</HintPath>
  <Private>false</Private>
</Reference>
```

Declare a soft dependency:

```csharp
[BepInPlugin(Guid, Name, Version)]
[BepInDependency("dev.meepen.cardshopcoop", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class MyPlugin : BaseUnityPlugin
{
}
```

The soft dependency is the registration. CardShopCoop discovers your assembly; there is no
registration call.

If you cannot declare a dependency, call `CoopApi.Register(assembly)` from `Awake` after
`CoopApi.IsAvailable` is true. Late registration applies from the next session, and your
`[PersistentBehaviour]` types are not created at all.

## 2. Quick start: one host-owned value

The host owns the value. A client sends an intent. The host applies it and broadcasts the
result. Each peer receives one baseline when it finishes joining.

The DTOs:

```csharp
[NetworkMessage]
public sealed class AddScore : INetMessage
{
    public int Delta;
}

[NetworkMessage]
public sealed class ScoreChanged : INetMessage
{
    public int Value;
}
```

The host:

```csharp
[ServerBehaviour]
public sealed class ScoreHost : CoopBehaviour
{
    private int _score;

    [OnFullyJoined]
    private void SendBaseline(PeerConnection peer)
        => Context.Send(peer, new ScoreChanged { Value = _score });

    [MessageHandler]
    private void OnAdd(CoopMessageContext from, AddScore intent)
    {
        _score += intent.Delta;
        Context.Broadcast(new ScoreChanged { Value = _score });
    }
}
```

The client:

```csharp
[ClientBehaviour]
public sealed class ScoreClient : CoopBehaviour
{
    private int _score;

    [MessageHandler]
    private void OnChanged(CoopMessageContext from, ScoreChanged state)
        => _score = state.Value;

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.K) && Context.HostConnection != null)
        {
            Context.Send(Context.HostConnection, new AddScore { Delta = 1 });
        }
    }
}
```

`[MessageHandler]` infers the message type from its `INetMessage` parameter. `Context` is
assigned before `OnEnable`, so use it directly.

`Context.Broadcast` reaches every ready peer from the host, and the host from a client.
`Context.Send` reaches one peer.

## 3. Behaviours and lifecycle

Derive from `CardShopCoop.Api.CoopBehaviour`. It provides `Context` and `Harmony`, and your
handlers are bound automatically. A plain `MonoBehaviour` is also discovered and gets handler
binding, but it has no `Context` or `Harmony`; use `CoopApi.Context` in that case.

| Attribute | Exists | Receives |
| --- | --- | --- |
| `[ServerBehaviour]` | host, for the session | host handlers and join events |
| `[ClientBehaviour]` | joiner, for the session | client handlers and join events |
| `[PersistentBehaviour]` | process lifetime | session start/stop and bound handlers; never join events |

Session behaviours are created and handler-bound while the session is prepared. Their Unity
lifecycle (`Awake`, `OnEnable`, `Start`) and `[OnSessionStarted]` run only after the transport
starts and the message and extended-information catalogs are frozen.

At that point `Context.Broadcast` is safe. Per-peer sends need a live connection, so send a
baseline from `[OnFullyJoined]`.

A `[PersistentBehaviour]` runs its Unity lifecycle at plugin load, before any transport
exists. Its `[OnSessionStarted]` fires before the transport starts and `[OnSessionStopped]`
after it stops. It never receives `[OnClientJoined]`, `[OnFullyJoined]`, or
`[OnClientDisconnected]`. Do not send from a persistent behaviour's Unity lifecycle or its
`[OnSessionStarted]`.

| Method attribute | Signature | When |
| --- | --- | --- |
| `[OnSessionStarted]` | `()` | this peer's session is ready |
| `[OnSessionStopped]` | `()` | this peer's session ends |
| `[OnClientJoined]` | `(PeerConnection)` | a peer is admitted, not yet in game |
| `[OnFullyJoined]` | `(PeerConnection)` | a peer is fully in game |
| `[OnClientDisconnected]` | `(PeerConnection, DisconnectInfo)` | a peer leaves |

CardShopCoop creates every behaviour and binds its handlers before `OnEnable`. There is no manual
registration. If you need more handler code, add another attributed behaviour instead of creating
a component yourself.

## 4. Harmony

`CoopBehaviour.Harmony` is created for each behaviour, assigned before `OnEnable`, and
unpatched when the behaviour is disposed. Never create `new Harmony(...)`, never call
`UnpatchSelf` or `UnpatchAll`, and never unregister patches yourself.

```csharp
[ServerBehaviour]
public sealed class PricePatches : CoopBehaviour
{
    private void OnEnable()
    {
        TryPatch(
            typeof(SetItemPriceScreen),
            "OnPressConfirm",
            prefix: new HarmonyMethod(typeof(PricePatches), nameof(BeforeConfirm)));
    }

    private static bool BeforeConfirm()
    {
        CoopLog.Info("price confirm pressed");
        return true;
    }
}
```

`TryPatch` returns false when the target or method is missing. Pass `args:` to select an
overload. It forwards to the public `CoopHarmony.TryPatch` extension, so you never need a
per-mod patch helper.

To patch a type from an optional mod, resolve it by name:

```csharp
var target = CoopReflection.OptionalType("OtherMod.Screen", "OtherMod");
if (target != null)
{
    TryPatch(target, "Open", postfix: new HarmonyMethod(typeof(PricePatches), nameof(AfterOpen)));
}
```

## 5. Choose a mechanism

| You need | Use |
| --- | --- |
| A host-owned value | intent + state message + `[OnFullyJoined]` baseline |
| A one-shot event | a `[NetworkMessage]` |
| A session-wide value | `[GlobalInformation]` + `Global<T>` |
| Data on an existing object, NPC, or box | extended information |
| A client action that must feel instant | `ActionMessage` |
| A new connection method | `INetworkingBackend` + `IDatagramTransport` |

Do not create your own object ids. CardShopCoop owns replicated object lifecycle.

## 6. Extended information and global values

Attach a typed value to an object that already exists on both peers:

```csharp
public sealed class ShelfColor : ExtendGameObjectInformation<InteractableObject>
{
    public byte R, G, B;
}

shelf.SetExtendedInformation(new ShelfColor { R = 255, G = 0, B = 0 });
var color = shelf.GetExtendedInformation<ShelfColor>(); // null when absent
shelf.RemoveExtendedInformation<ShelfColor>();
```

The owner must be an `InteractableObject`, `Customer`, or `Worker` that CardShopCoop
replicates. A packaging box and a placed decoration are `InteractableObject`s. `T` must be the
value's concrete type.

The host applies the write and broadcasts it. A client write is an intent; `Get` returns the
old value until the host echo arrives. Mark a type `[HostOnlyInformation]` when clients must
not write it.

React with a handler:

```csharp
[ExtendedInformationHandler]
private void OnColor(InteractableObject shelf, InformationChange<ShelfColor> change)
{
    if (change.Removed)
    {
        ClearColor(shelf);
    }
    else
    {
        ApplyColor(shelf, change.Current);
    }
}
```

A session-wide value uses the same flow:

```csharp
[GlobalInformation]
public sealed class ShopTaxRate
{
    public int Percent { get; init; }
}

Global<ShopTaxRate>.Value = new ShopTaxRate { Percent = 5 };
var tax = Global<ShopTaxRate>.Value; // null when unset
Global<ShopTaxRate>.Mutate(t => new ShopTaxRate { Percent = (t?.Percent ?? 0) + 1 });
Global<ShopTaxRate>.Value = null; // remove
```

Keep the value immutable. On net472, `init` needs the standard `IsExternalInit` shim.

```csharp
[GlobalInformationHandler]
private void OnTax(ShopTaxRate tax) // null when removed
{
    UpdateTaxUi(tax);
}
```

Join baselines are automatic for both forms.

## 7. Actions

An `ActionMessage` is a client command with a framework-owned action id and rollback:

```csharp
[NetworkMessage]
public sealed class PaintShelf : ActionMessage
{
    public InteractableObject Shelf;
    public byte R, G, B;

    public override void OnApply()
        => Shelf.SetExtendedInformation(new ShelfColor { R = R, G = G, B = B });

    public override void OnUndo() => Shelf.RemoveExtendedInformation<ShelfColor>();
}
```

Send it like any message; never set `ActionId`:

```csharp
Context.Send(Context.HostConnection, new PaintShelf { Shelf = shelf, R = 255, G = 0, B = 0 });
```

Validate the intent on the host before it runs:

```csharp
[IntentValidator]
private IntentValidation ValidatePaint(CoopMessageContext from, PaintShelf intent)
    => intent.Shelf == null
        ? IntentValidation.Deny("shelf not present")
        : IntentValidation.Allow();

[IntentRejected]
private void PaintRejected(CoopMessageContext from, PaintShelf intent, string reason)
    => CoopLog.Warn("paint rejected: " + reason);
```

A denial skips the handler and rolls back the action. Return `IntentValidation`; do not throw.

## 8. Identity and message rules

- A DTO is a `[NetworkMessage]` class implementing `INetMessage`, with a parameterless
  constructor and simple fields.
- Put the live `InteractableObject`, `Customer`, or `Worker` in the message. CardShopCoop maps
  it to its stable identity; do not add an id field. A null field means the object is not
  present on that peer; ignore that update.
- Use `PlayerRef` to name a player. It is presentation data, not authentication. The
  authenticated sender is `CoopMessageContext.Sender`.
- The host is authoritative. Clients send intents; the host applies and broadcasts state.
  Send small deltas during play and one baseline on join.
- Messages are reliable and ordered by default. Use `Reliability.Transient` only for small,
  loss-tolerant updates.
- Do not build polling, ACKs, retries, or reconciliation loops on top of messages.
- Do not throw from a handler; one exception drops that message and can desynchronize the
  peers until they rejoin.
- Message changes are patch-level. Both players must run the same version of your mod.

## 9. Custom transports

Implement `INetworkingBackend` (connect flow and UI) and `IDatagramTransport` (datagrams).
CardShopCoop owns reliability, framing, peer identity, and authorization.

```csharp
IDisposable handle = CoopApi.RegisterNetworkingBackend(new MyBackend());
```

Register once at plugin load and treat it as process-lifetime. Do not dispose the handle from
`OnDestroy`; that permanently removes the transport.

## 10. Compatibility and troubleshooting

- Both peers need the same CardShopCoop and mod versions. A mismatch refuses the join.
- Call the API on the Unity main thread.
- When CardShopCoop may be absent, guard direct API calls and keep them in a separate
  `[MethodImpl(MethodImplOptions.NoInlining)]` method:

  ```csharp
  if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("dev.meepen.cardshopcoop"))
  {
      return;
  }

  InitCoop();
  ```

- Do not call version-specific game APIs directly. Resolve optional types and members with
  `CoopReflection`.

| Symptom | Cause |
| --- | --- |
| A handler never runs | Missing behaviour attribute, missing `[MessageHandler]`, or an unbound self-created component |
| Join refused | Different mod versions or a catalog mismatch |
| `GetExtendedInformation` returns null | No value yet, or the client is waiting for the host echo |
| Works alone, not in co-op | Not in a session; check `IsHost`/`IsClient` and the soft dependency |
| The peers show different values | A client wrote locally instead of sending an intent |
| An object field is always null | The object is not present on the receiver |

## 11. Cheat sheet

| I want to… | Use |
| --- | --- |
| Check for co-op / the session | `CoopApi.IsAvailable`, `Context` |
| Send to the host / everyone | `Context.Send(Context.HostConnection, msg)`, `Context.Broadcast(msg)` |
| Target one peer | `Context.Send(peer, msg)` |
| Read the roster | `Context.Self`, `Context.Host`, `Context.Players` |
| Run code when a peer joins | `[OnClientJoined]`, `[OnFullyJoined]` |
| Log | `CoopLog.Info/Warn/Error` |
| Resolve an optional game type | `CoopReflection.OptionalType/Field/Method/Property` |
| Attach data to an object | `Set/Get/RemoveExtendedInformation` |
| Attach data to the session | `Global<T>.Value` |
| React to extended information | `[ExtendedInformationHandler]`, `[GlobalInformationHandler]` |
| Reject a client intent | `[IntentValidator]`, `[IntentRejected]` |
| Add a transport | `CoopApi.RegisterNetworkingBackend` |
