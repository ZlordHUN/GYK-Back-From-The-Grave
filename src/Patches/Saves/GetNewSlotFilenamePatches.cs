using HarmonyLib;
using System.IO;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches PlatformSpecific.GetNewSlotFilename to prefix multiplayer saves with "coop_".
    /// This ensures that saves created during a multiplayer session are tagged so they
    /// can be filtered out of single-player save lists and only shown in multiplayer mode.
    /// </summary>
    [HarmonyPatch(typeof(PlatformSpecific), "GetNewSlotFilename")]
    public class GetNewSlotFilenamePatches
    {
        static bool Prefix(ref string __result)
        {
            if (!MainMenuPatches.IsMultiplayerSaveMode &&
                !MainMenuPatches.IsMultiplayerSaveContextActive())
                return true;

            string saveFolder = PlatformSpecific.GetSaveFolder();
            int num = 0;
            while (num++ <= 1000)
            {
                string filename = "coop_" + num.ToString();
                string infoPath = saveFolder + filename + ".info";
                string datPath = saveFolder + filename + ".dat";
                if (!File.Exists(infoPath) && !File.Exists(datPath))
                {
                    CoopMod.Logger.LogInfo($"[GetNewSlotFilename] Multiplayer mode: assigned filename {filename}");
                    __result = filename;
                    return false;
                }
            }

            CoopMod.Logger.LogError("[GetNewSlotFilename] Could not find available coop_ slot filename");
            __result = null;
            return false;
        }
    }
}
