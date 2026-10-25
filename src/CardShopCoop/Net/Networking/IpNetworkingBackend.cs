using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CardShopCoop.Net.Datagrams;
using UnityEngine;
using CardShopCoop.Api;

namespace CardShopCoop.Net.Networking
{
    /// <summary>
    /// The IP backend: the host listens on a UDP port and a friend dials the host's address.
    ///
    /// There is no punching and no invite code any more. The host binds 0.0.0.0 and is reachable
    /// at whatever address a friend can route to - the LAN address, or the public address once the
    /// port is open - and a joiner simply types the host's address into one field. The host also
    /// asks its router to open the port (UPnP/NAT-PMP/PCP, unless the player turned that off) so a
    /// friend outside the house can join without a manual forward.
    ///
    /// One host socket serves every client, so several friends can join the same host. A joiner's
    /// transport is pinned to the single address it dialled, so a client is always exactly one
    /// peer - which is what keeps a client's PeerConnection.Id (always 1) unambiguous.
    ///
    /// STUN is used for exactly one thing: the host's "copy public address" button. It puts the
    /// public IP:port a friend outside the house would dial on the clipboard. That address is a
    /// home connection, so it is NEVER logged and NEVER drawn in the window - it lives only on the
    /// clipboard.
    /// </summary>
    internal sealed class IpNetworkingBackend : INetworkingBackend, INetworkingBackendDiagnostics
    {
        public const string BackendId = "ip";

        private readonly object _gate = new();
        private readonly Dictionary<FieldId, string> _configured = new();
        private string _status = "Ready.";
        private string _warning;
        /// <summary>Public "ip:port" for the clipboard ONLY - never logged, never shown.</summary>
        private volatile string _publicEndpoint;
        /// <summary>True once the host's router/public-address setup has finished, either way.</summary>
        private volatile bool _addressLookupDone;
        private ForwardState _forwardState = ForwardState.None;
        private string _forwardReason;
        /// <summary>Epoch of the router mapping THIS session created, or 0 when none was made.</summary>
        private long _mappingEpoch;
        private int _listenPort;
        private int _generation;
        private long _active;
        private bool _isHost;

        public IpNetworkingBackend()
        {
            _configured[FieldId.Port] = CoopPlugin.Port.Value.ToString(CultureInfo.InvariantCulture);
            _configured[FieldId.IpAddress] = CoopPlugin.LastJoinIP.Value ?? "";
            _configured[FieldId.AutoForward] = CoopPlugin.AutoPortForward.Value ? "1" : "0";
        }

        public string Id => BackendId;

        public string DisplayName => "IP / LAN";

        public ulong LocalUserId => 0UL;

        /// <summary>IP has no platform persona. It must stay empty: the identity resolver treats a
        /// non-empty <see cref="INetworkingBackend.LocalUserName"/> as a real platform name (Steam
        /// persona) and uses it as the default display name, so answering with the machine username
        /// here would mask the Xbox gamertag on Game Pass and the configured name on LAN.</summary>
        public string LocalUserName => "";

#pragma warning disable 0067 // never raised here; a UDP bearer has no out-of-band join request
        public event Action<INetworkingBackend, FieldValues> JoinRequested;
#pragma warning restore 0067

        public Task<BackendAvailability> GetAvailabilityAsync() => Task.FromResult(BackendAvailability.Yes);

        // ------------------------------------------------------------------ configuration

        public void Configure(FieldValues values)
        {
            if (values == null)
            {
                return;
            }

            lock (_gate)
            {
                foreach (var pair in values)
                {
                    if (pair.Value != null)
                    {
                        _configured[pair.Key] = pair.Value;
                    }
                }

                if (int.TryParse(Value(FieldId.Port), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var port) && port > 0 && port <= 65535)
                {
                    CoopPlugin.Port.Value = port;
                }

                var address = Value(FieldId.IpAddress).Trim();
                if (address.Length > 0)
                {
                    CoopPlugin.LastJoinIP.Value = address;
                }

                CoopPlugin.AutoPortForward.Value = Flag(FieldId.AutoForward);
            }
        }

        /// <summary>Thread-safe: also taken (re-entrantly) by <see cref="Configure"/>.</summary>
        private string Value(FieldId id)
        {
            lock (_gate)
            {
                return _configured.TryGetValue(id, out var value) ? (value ?? "") : "";
            }
        }

