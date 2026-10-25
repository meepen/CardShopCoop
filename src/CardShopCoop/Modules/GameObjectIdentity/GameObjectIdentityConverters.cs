using System;
using CardShopCoop.Runtime;
using Newtonsoft.Json;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>Global converter for live entity fields. The cache owns object to id and id to
    /// object; this converter only serializes the structured GameObjectId.</summary>
    internal sealed class GameObjectEntityJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type type)
            => type == typeof(Customer) || type == typeof(Worker)
                || typeof(InteractableObject).IsAssignableFrom(type);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            MainThreadGuard.RequireMainThread("game object identity serialization");
            if (value == null || !GameObjectIdentityCache.TryGetId(value, out var id))
            {
                writer.WriteNull();
                return;
            }

            serializer.Serialize(writer, id);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            MainThreadGuard.RequireMainThread("game object identity deserialization");
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            var decoded = serializer.Deserialize(reader, typeof(GameObjectId));
            if (decoded is not GameObjectId id || !id.IsValid
                || !GameObjectIdentityCache.TryResolve(id, out var entity))
            {
                return null;
            }

            if (objectType.IsInstanceOfType(entity))
            {
                return entity;
            }

            // A furniture identity resolves canonically to the placed object. A field typed as
            // a packaging box wants that furniture's current package, which is a view of the
            // same identity.
            if (entity is InteractableObject furniture
                && typeof(InteractablePackagingBox).IsAssignableFrom(objectType))
            {
                var box = furniture.GetPackagingBoxShelf();
                if (box != null && objectType.IsInstanceOfType(box))
                {
                    return box;
                }
            }

            return null;
        }
    }
}
