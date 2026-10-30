# Local split-screen on one PC: Nucleus Co-op + CardShopCoopCommunity

Investigation into running TCG Card Shop Simulator as **two game processes on one Windows PC** —
Nucleus Co-op style, each process driven by its own input device and shown in its own screen
region — with CardShopCoopCommunity (v2.0.7) providing the actual co-op session.

Status: **feasibility investigation, not a shipped handler.** Every claim below is evidence-backed
from this repository (file:line) or the decompiled game (`decompiled/1.02`). Items that are
inferred or unverified are marked **[VERIFY]**.

---

## 1. Verdict

It can work, and it needs *no change to CardShopCoop's networking to prove out* — on one
condition: the two instances run as a **host + joiner pair**, never two hosts.

- The base game has **no multiplayer and no LAN/online path** of its own, so Nucleus alone cannot
  connect anything (Nucleus explicitly does not add multiplayer). CardShopCoop supplies the
  connection; Nucleus supplies the *instances, layout, and input separation*.
- CardShopCoop's built-in **IP transport works over loopback** with no Steam: host binds
  `0.0.0.0:27886`, joiner dials `127.0.0.1`. Nothing in the code assumes peers are on different
  machines.
- CardShopCoop already exposes **auto-drive command-line arguments** (`-coopautohost=<slot>`,
  `-coopautojoin=<ip>`), which is exactly the hook a Nucleus handler needs so the two instances
  connect without the user typing an IP.
- The two instances are, by construction, **two players**: every layer of the mod resolves a single
  local player per process, and the other process appears as a remote avatar.

The practical work is **isolation and window/input plumbing**, all of which lives on the Nucleus
side (launch args, symlink rules, environment redirection, XInput routing), not in the mod.

---

## 2. Who owns what

Four owners. The important design fact is that **Nucleus and CardShopCoop do not overlap**: Nucleus
owns the *process/window/input* layer, CardShopCoop owns the *session* layer, the game owns
*gameplay*, and the handler is the glue spec.

| Concern | Owner | Mechanism (evidence) |
| --- | --- | --- |
| Launch the second game process | **Nucleus** | `Game.ExecutableName` / `Game.LauncherExe`; per-instance launch args |
| Defeat a single-instance lock | **Nobody needed** | Base game has none — no `Mutex`/`GetProcessesByName`/single-instance check in `decompiled/1.02`. (Nucleus `Game.KillMutex` available if a future build adds one) |
| Multi-instance file layout (symlink / hard-copy per instance) | **Nucleus** | `Game.DirSymlinkExclusions`, `Game.FileSymlinkCopyInstead`, `Game.ForceSymlink`; `content/<GUID>/InstanceN` |
| Window layout, resize, borderless, positioning, top-most | **Nucleus** | `Game.SetTopMostAtEnd`, `Game.PromptBetweenInstances`, resize/reposition hooks |
| Fake focus / keep an unfocused window running | **Nucleus** (focus hooks) + **CardShopCoop** (`Application.runInBackground = true`, `CoopCore.cs:1323`; no `OnApplicationFocus` handler anywhere) |
| Per-gamepad input routing (each instance sees one pad) | **Nucleus** | `Game.XInputPlusDll` custom XInput proxy, x360ce, dinput8 blocker |
| Multiple keyboard/mouse sets | **Nucleus** | Proto Input hooks; `End` hotkey locks routing |
| Local input → gameplay, camera, cursor lock | **Base game** | game's own `InteractionPlayerController` + CMF; the mod reads **no** gamepad/axis input |
| Modal / cursor / pause / `Time.timeScale` ownership | **CardShopCoop** | `Modules/SessionInput/SessionInputRuntime.cs` (process-wide modal owner) |
| Session networking (transport, reliability, QoS) | **CardShopCoop** | `Net/Networking/IpNetworkingBackend.cs`, `Net/Kcp*`, `CoopTransportGroup` |
| Connection setup path (address/lobby) | **CardShopCoop** | IP typed address (`ip` or `ip:port`), optional Steam lobby |
| Join handshake (version / catalog / hash validation) | **CardShopCoop** | `CoopCore.HandleHello/HandleWelcome` (`CoopCore.cs:869-1102`); Steam-independent |
| World transfer (host snapshot / guest borrowed world) | **CardShopCoop** | `Modules/SaveTransfer/*`; host slot 6, guest slot −100 |
| Peer identity, roster, names, remote avatars | **CardShopCoop** | `Modules/Presence/*`; roster keyed by the host-canonical `PlayerRef` identity, not SteamID |
| Save files / PlayerPrefs persistence | **Base game** | `CSaveLoad` → `Application.persistentDataPath`; PlayerPrefs → registry |
| Per-instance user-profile isolation (saves, config, PlayerPrefs) | **Nucleus** | `Game.UseNucleusEnvironment`, `Game.UserProfileConfigPath`, or `LaunchAsDifferentUsers` |
| Registry backup/restore across a session | **Nucleus** | handler registry-edit backup/restore |
| Achievements | **Base game** (Steam) | `AchievementManager` → Heathen Steamworks; shared account |
| Rendering | **Base game** | one full render per process (2× GPU cost, no shared camera) |
| Connection emulation (Goldberg/Nemirtingas/SSE) | **Not used** | the IP backend is standalone and needs no Steam emulation |

