using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Protocol;
using CardShopCoop.Modules.Catalog;
using CardShopCoop.Modules.Prediction;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CardShopCoop.Modules.Presence
{
    /// <summary>Host roster. The host's connection ids are canonical for the session.</summary>
    [NetworkMessage]
    public sealed class PresenceRosterMessage : INetMessage
    {
        // Host-provided identity. This is deliberately not inferred from a client payload or a
        // transport-local id; it lets the module stand alone after CoopCore is cut over.
        public int SelfId;
        public readonly List<PresenceRosterEntry> Entries = new();
    }

    public sealed class PresenceRosterEntry
    {
        public int Id;
        public string Name;
        public ulong SteamId;
    }

    public enum PresenceEntityDeltaKind : byte
    {
        Add = 1,
        Update = 2,
        Remove = 3,
    }

    [NetworkMessage]
    public sealed class PresenceRosterDeltaMessage : INetMessage
    {
        public PresenceEntityDeltaKind Kind;
        public int Id;
        public PresenceRosterEntry Entry;
    }

    /// <summary>Client-to-host appearance intent. The host never trusts a client id from a
    /// payload; the transport connection supplies identity.</summary>
    [NetworkMessage]
    public sealed class PresenceModelRequestMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public bool Female;
        public int ModelIndex;
        public string CustomizationJson;
    }

    /// <summary>Authoritative appearance state. Entry 0 is the host; other ids are host
    /// connection ids, not ids supplied by the clients.</summary>
    [NetworkMessage]
    public sealed class PresenceModelStateMessage : INetMessage
    {
        public readonly List<PresenceModelEntry> Entries = new();
    }

    public sealed class PresenceModelEntry
    {
        public int Id;
        public bool Female;
        public int ModelIndex;
        public string CustomizationJson;
    }

    [NetworkMessage]
    public sealed class PresenceModelDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public PresenceEntityDeltaKind Kind;
        public int Id;
        public PresenceModelEntry Entry;
    }

    /// <summary>Latest local transform and held-object state. Its JSON converter is attached to
    /// the module DTO rather than the shared wire settings, so this feature can be cut over
    /// without teaching the legacy namespace about a second state type.</summary>
    [NetworkMessage(Reliability = Reliability.Transient)]
    [JsonConverter(typeof(PresenceStateJsonConverter))]
    public sealed class PresenceStateMessage : INetMessage
    {
        public Vector3 Position;
        public float Yaw;
        public Vector3 CameraPosition;
        public Quaternion CameraRotation;
        public float Speed;
        public byte Hold;
        public List<int> HoldTypes;
        public List<CardData> HoldCards;
    }

    /// <summary>Host relay of a state sample. SenderId is written by the host from its
    /// authenticated ingress connection and is never accepted from the original client.</summary>
    [NetworkMessage(Reliability = Reliability.Transient)]
    public sealed class PresenceRelayStateMessage : INetMessage
    {
        public int SenderId;
        public PresenceStateMessage State = new();
    }

    [NetworkMessage]
    public sealed class PresenceEmoteMessage : INetMessage
    {
        public byte Emote;
    }

    [NetworkMessage]
    public sealed class PresenceActivityMessage : INetMessage
    {
        public byte Activity;
        public EItemType Pack;
    }

    /// <summary>Host-to-client visual event. SenderId is authoritative and uses the same
    /// canonical id space as PresenceRelayStateMessage.</summary>
    [NetworkMessage]
    public sealed class PresenceRelayTagMessage : INetMessage
    {
        public int SenderId;
        public byte Kind;
        public EItemType Extra;
    }

    /// <summary>Module-local copy of the legacy PlayerState converter. Item ids are translated
    /// by name at the wire edge, and vectors/quaternions retain their
    /// compact array shape from the existing protocol.</summary>
    internal sealed class PresenceStateJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(PresenceStateMessage);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var message = (PresenceStateMessage)value;
            writer.WriteStartObject();
            writer.WritePropertyName("Position");
            serializer.Serialize(writer, message.Position);
            writer.WritePropertyName("Yaw");
            writer.WriteValue(message.Yaw);
            writer.WritePropertyName("CameraPosition");
            serializer.Serialize(writer, message.CameraPosition);
            writer.WritePropertyName("CameraRotation");
            serializer.Serialize(writer, message.CameraRotation);
            writer.WritePropertyName("Speed");
            writer.WriteValue(message.Speed);
            writer.WritePropertyName("Hold");
            writer.WriteValue(message.Hold);
            writer.WritePropertyName("HoldTypes");
            writer.WriteStartArray();
            if (message.HoldTypes != null)
            {
                foreach (var id in message.HoldTypes)
                {
                    writer.WriteValue(CatalogIdMap.ToWireName(EnumKind.ItemType, id));
                }
            }

            writer.WriteEndArray();
            writer.WritePropertyName("HoldCards");
            serializer.Serialize(writer, message.HoldCards);
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType != JsonToken.StartObject)
            {
                throw new JsonSerializationException("PresenceState must be an object");
            }

            var message = new PresenceStateMessage();
            while (reader.Read() && reader.TokenType != JsonToken.EndObject)
            {
                if (reader.TokenType != JsonToken.PropertyName)
                {
                    throw new JsonSerializationException("Invalid PresenceState property");
                }

                var name = (string)reader.Value;
                if (!reader.Read())
                {
                    throw new JsonSerializationException("Unexpected end of PresenceState");
                }

                switch (name)
                {
                    case "Position":
                        message.Position = serializer.Deserialize<Vector3>(reader);
                        break;
                    case "Yaw":
                        message.Yaw = serializer.Deserialize<float>(reader);
                        break;
                    case "CameraPosition":
                        message.CameraPosition = serializer.Deserialize<Vector3>(reader);
                        break;
                    case "CameraRotation":
                        message.CameraRotation = serializer.Deserialize<Quaternion>(reader);
                        break;
                    case "Speed":
                        message.Speed = serializer.Deserialize<float>(reader);
                        break;
                    case "Hold":
                        message.Hold = serializer.Deserialize<byte>(reader);
                        break;
                    case "HoldTypes":
                        ReadHoldTypes(reader, message);
                        break;
                    case "HoldCards":
                        ReadHoldCards(reader, serializer, message);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            return message;
        }

        private static void ReadHoldTypes(JsonReader reader, PresenceStateMessage message)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return;
            }

            if (reader.TokenType != JsonToken.StartArray)
            {
                throw new JsonSerializationException("Presence HoldTypes must be an array");
            }

            message.HoldTypes = new List<int>();
            while (reader.Read() && reader.TokenType != JsonToken.EndArray)
            {
                int local;
                CatalogIdMap.TryReadWireName(EnumKind.ItemType, new JValue(reader.Value), out local);
                message.HoldTypes.Add(local);
            }
        }

        private static void ReadHoldCards(JsonReader reader, JsonSerializer serializer,
            PresenceStateMessage message)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return;
            }

            if (reader.TokenType != JsonToken.StartArray)
            {
                throw new JsonSerializationException("Presence HoldCards must be an array");
            }

            message.HoldCards = new List<CardData>();
            while (reader.Read() && reader.TokenType != JsonToken.EndArray)
            {
                message.HoldCards.Add(serializer.Deserialize<CardData>(reader));
            }
        }
    }
}
