using System;
using System.Collections.Generic;

namespace GraveyardKeeperCoop.Multiplayer
{
    public static partial class DebugChatCommands
    {
        private const int MaxCorpseSpawnCount = 20;

        private static void ExecuteSpawnCommand(
            List<string> args,
            Action<string> respond)
        {
            if (args.Count < 2 || !IsWord(args[1], "corpse"))
            {
                respond("Usage: /spawn corpse [amount]");
                return;
            }

            int amount = 1;
            if (args.Count > 2 && !TryParseInt(args[2], out amount))
            {
                respond($"Invalid amount: {args[2]}");
                return;
            }

            if (amount < 1 || amount > MaxCorpseSpawnCount)
            {
                respond($"Amount must be from 1 to {MaxCorpseSpawnCount}.");
                return;
            }

            WorldGameObject player = MainGame.me?.player;
            GameSave save = MainGame.me?.save;
            if (player == null || save == null || MainGame.me.world_root == null)
            {
                respond("Corpses cannot be spawned before the game world is ready.");
                return;
            }

            int spawned = 0;
            for (int i = 0; i < amount; i++)
            {
                Item corpse = save.GenerateBody(1, 3);
                if (corpse == null || corpse.definition == null)
                    break;

                player.DropItem(corpse, Direction.None);
                spawned++;
            }

            if (spawned == 0)
            {
                respond("Failed to generate a corpse.");
                return;
            }

            respond(spawned == 1
                ? "Spawned 1 corpse."
                : $"Spawned {spawned} corpses.");
            CoopMod.Logger.LogInfo(
                $"[DebugCommands] Spawned {spawned} complete corpse(s) near " +
                "the local player");
        }

        private static string[] CompleteSpawnCommand(CompletionContext context)
        {
            return context.ArgumentIndex == 1
                ? MatchPrefix(context.Partial, new[] { "corpse" })
                : null;
        }
    }
}
