using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Api;

namespace CardShopCoop.PurchaseOverhaul
{
    /// <summary>
    /// Reflection boundary to Purchase Overhaul. The mod is an optional dependency and the
    /// assemblies are never compiled against, so every member is resolved here and a missing or
    /// changed member disables the bridge instead of failing the plugin.
    /// </summary>
    internal static class PurchaseOverhaulInterop
    {
        private const string AssemblyName = "PurchaseOverhaul";
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic;

        private static readonly Type CheckoutType = ResolveType("PurchaseOverhaul.Checkout");
        private static readonly Type CartType = ResolveType("PurchaseOverhaul.Cart");
        private static readonly Type CartLineType = CartType?.GetNestedType("Line",
            BindingFlags.Public);
        private static readonly Type ProductType = ResolveType("PurchaseOverhaul.Product");
        private static readonly Type CatalogueType = ResolveType("PurchaseOverhaul.Catalogue");
        private static readonly Type CoopBridgeType = ResolveType("PurchaseOverhaul.CoopBridge");
        private static readonly Type ToastType = ResolveType("PurchaseOverhaul.Toast");

        private static readonly PropertyInfo CartLines = CartType?.GetProperty("Lines",
            StaticFlags);
        private static readonly MethodInfo CartSet = ResolveMethod(CartType, "Set", typeof(int),
            ProductType, typeof(int));
        private static readonly MethodInfo CartBoxes = ResolveMethod(CartType, "Boxes",
            typeof(int), ProductType);
        private static readonly MethodInfo ValidateMethod = ResolveMethod(CheckoutType,
            "Validate", typeof(string));
        private static readonly MethodInfo PlaceRemoteMethod = ResolvePlaceRemote();
        private static readonly MethodInfo BuyLicenseMethod = ResolveMethod(CheckoutType,
            "BuyLicense", typeof(bool), ProductType, typeof(string).MakeByRefType());
        private static readonly PropertyInfo CoopBridgeLive = CoopBridgeType?.GetProperty("Live",
            StaticFlags);
        private static readonly MethodInfo ProductId = ResolveMethod(CoopBridgeType, "IdOf",
            typeof(string), ProductType);
        private static readonly MethodInfo FindProduct = ResolveMethod(CoopBridgeType, "Find",
            ProductType, typeof(string));
        private static readonly MethodInfo ToastShow = ResolveMethod(ToastType, "Show",
            typeof(void), typeof(string));
        private static readonly MethodInfo CatalogueRefresh = ResolveMethod(CatalogueType,
            "Refresh", typeof(void));
        private static readonly FieldInfo ProductName = ProductType?.GetField("Name",
            BindingFlags.Instance | BindingFlags.Public);

        private static bool _probed;
        private static bool _available;

        internal static bool Available
        {
            get
            {
                Probe();
                return _available;
            }
        }

        internal static Type CheckoutSurface => CheckoutType;

        internal static Type ProductSurface => ProductType;

        /// <summary>True unless Purchase Overhaul's own QOLCoop bridge is carrying the order.
        /// Two co-op controllers must never both submit the same order.</summary>
        internal static bool ShouldRoute
        {
            get
            {
                if (!Available)
                {
                    return false;
                }

                try
                {
                    return !(CoopBridgeLive?.GetValue(null) is bool live && live);
                }
                catch (Exception error)
                {
                    CoopLog.Warn("Purchase Overhaul co-op state could not be read: "
                        + error.Message);
                    return false;
                }
            }
        }

        internal static bool ReadOrder(out List<string> productIds, out List<int> boxes,
            out string message)
        {
            productIds = new List<string>();
            boxes = new List<int>();

            var validation = ValidateMethod?.Invoke(null, null) as string;
            if (!string.IsNullOrEmpty(validation))
            {
                message = validation;
                return false;
            }

            var source = CartLines?.GetValue(null) as IEnumerable;
            if (source == null)
            {
                message = "Purchase Overhaul's cart is unavailable.";
                return false;
            }

            foreach (var line in source)
            {
                var product = GetField(line, "Product");
                var count = GetInt(line, "Boxes");
                if (product == null || count <= 0)
                {
                    continue;
                }

                var id = ProductId?.Invoke(null, new[] { product }) as string;
                if (string.IsNullOrEmpty(id))
                {
                    message = NameOf(product) + " cannot be ordered in co-op.";
                    return false;
                }

                productIds.Add(id);
                boxes.Add(count);
            }

            if (productIds.Count == 0)
            {
                message = "Nothing in the cart.";
                return false;
            }

            message = null;
            return true;
        }

