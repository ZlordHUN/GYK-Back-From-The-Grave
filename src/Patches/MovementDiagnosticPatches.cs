using HarmonyLib;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Diagnostic patches to debug Player 2 movement issues.
    /// </summary>
    [HarmonyPatch(typeof(MovementComponent))]
    public class MovementDiagnosticPatches
    {
        private static Vector3 lastPlayer2Position = Vector3.zero;
        private static int frameCounter = 0;
        
        [HarmonyPatch("UpdateMovement", MethodType.Normal)]
        [HarmonyPrefix]
        public static void UpdateMovement_Prefix(MovementComponent __instance, Vector2 dir, float delta_time)
        {
            if (!__instance.wgo.is_player)
                return;
                
            var manager = GraveyardKeeperCoop.LocalCoop.LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;
                
            var player2Char = manager.Player2?.wgo?.components?.character;
            if (player2Char == null)
                return;
                
            // Only log for Player 2
            if (__instance != player2Char)
                return;
                
            frameCounter++;
            if (frameCounter % 60 == 0 && dir.magnitude > 0) // Log once per second when moving
            {
                var body = __instance.body;
                CoopMod.Logger.LogInfo($"[MovementDiag] BEFORE MovePosition: " +
                    $"body={body != null}, " +
                    $"body.position={body?.position}, " +
                    $"transform.position={__instance.wgo.transform.position}, " +
                    $"dir={dir}, " +
                    $"movement_dir={__instance.movement_dir}");
            }
        }
        
        [HarmonyPatch("UpdateMovement", MethodType.Normal)]
        [HarmonyPostfix]
        public static void UpdateMovement_Postfix(MovementComponent __instance)
        {
            if (!__instance.wgo.is_player)
                return;
                
            var manager = GraveyardKeeperCoop.LocalCoop.LocalCoopManager.Instance;
            if (manager == null || !manager.IsLocalCoopEnabled)
                return;
                
            var player2Char = manager.Player2?.wgo?.components?.character;
            if (player2Char == null)
                return;
                
            // Only log for Player 2
            if (__instance != player2Char)
                return;
                
            if (frameCounter % 60 == 0) // Log once per second
            {
                Vector3 currentPos = __instance.wgo.transform.position;
                Vector3 delta = currentPos - lastPlayer2Position;
                
                var body = __instance.body;
                CoopMod.Logger.LogInfo($"[MovementDiag] AFTER MovePosition: " +
                    $"body.position={body?.position}, " +
                    $"transform.position={currentPos}, " +
                    $"delta={delta}, " +
                    $"movement_dir={__instance.movement_dir}, " +
                    $"velocity={__instance.velocity}");
                    
                lastPlayer2Position = currentPos;
            }
        }
    }
}
