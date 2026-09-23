using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using CardShopCoop.Net.Protocol;
using CardShopCoop.Util;

namespace CardShopCoop.Net
{
    /// <summary>Typed representation of one protocol message. Payload serialization is
    /// centralized in <see cref="WireCodec"/>; message DTOs contain data only.</summary>
    public interface INetMessage
    {
    }

    public static class NetMessageCodec
    {
        public static byte[] Encode(INetMessage message)
        {
            // Named framing remains the default. In particular, connection-phase messages must
            // be usable before an application has opted its transport into compact framing.
            return EncodeNamed(message);
        }

        /// <summary>Encodes a message with its Type.FullName token.</summary>
        public static byte[] EncodeNamed(INetMessage message)
        {
            return EncodeNamed(message, MessageRegistry.CurrentSnapshot);
        }

        /// <summary>
        /// Encodes a message using either named or compact framing from the supplied session
        /// snapshot. The boolean is deliberately an opt-in transport choice; false is named.
        /// </summary>
        public static byte[] Encode(INetMessage message, ProtocolSnapshot snapshot, bool compactIds)
        {
            return compactIds ? EncodeCompact(message, snapshot) : EncodeNamed(message, snapshot);
        }

        private static byte[] EncodeNamed(INetMessage message, ProtocolSnapshot snapshot)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }

