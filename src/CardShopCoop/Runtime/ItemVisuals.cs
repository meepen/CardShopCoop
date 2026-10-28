using UnityEngine;

namespace CardShopCoop.Runtime
{
    /// <summary>
    /// The one place the mod materializes an <see cref="Item"/> with the game's current visual
    /// recipe. Since 1.03 the base game selects shared texture-array materials and writes the
    /// texture slot through a <c>_TexIndex</c> MaterialPropertyBlock instead of assigning a
    /// per-item material. <see cref="Item.SetMesh"/> still exists, so using it silently produced
    /// the wrong texture for every array-based item instead of failing.
    ///
    /// Every remote shelf item, bench item, register item, held pack/carry prop and mirrored
    /// bundle must go through here so it matches what the game builds locally.
    /// </summary>
    internal static class ItemVisuals
    {
        /// <summary>Fetches a pooled item, applies the type's visual recipe, and activates it at
        /// the parent's origin. Callers position it afterwards.</summary>
        internal static Item Spawn(EItemType type, Transform parent)
        {
            if (InventoryBase.GetItemMeshData(type) == null)
            {
                return null;
            }

            var item = ItemSpawnManager.GetItem(parent);
            Apply(item, type);
            item.transform.localPosition = Vector3.zero;
            item.transform.localRotation = Quaternion.identity;
            item.gameObject.SetActive(true);
            return item;
        }

        /// <summary>Applies the exact five-branch recipe the game uses in
        /// <c>ShelfCompartment.SpawnItem</c> / <c>InteractionPlayerController</c>.</summary>
        internal static void Apply(Item item, EItemType type)
        {
            var data = InventoryBase.GetItemMeshData(type);
            if (item == null || data == null)
            {
                return;
            }

            switch (data.arrayMaterialType)
            {
                case ItemMeshData.ItemShareTexture2DArrayMaterial.None:
                    item.SetMesh(data.mesh, data.material, type, data.meshSecondary,
                        data.materialSecondary, data.materialList);
                    break;
                case ItemMeshData.ItemShareTexture2DArrayMaterial.FirstMesh:
                    item.SetItemType(type);
                    item.SetSharedMeshForFirstSlot(ref data.mesh);
                    item.SetTexture2DArrayMaterialsForMesh(data.arrayMaterialType,
                        data.textureIndex, ref data.material);
                    break;
                case ItemMeshData.ItemShareTexture2DArrayMaterial.SecondMesh:
                    item.SetItemType(type);
                    item.SetSharedMeshForSecondSlot(ref data.mesh);
                    item.SetTexture2DArrayMaterialsForMesh(data.arrayMaterialType,
                        data.textureIndex, ref data.material);
                    break;
                case ItemMeshData.ItemShareTexture2DArrayMaterial.BothMeshes:
                    item.SetItemType(type);
                    item.SetSharedMeshForFirstSlot(ref data.mesh);
                    item.SetSharedMeshForSecondSlot(ref data.meshSecondary);
                    item.SetTexture2DArrayMaterialsForMesh(data.arrayMaterialType,
                        data.textureIndex, ref data.material);
                    break;
                case ItemMeshData.ItemShareTexture2DArrayMaterial.FirstMeshArraySecondDefault:
                    item.SetItemType(type);
                    item.SetSharedMeshForFirstSlot(ref data.mesh);
                    item.SetTexture2DArrayMaterialsForMesh(
                        ItemMeshData.ItemShareTexture2DArrayMaterial.FirstMesh, data.textureIndex,
                        ref data.material);
                    item.SetSharedMeshForSecondSlot(ref data.meshSecondary);
                    item.SetSharedMaterialForSecondSlot(ref data.materialSecondary);
                    break;
            }
        }

        /// <summary>
        /// The workbench's jank-box skin is a <see cref="SkinnedMeshRenderer"/>, so
        /// <c>ItemMeshData.ApplyToRenderer</c> (which takes a MeshRenderer) cannot be used.
        /// Mirrors <c>InteractableWorkbench.PlayBundlingCardBoxSequence</c>: shared material plus
        /// the array index, not a per-renderer material instance.
        /// </summary>
        internal static void ApplyJankBoxSkin(SkinnedMeshRenderer renderer, EItemType type)
        {
            if (renderer == null || type == EItemType.None)
            {
                return;
            }

            var data = InventoryBase.GetItemMeshData(type);
            if (data == null || data.material == null)
            {
                return;
            }

            if (data.arrayMaterialType == ItemMeshData.ItemShareTexture2DArrayMaterial.None)
            {
                renderer.material = data.material;
                return;
            }

            renderer.sharedMaterial = data.material;
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            block.SetFloat("_TexIndex", data.textureIndex);
            renderer.SetPropertyBlock(block);
        }
    }
}
