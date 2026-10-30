using System.Runtime.CompilerServices;

// The runtime (CardShopCoop.dll) is the only writer of the binding seam. External mods read the
// public surface only; they can never install, replace, or bypass the binding. Custom TV is an
// external interop example and deliberately has no internal access. The Steam assembly is part of
// the runtime (it supplies the Steam datagram bearer) and needs the internal transport peer and
// message contract; it is not a third-party extension.
[assembly: InternalsVisibleTo("CardShopCoop")]
[assembly: InternalsVisibleTo("CardShopCoop.Steam")]
