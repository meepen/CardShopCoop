using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>
    /// Structured polymorphic wire form for an <see cref="EntityDescriptor"/>. The discriminator
    /// is the existing <see cref="GameObjectKind"/>; each derived descriptor then serialises only
    /// its own fields. There is no type-name string parsing and no per-field converter attributes.
    /// </summary>
    internal sealed class EntityDescriptorJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type type)
            => typeof(EntityDescriptor).IsAssignableFrom(type);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is not EntityDescriptor descriptor)
            {
                writer.WriteNull();
                return;
            }

            var contract = serializer.ContractResolver.ResolveContract(descriptor.GetType())
                as JsonObjectContract;
            if (contract == null)
            {
                throw new JsonSerializationException("No object contract for "
                    + descriptor.GetType().FullName);
            }

            writer.WriteStartObject();
            writer.WritePropertyName("kind");
            writer.WriteValue((byte)descriptor.Kind);
            for (var i = 0; i < contract.Properties.Count; i++)
            {
                var property = contract.Properties[i];
                if (property.Ignored || property.PropertyName == "kind")
                {
                    continue;
                }

                var fieldValue = property.ValueProvider.GetValue(descriptor);
                if (fieldValue == null && serializer.NullValueHandling == NullValueHandling.Ignore)
                {
                    continue;
                }

                writer.WritePropertyName(property.PropertyName);
                serializer.Serialize(writer, fieldValue);
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

            EntityDescriptor descriptor;
            if (objectType == typeof(EntityDescriptor))
            {
                var kindToken = item["kind"];
                if (kindToken == null)
                {
                    throw new JsonSerializationException(
                        "Entity descriptor has no kind discriminator.");
                }

                var kind = (GameObjectKind)kindToken.Value<byte>();
                item.Remove("kind");
                descriptor = kind switch
                {
                    GameObjectKind.NpcCustomer or GameObjectKind.NpcWorker =>
                        item.ToObject<NpcEntityDescriptor>(serializer),
                    GameObjectKind.Placement or GameObjectKind.Furniture =>
                        item.ToObject<PlacementEntityDescriptor>(serializer),
                    GameObjectKind.Decoration => item.ToObject<DecorationEntityDescriptor>(serializer),
                    GameObjectKind.PackagingBox =>
                        item.ToObject<PackagingBoxEntityDescriptor>(serializer),
                    _ => throw new JsonSerializationException(
                        "Unsupported entity descriptor kind " + kind + "."),
                };
            }
            else
            {
                // A concrete descriptor field already carries its CLR type. Populate it directly
                // so the polymorphic reader does
                // not recurse through this converter for the same object.
                if (objectType == null || objectType.IsAbstract
                    || !typeof(EntityDescriptor).IsAssignableFrom(objectType))
                {
                    throw new JsonSerializationException(
                        "Cannot read concrete entity descriptor type " + objectType + ".");
                }

                item.Remove("kind");
                descriptor = (EntityDescriptor)Activator.CreateInstance(objectType);
                using (var itemReader = item.CreateReader())
                {
                    serializer.Populate(itemReader, descriptor);
                }
            }

            if (descriptor == null || !descriptor.Id.IsValid)
            {
                throw new JsonSerializationException("Entity descriptor has an invalid identity.");
            }

            return descriptor;
        }
    }
}
