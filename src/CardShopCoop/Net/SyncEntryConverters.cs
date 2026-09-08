using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using CardShopCoop.Sync;

namespace CardShopCoop.Net
{
    /// <summary>Converters for the few legacy Sync DTOs whose integer type field belongs to
    /// different enum id spaces depending on the entry key/kind. Ordinary DTO enum fields use
    /// EnumWireConverter; these converters retain the old TryFromWire behavior for unresolved
    /// content-pack entries.</summary>
    internal static class SyncJson
    {
        public static JObject Start(JsonReader reader) { return JObject.Load(reader); }
        public static int Int(JObject o, string name) { return o[name] == null ? 0 : o[name].Value<int>(); }
        public static bool Bool(JObject o, string name) { return o[name] != null && o[name].Value<bool>(); }
        public static Vector3 Vector(JObject o, string name, JsonSerializer s) { return o[name] == null ? default(Vector3) : o[name].ToObject<Vector3>(s); }
        public static Quaternion ReadQuaternion(JObject o, string name, JsonSerializer s) { return o[name] == null ? UnityEngine.Quaternion.identity : o[name].ToObject<UnityEngine.Quaternion>(s); }
        public static JToken Token(JsonSerializer s, object value) { return JToken.FromObject(value, s); }
    }

    internal sealed class WorldEntryConverter : JsonConverter
    {
        public override bool CanConvert(Type t) { return t == typeof(WorldSync.Entry); }
        public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
        {
            var e = (WorldSync.Entry)value; w.WriteStartObject(); w.WritePropertyName("Key"); w.WriteValue(e.Key);
            w.WritePropertyName("Type"); w.WriteValue(Util.EnumMap.ToWire(Util.EnumKind.ItemType, e.Type));
            w.WritePropertyName("Count"); w.WriteValue(e.Count); w.WriteEndObject();
        }
        public override object ReadJson(JsonReader r, Type t, object old, JsonSerializer s)
        { var o = SyncJson.Start(r); return new WorldSync.Entry { Key = SyncJson.Int(o,"Key"), Type = Util.EnumMap.FromWire(Util.EnumKind.ItemType, SyncJson.Int(o,"Type")), Count = SyncJson.Int(o,"Count") }; }
    }

    internal sealed class PlayerStateConverter : JsonConverter
    {
        public override bool CanConvert(Type t) { return t == typeof(Messages.PlayerStateMessage); }
        public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
        {
            var m=(Messages.PlayerStateMessage)value; w.WriteStartObject();
            w.WritePropertyName("Position");s.Serialize(w,m.Position);w.WritePropertyName("Yaw");w.WriteValue(m.Yaw);w.WritePropertyName("Speed");w.WriteValue(m.Speed);w.WritePropertyName("Hold");w.WriteValue(m.Hold);
            w.WritePropertyName("HoldTypes");w.WriteStartArray();if(m.HoldTypes!=null)foreach(var id in m.HoldTypes)w.WriteValue(Util.EnumMap.ToWire(Util.EnumKind.ItemType,id));w.WriteEndArray();
            w.WritePropertyName("HoldCards");s.Serialize(w,m.HoldCards);w.WriteEndObject();
        }
        public override object ReadJson(JsonReader r, Type t, object old, JsonSerializer s)
        {
            var o=SyncJson.Start(r);var m=new Messages.PlayerStateMessage{Position=SyncJson.Vector(o,"Position",s),Yaw=o["Yaw"]==null?0:o["Yaw"].Value<float>(),Speed=o["Speed"]==null?0:o["Speed"].Value<float>(),Hold=(byte)SyncJson.Int(o,"Hold")};
            var a=o["HoldTypes"] as JArray;if(a!=null){m.HoldTypes=new System.Collections.Generic.List<int>();foreach(var id in a)m.HoldTypes.Add(Util.EnumMap.FromWire(Util.EnumKind.ItemType,id.Value<int>()));}
            m.HoldCards=o["HoldCards"] == null || o["HoldCards"].Type==JTokenType.Null ? null : o["HoldCards"].ToObject<System.Collections.Generic.List<CardData>>(s);return m;
        }
    }

