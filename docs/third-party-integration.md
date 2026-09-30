# Integrating with CardShopCoop

CardShopCoop exposes a small public contract, **`CardShopCoop.Api`**, so another mod can add
co-op behaviour to its own content without depending on CardShopCoop's internals — and without
breaking when CardShopCoop is not installed.

The complete, buildable reference is [`samples/CardShopCoop.SampleMod`](../samples/CardShopCoop.SampleMod).
It is a shared counter: a client intent, a host-authoritative broadcast, and a join baseline.

## The three-step integration

1. **Reference `CardShopCoop.Api.dll` with copy-local off.**
   ```xml
   <ProjectReference Include="...\CardShopCoop.Api\CardShopCoop.Api.csproj">
     <Private>false</Private>
   </ProjectReference>
   ```
   `CardShopCoop.Api.dll` reaches you three ways: it ships inside the `CardShopCoop` plugin folder,
   inside the core release zip `CardShopCoop-<version>.zip`, and as the standalone release asset
   `CardShopCoop.Api-<version>.zip` (which contains `CardShopCoop.Api/CardShopCoop.Api.dll` and its
   XML documentation) so you can compile against it directly. Never ship your own copy: a second
   copy in another plugin folder can shadow the shipped one (version skew, binding type mismatch) or
   load as a second assembly identity, and nothing will line up.

2. **Declare a soft BepInEx dependency.**
   ```csharp
   [BepInDependency("com.zwhit.cardshopcoop", BepInDependency.DependencyFlags.SoftDependency)]
   ```
   Soft means your plugin still loads (and simply does nothing co-op related) when CardShopCoop is
   absent. CardShopCoop also uses this dependency to discover you.

3. **Mark your DTOs and behaviours with the public attributes.** That is the entire integration.

## Discovery: no registration call

CardShopCoop scans the BepInEx plugin graph for plugins that depend on it and registers their
assemblies automatically. It picks up:

- every `[NetworkMessage]` DTO from your assembly,
- every `[ServerBehaviour]` / `[ClientBehaviour]` / `[PersistentBehaviour]` component,
- lifecycle events (`[OnClientJoined]`, `[OnClientDisconnected]`, `[OnFullyJoined]`,
  `[OnSessionStarted]`, `[OnSessionStopped]`).

Message handlers are bound **per behaviour instance**, not picked up globally: every behaviour
must call `Messages.RegisterAttributedHandlers(this)` from `OnEnable` and
`UnregisterAttributedHandlers(this)` from `OnDisable` (the example below shows it). If a handler
is never bound, inbound messages for it are dropped with a rate-limited warning in the log.

There is nothing else to call and no central allowlist to edit. If you cannot or do not want to
declare the dependency, `CoopApi.Register(assembly)` is an explicit escape hatch: call it from
your plugin's `Awake` on the main thread and only once CardShopCoop is loaded — guard with
`CoopApi.IsAvailable` first, because without a dependency BepInEx does not guarantee load order
(which is why auto-discovery is preferred). It throws when the binding is not installed. A late
call still registers your `[NetworkMessage]` DTOs, but they only apply from the next session, and
`[PersistentBehaviour]`s are not instantiated (an error is logged). Do not send a late-registered
DTO before then: it is not in the active session's catalog, so the send fails and can force session
recovery.

## Writing a behaviour

A message is a `[NetworkMessage]` type that **also implements `INetMessage`**; it needs a
parameterless constructor or another constructor JSON.NET can use.

```csharp
[NetworkMessage]
public sealed class MyIntent : INetMessage
{
    public Guid PredictionId { get; set; }
    public int Value;
}
```

Derive from `CardShopCoop.Api.CoopBehaviour` (or use any `MonoBehaviour` with the attribute and
read `CoopApi.Context`). The runtime creates the component at the right moment and assigns
`Context`.

```csharp
[ServerBehaviour]
public sealed class MyHostBehaviour : CoopBehaviour
{
    private ICoopContext _context;

    private void OnEnable()
    {
        _context = Context;
        _context.Messages.RegisterAttributedHandlers(this);
    }

    private void OnDisable()
    {
        _context?.Messages.UnregisterAttributedHandlers(this);
        _context = null;
    }

    [OnFullyJoined]
    private void SendBaseline(PeerConnection connection)
        => _context.Send(connection.Id, new MyState { Value = _value });

    [MessageHandler(typeof(MyIntent))]
    private void HandleIntent(CoopMessageContext context, MyIntent intent)
    {
        // host validates, applies, then broadcasts the result
        _context.Broadcast(new MyState { Value = ++_value });
    }
}
```

Message handlers take `CoopMessageContext` (`ConnectionId`, `Connection`, `IsAuthenticated`,
`InGame`). `ICoopContext` gives you `IsHost`/`IsClient`/`InSession`/`InGame`, `ConnectionIds`,
`HostConnectionId`, `PeerName`, `Send`, `Broadcast`, and `Messages`.

`[OnClientJoined]` fires as a client is admitted to the session — on the host once the client
begins its world transfer, and on the client once the host accepts it — always before
`[OnFullyJoined]`. Use it for handshake bookkeeping only: app traffic such as a baseline must wait
for `[OnFullyJoined]`, when the peer is fully in game.

## Prediction (optional)

