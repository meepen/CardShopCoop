# Manual in-game test scripts — 1.2.0 review fixes

Run two instances with `CoopPlugin.ArtificialLagMs` / `ArtificialJitterMs` (SETTINGS tab,
inbound) as noted. The game cannot be exercised in CI, so these are the acceptance scripts.

## 1. Take-boundary reservation (D1 / P1a)
Goal: a take cannot be placed/opened before the host confirms, even from a shelf the scanner
already passed.
1. Build a shop with more than ~12 item shelves and enough stock to take from a late shelf.
2. Set artificial lag to 3000 ms.
3. Take an item from a shelf near the end of the scan order, then immediately try to place it on a
   shelf / open a pack / trash it.
Expected: the action is blocked until the host result arrives; log shows the taken item noted at
the take and promoted into the transfer token. No duplicate after the result.

## 2. Equal-count / different-type add race (D2 / P1b)
1. Guest adds type B to an empty box while the host simultaneously adds type A to the same box.
2. Force ~2000 ms lag so the guest's in-flight snapshot has count 1 / type A.
Expected: the guest's mirror keeps B until the result; on host reject, B returns to the guest's
hand (not lost); a resync then converges to host truth. Log shows the pending-add protection.

## 3. Loose-box lid-only edit (D3 / P2a)
1. Host places a delivery box on the floor; guest does NOT pick it up.
2. Guest opens and closes the box.
Expected: lid state changes on both screens; item counts never change from the lid-only edit.
Also confirm a non-owner still cannot change counts.

## 4. Delayed result is not an accepted take (D4/D6 / P2b)
1. Set inbound lag to 60000 ms (or jitter to force reorder) on the guest.
2. Guest takes the last item from a box; host immediately removes it / rejects.
3. Keep the connection alive for > 15 s (the old TTL).
Expected: the guest's item stays reserved and cannot be used; the client keeps retrying the same
sequence; when the delayed result arrives it is applied exactly once. No duplicate, no grant on
timeout. Host log shows one apply and stores the ack; duplicate requests log "duplicate -> stored"
and do not re-apply.

## 5. Fail-closed reservation (D5 / V2)
1. Force a take whose returned item cannot be reserved (e.g. take from a shelf while the hand
   list is in a modal state that hides it).
Expected: no tracked transfer is sent; the just-taken hand item is rolled back; one loud warning;
a resync restores host truth. No untracked take that a later rejection cannot roll back, and no
hand+container duplicate.

## 6. Host idempotency under a send fault (D6 / A4)
1. Use a fault-injection build whose `SendTransferResult` / `SendResult` throws after the host
   applies the merge.
2. Perform a take and an add.
Expected: dispatch retries the same message; the host answers the duplicate from its stored ack
without re-applying; the client still receives the correct result.

## 7. Box heal routes (D7 / V4)
1. Force a `BoxSnapshot` / `BoxTransferResult` dispatch exception.
Expected: the client sends a join-resync request and a full authoritative box snapshot follows,
instead of a wallet/progress heal and a stranded box.

## 8. Collect status messages (D8 / A7)
Trigger each cause and check the client line:
- host is carrying the box → host-handling message (not silence),
- guest missing a content pack → content-pack message,
- genuine card drift → the existing cert-drift message,
- unknown/stale box id → stale-box message.

## 9. Storage-destroy suppression bound (D9 / A9)
1. Force `InteractableEmptyBoxStorage.StoreBox` to throw after `OnDestroyed`.
Expected: the suppression flag is not left armed; a later legitimate box destroy is not swallowed;
a frame-mismatch refusal is logged.

## 10. Session-after-session lifecycle (V1)
1. Host a session, stop hosting, then host again (and separately: fail a join, then retry).
2. On the second session: hire a worker, interact with a worker, buy a decoration, change a
   cashier, set a table number, trigger an equip.
Expected: all of them work and sync. (Previously all silently no-op'd.)

## 11. Card placement terminal state (A2)
1. Force repeated host rejection of a guest card placement (e.g. host removes the slot / lags).
2. Let the resend budget exhaust.
Expected: the card is NOT banked and the slot is NOT cleared (banking while the host may hold it
would duplicate the card). The client stops resending, notifies once, keeps the placement tracked,
and a later host echo still resolves it (rejection returns the card to the binder). No silent
divergence and no duplication.

## 12. Log hygiene (A3) and diagnostics
1. Send an unregistered message type repeatedly (dev build).
Expected: not one log line per frame; rate-limited/deduped warnings.
2. Confirm caught exceptions still log with a stack (`Swallow`).

## 13. Latency transport stop (A11)
Start a session with lag enabled, stop hosting, start again.
Expected: no delayed frames from the previous session are replayed.
