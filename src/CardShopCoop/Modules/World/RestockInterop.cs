using System.Collections.Generic;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// Scene-only access to the live restock manager's packaging-box collections.
    ///
    /// The game's own getters (<c>RestockManager.GetItemPackagingBoxList</c> and friends) tunnel
    /// through <c>CSingleton&lt;RestockManager&gt;.Instance</c>. That getter fabricates an empty
    /// <c>(singleton)RestockManager</c> and marks it DontDestroyOnLoad the moment it is touched
    /// while no scene instance is resolvable - which is exactly the case during a scene load. The
    /// empty copy then shadows the real manager for the rest of the run: every serialized field on
    /// it is null, so its box prefabs and spawn locations are null and every delivery spawn throws
    /// while the real scene manager's own Update throws on the queue the copy consumed. No restock
    /// box can appear for any player after that, including a guest's accepted order.
    ///
    /// Resolve through <see cref="SceneRef{T}"/> instead. It never fabricates a manager and returns
    /// null while the scene has not produced one yet, which the callers already treat as "nothing to
    /// read" (they all null-check the list they iterate).
    /// </summary>
    internal static class RestockInterop
    {
        internal static RestockManager Manager => SceneRef<RestockManager>.Get();

        internal static List<InteractablePackagingBox_Item> ItemBoxes
            => Manager?.m_ItemPackagingBoxList;

        internal static List<InteractablePackagingBox_Card> CardBoxes
            => Manager?.m_CardPackagingBoxList;

        internal static List<InteractablePackagingBox_Shelf> ShelfBoxes
            => Manager?.m_ShelfPackagingBoxList;
    }
}
