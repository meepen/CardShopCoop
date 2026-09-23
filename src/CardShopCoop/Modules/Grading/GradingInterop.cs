using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using CardShopCoop.Util;

namespace CardShopCoop.Modules.Grading
{
    /// <summary>
    /// The complete reflection boundary for Grading Overhaul. The optional assembly is never a
    /// compile-time reference, and every partial resolve fails closed at the operation that needs
    /// it. This class deliberately has no dependency on World's card implementation; card sources
    /// and mutations arrive through <see cref="IGradingWorldBridge"/>.
    /// </summary>
    internal static class GradingInterop
    {
        internal const string GradingAssembly = "Grading Overhaul";
        internal const int NoCompany = 255;
        internal const int VanillaSubmitSlots = 8;
        private const int GoSubmitSlotsFallback = 52;

        private static readonly Type TRegistry = ReflectionSurface.OptionalType(
            "TCGCardShopSimulator.GradingOverhaul.EncodedGradeRegistry", GradingAssembly);
        private static readonly Type THelper = ReflectionSurface.OptionalType(
            "TCGCardShopSimulator.GradingOverhaul.Helper", GradingAssembly);
        private static readonly Type TTheme = ReflectionSurface.OptionalType(
            "TCGCardShopSimulator.GradingOverhaul.GradingWebsiteThemeController", GradingAssembly);
        private static readonly Type TCompany = ReflectionSurface.OptionalType(
            "TCGCardShopSimulator.GradingOverhaul.GradingCompany", GradingAssembly);
        private static readonly Type TJobRegistry = ReflectionSurface.OptionalType(
            "TCGCardShopSimulator.GradingOverhaul.GradingJobCompanyRegistry", GradingAssembly);
        private static readonly Type TPreRoll = ReflectionSurface.OptionalType(
            "TCGCardShopSimulator.GradingOverhaul.PreRollManager", GradingAssembly);
        private static readonly Type TCodec = ReflectionSurface.OptionalType(
            "TCGCardShopSimulator.GradingOverhaul.ServiceLevelCodec", GradingAssembly);
        private static readonly Type TConfig = ReflectionSurface.OptionalType(
            "TCGCardShopSimulator.GradingOverhaul.ConfigSettings", GradingAssembly);
        private static readonly Type TSlotExpansion = ReflectionSurface.OptionalType(
            "TCGCardShopSimulator.GradingOverhaul.GradingSlotExpansionPatches", GradingAssembly);
        private static readonly Type TSaveManager = ReflectionSurface.OptionalType(
            "TCGCardShopSimulator.GradingOverhaul.GradingOverhaulSaveManager", GradingAssembly);
        private static readonly Type TDayStartPatch = ModParity.ResolveType(
            "TCGCardShopSimulator.GradingOverhaul.CompanyStamp_RestockManager_OnDayStartedPatch",
            GradingAssembly);
        private static readonly Type TSubmitValidatorPatch = ModParity.ResolveType(
            "TCGCardShopSimulator.GradingOverhaul.GradingSubmit_CompanyValidation_Patch",
            GradingAssembly);

        private static readonly MethodInfo MiRemember = TRegistry == null ? null
            : ResolveExactMethod(TRegistry, "RememberForExternalMod", typeof(CardData), typeof(int));
        private static readonly MethodInfo MiEncoded = TRegistry == null ? null
            : ReflectionSurface.OptionalMethod(TRegistry, "GetEncodedOrCurrent", typeof(CardData));
        private static readonly MethodInfo MiActual = THelper == null ? null
            : ReflectionSurface.OptionalMethod(THelper, "GetActualGrade", typeof(int));
        private static readonly MethodInfo MiDecodeFull = THelper == null || TCompany == null ? null
            : ReflectionSurface.OptionalMethod(THelper, "DecodeGradeFull", typeof(int),
                TCompany.MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType());
        private static readonly MethodInfo MiCheat = THelper == null ? null
            : ReflectionSurface.OptionalMethod(THelper, "IsCheatFlagged", typeof(int));

        private static readonly PropertyInfo PiCurrentCompany = TTheme == null ? null
            : ReflectionSurface.OptionalProperty(TTheme, "CurrentWebsiteCompany");
        private static readonly FieldInfo FiAllowedCompanies = TTheme == null ? null
            : ReflectionSurface.OptionalField(TTheme, "AllowedCompanies");
        private static readonly FieldInfo FiUseCheats = TConfig == null ? null
            : ReflectionSurface.OptionalField(TConfig, "UseCheatsWebsite");