        private bool Flag(FieldId id) => string.Equals(Value(id), "1", StringComparison.Ordinal);

        private int ResolvePort()
            => int.TryParse(Value(FieldId.Port), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var configured) && configured > 0 && configured <= 65535
                ? configured
                : CoopPlugin.Port.Value;

        /// <summary>Binds the asked-for port, or any port when it is taken: the host still works,
        /// the player is told which port it actually got, and a friend dials that one. A socket that
        /// cannot bind at all is closed before the failure is rethrown.</summary>
        private static Socket BindSocket(int port)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                return socket;
            }
            catch (SocketException)
            {
                try
                {
                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                    return socket;
                }
                catch
                {
                    socket.Close();
                    throw;
                }
            }
        }

        public IReadOnlyList<BackendField> GetFields(ConnectMode mode)
        {
            var fields = new List<BackendField>();
            if (mode == ConnectMode.Host)
            {
                fields.Add(new BackendField(FieldId.Port, "Local port", FieldKind.Port,
                    ResolvePort().ToString(CultureInfo.InvariantCulture), required: true,
                    tooltip: "The UDP port this PC listens on. Forward this port on your router so "
                        + "friends outside your house can reach you."));
            }
            else
            {
                fields.Add(new BackendField(FieldId.IpAddress, "Host address", FieldKind.Text,
                    Value(FieldId.IpAddress), required: true,
                    tooltip: "The host's address: an IP like 192.168.1.50, or IP:port when the host "
                        + "uses a different port. Ask the host to press the copy button.")
                {
                    Sensitive = true,
                });
            }

            fields.Add(new BackendField(FieldId.Password, "Password", FieldKind.Password,
                Value(FieldId.Password),
                tooltip: mode == ConnectMode.Host
                    ? "The password a friend must type to join. Press Copy password to hand it over."
                    : "The password the host set.")
            {
                Sensitive = true,
            });

            if (mode == ConnectMode.Host)
            {
                // Last, because the things you type come first: this is a preference, not an input.
                fields.Add(new BackendField(FieldId.AutoForward, "Open my router's port",
                    FieldKind.Toggle, Flag(FieldId.AutoForward) ? "1" : "0",
                    tooltip: "Ask your router to open the co-op port (UPnP, then NAT-PMP) and close "
                        + "it again when you stop hosting. Turn this off if you forward the port "
                        + "yourself: with it off the mod never touches the router."));
            }

            return fields;
        }

        /// <summary>The support-report view of this backend. Values the window deliberately keeps
        /// off screen - the session password and the public "ip:port" - are still listed, but marked
        /// sensitive so the report censors them completely.</summary>
        public IReadOnlyList<BackendDiagnosticField> GetDiagnosticFields(ConnectMode mode)
        {
            if (mode == ConnectMode.Host)
            {
                var fields = new List<BackendDiagnosticField>
                {
                    new BackendDiagnosticField("Backend", Id),
                    new BackendDiagnosticField("Local port",
                        ResolvePort().ToString(CultureInfo.InvariantCulture)),
                    new BackendDiagnosticField("Password", Value(FieldId.Password), sensitive: true),
                    new BackendDiagnosticField("Open router's port",
                        Flag(FieldId.AutoForward) ? "yes" : "no"),
                };

                var publicEndpoint = _publicEndpoint;
                if (!string.IsNullOrEmpty(publicEndpoint))
                {
                    fields.Add(new BackendDiagnosticField("Public address", publicEndpoint,
                        sensitive: true));
                }

                return fields;
            }

            return new[]
            {
                new BackendDiagnosticField("Backend", Id),
                new BackendDiagnosticField("Host address", Value(FieldId.IpAddress), sensitive: true),
                new BackendDiagnosticField("Password", Value(FieldId.Password), sensitive: true),
            };
        }

        public IReadOnlyList<BackendAction> Actions => BuildActions();

        private IReadOnlyList<BackendAction> BuildActions()
        {
            var hosting = Interlocked.Read(ref _active) != 0;
            var address = _publicEndpoint;
            var publicTip = !hosting
                ? "Start hosting first."
                : string.IsNullOrEmpty(address)
                    ? _addressLookupDone
                        ? "No public address was found. Forward the port on your router and send "
                            + "the friend your IP:port by hand."
                        : "Looking up your public address\u2026"
                    : "Copy your public IP:port to the clipboard, to send to a friend joining from "
                        + "outside your house.";

            return new[]
            {
                // HOSTING ONLY: these copy controls are meaningless until the backend is actually
                // carrying the session - there is no password or public address to hand out before
                // then - so they are offered only in the hosting panel, never as bare glyphs in the
                // "Host your shop" options view.
                new BackendAction("copyPassword", "Copy password",
                    () =>
                    {
                        var password = Value(FieldId.Password);
                        if (!string.IsNullOrEmpty(password))
                        {
                            GUIUtility.systemCopyBuffer = password;
                        }
                    },
                    enabled: !string.IsNullOrEmpty(Value(FieldId.Password)),
                    tooltip: "Copy the session password to the clipboard.",
                    icon: "copy",
                    mode: ConnectMode.Host,
                    phase: ActionPhase.Live),
                new BackendAction("copyAddress", "Copy public address",
                    () =>
                    {
                        var current = _publicEndpoint;
                        if (!string.IsNullOrEmpty(current))
                        {
                            GUIUtility.systemCopyBuffer = current;
                        }
                    },
                    enabled: hosting && !string.IsNullOrEmpty(address),
                    tooltip: publicTip,
                    icon: "copy",
                    mode: ConnectMode.Host,
                    phase: ActionPhase.Live),
            };
        }

        public ConnectionInfo ConnectionInfo => Describe();

        private ConnectionInfo Describe()
        {
            var active = Interlocked.Read(ref _active) != 0;
            string status;
            string warning;
            string forwardReason;
            ForwardState forward;
            int port;
            lock (_gate)
            {
                status = _status;
                port = _listenPort;
                forward = _forwardState;
                forwardReason = _forwardReason;
                warning = _warning;
            }

            if (string.IsNullOrEmpty(warning) && forward == ForwardState.Declined
                && !string.IsNullOrEmpty(forwardReason))
            {
                // Say WHY the router would not open the port, not just that it did not: that is the
                // difference between "forward it yourself" and "try again".
                warning = forwardReason;
            }

            var summary = active
                ? _isHost ? "Hosting on port " + port.ToString(CultureInfo.InvariantCulture) : "Connected"
                : status;
            return new ConnectionInfo(active, _isHost, summary, forward, warning,
                _isHost ? Value(FieldId.Password) : null);
        }

        // ------------------------------------------------------------------ connect / disconnect

        public void Connect(ConnectionId attempt, ConnectMode mode, FieldValues values,
            IBackendConnectCallbacks callbacks)
        {
            if (attempt.Value == 0)
            {
                throw new ArgumentException("A live connection attempt is required.", nameof(attempt));
            }
            if (callbacks == null)
            {
                throw new ArgumentNullException(nameof(callbacks));
            }

            Interlocked.Increment(ref _generation);
            lock (_gate)
            {
                foreach (var pair in values ?? new FieldValues())
                {
                    if (pair.Value != null)
                    {
                        _configured[pair.Key] = pair.Value;
                    }
                }
            }

            try
            {
                _isHost = mode == ConnectMode.Host;
                if (_isHost)
                {
                    ConnectHost(attempt, callbacks);
                }
                else
                {
                    ConnectClient(attempt, callbacks);
                }
            }
            catch (Exception error)
            {
                callbacks.Failed(attempt, error.Message);
            }
        }

        private void ConnectHost(ConnectionId attempt, IBackendConnectCallbacks callbacks)
        {
            var requestedPort = ResolvePort();
            var socket = BindSocket(requestedPort);
            var port = ((IPEndPoint)socket.LocalEndPoint).Port;

            lock (_gate)
            {
                _listenPort = port;
                _publicEndpoint = null;
                _addressLookupDone = false;
                _forwardState = ForwardState.None;
                _forwardReason = null;
                _mappingEpoch = 0;
                _status = "Ready.";
                _warning = port == requestedPort
                    ? null
                    : "Port " + requestedPort.ToString(CultureInfo.InvariantCulture)
                        + " was busy; listening on " + port.ToString(CultureInfo.InvariantCulture) + ".";
            }

            // The runtime owns the bearer from here: it wraps it in the KCP session, assigns the
            // peer-id band, starts it, and disposes it on disconnect. A host socket accepts a peer
            // from any endpoint, so one listening port serves every friend who dials it.
            var datagrams = UdpDatagramTransport.CreateHost(socket);
            Interlocked.Exchange(ref _active, attempt.Value);
            callbacks.BearerReady(attempt, datagrams);
            callbacks.Connected(attempt);

            StartHostAddressSetup(port, Flag(FieldId.AutoForward));
        }

        private void ConnectClient(ConnectionId attempt, IBackendConnectCallbacks callbacks)
        {
            if (!TryParseHostAddress(Value(FieldId.IpAddress), out var remote, out var error))
            {
                callbacks.Failed(attempt, error);
                return;
            }

            lock (_gate)
            {
                _publicEndpoint = null;
                _addressLookupDone = false;
                _forwardState = ForwardState.None;
                _forwardReason = null;
                _mappingEpoch = 0;
                _status = "Ready.";
                _warning = null;
            }

            // A client transport is pinned to the one endpoint it dialled, so the host is always a
            // single peer and the runtime's peer id is always 1.
            var datagrams = new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 0), remote);
            Interlocked.Exchange(ref _active, attempt.Value);
            callbacks.BearerReady(attempt, datagrams);
            callbacks.Connected(attempt);
        }

        /// <summary>A host address is an IPv4 literal, optionally with ":port". A hostname is
        /// refused rather than resolved: resolving on the Unity thread would freeze the game, and
        /// asking a friend for a name is not worth a stalled frame.</summary>
        private static bool TryParseHostAddress(string text, out IPEndPoint endpoint, out string error)
        {
            endpoint = null;
            error = null;

            var trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0)
            {
                error = "Type the host's address first.";
                return false;
            }

            var hostPart = trimmed;
            var port = CoopPlugin.Port.Value;
            var colon = trimmed.IndexOf(':');
            if (colon >= 0)
            {
                if (trimmed.IndexOf(':', colon + 1) >= 0)
                {
                    error = "That does not look like an address. Try 192.168.1.50 or 192.168.1.50:27886.";
                    return false;
                }

                hostPart = trimmed.Substring(0, colon).Trim();
                var portText = trimmed.Substring(colon + 1).Trim();
                if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out port) || port <= 0 || port > 65535)
                {
                    error = "That port doesn't look right.";
                    return false;
                }
            }

            if (!IPAddress.TryParse(hostPart, out var ip)
                || ip.AddressFamily != AddressFamily.InterNetwork)
            {
                error = "Type the host's IP address, like 192.168.1.50 (or 192.168.1.50:27886).";
                return false;
            }

            endpoint = new IPEndPoint(ip, port);
            return true;
        }

        /// <summary>
        /// Host setup, off the main thread and AFTER hosting is already live: ask the router to open
        /// the port (when the player left that on), then work out the public "ip:port" a friend
        /// outside the house would dial, for the copy button.
        ///
        /// The address is stored and never logged or drawn - only the copy action ever sees it. A
        /// stale run from a previous session is dropped by the generation check.
        /// </summary>
        private void StartHostAddressSetup(int port, bool openRouter)
        {
            var generation = Volatile.Read(ref _generation);
            new Thread(() =>
            {
                var forwardState = ForwardState.None;
                var forwardReason = (string)null;
                var externalPort = port;
                long epoch = 0;
                var externalIp = (string)null;
                try
                {
                    if (openRouter)
                    {
                        try
                        {
                            NetHelpers.BeginDiscovery();
                            if (NetHelpers.TryMapPort(port, out var why, out epoch, out externalPort))
                            {
                                forwardState = ForwardState.Mapped;
                                externalIp = NetHelpers.UpnpExternalIp();
                            }
                            else
                            {
                                forwardState = ForwardState.Declined;
                                forwardReason = why;
                            }
                        }
                        catch (Exception routerError)
                        {
                            forwardState = ForwardState.Declined;
                            forwardReason = routerError.Message;
                        }
                    }

                    if (Volatile.Read(ref _generation) != generation)
                    {
                        return;
                    }

                    // STUN is the independent view of how this line appears from outside; the router's
                    // own report is only a fallback (it can be a private address on CGNAT). A gateway
                    // that ACCEPTED the mapping is not proof the port is open: on carrier-grade NAT, or
                    // behind a second router, the address it reports is private or disagrees with STUN,
                    // and the port is not really open - say so instead of promising reachability.
                    var stunIp = NetHelpers.StunPublicIp();
                    if (forwardState == ForwardState.Mapped)
                    {
                        if (!NetHelpers.IsPublicIPv4(externalIp))
                        {
                            forwardState = ForwardState.Declined;
                            forwardReason = "your router's internet address is private (carrier-grade NAT)";
                            externalIp = null;
                        }
                        else if (!string.IsNullOrEmpty(stunIp)
                            && !string.Equals(externalIp, stunIp, StringComparison.Ordinal))
                        {
                            forwardState = ForwardState.Declined;
                            forwardReason = "double NAT - the router is behind another router";
                            externalIp = null;
                        }
                    }

                    // A superseded session must not publish an address: STUN above can block for
                    // seconds, and the generation may have moved on while it did.
                    if (Volatile.Read(ref _generation) != generation)
                    {
                        return;
                    }

                    var publicIp = NetHelpers.IsPublicIPv4(stunIp)
                        ? stunIp
                        : NetHelpers.IsPublicIPv4(externalIp) ? externalIp : null;
                    if (publicIp != null)
                    {
                        _publicEndpoint = publicIp + ":"
                            + (forwardState == ForwardState.Mapped
                                ? externalPort : port).ToString(CultureInfo.InvariantCulture);
                    }
                }
                catch (Exception e) { CoopLog.Swallow(e); }
                finally
                {
                    // Publish under the lock, re-checking the generation there, so this cannot race
                    // Disconnect: Disconnect bumps the generation BEFORE it reads _mappingEpoch, so
                    // only two orderings are possible - we publish and Disconnect removes it, or we
                    // see the change here and remove it ourselves. A check outside the lock left a
                    // window where the mapping was published after Disconnect had already looked.
                    bool current;
                    lock (_gate)
                    {
                        current = Volatile.Read(ref _generation) == generation;
                        if (current)
                        {
                            _forwardState = forwardState;
                            _forwardReason = forwardReason;
                            _mappingEpoch = epoch;
                        }
                    }

                    if (current)
                    {
                        _addressLookupDone = true;
                    }
                    else if (epoch != 0)
                    {
                        // Hosting ended (or started over) while the mapping was still being made, so
                        // nothing else will ever remove it: remove THIS epoch here, before its lease
                        // expires, without touching a newer session's mapping.
                        try
                        {
                            NetHelpers.RemoveMapping(epoch);
                        }
                        catch (Exception e) { CoopLog.Swallow(e); }
                    }
                }
            })
            {
                IsBackground = true,
                Name = "CoopHostAddress",
            }.Start();
        }

        public void Disconnect(ConnectionId connection)
        {
            if (connection.Value == 0
                || Interlocked.Read(ref _active) != connection.Value)
            {
                return;
            }

            Interlocked.Increment(ref _generation);
            Interlocked.Exchange(ref _active, 0);
            _publicEndpoint = null;
            _addressLookupDone = false;

            // The runtime owns the bearer and disposes it on the pump thread. This method only
            // releases backend-owned state (the router mapping and the advertised address).
            long unmapEpoch;
            lock (_gate)
            {
                unmapEpoch = _mappingEpoch;
                _mappingEpoch = 0;
                _forwardState = ForwardState.None;
                _forwardReason = null;
                _warning = null;
                _status = "Ready.";
            }

            if (unmapEpoch != 0)
            {
                // Close only the mapping THIS session made, on a worker: the old session's mapping
                // must not be removed by a newer one that has already re-opened the same port.
                new Thread(() =>
                {
                    try
                    {
                        NetHelpers.RemoveMapping(unmapEpoch);
                    }
                    catch (Exception e) { CoopLog.Swallow(e); }
                })
                {
                    IsBackground = true,
                    Name = "CoopUnmap",
                }.Start();
            }
        }
    }
}
