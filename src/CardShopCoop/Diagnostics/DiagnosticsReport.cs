using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Modules.Shop;
using CardShopCoop.Net;
using CardShopCoop.Net.Networking;
using CardShopCoop.Util;
using Newtonsoft.Json;
using UnityEngine;

namespace CardShopCoop.Diagnostics
{
    /// <summary>
    /// Builds the player-facing support report. The report is copied as text so Discord inserts it
    /// into the rich-text field; the separate Copy Log File action puts the real log on the
    /// clipboard as an attachment. Discord cannot accept text and a file in the same paste, so the
    /// two actions are deliberately separate.
    /// </summary>
    internal static class DiagnosticsReport
    {
        internal static bool TryCopyText(CoopCore core, out string message)
        {
            string report;
            var logPath = FindLogFile();
            try
            {
                report = Build(core, logPath);
            }
            catch (Exception error)
            {
                message = "Could not build diagnostics: " + error.Message;
                CoopPlugin.Log?.LogError("diagnostics report failed to build: " + error);
                return false;
            }

            try
            {
                ClipboardWriter.SetTextAndFiles(report, null);
                message = "Diagnostics copied as text. Paste into Discord, then use Copy Log File.";
                CoopPlugin.Log?.LogInfo("diagnostics text copied: " + report.Length + " characters");
                return true;
            }
            catch (Exception error)
            {
                try
                {
                    GUIUtility.systemCopyBuffer = report;
                    message = "Diagnostics copied as text. Paste into Discord, then use Copy Log File.";
                    CoopPlugin.Log?.LogWarning("diagnostics native text copy failed, used Unity clipboard: "
                        + error.Message);
                    return true;
                }
                catch (Exception fallbackError)
                {
                    message = "Could not copy diagnostics: " + fallbackError.Message;
                    CoopPlugin.Log?.LogError("diagnostics clipboard fallback failed: " + fallbackError);
                    return false;
                }
            }
        }

        internal static bool TryCopyLog(out string message)
        {
            var logPath = FindLogFile();
            if (logPath == null)
            {
                message = "No log file was found.";
                return false;
            }

            try
            {
                ClipboardWriter.SetTextAndFiles(null, new[] { logPath });
                message = "Raw log file copied: " + Path.GetFileName(logPath)
                    + ". It can contain player names and addresses; paste it only where appropriate.";
                CoopPlugin.Log?.LogInfo("diagnostics raw log file copied: "
                    + Path.GetFileName(logPath));
                return true;
            }
            catch (Exception error)
            {
                message = "Could not copy the log file: " + error.Message;
                CoopPlugin.Log?.LogError("diagnostics log file copy failed: " + error);
                return false;
            }
        }

        /// <summary>Copies one paste that carries both the Markdown report and this process's
        /// per-instance log as file attachments.</summary>
        internal static bool TryCopyBundle(out string message)
        {
            var core = CoopCore.Instance;
            if (core == null)
            {
                message = "Co-op is not running.";
                return false;
            }

            CleanupOldDiagnosticsFiles();

            var logPath = FindLogFile();
            string redactedLogPath = null;
            if (!string.IsNullOrEmpty(logPath))
            {
                try
                {
                    redactedLogPath = WriteRedactedLog(logPath, core);
                }
                catch (Exception error)
                {
                    // Never fall back to the raw log: it contains player names and endpoints.
                    CoopPlugin.Log?.LogWarning("diagnostics log redaction failed; the bundle will "
                        + "contain the report only: " + error.Message);
                }
            }

            string reportPath;
            try
            {
                var report = Build(core, redactedLogPath);
                reportPath = Path.Combine(Path.GetTempPath(),
                    "CardShopCoop_Diagnostics_"
                    + System.Diagnostics.Process.GetCurrentProcess().Id + ".md");
                File.WriteAllText(reportPath, report, new UTF8Encoding(false));
            }
            catch (Exception error)
            {
                message = "Could not build diagnostics: " + error.Message;
                CoopPlugin.Log?.LogError("diagnostics bundle failed to build: " + error);
                return false;
            }

            var files = new List<string> { reportPath };
            if (!string.IsNullOrEmpty(redactedLogPath))
            {
                files.Add(redactedLogPath);
            }

            try
            {
                ClipboardWriter.SetTextAndFiles(null, files);
                message = "Diagnostics copied (redacted): " + Path.GetFileName(reportPath)
                    + (string.IsNullOrEmpty(redactedLogPath)
                        ? " (log file not found or could not be redacted)."
                        : " and " + Path.GetFileName(redactedLogPath) + ".");
                CoopPlugin.Log?.LogInfo("diagnostics bundle copied: " + files.Count + " files");
                return true;
            }
            catch (Exception error)
            {
                message = "Could not copy diagnostics: " + error.Message;
                CoopPlugin.Log?.LogError("diagnostics bundle copy failed: " + error);
                return false;
            }
        }