        private static readonly MethodInfo MiOnJobSubmitted = TJobRegistry == null || TCompany == null ? null
            : ResolveExactMethod(TJobRegistry, "OnJobSubmitted", typeof(GradeCardSubmitSet),
                TCompany, typeof(bool), TCompany);
        private static readonly MethodInfo MiNextJobId = TPreRoll == null ? null
            : ResolveExactMethod(TPreRoll, "GetNextJobId");
        private static readonly MethodInfo MiPreRoll = TPreRoll == null || TCompany == null ? null
            : ResolveExactMethod(TPreRoll, "PreRollOnSubmit", typeof(GradeCardSubmitSet),
                TCompany, typeof(bool), typeof(int));
        private static readonly MethodInfo MiEncode = TCodec == null || TCompany == null ? null
            : ReflectionSurface.OptionalMethod(TCodec, "Encode", TCompany, typeof(int), typeof(int));
        private static readonly MethodInfo MiTryDecode = TCodec == null || TCompany == null ? null
            : ReflectionSurface.OptionalMethod(TCodec, "TryDecode", typeof(int),
                TCompany.MakeByRefType(), typeof(int).MakeByRefType());
        private static readonly MethodInfo MiHasBinding = TSaveManager == null || TCompany == null ? null
            : ReflectionSurface.OptionalMethod(TSaveManager, "HasCertBinding", TCompany, typeof(int));
        private static readonly MethodInfo MiIsBoundTo = TSaveManager == null || TCompany == null ? null
            : ReflectionSurface.OptionalMethod(TSaveManager, "IsCertBoundToCard", TCompany, typeof(int),
                typeof(CardData));
        // GO is optional, but submission is irreversible once these two hooks run. Do not search
        // by name or infer parameter order: an apparently similar method can leave a certificate
        // registry or pre-roll entry behind when a later economy step fails. These are the only
        // signatures this integration is allowed to invoke.
        private static readonly MethodInfo MiRollbackJobCompany = TJobRegistry == null
            || TCompany == null ? null : ResolveExactMethod(TJobRegistry,
                "OnJobCancelled", typeof(GradeCardSubmitSet), TCompany, typeof(bool), typeof(int));
        private static readonly MethodInfo MiRollbackPreRoll = TPreRoll == null || TCompany == null
            ? null : ResolveExactMethod(TPreRoll, "RollbackOnSubmit",
                typeof(GradeCardSubmitSet), TCompany, typeof(bool), typeof(int));
        private static readonly MethodInfo MiDayStart = TDayStartPatch == null ? null
            : ResolveExactMethod(TDayStartPatch, "Prefix");
        private static readonly MethodInfo MiSubmitValidator = TSubmitValidatorPatch == null ? null
            : ResolveExactMethod(TSubmitValidatorPatch, "Prefix",
                typeof(GradedCardSubmitSelectScreen));

        private static bool _logged;
        private static bool _compatibilityLogged;
        private static bool _dayStartSurfaceChecked;
        private static bool _dayStartSurfaceReady;
        private static int _maxSubmitSlots;
        private static readonly HashSet<long> RefusalWarnings = new();

        internal static bool Present
        {
            get
            {
                var compatible = GoCompatible;
                if (GoDetected && !_logged)
                {
                    _logged = true;
                    if (compatible)
                    {
                        CoopPlugin.Log.LogInfo("Grading Overhaul detected - the grading module will preserve encoded certificates");
                        LogCompanies();
                    }
                }

                return GoDetected;
            }
        }

        internal static bool GoCompatible
        {
            get
            {
                var failure = CompatibilityFailure();
                if (failure != null)
                    LogCompatibilityFailure(failure);
                return GoDetected && failure == null;
            }
        }

        private static bool GoDetected
            => TRegistry != null || THelper != null || TCompany != null || TJobRegistry != null
                || TPreRoll != null || TTheme != null || TCodec != null || TConfig != null
                || TSlotExpansion != null || TSaveManager != null || TDayStartPatch != null
                || TSubmitValidatorPatch != null;

