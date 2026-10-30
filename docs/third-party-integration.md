# Integrating a mod with CardShopCoop

`CardShopCoop.Api` adds co-op support to a BepInEx mod: typed messages, host-authoritative
state with late-join baselines, data attached to existing game objects, client actions, and
custom transports. The complete working example is
[`samples/CardShopCoop.SampleMod`](../samples/CardShopCoop.SampleMod).

The API lives in these namespaces: `CardShopCoop.Api`, `CardShopCoop.Attributes`,
`CardShopCoop.Net`, `CardShopCoop.Net.Connection`, `CardShopCoop.Net.Networking`,
`CardShopCoop.Net.Datagrams`, and `CardShopCoop.Net.Protocol`. Most integrations need only the
first four; custom transports use the networking and datagram namespaces, and `Reliability` lives
in `CardShopCoop.Net.Protocol`.

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

// Host only: remove it. Handlers receive null.
Global<ShopTaxRate>.Value = null;

// Any peer: read the last authoritative value. Late joiners get it automatically.
var tax = Global<ShopTaxRate>.Value; // null when unset
```

- `[HostOnlyInformation]` makes the value read-only for clients. Drop it if clients may change
  the value: the client sends the write to the host, the host applies it, and the client sees the
  result.
- Keep the value immutable; assign a new instance instead of editing the old one.
- On net472, `init` needs the standard `IsExternalInit` shim.

React on any peer:

```csharp
[GlobalInformationHandler]
private void OnTax(ShopTaxRate tax) // null when removed
    => _label.text = tax == null ? "-" : tax.Percent + "%";
```

The handler may take the raw `T` or `InformationChange<T>`. The raw form is the current value
(null on removal); the change form also exposes `Previous`, `Removed`, and `IsFirstValue`.

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

`[MessageHandler]` infers the message type from its `INetMessage` parameter. From the host,
`Context.Broadcast` reaches every reachable peer; from a client it reaches the host. `Context.Send`
also reaches the host from a client.

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

At that point `Context.Broadcast` is safe. A per-peer send needs a peer that has joined, so send a
baseline from `[OnFullyJoined]` to the peer you are given.

A `[PersistentBehaviour]` runs its Unity lifecycle at plugin load, before any transport
exists. Its `[OnSessionStarted]` fires before the transport starts and `[OnSessionStopped]`
after it stops. It never receives `[OnClientJoined]`, `[OnFullyJoined]`, or
`[OnClientDisconnected]`. Do not send from a persistent behaviour's Unity lifecycle or its
`[OnSessionStarted]`.

| Method attribute | Signature | When |
| --- | --- | --- |
| `[OnSessionStarted]` | `()` | this peer's session is ready |
| `[OnSessionStopped]` | `()` | this peer's session ends |
| `[OnClientJoined]` | `(ICoopPeer)` | a peer is admitted, not yet in game |
| `[OnFullyJoined]` | `(ICoopPeer)` | a peer is fully in game |
| `[OnClientDisconnected]` | `(ICoopPeer, DisconnectInfo)` | a peer leaves |

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
| A one-shot event or notification | a `[NetworkMessage]` implementing `INetMessage` |
| Data on an existing object, NPC, or box | extended information |
| A command the client predicts and may need to undo | `ActionMessage` |
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

The host applies the write and broadcasts it. A client write is sent to the host; `Get` returns
the old value until the host's answer arrives. Mark a type `[HostOnlyInformation]` when clients
must not write it.

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

`[ExtendedInformationHandler]` accepts the entity and information parameters in either order, and
the information parameter may be the raw value or `InformationChange<T>`.

For a session-wide value, use `[GlobalInformation]` + `Global<T>` from the quick start. Join
baselines are automatic for both extended information and global values.

## 7. Messages and actions

There are two kinds of message.

- `INetMessage` is a one-way message. Nothing predicts it and nothing undoes it. Use it for
  announcements, notifications, timers, cosmetic effects, and continuous state such as positions
  and animations. A client may send one to the host, but it is only a notification.
- `ActionMessage` is a command to the host. The client makes the change immediately (a prediction),
  sends the command, and the host either accepts or refuses it. If the host refuses, the client
  reverses its prediction. Use it for every world interaction another player can affect: picking
  up, placing, storing, moving, buying, opening, scanning, and so on.

If you are unsure which one to use, ask: can another player change the same thing at the same time?
If yes, it is an action. A one-way message would leave the two players disagreeing, because there
is nothing to undo.

### Sending an action

Define a `[NetworkMessage]` class that derives from `ActionMessage`, put the live game objects and
values the command needs in its fields, and call `Context.Send`:

```csharp
[NetworkMessage]
public sealed class MoveObjectAction : ActionMessage
{
    public InteractableObject Target;
    public Vector3 Position;

