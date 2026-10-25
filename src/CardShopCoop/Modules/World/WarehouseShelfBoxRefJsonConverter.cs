using System;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// Wire form for a warehouse entry reference. The DTO exposes only the live shelf, compartment
    /// index, and slot hint; this converter translates between that location and the identity
    /// module's warehouse Guid. That Guid is the only identity carried on the wire and never
    /// appears as a public feature-DTO property.
    /// </summary>
    internal sealed class WarehouseShelfBoxRefJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type type) => type == typeof(WarehouseShelfBoxRef);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            MainThreadGuard.RequireMainThread("warehouse box ref serialization");
            if (value is not WarehouseShelfBoxRef reference)
            {
                writer.WriteNull();
                return;
            }

            var id = reference.Identity;
            if (reference.Shelf != null
                && GameObjectIdentityApi.TryGetId(reference.Shelf, out var shelfIdentity))
            {
                reference.ShelfIdentity = shelfIdentity;
            }
            if (id == Guid.Empty && reference.Shelf != null && reference.CompartmentIndex >= 0
                && reference.Slot >= 0)
            {
                if (!WarehouseBoxLocations.TryGetId(reference.Shelf, reference.CompartmentIndex,
                        reference.Slot, out id) || id == Guid.Empty)
                {
                    // A live box at a known slot is named by its bound identity; a stored record is
                    // minted once and then reused from the location registry.
                    id = Guid.NewGuid();
                    WarehouseBoxLocations.RegisterStored(id, reference.Shelf,
                        reference.CompartmentIndex, reference.Slot,
                        WarehouseBoxLocations.NextSequence());
                }
            }

            if (id == Guid.Empty)
            {
                id = Guid.NewGuid();
                reference.Identity = id;
                if (reference.Shelf != null && reference.CompartmentIndex >= 0
                    && reference.Slot >= 0)
                {
                    WarehouseBoxLocations.RegisterStored(id, reference.Shelf,
                        reference.CompartmentIndex, reference.Slot,
                        WarehouseBoxLocations.NextSequence());
                }
            }
            else
            {
                reference.Identity = id;
            }

            writer.WriteStartObject();
            writer.WritePropertyName("shelf");
            if (reference.Shelf != null)
            {
                serializer.Serialize(writer, reference.Shelf);
            }
            else if (reference.ShelfIdentity.IsValid)
            {
                serializer.Serialize(writer, reference.ShelfIdentity);
            }
            else
            {
                writer.WriteNull();
            }
            writer.WritePropertyName("compartment");
            writer.WriteValue(reference.CompartmentIndex);
            writer.WritePropertyName("slot");
            writer.WriteValue(reference.Slot);
            writer.WritePropertyName("id");
            writer.WriteValue(id.ToString("D"));
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            MainThreadGuard.RequireMainThread("warehouse box ref deserialization");
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            var item = JObject.Load(reader);
            var reference = new WarehouseShelfBoxRef
            {
                CompartmentIndex = ReadInt(item, "compartment", -1),
                Slot = ReadInt(item, "slot", -1),
            };

            var idToken = item["id"];
            if (idToken != null && idToken.Type != JTokenType.Null
                && Guid.TryParse(idToken.Value<string>(), out var id))
            {
                reference.Identity = id;
            }

            var shelfToken = item["shelf"];
            if (shelfToken != null && shelfToken.Type != JTokenType.Null)
            {
                var shelfIdentity = GameObjectIdJsonConverter.Parse(shelfToken as JObject);
                reference.ShelfIdentity = shelfIdentity;
                if (GameObjectIdentityApi.TryResolve(shelfIdentity, out var shelfEntity))
                {
                    reference.Shelf = shelfEntity as WarehouseShelf;
                }
            }

            // The wire location is the authoritative intent. The registry fills a missing shelf,
            // compartment, or slot when the sender named an entry whose live object is not loaded
            // here yet, and it provides the live box for a reference that resolved by identity.
            if (reference.Identity != Guid.Empty
                && WarehouseBoxLocations.TryGetLocation(reference.Identity, out var known))
            {
                if (reference.Shelf == null)
                {
                    reference.Shelf = known.Shelf;
                }

                if (reference.CompartmentIndex < 0)
                {
                    reference.CompartmentIndex = known.CompartmentIndex;
                }

                if (reference.Slot < 0)
                {
                    reference.Slot = known.Slot;
                }
            }

            if (reference.Identity == Guid.Empty)
            {
                reference.Identity = Guid.NewGuid();
                CoopPlugin.Log?.LogWarning("[warehouse] wire reference had no identity; minting "
                    + reference.Identity + " for shelf="
                    + (reference.Shelf == null ? "<unresolved>" : reference.Shelf.name)
                    + " compartment=" + reference.CompartmentIndex + " slot=" + reference.Slot + ".");
            }
            else if (!WarehouseBoxLocations.Contains(reference.Identity))
            {
                // Keep the reference; a later authoritative state may teach the registry its
                // location. The handler defers the message rather than silently dropping it.
                CoopPlugin.Log.LogWarning("[warehouse] wire reference identity "
                    + reference.Identity + " is not known locally yet (shelf="
                    + (reference.Shelf == null ? "<unresolved>" : reference.Shelf.name)
                    + " compartment=" + reference.CompartmentIndex + " slot="
                    + reference.Slot + "); the message will be deferred.");
            }

            if (reference.Shelf != null && reference.CompartmentIndex >= 0 && reference.Slot >= 0
                && !WarehouseBoxLocations.TryGetLocation(reference.Identity, out _))
            {
                WarehouseBoxLocations.RegisterStored(reference.Identity, reference.Shelf,
                    reference.CompartmentIndex, reference.Slot,
                    WarehouseBoxLocations.NextSequence());
            }

            return reference;
        }

        private static int ReadInt(JObject item, string name, int fallback)
        {
            var token = item[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<int>();
        }
    }
}
