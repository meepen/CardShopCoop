using System;
using System.IO;
using BepInEx;
using Newtonsoft.Json;

namespace CardShopCoop.Modules.Presence
{
    /// <summary>Private co-op appearance persistence. It intentionally keeps the existing file
    /// name so the module can take over without discarding a player's saved appearance.</summary>
    internal static class PresenceModelStore
    {
        private static readonly string FilePath = Path.Combine(Paths.ConfigPath,
            "CardShopCoopPlayerModel.json");

        internal static bool TryLoad(out PresenceModelEntry model)
        {
            model = null;
            if (!File.Exists(FilePath))
            {
                return false;
            }

            try
            {
                model = JsonConvert.DeserializeObject<PresenceModelEntry>(File.ReadAllText(FilePath));
                if (model == null || model.ModelIndex < 0)
                {
                    throw new InvalidDataException("missing or invalid model fields");
                }

                return true;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("local player model could not be loaded; using the game appearance: "
                    + error.Message);
                model = null;
                return false;
            }
        }

        internal static void Save(PresenceModelEntry model)
        {
            if (model == null)
            {
                return;
            }

            var temporaryPath = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Paths.ConfigPath);
                File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(model, Formatting.Indented));
                if (File.Exists(FilePath))
                {
                    File.Replace(temporaryPath, FilePath, null);
                }
                else
                {
                    File.Move(temporaryPath, FilePath);
                }
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("local player model could not be saved: " + error.Message);
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch (Exception cleanupError)
                {
                    Swallow.Log(cleanupError);
                }
            }
        }
    }
}
