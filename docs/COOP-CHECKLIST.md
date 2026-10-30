# Card Shop Co-op — Read This First

Everything you need to get two people into one shop, and the fixes for the
things that go wrong most often.

---

## Install

**What you need (BOTH players):**

- BepInEx pack: https://www.nexusmods.com/tcgcardshopsimulator/mods/8
- CardShopCoop — always the latest release; both players must run the **same version**
- The **exact same mods and versions** on both PCs. This is the #1 cause of
  "it won't connect."

**Where your game folder is:**

- **Steam:** right-click the game → Manage → Browse local files
- **Game Pass:** `C:\XboxGames\TCG Card Shop Simulator\Content`

It's the folder that has **Card Shop Simulator.exe** in it.

**Installing BepInEx (do this first):**

1. Open the BepInEx zip and drag its **contents** into that game folder — NOT
   the zip's own folder, and NOT into `Card Shop Simulator_Data`.
2. When it's right, you'll see **winhttp.dll** and a **BepInEx** folder sitting
   right next to the exe.
3. Launch the game once, then press **F1**. If a settings window appears,
   BepInEx works. If nothing happens, it isn't installed right — redo step 1.

**Installing the co-op mod:**

4. Drop **CardShopCoop.dll** into `BepInEx\plugins`. A subfolder is fine —
   `BepInEx\plugins\CardShopCoop\CardShopCoop.dll` works too.
5. Launch, load into your shop, press **F2**. The co-op window opens.

> Mod managers (Vortex etc.) often put files in the wrong place for this game.
> Dragging the files in by hand is more reliable — it's literally one .dll.

---

## Playing together

**Load into your shop first.** Not the main menu — all the way into the game.
Then press **F2**.

**Steam ↔ Steam (easiest)**

Host clicks **Start hosting on Steam** → **Invite friend**. Friend accepts. Done.

**LAN / same house (IP / LAN)**

Host clicks **Start hosting on IP / LAN**. The friend types the host's address on your
network — like `192.168.1.50`, or `192.168.1.50:27886` if the host changed the port —
into **Host address** and clicks **Join through IP / LAN**. Both players type the same
password.

**Over the internet (no Steam — this is the Game Pass route)**

The Game Pass version of the game has no Steam Networking Sockets support at all, so it's
IP-only.

- Leave **Open my router's port** on (the default) and the mod asks your router to open the
  port for you. If your router refuses, forward UDP **27886** (or whatever port the host set)
  on it yourself. Either way, press **Copy public address** on the host panel and send that to
  the friend, who pastes it into **Host address**.
- **Easier:** use a free virtual-LAN app — **Radmin VPN**
  (https://www.radmin-vpn.com) or Hamachi. Both players join the same virtual
  network and then type each other's virtual address exactly as on a LAN.

**Steam ↔ Game Pass?**

Only when both stores are on the same game version. Right now they aren't
(Game Pass is on 1.00, Steam on 1.02), so the mod refuses the join and says
so. When the stores line up it'll just work — no mod update needed.

---

## Before you ask

**"I press F2 and nothing happens"**
The mod isn't loaded. Press **F1** — if that does nothing either, BepInEx isn't
installed correctly (see Install). If F1 works but F2 doesn't, the .dll isn't in
`BepInEx\plugins`.

**"Your custom-card database conflicts with the host's"**
The mod already sent you the host's database. You must **fully quit the game to
your desktop** — not to the main menu — then start it again and rejoin. Card IDs
are only read while the game is booting, so nothing changes until a real
restart. New saves won't help; this isn't about saves.

**"Your mod set differs from the host's"**
Someone has a mod the other doesn't, or a different version of one. The message
names them. Make both PCs identical.

**"Your GAME build doesn't match the host's"**
You're on different versions of the game itself (usually Steam vs Game Pass).
Nothing to fix on your end — wait for the stores to match.

**"Host snapshot failed"**
Load fully into your shop before hosting, then try again.

**Friends can't join over the internet**
Your router probably refused to open the port (the host panel says so). Forward UDP
**27886** (or whatever port the host set) on the host's router to the host's PC, then have
the host press **Copy public address** and send that to the friend. If your router won't
forward the port, use Radmin VPN / Hamachi instead.

**Cards or items look wrong / missing**
Update both PCs to the latest version first — a lot of these are already fixed.
If it persists, report it.

---

## Reporting a bug

Bugs get fixed fast when the report is good.

**1. Logs from BOTH PCs.** This is the big one — most bugs are only visible by
comparing the two sides. Do this on each PC:

- Open the co-op window's **Advanced Options** tab and press **Copy Diagnostics**.
- Paste once in Discord. That one paste carries the report (`CardShopCoop_Diagnostics_<number>.md`),
  `BepInEx\LogOutput.log`, and Unity's `Player.log` together.

The log files are attached exactly as they are on disk, so they can contain player
names and addresses. Send them where you would send the logs themselves.

If you are picking files by hand instead, `BepInEx\CardShopCoopCommunity_<number>.log`
is the important one (the number is the game's process id), and
`BepInEx\LogOutput.log` is also helpful if the mod isn't loading at all.

Grab the newest files from right after the problem happened, and say which PC is
the host's.

**2. Tell me:**

- What happened, and what you expected instead
- Who was hosting
- Mod version (both players) + your mod list
- Steam or Game Pass

**Attach the log files** — don't paste them as walls of text.

> The logs say a lot on their own now: which save system your game uses, whether
> your card databases match, what got synced, what got refused and why. Half the
> time the answer is in the first few lines.