    internal sealed class BoxEntryConverter : JsonConverter
    {
        public override bool CanConvert(Type t) { return t == typeof(BoxSync.Entry); }
        public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
        {
            var e=(BoxSync.Entry)value; w.WriteStartObject();
            w.WritePropertyName("Id");w.WriteValue(e.Id); w.WritePropertyName("Type");w.WriteValue(Util.EnumMap.ToWire(Util.EnumKind.ItemType,e.Type));
            w.WritePropertyName("Count");w.WriteValue(e.Count); w.WritePropertyName("IsBig");w.WriteValue(e.IsBig); w.WritePropertyName("IsOpen");w.WriteValue(e.IsOpen);
            w.WritePropertyName("Carried");w.WriteValue(e.Carried); w.WritePropertyName("Settled");w.WriteValue(e.Settled); w.WritePropertyName("Stored");w.WriteValue(e.Stored);
             w.WritePropertyName("StoreShelfId");w.WriteValue(e.StoreShelfId); w.WritePropertyName("StoreShelf");w.WriteValue(e.StoreShelf); w.WritePropertyName("StoreComp");w.WriteValue(e.StoreComp); w.WritePropertyName("Pos");s.Serialize(w,e.Pos);
            w.WritePropertyName("Yaw");w.WriteValue(e.Yaw); w.WritePropertyName("HolderWorker");w.WriteValue(e.HolderWorker); w.WritePropertyName("OwnerKind");w.WriteValue(e.OwnerKind); w.WriteEndObject();
        }
        public override object ReadJson(JsonReader r, Type t, object old, JsonSerializer s)
        {
            var o=SyncJson.Start(r); int local; bool mapped=Util.EnumMap.TryFromWire(Util.EnumKind.ItemType,SyncJson.Int(o,"Type"),out local);
             return new BoxSync.Entry { Id=(ushort)SyncJson.Int(o,"Id"), Type=local, Unmapped=!mapped, Count=SyncJson.Int(o,"Count"), IsBig=SyncJson.Bool(o,"IsBig"), IsOpen=SyncJson.Bool(o,"IsOpen"), Carried=SyncJson.Bool(o,"Carried"), Settled=SyncJson.Bool(o,"Settled"), Stored=SyncJson.Bool(o,"Stored"), StoreShelfId=(ushort)SyncJson.Int(o,"StoreShelfId"), StoreShelf=(byte)SyncJson.Int(o,"StoreShelf"), StoreComp=(byte)SyncJson.Int(o,"StoreComp"), Pos=SyncJson.Vector(o,"Pos",s), Yaw=o["Yaw"] == null ? 0 : o["Yaw"].Value<float>(), HolderWorker=(short)SyncJson.Int(o,"HolderWorker"), OwnerKind=(byte)SyncJson.Int(o,"OwnerKind") };
        }
    }

    internal sealed class ObjMoveEntryConverter : JsonConverter
    {
        public override bool CanConvert(Type t) { return t == typeof(ObjMoveSync.Entry); }
        public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
        {
            var e=(ObjMoveSync.Entry)value; var kind=e.Key>>24; var ek=kind==5?Util.EnumKind.DecoObject:Util.EnumKind.ObjectType;
            w.WriteStartObject();w.WritePropertyName("Key");w.WriteValue(e.Key);w.WritePropertyName("Type");w.WriteValue(Util.EnumMap.ToWire(ek,e.Type));w.WritePropertyName("Pos");s.Serialize(w,e.Pos);w.WritePropertyName("Rot");s.Serialize(w,e.Rot);w.WriteEndObject();
        }
        public override object ReadJson(JsonReader r, Type t, object old, JsonSerializer s)
        { var o=SyncJson.Start(r);int key=SyncJson.Int(o,"Key"),local;var ek=(key>>24)==5?Util.EnumKind.DecoObject:Util.EnumKind.ObjectType;bool ok=Util.EnumMap.TryFromWire(ek,SyncJson.Int(o,"Type"),out local);return new ObjMoveSync.Entry{Key=key,Type=local,Unresolved=!ok,Pos=SyncJson.Vector(o,"Pos",s),Rot=SyncJson.ReadQuaternion(o,"Rot",s)}; }
    }

    internal sealed class PopulationStateConverter : JsonConverter
    {
        public override bool CanConvert(Type t) { return t == typeof(Messages.PopStateMessage); }
        public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
        {
            var m=(Messages.PopStateMessage)value;w.WriteStartObject();w.WritePropertyName("Entries");w.WriteStartArray();
             for(int k=0;k<m.Entries.Count;k++){w.WriteStartArray();foreach(var e in m.Entries[k]){var ek=k==5?Util.EnumKind.DecoObject:Util.EnumKind.ObjectType;w.WriteStartObject();w.WritePropertyName("Id");w.WriteValue(e.Id);w.WritePropertyName("ObjType");w.WriteValue(Util.EnumMap.ToWire(ek,e.ObjType));w.WritePropertyName("Pos");s.Serialize(w,e.Pos);w.WritePropertyName("Rot");s.Serialize(w,e.Rot);w.WritePropertyName("IsBoxed");w.WriteValue(e.IsBoxed);w.WritePropertyName("BoxedPos");s.Serialize(w,e.BoxedPos);w.WritePropertyName("BoxedRot");s.Serialize(w,e.BoxedRot);w.WriteEndObject();}w.WriteEndArray();}w.WriteEndArray();w.WriteEndObject();
        }
        public override object ReadJson(JsonReader r, Type t, object old, JsonSerializer s)
        {
            var o=SyncJson.Start(r);var result=new Messages.PopStateMessage();var a=(JArray)o["Entries"];if(a==null)return result;
             for(int k=0;k<a.Count;k++){var list=new System.Collections.Generic.List<PopulationSync.Entry>();foreach(var token in (JArray)a[k]){var x=(JObject)token;var ek=k==5?Util.EnumKind.DecoObject:Util.EnumKind.ObjectType;int local;bool ok=Util.EnumMap.TryFromWire(ek,SyncJson.Int(x,"ObjType"),out local);list.Add(new PopulationSync.Entry{Id=(ushort)SyncJson.Int(x,"Id"),ObjType=local,Unresolved=!ok,Pos=SyncJson.Vector(x,"Pos",s),Rot=SyncJson.ReadQuaternion(x,"Rot",s),IsBoxed=SyncJson.Bool(x,"IsBoxed"),BoxedPos=SyncJson.Vector(x,"BoxedPos",s),BoxedRot=SyncJson.ReadQuaternion(x,"BoxedRot",s)});}result.Entries.Add(list);}return result;
        }
    }
}