---

## 3. The session, end to end (two processes, one PC)

```
        Nucleus Co-op (owner: instances, windows, input, isolation)
        │  launches from content/<GUID>/Instance0 and Instance1
        ├───────────────────────────────┐
        ▼                               ▼
  Instance 0 (host)                Instance 1 (joiner)
  game exe + BepInEx + CardShop    game exe + BepInEx + CardShop
  arg: -coopautohost=0             arg: -coopautojoin=127.0.0.1
  binds UDP 0.0.0.0:27886    ◄──►  binds ephemeral port, dials 127.0.0.1:27886
  KCP over UDP (loopback)  ───────  Hello/Welcome (version + catalog + hash)
  authoritative world              applies host world to borrowed slot −100
  presence: no avatar for self     renders the host as a remote avatar
        ▲                               ▲
        └──── one gamepad each ─────────┘   (Nucleus XInput proxy per instance)
```

- **Transport:** `IpNetworkingBackend` (default port 27886, `CoopPlugin.cs:82`, `IpNetworkingBackend.cs:29`).
  Host binds `IPAddress.Any` (`UdpDatagramTransport.cs:110-115,222-231`); joiner binds ephemeral
  (`:121-126`). KCP reliable/ordered rides on top; the pump runs on its own thread.
- **Address handoff:** direct-connect only, no discovery. The `-coopautojoin=<ip>` argument supplies
  the address the user would otherwise type, so the handler makes it automatic
  (`CoopCore.cs:259-280`, `AutoHost`/`AutoJoinIp` at `:1860-1876`).
- **Handshake:** exact `CardShopCoopVersion`, game/Unity build, plugin hash, and the ordinally
  sorted message catalog must match (`CoopCore.cs:869-1102`). Both processes run the *same* DLL, so
  this is trivially satisfied. The handshake carries a `SteamId` but **does not gate on it** —
  `ResolvePeerName` ignores it (`CoopCore.cs:1653-1656`).
- **World transfer:** host snapshot is saved to game slot **6** and read back; the joiner writes the
  borrowed world to reserved slot **−100** and loads it through the game's own `CSaveLoad`
  (`Modules/SaveTransfer/SaveTransferStorage.cs:22,29,48-52`). A host+joiner pair therefore touches
  **disjoint** save files (host: real slots + 6; guest: −100 only).

---

## 4. Isolation: what is shared and how to separate it

Two processes from one install share a lot of machine-wide state. This is where a real handler does
its work.

