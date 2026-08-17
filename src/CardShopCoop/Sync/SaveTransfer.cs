using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Join-time world sync: the host serializes its current shop with the game's own
    /// save pipeline and streams the JSON save file to the client. The client writes it
    /// into a dedicated slot (7) that the in-game save UI never shows, then boots it
    /// through the game's normal load path. Result: both players stand in the same shop,
    /// with the same shelves, stock, licenses and PTCGO expansions, without the client's
    /// own saves (slots 0-3) ever being touched.
    ///
    /// The in-memory half of that pipeline - SerializeInMemorySave on the host and the
    /// CSaveLoad.m_SavedGame injection in ApplyAndLoad - is an approach contributed by
    /// Jburne10 for the Game Pass build, where saves live in Xbox wgs containers invisible
    /// to the game process, so savedGames_Release&lt;slot&gt;.json is never written and never
    /// read.
    ///
    /// BOTH HALVES ARE PLATFORM-GATED, on the same condition and for symmetric reasons:
    ///  - HOST (BuildHostPayload): on Steam the slot file is the ONLY accepted source, because
    ///    only the file can prove the save actually ran - a missing file there means
    ///    SaveGameData bailed, not "the saves live somewhere we can't see", and the in-memory
    ///    object at that moment is the stale world the guards exist to stop us shipping.
    ///  - GUEST (ApplyAndLoad): on Steam the file write IS the load path, so overwriting the
    ///    process-global CSaveLoad.m_SavedGame buys nothing and costs the one promise this
    ///    class makes - that joining someone never touches the guest's own game state. That
    ///    static is what a subsequent save of the guest's OWN world would serialize.
    /// </summary>
    public static class SaveTransfer
    {
        /// <summary>Slot the co-op world lives in on the client (config: ClientWorldSlot).</summary>
        public static int CoopSlot => CoopPlugin.ClientWorldSlot?.Value ?? 7;

        /// <summary>Throwaway slot the HOST snapshots its live world into at every join
        /// (mirror of the client's coop slot 7 convention). We must NOT force-save over the
        /// host's REAL slot: SaveGameData writes the on-disk file immediately, so snapshotting
        /// into the live slot baked whatever the world looked like at that instant - including
        /// a transient mid-join shelf-stock wipe (see WorldSync FIX D-a) - permanently into the
        /// host's real save. Slot 6 is outside the vanilla range (0 = autosave, 1-3 = manual),
        /// the in-game save UI never shows it, and the host never LOADS it, so it's safe to
        /// clobber on every join.</summary>
        public const int HostSnapshotSlot = 6;

        public static string SlotPath(int slot)
        {
            return Application.persistentDataPath + "/savedGames_Release" + slot + ".json";
        }

        /// <summary>Reads the game's in-memory save object (CSaveLoad.m_SavedGame) and returns
        /// it serialized exactly the way the game serializes it to disk. Null if unavailable.
        ///
        /// GAME PASS FALLBACK: on the Microsoft Store / Game Pass build the game does NOT write
        /// savedGames_Release&lt;slot&gt;.json at all - saves go to Xbox Game Save ("wgs") containers
        /// named GameData_&lt;slot&gt;, and MSIX redirection hides those from the game's own process,
        /// so no amount of file probing can find them. But the game keeps the whole save in
        /// memory as the static CSaveLoad.m_SavedGame, and its own writer is just
        /// JsonUtility.ToJson(m_SavedGame) (decompiled CSaveLoad.Save). Serializing that object
        /// ourselves therefore yields byte-identical content to the file the joiner expects,
        /// with no filesystem involved.
        ///
        /// The field is read through reflection and written back through fld.FieldType so this
        /// method never names CGameData: the Game Pass Assembly-CSharp is a different build and
        /// a renamed/reshaped save type must cost us this one path, not the whole type's load.
        /// Approach contributed by Jburne10.</summary>
        private static byte[] SerializeInMemorySave()
        {
            try
            {
                var fld = typeof(CSaveLoad).GetField("m_SavedGame",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (fld == null) return null;
                object saved = fld.GetValue(null);
                if (saved == null) return null;

                string json = JsonUtility.ToJson(saved);
                // an empty/degenerate serialization means the object is not a real world -
                // shipping "{}" to the joiner would boot it into an empty shop
                if (string.IsNullOrEmpty(json) || json.Length < 32 || json == "{}") return null;
                return new UTF8Encoding(false).GetBytes(json); // no BOM: the game's writer emits none
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("coop: in-memory save serialization failed: " + e.Message);
                return null;
            }
        }

        /// <summary>Host: flush the live game into the THROWAWAY snapshot slot (never the host's
        /// real slot) and return the save bytes. CGameManager.SaveGameData derives the file path
        /// from the slot ARG (verified in the decompiled source - slot 6 writes
        /// savedGames_Release6.json), but it also overwrites m_CurrentSaveLoadSlotSelectedIndex as
        /// a side effect; that field feeds the game's load/quit paths, so we capture and RESTORE
        /// it in a finally to keep the host's notion of "current slot" from drifting to 6.</summary>
        public static byte[] BuildHostPayload()
        {
            var gm = CSingleton<CGameManager>.Instance;
            string path = SlotPath(HostSnapshotSlot);
            // delete the PREVIOUS join's snapshot first: SaveGameData silently bails on any of
            // its guards (loading error, mid scene-transition, day-report screen...), and a
            // stale slot-6 file from an earlier join would then be mistaken for this join's
            // snapshot and ship an OLD world to the joiner. A skip must be LOUD (the throw
            // below), never stale. Best effort only - the timestamp check further down is what
            // makes the guarantee, because this delete is allowed to fail silently.
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try
            {
                string gd = Application.persistentDataPath + "/savedGames_Release" + HostSnapshotSlot + ".gd";
                if (File.Exists(gd)) File.Delete(gd);
            }
            catch { }
            int prevSlot = gm.m_CurrentSaveLoadSlotSelectedIndex;
            // FRESHNESS CLOCK. The two deletes above swallow their failures on purpose (an
            // antivirus or cloud-sync client can hold the file open for a moment, and a
            // transient lock must not abort a join) - which means a stale slot-6 file can
            // SURVIVE them, and a bare File.Exists below would then hand the joiner an old
            // world exactly as if nothing had gone wrong. So stamp the clock here, before the
            // save, and require the file to be at least this new: existence alone proves the
            // save COMPLETED, but only the timestamp proves it completed NOW.
            DateTime t0 = DateTime.UtcNow;
            try
            {
                gm.SaveGameData(HostSnapshotSlot); // synchronous: writes savedGames_Release6.json
            }
            catch (Exception e)
            {
                // Some builds throw inside SaveGameData's save-slot UI refresh for this
                // out-of-band slot (the save-UI list has no button for slot 6). The world data
                // is written before that point, so log and let the checks below decide.
                CoopPlugin.Log.LogWarning("coop: SaveGameData(" + HostSnapshotSlot + ") threw: " + e.Message);
            }
            finally
            {
                gm.m_CurrentSaveLoadSlotSelectedIndex = prevSlot; // undo SaveGameData's side effect
            }

            // ORDER OF PREFERENCE - deliberately the reverse of the upstream Game Pass patch,
            // and the one place we deviate from it. A FRESH slot file wins whenever there is
            // one: CSaveLoad.Save writes a temp json, validates it by round-tripping it back
            // through JsonUtility, and only THEN moves it onto savedGames_Release6.json - so a
            // file whose timestamp is on OUR side of t0 is PROOF that the save ran all the way
            // to completion, just now. The in-memory object carries no such proof:
            // CGameManager.SaveGameData bails silently on any of its four guards (title screen,
            // loading error, no shelf data, savefile still loading), and CSaveLoad.m_SavedGame
            // then still holds whatever the previous save or load left in it - an OLD world that
            // would ship to the joiner with no error at all. A skip must be LOUD, never stale.
            //
            // A file that exists but is OLD is treated as ABSENT (a delete that quietly failed
            // - see the clock above), which drops through to the platform-gated fallback and,
            // on Steam, to the throw. The one second of slack is for filesystem timestamp
            // granularity and clock skew only; it is orders of magnitude shorter than the gap
            // between two joins, so it can never re-admit a previous session's snapshot.
            bool fresh;
            try { fresh = File.Exists(path) && File.GetLastWriteTimeUtc(path) >= t0.AddSeconds(-1); }
            catch { fresh = false; } // unreadable metadata is not proof of anything: treat as absent
            if (fresh)
            {
                byte[] bytes = File.ReadAllBytes(path);
                CoopPlugin.Log.LogInfo($"coop: host snapshot from slot file ({bytes.Length / 1024} KB)");
                return bytes;
            }

            // ---- fallback: in-memory path, GAME PASS / DRM-FREE ONLY ----
            // PLATFORM-GATED ON PURPOSE. No fresh file has two completely different meanings,
            // and only one of them is survivable:
            //  - Steamworks ABSENT (Game Pass / MSIX): the game never used the local json
            //    layout at all - it wrote an Xbox wgs container we cannot see - so there was
            //    never going to be a file, and the in-memory object is the only source there
            //    is. We accept it knowing we lose the completion proof above.
            //  - Steamworks PRESENT (Steam): the game DOES write savedGames_Release6.json, so
            //    no fresh file means SaveGameData silently bailed on one of its guards. The
            //    in-memory object at that moment is precisely the stale world the guards
            //    exist to stop us from shipping. Falling back here would replace a loud,
            //    fixable error with a joiner standing in an old or empty shop - so on Steam
            //    we never reach this block and go straight to the throw.
            if (!Net.PlatformProbe.SteamworksPresent)
            {
                byte[] mem = SerializeInMemorySave();
                if (mem != null)
                {
                    CoopPlugin.Log.LogInfo($"coop: host snapshot from CSaveLoad.m_SavedGame ({mem.Length / 1024} KB)");
                    return mem;
                }
            }

            throw new FileNotFoundException(
                Net.PlatformProbe.SteamworksPresent
                ? "Host snapshot failed: the game wrote no fresh slot file (SaveGameData skipped - " +
                  "title screen, loading error, no shelf data, or the savefile was still loading). " +
                  "On Steam the slot file is the ONLY accepted source: the in-memory save object is " +
                  "deliberately not used here, because after a skipped save it still holds the " +
                  "PREVIOUS world and would ship it to the joiner with no error at all. Load into " +
                  "your shop fully, then retry."
                : "Host snapshot failed on BOTH paths: the game wrote no fresh slot file (SaveGameData " +
                  "skipped - title screen, loading error, no shelf data, or the savefile was still " +
                  "loading), AND the in-memory save object (CSaveLoad.m_SavedGame) could not be read " +
                  "or was empty. This build has no Steamworks, so the game writes Xbox wgs containers " +
                  "rather than local json and the in-memory path is the only one that can work here. " +
                  "Load into your shop fully, then retry.", path);
        }

        /// <summary>Client: apply the received world into the co-op slot and load it.
        ///
        /// GAME PASS FALLBACK: writing savedGames_Release&lt;CoopSlot&gt;.json is not enough on the
        /// Microsoft Store build - the game never reads that file there (its saves live in Xbox
        /// wgs containers), so the subsequent load silently kept the client's own world. We
        /// therefore ALSO deserialize the received JSON straight into CSaveLoad.m_SavedGame,
        /// which is the object CGameManager.LoadData propagates into the live CGameData.
        ///
        /// PLATFORM-GATED, mirroring BuildHostPayload. On Steam the file write below IS the load
        /// path, so the injection would add nothing - while m_SavedGame is a PROCESS-GLOBAL that
        /// the guest's own saves serialize from, and this class exists to promise that joining
        /// someone never touches the guest's own game state. Doing it anyway on the platform
        /// that does not need it is spending exactly the risk we advertise we do not take. The
        /// file write is retained on BOTH platforms (harmless where it isn't read) and wrapped
        /// in its own try/catch so that a permission-restricted Packages tree (the Game Pass
        /// persistentDataPath) degrades to in-memory-only instead of aborting the join.
        /// Approach contributed by Jburne10.</summary>
        public static void ApplyAndLoad(byte[] saveBytes)
        {
            var gm = CSingleton<CGameManager>.Instance;
            gm.m_ForceNoCloudSaveLoad = true; // keep Steam/Xbox cloud away from the borrowed world

            bool injected = false;
            if (!Net.PlatformProbe.SteamworksPresent)
            {
                try
                {
                    // the host may have serialized from memory (no BOM) or shipped a file the
                    // game wrote; trim either shape before the sanity check
                    string json = new UTF8Encoding(false).GetString(saveBytes)
                                                         .TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                    if (json.StartsWith("{", StringComparison.Ordinal)) // never hand junk to FromJson
                    {
                        var fld = typeof(CSaveLoad).GetField("m_SavedGame",
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (fld != null)
                        {
                            // FromJson(json, fld.FieldType), not FromJson<CGameData>: the save
                            // type is resolved from the running build, never named here
                            object world = JsonUtility.FromJson(json, fld.FieldType);
                            if (world != null) { fld.SetValue(null, world); injected = true; }
                            else CoopPlugin.Log.LogWarning("coop: FromJson returned null for the received world");
                        }
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("coop: injecting received world failed: " + e.Message); }
            }

            // CLEAR EVERY LOADABLE FORM OF THE CO-OP SLOT FIRST - all three, in one try, BEFORE
            // the write below. If the write then throws (a restricted Packages tree), the slot
            // must hold NOTHING: a failure that boots the joiner into an empty slot is loud and
            // obvious, while a failure that boots them into the PREVIOUS join's world is silent
            // and wrong. The game gives the slot three chances to be stale:
            //   .gd   - CSaveLoad.Load deserializes savedGames_Release<slot>.gd when the json is
            //           missing, so a .gd left by an earlier session is a complete old world.
            //   .json - the file we are about to overwrite. Deleting it first means a failed
            //           write leaves no file at all instead of the one already sitting there.
            //   savedGames_ReleaseBackupFile<slot>.json - the game's LoadBackupData reads this
            //           when the normal Load fails, which is exactly the state a failed write
            //           puts us in. It is the last door into a previous join's world.
            //
            // ONE try PER FILE, not one around all three. An antivirus or cloud-sync client
            // holding ANY of them open threw out of the shared block and silently skipped the
            // rest - and the two doors most likely to be left standing that way (.gd and the
            // backup json) are precisely the ones the game falls back to when the load of the
            // file we DID replace goes wrong. Each delete is independent, so each gets its own
            // chance to succeed and its own warning when it doesn't.
            string basePath = Application.persistentDataPath + "/savedGames_Release" + CoopSlot;
            TryDelete(basePath + ".gd");
            TryDelete(SlotPath(CoopSlot));
            TryDelete(Application.persistentDataPath + "/savedGames_ReleaseBackupFile" + CoopSlot + ".json");

            // A failed write is NOT fatal: on Game Pass the injected in-memory world above is
            // what the load path reads anyway, so we degrade to in-memory-only and carry on.
            try
            {
                File.WriteAllBytes(SlotPath(CoopSlot), saveBytes);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("coop: could not write co-op slot file: " + e.Message); }

            CoopPlugin.Log.LogInfo($"Coop save received ({saveBytes.Length / 1024} KB){(injected ? " [in-memory]" : "")}, loading world...");
            ForceLoadSlot(CoopSlot);
        }

        /// <summary>Delete one file if it is there, and let a failure be exactly that one file's
        /// failure. Never throws: every caller is mid-join, where a locked file is a degraded
        /// outcome to warn about, not a reason to abandon the join.</summary>
        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("coop: could not clear " + Path.GetFileName(path) + ": " + e.Message);
            }
        }

        /// <summary>Drive the game's own title->shop load path for an arbitrary slot.</summary>
        public static void ForceLoadSlot(int slot)
        {
            var gm = CSingleton<CGameManager>.Instance;
            gm.m_CurrentSaveLoadSlotSelectedIndex = slot;

            // The load-on-scene-enter path only runs while m_InitLoaded is false.
            var initLoaded = typeof(CGameManager).GetField("m_InitLoaded",
                BindingFlags.NonPublic | BindingFlags.Static);
            initLoaded?.SetValue(null, false);

            gm.LoadMainLevelAsync("Start", slot);
        }
    }
}