        private static string CompatibilityFailure()
        {
            if (!GoDetected)
                return null;

            var failures = new List<string>();
            if (!IsExactMethod(MiRemember, TRegistry, true, typeof(bool), typeof(CardData),
                typeof(int)))
            {
                failures.Add("RememberForExternalMod must be declared by EncodedGradeRegistry, "
                    + "static, (CardData, int), and return bool");
            }
            if (TCompany == null || !TCompany.IsEnum)
                failures.Add("GradingCompany must be an enum");
            if (!IsExactMethod(MiEncoded, TRegistry, true, typeof(int), typeof(CardData)))
                failures.Add("GetEncodedOrCurrent must be declared by EncodedGradeRegistry, static, "
                    + "(CardData), and return int");
            if (!IsExactMethod(MiActual, THelper, true, typeof(int), typeof(int)))
                failures.Add("GetActualGrade must be declared by Helper, static, (int), and return int");
            if (!IsExactMethod(MiDecodeFull, THelper, true, typeof(void), typeof(int),
                TCompany?.MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType()))
            {
                failures.Add("DecodeGradeFull must have the exact encoded/company/grade/certificate signature");
            }
            if (!IsExactMethod(MiCheat, THelper, true, typeof(bool), typeof(int)))
                failures.Add("IsCheatFlagged must be declared by Helper, static, (int), and return bool");
            if (!IsExactMethod(MiHasBinding, TSaveManager, true, typeof(bool), TCompany, typeof(int)))
                failures.Add("HasCertBinding must have the exact company/certificate signature");
            if (!IsExactMethod(MiIsBoundTo, TSaveManager, true, typeof(bool), TCompany, typeof(int),
                typeof(CardData)))
            {
                failures.Add("IsCertBoundToCard must have the exact company/certificate/card signature");
            }
            if (!IsExactMethod(MiDayStart, TDayStartPatch, true, typeof(bool)))
            {
                failures.Add("day-start Prefix must be declared by "
                    + "CompanyStamp_RestockManager_OnDayStartedPatch, static, (), and return bool");
            }
            else if (_dayStartSurfaceChecked && !_dayStartSurfaceReady)
            {
                failures.Add("the exact day-start maturation guard could not be installed");
            }
            if (!IsExactMethod(MiSubmitValidator, TSubmitValidatorPatch, true, typeof(bool),
                typeof(GradedCardSubmitSelectScreen)))
            {
                failures.Add("submit validator Prefix must be declared by "
                    + "GradingSubmit_CompanyValidation_Patch, static, "
                    + "(GradedCardSubmitSelectScreen), and return bool");
            }
            if (!IsExactMethod(MiOnJobSubmitted, TJobRegistry, true, typeof(void),
                typeof(GradeCardSubmitSet), TCompany, typeof(bool), TCompany))
            {
                failures.Add("OnJobSubmitted apply method has an unexpected declaring type, "
                    + "static/instance mode, parameter list, or return type");
            }
            if (!IsExactMethod(MiPreRoll, TPreRoll, true, typeof(void),
                typeof(GradeCardSubmitSet), TCompany, typeof(bool), typeof(int)))
            {
                failures.Add("PreRollOnSubmit apply method has an unexpected declaring type, "
                    + "static/instance mode, parameter list, or return type");
            }
            if (!IsExactMethod(MiRollbackJobCompany, TJobRegistry, true, typeof(bool),
                typeof(GradeCardSubmitSet), TCompany, typeof(bool), typeof(int)))
            {
                failures.Add("OnJobCancelled rollback method has an unexpected declaring type, "
                    + "static/instance mode, parameter list, or return type");
            }
            if (!IsExactMethod(MiRollbackPreRoll, TPreRoll, true, typeof(bool),
                typeof(GradeCardSubmitSet), TCompany, typeof(bool), typeof(int)))
            {
                failures.Add("RollbackOnSubmit rollback method has an unexpected declaring type, "
                    + "static/instance mode, parameter list, or return type");
            }
            if (!IsExactMethod(MiNextJobId, TPreRoll, true, typeof(int)))
                failures.Add("GetNextJobId must be declared by PreRollManager, static, (), and return int");
            if (!IsExactMethod(MiEncode, TCodec, true, typeof(int), TCompany, typeof(int), typeof(int)))
                failures.Add("ServiceLevelCodec.Encode has an unexpected exact signature");
            if (!IsExactMethod(MiTryDecode, TCodec, true, typeof(bool), typeof(int),
                TCompany?.MakeByRefType(), typeof(int).MakeByRefType()))
            {
                failures.Add("ServiceLevelCodec.TryDecode has an unexpected exact signature");
            }

            return failures.Count == 0 ? null : string.Join("; ", failures);
        }

