using PeerConnection = CardShopCoop.Net.Connection.PeerConnection;
using System;
using System.IO;
using System.Text;
using System.Threading;
using CardShopCoop.Util;
using CardShopCoop.Net.Protocol;

namespace CardShopCoop.Net
{
    /// <summary>One received message, already reassembled and decoded from the wire.
    /// Transports never expose partial frames to callers.</summary>
    public struct InMsg
    {
        public PeerConnection Connection;
        public INetMessage Message;
        internal bool ReceivedBeforeMessageIdActivation;

        public Type MessageType
        {
            get
            {
                return Message?.GetType();
            }
        }
    }

    /// <summary>Frame builders/parsers. Wire format per frame:
    /// [int32 frameLen][byte tokenKind][token][UTF-8 JSON payload]. A named token is
    /// [int32 nameLen][UTF-8 Type.FullName]; a compact token is [uint16 messageId].</summary>
    public static class Msg
    {
        public const int FrameHeaderSize = 4;
        public const int TokenKindSize = 1;
        public const int NameLengthSize = 4;
        public const int CompactIdSize = 2;
        public const int MinimumDeclaredFrameSize = TokenKindSize + CompactIdSize;
        public const int MinimumFrameSize = FrameHeaderSize + MinimumDeclaredFrameSize;
        public const byte NamedTokenKind = 0;
        public const byte CompactTokenKind = 1;
        public const int MaxMessageNameBytes = 4096;
        public const int MaxFrameSize = 64 * 1024 * 1024;
        // Patch releases must not change the wire revision. A new message or a
        // substantial change behind an existing message requires a minor bump.
        // 1.2.3 therefore becomes 102 (major * 100 + minor).
        public static readonly int WireVersion = DeriveWireVersion(CoopPlugin.Version);
        private const int DecodeWarningCooldownMs = 2000;
        private const int DecodeWarningBucketCount = 64;
        private static readonly int[] LastDecodeWarning = CreateDecodeWarningBuckets();

        private static int[] CreateDecodeWarningBuckets()
        {
            var buckets = new int[DecodeWarningBucketCount];
            for (var i = 0; i < buckets.Length; i++)
            {
                buckets[i] = int.MinValue;
            }

            return buckets;
        }

