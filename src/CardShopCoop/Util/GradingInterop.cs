using System;
using System.Reflection;
using HarmonyLib;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Reflection bridge to the <b>Grading Overhaul</b> mod (munch.gradingoverhaul), which
    /// both players run. With it installed, a graded card's <c>CardData.cardGrade</c> is NOT
    /// 1-10 - it's an ENCODED int packing (grading company + real 1-10 grade + cert serial),
    /// e.g. 380009117. Grading Overhaul exposes a public integration API for exactly this
    /// (co-op) case:
    ///  - <c>EncodedGradeRegistry.RememberForExternalMod(CardData, int)</c> burns + binds the
    ///    cert and registers the encoded grade, so its anti-cheat AddCard prefix leaves a
    ///    received graded card alone instead of re-encoding it as FAKE (the changing-grade
    ///    churn we used to drop as "corrupt").
    ///  - <c>EncodedGradeRegistry.GetEncodedOrCurrent(CardData)</c> returns the true encoded
    ///    grade even when a display transiently swapped in the bare 1-10.
    ///  - <c>Helper.GetActualGrade(int)</c> decodes an encoded grade to its 1-10 value.
    /// All accessed by reflection (Grading Overhaul is not a compile-time dependency). When
    /// it's absent, <see cref="Present"/> is false and callers keep vanilla 1-10 behavior.
    /// Namespace is TCGCardShopSimulator.GradingOverhaul (verified in the decompiled 3.4.2).
    /// </summary>
    public static class GradingInterop
    {
        /// <summary>SIMPLE ASSEMBLY NAME of Grading Overhaul, which is NOT its namespace and
        /// not a compressed form of it: the plugin ships as "Grading Overhaul.dll" and declares
        /// [assembly: AssemblyTitle("Grading Overhaul")] - space and all - while its types live
        /// under TCGCardShopSimulator.GradingOverhaul (verified in the decompiled 3.4.2). It is
        /// only the fast path; ResolveType falls back to the type walk if this is ever wrong,
        /// so a rename by GO costs a little log noise on Game Pass, never the integration.</summary>
        private const string GradingAssembly = "Grading Overhaul";

        // Assembly-qualified bind first, app-domain type walk only if it misses. This is a
        // STATIC INITIALIZER: it runs the first time anything touches GradingInterop, and with
        // the bare walk that was one guaranteed ReflectionTypeLoadException in the log of every
        // Game Pass session that ever synced a card. See Util.ModParity.ResolveType.
        private static readonly Type TReg = ModParity.ResolveType("TCGCardShopSimulator.GradingOverhaul.EncodedGradeRegistry", GradingAssembly);
        private static readonly Type THelper = ModParity.ResolveType("TCGCardShopSimulator.GradingOverhaul.Helper", GradingAssembly);

        // void RememberForExternalMod(CardData, int) - public static, decompiled :5900
        private static readonly MethodInfo MiRemember = TReg == null ? null
            : AccessTools.Method(TReg, "RememberForExternalMod", new[] { typeof(CardData), typeof(int) });
        // int GetEncodedOrCurrent(CardData) - public static, decompiled :5929
        private static readonly MethodInfo MiGetEncoded = TReg == null ? null
            : AccessTools.Method(TReg, "GetEncodedOrCurrent", new[] { typeof(CardData) });
        // int GetActualGrade(int) - public static, decompiled :15993
        private static readonly MethodInfo MiActual = THelper == null ? null
            : AccessTools.Method(THelper, "GetActualGrade", new[] { typeof(int) });

        private static bool _logged;

        /// <summary>True when Grading Overhaul is loaded and the integration API resolved.</summary>
        public static bool Present
        {
            get
            {
                if (MiRemember != null && !_logged)
                {
                    _logged = true;
                    CoopPlugin.Log.LogInfo("Grading Overhaul detected - graded cards will sync via its encoded-grade API");
                }
                return MiRemember != null;
            }
        }

        /// <summary>Register a received graded card's cert with Grading Overhaul BEFORE it's
        /// added to the album, so GO's anti-cheat prefix finds the cert burned+bound and does
        /// not re-encode it. Idempotent (re-binds the same cert). No-op for non-encoded grades
        /// or when GO is absent. Call on delta/price RECEIVE only, never in a per-frame loop.</summary>
        public static void Remember(CardData card)
        {
            if (card == null || card.cardGrade <= 10 || MiRemember == null) return;
            try { MiRemember.Invoke(null, new object[] { card, card.cardGrade }); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.Remember: " + e.Message); }
        }

        /// <summary>The true encoded grade GO holds for this card (its registry value, or the
        /// card's own cardGrade). Used on SEND so a display's transient 1-10 isn't forwarded.</summary>
        public static int Encoded(CardData card)
        {
            if (card == null) return 0;
            if (MiGetEncoded == null) return card.cardGrade;
            try { return (int)MiGetEncoded.Invoke(null, new object[] { card }); }
            catch { return card.cardGrade; }
        }

        /// <summary>Decode an encoded grade to its real 1-10 value (identity for a bare 1-10).</summary>
        public static int Actual(int encoded)
        {
            if (encoded <= 10 || MiActual == null) return encoded;
            try { return (int)MiActual.Invoke(null, new object[] { encoded }); }
            catch { return encoded; }
        }
    }
}