    [NonSerialized] private Vector3 _previousPosition;

    // Your prediction. Call this yourself on the client before Send; it is normal game code.
    public void Predict()
    {
        _previousPosition = Target.transform.position;
        Target.transform.position = Position;
    }

    // Reverse the prediction only.
    public override void OnUndo(ActionResponseMessage response)
        => Target.transform.position = _previousPosition;

    // Apply the host's state and clean up anything this action created locally.
    public override void OnMerge(ActionResponseMessage response)
        => Target.transform.position = ((MoveObjectResponse)response).Position;
}

// Client: predict first, then send to the host.
var action = new MoveObjectAction { Target = target, Position = position };
action.Predict();
Context.Send(action);
```

The framework fills `ActionId`; leave it empty when you send. If the client has no local change to
make, do not predict; `OnUndo` can stay empty and `OnMerge` still applies the host's response.

### The host handler

The host handles the action, applies it, and returns an `ActionOutcome`:

```csharp
[NetworkMessage]
public sealed class MoveObjectResponse : ActionResponseMessage
{
    public Vector3 Position;
}

[ServerBehaviour]
public sealed class MoveObjectHost : CoopBehaviour
{
    [MessageHandler]
    private ActionOutcome Handle(CoopMessageContext from, MoveObjectAction action)
    {
        if (!CanMove(action.Target))
        {
            // Refused. The response still carries the host's state for this action.
            return ActionOutcome.Reject("the object cannot move now", new MoveObjectResponse
            {
                Position = action.Target.transform.position,
            });
        }

        // Apply the command on the host. The response carries the host's result.
        action.Target.transform.position = action.Position;
        return ActionOutcome.Applied(new MoveObjectResponse
        {
            Position = action.Target.transform.position,
        });
    }
}
```

- The handler always applies the command itself. There is no second apply step.
- `ActionOutcome.Applied(response)` means the command was applied.
- `ActionOutcome.Reject(reason, response)` means it was not applied. The reason is logged when no
  response is supplied.
- A handler for an `ActionMessage` must return `ActionOutcome` and must be on a
  `[ServerBehaviour]`. Registration fails otherwise.
- Do not write a handler for an `ActionResponseMessage`. The framework delivers it to the action
  that sent it. Return the response from the outcome; do not send it yourself.

### Validate before the handler

A host can reject a client intent before the handler runs. Put `[IntentValidator]` on a
`[ServerBehaviour]` method that takes a `CoopMessageContext` and the message, in either order,
and returns `IntentValidation`:

```csharp
[IntentValidator]
private IntentValidation ValidateMove(CoopMessageContext from, MoveObjectAction action)
    => CanMove(action.Target)
        ? IntentValidation.Allow()
        : IntentValidation.Deny("the object cannot move now");