`CoopPredict` exposes the same generic prediction system the built-in features use. Prediction is
client-side only: `CoopPredict.IsAvailable` is false on the host. The `scope` you pass is
automatically namespaced with your assembly name, so short keys are safe. The `send` closure must
actually transmit the intent carrying the returned id, or there is nothing to later roll back or
acknowledge:

```csharp
CoopPredict.Predict("mymod", id => _context.Send(1, new MyIntent { PredictionId = id }),
    apply: () => ApplyState(predicted), undo: () => ApplyState(prior));

// host side, on rejection:
CoopPredict.Rollback(_context, context.ConnectionId, intent.PredictionId);

// client side, on the authoritative result:
CoopPredict.AckOrApply(intent.PredictionId, () => ApplyState(message.Value));
// ...or, when the authoritative value supersedes your optimistic run and must always apply:
CoopPredict.Confirm(intent.PredictionId, () => ApplyState(message.Value));
```

A DTO that carries a prediction id may implement `IPredictedMessage`.

## Helpers

- `CoopApi.Register(assembly)` — explicit registration escape hatch for a mod without a BepInEx
  dependency; guard with `CoopApi.IsAvailable` and call from `Awake` on the main thread. See
  **Discovery** for the full contract; auto-discovery is preferred.
- `CoopLog.Info/Warn/Error`, `CoopLog.Swallow` — logging that no-ops when the API is loaded but
  CardShopCoop has not installed its binding. They are still a hard reference to the API assembly,
  so read the guard rule under **What to expect** before calling them when CardShopCoop may be
  absent.
- `CoopReflection.OptionalType/Field/Method/Property` — optional cross-mod reflection with
  one-shot diagnostics; `Required*` variants throw loudly.

## What to expect

- **Works without CardShopCoop.** Your plugin loads either way. The API assembly ships with
  CardShopCoop, so when CardShopCoop is absent the API types cannot be resolved at all. The
  attribute path needs no guard: a behaviour whose base type cannot load is simply never
  discovered, and your plugin still loads. Every direct call to `CoopApi`, `CoopLog`, `CoopPredict`,
  or `CoopReflection` must be guarded, and the guarded call must live in its own method:
  ```csharp
  private void Awake()
  {
      if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.zwhit.cardshopcoop"))
      {
          return;
      }

      InitCoop();
  }

  [System.Runtime.CompilerServices.MethodImpl(
      System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
  private void InitCoop()
  {
      // only here may you touch CoopApi / CoopLog / CoopPredict / CoopReflection
  }
  ```
  The guard is checked first, but the CLR JITs a method body before it runs, so any method that
  names an API type faults as soon as it is compiled — a guard in that same method would not save
  it. Keeping the API-touching code in a separate method (and `NoInlining` so the compiler cannot
  merge them) is what makes the guard effective.
- **Both players need your mod.** The join handshake compares the exact sorted message catalog.
  If your mod adds messages and one peer does not have it, the join is refused with a catalog
  mismatch — the same rule as any other mod difference.
- **A broken external behaviour does not take the session down.** A behaviour with an invalid
  lifecycle signature or a non-`MonoBehaviour` type is logged and skipped at session start.
- **Wire identity is `Type.FullName`.** Moving or renaming a DTO changes the wire contract; bump
  your version and note it.

## Reliability

- Messages are reliable and ordered by default. `[NetworkMessage(Reliability = Reliability.Transient)]`
  opts a message into the unreliable lane for loss-tolerant state (for example position or visual
  state).
- Transient messages have no fragmentation and no reliable fallback and must stay small (roughly
  1 KB; larger sends fail and can force session recovery). Use the default reliable lane for
  anything bigger.
- An exception thrown by a reliable handler forces session recovery (both players are
  disconnected); a transient handler exception drops the message. Do not throw from handlers.

## Connection ids

- `ConnectionIds` is the local view: the host sees every client; a client sees only the host
  (id 1).
- `HostConnectionId` is the id that addresses the host from a client (always 1) and -1 on the
  host. Send intents to the host with this instead of a literal 1.
- `LocalConnectionId` is your identity in the host's roster (0 on the host; the id the host
  assigned on a client). It is not necessarily a valid target in your own connection-id space.
- `Send(id, message)` targets one peer; `Broadcast(message)` fans out to all ready peers from the
  host and sends to the host from a client; `PeerName(id)` returns a display name or null.

## API compatibility

The API assembly version follows the plugin version, but the assembly is unsigned, so binding is
by simple name. Additive API changes are safe within a patch or minor release; renamed or removed
members are breaking and are called out in `CHANGELOG.md`. XML docs ship in the API zip and beside
the installed DLL.

## API surface at a glance

| Type | Purpose |
| --- | --- |
| `CoopApi` | availability, live context, optional log, explicit registration |
| `CoopBehaviour` | base component that carries `Context` |
| `ICoopContext` | live session: role, connections, `HostConnectionId`, send/broadcast, message registry |
| `CoopMessageContext` | inbound message context for external handlers |
| `CoopPredict` / `IPredictedMessage` | generic client prediction |
| `CoopLog` / `CoopReflection` | logging and reflection helpers (guard direct calls when CardShopCoop may be absent) |
| `[ServerBehaviour]`, `[ClientBehaviour]`, `[PersistentBehaviour]`, `[OnClientJoined]` | behaviour roles and join lifecycle |
| `[NetworkMessage]`, `[MessageHandler]`, `INetMessage` | message contract |
| `PeerConnection`, `ConnectionState`, `DisconnectInfo` | connection contract |
