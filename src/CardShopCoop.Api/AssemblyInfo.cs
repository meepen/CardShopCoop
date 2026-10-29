using System.Runtime.CompilerServices;

// The runtime (CardShopCoop.dll) is the only writer of the binding seam. External mods read the
// public surface only; they can never install, replace, or bypass the binding. Custom TV is an
// external interop example and deliberately has no internal access.
[assembly: InternalsVisibleTo("CardShopCoop")]