```

The validator runs on the host before the handler. A denial skips the handler. For an
`ActionMessage` it sends the rejection to the client and makes the client's `OnUndo` run; for a
one-way `INetMessage`, the denied message is simply dropped. Use `[IntentRejected]` when a denial
must publish authoritative state before the framework sends the rejection; for example, write an
`IActionValue` with the action id and `Rejected = true`:

```csharp
[IntentRejected]
private void OnMoveRejected(CoopMessageContext from, MoveObjectAction action, string reason)
{
    // Publish authoritative state before the framework rejects the action.
}
```

The rejection hook takes the context, the message, and the reason, in any order. Simple
validation can still return `ActionOutcome.Reject(...)` from the handler; the validator path is
for checks that must run before any handler code.

### Undo and merge

- `OnUndo(response)` runs on the client only when the host refuses the action. Reverse the local
  prediction and nothing else. Do not apply host state here.
- `OnMerge(response)` runs on the client exactly once for every action, accepted or refused. On an
  accept, apply the host's state from the response. On a refusal it runs after `OnUndo`, so use it
  to apply the host's state and remove local objects, UI, or effects you created for the
  prediction.

`OnMerge` is the only cleanup point. If your action creates a temporary object or opens a screen,
remove it there, not in `OnUndo`. When the action returns no response, `OnMerge` receives null;
check for that before casting.

### State on accept and refuse

If the action changes shared state, the response must carry the host's state after the command.
Return it from both `Applied` and `Reject` (here `BuyItemResponse` is your own response type):

```csharp
return ActionOutcome.Reject("not enough money", new BuyItemResponse
{
    Price = hostItem.CurrentPrice,
    Amount = hostItem.Amount,
});
```

The same rule applies to an accept. The client replaces its prediction with the response, so a
missing response leaves the client showing a value the host does not have. An action with no shared
state (a sound, an animation, a local-only effect) may return no response at all.

If the action publishes an `IActionValue` (an extended-information value carrying `ActionId` and
`Rejected`), the framework applies it before `OnMerge`, with a null response. This is only for
state that already lives in extended information.

### When to use `INetMessage` instead

Use `INetMessage` for anything that is not a discrete command another player can interfere with:

- a notification or announcement;
- a continuous pose, animation, or cosmetic update;
- a host-to-client state broadcast;
- a message that only one side needs to hear.

Never predict an `INetMessage`. There is no undo for it and no response, so the sender cannot learn
that it was wrong. Actions are always reliable; do not mark them `Reliability.Transient`
(`Reliability` is in `CardShopCoop.Net.Protocol`). Transient is only for small `INetMessage`
updates that can be lost without breaking the game.

Do not use an action for extended information. A client `SetExtendedInformation` is already
host-authoritative: the host applies it, broadcasts the result, and late joiners get it in the
baseline.

```csharp
// Client: no action, no undo.
shelf.SetExtendedInformation(new ShelfColor { R = 255, G = 0, B = 0 });
```

### Peers

A player is an `ICoopPeer`. `Context.Self` is this player, `Context.Host` is the host, and
`Context.Peers` lists the other players. There are no connection ids in the peer or message API.

```csharp
ICoopPeer self = Context.Self;    // this player
ICoopPeer host = Context.Host;    // the host

