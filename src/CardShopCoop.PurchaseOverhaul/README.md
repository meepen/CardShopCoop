# CardShopCoop.PurchaseOverhaul

Optional companion plugin that adds CardShopCoop co-op support to Purchase Overhaul without
changing CardShopCoop core.

## What it does

- Guest order: `PurchaseOverhaul.Checkout.Place` is intercepted before it charges the guest or
  queues local deliveries. The cart's stable product ids and box counts are sent to the host as
  one `ActionMessage`.
- Host order: the host resolves those ids through Purchase Overhaul's own catalogue and calls
  `Checkout.PlaceRemote`. Purchase Overhaul owns pricing, volume/recycle discounts, validation,
  the wallet charge, deliveries and XP.
- Guest result: the host returns `PurchaseOrderResult`. An accepted result runs
  `OnMerge` and removes exactly the ordered lines from the guest's cart; a rejected result runs
  `OnUndo` and leaves the cart intact with the host's message.
- Product licences use the same flow through `Checkout.BuyLicense`; the host's
  `CPlayerData.SetUnlockItemLicense` is observed by CardShopCoop's Catalog module, so the
  entitlement reaches the guest normally.
- Purchase Overhaul's own QOLCoop bridge is left alone. If QOLCoop is active, this plugin lets
  Purchase Overhaul handle the order.

No wire message carries a live `Product` or other third-party object. The DTOs carry product id
strings and box counts; the client resolves them through Purchase Overhaul's own lookup when it
needs to touch the cart.

## Install

Copy `CardShopCoop.PurchaseOverhaul.dll` into `BepInEx/plugins` on both PCs. Both players must
install it; the new request/response DTOs are part of the join message catalog.

Dependencies (soft):

- `dev.meepen.cardshopcoop`
- `com.qolmods.purchaseoverhaul`

## Limitations

- Purchase Overhaul's shared cart is a QOLCoop feature and is not enabled here. Each guest orders
  its own cart on the host.
- Client auto-restock also goes through `Checkout.Place` and is routed to the host. If both host
  and guest enable auto-restock, both orders will be placed; this plugin does not coordinate the
  auto-restock setting.
