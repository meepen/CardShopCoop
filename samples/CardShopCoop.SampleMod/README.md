# CardShopCoop.SampleMod

The smallest complete integration with CardShopCoopCommunity, kept intentionally tiny so it can be copied
as a starting point. Inside this repository it builds against CardShopCoop through a project
reference; copied outside the repo it needs a `GamePath` pointing at a game install with BepInEx,
plus a reference to `CardShopCoop.Api.dll` with copy-local off (never ship your own copy). It
synchronises a shared counter:

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

`GamePath` must point at the game install: it is what resolves the BepInEx and Unity assemblies.
When building outside this repository, the API DLL comes from the CardShopCoop install or package
(the in-repo sample uses the project reference shown above).

Or, from the solution, build the **Deploy** configuration with `-p:DeploySample=true` to build the
plugins and copy this sample into `BepInEx/plugins/`:

```powershell
dotnet build CardShopCoop.sln -c Deploy -p:DeploySample=true
```

The plugin DLL lands in the project's `bin/Release`. Copy it to `BepInEx/plugins/` together
with CardShopCoopCommunity (and its `CardShopCoop.Api.dll`). The real join gate is stricter than "messages
line up": both players must run the same plugin set (a plugin-parity check) and expose an exact
message-catalog match. If either differs, the join is refused with a mismatch reason rather than
silently proceeding.

Note `Private=false` on the `CardShopCoop.Api` reference: the API assembly ships with
CardShopCoopCommunity, and shipping a second copy can load as a second assembly identity.
