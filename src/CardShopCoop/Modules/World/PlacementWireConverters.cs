using System;
using System.Collections.Generic;
using CardShopCoop.Modules.Catalog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    public sealed class PlacementPopulationMessageConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
            => objectType == typeof(PlacementPopulationMessage);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var message = (PlacementPopulationMessage)value;
            writer.WriteStartArray();
            for (var kind = 0; message?.Entries != null && kind < message.Entries.Count; kind++)
            {
                writer.WriteStartArray();
                var entries = message.Entries[kind];
                if (entries != null)
                {
                    for (var i = 0; i < entries.Count; i++)
                    {
                        var entry = entries[i];
                        if (entry == null)
                        {
                            continue;
                        }

                        writer.WriteStartObject();
                        writer.WritePropertyName("Id");
                        writer.WriteValue(entry.Id);
                        writer.WritePropertyName("ObjType");
                        writer.WriteValue(PlacementWire.EnumValue(kind, entry.ObjType));
                        writer.WritePropertyName("Pos");
                        serializer.Serialize(writer, entry.Pos);
                        writer.WritePropertyName("Rot");
                        serializer.Serialize(writer, entry.Rot);
                        writer.WritePropertyName("IsBoxed");
                        writer.WriteValue(entry.IsBoxed);
                        writer.WritePropertyName("BoxedPos");
                        serializer.Serialize(writer, entry.BoxedPos);
                        writer.WritePropertyName("BoxedRot");
                        serializer.Serialize(writer, entry.BoxedRot);
                        writer.WriteEndObject();
                    }
                }

                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            var message = new PlacementPopulationMessage();
            var rows = JArray.Load(reader);
            for (var kind = 0; rows != null && kind < rows.Count; kind++)
            {
                message.Entries.Add(ReadEntries(rows[kind] as JArray, kind, serializer));
            }

            return message;
        }

        private static List<PlacementPopulationEntry> ReadEntries(JArray row, int kind,
            JsonSerializer serializer)
        {
            var result = new List<PlacementPopulationEntry>();
            for (var i = 0; row != null && i < row.Count; i++)
            {
                var item = row[i] as JObject;
                if (item == null || item["Id"] == null)
                {
                    continue;
                }

                var hostObjectType = item["ObjType"]?.Value<int>() ?? 0;
                var resolved = CatalogIdMap.TryFromHostValue(PlacementWire.EnumKindFor(kind),
                    hostObjectType, out var objectType);
                result.Add(new PlacementPopulationEntry
                {
                    Id = (ushort)item["Id"].Value<int>(),
                    ObjType = objectType,
                    Unresolved = !resolved,
                    Pos = PlacementWire.ReadVector(item, "Pos", serializer),
                    Rot = PlacementWire.ReadQuaternion(item, "Rot", serializer),
                    IsBoxed = item["IsBoxed"]?.Value<bool>() ?? false,
                    BoxedPos = PlacementWire.ReadVector(item, "BoxedPos", serializer),
                    BoxedRot = PlacementWire.ReadQuaternion(item, "BoxedRot", serializer),
                });
            }

            return result;
        }
    }

    public sealed class PlacementMoveEntryConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
            => objectType == typeof(PlacementMoveEntry);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var entry = (PlacementMoveEntry)value;
            writer.WriteStartObject();
            writer.WritePropertyName("Key");
            writer.WriteValue(entry.Key);
            writer.WritePropertyName("Type");
            writer.WriteValue(PlacementWire.EnumValue(entry.Key >> 24, entry.Type));
            writer.WritePropertyName("Pos");
            serializer.Serialize(writer, entry.Pos);
            writer.WritePropertyName("Rot");
            serializer.Serialize(writer, entry.Rot);
            writer.WritePropertyName("IsBoxed");
            writer.WriteValue(entry.IsBoxed);
            writer.WritePropertyName("BoxedPos");
            serializer.Serialize(writer, entry.BoxedPos);
            writer.WritePropertyName("BoxedRot");
            serializer.Serialize(writer, entry.BoxedRot);
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            var item = JObject.Load(reader);
            var key = item["Key"].Value<int>();
            var hostType = item["Type"]?.Value<int>() ?? 0;
            CatalogIdMap.TryFromHostValue(PlacementWire.EnumKindFor(key >> 24), hostType,
                out var wireObjectType);
            return new PlacementMoveEntry
            {
                Key = key,
                Type = wireObjectType,
                Pos = PlacementWire.ReadVector(item, "Pos", serializer),
                Rot = PlacementWire.ReadQuaternion(item, "Rot", serializer),
                IsBoxed = item["IsBoxed"]?.Value<bool>() ?? false,
                BoxedPos = PlacementWire.ReadVector(item, "BoxedPos", serializer),
                BoxedRot = PlacementWire.ReadQuaternion(item, "BoxedRot", serializer),
            };
        }
    }

    internal static class PlacementWire
    {
        internal static EnumKind EnumKindFor(int kind)
            => kind == PlacementApi.DecorationKind ? EnumKind.DecoObject : EnumKind.ObjectType;

        internal static int EnumValue(int kind, int value)
            => CatalogIdMap.ToHostValue(EnumKindFor(kind), value);

        internal static Vector3 ReadVector(JObject value, string name, JsonSerializer serializer)
            => value[name] == null ? default : value[name].ToObject<Vector3>(serializer);

        internal static Quaternion ReadQuaternion(JObject value, string name,
            JsonSerializer serializer)
            => value[name] == null ? Quaternion.identity
                : value[name].ToObject<Quaternion>(serializer);
    }
}
