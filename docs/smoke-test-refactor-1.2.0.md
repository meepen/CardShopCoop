# In-game smoke test — module-system refactor (1.2.0 wire)

Two players (host + guest), same deployed build. The refactor changed **how** messages are
routed and how modules tick/reset, not the wire format. So the test is: exercise every message
family once and confirm the two shops stay in lockstep, then exercise join/rejoin/disconnect.

Enable before testing: `Diagnostics → BoxSyncDebug = true` and `Diagnostics → PerfDebug = true`
in `BepInEx/config/com.zwhit.cardshopcoop.cfg`, then restart. Logs:
`BepInEx\CardShopCoop_<pid>.log` on each PC.

Verdict is **fail** on any desync, stuck state, exception line, or one-sided change.

## 1. Pre-flight (2 min)
- [ ] Both logs show `CardShopCoop 1.2.0 loaded.` and no `CoopCore FAILED TO LOAD`.
- [ ] Guest joins; both shops load to the same world; avatars appear.
- [ ] Host log has no `main-thread action ... failed` / `Dispatch conn=... failed`.

## 2. Message-family pass (the Phase 2 risk)
Do each on the guest, watch the host, then reverse the roles. Expected: the host applies it and
the guest does not drift.

| # | Family | Action | Expected both sides |
|---|--------|--------|---------------------|
| 1 | Register | Host mans counter; guest scans item+card, takes payment, change, Space to finish | Same cart/phase/total; no ghost cart |
| 2 | Trades | Serve a counter trade/sell-in customer on each side | Offer, price, accept/decline mirror; customer leaves once |
| 3 | Containers | Restock from storage, donation box, auto-opener claim, box-bank take | Counts match; opener claimed once |
| 4 | Staff | Hire a worker, change its settings, fire it | Roster + settings match; wages once |
| 5 | Market | Buy stock (market/base prices), card % change, item price edit | Prices converge; binder not $0 |
| 6 | Boxes | Pickup/throw item box, Q-place, open/close card box | Hidden while held; throw impulse; lid state |
| 7 | Grading | Submit cards, advance a day, collect | Pending + collected mirror; no double certs |
| 8 | TV | Alt+Y open, play a shared stream, pause/next | Both play same stream/position |
| 9 | Settings | Buy/equip deco, toggle a game event / counter option | Applied on both; no repeat charge |
| 10 | Tournament | Schedule + start a tournament; watch the pairing board | Schedule/rounds/board match |
| 11 | Light/time | Let time pass or force a day change | Sky/day match; no frozen guest |
| 12 | Cards/prices | Add/lose a card; set a card's marked price | Collection + price match; no drift/flood |
| 13 | Movement | Walk, look around, place a furniture preview | Avatar smooth; preview follows |

## 3. Module lifecycle pass (the Phase 0/1 risk)
- [ ] **Rejoin:** guest leaves to title, rejoins same host. Market/boxes/prices/roster resync; no stale or duplicated furniture.
- [ ] **Scene change:** host loads a save mid-session; guest reloads and sync resumes. No `co-op module reset failed` / `resend failed`.
- [ ] **Guest disconnect while holding a box:** host's box unhides, becomes pickable, contents intact.
- [ ] **Guest disconnect while pushing a box:** host's box settles back to physics.
- [ ] **Host stops hosting / disconnects:** guest shuts down cleanly, no exception spam. (Known edge: a box hidden on the non-holding side may stay invisible until reload — note if seen.)

## 4. 1.2.0 regression spot-checks
- [ ] Guest joins and market/price values are correct **after** the world finishes loading.
- [ ] Guest standing still swaps a held item → host sees it update within ~0.1 s.
- [ ] Guest sells boxed furniture / boxes up placed furniture → host removes/boxes the same object, no guest-only copy.
- [ ] New character with "Allow NSFW" off: random clothed preset, Nude hidden; remote nude shown clothed.

## 5. Log review (both PCs)
- [ ] No `[<Module>:host]` or `[<Module>:apply]` errors (new `ModuleGuard` boundary; full exception + stack).
- [ ] No `main-thread action '...' abandoned`.
- [ ] No `Dispatch conn=... failed`.
- [ ] No flood of `[caught]` lines; occasional is fine.
- [ ] TV/container/box modules each show their normal snapshot activity, not silence.

## 6. If it fails (triage)
1. Capture both `CardShopCoop_<pid>.log` files from the failing session.
2. Note which family (table #), which role, and whether one side or both desynced.
3. To confirm it is the refactor and not the game: `git switch 1.2.0` and
   `dotnet build src\CardShopCoop\CardShopCoop.csproj -c Release -p:Deploy=true`, rerun that
   one step. Passes on 1.2.0 but fails on the refactor = a refactor regression.
