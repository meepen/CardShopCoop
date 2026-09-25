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
   `CardShopCoop.Api.dll` ships inside the `CardShopCoop` plugin folder and is also published as its
   own release asset (and inside the core package) so you can compile against it directly. Never
   ship your own copy: a second copy in another plugin folder can shadow the shipped one (version
   skew, binding type mismatch) or load as a second assembly identity, and nothing will line up.

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

- every `[NetworkMessage]` / `[MessageHandler]` DTO and handler,
- every `[ServerBehaviour]` / `[ClientBehaviour]` / `[PersistentBehaviour]` component,
- lifecycle events (`[OnClientJoined]`, `[OnClientDisconnected]`, `[OnFullyJoined]`,
  `[OnSessionStarted]`, `[OnSessionStopped]`).

There is nothing to call and no central allowlist to edit. If you cannot or do not want to
declare the dependency, `CoopApi.Register(assembly)` is an explicit escape hatch — call it during
your plugin's `Awake`, before the first frame (auto-discovery is preferred, and persistent
behaviours from a manual registration are only picked up if registration happens before then).

## Writing a behaviour

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
`PeerName`, `Send`, `Broadcast`, and `Messages`.

## Prediction (optional)

`CoopPredict` exposes the same generic prediction system the built-in features use:

```csharp
CoopPredict.Predict("mymod", id => _context.Send(1, new MyIntent { PredictionId = id }),
    apply: () => ApplyState(predicted), undo: () => ApplyState(prior));

// host side, on rejection:
CoopPredict.Rollback(_context, context.ConnectionId, intent.PredictionId);

// client side, on the authoritative result:
CoopPredict.ApplyAuthoritative(intent.PredictionId, () => ApplyState(message.Value));
```

A DTO that carries a prediction id may implement `IPredictedMessage`.

## Helpers

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
- **Wire identity is `Type.FullName`.** Moving or renaming a DTO changes the wire contract; bump
  your version and note it.

## API surface at a glance

| Type | Purpose |
| --- | --- |
| `CoopApi` | availability, live context, optional log, explicit registration |
| `CoopBehaviour` | base component that carries `Context` |
| `ICoopContext` | live session: role, connections, send/broadcast, message registry |
| `CoopMessageContext` | inbound message context for external handlers |
| `CoopPredict` / `IPredictedMessage` | generic client prediction |
| `CoopLog` / `CoopReflection` | logging and reflection helpers (guard direct calls when CardShopCoop may be absent) |
| `[ServerBehaviour]`, `[ClientBehaviour]`, `[PersistentBehaviour]` | behaviour roles |
| `[NetworkMessage]`, `[MessageHandler]`, `INetMessage` | message contract |
| `PeerConnection`, `ConnectionState`, `DisconnectInfo` | connection contract |