        private static string FindLogFile()
        {
            try
            {
                FileLog.FlushNow();
                var path = FileLog.Path;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    return path;
                }

                if (string.IsNullOrEmpty(Paths.BepInExRootPath))
                {
                    return null;
                }

                var fallback = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
                return File.Exists(fallback) ? fallback : null;
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogWarning("diagnostics could not locate the log file: "
                    + error.Message);
                return null;
            }
        }

        private const string DiagnosticsFilePrefix = "CardShopCoop_Diagnostics_";

        /// <summary>Removes reports from earlier runs. The current report/log must stay on disk
        /// while it is on the clipboard, so cleanup is deferred to the next bundle action.</summary>
        private static void CleanupOldDiagnosticsFiles()
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-1);
                var files = Directory.GetFiles(Path.GetTempPath(), DiagnosticsFilePrefix + "*");
                for (var i = 0; i < files.Length; i++)
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(files[i]) < cutoff)
                        {
                            File.Delete(files[i]);
                        }
                    }
                    catch (Exception error)
                    {
                        CoopPlugin.Log?.LogWarning("diagnostics could not remove an old temp file: "
                            + error.Message);
                    }
                }
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogWarning("diagnostics temp cleanup failed: " + error.Message);
            }
        }

        /// <summary>Writes the log copy attached to a support bundle with player names, shop/lobby
        /// names, user paths, addresses and Steam ids removed. The raw log is never attached to a
        /// bundle; it remains available through the explicit Copy Log File action.</summary>
        private static string WriteRedactedLog(string logPath, CoopCore core)
        {
            var text = File.ReadAllText(logPath);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddRedactionName(names, core?.EffectivePlayerName);
            var players = PresenceApi.SessionPlayers;
            for (var i = 0; i < players.Count; i++)
            {
                AddRedactionName(names, players[i].Name);
            }
            if (ShopApi.TryGetShopName(out var shopName))
            {
                AddRedactionName(names, shopName);
            }

            var hostBackends = ActiveBackends(core, hosts: true);
            for (var i = 0; i < hostBackends.Count; i++)
            {
                var fields = DiagnosticFields(hostBackends[i], ConnectMode.Host);
                for (var j = 0; j < fields.Count; j++)
                {
                    var field = fields[j];
                    if (field != null && field.Key != null
                        && field.Key.IndexOf("lobby name", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        AddRedactionName(names, field.Value);
                    }
                }
            }

            var ordered = new List<string>(names);
            ordered.Sort((left, right) => right.Length.CompareTo(left.Length));
            for (var i = 0; i < ordered.Count; i++)
            {
                text = Regex.Replace(text, Regex.Escape(ordered[i]), "[redacted]",
                    RegexOptions.IgnoreCase);
            }

            text = Regex.Replace(text, @"(?i)([A-Z]:\\Users\\)[^\\\s]+", "$1[user]");
            text = Regex.Replace(text, @"\b\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?\b", "[address]");
            text = Regex.Replace(text, @"\b7656119\d{10}\b", "[steamid]");

            var path = Path.Combine(Path.GetTempPath(), DiagnosticsFilePrefix
                + System.Diagnostics.Process.GetCurrentProcess().Id + ".log");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        private static void AddRedactionName(HashSet<string> names, string name)
        {
            name = name == null ? null : name.Trim();
            if (string.IsNullOrEmpty(name) || name.Length < 3)
            {
                return;
            }
            if (name.Equals("Shop", StringComparison.OrdinalIgnoreCase)
                || name.Equals("My Shop", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Client", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Player", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            names.Add(name);
        }

        internal static string Build(CoopCore core) => Build(core, FindLogFile());

        private static string Build(CoopCore core, string logPath)
        {
            var text = new StringBuilder(128 * 1024);
            AppendHeader(text, core);

            var hostBackends = ActiveBackends(core, hosts: true);
            if (hostBackends.Count > 0)
            {
                AppendHostingDetails(text, core, hostBackends);
            }

            var clientBackends = ActiveBackends(core, hosts: false);
            if (clientBackends.Count > 0)
            {
                AppendClientDetails(text, core, clientBackends);
            }

            if (core.InSave)
            {
                AppendGameInformation(text);
            }

            AppendLogReference(text, logPath);
            return text.ToString();
        }

        private static void AppendHeader(StringBuilder text, CoopCore core)
        {
            text.Append("# CardShopCoop ").Append(CoopPlugin.Version).Append("\n\n");
            text.Append("*Running on Unity ").Append(Unknown(Application.unityVersion))
                .Append(" - Steam API ").Append(SteamApiStatus())
                .Append(" - Xbox API ").Append(XboxApiStatus()).Append("*\n\n");
            AppendField(text, "Game build", Unknown(Application.version));
            AppendField(text, "Platform", Application.platform.ToString());
            AppendField(text, "Role", RoleLabel());
            AppendField(text, "In a loaded save", YesNo(core.InSave));
            AppendField(text, "Save backend", PlatformProbe.SaveBackendDescription);
            text.Append('\n');
        }

        private static void AppendHostingDetails(StringBuilder text, CoopCore core,
            IReadOnlyList<INetworkingBackend> hostBackends)
        {
            text.Append("# Hosting Details\n\n");
            for (var i = 0; i < hostBackends.Count; i++)
            {
                var backend = hostBackends[i];
                var info = backend.ConnectionInfo;
                text.Append("## Hosting via ").Append(Inline(Safe(backend.DisplayName))).Append("\n\n");
                AppendField(text, "Status", info.Summary);
                AppendField(text, "Port forwarding", info.ForwardState.ToString());
                if (!string.IsNullOrEmpty(info.Warning))
                {
                    AppendField(text, "Warning", info.Warning);
                }

                if (!string.IsNullOrEmpty(info.Password))
                {
                    AppendSensitiveField(text, "Session password", info.Password);
                }

                var fields = DiagnosticFields(backend, ConnectMode.Host);
                for (var fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
                {
                    var field = fields[fieldIndex];
                    if (field == null)
                    {
                        continue;
                    }

                    AppendField(text, field.Key, field.Value,
                        field.Sensitive || LooksSensitive(field.Key));
                }

                AppendConnectedClients(text, core);
            }
        }

        private static void AppendConnectedClients(StringBuilder text, CoopCore core)
        {
            text.Append("\n### Connected clients\n\n");
            var players = PresenceApi.SessionPlayers;
            var clientNumber = 0;
            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player.IsHost)
                {
                    continue;
                }

                clientNumber++;
                text.Append("- **Client ").Append(clientNumber).Append(":** `")
                    .Append(Inline(PeerBuild(core, player))).Append("`\n");
            }

            if (clientNumber == 0)
            {
                text.Append("- No clients connected.\n");
            }

            text.Append('\n');
        }

        private static void AppendClientDetails(StringBuilder text, CoopCore core,
            IReadOnlyList<INetworkingBackend> clientBackends)
        {
            text.Append("# Client Details\n\n");
            for (var i = 0; i < clientBackends.Count; i++)
            {
                var backend = clientBackends[i];
                AppendField(text, "Connected via", Safe(backend.DisplayName));
                AppendField(text, "Backend", Safe(backend.Id));
            }

            // Names are deliberately omitted: the report is pasted into public support threads.
            AppendField(text, "Host", "Host");
            AppendField(text, "Host CardShopCoop", Unknown(core.HostVersion));
            AppendField(text, "Host game build", Unknown(core.HostGameVersion));
            AppendField(text, "Host Unity build", Unknown(core.HostUnityVersion));
            text.Append('\n');
        }

        private static void AppendGameInformation(StringBuilder text)
        {
            text.Append("# Game Information\n\n");
            AppendEntityDetails(text);
            AppendPlayerDetails(text);
            AppendShopDetails(text);
        }

        private static void AppendEntityDetails(StringBuilder text)
        {
            text.Append("## Entity Details\n\n");
            var live = CountEntities();

            var world = new StringBuilder();
            AppendCount(world, "Customers", live.Customers,
                CPlayerData.m_CustomerSaveDataList?.Count ?? 0);
            AppendCount(world, "Workers", live.Workers,
                CPlayerData.m_WorkerSaveDataList?.Count ?? 0);
            AppendCount(world, "Interactable objects", live.InteractableObjects,
                CPlayerData.m_InteractableObjectSaveDataList?.Count ?? 0);
            AppendCount(world, "Shelves", live.Shelves,
                CPlayerData.m_ShelfSaveDataList?.Count ?? 0);
            AppendCount(world, "Card shelves", live.CardShelves,
                CPlayerData.m_CardShelfSaveDataList?.Count ?? 0);
            AppendCount(world, "Card + item shelves", live.CardItemShelves,
                CPlayerData.m_CardItemCombiShelfSaveDataList?.Count ?? 0);
            AppendCount(world, "Warehouse shelves", live.WarehouseShelves,
                CPlayerData.m_WarehouseShelfSaveDataList?.Count ?? 0);
            text.Append("- **World:** ").Append(world).Append('\n');

            var containers = new StringBuilder();
            AppendCount(containers, "Packaging boxes", live.PackagingBoxes);
            AppendCount(containers, "Item boxes", live.ItemBoxes,
                CPlayerData.m_PackageBoxItemSaveDataList?.Count ?? 0);
            AppendCount(containers, "Card boxes", live.CardBoxes,
                CPlayerData.m_PackageBoxCardSaveDataList?.Count ?? 0);
            AppendCount(containers, "Card storage shelves", live.CardStorageShelves,
                CPlayerData.m_CardStorageShelfSaveDataList?.Count ?? 0);
            AppendCount(containers, "Workbenches", live.Workbenches,
                CPlayerData.m_WorkbenchSaveDataList?.Count ?? 0);
            AppendCount(containers, "Play tables", live.PlayTables,
                CPlayerData.m_PlayTableSaveDataList?.Count ?? 0);
            AppendCount(containers, "Cashier counters", live.CashierCounters,
                CPlayerData.m_CashCounterSaveDataList?.Count ?? 0);
            text.Append("- **Containers:** ").Append(containers).Append('\n');

            var other = new StringBuilder();
            AppendCount(other, "Decorations", live.Decorations,
                CPlayerData.m_DecoObjectSaveDataList?.Count ?? 0);
            AppendCount(other, "Auto cleansers", live.AutoCleansers,
                CPlayerData.m_AutoCleanserSaveDataList?.Count ?? 0);
            AppendCount(other, "Auto pack openers", live.AutoPackOpeners,
                CPlayerData.m_AutoPackOpenerSaveDataList?.Count ?? 0);
            AppendCount(other, "Empty box storages", live.EmptyBoxStorages,
                CPlayerData.m_EmptyBoxStorageSaveDataList?.Count ?? 0);
            AppendCount(other, "Bulk donation boxes", live.BulkDonationBoxes,
                CPlayerData.m_BulkDonationSaveDataList?.Count ?? 0);
            AppendCount(other, "Food bowls (saved)",
                CPlayerData.m_FoodBowlSaveDataList?.Count ?? 0);
            AppendCount(other, "Trash bins", live.TrashBins);
            AppendCount(other, "Cards - 3D", live.Cards);
            AppendCount(other, "Price tags", live.PriceTags);
            text.Append("- **Other:** ").Append(other).Append('\n');
            text.Append('\n');
        }

        private static void AppendCount(StringBuilder text, string label, int live, int saved = -1)
        {
            if (text.Length > 0)
            {
                text.Append("; ");
            }

            text.Append(label).Append(' ').Append(live);
            if (saved >= 0)
            {
                text.Append('/').Append(saved);
            }
        }

        /// <summary>One scene scan for every entity count. Calling FindObjectsOfType once per type
        /// would scan the whole shop 20+ times and turn a support button into a visible hitch.</summary>
        private static EntityCounts CountEntities()
        {
            var counts = new EntityCounts();
            var behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            for (var i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null)
                {
                    continue;
                }

                if (behaviour is Customer)
                {
                    counts.Customers++;
                }

                if (behaviour is Worker)
                {
                    counts.Workers++;
                }

                if (behaviour is InteractableObject)
                {
                    counts.InteractableObjects++;
                }

                if (behaviour is Shelf)
                {
                    counts.Shelves++;
                }

                if (behaviour is CardShelf && !(behaviour is CardItemCombiShelf))
                {
                    counts.CardShelves++;
                }

                if (behaviour is CardItemCombiShelf)
                {
                    counts.CardItemShelves++;
                }

                if (behaviour is WarehouseShelf)
                {
                    counts.WarehouseShelves++;
                }

                if (behaviour is InteractablePackagingBox)
                {
                    counts.PackagingBoxes++;
                }

                if (behaviour is InteractablePackagingBox_Item)
                {
                    counts.ItemBoxes++;
                }

                if (behaviour is InteractablePackagingBox_Card)
                {
                    counts.CardBoxes++;
                }

                if (behaviour is InteractableCardStorageShelf)
                {
                    counts.CardStorageShelves++;
                }

                if (behaviour is InteractableWorkbench)
                {
                    counts.Workbenches++;
                }

                if (behaviour is InteractablePlayTable)
                {
                    counts.PlayTables++;
                }

                if (behaviour is InteractableCashierCounter)
                {
                    counts.CashierCounters++;
                }

                if (behaviour is InteractableDecoration)
                {
                    counts.Decorations++;
                }

                if (behaviour is InteractableAutoCleanser)
                {
                    counts.AutoCleansers++;
                }

                if (behaviour is InteractableAutoPackOpener)
                {
                    counts.AutoPackOpeners++;
                }

                if (behaviour is InteractableEmptyBoxStorage)
                {
                    counts.EmptyBoxStorages++;
                }

                if (behaviour is InteractableBulkDonationBox)
                {
                    counts.BulkDonationBoxes++;
                }

                if (behaviour is InteractableTrashBin)
                {
                    counts.TrashBins++;
                }

                if (behaviour is InteractableCard3d)
                {
                    counts.Cards++;
                }

                if (behaviour is InteractablePriceTag || behaviour is InteractableCardPriceTag)
                {
                    counts.PriceTags++;
                }
            }

            return counts;
        }

        private static void AppendPlayerDetails(StringBuilder text)
        {
            text.Append("## Player Details\n\n");
            var players = PresenceApi.SessionPlayers;
            if (players.Count == 0)
            {
                text.Append("_No session roster is available._\n\n");
                return;
            }

            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                text.Append("### Player ").Append(i + 1).Append("\n\n");
                AppendField(text, "Role", player.IsHost ? "Host" : "Client");
                AppendField(text, "Build", PeerBuild(CoopCore.Instance, player));

                PresenceModelEntry model = null;
                if (player.IsSelf)
                {
                    model = PresenceApi.GetLocalPlayerModel();
                }
                else if (!PresenceApi.TryGetSessionModel(player.Id, out model))
                {
                    model = null;
                }

                AppendOutfit(text, model);
                text.Append('\n');
            }
        }

        private static void AppendOutfit(StringBuilder text, PresenceModelEntry model)
        {
            if (model == null)
            {
                text.Append("- **Outfit:** unknown\n");
                return;
            }

            text.Append("- **Model:** ")
                .Append(model.Female ? "Female " : "Male ")
                .Append(model.ModelIndex).Append('\n');

            if (string.IsNullOrEmpty(model.CustomizationJson))
            {
                return;
            }

            CC.CC_CharacterData data;
            try
            {
                data = JsonConvert.DeserializeObject<CC.CC_CharacterData>(model.CustomizationJson);
            }
            catch (Exception error)
            {
                text.Append("- **Outfit data:** ")
                    .Append(Inline("unreadable: " + error.Message)).Append('\n');
                return;
            }

            if (data == null)
            {
                return;
            }

            AppendListField(text, "Hair", data.HairNames);
            AppendListField(text, "Apparel", data.ApparelNames);
            var colors = HairColors(data.HairColor);
            if (colors.Length > 0)
            {
                text.Append("- **Hair colors:** `").Append(colors).Append("`\n");
            }
        }

        private static void AppendShopDetails(StringBuilder text)
        {
            text.Append("## Shop Details\n\n");
            // The player-chosen shop name is deliberately omitted from a public support report.
            var summary = new StringBuilder();
            AppendPair(summary, "Money",
                CPlayerData.m_CoinAmountDouble.ToString("0.##", CultureInfo.InvariantCulture));
            AppendPair(summary, "Total earned",
                CPlayerData.m_TotalCoinAmount.ToString(CultureInfo.InvariantCulture));
            AppendPair(summary, "Level", CPlayerData.m_ShopLevel.ToString(CultureInfo.InvariantCulture));
            AppendPair(summary, "XP", CPlayerData.m_ShopExpPoint.ToString(CultureInfo.InvariantCulture));
            AppendPair(summary, "Day", CPlayerData.m_CurrentDay.ToString(CultureInfo.InvariantCulture));
            AppendPair(summary, "Time", CurrentTime());
            text.Append("- ").Append(summary).Append('\n');

            var progress = new StringBuilder();
            AppendPair(progress, "Fame",
                CPlayerData.m_TotalFameAdd.ToString(CultureInfo.InvariantCulture));
            AppendPair(progress, "Licenses",
                CountTrue(CPlayerData.m_IsItemLicenseUnlocked) + " / "
                + (CPlayerData.m_IsItemLicenseUnlocked?.Count ?? 0));
            AppendPair(progress, "Workers",
                CountTrue(CPlayerData.m_IsWorkerHired) + " / "
                + (CPlayerData.m_IsWorkerHired?.Count ?? 0));
            AppendPair(progress, "Rooms",
                CPlayerData.m_UnlockRoomCount.ToString(CultureInfo.InvariantCulture));
            AppendPair(progress, "Warehouse rooms",
                CPlayerData.m_UnlockWarehouseRoomCount.ToString(CultureInfo.InvariantCulture));
            AppendPair(progress, "Warehouse", YesNo(CPlayerData.m_IsWarehouseRoomUnlocked));
            AppendPair(progress, "Scanner restock", YesNo(CPlayerData.m_IsScannerRestockUnlocked));
            text.Append("- ").Append(progress).Append('\n');

            var state = new StringBuilder();
            AppendPair(state, "Shop open", YesNo(CPlayerData.m_IsShopOpen));
            AppendPair(state, "Warehouse door closed", YesNo(CPlayerData.m_IsWarehouseDoorClosed));
            AppendPair(state, "Customer reviews",
                CPlayerData.m_CustomerReviewCount.ToString(CultureInfo.InvariantCulture));
            AppendPair(state, "Review average",
                CPlayerData.m_CustomerReviewScoreAverage.ToString("0.##", CultureInfo.InvariantCulture));
            text.Append("- ").Append(state).Append('\n');
            text.Append('\n');
        }

        private static void AppendPair(StringBuilder text, string label, string value)
        {
            if (text.Length > 0)
            {
                text.Append(" \u00b7 ");
            }

            text.Append("**").Append(Safe(label)).Append(":** `")
                .Append(string.IsNullOrEmpty(value) ? "empty" : Inline(value)).Append('`');
        }

        private static void AppendLogReference(StringBuilder text, string logPath)
        {
            text.Append("# Full Log\n\n");
            if (logPath == null)
            {
                text.Append("_No log file was available._\n");
                return;
            }

            text.Append("_The co-op log file is `")
                .Append(Inline(Path.GetFileName(logPath))).Append("`._\n");
        }

        private static IReadOnlyList<INetworkingBackend> ActiveBackends(CoopCore core, bool hosts)
        {
            var result = new List<INetworkingBackend>();
            var backends = core?.Backends;
            if (backends == null)
            {
                return result;
            }

            for (var i = 0; i < backends.Count; i++)
            {
                var backend = backends[i];
                var info = backend.ConnectionInfo;
                if (info.Active && info.IsHost == hosts)
                {
                    result.Add(backend);
                }
            }

            return result;
        }

        private static IReadOnlyList<BackendDiagnosticField> DiagnosticFields(
            INetworkingBackend backend, ConnectMode mode)
        {
            if (backend is INetworkingBackendDiagnostics diagnostics)
            {
                var fields = diagnostics.GetDiagnosticFields(mode);
                if (fields != null)
                {
                    return fields;
                }
            }

            // A third-party transport need not implement the diagnostic interface. Its normal
            // field list still gives the report something useful, including the new Sensitive flag.
            var source = backend.GetFields(mode);
            var fallback = new List<BackendDiagnosticField>((source?.Count ?? 0) + 1)
            {
                new BackendDiagnosticField("Backend", backend.Id),
            };
            if (source != null)
            {
                for (var i = 0; i < source.Count; i++)
                {
                    var field = source[i];
                    if (field != null)
                    {
                        fallback.Add(new BackendDiagnosticField(field.Label, field.Value,
                            field.Sensitive || field.Kind == FieldKind.Password
                            || LooksSensitive(field.Label)));
                    }
                }
            }

            return fallback;
        }

        private static string PeerBuild(CoopCore core, PresencePlayer player)
        {
            if (player.IsSelf)
            {
                return LocalBuild();
            }

            if (player.IsHost)
            {
                return Build(core == null ? "" : core.HostVersion,
                    core == null ? "" : core.HostGameVersion,
                    core == null ? "" : core.HostUnityVersion);
            }

            if (core != null && core.TryGetPeerBuild(player.Id, out var peer))
            {
                return Build(peer.Version, peer.GameVersion, peer.UnityVersion);
            }

            return "unknown";
        }

        private static string LocalBuild()
            => Build(CoopPlugin.Version, Application.version, Application.unityVersion);

        private static string Build(string modVersion, string gameVersion, string unityVersion)
            => "CardShopCoop " + Unknown(modVersion)
                + ", game " + Unknown(gameVersion)
                + ", Unity " + Unknown(unityVersion);

        private static string Unknown(string value)
            => string.IsNullOrEmpty(value) ? "unknown" : value;

        private static string RoleLabel()
            => CoopCore.Role == CoopRole.Host ? "Host"
                : CoopCore.Role == CoopRole.Client ? "Client" : "Not connected";

        private static string SteamApiStatus()
            => PlatformProbe.SteamworksPresent ? "available" : "unavailable";

        private static string XboxApiStatus()
            => PlatformProbe.GamePassBuild ? "available" : "unavailable";

        private sealed class EntityCounts
        {
            internal int Customers;
            internal int Workers;
            internal int InteractableObjects;
            internal int Shelves;
            internal int CardShelves;
            internal int CardItemShelves;
            internal int WarehouseShelves;
            internal int PackagingBoxes;
            internal int ItemBoxes;
            internal int CardBoxes;
            internal int CardStorageShelves;
            internal int Workbenches;
            internal int PlayTables;
            internal int CashierCounters;
            internal int Decorations;
            internal int AutoCleansers;
            internal int AutoPackOpeners;
            internal int EmptyBoxStorages;
            internal int BulkDonationBoxes;
            internal int TrashBins;
            internal int Cards;
            internal int PriceTags;
        }

        private static int CountTrue(List<bool> values)
        {
            if (values == null)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i])
                {
                    count++;
                }
            }

            return count;
        }

        private static string CurrentTime()
        {
            if (LightManager.m_Instance == null)
            {
                return "unavailable";
            }

            try
            {
                var hour = LightManager.GetTimeHour();
                var minute = LightManager.GetTimeMinute();
                return hour.ToString("00", CultureInfo.InvariantCulture)
                    + ":" + minute.ToString("00", CultureInfo.InvariantCulture);
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogWarning("diagnostics could not read the shop clock: "
                    + error.Message);
                return "unavailable";
            }
        }

        private static string HairColors(List<CC.CC_Property> colors)
        {
            if (colors == null || colors.Count == 0)
            {
                return "";
            }

            var result = new List<string>(colors.Count);
            for (var i = 0; i < colors.Count; i++)
            {
                var value = colors[i]?.stringValue;
                if (!string.IsNullOrEmpty(value))
                {
                    result.Add(Inline("#" + value));
                }
            }

            return string.Join(", ", result.ToArray());
        }

        private static void AppendListField(StringBuilder text, string label, List<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return;
            }

            var present = new List<string>(values.Count);
            for (var i = 0; i < values.Count; i++)
            {
                if (!string.IsNullOrEmpty(values[i]))
                {
                    present.Add(values[i]);
                }
            }

            if (present.Count > 0)
            {
                text.Append("- **").Append(label).Append(":** `")
                    .Append(Inline(string.Join(", ", present.ToArray()))).Append("`\n");
            }
        }

        private static void AppendSensitiveField(StringBuilder text, string label, string value)
            => AppendField(text, label, value, sensitive: true);

        /// <summary>The fallback path cannot know whether a third-party transport compiled against
        /// an older API set <see cref="BackendField.Sensitive"/>. Redact the obvious credential and
        /// address labels as well, so an old transport cannot publish a password by omission.</summary>
        private static bool LooksSensitive(string label)
        {
            var lowered = label == null ? "" : label.ToLowerInvariant();
            return lowered.Contains("password")
                || lowered.Contains("passcode")
                || lowered.Contains("lobby id")
                || lowered.Contains("lobby name")
                || lowered.Contains("host address")
                || lowered.Contains("public address")
                || lowered.Contains("public endpoint")
                || lowered.Contains("ip address");
        }

        private static void AppendField(StringBuilder text, string label, string value,
            bool sensitive = false)
        {
            text.Append("- **").Append(Safe(label)).Append(":** ");
            if (sensitive)
            {
                text.Append("[redacted]");
            }
            else if (string.IsNullOrEmpty(value))
            {
                text.Append("empty");
            }
            else
            {
                text.Append('`').Append(Inline(value)).Append('`');
            }
            text.Append('\n');
        }

        private static string Inline(string value)
            => (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace("`", "'");

        private static string Safe(string value) => value ?? "";

        private static string YesNo(bool value) => value ? "yes" : "no";
    }
}
