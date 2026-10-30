using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Net;
using CardShopCoop.Net.Networking;
using Newtonsoft.Json;
using UnityEngine;

namespace CardShopCoop.Diagnostics
{
    /// <summary>
    /// Builds the player-facing support report. One clipboard session carries the Markdown report,
    /// BepInEx's <c>LogOutput.log</c> and Unity's <c>Player.log</c> as file attachments, so a player
    /// can send the complete support bundle with a single paste. The logs are attached exactly as
    /// they are on disk; the report itself still redacts credentials and addresses.
    /// </summary>
    internal static class DiagnosticsReport
    {
        /// <summary>Copies one paste that carries the Markdown report, the BepInEx master log and
        /// the Unity player log as file attachments.</summary>
        internal static bool TryCopyBundle(out string message)
        {
            var core = CoopCore.Instance;
            if (core == null)
            {
                message = "Co-op is not running.";
                return false;
            }

            CleanupOldDiagnosticsFiles();

            var logOutputPath = FindLogOutputFile();
            var playerLogPath = FindPlayerLog();
            var logPaths = new List<string>(2);
            if (logOutputPath != null)
            {
                logPaths.Add(logOutputPath);
            }

            if (playerLogPath != null)
            {
                logPaths.Add(playerLogPath);
            }

            string reportPath;
            try
            {
                var report = Build(core, logPaths);
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

            var files = new List<string>(1 + logPaths.Count) { reportPath };
            files.AddRange(logPaths);

            try
            {
                ClipboardWriter.SetTextAndFiles(null, files);
                message = DescribeBundle(files.Count, logOutputPath, playerLogPath);
                CoopPlugin.Log?.LogInfo("diagnostics bundle copied: " + files.Count + " files ("
                    + string.Join(", ", files.ToArray()) + ")");
                return true;
            }
            catch (Exception error)
            {
                message = "Could not copy diagnostics: " + error.Message;
                CoopPlugin.Log?.LogError("diagnostics bundle copy failed: " + error);
                return false;
            }
        }

        private static string DescribeBundle(int fileCount, string logOutputPath, string playerLogPath)
        {
            var missing = new List<string>(2);
            if (logOutputPath == null)
            {
                missing.Add("LogOutput.log");
            }

            if (playerLogPath == null)
            {
                missing.Add("Player.log");
            }

            var message = "Diagnostics copied (" + fileCount + " files). Logs are raw and can "
                + "contain player names and addresses.";
            if (missing.Count > 0)
            {
                message += " Not found: " + string.Join(" and ", missing.ToArray()) + ".";
            }

            return message;
        }

        /// <summary>BepInEx's master log. It contains every plugin's output but belongs to the first
        /// game instance, which is why the per-process mod log still exists for multi-instance
        /// testing.</summary>
        private static string FindLogOutputFile()
        {
            try
            {
                if (string.IsNullOrEmpty(Paths.BepInExRootPath))
                {
                    return null;
                }

                var path = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
                return File.Exists(path) ? path : null;
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogWarning("diagnostics could not locate LogOutput.log: "
                    + error.Message);
                return null;
            }
        }

        /// <summary>Unity's current player log. <see cref="Application.consoleLogPath"/> follows a
        /// launch argument such as <c>-logFile</c>; the persistent-data default is the fallback.</summary>
        private static string FindPlayerLog()
        {
            try
            {
                var path = Application.consoleLogPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    return path;
                }

                path = Path.Combine(Application.persistentDataPath, "Player.log");
                return File.Exists(path) ? path : null;
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogWarning("diagnostics could not locate Player.log: "
                    + error.Message);
                return null;
            }
        }

        private const string DiagnosticsFilePrefix = "CardShopCoop_Diagnostics_";

        /// <summary>Removes reports from earlier runs. The current report must stay on disk while
        /// it is on the clipboard, so cleanup is deferred to the next bundle action.</summary>
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

        private static string Build(CoopCore core, IReadOnlyList<string> logPaths)
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

            AppendLogReference(text, logPaths);
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

        /// <summary>One scene scan for every entity count. Calling FindObjectsByType once per type
        /// would scan the whole shop 20+ times and turn a support button into a visible hitch.</summary>
        private static EntityCounts CountEntities()
        {
            var counts = new EntityCounts();
            var behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
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

        private static void AppendLogReference(StringBuilder text, IReadOnlyList<string> logPaths)
        {
            text.Append("# Attached Logs\n\n");
            if (logPaths == null || logPaths.Count == 0)
            {
                text.Append("_No log files were available._\n");
                return;
            }

            text.Append("_These files are attached with this report:_\n\n");
            for (var i = 0; i < logPaths.Count; i++)
            {
                text.Append("- `").Append(Inline(Path.GetFileName(logPaths[i]))).Append("`\n");
            }
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
