using System.Runtime.CompilerServices;

// The runtime (CardShopCoop.dll) is the only writer of the binding seam. External mods read the
// public surface only; they can never install, replace, or bypass the binding. The Custom TV add-on
// is a first-party sibling and uses the internal action surface.
[assembly: InternalsVisibleTo("CardShopCoop")]
[assembly: InternalsVisibleTo("CardShopCoop.CustomTv")]
