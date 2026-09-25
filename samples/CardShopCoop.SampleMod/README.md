# CardShopCoop.SampleMod

The smallest complete integration with CardShopCoop, kept intentionally tiny so it can be copied
as a starting point. It synchronises a shared counter:

- **Client** press K → sends `SampleIncrementIntent` to the host (connection id 1).
- **Host** press K → increments locally and broadcasts `SampleCounterState`.
- **Host** applies a client intent, then broadcasts the authoritative value.
- **Host** sends one baseline to each late joiner.

Everything is logged with `CoopLog`, so watch `BepInEx/LogOutput.log` (or the console) to see it
work. No UI is drawn on purpose.

## Files

| File | What it shows |
| --- | --- |
| `SamplePlugin.cs` | the whole dependency footprint: soft `[BepInDependency]` only |
| `SampleCounterMessages.cs` | `[NetworkMessage]` DTOs |
| `SampleCounterHost.cs` | `[ServerBehaviour]`, join baseline, intent handling, broadcast |
| `SampleCounterClient.cs` | `[ClientBehaviour]`, intent send, authoritative apply |

## Build

```powershell
dotnet build samples\CardShopCoop.SampleMod\CardShopCoop.SampleMod.csproj -c Release
```

Or, from the solution, build the **Deploy** configuration with `-p:DeploySample=true` to build the
plugins and copy this sample into `BepInEx/plugins/`:

```powershell
dotnet build CardShopCoop.sln -c Deploy -p:DeploySample=true
```

The plugin DLL lands in the project's `bin/Release`. Copy it to `BepInEx/plugins/` together
with CardShopCoop (and its `CardShopCoop.Api.dll`). Both players need this sample mod for the
messages to line up in the join handshake.

Note `Private=false` on the `CardShopCoop.Api` reference: the API assembly ships with
CardShopCoop, and shipping a second copy can load as a second assembly identity.
