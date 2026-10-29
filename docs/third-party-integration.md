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

## 2. Quick start: a host-owned value

A session-wide value that the host owns is **global information**. There are no DTOs and no
baseline code to write. The calls live in your attributed behaviours; the host writes and any
peer reads:

```csharp
[GlobalInformation]
[HostOnlyInformation]
public sealed class ShopTaxRate
{
    public int Percent { get; init; }
}

// Host only: set it, or replace it when it changes.
Global<ShopTaxRate>.Value = new ShopTaxRate { Percent = 5 };
Global<ShopTaxRate>.Mutate(t => new ShopTaxRate { Percent = (t?.Percent ?? 0) + 1 });

// Any peer: read the last authoritative value. Late joiners get it automatically.
var tax = Global<ShopTaxRate>.Value; // null when unset
```

- `[HostOnlyInformation]` makes the value read-only for clients. Drop it if clients may change
  the value: a client write becomes an intent, the host applies it, and the client sees the echo.
- Keep the value immutable; assign a new instance instead of editing the old one.
- On net472, `init` needs the standard `IsExternalInit` shim.

React on any peer:

```csharp
[GlobalInformationHandler]
private void OnTax(ShopTaxRate tax) // null when removed
    => _label.text = tax == null ? "-" : tax.Percent + "%";
```

`Global<T>` handles host authority, the broadcast, and the join baseline. `Context` is assigned
before `OnEnable`, so use it directly.

### One-shot events

State uses `Global<T>`. A one-shot event is a normal message:

```csharp
[NetworkMessage]
public sealed class ShowBanner : INetMessage
{
    public string Text;
}

Context.Broadcast(new ShowBanner { Text = "Sale started" });

[MessageHandler]
private void OnBanner(CoopMessageContext from, ShowBanner message)
    => _banner.Show(message.Text);
```

`[MessageHandler]` infers the message type from its `INetMessage` parameter. `Context.Broadcast`
reaches every ready peer from the host, and the host from a client. `Context.Send` reaches one
peer.

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
| A session-wide value | `[GlobalInformation]` + `Global<T>`; add `[HostOnlyInformation]` if clients must not write it |
| A one-shot event | a `[NetworkMessage]` |
| Data on an existing object, NPC, or box | extended information |
| A client action that must feel instant | `ActionMessage` |
| A new connection method | `INetworkingBackend` + `IDatagramTransport` |

Do not create your own object ids. CardShopCoop owns replicated object lifecycle.

## 6. Extended information

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

For a session-wide value, use `[GlobalInformation]` + `Global<T>` from the quick start. Join
baselines are automatic for both extended information and global values.

## 7. Messages and actions (advanced)

There are two message shapes:

- `INetMessage` is a one-way message. Its handler returns `void`, may live on any behaviour, and
  runs wherever the message arrives. Use it for events, announcements, and state you replicate
  yourself.
- `ActionMessage` is a client command that the host resolves. It is always recorded in the action
  ledger, so its handler returns `ActionOutcome` and lives on a `[ServerBehaviour]`.

Do not use an action for extended information. A client `SetExtendedInformation` is already a
host-authoritative intent: the host applies it, broadcasts the result, and late joiners get it in
the baseline. Multiple clients are ordered by the host; the last host write wins.

```csharp
// Client: no action, no undo.
shelf.SetExtendedInformation(new ShelfColor { R = 255, G = 0, B = 0 });
```

Use `ActionMessage` only when the client changes something through a game path that must feel
instant and must be reversed if the host rejects it:

```csharp
[NetworkMessage]
public sealed class MyAction : ActionMessage
{
    public InteractableObject Target;

    [NonSerialized] private Vector3 _previous;

    // Host apply and client replay. The client performs its own local prediction before
    // sending; this is not called on the initial send.
    public override void OnApply() { /* perform the local game change */ }

    // Reverse the local prediction when the host rejects the action.
    public override void OnUndo(ActionResponseMessage response) { /* restore _previous */ }

    // Optional. Only needed when a custom ActionResponseMessage carries data worth merging
    // into local state. Accepted responses call this; rejections call OnUndo.
    public override void OnMerge(ActionResponseMessage response) { /* merge accepted state */ }
}

// Client: perform the local prediction through the game path, then send the command.
// Context.Send records it in the action ledger and fills ActionId before it leaves.
Context.Send(Context.HostConnection, new MyAction { Target = target });
```

The host handler decides the outcome:

```csharp
[ServerBehaviour]
public sealed class MyActionHost : CoopBehaviour
{
    [MessageHandler]
    private ActionOutcome Handle(CoopMessageContext from, MyAction message)
    {
        // Refuse without applying anything on the host; the client undoes its prediction.
        if (message.Target == null)
        {
            return ActionOutcome.Reject("target is not available");
        }

        // Let the framework call message.OnApply() on the host after this returns.
        // If this handler already applied the command itself, return
        // ActionOutcome.Applied() instead so the framework does not apply it twice.
        return ActionOutcome.Apply();
    }
}
```

- Every `[MessageHandler]` for an `ActionMessage` must return `CardShopCoop.Api.ActionOutcome` and
  live on a `[ServerBehaviour]`. Registration fails otherwise.
- `ActionOutcome.Apply(response)` means the handler did not apply the command: the framework calls
  `OnApply()` on the host, then answers the client.
- `ActionOutcome.Applied(response)` means the handler already applied the command: the framework
  skips `OnApply()`.
- `ActionOutcome.Reject(reason, response)` means the host did not apply the command. The client
  reverses its prediction with `OnUndo`. The reason is logged when no response is supplied.
- The `response` argument is optional. Null sends only the framework's internal accept/rollback
  control, which is all a state-based action needs.
- `ActionResponseMessage` is the reply base (`Guid ActionId`, `bool Rejected`); the framework fills
  both fields from the request and the outcome. Responses are routed by the framework: an accepted
  response calls the action's `OnMerge`, a rejected response calls its `OnUndo`. Never declare a
  `[MessageHandler]` for an `ActionResponseMessage`; registration fails.
- `OnApply` runs on the host for `Apply`, and on the client when a rejected action's newer
  neighbours are replayed. It does not run when the client first sends the action.
- `OnUndo` restores what was there, not a blank/default value. The host never trusts a client's
  undo.
- The client's action ledger orders pending actions per object. On a rejection it undoes the
  rejected action and every newer pending action on that object, then replays the survivors before
  applying the host's state. One client cannot undo another client's newer action.
- `_previous` is local prediction state, never cross-client truth. The host's state carries
  `ActionId` and `Rejected`; the client applies it over its local undo. An `IActionValue` is a
  normal extended-information value with those two fields, and is how the framework matches the
  host's state to the pending action.
- Reject with `[IntentValidator]` and handle `[IntentRejected]`; return `IntentValidation`, do
  not throw. A validator rejection never reaches the handler.
- On accept, publish the resulting `IActionValue` with `ActionId = intent.ActionId` and
  `Rejected = false`. In `[IntentRejected]`, publish the current value with the same id and
  `Rejected = true`. A handler that publishes state itself returns `ActionOutcome.Applied()`; the
  state settles the sender's action.

See `TvActionMessage`/`TvState` in CustomTv for a complete action + authoritative state pair.

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
| Send a one-way event | a `[NetworkMessage]` `INetMessage` with a `void` handler |
| Resolve a client command | return `ActionOutcome.Apply/Applied/Reject` from its `[MessageHandler]` |
| Add a transport | `CoopApi.RegisterNetworkingBackend` |
