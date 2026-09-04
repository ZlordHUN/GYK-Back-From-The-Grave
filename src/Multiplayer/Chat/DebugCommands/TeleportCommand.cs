using System;
using System.Collections.Generic;
using GraveyardKeeperCoop.Network;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    public static partial class DebugChatCommands
    {
        private static void ExecuteTeleportCommand(
            List<string> args,
            Action<string> respond)
        {
            if (args.Count != 2)
            {
                respond("Usage: /tp <player>");
                return;
            }

            string requestedName = args[1];
            WorldGameObject targetPlayer = FindPlayerByName(requestedName);
            if (targetPlayer == null || targetPlayer.transform == null)
            {
                respond($"Player not found: {requestedName}");
                return;
            }

            WorldGameObject localPlayer = MainGame.me?.player;
            BaseCharacterComponent localCharacter = localPlayer?.components?.character;
            if (localPlayer == null || localCharacter == null)
            {
                respond("Teleport is unavailable before the local player is ready.");
                return;
            }

            if (localPlayer.dont_update)
            {
                respond("Teleport is unavailable during the current transition.");
                return;
            }

            if (!localCharacter.control_enabled)
            {
                respond("Teleport is unavailable while player control is disabled.");
                return;
            }

            // Resolve the proxy's current position before the fade starts. Each peer
            // owns its local player, so this moves only the command issuer; the
            // existing position channel then publishes the new location to peers.
            Vector3 targetPosition = targetPlayer.transform.position;
            Vector2 targetGridPosition = targetPosition / 96f;
            GJCommons.VoidDelegate onPositionChanged = delegate
            {
                Vector3 actualPosition = localPlayer.transform.position;
                MainGame.me.player_pos = actualPosition;
                SteamP2PManager.Instance?.SendPositionWithState(
                    actualPosition,
                    localCharacter.direction,
                    false,
                    Vector2.zero);

                CoopMod.Logger.LogInfo(
                    $"[DebugCommands] Teleported local player to " +
                    $"'{requestedName}' at {actualPosition}");
                respond($"Teleported to {requestedName}.");
            };

            localCharacter.TeleportWithFade(
                targetGridPosition,
                onPositionChanged,
                null);
        }

        private static string[] CompleteTeleportCommand(
            CompletionContext context)
        {
            return context.ArgumentIndex == 1
                ? MatchPrefix(context.Partial, GetOnlinePlayerNames())
                : null;
        }
    }
}