| State | Location | Shared by default? | How to isolate |
| --- | --- | --- | --- |
| Mod log | `<install>\BepInEx\CardShopCoopCommunity_{pid}.log` | **No** (PID in name, `Util/FileLog.cs:28-32`) | already safe |
| BepInEx master log | `<install>\BepInEx\LogOutput.log` | Yes (exclusive to instance 1, `FileLog.cs:9-11`) | not fixable; per-PID mod log compensates |
| Mod config `.cfg` | `<install>\BepInEx\config\dev.meepen.cardshopcoop.cfg` | **Yes** (`CoopPlugin.cs:82-129`) | per-instance install folder (exclude `BepInEx` from symlink) |
| Local avatar model | `<install>\BepInEx\config\CardShopCoopPlayerModel.json` | **Yes** (`PresenceModelStore.cs:13-14`) | per-instance install folder |
| Game saves + co-op slots | `%USERPROFILE%\AppData\LocalLow\OPNeonGames\Card Shop Simulator\` | **Yes** | `Game.UseNucleusEnvironment` / `LaunchAsDifferentUsers` |
| Mod sidecars + EPL enum registry | same `persistentDataPath` tree | **Yes** (`SaveTransferSidecars.cs:56-165,438-443`; `CatalogParity.cs:458-468`) | user-profile isolation |
| Game character file | `<install>\..._Data\CharacterCustomizer.json` | **Yes** (via symlink) (`CC/CharacterCustomization.cs:35,50`) | `Game.FileSymlinkCopyInstead` |
| PlayerPrefs / options | `HKCU` registry | **Yes** | `LaunchAsDifferentUsers` (different user = different hive) |
| UDP port 27886 | host bind | **Only a conflict if both host** (`UdpDatagramTransport.cs:502-524`, no `SO_REUSEADDR`) | host+joiner pair, or per-instance config |

**Rules that follow from the table:**

1. **One host, one joiner.** Never two hosts on 27886 (second bind throws) and never two joiners
   (both write slot −100). A host+joiner pair is collision-free on slots and ports.
2. **Isolate the user profile** to keep saves, sidecars, and PlayerPrefs apart. Because
   CardShopCoop's host scan walks every mod-data file under `persistentDataPath`, a shared root can
   sweep the other instance's files into a transfer (`SaveTransferHostBehaviour.cs:169`,
   `SaveTransferSidecars.cs:56-165`). `Game.UseNucleusEnvironment` redirected per player, or
   `LaunchAsDifferentUsers`, removes this.
3. **Isolate the install folder per instance** to keep the two configs (`PlayerName`, `Port`,
   module toggles) from clobbering each other in the one `.cfg`.
4. **Do not use the Steam transport.** Two processes of one Steam account cannot host/join each
   other as distinct SteamIDs (`SteamNet.cs:816-831`, `SteamDatagramTransport.cs:93-104,299-301`).
   The IP backend needs none of it ([VERIFY] exact failure mode not reproduced).

---

## 5. Input and focus

This is the part that decides whether split-screen actually *plays*.

- **Nucleus routes devices; the game consumes them; CardShopCoop ignores them.** CardShopCoop has
  **no** gamepad/axis/device code — it only patches the game's single `InteractionPlayerController`
  and CMF camera/cursor path (`Modules/SessionInput/SessionInputPatches.cs`,
  `SessionInputRuntime.cs:36-52`). So each instance simply needs to *see* its own pad, which is
  exactly what Nucleus's XInput proxy (`Game.XInputPlusDll`) does.
- **One player per process is assumed and is fine here.** Single `CoopCore` singleton
  (`CoopPlugin.cs:169-176`), a single local connection identity (`CoopCore.cs:147`), and
  `SceneRef<T>` resolves exactly one instance (`SceneRef.cs:135-166`). Two processes ⇒ two
  connections ⇒ two avatars.
- **Unfocused instances keep running — the single biggest de-risker.** The mod sets
  `Application.runInBackground = true` each frame (`CoopCore.cs:1323`), has no
  `OnApplicationFocus`/`OnApplicationPause` handler, and never reads `Application.isFocused`. Nucleus
  cannot put two windows in the foreground at once; this mod doesn't need them to be.
- **Each process owns its own modal/cursor/freeze state.** `SessionInputRuntime` is per-process
  static state: it claims `InteractionPlayerController` UI mode, shows/hides the cursor, stops the
  walker, and drives `Time.timeScale` for the co-op window (`SessionInputRuntime.cs:279-299,323-484`).
  Opening the co-op window (default `F2`, `CoopPlugin.cs:99`) affects only the instance it is pressed
  in — but the two instances share the *same* hotkey, so it should be rebound per instance if both
  players need it simultaneously.

---

## 6. What Nucleus *cannot* do here (and why that is fine)

- **Add multiplayer.** Nucleus starts instances and connects them through a path the game (or a mod)
  already provides. TCG Card Shop Simulator has none; CardShopCoop is that path. Nucleus is the
  launcher/layout/input half; CardShopCoop is the session half.
- **Share the render.** Two windows means two full 3D renders. Expect ~2× GPU and heavy CPU; set
  per-instance processor affinity, cap FPS, and use `Ctrl+H`/unfocus to equalize the two.
- **Isolate the mod config for free.** It must be told (symlink exclusions / environment) to do so.

---

## 7. Reference handler sketch (illustrative — not tested)

Property names below are the documented Nucleus handler API; the **verified** half is the
CardShopCoop side (`-coopautohost` / `-coopautojoin`). Confirm exact field names against
`MasterHandler.js` before use. **[VERIFY]**

```js
// doc-only illustration; lives in NucleusCo-op\handlers\...
Game.ExecutableName = "Card Shop Simulator.exe";
Game.GUID = "TCG Card Shop Simulator";
Game.GameName = "TCG Card Shop Simulator (Co-op)";
Game.MaxPlayers = 2;
Game.MaxPlayersOneMonitor = 2;

