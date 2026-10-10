# CardShopCoopCommunity ExternalModInterop

Optional bundle of external-mod integrations for
[CardShopCoopCommunity](https://thunderstore.io/c/tcg-card-shop-simulator/p/CardShopCoop/CardShopCoopCommunity/).
It currently ships **RTCGO Custom TV** playback sync.

- Requires **CardShopCoopCommunity** — declared as a dependency, so the mod manager installs it for you.
- **Both players must install it** for the shared feature (for example TV state) to sync. Without it,
  co-op works exactly as normal and that feature simply stays local.
- Built against the public CardShopCoop API. Each integration is inert unless its target mod is installed.

Source and issues: https://github.com/meepen/CardShopCoop
