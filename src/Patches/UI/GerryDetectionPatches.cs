using HarmonyLib;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches to detect and log Gerry's obj_id when he appears in the game.
    /// Start a new game and check logs for Gerry's spawning.
    /// 
    /// NOTE: This class is currently DISABLED - We already discovered Gerry's ID is "talking_skull".
    /// These patches cause SEVERE performance issues by logging every game object spawn.
    /// </summary>
    // [HarmonyPatch] // DISABLED - causes performance issues
    public class GerryDetectionPatches
    {
        /// <summary>
        /// Logs every WorldGameObject that gets created with its ID and custom tag.
        /// This will help us identify Gerry when he spawns at game start.
        /// DISABLED - This logs thousands of objects and causes FPS drops!
        /// </summary>
        // [HarmonyPatch(typeof(WorldGameObject), "Awake")]
        // [HarmonyPostfix]
        public static void WorldGameObject_Awake_Postfix(WorldGameObject __instance)
        {
            try
            {
                if (__instance == null) return;

                string objId = __instance.obj_id ?? "null";
                string customTag = __instance.custom_tag ?? "null";
                string objName = __instance.gameObject?.name ?? "null";
                
                // Log all WGOs, but highlight potential Gerry matches
                bool isPotentialGerry = 
                    objId.ToLower().Contains("skull") ||
                    objId.ToLower().Contains("gerry") ||
                    objId.ToLower().Contains("companion") ||
                    customTag.ToLower().Contains("skull") ||
                    customTag.ToLower().Contains("gerry") ||
                    objName.ToLower().Contains("skull") ||
                    objName.ToLower().Contains("gerry");

                if (isPotentialGerry)
                {
                    CoopMod.Logger.LogWarning($"[GERRY?] WGO Created - ID: '{objId}', Tag: '{customTag}', Name: '{objName}'");
                }
                else
                {
                    // Log all WGOs but at Info level
                    CoopMod.Logger.LogInfo($"[WGO] Created - ID: '{objId}', Tag: '{customTag}', Name: '{objName}'");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error in WGO Awake logging: {ex.Message}");
            }
        }

        /// <summary>
        /// Logs character game objects specifically (Gerry might be a CharacterGameObject).
        /// DISABLED - causes performance issues
        /// </summary>
        // [HarmonyPatch(typeof(CharacterGameObject), "Awake")]
        // [HarmonyPostfix]
        public static void CharacterGameObject_Awake_Postfix(CharacterGameObject __instance)
        {
            try
            {
                if (__instance == null) return;

                string objId = __instance.obj_id ?? "null";
                string customTag = __instance.custom_tag ?? "null";
                string objName = __instance.gameObject?.name ?? "null";
                Vector3 pos = __instance.transform?.position ?? Vector3.zero;
                
                CoopMod.Logger.LogWarning($"[CHARACTER] Created - ID: '{objId}', Tag: '{customTag}', Name: '{objName}', Pos: {pos}");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error in CharacterGameObject Awake logging: {ex.Message}");
            }
        }

        /// <summary>
        /// Logs NPC data being loaded/created.
        /// DISABLED - not needed anymore
        /// </summary>
        // [HarmonyPatch(typeof(KnownNPC), MethodType.Constructor)]
        // [HarmonyPostfix]
        public static void KnownNPC_Constructor_Postfix(KnownNPC __instance)
        {
            try
            {
                if (__instance == null) return;

                string npcId = __instance.npc_id ?? "null";
                
                bool isPotentialGerry = 
                    npcId.ToLower().Contains("skull") ||
                    npcId.ToLower().Contains("gerry") ||
                    npcId.ToLower().Contains("companion");

                if (isPotentialGerry || npcId != "player")
                {
                    CoopMod.Logger.LogWarning($"[NPC] Created - NPC ID: '{npcId}'");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error in KnownNPC constructor logging: {ex.Message}");
            }
        }
    }
}
