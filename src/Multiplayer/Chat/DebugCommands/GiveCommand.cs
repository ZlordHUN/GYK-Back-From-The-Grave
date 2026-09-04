using System;
using System.Collections.Generic;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.Utils;
using Steamworks;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    public static partial class DebugChatCommands
    {
        private const int MaxMoneyGrantBronze = 10000000;
        private const int MaxItemGrantAmount = 999;
        private const int MaxGiveItemIdCharacters = 128;
        private const int MaxGiveGrantPacketBytes = 256;
        private const int MaxGiveResultPacketBytes = 768;
        private const int MaxGiveResultCharacters = 512;
        private const string BronzeBeerItemId = "cup_beer:1";

        private static void ExecuteGiveCommand(
            List<string> args,
            Action<string> respond)
        {
            // /give <item_id> [amount]
            // /give <player> <item_id> [amount]
            if (args.Count < 2)
            {
                respond("Usage: /give <item_id> [amount]  or  " +
                        "/give <player> <item_id> [amount]");
                return;
            }

            WorldGameObject targetPlayer = MainGame.me?.player;
            int itemIdIndex = 1;
            int amountIndex = 2;

            string possiblePlayer = args[1];
            WorldGameObject namedPlayer = IsReservedGiveItemToken(possiblePlayer)
                ? null
                : FindPlayerByName(possiblePlayer);
            if (namedPlayer != null)
            {
                if (args.Count < 3)
                {
                    respond($"Usage: /give {possiblePlayer} <item_id> [amount]");
                    return;
                }

                targetPlayer = namedPlayer;
                itemIdIndex = 2;
                amountIndex = 3;
            }

            string requestedItem = args[itemIdIndex];
            if (!ResolveItemAlias(
                    requestedItem,
                    out string itemId,
                    out string friendlyName,
                    out string resolutionError))
            {
                respond(resolutionError);
                return;
            }

            int amount = 1;
            if (args.Count > amountIndex &&
                !TryParseInt(args[amountIndex], out amount))
            {
                respond($"Invalid amount: {args[amountIndex]}");
                return;
            }

            if (targetPlayer == null)
            {
                respond("No target player found.");
                return;
            }

            if (IsWord(itemId, "money"))
            {
                int bronze = Mathf.Clamp(amount, 1, MaxMoneyGrantBronze);
                ExecuteGiveMoney(targetPlayer, bronze, respond);
                return;
            }

            if (IsWord(itemId, "energy"))
            {
                ExecuteGiveEnergy(targetPlayer, amount, respond);
                return;
            }

            amount = Mathf.Max(1, Mathf.Min(amount, MaxItemGrantAmount));

            if (!TryCreateGrantItem(itemId, amount, out Item item, out string itemError))
            {
                respond(itemError);
                return;
            }

            if (targetPlayer == MainGame.me?.player)
            {
                if (TryAddGrantItemToLocalPlayer(item, itemId, out string addError))
                {
                    respond($"Gave {amount}x {friendlyName} [{itemId}] to You.");
                }
                else
                {
                    respond($"Failed to add {itemId} to inventory: {addError}");
                }
                return;
            }

            if (!TryGetRemotePlayerSteamId(targetPlayer, out CSteamID targetId))
            {
                respond("That player's network session is no longer available.");
                return;
            }

            if (!TrySendRemoteItemGrant(
                    targetId,
                    itemId,
                    amount,
                    out bool hostSentGrant))
            {
                respond("Failed to send the item grant. Check that the player is " +
                        "still connected and try again.");
                return;
            }

            string targetName = GetGivePlayerName(targetId);
            respond(hostSentGrant
                ? $"Sent {amount}x {friendlyName} [{itemId}] to {targetName}."
                : $"Requested {amount}x {friendlyName} [{itemId}] for " +
                  $"{targetName}; the host will authorize it.");
        }

        private static bool TryCreateGrantItem(
            string itemId,
            int amount,
            out Item item,
            out string error)
        {
            item = null;
            error = null;
            if (string.IsNullOrWhiteSpace(itemId) ||
                itemId.Length > MaxGiveItemIdCharacters ||
                IsWord(itemId, "money") || IsWord(itemId, "energy"))
            {
                error = $"Unknown item: {itemId}";
                return false;
            }

            if (amount < 1 || amount > MaxItemGrantAmount)
            {
                error = $"Invalid item amount: {amount}";
                return false;
            }

            try
            {
                item = new Item(itemId, amount);
                if (item?.definition != null)
                    return true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[DebugCommands] Could not construct give item '{itemId}': " +
                    ex.Message);
            }

            item = null;
            error = $"Unknown item: {itemId}";
            return false;
        }

        private static bool TryGetRemotePlayerSteamId(
            WorldGameObject targetPlayer,
            out CSteamID targetId)
        {
            targetId = CSteamID.Nil;
            List<KeyValuePair<CSteamID, PlayerComponent>> remotes =
                OnlineCoopManager.Instance?.GetRemotePlayersSnapshot();
            if (targetPlayer == null || remotes == null)
                return false;

            for (int i = 0; i < remotes.Count; i++)
            {
                if (ReferenceEquals(remotes[i].Value?.wgo, targetPlayer))
                {
                    targetId = remotes[i].Key;
                    return targetId != CSteamID.Nil;
                }
            }

            return false;
        }

        private static bool TrySendRemoteItemGrant(
            CSteamID targetId,
            string itemId,
            int amount,
            out bool hostSentGrant)
        {
            hostSentGrant = false;
            SteamLobbyManager lobby = SteamLobbyManager.Instance;
            OnlineCoopManager online = OnlineCoopManager.Instance;
            SteamP2PManager p2p = SteamP2PManager.Instance;
            if (lobby == null || !lobby.IsInLobby ||
                online == null || !online.IsOnlineCoopEnabled ||
                p2p == null ||
                targetId == CSteamID.Nil ||
                online.GetRemotePlayer(targetId) == null ||
                !p2p.IsCurrentLobbyMember(targetId))
            {
                return false;
            }

            if (lobby.IsHost)
            {
                if (!online.IsHost ||
                    !lobby.IsPeerCompatibilityValidated(targetId))
                {
                    return false;
                }

                hostSentGrant = true;
                return p2p.SendDebugGiveGrant(targetId, itemId, amount);
            }

            if (online.IsHost)
                return false;

            CSteamID hostId = lobby.GetLobbyOwner();
            if (hostId == CSteamID.Nil || hostId == SteamUser.GetSteamID() ||
                !p2p.IsCurrentLobbyMember(hostId))
            {
                return false;
            }

            return p2p.SendDebugGiveRequest(targetId, itemId, amount);
        }

        internal static void HandleGiveRequestMessage(
            CSteamID senderId,
            ref MsgReader reader)
        {
            if (!TryReadGiveRequest(
                    senderId,
                    ref reader,
                    out CSteamID targetId,
                    out string itemId,
                    out int amount))
            {
                return;
            }

            SteamLobbyManager lobby = SteamLobbyManager.Instance;
            OnlineCoopManager online = OnlineCoopManager.Instance;
            SteamP2PManager p2p = SteamP2PManager.Instance;
            if (lobby == null || !lobby.IsInLobby || !lobby.IsHost ||
                online == null || !online.IsOnlineCoopEnabled || !online.IsHost ||
                !CanUseGameState() || senderId == SteamUser.GetSteamID() ||
                !p2p.IsCurrentLobbyMember(senderId) ||
                !lobby.IsPeerCompatibilityValidated(senderId) ||
                online.GetRemotePlayer(senderId) == null)
            {
                CoopMod.Logger.LogWarning(
                    $"[DebugCommands] Rejected /give request outside an active " +
                    $"authenticated host session: sender={senderId}");
                return;
            }

            if (ModConfig.EnableCheats == null || !ModConfig.EnableCheats.Value)
            {
                SendGiveRequestResult(
                    senderId,
                    false,
                    "Give request rejected: cheats are disabled by the host.");
                CoopMod.Logger.LogWarning(
                    $"[DebugCommands] Rejected /give request from {senderId}: " +
                    "host cheats are disabled");
                return;
            }

            CSteamID localId = SteamUser.GetSteamID();
            bool targetsHost = targetId == localId;
            if (targetId == CSteamID.Nil ||
                !p2p.IsCurrentLobbyMember(targetId) ||
                (!targetsHost &&
                 (!lobby.IsPeerCompatibilityValidated(targetId) ||
                  online.GetRemotePlayer(targetId) == null)))
            {
                SendGiveRequestResult(
                    senderId,
                    false,
                    "Give request rejected: the target player is no longer active.");
                return;
            }

            if (!TryCreateGrantItem(itemId, amount, out Item item, out string error))
            {
                SendGiveRequestResult(
                    senderId,
                    false,
                    "Give request rejected: " + error);
                return;
            }

            string friendlyName = GetGrantItemDisplayName(item, itemId);
            if (targetsHost)
            {
                if (!TryAddGrantItemToLocalPlayer(
                        item,
                        itemId,
                        out string addError))
                {
                    SendGiveRequestResult(
                        senderId,
                        false,
                        $"Give request failed for {GetGivePlayerName(localId)}: " +
                        addError);
                    return;
                }

                string success =
                    $"Host granted {amount}x {friendlyName} [{itemId}] to " +
                    $"{GetGivePlayerName(localId)}.";
                SendGiveRequestResult(senderId, true, success);
                ChatManager.AddMessage(
                    $"[System] Received {amount}x {friendlyName} [{itemId}] " +
                    $"from {GetGivePlayerName(senderId)}'s /give command.");
                CoopMod.Logger.LogInfo(
                    $"[DebugCommands] Applied authenticated /give request locally: " +
                    $"sender={senderId}, item='{itemId}', amount={amount}");
                return;
            }

            bool forwarded = p2p.SendDebugGiveGrant(targetId, itemId, amount);
            string targetName = GetGivePlayerName(targetId);
            SendGiveRequestResult(
                senderId,
                forwarded,
                forwarded
                    ? $"Host approved {amount}x {friendlyName} [{itemId}] for " +
                      $"{targetName}."
                    : $"Give request failed: could not reach {targetName}.");
            CoopMod.Logger.LogInfo(
                $"[DebugCommands] Host {(forwarded ? "forwarded" : "failed to forward")} " +
                $"/give request: sender={senderId}, target={targetId}, " +
                $"item='{itemId}', amount={amount}");
        }

        internal static void HandleGiveGrantMessage(
            CSteamID senderId,
            ref MsgReader reader)
        {
            if (!TryReadGiveGrant(
                    senderId,
                    ref reader,
                    out string itemId,
                    out int amount))
            {
                return;
            }

            SteamLobbyManager lobby = SteamLobbyManager.Instance;
            OnlineCoopManager online = OnlineCoopManager.Instance;
            SteamP2PManager p2p = SteamP2PManager.Instance;
            CSteamID hostId = lobby?.GetLobbyOwner() ?? CSteamID.Nil;
            if (lobby == null || !lobby.IsInLobby || lobby.IsHost ||
                online == null || !online.IsOnlineCoopEnabled || online.IsHost ||
                !CanUseGameState() || hostId == CSteamID.Nil ||
                senderId != hostId || !p2p.IsCurrentLobbyMember(senderId) ||
                !lobby.IsPeerCompatibilityValidated(senderId) ||
                online.GetRemotePlayer(senderId) == null)
            {
                CoopMod.Logger.LogWarning(
                    $"[DebugCommands] Rejected unauthorized DebugGiveGrant from " +
                    $"{senderId}; expected current host {hostId}");
                return;
            }

            if (!TryApplyItemGrantToLocalPlayer(
                    itemId,
                    amount,
                    out string friendlyName,
                    out string error))
            {
                ChatManager.AddMessage($"[System] Remote item grant failed: {error}");
                CoopMod.Logger.LogWarning(
                    $"[DebugCommands] Host-authenticated item grant failed locally: " +
                    $"item='{itemId}', amount={amount}, error='{error}'");
                return;
            }

            ChatManager.AddMessage(
                $"[System] Received {amount}x {friendlyName} [{itemId}] " +
                "from a /give command.");
            CoopMod.Logger.LogInfo(
                $"[DebugCommands] Applied host-authenticated item grant to local " +
                $"player: item='{itemId}', amount={amount}");
        }

        internal static void HandleGiveResultMessage(
            CSteamID senderId,
            ref MsgReader reader)
        {
            bool accepted;
            string message;
            try
            {
                if (reader.Length > MaxGiveResultPacketBytes || reader.Remaining < 2)
                    throw new InvalidOperationException("invalid result packet size");

                accepted = reader.ReadBool();
                message = reader.ReadString();
                if (reader.Remaining != 0 ||
                    string.IsNullOrWhiteSpace(message) ||
                    message.Length > MaxGiveResultCharacters)
                {
                    throw new InvalidOperationException("invalid result payload");
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[DebugCommands] Dropped malformed DebugGiveResult from " +
                    $"{senderId}: {ex.Message}");
                return;
            }

            SteamLobbyManager lobby = SteamLobbyManager.Instance;
            OnlineCoopManager online = OnlineCoopManager.Instance;
            CSteamID hostId = lobby?.GetLobbyOwner() ?? CSteamID.Nil;
            if (lobby == null || !lobby.IsInLobby || lobby.IsHost ||
                online == null || !online.IsOnlineCoopEnabled || online.IsHost ||
                senderId != hostId ||
                !lobby.IsPeerCompatibilityValidated(senderId) ||
                !SteamP2PManager.Instance.IsCurrentLobbyMember(senderId))
            {
                CoopMod.Logger.LogWarning(
                    $"[DebugCommands] Rejected unauthorized DebugGiveResult from " +
                    $"{senderId}; expected current host {hostId}");
                return;
            }

            ChatManager.AddMessage("[System] " + message);
            CoopMod.Logger.LogInfo(
                $"[DebugCommands] Host /give request result: accepted={accepted}, " +
                $"message='{message}'");
        }

        private static bool TryReadGiveRequest(
            CSteamID senderId,
            ref MsgReader reader,
            out CSteamID targetId,
            out string itemId,
            out int amount)
        {
            targetId = CSteamID.Nil;
            itemId = null;
            amount = 0;
            try
            {
                if (reader.Length > MaxGiveGrantPacketBytes || reader.Remaining < 13)
                    throw new InvalidOperationException("invalid request packet size");

                targetId = new CSteamID(reader.ReadUInt64());
                amount = reader.ReadInt32();
                itemId = reader.ReadString();
                if (reader.Remaining != 0 ||
                    !IsValidGiveWireItem(itemId, amount))
                {
                    throw new InvalidOperationException("invalid request payload");
                }

                return true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[DebugCommands] Dropped malformed DebugGiveRequest from " +
                    $"{senderId}: {ex.Message}");
                return false;
            }
        }

        private static bool TryReadGiveGrant(
            CSteamID senderId,
            ref MsgReader reader,
            out string itemId,
            out int amount)
        {
            itemId = null;
            amount = 0;
            try
            {
                if (reader.Length > MaxGiveGrantPacketBytes || reader.Remaining < 5)
                    throw new InvalidOperationException("invalid grant packet size");

                amount = reader.ReadInt32();
                itemId = reader.ReadString();
                if (reader.Remaining != 0 ||
                    !IsValidGiveWireItem(itemId, amount))
                {
                    throw new InvalidOperationException("invalid grant payload");
                }

                return true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[DebugCommands] Dropped malformed DebugGiveGrant from " +
                    $"{senderId}: {ex.Message}");
                return false;
            }
        }

        private static bool IsValidGiveWireItem(string itemId, int amount)
        {
            return !string.IsNullOrWhiteSpace(itemId) &&
                   itemId.Length <= MaxGiveItemIdCharacters &&
                   amount >= 1 && amount <= MaxItemGrantAmount &&
                   !IsWord(itemId, "money") && !IsWord(itemId, "energy");
        }

        private static bool TryApplyItemGrantToLocalPlayer(
            string itemId,
            int amount,
            out string friendlyName,
            out string error)
        {
            friendlyName = itemId;
            error = null;
            WorldGameObject localPlayer = MainGame.me?.player;
            if (localPlayer == null)
            {
                error = "the local player is not ready.";
                return false;
            }

            if (!TryCreateGrantItem(itemId, amount, out Item item, out error))
                return false;

            friendlyName = GetGrantItemDisplayName(item, itemId);
            return TryAddGrantItemToLocalPlayer(item, itemId, out error);
        }

        private static bool TryAddGrantItemToLocalPlayer(
            Item item,
            string itemId,
            out string error)
        {
            error = null;
            WorldGameObject localPlayer = MainGame.me?.player;
            if (localPlayer?.data?.inventory == null ||
                MainGame.me?.save?.quests == null ||
                GameBalance.me == null ||
                GUIElements.me?.hud?.toolbar == null)
            {
                error = "the local inventory UI is not ready.";
                return false;
            }

            try
            {
                if (localPlayer.AddToInventory(item))
                    return true;

                error = $"inventory is full or cannot accept {itemId}.";
                return false;
            }
            catch (Exception ex)
            {
                // AddToInventory touches quests, HUD, and auto-use behavior after
                // mutating data. Never retry automatically after an exception: the
                // item may already have been added.
                error = $"an error occurred while applying {itemId}; check the " +
                        "inventory before trying again.";
                CoopMod.Logger.LogWarning(
                    $"[DebugCommands] Local item grant threw after validation: " +
                    $"item='{itemId}', error='{ex}'");
                return false;
            }
        }

        private static string GetGrantItemDisplayName(Item item, string itemId)
        {
            try
            {
                if (GiveItemCatalog.TryResolve(
                        itemId,
                        out string resolvedId,
                        out string friendlyName,
                        out _) &&
                    string.Equals(
                        resolvedId,
                        itemId,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(friendlyName))
                {
                    return friendlyName.Trim();
                }

                string displayName = item?.definition?.GetItemName(true);
                return string.IsNullOrWhiteSpace(displayName)
                    ? itemId
                    : displayName.Trim();
            }
            catch
            {
                return itemId;
            }
        }

        private static string GetGivePlayerName(CSteamID playerId)
        {
            string playerName = playerId == SteamUser.GetSteamID()
                ? SteamFriends.GetPersonaName()
                : SteamFriends.GetFriendPersonaName(playerId);
            return string.IsNullOrWhiteSpace(playerName)
                ? playerId.ToString()
                : playerName;
        }

        private static void SendGiveRequestResult(
            CSteamID requester,
            bool accepted,
            string message)
        {
            if (!SteamP2PManager.Instance.SendDebugGiveResult(
                    requester,
                    accepted,
                    message))
            {
                CoopMod.Logger.LogWarning(
                    $"[DebugCommands] Failed to send /give request result to " +
                    requester);
            }
        }

        private static void ExecuteGiveMoney(
            WorldGameObject targetPlayer,
            int bronze,
            Action<string> respond)
        {
            if (targetPlayer != MainGame.me?.player)
            {
                respond("Money is personal; that player must run " +
                        "/give money locally.");
                return;
            }

            if (targetPlayer?.data == null)
            {
                respond("Money cannot be changed before the game world is ready.");
                return;
            }

            float amount = bronze / 100f;
            targetPlayer.data.money += amount;
            DropCollectGUI.OnMoneyCollected(amount);

            respond($"Added {FormatMoneyFromBronze(bronze)} to your balance. " +
                    $"Balance: {Trading.FormatMoney(targetPlayer.data.money, true, true)}.");
            CoopMod.Logger.LogInfo(
                $"[DebugCommands] Added {bronze} bronze to local money; " +
                $"balance={targetPlayer.data.money:F2}");
        }

        /// <summary>
        /// Converts a raw ID or friendly item alias to the authoritative game ID.
        /// </summary>
        private static bool ResolveItemAlias(
            string itemNameOrId,
            out string itemId,
            out string friendlyName,
            out string error)
        {
            if (IsWord(itemNameOrId, "money"))
            {
                itemId = "money";
                friendlyName = "money";
                error = null;
                return true;
            }

            if (IsWord(itemNameOrId, "energy"))
            {
                itemId = "energy";
                friendlyName = "energy";
                error = null;
                return true;
            }

            // Preserve the original shorthand even if localization data has not
            // finished loading yet.
            if (IsWord(itemNameOrId, "beer"))
            {
                itemId = BronzeBeerItemId;
                friendlyName = "Beer (bronze)";
                error = null;
                return true;
            }

            return GiveItemCatalog.TryResolve(
                itemNameOrId,
                out itemId,
                out friendlyName,
                out error);
        }

        private static bool IsReservedGiveItemToken(string token)
        {
            if (IsWord(token, "beer") || IsWord(token, "energy") ||
                IsWord(token, "money"))
            {
                return true;
            }

            // Ambiguous aliases are still item tokens. This prevents a player with
            // the same name from silently changing how /give parses the command.
            return GiveItemCatalog.IsRecognizedToken(token);
        }

        private static void ExecuteGiveEnergy(
            WorldGameObject targetPlayer,
            int requestedAmount,
            Action<string> respond)
        {
            if (targetPlayer != MainGame.me?.player)
            {
                respond("Energy is personal; that player must run " +
                        "/give energy locally.");
                return;
            }

            if (targetPlayer?.data == null || MainGame.me?.save == null)
            {
                respond("Energy cannot be changed before the game world is ready.");
                return;
            }

            float previousEnergy = targetPlayer.energy;
            float maxEnergy = Mathf.Max(0f, MainGame.me.save.max_energy);
            float newEnergy = Mathf.Clamp(
                previousEnergy + requestedAmount,
                0f,
                maxEnergy);
            targetPlayer.energy = newEnergy;

            float appliedAmount = newEnergy - previousEnergy;
            if (!Mathf.Approximately(appliedAmount, 0f))
                EffectBubblesManager.ShowStackedEnergy(targetPlayer, appliedAmount);

            if (Mathf.Approximately(appliedAmount, 0f))
            {
                respond($"Energy unchanged at {newEnergy:0.##}/{maxEnergy:0.##}.");
            }
            else
            {
                string action = appliedAmount < 0f ? "Removed" : "Added";
                respond($"{action} {Mathf.Abs(appliedAmount):0.##} energy. " +
                        $"Energy: {newEnergy:0.##}/{maxEnergy:0.##}.");
            }

            CoopMod.Logger.LogInfo(
                $"[DebugCommands] Changed local energy by {appliedAmount:F2} " +
                $"(requested={requestedAmount}, " +
                $"energy={newEnergy:F2}/{maxEnergy:F2})");
        }

        private static string FormatMoneyFromBronze(int bronze)
        {
            return Trading.FormatMoney(bronze / 100f, true, true);
        }

        private static string[] CompleteGiveCommand(CompletionContext context)
        {
            if (context.ArgumentIndex == 1)
            {
                return MergeCandidates(
                    GetGiveItemCompletionNames(context.Partial),
                    MatchPrefix(context.Partial, GetOnlinePlayerNames()));
            }

            if (context.ArgumentIndex == 2 &&
                context.Args.Count > 1 &&
                !IsReservedGiveItemToken(context.Args[1]) &&
                FindPlayerByName(context.Args[1]) != null)
            {
                return GetRemoteGiveItemCompletionNames(context.Partial);
            }

            return null;
        }

        /// <summary>
        /// Returns unique, friendly aliases for every definition whose ID or
        /// player-facing name matches the current token.
        /// </summary>
        private static string[] GetGiveItemCompletionNames(string partial)
        {
            return GiveItemCatalog.GetCompletionCandidates(
                partial,
                includePersonalValues: true);
        }

        private static string[] GetRemoteGiveItemCompletionNames(string partial)
        {
            return GiveItemCatalog.GetCompletionCandidates(
                partial,
                includePersonalValues: false);
        }

        private static string GetGiveCompletionDisplay(string candidate)
        {
            return GiveItemCatalog.TryGetCompletionDisplay(
                candidate,
                out string displayText)
                ? displayText
                : candidate;
        }
    }
}
