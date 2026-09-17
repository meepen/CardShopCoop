using System.Text;
using Newtonsoft.Json.Linq;
using CardShopCoop;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception(name);
    Console.WriteLine("PASS " + name);
    passed++;
}

var connection = new Connection(7);
Check(connection.State == ConnectionState.Handshaking, "production connection starts handshaking");
Check(!connection.TryTransition(ConnectionState.FullyJoined), "production rejects skipping transfer");
Check(connection.TryTransition(ConnectionState.Transferring), "production enters transfer");
Check(connection.TryTransition(ConnectionState.FullyJoined), "production enters fully joined");
Check(!connection.TryTransition(ConnectionState.Handshaking), "production rejects backwards transition");

var first = new DisconnectInfo(" first reason ", code: "first", retryable: true, phase: ConnectionState.FullyJoined);
var second = new DisconnectInfo("second reason", code: "second");
Check(connection.BeginDisconnect(first), "production claims terminal transition");
Check(!connection.BeginDisconnect(second) && ReferenceEquals(connection.DisconnectReason, first),
    "production preserves the first terminal reason");
Check(connection.TryMarkDisconnected() && !connection.TryMarkDisconnected(), "production terminal cleanup is one-shot");

var longReason = new string('r', DisconnectInfo.MaxDetailLength + 20);
var longCode = new string('c', DisconnectInfo.MaxCodeLength + 20);
var disconnect = new DisconnectMessage
{
    Reason = longReason,
    Code = longCode,
    Retryable = true,
    Phase = (int)ConnectionState.Transferring
};
var decoded = (DisconnectMessage)WireCodec.Deserialize(typeof(DisconnectMessage), WireCodec.Serialize(disconnect), 0,
    WireCodec.Serialize(disconnect).Length);
Check(decoded.Reason.Length == DisconnectInfo.MaxDetailLength && decoded.Code.Length == DisconnectInfo.MaxCodeLength,
    "Disconnect wire DTO bounds reason and code");
Check(decoded.Retryable && decoded.Phase == (int)ConnectionState.Transferring,
    "Disconnect wire DTO preserves retry and phase fields");
var rawDisconnect = Encoding.UTF8.GetBytes("{\"Reason\":\"raw\",\"Code\":\"raw_code\",\"Retryable\":true,\"Phase\":2}");
Check(!JObject.Parse(Encoding.UTF8.GetString(WireCodec.Serialize(disconnect))).ContainsKey("Type"),
    "production Disconnect serialization omits Type");
var rawDecoded = (DisconnectMessage)WireCodec.Deserialize(typeof(DisconnectMessage), rawDisconnect, 0, rawDisconnect.Length);
Check(rawDecoded.Reason == "raw" && rawDecoded.Code == "raw_code" && rawDecoded.Retryable && rawDecoded.Phase == 2,
    "production Disconnect deserializes raw Type-less JSON");

foreach (var message in new INetMessage[] { new FullyJoinedMessage(), new FullyJoinedAckMessage(), disconnect })
{
    var frame = NetMessageCodec.Encode(message);
    Check(Msg.TryGetType(frame, out var type) && type == message.Type,
        message.Type + " wire frame carries its production type");
    Check(Msg.TryDecodeFrame(frame, 0, frame.Length, connection, Msg.MaxFrameSize, out var incoming)
        && incoming.Message.GetType() == message.GetType(), message.Type + " wire DTO round-trips");
}

var router = new MessageRouter();
var productionCoin = new CoinSetMessage { Coin = 42 };
var productionEmote = new EmoteMessage { Emote = 3 };
var productionCoinPayload = WireCodec.Serialize(productionCoin);
var productionEmotePayload = WireCodec.Serialize(productionEmote);
Check(MessageRegistry.Deserialize(MsgType.CoinSet, productionCoinPayload, 0, productionCoinPayload.Length).GetType()
    == typeof(CoinSetMessage), "MessageRegistry resolves production CoinSetMessage");
Check(MessageRegistry.Deserialize(MsgType.Emote, productionEmotePayload, 0, productionEmotePayload.Length).GetType()
    == typeof(EmoteMessage), "MessageRegistry resolves production EmoteMessage");
var fullyJoinedCalls = 0;
var ackCalls = 0;
var baselineCalls = 0;
var gameplayCalls = 0;
router.Register<FullyJoinedMessage>((_, _) => fullyJoinedCalls++);
router.Register<FullyJoinedAckMessage>((_, _) => ackCalls++);
router.Register<CoinSetMessage>((_, _) => baselineCalls++);
router.Register<EmoteMessage>((_, _) => gameplayCalls++);

var hostConnection = new Connection(1);
var transport = new FakeTransport { Connections = new[] { hostConnection } };
var hostContext = new MessageContext { Connection = hostConnection, Role = CoopRole.Host, Transport = transport };
Check(!router.Dispatch(hostContext, new FullyJoinedMessage()), "FullyJoined before transfer is rejected");
Check(hostConnection.TryTransition(ConnectionState.Transferring), "routing fixture enters transfer");
Check(router.Dispatch(hostContext, new FullyJoinedMessage()), "FullyJoined from active Transferring connection is allowed");
Check(fullyJoinedCalls == 1, "allowed FullyJoined reaches production router handler");
Check(hostConnection.TryTransition(ConnectionState.FullyJoined), "join handler can complete production transition");
Check(!router.Dispatch(hostContext, new FullyJoinedMessage()), "duplicate FullyJoined after completion is rejected");

var clientConnection = new Connection(2);
clientConnection.TryTransition(ConnectionState.Transferring);
var clientTransport = new FakeTransport { Connections = new[] { clientConnection } };
var clientContext = new MessageContext { Connection = clientConnection, Role = CoopRole.Client, Transport = clientTransport };
Check(router.Dispatch(clientContext, productionCoin), "host baseline is allowed during client transfer");
Check(baselineCalls == 1, "baseline reaches handler before FullyJoinedAck");
Check(!router.Dispatch(clientContext, productionEmote), "gameplay is rejected before client FullyJoinedAck");
Check(router.Dispatch(clientContext, new FullyJoinedAckMessage()), "FullyJoinedAck control is admitted before completion");
Check(ackCalls == 1, "FullyJoinedAck reaches production router handler");

var staleContext = new MessageContext
{
    Connection = clientConnection,
    Role = CoopRole.Host,
    Transport = new FakeTransport { Connections = new[] { new Connection(99) } }
};
Check(!router.Dispatch(staleContext, new FullyJoinedMessage()), "FullyJoined rejects stale connection identity");
var terminal = new Connection(3);
terminal.BeginDisconnect(new DisconnectInfo("local"));
var terminalContext = new MessageContext { Connection = terminal, Role = CoopRole.Host, Transport = transport };
Check(router.Dispatch(terminalContext, new FullyJoinedMessage()) == false, "terminal connection rejects join control");
router.Register<DisconnectMessage>((_, message) =>
    Check(message.Reason == "remote failure", "remote disconnect reason reaches structured DTO handler"));
Check(router.Dispatch(terminalContext, new DisconnectMessage { Reason = "remote failure" }),
    "terminal Disconnect control remains admissible");

var remote = new Connection(4);
var remoteReason = new DisconnectInfo("peer stopped", remote: true, code: "peer_stop");
Check(remote.RecordRemoteDisconnect(remoteReason) && ReferenceEquals(remote.DisconnectReason, remoteReason),
    "production records structured remote reason");

Console.WriteLine($"{passed} production connection state checks passed.");