        internal static void LogDecodeWarning(string name, string warning)
        {
            var bucket = GetDecodeWarningBucket(name);
            var now = Environment.TickCount;
            while (true)
            {
                var last = Thread.VolatileRead(ref LastDecodeWarning[bucket]);
                if (last != int.MinValue
                    && unchecked(now - last) < DecodeWarningCooldownMs)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref LastDecodeWarning[bucket], now, last) == last)
                {
                    break;
                }
            }
            CoopPlugin.Log?.LogWarning(warning);
        }

        private static int GetDecodeWarningBucket(string name)
        {
            unchecked
            {
                var hash = 2166136261u;
                if (name != null)
                {
                    for (var i = 0; i < name.Length; i++)
                    {
                        hash ^= name[i];
                        hash *= 16777619u;
                    }
                }

                return (int)(hash % DecodeWarningBucketCount);
            }
        }

        private static int DeriveWireVersion(string version)
        {
            if (!Version.TryParse(version, out var parsed)
                || parsed.Major < 0 || parsed.Minor < 0
                || parsed.Major > 999 || parsed.Minor > 999)
            {
                throw new InvalidOperationException(
                    CoopPlugin.Name + " version must have major and minor components from 0 to 999: " + version);
            }

            return parsed.Major * 100 + parsed.Minor;
        }

        /// <summary>
        /// Decodes one complete wire frame. This is the only protocol-framing entry
        /// point used by transports. LAN UDP/KCP and Steam KCP over Steam Networking Sockets
        /// both hand this method a complete frame and receive the same result, so neither
        /// transport knows about the message-name encoding or payload layout.
        /// </summary>
        public static bool TryDecodeFrame(byte[] frame, int offset, int count, PeerConnection connection,
            int maxFrame, out InMsg message)
        {
            return TryDecodeFrame(frame, offset, count, connection, maxFrame,
                MessageRegistry.CurrentSnapshot, false, out message);
        }

        /// <summary>
        /// Decodes named frames and, when activated, compact frames against one immutable
        /// session snapshot. This codec can parse named frames after activation for standalone
        /// callers; session transports enforce named-before-activation and compact-after-
        /// activation admission at their framing boundary.
        /// </summary>
        public static bool TryDecodeFrame(byte[] frame, int offset, int count, PeerConnection connection,
            int maxFrame, ProtocolSnapshot snapshot, bool compactIds, out InMsg message)
        {
            message = default(InMsg);
            if (!TryResolveFrame(frame, offset, count, maxFrame, out var descriptor,
                out var name, out var payloadOffset, out var payloadLength, snapshot, compactIds))
            {
                return false;
            }

            message = new InMsg { Connection = connection };
            // Decode centrally. A malformed or unknown DTO is DROPPED (return false) so a
            // single bad frame can never unwind a transport pump thread - the same fail-safe
            // the transport used to get from the switch's per-message try/catch. Unknown
            // message types and malformed payloads are rate-limited per type.
            //
            // The payload is decoded straight out of the frame slice: an earlier version
            // copied it into a new byte[] per frame, which was pure garbage on the hot
            // receive path. The frame is private to this loop and deserialization is
            // synchronous, so the slice cannot be mutated underneath Json.NET.
            var perfStart = PerfProbe.StartThreadMetric();
            try
            {
                message.Message = MessageRegistry.Deserialize(descriptor, frame, payloadOffset, payloadLength);
            }
            catch (Exception e)
            {
                LogDecodeWarning(name, $"discarding malformed {name} frame: {e.GetType().Name}: {e.Message}");

                return false;
            }
            finally
            {
                PerfProbe.EndThreadMetric("net.decode.", descriptor.MessageType, perfStart);
            }
            return true;
        }

        /// <summary>Returns the type from a complete outbound frame without making
        /// callers depend on the wire offset. Used only for transport scheduling.</summary>
        public static bool TryGetMessageType(byte[] frame, out Type type)
        {
            return TryGetMessageType(frame, false, out type);
        }

        /// <summary>Returns the type from a complete frame, with compact IDs explicitly
        /// enabled only for a session that has completed compact activation.</summary>
        public static bool TryGetMessageType(byte[] frame, bool compactIds, out Type type)
        {
            type = null;
            if (!TryResolveFrame(frame, 0, frame == null ? 0 : frame.Length, MaxFrameSize,
                out var descriptor, out _, out _, out _, MessageRegistry.CurrentSnapshot, compactIds))
            {
                return false;
            }

            type = descriptor.MessageType;
            return true;
        }

        private static bool TryResolveFrame(byte[] frame, int offset, int count, int maxFrame,
            out MessageDescriptor descriptor, out string name, out int payloadOffset,
            out int payloadLength, ProtocolSnapshot snapshot, bool compactIds)
        {
            descriptor = null;
            name = null;
            payloadOffset = 0;
            payloadLength = 0;
            if (snapshot == null || frame == null || offset < 0 || count < MinimumFrameSize
                || offset > frame.Length - count || maxFrame < MinimumDeclaredFrameSize
                || maxFrame > MaxFrameSize)
            {
                return false;
            }

            var declared = BitConverter.ToInt32(frame, offset);
            if (declared < MinimumDeclaredFrameSize || declared > maxFrame
                || declared != count - FrameHeaderSize)
            {
                return false;
            }

            var tokenOffset = offset + FrameHeaderSize;
            var tokenKind = frame[tokenOffset];
            var tokenDataOffset = tokenOffset + TokenKindSize;
            if (tokenKind == NamedTokenKind)
            {
                var namedOverhead = TokenKindSize + NameLengthSize;
                if (declared < namedOverhead)
                {
                    return false;
                }

                var nameLength = BitConverter.ToInt32(frame, tokenDataOffset);
                if (nameLength <= 0 || nameLength > MaxMessageNameBytes
                    || nameLength > declared - namedOverhead)
                {
                    return false;
                }

                var nameOffset = tokenDataOffset + NameLengthSize;
                try
                {
                    name = WireCodec.StrictUtf8.GetString(frame, nameOffset, nameLength);
                }
                catch (DecoderFallbackException)
                {
                    return false;
                }

                if (!snapshot.TryGet(name, out descriptor))
                {
                    LogDecodeWarning(name, "network: no DTO registered for message " + name
                        + " - frame dropped");
                    return false;
                }

                payloadOffset = nameOffset + nameLength;
                payloadLength = declared - namedOverhead - nameLength;
                if (!IsPayloadWithinLimit(name, payloadLength))
                {
                    return false;
                }

                return true;
            }

            if (tokenKind == CompactTokenKind)
            {
                if (declared < MinimumDeclaredFrameSize)
                {
                    return false;
                }

                var id = BitConverter.ToUInt16(frame, tokenDataOffset);
                if (!compactIds)
                {
                    LogDecodeWarning("compact:" + id, "network: compact message ID " + id
                        + " received before message IDs were activated - frame dropped");
                    return false;
                }
                if (!snapshot.IsFrozen)
                {
                    LogDecodeWarning("compact", "network: compact message ID " + id
                        + " received before the protocol snapshot was frozen - frame dropped");
                    return false;
                }
                if (!snapshot.TryGet(id, out descriptor))
                {
                    LogDecodeWarning("compact:" + id, "network: unknown compact message ID " + id
                        + " for protocol snapshot generation " + snapshot.Generation
                        + " - frame dropped");
                    return false;
                }

                name = descriptor.WireName;
                payloadOffset = tokenDataOffset + CompactIdSize;
                payloadLength = declared - MinimumDeclaredFrameSize;
                if (!IsPayloadWithinLimit(name, payloadLength))
                {
                    return false;
                }

                return true;
            }

            LogDecodeWarning("token:" + tokenKind, "network: unknown message token kind "
                + tokenKind + " - frame dropped");
            return false;
        }

        private static bool IsPayloadWithinLimit(string name, int payloadLength)
        {
            if (payloadLength >= 0 && payloadLength <= MessageRegistry.MaxSerializedPayloadBytes)
            {
                return true;
            }

            LogDecodeWarning(name, "network: payload for " + (name ?? "<unknown>")
                + " exceeds the protocol limit - frame dropped");
            return false;
        }

        /// <summary>World/bundle transfers are gzipped: the EPL sidecar json compresses
        /// ~5-10x, turning a minutes-long relay transfer into seconds.</summary>
        public static byte[] Gzip(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            // GZipStream emits no bytes at all for empty input on current runtimes, but an empty
            // gzip payload is indistinguishable from a malformed one to the catalog reader. Emit
            // the canonical empty gzip stream so an empty catalog remains a valid blob that
            // still decompresses to zero bytes.
            if (data.Length == 0)
            {
                return EmptyGzip;
            }

            using (var ms = new MemoryStream())
            {
                using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest))
                {
                    gz.Write(data, 0, data.Length);
                }

                return ms.ToArray();
            }
        }

        // RFC 1952 stream of zero bytes: header, empty final deflate block, zero CRC and ISIZE.
        private static readonly byte[] EmptyGzip =
        {
            0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x03, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        };

        public static byte[] Gunzip(byte[] data)
        {
            using (var src = new MemoryStream(data, writable: false))
            using (var gz = new System.IO.Compression.GZipStream(src, System.IO.Compression.CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                gz.CopyTo(dst);
                return dst.ToArray();
            }
        }

    }
}
