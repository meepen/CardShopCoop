# CardShopCoop — Changelog

True co-op multiplayer for TCG Card Shop Simulator. Both players must run the
**same version** — the join handshake enforces it.

---

## 1.0.39
**Hotfix: Game Pass hosting broken in 1.0.38 — thanks Tonio's crew, whose logs from seven join attempts told the whole story in one read.** Both players must update.

**"Host snapshot failed" on Game Pass**
- **Fixed: the mod deciding you were on Steam because a Steam file exists on your PC.** It turns out the Game Pass build itself ships a hollowed-out copy of Steam's networking library (with most of its insides stripped out), and other mods sometimes bundle a full copy — either way the old check saw the file and demanded a save file Game Pass never creates. The mod now ignores that file entirely for saves and instead asks the game directly whether the save it just requested actually completed, using the game's own save counter — which works identically on both builds.
- **Bonus fix for Steam:** if the game's save succeeds but its file write fails (locked file, full disk), hosting now ships the confirmed-current world from memory instead of erroring out.
- The joiner-side world delivery no longer depends on platform detection at all.
- If a full copy of Steam's library sneaks onto a Game Pass install (bundled by another mod), the Steam buttons no longer appear as dead controls — the mod now tests whether Steam can actually run before offering any of it.
- Logs now state plainly which save backend is in use and, when a world is sent from memory, which world it was (day and shop name) — so the next report answers itself.

## 1.0.38
**One mod for everyone: the same download now works on Steam AND Xbox Game Pass — huge thanks to Jburne10, whose Game Pass port supplied the save-transfer fix and the proof it works.** Both players must update — Steam players via the launcher as usual, Game Pass players by dropping the same DLL into `BepInEx\plugins`.

**Game Pass support (new)**
- **The mod now loads and runs on the Microsoft Store / Game Pass build.** Previously it didn't just fail there — it left half-installed hooks that broke saving and customers. Now it detects that Steam isn't part of that build and cleanly runs in LAN / direct-IP mode.
- **Fixed: hosting on Game Pass never delivered the world to the joiner.** Game Pass saves live in Xbox storage containers the game's own process can't even see, so the old file-based world snapshot could never find them. On Game Pass the world is now read straight from the game's memory (Jburne10's approach) — no files involved. On Steam the slot file stays the only accepted source: if the game skips the save you get a clear error rather than a stale world.
- Game Pass play is LAN / direct IP: same house just works; over the internet the host port-forwards and the joiner enters the host's public IP. The co-op window shows exactly the options your build supports — no dead Steam buttons.
- **New safety gate: the game versions must match.** The Game Pass build can run a different game version than Steam; the join handshake now compares them and refuses a mismatched pair with a clear message instead of letting two different game builds corrupt each other. (A host who understands the risk can set `AllowCrossBuildJoin` in the config for supervised cross-play testing.)

**Invite codes + automatic port forwarding (new, for LAN / direct-IP play)**
- **Hosting now gives you a "Copy invite code" button** — one short code with your address, port, and a randomly generated session password baked in. Your friend pastes it into the new "Join by code" field and they're in. No more reading IP addresses over voice chat.
- **LAN hosting now has a session password.** A random password is generated each time you host (shown in the host panel for friends who type the IP by hand, and carried inside the invite code automatically), so an internet-reachable port is never an open door. Configurable (`AutoLanPassword`, on by default).
- **The mod now asks your router to forward the co-op port automatically (UPnP)** while you host, and removes the mapping when you stop. Works on most home routers; when the router declines, the host panel says so and the manual port-forward instructions still apply. Configurable (`AutoPortForward`, on by default).
- Steam sessions are untouched by all of this — Steam invites already cover it. (The "Join by code" field works on Steam builds too, so a Steam player can join a Game Pass host's code over IP.)

**For Steam players**
- Nothing changes: Steam lobbies, invites, and the launcher all work exactly as before. This release was verified against the Steam build line-by-line; Game Pass support is marked experimental until it's been through more field time.

## 1.0.37
**Hotfix: modded cards missing from the shared album — thanks Aetheryk and the_capyman, and especially the paired host+guest logs, which proved it in one read.** Both players must update — the launcher does it automatically.

**Pokemon (and other content-pack) cards not appearing in your partner's album**
- **Fixed: cards your partner pulled being wrongly refused as "from a card set you don't have installed" — even though you both have everything.** Since 1.0.34, the safety check that protects your album from genuinely-unknown cards asked the wrong authority: it checked the game's enum table, but content packs register their monsters as DATA, numbered 1..N per expansion — so every card past #122 in a modded set failed the check on every PC, universally, while cards #1–122 slipped through by accidental number collision with vanilla monsters (that split is why "some" cards synced and some didn't). The check now asks the same per-expansion card list the game itself uses, so data-backed modded cards pass and genuinely-missing content is still refused.
- Also hardened: card REMOVES ran no check at all and could crash outright for modded cards; two more paths (grading returns, card-box collection) applied wire cards unguarded; and the check could no longer be tricked into permanently breaking the game's item database during a loading screen.
- Better logs: refused and applied modded cards now print as Expansion#N instead of a bare number (which could render as an unrelated vanilla monster's name), refusals log once per card instead of hundreds of times, and a card that genuinely can't be processed on one PC now says so instead of vanishing silently.
- In a 3+ player session, the host now passes along card changes for content packs it doesn't have installed itself, so two guests who share a pack stay in sync even when the host lacks it.
- Your partner's cards were never deleted — they always existed on their side. After both of you update, newly pulled cards sync normally, and older stuck cards can be nudged across by having their owner place one on the card table and pick it back up (this transfers exactly one copy per card — repeating it won't duplicate).

