using System.Text.RegularExpressions;
using UnityEngine;

namespace GraveyardKeeperCoop.Utils
{
    /// <summary>
    /// Helper utilities for processing and formatting game dialogue text
    /// </summary>
    public static class DialogueHelper
    {
        /// <summary>
        /// Cleans dialogue text by removing color tags, localization markers, and other formatting
        /// </summary>
        public static string CleanDialogueText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            try
            {
                // Apply game's localization first
                string localized = GJL.L(text);

                // Remove NGUI color tags: [c][ffffff]text[-][/c]
                localized = Regex.Replace(localized, @"\[c\]\[[^\]]*\]", "");
                localized = Regex.Replace(localized, @"\[-\]", "");
                localized = Regex.Replace(localized, @"\[/c\]", "");

                // Remove other common tags
                localized = Regex.Replace(localized, @"\[b\]", "");
                localized = Regex.Replace(localized, @"\[/b\]", "");
                localized = Regex.Replace(localized, @"\[i\]", "");
                localized = Regex.Replace(localized, @"\[/i\]", "");

                // Remove any remaining bracket tags
                localized = Regex.Replace(localized, @"\[.*?\]", "");

                // Trim and collapse multiple spaces
                localized = Regex.Replace(localized.Trim(), @"\s+", " ");

                return localized;
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[DialogueHelper] Failed to clean text: {ex.Message}");
                return text;
            }
        }

        /// <summary>
        /// Attempts to resolve a speaker's display name from their unique ID
        /// </summary>
        public static string GetSpeakerName(long speaker_id)
        {
            try
            {
                // Try to find the world object by its unique ID
                if (WorldMap.objs != null)
                {
                    foreach (var wgo in WorldMap.objs)
                    {
                        if (wgo != null && wgo.unique_id == speaker_id)
                        {
                            // Check if it's the player
                            if (wgo.is_player)
                            {
                                // For player, use the game object's name
                                return wgo.name ?? "Player";
                            }

                            // Try to get NPC name from object definition
                            if (wgo.obj_def != null && !string.IsNullOrEmpty(wgo.obj_def.id))
                            {
                                // Clean up the NPC ID for display
                                string npcId = wgo.obj_def.id;
                                
                                // Remove common prefixes
                                npcId = npcId.Replace("npc_", "");
                                npcId = npcId.Replace("enemy_", "");
                                npcId = npcId.Replace("_", " ");
                                
                                // Capitalize first letter of each word
                                return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(npcId);
                            }

                            // Fallback to object ID
                            if (!string.IsNullOrEmpty(wgo.obj_id))
                            {
                                return wgo.obj_id;
                            }
                        }
                    }
                }

                // If we can't find the object, check if it's a GUI element ID
                // (Some dialogue comes from UI elements, not world objects)
                
                // Fallback to generic name
                return "NPC";
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[DialogueHelper] Failed to resolve speaker {speaker_id}: {ex.Message}");
                return "Unknown";
            }
        }

        /// <summary>
        /// Formats a dialogue message for the chat system
        /// </summary>
        public static string FormatNPCDialogue(string speakerName, string dialogueText)
        {
            return $"[NPC] {speakerName}: {dialogueText}";
        }

        /// <summary>
        /// Formats a player choice message for the chat system
        /// </summary>
        public static string FormatPlayerChoice(string playerName, string choiceText)
        {
            return $"[Choice] {playerName}: {choiceText}";
        }

        /// <summary>
        /// Checks if dialogue should be sent to chat based on mod configuration
        /// </summary>
        public static bool ShouldSendToChat()
        {
            // Dialogue to chat is hardcoded enabled while the settings row is hidden.
            // return ModConfig.EnableDialogueToChat?.Value ?? true;
            return true;
        }

        /// <summary>
        /// Checks if NPC dialogue should be captured
        /// </summary>
        public static bool ShouldCaptureNPCDialogue()
        {
            // Show NPC dialogue is hardcoded enabled while the settings row is hidden.
            // return ModConfig.ShowNPCDialogue?.Value ?? true;
            return true;
        }

        /// <summary>
        /// Checks if player choices should be captured
        /// </summary>
        public static bool ShouldCapturePlayerChoices()
        {
            // Show player choices is hardcoded enabled while the settings row is hidden.
            // return ModConfig.ShowPlayerChoices?.Value ?? true;
            return true;
        }

        /// <summary>
        /// Determines if a dialogue bubble type should be logged
        /// </summary>
        public static bool ShouldLogBubbleType(SpeechBubbleGUI.SpeechBubbleType bubbleType)
        {
            // Log Talk and Think bubbles (actual dialogue)
            // Skip InfoBox unless configured otherwise
            switch (bubbleType)
            {
                case SpeechBubbleGUI.SpeechBubbleType.Talk:
                case SpeechBubbleGUI.SpeechBubbleType.Think:
                    return true;
                case SpeechBubbleGUI.SpeechBubbleType.InfoBox:
                    // Could add config option for this
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Prevents message spam by rate limiting
        /// </summary>
        private static float lastMessageTime = 0f;
        private const float MESSAGE_COOLDOWN = 0.1f; // 100ms between messages

        public static bool CanSendMessage()
        {
            float currentTime = Time.realtimeSinceStartup;
            if (currentTime - lastMessageTime < MESSAGE_COOLDOWN)
            {
                return false;
            }
            lastMessageTime = currentTime;
            return true;
        }
    }
}
