using System;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// Wire form for one compartment header. The shelf travels as its structured identity so a
    /// guest that has not materialized the shelf yet can defer the state and resolve it later,
    /// instead of losing the reference when the live object is temporarily unavailable.
    /// </summary>
    internal sealed class WarehouseCompartmentStateJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type type) => type == typeof(WarehouseCompartmentState);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            MainThreadGuard.RequireMainThread("warehouse compartment serialization");
            if (value is not WarehouseCompartmentState state)
            {
                writer.WriteNull();
                return;
            }

            var shelfIdentity = state.ShelfIdentity;
            if (state.Shelf != null
                && GameObjectIdentityApi.TryGetId(state.Shelf, out var liveIdentity))
            {
                shelfIdentity = liveIdentity;
                state.ShelfIdentity = liveIdentity;
            }

            writer.WriteStartObject();
            writer.WritePropertyName("shelf");
            if (shelfIdentity.IsValid)
            {
                serializer.Serialize(writer, shelfIdentity);
            }
            else
            {
                writer.WriteNull();
            }

            writer.WritePropertyName("compartment");
            writer.WriteValue(state.CompartmentIndex);
            writer.WritePropertyName("itemType");
            serializer.Serialize(writer, state.ItemType);
            writer.WritePropertyName("isBig");
            writer.WriteValue(state.IsBig);
            writer.WritePropertyName("boxes");
            serializer.Serialize(writer, state.Boxes);
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            MainThreadGuard.RequireMainThread("warehouse compartment deserialization");
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            var item = JObject.Load(reader);
            var state = new WarehouseCompartmentState
            {
                CompartmentIndex = ReadInt(item, "compartment", -1),
                IsBig = item["isBig"]?.Value<bool>() ?? false,
            };

            var shelfToken = item["shelf"];
            if (shelfToken != null && shelfToken.Type != JTokenType.Null)
            {
                var shelfIdentity = GameObjectIdJsonConverter.Parse(shelfToken as JObject);
                state.ShelfIdentity = shelfIdentity;
                if (GameObjectIdentityApi.TryResolve(shelfIdentity, out var shelfEntity))
                {
                    state.Shelf = shelfEntity as WarehouseShelf;
                }
            }

            var itemTypeToken = item["itemType"];
            if (itemTypeToken != null && itemTypeToken.Type != JTokenType.Null)
            {
                state.ItemType = itemTypeToken.ToObject<EItemType>(serializer);
            }

            var boxesToken = item["boxes"];
            if (boxesToken != null && boxesToken.Type != JTokenType.Null)
            {
                state.Boxes = boxesToken.ToObject<System.Collections.Generic.List<WarehouseBoxEntry>>(
                    serializer) ?? new System.Collections.Generic.List<WarehouseBoxEntry>();
            }

            return state;
        }

        private static int ReadInt(JObject item, string name, int fallback)
        {
            var token = item[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<int>();
        }
    }
}
