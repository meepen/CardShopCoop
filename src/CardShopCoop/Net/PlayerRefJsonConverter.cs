using System;
using CardShopCoop.Api;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CardShopCoop.Net
{
    /// <summary>
    /// A DTO may carry a <see cref="PlayerRef"/> to name a session player. The wire carries the
    /// host-canonical id and the sender's name snapshot; the receiver derives IsHost from id 0 and
    /// IsSelf from its own role/id. No roster lookup is required, so a reference works even before
    /// the local roster has synced.
    /// </summary>
    internal sealed class PlayerRefJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type type) => type == typeof(PlayerRef);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is not PlayerRef player)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartObject();
            writer.WritePropertyName("id");
            writer.WriteValue(player.Id);
            if (!string.IsNullOrEmpty(player.Name))
            {
                writer.WritePropertyName("name");
                writer.WriteValue(player.Name);
            }
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            var item = JObject.Load(reader);
            var idToken = item["id"];
            if (idToken == null)
            {
                throw new JsonSerializationException("PlayerRef has no player id.");
            }

            var id = idToken.Value<int>();
            if (id < 0)
            {
                return null;
            }

            var name = item["name"]?.Value<string>();
            var isHost = id == 0;
            var isSelf = isHost
                ? CoopCore.Role == CoopRole.Host
                : CoopCore.Role == CoopRole.Client && id == CoopCore.LocalConnectionId;
            return new PlayerRef(id, name, isHost, isSelf);
        }
    }
}