## 1.0.36
**The join-rejection fixes — thanks Curi Cole's screenshot and the pair who tested on a pure vanilla game (that report cracked it).** Wire format extended, so 1.0.36 only connects to 1.0.36. Both players must update — the launcher does it automatically.

**"Your card database still conflicts after syncing" (the big one)**
- **We were wrong about how the content-pack loader works, and the fix follows from it.** That error told you copying the host's database can't work and that you need identical content packs. Neither is true: the loader KEEPS every ID already in the database file — it only invents fresh IDs for genuinely new content. What actually differs between two PCs with the same packs is just the ORDER the IDs were handed out (install order). So the database sync does fix it — the message now says so, the sync is attempted twice per session instead of once, and if it still fails the error tells you the actual reason (didn't fully quit to desktop, the host needs a restart, or where to hand-copy the file).
- **Fixed: a host who had ever JOINED someone couldn't auto-sync anyone afterward.** An internal "my database is borrowed" flag never cleared and made the host refuse to send its database — forever. It now only blocks when the database file genuinely doesn't match what the host is running (i.e., an actual restart is owed).

**"Same error on a pure vanilla game"**
- **Fixed: leftover files from uninstalled mods haunting the connection check.** Uninstalling your content mods doesn't delete the ID database (it lives outside the game folder), and the check would fall back to reading that stale file — so two vanilla players could conflict forever over Pokemon content neither of them still had. A game with no content loader loaded now reports a clean empty state, always.

**Under the hood**
- Groundwork for name-based ID translation: the host's registry now travels in the join handshake, and every modded ID that crosses the wire goes through a translation layer. With matching registries it changes nothing (identity, verified). The one place it's already live: content only ONE of you has installed now crosses the wire as an explicit "not installed here" marker instead of a raw foreign number — which closes a few places where a foreign ID could accidentally collide with something you DO have. Full translation (no more restart-after-sync) activates in a future version.

## 1.0.35
**The binder freeze and the price fixes — thanks to the anonymous July-27 reporter running the Pokemon pack-opener setup.** Wire format extended (card changes now travel in batches), so 1.0.35 only connects to 1.0.35. Both players must update — the launcher does it automatically.

**The 20–30 second freeze with the binder open**
- **Fixed: your game freezing while the other player opens packs or collects card machines with the binder open.** Every single card they gained used to trigger a full re-sort and rebuild of your open binder — hundreds of times for one "collect all" click. The binder now updates once per frame no matter how many cards arrive, card changes travel in one batched message per frame instead of one message per card, and a backlog is processed on a per-frame budget instead of all at once. The freeze is gone; you just see your binder totals tick up.

**Co-op player's card prices (both directions)**
- **Fixed: the joiner's card prices not sticking / never reaching the host.** A price you set was sent exactly once with no confirmation — if that one message got squeezed out (which the pack-opening flood above did reliably), the host never saw it, and the host's periodic price re-sync then painted its own OLD price right back over yours. That's why it felt like you "couldn't set prices at all." Now the joiner re-sends until the host confirms, the host's re-sync can't overwrite a price you just set, the host confirms every applied price back (which also finally delivers your prices to a third player), and mismatched currency settings between PCs no longer make confirmations impossible.
- The host now says so in the log when it *can't* store a price (card set not installed, or a modded price store rejecting the write) instead of failing silently — and when the host can't store it but the other players can, it passes the price along anyway.
- Item prices got the same safety window (the host's bulk price table can no longer repaint an item price you set seconds ago), and a rejected item price now shows a message on your screen instead of silently reverting later.

**Under the hood (co-op safety)**
- A stale session's not-yet-applied card changes can no longer replay into your NEXT session's collection (they're now cleared on disconnect).
- One corrupt card change in a batch now costs exactly that one card, not the whole batch.
- A declined purchase can no longer slip through as free product if the charge and the delivery get split across a message backlog.

## 1.0.34
**Fixes for the latest report batch — thanks Metz, Domworth, Toat, and Skibbles (whose repro notes basically drew the map).** Wire format extended, so 1.0.34 only connects to 1.0.34. Both players must update — the launcher does it automatically.

**The recap screen (Metz)**
- **Fixed: the joiner getting stuck in the end-of-day recap.** The recap's Next Day button silently did nothing for guests, the only exit was an invisible click-anywhere, and once stuck, every later night's recap was swallowed too. Now the button closes it, the host advancing the day closes it automatically, and your movement unlocks properly.

**The "custom card database" loop, part 2 (Toat)**
- **Fixed: the database-differs loop when your PLUGINS match but your CONTENT packs don't.** The game rebuilds its card-ID registry from YOUR installed content on every boot, so copying the host's database could never stick when content differed — infinite sync-restart-reject. The check now compares the IDs your game is ACTUALLY running (not a file), only rejects on a genuine conflict (same card name mapped to different IDs), and allows one-sided extras — they just show the existing "catalogs differ" heads-up. If a true conflict can't be fixed by syncing, you now get one honest message telling you to match content packs instead of an endless loop. Bonus: far fewer database syncs = far fewer "solo saves won't load" incidents.
- Safety net that made this possible: a card from a set you don't have installed is now skipped with a clear log line — previously it silently credited your first Tetramon card.

**Items vanishing from shelves (Domworth)**
- **Fixed three ways items could vanish into the void**: the periodic stock heal could roll back items a guest had *just* placed; a shelf your game couldn't render correctly (content mismatch, capacity differences) echoed its shortfall back and wiped the HOST's shelf too; and an item type from a content pack you don't have was zeroed instead of skipped. All three closed — worst case now is "he sees items I don't" instead of items being destroyed for both of you.

**Box duplication & warping (Skibbles)**
- **Fixed: items pulled out of a box teleporting back in** (duplication) and **boxes the host places or throws warping away**. Root cause was one mechanism: the guest's mirror could get stuck endlessly re-reporting stale box contents and positions it never touched. Guests now only affect a box's contents while they're actually holding it (or just set it down), and the stale-echo latch is gone — which also fixes the reverse case where items a guest unloaded stayed in the box.

## 1.0.33
**Hotfix: the endless "custom-card database differed - RESTART" loop (thanks joshepi89).** The database check compared the registry file byte-for-byte - but the prefab loader rewrites that file on every game boot, so two players whose card databases genuinely MATCHED could mismatch forever (sync, restart, rejoin, rejected again - no number of restarts escaped it). The check now compares what the registry actually MEANS (every card/item name and its ID), so identical databases match on the first rejoin, and the "already synced" message can no longer appear in a loop. If the databases genuinely differ, sync + one restart still fixes it as designed. Both players must update - the launcher does it automatically.

## 1.0.32
**Hotfix: the guest's serve key going permanently dead ("guest can't interact with npc" - thanks Coke).** A safety guard added in 1.0.29 (don't fire the serve key while typing in a text box) checked whether a game text field was *selected* rather than *actively being edited* - and Unity keeps the last-clicked UI element selected forever, so touching any price box / phone app / website input silently killed the serve key for the rest of the session. It now only suppresses while you are genuinely typing, the co-op window can no longer leave a stale focus behind when closed, and a suppressed press is logged instead of silent - so if a key ever dies again, the log says exactly why. Both players must update - the launcher does it automatically.

## 1.0.31
**New in-game look.** The co-op window (F2) got a full visual overhaul - a proper themed panel matching the game's cozy style (rounded corners, title bar, HOST/JOIN section cards, colored status chips, styled buttons and fields), and the on-screen prompts (serve hints, host clock, errors) are now readable dark pills instead of floating text. No gameplay or sync changes. Both players must update - the launcher does it automatically.

## 1.0.30
**Fixes for the full 1.0.29 field-report batch — thank you Bytelocker, SerDakota, dereck, Kamun, Elvundil, Latch, and everyone in the Discord.** Wire format changed again, so 1.0.30 only connects to 1.0.30. Both players must update — the launcher does it automatically.

**Your solo saves (the big one)**
- **Fixed: solo modded saves showing "data lost" after playing co-op.** Joining a host with a different custom-card database syncs the host's registry over yours (with a backup) — and there was no way back, so your own modded saves couldn't load anymore. The co-op window now shows a clear warning whenever your registry is the host's copy, with a one-click **"Restore MY card database"** button (restart afterwards). Nothing is ever lost — every sync keeps backups.
- The host's real save slot is no longer overwritten by the join-time snapshot (it saves to a scratch slot now), so a mid-session problem can never be baked into your only copy.

**Joining (fixed a serious wipe)**
- **Fixed: a guest joining mid-day could WIPE everyone's shelf stock** — the fresh guest "reported" every shelf as changed-from-empty and the host applied it. This was also behind "our third player joined and we lost all our stock" and "the shelves were erased after we restarted." Joining is now safe at any time of day, and item stock heals itself every 12 seconds like card displays already did.
- The "mod set differs" rejection now tells you **exactly which mods differ** — missing, extra, and version mismatches by name (same for custom-card differences).

**1.0.29 regressions (Latch was right)**
- **Fixed: guest purchases were delayed ~1.5 seconds** and a purchase the shared wallet couldn't afford still went through. The purchase/charge pairing was rebuilt properly.
- **Fixed: boxes freezing solid in the warehouse** ("clogging up our shelving") — a guest-side fix was wrongly running on the host and pinning real boxes in place.

**Boxes & items**
- **Fixed: opened boxes refilling endlessly** ("unlimited items") — the guest was echoing stale box contents back at the host while it dispensed.
- **Fixed: boxes a guest trashed silently surviving on the host** and "respawning the next day" — an over-eager anti-grief cap was eating legitimate cleanup sprees; it now allows them and logs when it ever declines one.
- **Fixed: boxes the host moves snapping back** to the delivery area or the guest's old placement.
- Boxes can no longer be placed (or reappear) **under the floor**; anything released below the map is lifted back up.
- Staff can no longer take or drain a box out of a guest's hands (carried over from 1.0.29, now with the disconnect cleanup verified).

**Guests**
- **Fixed: clicking near the register soft-locking a guest** into cash-counter mode with no way out (the cause of "they can't interact with anything until they leave"). Guests serve with the serve key (V by default) — the game now says so instead of trapping you.
- **Fixed: the shop name not updating the first time a friend joins.**
- 3-player: furniture can no longer be teleported/vanished by a stale index from another player (moves now verify the object's identity), and fresh joiners no longer re-report every object's position.

**New option**
- `HostServeKey` (off by default): lets the HOST use the quick-serve key at the register too, same as joiners.

## 1.0.29
**Preemptive fixes from a full audit of the game against the mod.** Instead of waiting for field reports, every game system was swept for 2-player coverage gaps (the full audit lives in `docs/audit-2026-07-07.md`: 134 systems confirmed covered, 10 gaps found — all fixed or safely blocked below). Adds one new network message, so 1.0.29 only connects to 1.0.29.

**Duplication & money exploits (never reported — found by the audit)**
- **Fixed: packs sitting in an auto pack opener duplicated on the host every time a guest joined.** The guest's world-load re-sent every stored pack as if a player had just inserted it. Same bug fixed for deodorant cans stored in the auto cleanser.
- **Fixed: a guest could sell a boxed-up shelf the host placed and credit the shared wallet while the furniture survived** — an infinite money printer. Guest furniture *selling* is blocked (with a message) until a fully-validated version ships; host selling is untouched.
- **Fixed: a purchase whose coin charge the host declined (shared wallet short) still delivered the product free** — restock orders, furniture, and licenses now cancel cleanly when the charge is declined.

**Guest actions that silently did nothing (or worse)**
- **Fixed: the guest's handheld deodorant spray never actually cleaned a smelly customer** (it only hit inert local copies). Sprays now forward to the host and land on the real customer.
- **Fixed: a guest placing a decoration lost it** — the host's world wipes the guest's local copy and the inventory count was gone for good; placing one could even teleport a *different* decoration. Deco placement is blocked with a message for now.
- **Fixed: pulling a shelf item into an empty box destroyed the item** (the host rejected the box's new contents while the shelf loss went through).

**Robustness**
- **Fixed: a host worker could take or drain the box a guest was carrying** out of their hands.
- **Fixed: a guest loading their own save from the pause menu mid-session left a half-connected session** — the session now ends cleanly the moment either side loads a different world.
- **Fixed: re-submitting an already-graded card for re-grading could delete a duplicate graded card** on every player who owned one (a stale assumption from 1.0.22 — the same wrong comment caused two other bugs this week; all three paths corrected).

Both players must update — the launcher does it automatically.

## 1.0.28
**Fixes for all 9 bugs from the latest two-player field test.** This build changes the network format (box sync), so 1.0.28 only connects to 1.0.28 — the join screen says so if versions differ.

**Storage & deliveries (the big one)**
- **Fixed: guest-ordered deliveries only showing on the host.** Root cause: the box sync only carried the first **250** boxes, and a well-stocked warehouse blows past that — new delivery boxes were exactly the ones past the cap. Worse, the guest *deleted* its local copies of boxes past the cap, permanently. The cap is now **1000** (the wire count grew from a byte to a ushort), and a capped snapshot can never delete boxes.
- **Fixed: storage boxes floating in the air.** A box the guest's rack refused to store was left loose at rack-slot height with physics on. It now pins in place on the rack, matching the host's view.
- **Fixed: items in storage boxes differing between players.** Stored-box contents could clamp to zero on the guest (adopted boxes had no item-position list); plus the rack rejections themselves now self-heal — a stale "ghost" occupant blocking the slot gets evicted — and every rejection logs full diagnostics (rack indices, occupants, where the host says they live).

**Graded cards & grading (Grading Overhaul)**
- **Fixed: a graded card's price change by the guest not reaching the host.** A patch-ordering race with Grading Overhaul's own price patch made the forward carry a decoded 1–10 grade, so the host filed the price under the wrong key. The forward now always carries the encoded grade.
- **Fixed: the grading bill charging a different amount than the screen showed** (the $200,000 2-day bill that only took $12k). The host was recomputing the fee with the vanilla flat formula; it now charges the guest's actual on-screen total (sanity-checked).
- Grading Overhaul's extra service tiers (beyond the vanilla 4) no longer get truncated to tier 3 on the wire — the enrolled tier, duration, and fee now match what the guest picked.
- A rejected submission (slots full / wallet race) now returns a re-graded card to the **graded album** it came from instead of converting it into an ungraded copy.
- Graded card names in trade offers show the real grade (e.g. "[grade 9]") instead of a ten-digit encoded number.

**Trading counter**
- **Fixed: the trading NPC walking away while the guest's trade screen was still up.** Two halves: the host now pauses that customer's patience timer while a guest has the screen open (per counter — two guests at two counters are both covered), and a trade screen that lost its binding to the counter (the source of silently-eaten Accept clicks and the "wrong card name" popup) now re-binds to the customer you're standing at — at their real asking price — instead of doing nothing.
- Typing a haggle price no longer triggers the serve key (register serve while typing).

**Binder & collection**
- **Fixed: "total value of cards in binder differs between host and guest."** The binder's total-value text only updated when you opened it; it now refreshes live as cards arrive and leave while the binder is open. (Cards bought from NPCs by the guest were arriving all along — this stale total hid them.)
- The guest no longer generates its own card market prices — it always uses the host's, so values can't drift.
- 3+ player sessions: a card gained/lost by one guest now reaches the *other* guests too, and a delta the host refuses (registry mismatch) is never propagated.
- Card removals that would drive a count negative are skipped loudly instead of silently corrupting the collection — and modded-expansion cards (EPL / CardForge packs) are handled safely there.

Both players must update — the launcher does it automatically.

## 1.0.27
**Graded cards now sync (Grading Overhaul).** ⚠️ Please test this one with a real graded card.

The mod was throwing away every graded card. With **Grading Overhaul** installed, a graded card's grade is an *encoded* value (grading company + grade + certificate number), not a plain 1–10 — and an old safety guard treated everything above 10 as "corrupt" and dropped it. That's why graded cards never appeared in the other player's binder, showed only the red "!", or turned "fake" (the game's grading anti-cheat was re-stamping them because they arrived un-registered).

- The guard is gone; graded cards now register properly with Grading Overhaul on arrival (binding the host's certificate), so they appear in the other player's binder with the correct grade and stop churning.
- **Graded-card prices** now sync too (routed through Grading Overhaul's own price store instead of the vanilla one that couldn't hold them).
- Both players still need the **same grading mods** (the join handshake enforces matching mods). Without a grading mod, nothing changes.

This is the first build that actually attempts modded-grade sync, so if a graded card looks wrong, grab a `BepInEx\CardShopCoop_<number>.log` from both PCs. Everything from 1.0.26 is included.

## 1.0.26
**Big field-report batch from live co-op testing** — thanks to everyone reporting in the Discord.

**Placement, boxes & machines**
- **Fixed: placed objects/machines snapping back** to their old spot when the host moved them, and **objects landing a few degrees off the rotation grid** (couldn't align, showed red). Both were the same bug — an object-move tug-of-war where the guest kept re-asserting the old pose. The host is now authoritative over an object it's actively moving, and the guest no longer fights it.
- **Fixed the auto card opener** in co-op: the guest couldn't add packs after collecting once ("no empty slot" — the machine stayed stuck "processing"), and the machine's UI panel floated at the old spot after the host moved it. Both fixed; you no longer have to leave it in one place.
- **Fixed a guest soft-lock:** if a box you were holding got consumed on the host's side, you'd be stuck in "carry" mode with an invisible box — unable to interact with anything, not even the trash. Now it releases carry mode first, plus a safety net that auto-frees anyone already stuck.
- **Fixed furniture placing diagonally** (host and guest): unpacked furniture now keeps its true placed pose instead of the delivery box's random angle.
- **Fixed boxes floating frozen in mid-air** on storage shelves (the mod was reading the wrong rigidbody) and **a box taken off a storage rack** staying stuck on the shelf for the other player.

**Customers, prices & world**
- **Fixed: the host pausing froze everything for the guest.** Pause stops game time, which stopped the co-op tick; the world now keeps running under the pause menu during a session.
- **Fixed: the guest couldn't answer trade / sell-in customers** (the red "!" ones). It worked, but was undiscoverable — clicking the customer does nothing on the guest, and the prompt only showed right at the till. Now a **walk-up hint** tells the guest to go to the counter and press the serve key (V) whenever a customer wants to trade.
- **Fixed: card prices set on a display not showing for the other player** — card prices now re-send periodically so a dropped update self-corrects. (Note: *play tables* can't hold priced cards — use the glass display counter to sell cards.)
- **Fixed grading status showing "-1 day"** remaining for the joiner (clamped to the shared host progress).

**Grading (partial):** guarded a possible crash from modded (un-clamped) grade values. Full graded-card sync with grading mods is still WIP — both players must run the **identical** grading mods, and some graded cards may not yet appear in the other's binder.

Both players must update — the launcher does it automatically.

## 1.0.24
**Fixed: the guest was autosaving the host's world into its own save slot.**
- We claimed a joiner never saves, but a hole let it through: our save guard only blocked saves while you were actively connected (`Role == Client`). After the host left — or the day rolled over, or you quit — you were still *standing in the host's shop* but no longer "a client," so the game's autosave fired and wrote the **host's** world over **your** save slot (the "saves get bundled together" reports).
- The guard now tracks a *borrowed-world* state that's set the moment you join and stays set through a disconnect until you actually return to the **title screen** — so no autosave, day-end save, or quit-save can touch your own saves while you're in someone else's shop. Your solo saves are now genuinely never touched.

Both players must update — the launcher does it automatically.

## 1.0.23
**Tutorial/task progression now syncs to the guest.**
- Previously every tutorial task was host-only: the guest's task panel stayed stuck on "Set the shop sign to OPEN" (and never advanced past any later task) because the actions that credit tasks were forwarded to the host and only advanced the *host's* tutorial. The host now ships its authoritative task progress with the rest of the shop state, and the guest replays it — so both players' task lists advance together.
- Please test this one specifically (it's the first release to sync tutorial state). If a task looks wrong on the guest, a `BepInEx\CardShopCoop_<number>.log` from both PCs helps.
- Also fixed: on a shop with multiple checkout counters, the guest's scanned-item bar could carry over to the next customer (each counter's checkout screen is now cleared on a completed sale, not just one).

Both players must update — the launcher does it automatically.

## 1.0.22
**Big bug-fix pass — a verified sweep of the whole mod. 24 fixes across trades, money, packs, boxes, furniture, customers, world sync, and networking.**

Trades & haggling
- **Fixed the haggle price reading as $0.** When you typed a counter-offer on a sell-in, the game only committed the number when the field lost focus — pressing Accept could beat that, so it sent $0 (or your *last* amount) while the field showed what you typed. The price is now force-committed on Accept and the field is seeded with the asking price, so what you see is what you send.
- **Fixed a traded-in graded card staying in your binder as a "fake."** Graded cards leave the album through a path the shared-binder mirror didn't cover, so a graded card traded/donated/re-graded away on one side ghosted on the other. That removal is now mirrored.
- A one-off pre-roll hiccup no longer marks a customer's offer "host-only" for the rest of their visit (it retries), and the offer you're mid-trade with can no longer time out and close the screen under you.

Money
- **A guest purchase can no longer be silently eaten.** Ordering furniture that isn't in the host's catalog charged the shared wallet and delivered nothing, with no refund — now it refunds (from the host's real price) and says why, exactly like restock orders already did.
- The shared wallet can no longer be overspent negative by a guest buying against a slightly-stale balance — the host now rejects an unaffordable spend and corrects the guest.
- The wallet and shop XP/level/fame now re-send every 15s, so a single dropped economy packet no longer strands the guest's money display.

Packs & binder
- **Fixed the guest freezing on the first card when opening a pack.** A card-mirror hiccup could throw *inside* the game's pack-open loop and wedge the reveal forever; the mirror is now crash-isolated so the reveal always completes.
- A traded/pulled card now appears in an **already-open** binder immediately, instead of only after you flip a page or reopen it.

Customers & workers
- **Customers no longer blink out for the guest** on a brief loading flicker.
- The red **"!" trade prompt** above a customer is now visible on the guest.
- **Female workers** now show as female (they were spawning from the male model).

Boxes, storage & furniture
- **Empty boxes can be taken from the guest again** — the dispenser count no longer strands too-high for ~15s (which made every further click do nothing), and a freshly taken box now appears within a tick instead of up to 1.5s later.
- Loose boxes no longer get randomly scattered to the wrong spot on the guest (the guest's own out-of-bounds sweep is suppressed; the host owns placement).
- Furniture delivered from the generic catalog no longer **duplicates** on every re-sync, and an unresolved generic box no longer **blocks all furniture from being sold/unpacked** on the guest.

World & performance
- **The shop light switch now syncs** — flipping it toggles the light for both players (host-authoritative) instead of only locally, and a light that got out of step self-heals.
- **Decorations now appear for the guest** (they were never syncing).
- The full card-wall repaint now only re-sends when it actually changed (was an unconditional reliable spike every 12s), and a single oversized reliable packet can no longer wedge the whole reliable lane and freeze all state for the guest.
- Population sync no longer silently stops past 250 objects of one kind.

Both players must update — the launcher does it automatically.

## 1.0.21
**Custom cards are now checked at join — no more silent desync from mismatched custom cards.**
- The join handshake already checked mod versions and the modded-item database, but **custom cards added by CreateCards/CardForge weren't covered** (they live in a different ID space that the old checks couldn't see). Two players with different custom cards could connect and then silently see the wrong card. The handshake now also compares the custom-card ID mapping and stops the join with a clear message if they differ, telling you to install the same custom cards (identical files) and restart.
- Harmless if neither player uses custom cards.

Both players must update — the launcher does it automatically.

## 1.0.20
**Fixed: a guest buying furniture (e.g. the play table) charged them but nothing arrived.**
- When a guest ordered furniture, the host spawned it through the game's placement code, which runs an interactive-move cleanup step meant for when *you* drag-and-drop an object. With no drag in progress that step hit a null reference and aborted the delivery halfway — so the furniture never finished spawning even though the money was already taken. The cleanup is now skipped when there's nothing to clean up, and the delivery completes normally.

Both players must update — the launcher does it automatically.

## 1.0.19
**Fixes fake graded cards, wrong card names, and a stuck-box loop.**
- **Fixed: traded graded cards turning "fake," and cards showing the wrong name/art** (e.g. "Uncommon Golem" on the wrong picture). One root cause: the customer's trade offer was stored by *reference* to a card object the game reuses for the next customer, so a later trade would silently overwrite it — scrambling the grade and the card identity. Offers are now frozen as copies. A safety net also refuses any card with an impossible grade instead of creating a broken one.
- **Fixed: a box that couldn't fit on a storage rack retrying forever** (log spam every 30s, and boxes left frozen/unpickable). After a few tries it now leaves the box loose and grabbable, and retries only if the rack situation changes. This should also clear the "empty boxes can't be picked up" case where a stuck box was left frozen.

Both players must update — the launcher does it automatically.

## 1.0.18
**Busy shops now mirror every customer + trade fixes.**
- Fixed: in a busy shop, the guest only saw ~20 customers no matter how many the host had (trade/sell customers at counters often among the invisible ones). The fast network lane was collapsing the host's multi-packet customer batches down to one packet per tick, so only a fraction of the crowd ever arrived. All customer packets now go through. (Only showed up with large crowds — smaller shops fit in one packet.)
- Fixed: accepting a customer's sell-in at the asking price forwarded $0.00 and got refused as a lowball. The asking price is now pre-filled, so accepting takes the offer as shown.
- Fixed: cards from a trade sometimes not appearing in an already-open binder — the binder now refreshes after a card change arrives. Card changes are also logged for easier bug reports.

## 1.0.17
**Modded item prices now sync — set prices, market prices, costs, and averages.**
- Continuation of the 1.0.15 discovery: the content-mod framework (EnhancedPrefabLoader) also invisibly reroutes the game's *price* storage for modded items, so the mod had been reading and writing a shadow copy the game never uses.
- Fixed: price tags stuck at "–" on the other player's screen for modded products (set a price, partner never saw it) — both directions, tags update live.
- Fixed: modded items showing $0.00 market price, cost, and **average cost** for the joiner.
- Harmless without content mods; vanilla behavior unchanged.

## 1.0.16
**Restocker workers + trade-screen turn-taking.**
- Fixed: restock workers unable to carry boxes inside — the other player's box sync was knocking boxes out of workers' hands, breaking their carry loop. Worker-held boxes are now fully protected and hidden on the other screen while carried, same as player-held.
- Fixed: the same trade customer being served on both screens at once. Now proper turn-taking — whoever opens the trade first gets it; the other player sees a polite message. Crash-safe: a lost connection releases the hold within seconds.

## 1.0.15
**The big modded-products fix — guests can order any modded product the host has.**
- Discovered by decompiling EnhancedPrefabLoader: it doesn't add modded products to the game's catalog list, it invisibly intercepts the game's *reads* of it. The mod had been reading the raw list and was blind to every modded product on the host side.
- Fixed: guest orders of modded products refunding as "isn't in the host's catalog" even when the host had the product on their shelf with the license unlocked.
- Fixed: modded licenses (Pokémon/Hololive packs, etc.) not unlocking for the other player.
- Fixed: the catalog comparison always reporting "identical (135 products)" regardless of actual content.
- Refund messages now explain the real cause instead of misleadingly blaming the host's save/tutorial.

## 1.0.14
**Stability hardening — a whole class of rare, session-breaking bugs.**
- The game engine creates a permanently-broken "fake" manager if one is looked up during a loading screen. An audit found 27 places this could strike; all fixed.
- Prevents (all rare, all previously permanent until restart): a partner's avatar never reappearing after reconnect; trades/tournaments/register silently going dead mid-session; license/settings/report sync quietly stopping; the wall-repaint and expansion healing being disabled.

## 1.0.13
**Price sync self-healing.**
- Fixed: prices set by one player sometimes never appearing for the other (tag stuck at "–"). Price updates were change-gated only, so one lost transmission stranded them. Prices now also re-send every 30 seconds; a missed update fixes itself within half a minute.
- Price applies are now logged on the receiving side for easier bug reports.

## 1.0.12
**Storage sync verified + hardened; long-standing rack corruption fixed.**
- 1.0.11's storage fix had a one-line bug that silently disabled it (a fake-manager landmine); this release fixes it after an adversarial re-review, and boxes stored on racks now truly appear stored for both players.
- Fixed a long-standing bug where the mod corrupted storage racks — rack slot counts were treated as loose shelf items, spawning phantom items and breaking "put box on rack" until restart (also the source of the `WorldSync apply: index out of range` log spam).
- Fixed unclickable "ghost" boxes: a box taken off a rack by the other player could become permanently un-grabbable.
- Box contents now stay correct through store/carry/unstore — no more item loss or duplication at the rack.
- Wrong-order refund messages corrected (no longer told past-tutorial hosts to "play past the tutorial").

## 1.0.11
**Warehouse-rack storage sync.**
- Fixed: boxes stored on storage/warehouse racks were invisible to the other player's rack — they appeared as loose boxes clipping the shelf, fell off, and dragged the properly-stored originals off too ("boxes all over the place"). Storing a box now registers it in the other player's rack exactly as the game does locally, and **Take Box** works for both players.
- Boxes on racks are owned by their slot — no position fighting or phantom drift.

## 1.0.10
**Day-rollover lighting freeze + missing-wall repaint.**
- Fixed: daylight sky at night for guests. A host day rollover was mis-read as 13 hours of "drift" and collided with the day-change reset, freezing the sky in daylight. Rollovers are now handled only by the day mirror; a frozen sky self-repairs at the next in-game morning.
- Fixed: missing wall/window sections for guests. Shop expansions were only ever applied as one-way animations, so a wall piece lost to an interrupted animation stayed missing. Guests now periodically re-run the game's own wall repaint.

## 1.0.9
**Stable per-box IDs — removals can't destroy the wrong box.**
- Fixed: a box could vanish from a player's hands while restocking. Boxes were matched between players by list position, so any removal shifted every later index and the reconcile rebuilt the wrong boxes — including a carried one. Every box now has a permanent ID; removals are surgical.
- Hardened the 1.0.8 rejoin protection: the anti-wipe guard now also covers card boxes and furniture boxes, is per-player, and closes a timing race that could disarm it during a quick reconnect.

## 1.0.8
**Guest rejoin no longer wipes the host's boxes.**
- Fixed the first major field bug: when a guest re-joined, their world-reload destroyed their local boxes, and the mod forwarded all ~250 as "player trashed a box," deleting the host's entire box population ("boxes disappeared from shelves and appeared outside the shop"). Fixed with a reload suppression window plus a host-side flood guard that also protects against guests still on 1.0.7.
- Fixed: customers blinking in and out on rough connections (despawn timeout raised from 1.5s to 6s).
- Fixed: false "product catalogs differ" warning at join from blank placeholder entries and mod-load timing; both players now get an explicit all-clear.
- Guests can now serve the register even when a worker is manning it.

## 1.0.7
**Public launch + auto-updating launcher.**
- First public release on GitHub and Nexus, with a Discord community and a polished WPF launcher that auto-updates the DLL and launches the game.
- Folded together the pre-launch work: identity-keyed orders and shared licenses, native trade-screen serving, enum/card-database auto-sync, furniture-box sync, settled-physics boxes, and 3-player echo fan-out.

## 1.0.6
**3-player fixes.**
- Echo fan-out so a host applying one client's action reaches the other clients; relayed player names; instant box pickup/set-down visibility; register-reach distance corrected.

## 1.0.5
**Carried-box echo fix.**
- The joiner no longer echoes stale state for boxes the host is currently carrying.

## 1.0.4
**Sky drift heal.**
- Joiner sky/time drift corrected with a periodic heal heartbeat on the lighting sync.

## 1.0.3
**Carried-box pose.**
- A carried box now sits in the avatar's arms instead of down at the knees.

## 1.0.2
**Catalog-check timing.**
- The join-time catalog check now waits for late-registering content mods before warning.

## 1.0.1
**Toast wrapping.**
- On-screen notifications wrap instead of clipping off the edge.

## 1.0.0
**Initial public release.**
- One shared shop over Steam lobbies or LAN: money, XP, collection (graded cards included), item and card prices, shelf stock, card walls, loose boxes, placed furniture, licenses, bills, room expansions, tournaments, grading, and the daily market. Both players work the register, run trades/sell-ins, restock, price, order, hire, and pay bills. Mod-stack aware (PTCGO / Enhanced Prefab Loader), host-authoritative, joiners never write their own saves.