        private static void LogCompatibilityFailure(string failure)
        {
            if (_compatibilityLogged || CoopPlugin.Log == null)
                return;

            _compatibilityLogged = true;
            CoopPlugin.Log.LogError("Grading Overhaul grading disabled fail-closed: " + failure);
        }

        private static MethodInfo ResolveExactMethod(Type declaringType, string name,
            params Type[] parameterTypes)
        {
            if (declaringType == null)
                return null;

            return declaringType.GetMethod(name, BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic, null, parameterTypes, null);
        }

        private static bool IsExactMethod(MethodInfo method, Type declaringType, bool isStatic,
            Type returnType, params Type[] parameterTypes)
        {
            if (method == null || method.DeclaringType != declaringType
                || method.IsStatic != isStatic || method.ReturnType != returnType)
            {
                return false;
            }

            var parameters = method.GetParameters();
            if (parameters.Length != parameterTypes.Length)
                return false;

            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType != parameterTypes[i])
                    return false;
            }

            return true;
        }

        internal static int CurrentCompanyId
        {
            get
            {
                if (PiCurrentCompany == null)
                    return NoCompany;

                try
                {
                    var value = PiCurrentCompany.GetValue(null, null);
                    return value == null ? NoCompany : Convert.ToInt32(value);
                }
                catch (Exception error)
                {
                    Swallow.Log(error);
                    return NoCompany;
                }
            }
        }

        internal static bool HostUseCheatsWebsite
        {
            get
            {
                try
                {
                    var entry = FiUseCheats?.GetValue(null) as ConfigEntry<bool>;
                    return entry != null && entry.Value;
                }
                catch (Exception error)
                {
                    Swallow.Log(error);
                    return false;
                }
            }
        }

        internal static MethodInfo GoDayStartMethod => MiDayStart;

        internal static bool GoDayStartMethodMatches
            => IsExactMethod(MiDayStart, TDayStartPatch, true, typeof(bool));

        internal static MethodInfo GoSubmitValidatorMethod => MiSubmitValidator;

        internal static void SetDayStartSurface(bool ready, string reason)
        {
            _dayStartSurfaceChecked = true;
            _dayStartSurfaceReady = ready;
            if (!ready && GoDetected)
            {
                CoopPlugin.Log.LogError("Grading Overhaul grading disabled fail-closed: "
                    + (reason ?? "the exact day-start maturation guard is unavailable"));
            }
        }

        internal static int MaxSubmitSlots
        {
            get
            {
                if (!Present)
                    return VanillaSubmitSlots;
                if (_maxSubmitSlots > 0)
                    return _maxSubmitSlots;

                var value = GoSubmitSlotsFallback;
                try
                {
                    var field = TSlotExpansion == null ? null
                        : ReflectionSurface.OptionalField(TSlotExpansion, "MAX_SLOTS");
                    if (field != null && field.IsLiteral && field.FieldType == typeof(int))
                    {
                        var raw = Convert.ToInt32(field.GetRawConstantValue());
                        if (raw >= VanillaSubmitSlots && raw <= byte.MaxValue)
                            value = raw;
                        else
                            CoopPlugin.Log.LogWarning("GradingInterop: GO submit slot count is outside the wire-safe range; using " + value);
                    }
                }
                catch (Exception error)
                {
                    CoopPlugin.Log.LogWarning("GradingInterop: could not read GO submit slot count - " + error.Message);
                }

                _maxSubmitSlots = value;
                CoopPlugin.Log.LogInfo("GradingInterop: grading submit slot cap = " + value);
                return value;
            }
        }

        internal static int Encoded(CardData card)
        {
            if (card == null)
                return 0;
            if (GoDetected && !GoCompatible)
                return card.cardGrade;
            if (MiEncoded == null)
                return card.cardGrade;

            try
            {
                return Convert.ToInt32(MiEncoded.Invoke(null, new object[] { card }));
            }
            catch (Exception error)
            {
                Swallow.Log(error);
                return card.cardGrade;
            }
        }

        internal static int Actual(int encoded)
        {
            if (encoded <= 10 || (GoDetected && !GoCompatible) || MiActual == null)
                return encoded;

            try
            {
                return Convert.ToInt32(MiActual.Invoke(null, new object[] { encoded }));
            }
            catch (Exception error)
            {
                Swallow.Log(error);
                return encoded;
            }
        }

        internal static bool Remember(CardData card)
        {
            if (!GoCompatible || card == null || MiRemember == null)
                return false;

            var encoded = Encoded(card);
            if (encoded <= 10 || !CertFreeForCard(card, encoded))
                return false;

            try
            {
                var result = MiRemember.Invoke(null, new object[] { card, encoded });
                return result is bool success && success;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("GradingInterop.Remember: " + error.Message);
                return false;
            }
        }

        internal static bool IsAllowedCompany(int id)
        {
            if (TCompany == null || !TCompany.IsEnum)
                return false;

            try
            {
                var company = Enum.ToObject(TCompany, id);
                if (!Enum.IsDefined(TCompany, company))
                    return false;

                var allowed = FiAllowedCompanies?.GetValue(null) as Array;
                if (allowed == null)
                {
                    var name = Enum.GetName(TCompany, company);
                    return name != null && name != "Custom";
                }

                for (var i = 0; i < allowed.Length; i++)
                {
                    if (Convert.ToInt32(allowed.GetValue(i)) == id)
                        return true;
                }

                return false;
            }
            catch (Exception error)
            {
                Swallow.Log(error);
                return false;
            }
        }

        internal static bool ValidSubmissionCard(CardData card)
        {
            if (card == null || card.expansionType == ECardExpansionType.None
                || card.monsterType == EMonsterType.None
                || !Enum.IsDefined(typeof(ECardBorderType), card.borderType)
                || card.isChampionCard || card.cardGrade < 0
                || card.gradedCardIndex < 0 || card.gradedCardIndex > 1000000)
            {
                return false;
            }

            try
            {
                var encoded = Encoded(card);
                if (encoded < 0 || (encoded > 10 && !Present)
                    || (encoded > 10 && (Actual(encoded) < 1 || Actual(encoded) > 10)))
                    return false;
                if (encoded > 10 && !DecodeCert(encoded, out _, out _))
                    return false;
                if (encoded == 0 && card.gradedCardIndex != 0)
                    return false;

                var index = CPlayerData.GetCardSaveIndex(card);
                var collected = CPlayerData.GetCardCollectedList(card.expansionType, card.isDestiny);
                if (index < 0 || collected == null || index >= collected.Count)
                    return false;
                var canonical = CPlayerData.GetCardData(index, card.expansionType, card.isDestiny);
                return canonical != null && canonical.expansionType == card.expansionType
                    && canonical.monsterType == card.monsterType
                    && canonical.borderType == card.borderType
                    && canonical.isFoil == card.isFoil
                    && canonical.isDestiny == card.isDestiny
                    && canonical.isChampionCard == card.isChampionCard;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("grading submission card validation failed: " + error.Message);
                return false;
            }
        }

        internal static bool RegisterJobCompany(GradeCardSubmitSet set, int companyId, bool useCheats)
        {
            if (set == null || MiOnJobSubmitted == null || TCompany == null)
                return false;

            try
            {
                var company = Enum.ToObject(TCompany, companyId);
                MiOnJobSubmitted.Invoke(null, new object[] { set, company, useCheats, company });
                return true;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("GradingInterop.RegisterJobCompany: " + error.Message);
                return false;
            }
        }

        internal static int NextJobId()
        {
            if (MiNextJobId == null)
                return 0;
            try
            {
                return Convert.ToInt32(MiNextJobId.Invoke(null, null));
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("GradingInterop.NextJobId: " + error.Message);
                return 0;
            }
        }

        internal static int EncodeServiceLevel(int companyId, int tier, int jobId)
        {
            if (MiEncode == null || TCompany == null)
                return 0;
            try
            {
                return Convert.ToInt32(MiEncode.Invoke(null,
                    new object[] { Enum.ToObject(TCompany, companyId), tier, jobId }));
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("GradingInterop.EncodeServiceLevel: " + error.Message);
                return 0;
            }
        }

        internal static bool TryDecodeServiceLevel(int raw, out int companyId, out int tier)
        {
            companyId = -1;
            tier = raw;
            if (MiTryDecode == null || TCompany == null)
                return false;

            try
            {
                var args = new object[] { raw, Enum.ToObject(TCompany, 0), 0 };
                if (!(bool)MiTryDecode.Invoke(null, args))
                    return false;
                companyId = Convert.ToInt32(args[1]);
                tier = Convert.ToInt32(args[2]);
                return companyId >= 0 && Enum.IsDefined(TCompany, companyId)
                    && tier >= 0 && tier <= byte.MaxValue;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("GradingInterop.TryDecodeServiceLevel: " + error.Message);
                return false;
            }
        }

        internal static bool PreRollJob(GradeCardSubmitSet set, int companyId, bool useCheats, int jobId)
        {
            if (set == null || MiPreRoll == null || TCompany == null)
                return false;
            try
            {
                MiPreRoll.Invoke(null, new object[] { set, Enum.ToObject(TCompany, companyId), useCheats, jobId });
                return true;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("GradingInterop.PreRollJob: " + error.Message);
                return false;
            }
        }

        internal static bool RollbackJobCompany(GradeCardSubmitSet set, int companyId,
            bool useCheats, int jobId)
            => InvokeRollback(MiRollbackJobCompany, set, companyId, useCheats, jobId,
                "job enrollment");

        internal static bool RollbackPreRoll(GradeCardSubmitSet set, int companyId,
            bool useCheats, int jobId)
            => InvokeRollback(MiRollbackPreRoll, set, companyId, useCheats, jobId,
                "job pre-roll");

        internal static bool DecodeCert(int encoded, out int companyId, out int cert)
        {
            companyId = -1;
            cert = 0;
            if (encoded <= 10 || !GoCompatible || MiDecodeFull == null || TCompany == null)
                return false;

            try
            {
                var args = new object[] { encoded, Enum.ToObject(TCompany, 0), 0, 0 };
                MiDecodeFull.Invoke(null, args);
                companyId = Convert.ToInt32(args[1]);
                var actualGrade = Convert.ToInt32(args[2]);
                cert = Convert.ToInt32(args[3]);
                return Enum.IsDefined(TCompany, companyId) && actualGrade >= 1
                    && actualGrade <= 10 && cert > 0;
            }
            catch (Exception error)
            {
                Swallow.Log(error);
                return false;
            }
        }

        internal static bool CheatFlagged(int encoded)
        {
            if (MiCheat == null)
                return false;
            try
            {
                return (bool)MiCheat.Invoke(null, new object[] { encoded });
            }
            catch (Exception error)
            {
                Swallow.Log(error);
                return false;
            }
        }

        internal static void Reset()
        {
            _maxSubmitSlots = 0;
            RefusalWarnings.Clear();
            _dayStartSurfaceChecked = false;
            _dayStartSurfaceReady = false;
        }

        private static bool CertFreeForCard(CardData card, int encoded)
        {
            if (CheatFlagged(encoded))
                return true;
            if (MiHasBinding == null || MiIsBoundTo == null || TCompany == null)
                return false;
            if (!DecodeCert(encoded, out var companyId, out var cert))
                return false;

            try
            {
                var company = Enum.ToObject(TCompany, companyId);
                if (!(bool)MiHasBinding.Invoke(null, new object[] { company, cert })
                    || (bool)MiIsBoundTo.Invoke(null, new object[] { company, cert, card }))
                    return true;
            }
            catch (Exception error)
            {
                Swallow.Log(error);
                return false;
            }

            var key = ((long)companyId << 32) | (uint)cert;
            if (!RefusalWarnings.Add(key))
            {
                CoopPlugin.Log.LogWarning("graded cert refused again: " + cert + " (company " + companyId + ")");
                return false;
            }

            CoopPlugin.Log.LogWarning("graded cert refused: certificate " + cert + " (company " + companyId
                + ") is already bound to a different card on this PC; the incoming card is not registered");
            return false;
        }

        private static bool InvokeRollback(MethodInfo method, GradeCardSubmitSet set,
            int companyId, bool useCheats, int jobId, string operation)
        {
            if (method == null || method.ReturnType != typeof(bool) || set == null || TCompany == null)
                return false;
            try
            {
                var result = method.Invoke(null, new object[]
                {
                    set, Enum.ToObject(TCompany, companyId), useCheats, jobId,
                });
                return result is bool success && success;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("GradingInterop rollback " + operation + " failed: "
                    + error.Message);
                return false;
            }
        }

        private static void LogCompanies()
        {
            try
            {
                if (TCompany == null || !TCompany.IsEnum)
                    return;
                var names = Enum.GetNames(TCompany);
                var values = new string[names.Length];
                for (var i = 0; i < names.Length; i++)
                    values[i] = names[i] + "=" + Convert.ToInt32(Enum.Parse(TCompany, names[i]));
                CoopPlugin.Log.LogInfo("GradingInterop: GO GradingCompany = " + string.Join(", ", values));
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("GradingInterop: could not enumerate GO companies - " + error.Message);
            }
        }
    }
}
