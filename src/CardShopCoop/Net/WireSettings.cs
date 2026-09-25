using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using CardShopCoop.Modules.Catalog;

namespace CardShopCoop.Net
{
    public static class WireSettings
    {
        public static readonly JsonSerializerSettings Settings = Create();

        private static JsonSerializerSettings Create()
        {
            var settings = new JsonSerializerSettings
            {
                Culture = CultureInfo.InvariantCulture,
                Formatting = Formatting.None,
                // Preserve constructor-initialised collection defaults when an untrusted peer
                // explicitly sends null. Nullable scalars are handled by their DTO defaults.
                NullValueHandling = NullValueHandling.Ignore,
                ContractResolver = new WireContractResolver(),
                MissingMemberHandling = MissingMemberHandling.Error,
                MaxDepth = 128,
            };
            settings.Converters.Add(new EnumWireConverter<EItemType>(EnumKind.ItemType));
            settings.Converters.Add(new EnumWireConverter<EObjectType>(EnumKind.ObjectType));
            settings.Converters.Add(new EnumWireConverter<EDecoObject>(EnumKind.DecoObject));
            settings.Converters.Add(new EnumWireConverter<ECardExpansionType>(EnumKind.CardExpansion));
            settings.Converters.Add(new EnumWireConverter<EMonsterType>(EnumKind.MonsterType));
            settings.Converters.Add(new EnumWireConverter<ERarity>(EnumKind.Rarity));
            settings.Converters.Add(new EnumWireConverter<ECollectionPackType>(EnumKind.CollectionPack));
            // Every enum that is NOT a gated registry enum travels as its member-name string, so a
            // non-renumbered enum can never be silently misread. Registered after the gated
            // converters, which therefore win for their own types.
            settings.Converters.Add(new Newtonsoft.Json.Converters.StringEnumConverter());
            settings.Converters.Add(new Vector3Converter());
            settings.Converters.Add(new QuaternionConverter());
            return settings;
        }
    }

    internal sealed class WireContractResolver : DefaultContractResolver
    {
        protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
        {
            var properties = base.CreateProperties(type, MemberSerialization.OptOut);
            for (var i = properties.Count - 1; i >= 0; i--)
            {
                var p = properties[i];
                if (p.Ignored || (p.DeclaringType != null && p.DeclaringType.Namespace == "UnityEngine"))
                {
                    properties.RemoveAt(i);
                }
            }
            return properties;
        }
    }

    internal sealed class EnumWireConverter<T> : JsonConverter where T : struct
    {
        private readonly EnumKind _kind;
        public EnumWireConverter(EnumKind kind)
        {
            _kind = kind;
        }
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(T);
        }
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            // The wire carries the HOST's numeric space, the single space every enum uses. The
            // host is the identity translation; the client maps its value into the host's.
            writer.WriteValue(CatalogIdMap.ToHostValue(_kind,
                Convert.ToInt32(value, CultureInfo.InvariantCulture)));
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                throw new JsonSerializationException("Null enum " + objectType);
            }

            if (reader.TokenType != JsonToken.Integer && reader.TokenType != JsonToken.Float)
            {
                throw new JsonSerializationException("Non-numeric " + objectType.Name + " value "
                    + Convert.ToString(reader.Value, CultureInfo.InvariantCulture)
                    + " received: gated enums travel as the host's numeric value");
            }

            var hostValue = Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture);
            var local = CatalogIdMap.FromHostValue(_kind, hostValue);

            return (T)Enum.ToObject(typeof(T), local);
        }
    }

    internal sealed class Vector3Converter : JsonConverter
    {
        public override bool CanConvert(Type t)
        {
            return t == typeof(Vector3);
        }
        public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
        {
            var v = (Vector3)value;
            w.WriteStartArray();
            w.WriteValue(v.x);
            w.WriteValue(v.y);
            w.WriteValue(v.z);
            w.WriteEndArray();
        }
        public override object ReadJson(JsonReader r, Type t, object existing, JsonSerializer s)
        {
            var a = ReadArray(r, 3);
            return new Vector3(Convert.ToSingle(a[0], CultureInfo.InvariantCulture), Convert.ToSingle(a[1], CultureInfo.InvariantCulture), Convert.ToSingle(a[2], CultureInfo.InvariantCulture));
        }
        private static List<object> ReadArray(JsonReader r, int count)
        {
            var a = new List<object>();
            if (r.TokenType != JsonToken.StartArray)
            {
                throw new JsonSerializationException("Expected vector array");
            }

            while (r.Read() && r.TokenType != JsonToken.EndArray)
            {
                if (a.Count >= count)
                {
                    throw new JsonSerializationException("Too many vector components");
                }

                a.Add(r.Value);
            }
            if (a.Count != count)
            {
                throw new JsonSerializationException("Wrong vector component count");
            }

            return a;
        }
    }

    internal sealed class QuaternionConverter : JsonConverter
    {
        public override bool CanConvert(Type t)
        {
            return t == typeof(Quaternion);
        }
        public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
        {
            var q = (Quaternion)value;
            w.WriteStartArray();
            w.WriteValue(q.x);
            w.WriteValue(q.y);
            w.WriteValue(q.z);
            w.WriteValue(q.w);
            w.WriteEndArray();
        }
        public override object ReadJson(JsonReader r, Type t, object existing, JsonSerializer s)
        {
            var a = new List<object>();
            if (r.TokenType != JsonToken.StartArray)
            {
                throw new JsonSerializationException("Expected quaternion array");
            }

            while (r.Read() && r.TokenType != JsonToken.EndArray)
            {
                if (a.Count >= 4)
                {
                    throw new JsonSerializationException("Too many quaternion components");
                }

                a.Add(r.Value);
            }
            if (a.Count != 4)
            {
                throw new JsonSerializationException("Wrong quaternion component count");
            }

            return new Quaternion(Convert.ToSingle(a[0], CultureInfo.InvariantCulture), Convert.ToSingle(a[1], CultureInfo.InvariantCulture), Convert.ToSingle(a[2], CultureInfo.InvariantCulture), Convert.ToSingle(a[3], CultureInfo.InvariantCulture));
        }
    }
}


