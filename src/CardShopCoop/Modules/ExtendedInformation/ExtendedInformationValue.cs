using System;
using CardShopCoop.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CardShopCoop.Modules.ExtendedInformation
{
    /// <summary>
    /// A typed extended-information value carried by the normal wire codec. The type id is the
    /// session catalog index of the concrete <see cref="ExtendGameObjectInformation{T}"/>
    /// subclass; the information object itself is serialized by the same <see cref="WireSettings"/>
    /// pipeline as every other DTO.
    /// </summary>
    public sealed class ExtendedInformationValue
    {
        public ushort TypeId;
        public object Information;
    }

    /// <summary>
    /// Keeps the catalog id and the dynamically typed information together in one normal message
    /// field. The nested value is written and read through the serializer that already owns the
    /// wire converters, instead of pre-serializing it into a string.
    /// </summary>
    internal sealed class ExtendedInformationValueJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type type) => type == typeof(ExtendedInformationValue);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is not ExtendedInformationValue payload)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartObject();
            writer.WritePropertyName("typeId");
            writer.WriteValue(payload.TypeId);
            if (payload.Information != null)
            {
                writer.WritePropertyName("information");
                serializer.Serialize(writer, payload.Information);
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
            var typeToken = item["typeId"];
            if (typeToken == null)
            {
                throw new JsonSerializationException(
                    "Extended information value has no catalog type id.");
            }

            var typeId = typeToken.Value<ushort>();
            var informationToken = item["information"];
            object information = null;
            if (informationToken != null && informationToken.Type != JTokenType.Null)
            {
                var type = ExtendedInformationCatalog.TypeFor(typeId);
                if (type != null
                    && (ExtendedInformationCodec.IsExtendedInformation(type, out _)
                        || ExtendedInformationCodec.IsGlobalInformation(type)))
                {
                    try
                    {
                        information = informationToken.ToObject(type, serializer);
                    }
                    catch (Exception error)
                    {
                        CoopPlugin.Log?.LogWarning(
                            "[identity] extended-information payload decode failed for catalog id "
                            + typeId + " (" + ExtendedInformationCodec.KeyFor(type) + "): " + error);
                    }
                }
            }

            return new ExtendedInformationValue
            {
                TypeId = typeId,
                Information = information,
            };
        }
    }
}
