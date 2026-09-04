using System;
using System.Collections.Generic;

namespace GraveyardKeeperCoop.Multiplayer
{
    public static partial class DebugChatCommands
    {
        private static void ExecuteTestCommand(
            List<string> args,
            Action<string> respond)
        {
            if (args.Count != 2 || !IsWord(args[1], "zone-recovery"))
            {
                respond("Usage: /test zone-recovery");
                return;
            }

            ZoneNavSync sync = ZoneNavSync.Instance;
            if (sync == null)
            {
                respond("Zone recovery test is unavailable because ZoneNavSync " +
                        "is not initialized.");
                return;
            }

            if (!sync.ArmZoneRecoveryTest(out string error))
            {
                respond(error);
                return;
            }

            respond("Zone recovery test armed. Have the host discover a zone " +
                    "this client does not know, then keep the map open.");
        }

        private static string[] CompleteTestCommand(CompletionContext context)
        {
            return context.ArgumentIndex == 1
                ? MatchPrefix(context.Partial, new[] { "zone-recovery" })
                : null;
        }
    }
}
