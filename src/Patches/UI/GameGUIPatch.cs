using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches GameGUI to add a new "Chat" tab alongside Map, Inventory, Techs, NPCs
    /// </summary>
    [HarmonyPatch(typeof(GameGUI), "Init")]
    public static class GameGUIPatch
    {
        static void Postfix(GameGUI __instance)
        {
            try
            {
                CoopMod.Logger.LogInfo("[GameGUIPatch] Adding Chat tab to GameGUI...");

                // Create ChatGUI if it doesn't exist
                if (GraveyardKeeperCoop.UI.ChatGUI.Instance == null)
                {
                    GraveyardKeeperCoop.UI.ChatGUI.Create();
                }

                var chatGUI = GraveyardKeeperCoop.UI.ChatGUI.Instance;
                if (chatGUI == null)
                {
                    CoopMod.Logger.LogError("[GameGUIPatch] Failed to create ChatGUI!");
                    return;
                }

                // Initialize ChatGUI
                chatGUI.Init();

                // Access the private TABS dictionary via reflection
                var tabsField = AccessTools.Field(typeof(GameGUI), "TABS");
                var tabs = (Dictionary<GameGUI.TabType, BaseGameGUI>)tabsField.GetValue(__instance);

                // Access the private _tabs dictionary via reflection
                var _tabsField = AccessTools.Field(typeof(GameGUI), "_tabs");
                var _tabs = (Dictionary<GameGUI.TabType, GameTabItemGUI>)_tabsField.GetValue(__instance);

                // Access the _tabs_grid
                var _tabsGridField = AccessTools.Field(typeof(GameGUI), "_tabs_grid");
                var _tabs_grid = (UITable)_tabsGridField.GetValue(__instance);

                if (tabs == null || _tabs == null || _tabs_grid == null)
                {
                    CoopMod.Logger.LogError("[GameGUIPatch] Could not access GameGUI private fields!");
                    return;
                }

                // Check if we already added Chat tab
                // We need to use an integer value that doesn't conflict with existing enum values
                // Inventory=0, Techs=1, NPCs=2, Bodies=3, Map=4
                // We'll use 5 for Chat
                GameGUI.TabType chatTabType = (GameGUI.TabType)5;

                if (tabs.ContainsKey(chatTabType))
                {
                    CoopMod.Logger.LogInfo("[GameGUIPatch] Chat tab already exists, skipping");
                    return;
                }

                // Add Chat to TABS dictionary
                tabs.Add(chatTabType, chatGUI);
                CoopMod.Logger.LogInfo("[GameGUIPatch] Added Chat to TABS dictionary");

                // Find an existing tab button to clone
                if (_tabs.Count == 0)
                {
                    CoopMod.Logger.LogError("[GameGUIPatch] No existing tabs found to clone!");
                    return;
                }

                GameTabItemGUI templateTab = _tabs.Values.First();
                
                // Clone the tab button
                GameObject chatTabObj = Object.Instantiate(templateTab.gameObject);
                chatTabObj.name = "ChatTab";
                chatTabObj.transform.SetParent(_tabs_grid.transform, false);
                chatTabObj.transform.localScale = Vector3.one;

                // Get the GameTabItemGUI component
                GameTabItemGUI chatTab = chatTabObj.GetComponent<GameTabItemGUI>();
                if (chatTab == null)
                {
                    CoopMod.Logger.LogError("[GameGUIPatch] Failed to get GameTabItemGUI component from cloned tab!");
                    Object.Destroy(chatTabObj);
                    return;
                }

                // Initialize the tab
                chatTab.Init(chatTabType, chatGUI);

                // Remove the LocalizedLabel component that keeps resetting the text
                var localizedLabel = chatTabObj.GetComponent<LocalizedLabel>();
                if (localizedLabel != null)
                {
                    Object.Destroy(localizedLabel);
                    CoopMod.Logger.LogInfo("[GameGUIPatch] Removed LocalizedLabel component");
                }

                // Override the label text (since "tab_5" doesn't have a localization string)
                var labelField = AccessTools.Field(typeof(GameTabItemGUI), "_label");
                var label = (UILabel)labelField.GetValue(chatTab);
                if (label != null)
                {
                    label.text = "Chat";
                    CoopMod.Logger.LogInfo("[GameGUIPatch] Set tab label to 'Chat'");
                }

                // Add to _tabs dictionary
                _tabs.Add(chatTabType, chatTab);

                // Reposition the tabs grid
                _tabs_grid.Reposition();

                CoopMod.Logger.LogInfo($"[GameGUIPatch] Chat tab added successfully! Total tabs: {_tabs.Count}");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[GameGUIPatch] Error adding Chat tab: {ex.Message}");
                CoopMod.Logger.LogError($"Stack trace: {ex.StackTrace}");
            }
        }
    }

    /// <summary>
    /// Extension to add Chat to the TabType enum
    /// Note: We can't actually extend the enum, but we can cast an integer to it
    /// </summary>
    public static class GameGUIExtensions
    {
        public const int CHAT_TAB_VALUE = 5;
        
        public static GameGUI.TabType GetChatTabType()
        {
            return (GameGUI.TabType)CHAT_TAB_VALUE;
        }
    }
}