// Per-instance install layout so each process gets its own BepInEx config + logs.
Game.DirSymlinkExclusions = ["BepInEx"];        // don't share config between instances
Game.FileSymlinkCopyInstead = ["CharacterCustomizer.json"];

// Input: one pad per instance.
Game.XInputPlusDll = ["xinput1_3.dll"];

// User-profile isolation: keeps saves, sidecars and PlayerPrefs apart.
Game.UseNucleusEnvironment = true;

// Auto-drive the session: instance 0 hosts, instance 1 joins over loopback.
// Exact per-instance launch-arg hook to confirm in MasterHandler.js. [VERIFY]
Game.Play = function () {
    Context.StartArguments = (Context.PlayerID === 0)
        ? "-coopautohost=0"
        : "-coopautojoin=127.0.0.1";
};

Game.HandlerNotes =
    "Both instances run CardShopCoopCommunity. Instance 1 auto-joins the host on loopback.\n" +
    "Do NOT use the Steam transport for same-PC play.";
```

---

## 8. Gaps, risks, and open questions

| # | Item | Severity | Notes |
| --- | --- | --- | --- |
| 1 | Exact Nucleus per-instance launch-argument hook | blocker for full automation | The mod side is done; only the handler field name is unconfirmed. Fallback: user types `127.0.0.1` once (the mod persists `LastJoinIP`, `IpNetworkingBackend.cs:133-148`). |
| 2 | BepInEx config hot-reload of a shared `.cfg` | medium | If BepInEx 5 watches and reloads the config, one instance's UI change could mutate the other mid-session — another reason to isolate `BepInEx` per instance. **[VERIFY]** |
| 3 | Steamworks init for a second same-account process | low | Affects achievements only; the IP session is unaffected. **[VERIFY]** |
| 4 | Game requires a windowed/borderless-capable mode for resize | medium | Unity fullscreen may need the handler to force windowed before Nucleus positions windows. **[VERIFY in-game]** |
| 5 | Both-host / both-join misuse | high if misconfigured | Save-slot and port collisions. Enforce host+joiner in the handler notes and via the launch args. |
| 6 | 2× render cost | medium | Per-instance affinity + FPS cap. |
| 7 | Game Pass build | high | Nucleus cannot symlink/multi-instance Game Pass DRM; only the Steam/default install is viable. |
| 8 | Version/catalog drift between the two processes | none | Same DLL, same build — handshake always matches. Requires both instances updated together, as usual. |

---

## 9. Recommended shape for a real implementation

1. **Prove the loopback pair first, no Nucleus.** Launch two instances manually (or with
   `-coopautohost=0` / `-coopautojoin=127.0.0.1`), one host + one joiner, and confirm a session
   forms and both players move. This validates the transport half in isolation.
2. **Add the handler** with `DirSymlinkExclusions = ["BepInEx"]`, `UseNucleusEnvironment = true`,
   `Game.XInputPlusDll`, and the per-instance launch args.
3. **Then tune input/window:** pad assignment, `Ctrl+H` unfocus, per-instance affinity/FPS caps.
4. **Only if a gap appears** (e.g. config truly cannot be isolated, or the mod needs to advertise
   loopback differently) does *mod* code change. None is required for the concept.

### Evidence index

- Transport: `src/CardShopCoop/Net/Networking/IpNetworkingBackend.cs`,
  `CoopTransportGroup.cs`; `Net/Datagrams/UdpDatagramTransport.cs`;
  `Net/InviteCode.cs`, `ReachCode.cs`; `src/CardShopCoop.Steam/Net/SteamNet.cs`,
  `src/CardShopCoop.Steam/Net/Datagrams/SteamDatagramTransport.cs`.
- Session/handshake: `src/CardShopCoop/CoopCore.cs` (`:259-280`, `:869-1102`, `:1323`, `:1860-1876`).
- Save transfer: `src/CardShopCoop/Modules/SaveTransfer/SaveTransferStorage.cs` (`:22,29,48-52`),
  `SaveTransferSidecars.cs`.
- Input/modal: `src/CardShopCoop/Modules/SessionInput/*`.
- Presence: `src/CardShopCoop/Modules/Presence/*`.
- Logging/ports: `src/CardShopCoop/Util/FileLog.cs` (`:9-32`), `CoopPlugin.cs` (`:82-129`).
- Base game: `decompiled/1.02/CSaveLoad.cs`, `AchievementManager.cs`, `CC/CharacterCustomization.cs`;
  absence of any single-instance guard across `decompiled/1.02`.