foreach (ICoopPeer peer in Context.Peers) // the other players
{
    CoopLog.Info(peer.Name + " is " + peer.State);
}
```

| Why | Use |
| --- | --- |
| Send to the host (client) | `Context.Send(message)` |
| Send to every peer (host) | `Context.Broadcast(message)` |
| Send to every peer except one (host) | `Context.BroadcastExcept(peer, message)` |
| Send to one peer | `peer.Send(message)` |
| Read this player and the host | `Context.Self`, `Context.Host` |
| Read the other players | `Context.Peers` |
| Read the full roster, including this player | `Context.Players` |
| Get the player behind a peer | `peer.Player` (a `PlayerRef`) |

On a client, `peer.Send` only works when the peer is the host. On the host, it works for any peer.
A client's `Context.Broadcast` sends to the host.

## 8. Identity and message rules

- A DTO is a `[NetworkMessage]` class implementing `INetMessage`, with a parameterless
  constructor and simple fields.
- Put the live `InteractableObject`, `Customer`, or `Worker` in the message. CardShopCoop maps
  it to its stable identity; do not add an id field. A null field means the object is not
  present on that peer; ignore that update.
- Use `PlayerRef` to name a player. `Context.Self.Player`, `Context.Host.Player`, and
  `peer.Player` are the `PlayerRef`s for the roster. `Context.Players` is the full roster, host
  first and including this player; use it when a DTO must name someone other than the sender. A
  `PlayerRef` is presentation data, not authentication.
- The authenticated sender of a message is `CoopMessageContext.Peer`. Its `Player` is the
  sender's `PlayerRef` and its `Name` is safe to log. Never trust a player field inside a DTO.
- On the host, `ActionMessage.Peer` is filled with the same authenticated sender before the
  handler runs, for handlers that do not take a context. It is null on a client and outside
  dispatch.
- Address peers as objects (`ICoopPeer`), never by connection id. `Context.Send`,
  `Context.Broadcast`, `Context.BroadcastExcept`, and `peer.Send` are the only send calls.
- The host is authoritative. Clients send actions; the host applies and broadcasts state.
  Send small updates during play and one baseline to each peer from `[OnFullyJoined]`.
- Messages are reliable and ordered by default. Use `Reliability.Transient` only for small,
  loss-tolerant one-way updates. Actions are always reliable.
- Do not build polling, resend, or retry loops on top of messages.
- Do not throw from a handler; one exception drops that message and can desynchronize the
  peers until they rejoin.
- Message changes are patch-level. Both players must run the same version of your mod.

## 9. Custom transports

Implement `INetworkingBackend` (connect flow and UI) and `IDatagramTransport` (datagrams).
`INetworkingBackend` is in `CardShopCoop.Net.Networking` and `IDatagramTransport` is in
`CardShopCoop.Net.Datagrams`. CardShopCoop owns reliability, framing, peer identity, and
authorization.

```csharp
IDisposable handle = CoopApi.RegisterNetworkingBackend(new MyBackend());
```

Register once at plugin load and treat it as process-lifetime. Do not dispose the handle from
`OnDestroy`; that permanently removes the transport.

## 10. Compatibility and troubleshooting

- Both peers need the same CardShopCoop and mod versions. A mismatch refuses the join.
- `CoopApi.ApiVersion` is the version of the public API your mod was built against. It is separate
  from the CardShopCoop release version. CardShopCoop 2.3.0 ships API `2.3.0`.
- A mod built against an older Action API must be rebuilt. The old connection-id send calls, action
  keys, and `OnApply` hook are gone.
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
| The peers show different values | A client sent an `INetMessage` instead of an `ActionMessage`, or the response is missing the host's state |
| An object field is always null | The object is not present on the receiver |

## 11. Cheat sheet

| I want to… | Use |
| --- | --- |
| Check for co-op / the session | `CoopApi.IsAvailable`, `Context` |
| Send to the host (client) | `Context.Send(msg)` |
| Send to everyone (host) | `Context.Broadcast(msg)` |
| Send to everyone except one peer (host) | `Context.BroadcastExcept(peer, msg)` |
| Send to one peer | `peer.Send(msg)` |
| Read this player, the host, and the other players | `Context.Self`, `Context.Host`, `Context.Peers` |
| Read the full player roster, including this player | `Context.Players` |
| Run code when a peer joins | `[OnClientJoined]`, `[OnFullyJoined]` |
| Log | `CoopLog.Info/Warn/Error` |
| Resolve an optional game type | `CoopReflection.OptionalType/Field/Method/Property` |
| Attach data to an object | `Set/Get/RemoveExtendedInformation` |
| Attach data to the session | `Global<T>.Value` |
| React to extended information | `[ExtendedInformationHandler]`, `[GlobalInformationHandler]` |
| Send a one-way event | a `[NetworkMessage]` `INetMessage` with a `void` handler |
| Predict a client command | a `[NetworkMessage]` `ActionMessage`, then `Context.Send` |
| Resolve a client command | return `ActionOutcome.Applied/Reject` from its `[MessageHandler]` |
| Validate a client intent before its handler | `[IntentValidator]` + `IntentValidation.Allow/Deny`; `[IntentRejected]` for the denial hook |
| Add a transport | `CoopApi.RegisterNetworkingBackend` |
