using System;
using System.Text;
using Newtonsoft.Json;

namespace CardShopCoop.Net
{
    /// <summary>Typed representation of one protocol message. Payload serialization is
    /// centralized in <see cref="WireCodec"/>; message DTOs contain data only.</summary>
    public interface INetMessage
    {
        MsgType Type { get; }
    }

    public static class NetMessageCodec
    {
        public static byte[] Encode(INetMessage message)
        {
            if (message == null) throw new ArgumentNullException("message");
            var payload = WireCodec.Serialize(message);
            var frame = new byte[Msg.FrameHeaderSize + Msg.TypeSize + payload.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(payload.Length + Msg.TypeSize), 0, frame, 0, Msg.FrameHeaderSize);
            frame[Msg.FrameHeaderSize] = (byte)message.Type;
            Buffer.BlockCopy(payload, 0, frame, Msg.FrameHeaderSize + Msg.TypeSize, payload.Length);
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
            if (message == null) throw new ArgumentNullException("message");
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message, message.GetType(), WireSettings.Settings));
        }

        public static INetMessage Deserialize(Type type, byte[] payload)
        {
            if (type == null) throw new ArgumentNullException("type");
            if (payload == null) throw new ArgumentNullException("payload");
            return (INetMessage)JsonConvert.DeserializeObject(Encoding.UTF8.GetString(payload), type, WireSettings.Settings);
        }
    }
}
