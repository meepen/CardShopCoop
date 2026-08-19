using System;
using System.Collections.Generic;
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

        // ------------------------------------------------------------------
        // JOB ENROLLMENT bridge (GradingSync.HostApplyOp).
        //
        // A submission is only a Grading Overhaul job because GO's OnPressSubmitButton
        // POSTFIX stamps it: GradingJobSubmissionRegistryPatch (decompiled :12866-12895)
        // registers the company, takes a jobId, ENCODES that into m_ServiceLevel and
        // pre-rolls the grades. That postfix is gated on
        // m_GradeCardInProgressList.Contains(m_CurrentGradeCardSubmitSet), so it can
        // never fire for a set the HOST enrolled on a guest's behalf off the wire -
        // GO's maturation then fails TryDecode, misses the reference-keyed registry and
        // the cards come back vanilla-graded. These members let GradingSync replay that
        // exact chain by reflection for a wire-born set.
        // ------------------------------------------------------------------

        private static readonly Type TTheme = ModParity.ResolveType("TCGCardShopSimulator.GradingOverhaul.GradingWebsiteThemeController", GradingAssembly);
        /// <summary>GO's GradingCompany enum (decompiled :1036 - Cardinals, Custom, PSA, Beckett).
        /// Resolved, never hard-coded: a future GO reorder must show up as a decode mismatch,
        /// not as silently mis-stamped jobs. <see cref="LogCompanyEnum"/> prints it once.</summary>
        private static readonly Type TCompany = ModParity.ResolveType("TCGCardShopSimulator.GradingOverhaul.GradingCompany", GradingAssembly);
        private static readonly Type TJobRegistry = ModParity.ResolveType("TCGCardShopSimulator.GradingOverhaul.GradingJobCompanyRegistry", GradingAssembly);
        private static readonly Type TPreRoll = ModParity.ResolveType("TCGCardShopSimulator.GradingOverhaul.PreRollManager", GradingAssembly);
        private static readonly Type TCodec = ModParity.ResolveType("TCGCardShopSimulator.GradingOverhaul.ServiceLevelCodec", GradingAssembly);
        private static readonly Type TConfig = ModParity.ResolveType("TCGCardShopSimulator.GradingOverhaul.ConfigSettings", GradingAssembly);

        // GradingCompany CurrentWebsiteCompany { get; private set; } - decompiled :4108.
        // Read RAW. GO's own recording postfixes read exactly this property with no fallback
        // (:7940, :12884); only the VALIDATION prefix (:13064) substitutes
        // ConfigSettings.ActiveCompanyProfile when the raw value is Cardinals, and copying that
        // fallback here would stamp jobs with a company the recorder never saw.
        private static readonly PropertyInfo PiCurrentCompany = TTheme == null ? null
            : AccessTools.Property(TTheme, "CurrentWebsiteCompany");
        // private static readonly GradingCompany[] AllowedCompanies - decompiled :4017.
        // The website can only ever select these three; Custom (=1) is the internal cheat skin.
        private static readonly FieldInfo FiAllowed = TTheme == null ? null
            : AccessTools.Field(TTheme, "AllowedCompanies");
        // public static ConfigEntry<bool> UseCheatsWebsite - decompiled :9585.
        private static readonly FieldInfo FiUseCheats = TConfig == null ? null
            : AccessTools.Field(TConfig, "UseCheatsWebsite");

        private static readonly MethodInfo MiOnJobSubmitted = (TJobRegistry == null || TCompany == null) ? null
            : AccessTools.Method(TJobRegistry, "OnJobSubmitted",
                new[] { typeof(GradeCardSubmitSet), TCompany, typeof(bool), TCompany });
        private static readonly MethodInfo MiNextJobId = TPreRoll == null ? null
            : AccessTools.Method(TPreRoll, "GetNextJobId", Type.EmptyTypes);
        private static readonly MethodInfo MiPreRoll = (TPreRoll == null || TCompany == null) ? null
            : AccessTools.Method(TPreRoll, "PreRollOnSubmit",
                new[] { typeof(GradeCardSubmitSet), TCompany, typeof(bool), typeof(int) });
        // int Encode(GradingCompany, int tierIndex, int jobId) - decompiled :5574. The class is
        // INTERNAL, so this is unreachable without reflection even though the method is public.
        // Overloaded (a 2-arg Encode exists at :5553), hence the explicit signature.
        private static readonly MethodInfo MiEncode = (TCodec == null || TCompany == null) ? null
            : AccessTools.Method(TCodec, "Encode", new[] { TCompany, typeof(int), typeof(int) });
        // bool TryDecode(int, out GradingCompany, out int) - decompiled :5604 (also overloaded).
        private static readonly MethodInfo MiTryDecode = (TCodec == null || TCompany == null) ? null
            : AccessTools.Method(TCodec, "TryDecode",
                new[] { typeof(int), TCompany.MakeByRefType(), typeof(int).MakeByRefType() });

        /// <summary>Sentinel written on the wire when GO is absent or the company is unreadable.
        /// Chosen outside any plausible enum ordinal so the host can reject it by the same
        /// <see cref="IsAllowedCompany"/> test it applies to everything else.</summary>
        public const int NoCompany = 255;

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
                    LogCompanyEnum();
                }
                return MiRemember != null;
            }
        }

        /// <summary>One-shot startup dump of GO's GradingCompany members and ordinals. The wire
        /// carries a company as its ORDINAL, and both peers' hosts encode/decode with it, so a
        /// future GO release that inserts or reorders a member silently re-points every stamped
        /// job. Printing the resolved table means the first field log after such a release shows
        /// the change instead of hiding it behind mis-graded cards.</summary>
        private static void LogCompanyEnum()
        {
            try
            {
                if (TCompany == null || !TCompany.IsEnum)
                {
                    CoopPlugin.Log.LogWarning("GradingInterop: GO present but GradingCompany enum did not resolve - job enrollment disabled");
                    return;
                }
                var names = Enum.GetNames(TCompany);
                var parts = new string[names.Length];
                for (int i = 0; i < names.Length; i++)
                    parts[i] = names[i] + "=" + Convert.ToInt32(Enum.Parse(TCompany, names[i]));
                CoopPlugin.Log.LogInfo("GradingInterop: GO GradingCompany = " + string.Join(", ", parts)
                    + "; allowed on the website = " + AllowedList());
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.LogCompanyEnum: " + e.Message); }
        }

        private static string AllowedList()
        {
            try
            {
                var arr = FiAllowed?.GetValue(null) as Array;
                if (arr == null) return "<unreadable>";
                var parts = new string[arr.Length];
                for (int i = 0; i < arr.Length; i++)
                {
                    object v = arr.GetValue(i);
                    parts[i] = v + "=" + Convert.ToInt32(v);
                }
                return string.Join(", ", parts);
            }
            catch { return "<unreadable>"; }
        }

        /// <summary>The grading company the local website is showing RIGHT NOW, as an ordinal,
        /// or <see cref="NoCompany"/> when GO is absent or the property will not read. Read raw
        /// (see <see cref="PiCurrentCompany"/>) because this is what GO's recording postfixes
        /// stamp a job with - the wire has to carry the same value the guest's own postfix
        /// would have recorded had it been allowed to run.</summary>
        public static int CurrentCompanyId
        {
            get
            {
                if (PiCurrentCompany == null) return NoCompany;
                try
                {
                    object v = PiCurrentCompany.GetValue(null, null);
                    return v == null ? NoCompany : Convert.ToInt32(v);
                }
                catch { return NoCompany; }
            }
        }

        /// <summary>Host-side gate on a wire-supplied company ordinal: it must be a real
        /// GradingCompany member AND one the website is actually allowed to select. The second
        /// half is the load-bearing one - Custom (ordinal 1) is a defined member but it is GO's
        /// internal cheat skin, never offered by the site, and a crafted wire must not be able to
        /// enrol a job under it.</summary>
        public static bool IsAllowedCompany(int id)
        {
            if (TCompany == null || !TCompany.IsEnum) return false;
            try
            {
                if (!Enum.IsDefined(TCompany, Enum.ToObject(TCompany, id))) return false;
                var arr = FiAllowed?.GetValue(null) as Array;
                if (arr == null)
                {
                    // Field walked away (GO rename/refactor). Keep the one rule that actually
                    // protects the shared album rather than opening the gate: everything except
                    // the cheat skin. Name-based so a reorder cannot turn this into "allow all".
                    string n = Enum.GetName(TCompany, Enum.ToObject(TCompany, id));
                    CoopPlugin.Log.LogWarning("GradingInterop: GO AllowedCompanies unreadable - falling back to name check for " + n);
                    return n != null && n != "Custom";
                }
                for (int i = 0; i < arr.Length; i++)
                    if (Convert.ToInt32(arr.GetValue(i)) == id) return true;
                return false;
            }
            catch { return false; }
        }

        /// <summary>Display name of a company ordinal, for the enrollment log line.</summary>
        public static string CompanyName(int id)
        {
            if (TCompany == null || !TCompany.IsEnum) return id.ToString();
            try { return Enum.GetName(TCompany, Enum.ToObject(TCompany, id)) ?? id.ToString(); }
            catch { return id.ToString(); }
        }

        /// <summary>This machine's Grading Overhaul "Enable Probability System (FAKE CARDS)"
        /// setting (ConfigSettings.UseCheatsWebsite, decompiled :9585/:9739). Defaults to false
        /// on any failure to read.
        ///
        /// The HOST's value is deliberately the one GradingSync uses for a guest's submission,
        /// even though GO's postfix would have read the SUBMITTER's. Grading results land in the
        /// shared album that every peer mirrors, and useCheats=true makes GO grade the job with
        /// the Custom slider odds and stamp the cards as FAKE. Taking the flag off the wire would
        /// let a guest push fake cards into the host's save with the host's own config saying no.
        /// The cost of host-authority is the reverse case: a guest with cheats on submitting to a
        /// clean host gets honest grades - which is the failure direction to prefer.</summary>
        public static bool HostUseCheatsWebsite
        {
            get
            {
                try
                {
                    var entry = FiUseCheats?.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
                    return entry != null && entry.Value;
                }
                catch { return false; }
            }
        }

        /// <summary>True when every reflection member the enrollment chain needs resolved.
        /// Checked before the chain runs so a partial resolve leaves the raw tier alone
        /// instead of half-stamping a job.</summary>
        public static bool CanEnroll
        {
            get
            {
                return TCompany != null && TCompany.IsEnum
                    && MiOnJobSubmitted != null && MiNextJobId != null
                    && MiPreRoll != null && MiTryDecode != null;
            }
        }

        /// <summary>Step (b) of GO's chain: GradingJobCompanyRegistry.OnJobSubmitted (decompiled
        /// :10209). embeddedCompany is passed as the same company, exactly as GO does at :12886.</summary>
        public static bool RegisterJobCompany(GradeCardSubmitSet set, int companyId, bool useCheats)
        {
            if (set == null || MiOnJobSubmitted == null) return false;
            try
            {
                object company = Enum.ToObject(TCompany, companyId);
                MiOnJobSubmitted.Invoke(null, new object[] { set, company, useCheats, company });
                return true;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.RegisterJobCompany: " + e.Message); return false; }
        }

        /// <summary>Step (c): PreRollManager.GetNextJobId (decompiled :5366). 0 on failure - GO
        /// treats jobId &lt;= 0 as "no pre-roll" at maturation (:8022), so 0 is a safe sentinel.</summary>
        public static int NextJobId()
        {
            if (MiNextJobId == null) return 0;
            try { return Convert.ToInt32(MiNextJobId.Invoke(null, null)); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.NextJobId: " + e.Message); return 0; }
        }

        /// <summary>Step (d): ServiceLevelCodec.Encode(company, tier, jobId) (decompiled :5574).
        /// Returns 0 when the internal method could not be invoked, so the caller can fall back
        /// to the literal layout. NEVER trust the result without <see cref="TryDecodeServiceLevel"/>.</summary>
        public static int EncodeServiceLevel(int companyId, int tier, int jobId)
        {
            if (MiEncode == null) return 0;
            try { return Convert.ToInt32(MiEncode.Invoke(null, new object[] { Enum.ToObject(TCompany, companyId), tier, jobId })); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.EncodeServiceLevel: " + e.Message); return 0; }
        }

        /// <summary>ServiceLevelCodec.TryDecode(raw, out company, out tier) (decompiled :5604) -
        /// the same call GO's maturation makes first (:8007). Used to VERIFY an encode before it
        /// is written into a live set.</summary>
        public static bool TryDecodeServiceLevel(int raw, out int companyId, out int tier)
        {
            companyId = -1;
            tier = raw;
            if (MiTryDecode == null) return false;
            try
            {
                var args = new object[] { raw, Enum.ToObject(TCompany, 0), 0 };
                bool ok = (bool)MiTryDecode.Invoke(null, args);
                if (!ok) return false;
                companyId = Convert.ToInt32(args[1]);
                tier = Convert.ToInt32(args[2]);
                return true;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.TryDecodeServiceLevel: " + e.Message); return false; }
        }

        /// <summary>Step (e): PreRollManager.PreRollOnSubmit (decompiled :5371). Rolls each card's
        /// grade and burns its cert NOW, so maturation just applies the stored result.</summary>
        public static bool PreRollJob(GradeCardSubmitSet set, int companyId, bool useCheats, int jobId)
        {
            if (set == null || MiPreRoll == null) return false;
            try
            {
                MiPreRoll.Invoke(null, new object[] { set, Enum.ToObject(TCompany, companyId), useCheats, jobId });
                return true;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.PreRollJob: " + e.Message); return false; }
        }

        /// <summary>Lowest m_ServiceLevel value that is a GO-ENCODED level rather than a vanilla
        /// tier index (ServiceLevelCodec V1_MAGIC, decompiled :5543). Anything at or above this
        /// must not be fed to vanilla's GetGradeCardServiceData for real meaning.</summary>
        public const int EncodedLevelFloor = 10000;

        // ------------------------------------------------------------------
        // SUBMIT SLOT COUNT (GradingSync.ClientSubmit / HostApplyOp).
        //
        // Vanilla GradedCardSubmitSelectScreen.OnPressSubmitButton rebuilds the scratch set
        // with EIGHT empty slots (decompiled GradedCardSubmitSelectScreen.cs :193), and the
        // submit screen only ever shows eight panels. Grading Overhaul changes both halves:
        // GradingSlotExpansionPatches builds up to 52 panels (:1421, :1459) and TRANSPILES
        // OnPressSubmitButton, rewriting that ldc.i4.8 to ldc.i4.s 52
        // (PatchSubmitSlotCount, decompiled-grading Grading Overhaul.decompiled.cs
        // :1498-1515). So with GO a guest really can have up to 52 cards sitting in
        // m_CurrentGradeCardSubmitSet when he presses submit.
        //
        // The named source is GradingSlotExpansionPatches.MAX_SLOTS = 52 (:1338). It is a
        // private const, so C# inlines it at every use site - but a const still exists in
        // metadata as a literal field, which is exactly what GetRawConstantValue reads.
        // Resolve it so a GO release that raises the cap is picked up on its own, and fall
        // back to the literal 52 read off that decompile if the field ever walks away.
        // ------------------------------------------------------------------

        private static readonly Type TSlotExpansion = ModParity.ResolveType("TCGCardShopSimulator.GradingOverhaul.GradingSlotExpansionPatches", GradingAssembly);

        /// <summary>Vanilla's submit-set slot count. Also the shape every vanilla
        /// GradeCardSubmitSet carries, so it stays the pad target for un-enrolled sets.</summary>
        public const int VanillaSubmitSlots = 8;

        /// <summary>Value of GO 3.4.2's GradingSlotExpansionPatches.MAX_SLOTS (:1338), used
        /// only when the const cannot be read back off the loaded assembly.</summary>
        private const int GoSubmitSlotsFallback = 52;

        private static int _maxSubmitSlots; // resolved once, 0 = not yet resolved

        /// <summary>How many cards one submission may legitimately carry on this machine:
        /// 8 without Grading Overhaul, GO's MAX_SLOTS (52) with it. Bounded to 255 because
        /// the wire carries the card count as a byte.</summary>
        public static int MaxSubmitSlots
        {
            get
            {
                if (!Present) return VanillaSubmitSlots;
                if (_maxSubmitSlots > 0) return _maxSubmitSlots;
                int v = GoSubmitSlotsFallback;
                string how = "GO 3.4.2 decompile literal (MAX_SLOTS unreadable)";
                try
                {
                    var fi = TSlotExpansion == null ? null : AccessTools.Field(TSlotExpansion, "MAX_SLOTS");
                    if (fi != null && fi.IsLiteral && fi.FieldType == typeof(int))
                    {
                        int raw = Convert.ToInt32(fi.GetRawConstantValue());
                        // Never below vanilla (a smaller cap would start dropping cards the
                        // vanilla screen can hold) and never past the byte the wire carries.
                        if (raw >= VanillaSubmitSlots && raw <= 255) { v = raw; how = "GradingSlotExpansionPatches.MAX_SLOTS"; }
                        else CoopPlugin.Log.LogWarning($"GradingInterop: GO MAX_SLOTS = {raw} is out of the usable 8..255 range - using {v}");
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.MaxSubmitSlots: " + e.Message); }
                _maxSubmitSlots = v;
                CoopPlugin.Log.LogInfo($"GradingInterop: grading submit slot cap = {v} (from {how})");
                return v;
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

        // ------------------------------------------------------------------
        // CERT IDENTITY + THE CERT-EXISTENCE UNION (CoopCore's graded-album digest).
        //
        // The encoded grade IS the album key (CPlayerData.RemoveGradedCard matches on
        // cardSaveIndex + amount == cardGrade + expansionType + isDestiny, :1552-1571) and it
        // packs the grading company and the cert serial, so the cert is the card's identity.
        // gradedCardIndex is NOT: AddCard sets it to m_GradedCardInventoryList.Count + 1
        // (:1509), which stops being unique the moment any row is removed.
        // ------------------------------------------------------------------

        // internal static void DecodeGradeFull(int, out GradingCompany, out int grade, out int cert)
        // - decompiled-grading :15953. The PUBLIC DecodeGrade wrapper right above it (:15948)
        // deliberately zeroes the cert on the way out, so it is useless here; the internal one is
        // reachable by reflection all the same.
        private static readonly MethodInfo MiDecodeFull = (THelper == null || TCompany == null) ? null
            : AccessTools.Method(THelper, "DecodeGradeFull",
                new[] { typeof(int), TCompany.MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType() });
        // public static bool IsCheatFlagged(int) - decompiled-grading :16005 (encoded >= 1e9).
        private static readonly MethodInfo MiIsCheat = THelper == null ? null
            : AccessTools.Method(THelper, "IsCheatFlagged", new[] { typeof(int) });

        /// <summary>Split an encoded grade into its grading company ordinal and cert serial.
        /// False (and cert 0) for a bare 1-10 grade, when Grading Overhaul is absent, or when the
        /// decode member could not be resolved - callers must then simply skip cert reasoning
        /// rather than guess, since a fabricated cert would produce false collision reports.</summary>
        public static bool DecodeCert(int encoded, out int companyId, out int cert)
        {
            companyId = -1;
            cert = 0;
            if (encoded <= 10 || MiDecodeFull == null) return false;
            try
            {
                var args = new object[] { encoded, Enum.ToObject(TCompany, 0), 0, 0 };
                MiDecodeFull.Invoke(null, args);
                companyId = Convert.ToInt32(args[1]);
                cert = Convert.ToInt32(args[3]);
                return cert > 0;
            }
            catch { return false; }
        }

        /// <summary>(company, cert) packed exactly the way GO's own BuildExistingCertSet packs it
        /// (decompiled-grading :8801-8880) - a serial is only unique WITHIN a company. 0 means
        /// "no usable cert", never a valid key.</summary>
        public static long CertKey(int encoded)
        {
            int company, cert;
            if (!DecodeCert(encoded, out company, out cert)) return 0L;
            return ((long)company << 32) | (uint)cert;
        }

        /// <summary>True when GO has stamped this grade with its FAKE encoding (+1,000,000,000).
        /// Worth naming in a divergence report: a FAKE-flagged row on one side and a clean cert
        /// on the other is the duplicate-cert sweep having fired, not a missing card.</summary>
        public static bool CheatFlagged(int encoded)
        {
            if (MiIsCheat == null) return false;
            try { return (bool)MiIsCheat.Invoke(null, new object[] { encoded }); }
            catch { return false; }
        }

        /// <summary>One graded card as the digest identifies it. Everything
        /// CPlayerData.GetCardSaveIndex reads (monsterType, borderType, isFoil - :795-811) plus
        /// the expansion, isDestiny and the encoded grade. Border and foil are NOT decoration:
        /// they are half of cardSaveIndex, so a digest without them both under-reports (two
        /// border variants of one monster collapse into one key) and, worse, would make the adopt
        /// path file a borrowed card as border 0 / non-foil - a different card.</summary>
        public struct GradedEntry
        {
            public ECardExpansionType Expansion;
            public EMonsterType Monster;
            public ECardBorderType Border;
            public bool IsFoil;
            public bool IsDestiny;
            public int Encoded;

            /// <summary>Local bookkeeping only - NEVER written to or read from the wire (see
            /// CoopCore.WriteGradedDigest, which writes the six identity fields and nothing else).
            /// Set by CoopCore.GradedAdopt on a candidate it refused, so the entry can stay in the
            /// per-peer diff for the report while being excluded from the offer count.</summary>
            public bool Refused;

            /// <summary>Set-membership key for the diff. String rather than a packed hash for the
            /// same reason CoopCore.CardPriceKey is one: it is also what the log lines print.</summary>
            public string Key
            {
                get
                {
                    return (int)Expansion + ":" + (int)Monster + ":" + (int)Border
                        + ":" + (IsFoil ? 1 : 0) + (IsDestiny ? 1 : 0) + ":" + Encoded;
                }
            }

            public CardData ToCard()
            {
                return new CardData
                {
                    expansionType = Expansion,
                    monsterType = Monster,
                    borderType = Border,
                    isFoil = IsFoil,
                    isDestiny = IsDestiny,
                    isChampionCard = false,
                    isNew = false,
                    cardGrade = Encoded,
                    gradedCardIndex = 0, // never an identity - see the block comment above
                };
            }
        }

        /// <summary>Every place a graded cert can legitimately live on THIS machine, deduped.
        ///
        /// THIS IS A DELIBERATE SUPERSET OF GRADING OVERHAUL'S OWN BuildExistingCertSet
        /// (decompiled-grading :8801-8880, which reads only m_GradedCardInventoryList,
        /// m_HoldCardDataList, m_CardShelfSaveDataList and m_PackageBoxCardSaveDataList), and the
        /// asymmetry is the whole reason the extra containers are listed here. The two consumers
        /// fail in OPPOSITE directions:
        ///  - GO misses a container and merely UNDER-FLAGS: a cert it cannot see is a cert its
        ///    duplicate sweep does not fire on. Nothing is created; a cheat slips by.
        ///  - We miss a container and the card looks ABSENT from this PC, so the digest reports it
        ///    as peer-only, the F2 panel FABRICATES an adopt offer for a card that is already here,
        ///    and pressing it calls AddCard - which is exactly the duplicate-cert input that makes
        ///    GO rewrite BOTH rows to the FAKE encoding. A miss here MANUFACTURES the corruption.
        /// So this list must be at least as wide as GO's and is allowed to be wider. Never trim it
        /// back toward GO's set "for parity" - parity is the bug.
        ///
        /// Beyond GO's four: m_GradeCardInProgressList (jobs at the grader),
        /// m_CurrentGradeCardSubmitSet (cards STAGED on the submit screen but not yet submitted -
        /// a player can sit in that state indefinitely, or back out of it, and every second there
        /// is a second the album does not list the card), and the four storage containers whose
        /// save data holds cards directly: card storage shelves, card/item combi shelves, bulk
        /// donation boxes and auto pack openers. An ALBUM-ONLY digest reports a false "missing" for
        /// every graded card the other player happens to be holding, has put on a display shelf,
        /// has sitting in a graded-returns box, has staged for grading, or has filed in any of
        /// those containers. Those are exactly the states a session spends seconds at a time in.
        ///
        /// Only encoded grades (&gt; 10) are collected: a bare 1-10 vanilla grade carries no cert,
        /// so it has no stable identity to diff and every one of them would "collide" on cert 0.
        /// With Grading Overhaul absent this therefore returns an empty list, which self-gates the
        /// whole diagnostic off rather than needing a Present check (and Present has a one-shot
        /// logging side effect that does not belong on a 45s timer).</summary>
        public static List<GradedEntry> BuildGradedCertInventory()
        {
            var list = new List<GradedEntry>();
            var seen = new HashSet<string>();
            try
            {
                AddAllCompact(list, seen, CPlayerData.m_GradedCardInventoryList);
                AddAll(list, seen, CPlayerData.m_HoldCardDataList);
                var shelves = CPlayerData.m_CardShelfSaveDataList;
                if (shelves != null)
                    for (int i = 0; i < shelves.Count; i++)
                        AddAll(list, seen, shelves[i]?.cardDataList);
                var boxes = CPlayerData.m_PackageBoxCardSaveDataList;
                if (boxes != null)
                    for (int i = 0; i < boxes.Count; i++)
                        AddAll(list, seen, boxes[i]?.cardDataList);
                var inProgress = CPlayerData.m_GradeCardInProgressList;
                if (inProgress != null)
                    for (int i = 0; i < inProgress.Count; i++)
                        AddAll(list, seen, inProgress[i]?.m_CardDataList);
                // The stage-then-abandon state: cards sitting in the submit screen's scratch set.
                AddAll(list, seen, CPlayerData.m_CurrentGradeCardSubmitSet?.m_CardDataList);
                var storage = CPlayerData.m_CardStorageShelfSaveDataList;
                if (storage != null)
                    for (int i = 0; i < storage.Count; i++)
                        AddAllCompact(list, seen, storage[i]?.compactCardDataAmountList);
                var combi = CPlayerData.m_CardItemCombiShelfSaveDataList;
                if (combi != null)
                    for (int i = 0; i < combi.Count; i++)
                        AddAll(list, seen, combi[i]?.cardDataList);
                var donation = CPlayerData.m_BulkDonationSaveDataList;
                if (donation != null)
                    for (int i = 0; i < donation.Count; i++)
                        AddAllCompact(list, seen, donation[i]?.compactCardDataAmountList);
                var openers = CPlayerData.m_AutoPackOpenerSaveDataList;
                if (openers != null)
                    for (int i = 0; i < openers.Count; i++)
                        AddAllCompact(list, seen, openers[i]?.compactCardDataAmountList);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("GradingInterop.BuildGradedCertInventory: " + e.Message);
            }
            return list;
        }

        private static void AddAll(List<GradedEntry> list, HashSet<string> seen, List<CardData> cards)
        {
            if (cards == null) return;
            for (int i = 0; i < cards.Count; i++) Add(list, seen, cards[i]);
        }

        /// <summary>Compact (save-shaped) rows, where the encoded grade rides in <c>amount</c>.
        /// Per-ROW try/catch, never per-list: GetGradedCardData divides by
        /// GetCardAmountPerMonsterType, which is 0 for an expansion this install has no data for,
        /// and one such row must not silently truncate the rest of a container's contents - a
        /// truncated container is precisely the miss that fabricates an adopt offer.</summary>
        private static void AddAllCompact(List<GradedEntry> list, HashSet<string> seen,
            List<CompactCardDataAmount> rows)
        {
            if (rows == null) return;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || row.amount <= 10) continue;
                try { Add(list, seen, CPlayerData.GetGradedCardData(row)); }
                catch { }
            }
        }

        private static void Add(List<GradedEntry> list, HashSet<string> seen, CardData c)
        {
            if (c == null || c.cardGrade <= 10) return;
            var e = new GradedEntry
            {
                Expansion = c.expansionType,
                Monster = c.monsterType,
                Border = c.borderType,
                IsFoil = c.isFoil,
                IsDestiny = c.isDestiny,
                Encoded = c.cardGrade,
            };
            // Deduped because this is an EXISTENCE set, not a count. The duplicate rows GO's
            // anti-cheat sweep leaves behind would otherwise inflate one side's total and make a
            // pure set difference read as a count difference.
            if (seen.Add(e.Key)) list.Add(e);
        }
    }
}
