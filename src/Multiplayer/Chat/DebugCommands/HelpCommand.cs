using System;
using System.Collections.Generic;

namespace GraveyardKeeperCoop.Multiplayer
{
    public static partial class DebugChatCommands
    {
        private static void ExecuteHelpCommand(
            List<string> args,
            Action<string> respond)
        {
            string subcommand = args.Count > 1
                ? NormalizeCommand(args[1])
                : string.Empty;

            if (subcommand == "time")
            {
                respond("/time set <time> — set time (e.g. 12:00, 0.5)");
                respond("/time set <day> <time> — set day and time");
                respond("/time status — show current day/time");
            }
            else if (subcommand == "weather")
            {
                respond("/weather set preset <name> — set weather preset");
                respond("/weather set rain|fog|wind <0-5> — override weather value");
                respond("/weather clear — clear all forced weather");
                respond("/weather reset — reset to daily defaults");
            }
            else if (subcommand == "give")
            {
                respond("/give <item_name|item_id> [amount] — give item to yourself");
                respond("/give <player> <item_name|item_id> [amount] — give item to player");
                respond("/give money <bronze> — add to your personal balance");
                respond("/give energy <signed amount> — change your personal energy");
                respond("/give beer [amount] — give yourself bronze beer");
                respond("Item suggestions use localized names and distinguish " +
                        "bronze, silver, and gold variants.");
                respond("Examples: /give beer, /give energy -25, " +
                        "/give PlayerName gold_beer 5");
            }
            else if (subcommand == "spawn")
            {
                respond("/spawn corpse [amount] — spawn complete corpses near you");
                respond($"Amount is limited to {MaxCorpseSpawnCount}.");
            }
            else if (subcommand == "test")
            {
                respond("/test zone-recovery — client: drop the next host zone event " +
                        "and immediate snapshot to verify periodic map recovery");
            }
            else if (subcommand == "tp")
            {
                respond("/tp <player> — teleport yourself to that player");
            }
            else
            {
                respond("/help [command] — show command help\n" +
                        "/time — set or view time of day\n" +
                        "/weather — set weather, rain, fog, wind\n" +
                        "/give — give items to yourself or others\n" +
                        "/spawn — spawn world objects near you\n" +
                        "/test — run a controlled multiplayer recovery test\n" +
                        "/tp — teleport yourself to another player\n" +
                        "Use /help <command> for details. Cheats can be " +
                        "enabled in Multiplayer Settings.");
            }
        }
    }
}