            var perfStart = PerfProbe.StartThreadMetric();
            try
            {
                if (!snapshot.TryGet(message.GetType(), out var descriptor))
                {
                    throw new InvalidDataException("No network message registration for "
                        + message.GetType().FullName);
                }

                var nameBytes = WireCodec.StrictUtf8.GetBytes(descriptor.WireName);
                if (nameBytes.Length <= 0 || nameBytes.Length > Msg.MaxMessageNameBytes)
                {
                    throw new InvalidDataException("Message name exceeds the protocol limit for "
                        + descriptor.WireName);
                }

                var payload = WireCodec.Serialize(message);
                return CreateFrame(Msg.NamedTokenKind, nameBytes, payload, true);
            }
            finally
            {
                PerfProbe.EndThreadMetric("net.encode.", message.GetType(), perfStart);
            }
        }

        /// <summary>
        /// Encodes a message with the ID owned by a frozen protocol snapshot. Callers should
        /// retain and use the same snapshot for the lifetime of the transport session.
        /// </summary>
        public static byte[] EncodeCompact(INetMessage message, ProtocolSnapshot snapshot)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            if (!snapshot.IsFrozen)
            {
                throw new InvalidOperationException("Compact framing requires a frozen protocol snapshot");
            }
            if (!snapshot.TryGetId(message.GetType(), out var id))
            {
                throw new InvalidDataException("No compact message ID for " + message.GetType().FullName);
            }

            var perfStart = PerfProbe.StartThreadMetric();
            try
            {
                var payload = WireCodec.Serialize(message);
                return CreateFrame(Msg.CompactTokenKind, BitConverter.GetBytes(id), payload, false);
            }
            finally
            {
                PerfProbe.EndThreadMetric("net.encode.", message.GetType(), perfStart);
            }
        }

        /// <summary>Uses the current frozen snapshot for compact framing.</summary>
        public static byte[] EncodeCompact(INetMessage message)
        {
            return EncodeCompact(message, MessageRegistry.CurrentSnapshot);
        }

        private static byte[] CreateFrame(byte tokenKind, byte[] token, byte[] payload,
            bool named)
        {
            if (token == null || payload == null)
            {
                throw new ArgumentNullException(token == null ? nameof(token) : nameof(payload));
            }

            if (named)
            {
                if (tokenKind != Msg.NamedTokenKind || token.Length <= 0
                    || token.Length > Msg.MaxMessageNameBytes || token.Length > ushort.MaxValue)
                {
                    throw new InvalidDataException("Encoded message name exceeds the protocol limit");
                }
            }
            else if (tokenKind != Msg.CompactTokenKind || token.Length != Msg.CompactIdSize)
            {
                throw new InvalidDataException("Encoded compact message token is invalid");
            }

            if (payload.Length > MessageRegistry.MaxSerializedPayloadBytes)
            {
                throw new InvalidDataException("Encoded payload exceeds the protocol limit");
            }

            var tokenLength = named ? Msg.TokenKindSize + Msg.NameLengthSize + token.Length
                : Msg.TokenKindSize + Msg.CompactIdSize;
            var declaredLong = (long)tokenLength + payload.Length;
            if (declaredLong < Msg.MinimumDeclaredFrameSize || declaredLong > Msg.MaxFrameSize)
            {
                throw new InvalidDataException("Encoded message exceeds the protocol frame limit");
            }

            var declared = (int)declaredLong;
            var frame = new byte[Msg.FrameHeaderSize + declared];
            Buffer.BlockCopy(BitConverter.GetBytes(declared), 0, frame, 0, Msg.FrameHeaderSize);
            frame[Msg.FrameHeaderSize] = tokenKind;
            var tokenOffset = Msg.FrameHeaderSize + Msg.TokenKindSize;
            if (named)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(token.Length), 0, frame, tokenOffset,
                    Msg.NameLengthSize);
                tokenOffset += Msg.NameLengthSize;
            }

            Buffer.BlockCopy(token, 0, frame, tokenOffset, named ? token.Length : Msg.CompactIdSize);
            var payloadOffset = named
                ? tokenOffset + token.Length
                : tokenOffset + Msg.CompactIdSize;
            Buffer.BlockCopy(payload, 0, frame, payloadOffset, payload.Length);
            return frame;
        }

    }

    /// <summary>Single protocol payload serializer. JSON is intentionally used instead of
    /// UnityEngine.JsonUtility: Json.NET supports converters, which lets enum id translation
    /// remain exactly at the wire boundary.</summary>
    public static class WireCodec
    {
        public static byte[] Serialize(INetMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            return SerializeObject(message, message.GetType());
        }

        public static byte[] SerializeObject(object value)
        {
            if (value == null)
            {
                throw new ArgumentNullException("value");
            }

            return SerializeObject(value, value.GetType());
        }

        private static byte[] SerializeObject(object value, Type type)
        {
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, type, WireSettings.Settings));
        }

        internal static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        [ThreadStatic] private static StringWriter _lengthWriter;
        [ThreadStatic] private static JsonSerializer _lengthSerializer;
        [ThreadStatic] private static char[] _lengthChars;

        /// <summary>UTF8 payload length of a value without materializing the JSON string or
        /// byte[] (used for chunk sizing on the hot unreliable NPC stream).</summary>
        public static int SerializeUtf8Length(object value)
        {
            if (value == null)
            {
                throw new ArgumentNullException("value");
            }

            if (_lengthWriter == null)
            {
                _lengthWriter = new StringWriter(CultureInfo.InvariantCulture);
                _lengthSerializer = JsonSerializer.Create(WireSettings.Settings);
            }
            var sb = _lengthWriter.GetStringBuilder();
            sb.Length = 0;
            _lengthSerializer.Serialize(_lengthWriter, value, value.GetType());
            var n = sb.Length;
            if (n == 0)
            {
                return 0;
            }

            if (_lengthChars == null || _lengthChars.Length < n)
            {
                _lengthChars = new char[n];
            }

            sb.CopyTo(0, _lengthChars, 0, n);
            return Encoding.UTF8.GetByteCount(_lengthChars, 0, n);
        }

        /// <summary>Decodes a JSON payload that lives inside a larger frame buffer without
        /// copying it out first: callers pass the exact (offset, count) slice instead of a
        /// freshly allocated byte[]. The per-frame copy was pure garbage on the hot receive
        /// path, where the same private frame byte[] already holds the payload.</summary>
        public static INetMessage Deserialize(Type type, byte[] payload, int offset, int count)
        {
            if (type == null)
            {
                throw new ArgumentNullException("type");
            }

            if (payload == null)
            {
                throw new ArgumentNullException("payload");
            }

            if (offset < 0 || count < 0 || offset > payload.Length - count)
            {
                throw new ArgumentOutOfRangeException("payload");
            }

            if (count > MessageRegistry.MaxSerializedPayloadBytes)
            {
                throw new InvalidDataException("Payload exceeds the protocol limit");
            }

            return (INetMessage)JsonConvert.DeserializeObject(
                StrictUtf8.GetString(payload, offset, count), type, WireSettings.Settings);
        }
    }
}
