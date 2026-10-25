using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>Structured wire form for GameObjectId. No string formatting/parsing.</summary>
    internal sealed class GameObjectIdJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type type) => type == typeof(GameObjectId);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var id = (GameObjectId)value;
            writer.WriteStartObject();
            writer.WritePropertyName("kind");
            writer.WriteValue((byte)id.Kind);
            writer.WritePropertyName("sub");
            writer.WriteValue(id.SubKind);
            writer.WritePropertyName("value");
            writer.WriteValue(id.Value);
            if (id.Kind == GameObjectKind.PackagingBox
                || id.Kind == GameObjectKind.Furniture)
            {
                writer.WritePropertyName("guid");
                writer.WriteValue(id.GuidValue);
            }
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return default(GameObjectId);
            }

            var id = Parse(JObject.Load(reader));
            // A struct cannot carry null. An invalid id deserializes to the default value
            // (Kind = None); entity-reference fields then map it to null through
            // GameObjectEntityJsonConverter, and handlers treat it as unknown.
            return id.IsValid ? id : default(GameObjectId);
        }

        internal static GameObjectId Parse(JObject item)
        {
            if (item == null)
            {
                return default;
            }

            var kind = (GameObjectKind)(item["kind"]?.Value<byte>() ?? 0);
            var subKind = item["sub"]?.Value<byte>() ?? 0;
            var value = item["value"]?.Value<long>() ?? 0;
            var guid = Guid.Empty;
            var guidToken = item["guid"];
            if (guidToken != null)
            {
                Guid.TryParse(guidToken.Value<string>(), out guid);
            }

            return GameObjectId.Create(kind, subKind, value, guid);
        }
    }
}