        internal static bool TryPlaceOrder(List<string> productIds, List<int> boxes,
            out string message)
        {
            message = null;
            if (productIds == null || boxes == null || productIds.Count != boxes.Count
                || productIds.Count == 0 || productIds.Count > 64)
            {
                message = "The order is invalid.";
                return false;
            }

            var lineType = CartLineType;
            var placeRemote = PlaceRemoteMethod;
            if (lineType == null || placeRemote == null)
            {
                message = "Purchase Overhaul's host order path is unavailable.";
                return false;
            }

            var listType = typeof(List<>).MakeGenericType(lineType);
            var lines = (IList)Activator.CreateInstance(listType);
            for (var i = 0; i < productIds.Count; i++)
            {
                var count = Math.Max(1, Math.Min(5000, boxes[i]));
                var product = FindProduct?.Invoke(null, new object[] { productIds[i] });
                if (product == null)
                {
                    message = "An item in the order is unknown to the host.";
                    return false;
                }

                var line = Activator.CreateInstance(lineType);
                SetField(line, "Product", product);
                SetField(line, "Boxes", count);
                lines.Add(line);
            }

            var args = new object[] { lines, null };
            try
            {
                var placed = PlaceRemoteMethod.Invoke(null, args) is bool value && value;
                message = args[1] as string;
                return placed;
            }
            catch (Exception error)
            {
                message = "The host could not complete the order: " + error.Message;
                return false;
            }
        }

        internal static bool TryBuyLicense(string productId, out string message)
        {
            message = null;
            var buy = BuyLicenseMethod;
            if (buy == null || string.IsNullOrEmpty(productId))
            {
                message = "Purchase Overhaul's licence path is unavailable.";
                return false;
            }

            var product = FindProduct?.Invoke(null, new object[] { productId });
            if (product == null)
            {
                message = "The licence is unknown to the host.";
                return false;
            }

            var args = new object[] { product, null };
            try
            {
                var bought = buy.Invoke(null, args) is bool value && value;
                message = args[1] as string;
                return bought;
            }
            catch (Exception error)
            {
                message = "The host could not complete the licence purchase: " + error.Message;
                return false;
            }
        }

        internal static void RemoveOrderedLines(IReadOnlyList<string> productIds,
            IReadOnlyList<int> boxes)
        {
            if (productIds == null || boxes == null || CartBoxes == null || CartSet == null)
            {
                return;
            }

            for (var i = 0; i < productIds.Count && i < boxes.Count; i++)
            {
                var product = FindProduct?.Invoke(null, new object[] { productIds[i] });
                if (product == null)
                {
                    continue;
                }

                var current = CartBoxes.Invoke(null, new[] { product }) is int value ? value : 0;
                var remaining = Math.Max(0, current - boxes[i]);
                CartSet.Invoke(null, new object[] { product, remaining });
            }
        }

        internal static string NameOf(object product)
            => ProductName?.GetValue(product) as string ?? "An item";

        internal static string ProductIdOf(object product)
            => ProductId?.Invoke(null, new[] { product }) as string;

        internal static void ShowToast(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                ToastShow?.Invoke(null, new object[] { text });
            }
            catch (Exception error)
            {
                CoopLog.Warn("Purchase Overhaul toast failed: " + error.Message);
            }
        }

        internal static void RefreshCatalogue()
        {
            try
            {
                CatalogueRefresh?.Invoke(null, null);
            }
            catch (Exception error)
            {
                CoopLog.Warn("Purchase Overhaul catalogue refresh failed: " + error.Message);
            }
        }

        private static void Probe()
        {
            if (_probed)
            {
                return;
            }

            _probed = true;
            if (CheckoutType == null)
            {
                return;
            }

            _available = CheckoutType != null && CartType != null && CartLineType != null
                && ProductType != null && ValidateMethod != null && PlaceRemoteMethod != null
                && BuyLicenseMethod != null && CartLines != null && CartSet != null
                && CartBoxes != null && ProductId != null && FindProduct != null;
            if (!_available)
            {
                CoopLog.Warn("Purchase Overhaul is installed but its order surface could not be "
                    + "read; its co-op bridge is disabled.");
            }
        }

        private static Type ResolveType(string typeName)
        {
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name == AssemblyName)
                    {
                        return assembly.GetType(typeName, false);
                    }
                }
            }
            catch (Exception error)
            {
                CoopLog.Warn("Purchase Overhaul type probe failed for " + typeName + ": "
                    + error.Message);
            }

            return null;
        }

        private static MethodInfo ResolveMethod(Type type, string name, Type returnType,
            params Type[] parameters)
        {
            if (type == null)
            {
                return null;
            }

            var method = type.GetMethod(name, StaticFlags, null, parameters, null);
            return method != null && method.ReturnType == returnType ? method : null;
        }

        private static MethodInfo ResolvePlaceRemote()
        {
            if (CheckoutType == null || CartLineType == null)
            {
                return null;
            }

            var listType = typeof(List<>).MakeGenericType(CartLineType);
            return ResolveMethod(CheckoutType, "PlaceRemote", typeof(bool), listType,
                typeof(string).MakeByRefType());
        }

        private static object GetField(object instance, string name)
            => instance?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(instance);

        private static int GetInt(object instance, string name)
            => GetField(instance, name) is int value ? value : 0;

        private static void SetField(object instance, string name, object value)
            => instance?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)
                ?.SetValue(instance, value);
    }
}
