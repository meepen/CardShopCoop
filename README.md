# Community Multiplayer Mod

Run the card shop together.

Community Multiplayer Mod turns **TCG Card Shop Simulator** into a shared co-op shop:
one shop, one wallet, one collection, and one living simulation. Both players can stock
shelves, serve the register, haggle with trade-in customers, open packs, order furniture,
and watch the same customers move through the same aisles.

The goal is a full vanilla-feeling co-op experience. The host runs the real game simulation
and the other player joins that shop as a seamless working partner, using the game's normal
interactions wherever possible instead of separate or simplified multiplayer systems.

## Features

- **Steam, LAN, and direct-IP multiplayer** — invite friends through Steam, host a
  searchable public lobby with an optional password, or connect over LAN/direct IP. Invite
  codes and automatic UPnP port forwarding make direct connections easier.
- **One shared shop** — money, XP, level, fame, the card collection (including graded
  cards), item and card prices, shelf stock, card display walls, delivery boxes, furniture,
  licenses, bills, room expansions, decorations, shop signs, tournaments, grading, the
  daily market, and the end-of-day report are shared.
- **Both players can work** — serve the register by clicking items or holding V, run real
  trade-in and sell-in screens with haggling, restock shelves, set prices, order stock and
  furniture, hire staff, pay bills, buy licenses, and purchase expansions.
- **A live shared world** — customers, workers, avatars, carried boxes, card fans, binders,
  the day/night cycle, and lighting are mirrored with smooth interpolated motion instead of
  teleporting or jittering.
- **Content-mod friendly** — built for the PTCGO / Enhanced Prefab Loader ecosystem. Join
  checks compare plugin sets and custom-card registries, explain mismatches clearly, back up
  and sync the card database when needed, warn about different product catalogs, and refund
  orders for products the host cannot provide.
- **Optional Custom TV synchronization** — with the RTCGO Custom TV pack installed on both
  PCs, stream selection, power, pause, playlists, and best-effort playback position can be
  shared. Volume and mute remain local.
- **Safe joining** — the joining player receives the host's current shop in a dedicated
  scratch slot. Their own solo saves are never overwritten or saved over while visiting.

## Compatibility

- Tested only on the newest game version at the time of writing: **TCG Card Shop Simulator
  0.70.3**.
- Requires **BepInEx 5.4.23 x64** installed in the game folder on both PCs.
- Both players must use the same Community Multiplayer Mod version.
- Both players should use the same BepInEx plugins and content packs. The join screen
  reports missing, extra, and mismatched plugins; content-pack differences are reported
  separately.
- Steam and Xbox Game Pass builds are supported through the connection methods available
  to each build. The game version must match between players. Cross-build joining is an
  advanced, potentially unsafe option and should only be used for supervised testing.
- Designed and tested primarily for two players. Additional joiners can be relayed by the
  host but are less heavily tested.

## Mod compatibility

- **Grading Overhaul** — [Nexus Mods page](https://www.nexusmods.com/tcgcardshopsimulator/mods/612).
  Optional integration for graded cards, grading companies, grades, certificate numbers,
  graded-card prices, and submissions from either player. Both players should use the same
  Grading Overhaul version.
- **RTCGO Custom TV** — [Nexus Mods page](https://www.nexusmods.com/tcgcardshopsimulator/mods/895).
  Optional integration that shares stream selection, power, pause, playlists, and best-effort
  playback position when the TV pack and working stream tools are installed on both PCs.
  Volume and mute remain local to each player.

## Installation

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx) (5.4.23 x64) into the game
   folder on both PCs.
2. Download the release and place `CardShopCoop.dll` in `BepInEx/plugins/` on both PCs.
3. Start the game and press **F2** to open the multiplayer window.

## Quick start

**Host:** Load your save normally, press F2, choose Steam, LAN, or direct IP, then click
**Host**. Steam hosting can be friends-only or public with an optional password.

**Friend:** Press F2 and join through a Steam invite, the public lobby browser, an invite
code, or the host's LAN/direct-IP address. The shop normally downloads in about 30–60
seconds, then you appear in the host's store.

If the custom-card database differs, the mod backs up the local copy, syncs the host's
copy, and asks you to restart and join again. This is expected and normally requires one
restart.

## Good to know

- The host's save is the shared world. The joining player is a guest and their own saves
  are never written while visiting.
- Staff hiring is supported for both players; advanced staff management such as firing,
  tasks, and bonuses remains host-only where the base game provides no guest interaction
  path.
- Steam achievements continue to progress per player.
- The joining player cannot sit down for the play-table minigame; table layouts are still
  mirrored visually.
- If newly installed content-pack items show a price of $0, the host may need to use the
  PTCGO Economics price-reset hotkey. Corrected prices then sync normally.

## Troubleshooting

- **"Your mod set differs"** — compare the `BepInEx/plugins` folders and make sure both
  players use the same plugin versions.
- **"Your custom-card database differed"** — restart the game completely and join again so
  the synchronized database is loaded.
- **"Product catalogs differ"** — compare content DATA packs, including the small `.json`
  files beside larger bundle files. These are not always visible to the plugin-set check.
- For other issues, collect the detailed log from both PCs:
  `BepInEx/CardShopCoop_<number>.log`.

## Source and license

Source code: [github.com/meepen/CardShopCoop](https://github.com/meepen/CardShopCoop)

Licensed under the [MIT License](https://github.com/meepen/CardShopCoop/blob/main/LICENSE).
