using System.Collections.Generic;
using UnityEngine;
using GraveyardKeeperCoopMod.Utils;
using GraveyardKeeperCoop.Utils;
using GraveyardKeeperCoop.LocalCoop;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Patches;
using Steamworks;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Lobby GUI - Created by cloning SaveSlotsMenuGUI for proper rendering
    /// </summary>
    public class LobbyGUI : BaseMenuGUI
    {
        private static LobbyGUI _instance;
        public static LobbyGUI Instance => _instance;
        
        // Track if we're in an active multiplayer session
        private static bool isMultiplayerSessionActive = false;
        public static bool IsMultiplayerSessionActive => isMultiplayerSessionActive;

        public static void MarkMultiplayerSessionActive()
        {
            isMultiplayerSessionActive = true;
            CoopMod.Logger.LogInfo("[LobbyGUI] Multiplayer session marked active");
        }
        
        // Track if this player is the host
        private bool isHost = false;
        // Track local ready state for button text
        private bool isLocalReady = false;
        // Label reference for dynamic button text
        private UILabel startButtonLabel;

        private UILabel titleLabel;
        private PlayerListPanel playerListPanel;
        private ChatPanel chatPanel;
        private SaveSelectorPanel saveSelectorPanel; // Save slot selector
        private GameObject titleObj;
        private GameObject playersHeaderObj;
        private GameObject playersBackgroundObj;
        private GameObject chatHeaderObj;
        private GameObject chatBackgroundObj;
        private GameObject backButtonObj; // Store for click detection
        private GameObject startButtonObj; // Store for click detection
        private GameObject inviteButtonObj; // Store for click detection
        private UIWidget backButtonWidget;
        private UIWidget startButtonWidget;
        private UIWidget inviteButtonWidget;
        private GamepadNavigationItem backNavigationItem;
        private GamepadNavigationItem startNavigationItem;
        private GamepadNavigationItem inviteNavigationItem;
        private ChatInputPanel chatInputPanel;
        private GameObject saveSlotBackgroundTemplate; // Store template for chat input and save selector
        private UIFont bodyFont; // Store body font for chat input
        private Camera uiCamera; // UI Camera for coordinate conversion
        private CSteamID pendingKickTarget = CSteamID.Nil;
        private string pendingKickPlayerName = "";
        private static readonly LobbyResolutionLayout DefaultResolutionLayout = new LobbyResolutionLayout(
            "Default",
            0,
            0,
            Vector3.zero,
            Vector3.one,
            new Vector3(0f, 140f, 0f),
            new Vector3(-200f, 60f, 0f),
            new Vector3(-200f, 15f, 0f),
            Vector3.one,
            new Vector3(-280f, 15f, 0f),
            new Vector3(-200f, -100f, 0f),
            Vector3.one,
            new Vector3(200f, 60f, 0f),
            new Vector3(200f, 15f, 0f),
            Vector3.one,
            200,
            new Vector3(100f, 15f, 0f),
            435f,
            185f,
            -10f,
            -92.5f,
            -10f,
            18f,
            10,
            45,
            new Vector3(200f, -180f, 0f),
            new Vector3(250f, -210f, 0f),
            220,
            35,
            210,
            30,
            new Vector3(-200f, -210f, 0f),
            new Vector3(0f, -210f, 0f),
            new Vector3(-40f, 51f, 0f));
        private static readonly LobbyResolutionLayout[] ResolutionLayouts =
        {
            new LobbyResolutionLayout(
                "SteamDeck1280x800",
                1280,
                800,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-205f, 82f, 0f),
                new Vector3(-205f, 37f, 0f),
                Vector3.one,
                new Vector3(-285f, 37f, 0f),
                new Vector3(-205f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(205f, 82f, 0f),
                new Vector3(205f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(105f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(85f, -130f, 0f),
                new Vector3(210f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-205f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-45f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1366x768",
                1366,
                768,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.90f, 0.90f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-230f, 82f, 0f),
                new Vector3(-230f, 37f, 0f),
                Vector3.one,
                new Vector3(-310f, 37f, 0f),
                new Vector3(-230f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(230f, 82f, 0f),
                new Vector3(230f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(130f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(110f, -130f, 0f),
                new Vector3(235f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-230f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-70f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1440x900",
                1440,
                900,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-230f, 82f, 0f),
                new Vector3(-230f, 37f, 0f),
                Vector3.one,
                new Vector3(-310f, 37f, 0f),
                new Vector3(-230f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(230f, 82f, 0f),
                new Vector3(230f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(130f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(110f, -130f, 0f),
                new Vector3(235f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-230f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-70f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1600x900",
                1600,
                900,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-260f, 82f, 0f),
                new Vector3(-260f, 37f, 0f),
                Vector3.one,
                new Vector3(-340f, 37f, 0f),
                new Vector3(-260f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(260f, 82f, 0f),
                new Vector3(260f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(160f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(140f, -130f, 0f),
                new Vector3(265f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-260f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-100f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1920x800",
                1920,
                800,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.90f, 0.90f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-330f, 82f, 0f),
                new Vector3(-330f, 37f, 0f),
                Vector3.one,
                new Vector3(-410f, 37f, 0f),
                new Vector3(-330f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(330f, 82f, 0f),
                new Vector3(330f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(230f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(210f, -130f, 0f),
                new Vector3(335f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-330f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-160f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1920x1080",
                1920,
                1080,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-330f, 82f, 0f),
                new Vector3(-330f, 37f, 0f),
                Vector3.one,
                new Vector3(-410f, 37f, 0f),
                new Vector3(-330f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(330f, 82f, 0f),
                new Vector3(330f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(230f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(210f, -130f, 0f),
                new Vector3(335f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-330f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-160f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1920x1200",
                1920,
                1200,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-330f, 82f, 0f),
                new Vector3(-330f, 37f, 0f),
                Vector3.one,
                new Vector3(-410f, 37f, 0f),
                new Vector3(-330f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(330f, 82f, 0f),
                new Vector3(330f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(230f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(210f, -130f, 0f),
                new Vector3(335f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-330f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-160f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1920x1280",
                1920,
                1280,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-330f, 82f, 0f),
                new Vector3(-330f, 37f, 0f),
                Vector3.one,
                new Vector3(-410f, 37f, 0f),
                new Vector3(-330f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(330f, 82f, 0f),
                new Vector3(330f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(230f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(210f, -130f, 0f),
                new Vector3(335f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-330f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-160f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1920x1440",
                1920,
                1440,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-330f, 82f, 0f),
                new Vector3(-330f, 37f, 0f),
                Vector3.one,
                new Vector3(-410f, 37f, 0f),
                new Vector3(-330f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(330f, 82f, 0f),
                new Vector3(330f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(230f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(210f, -130f, 0f),
                new Vector3(335f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-330f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-160f, 73f, 0f)),
            new LobbyResolutionLayout(
                "2048x1152",
                2048,
                1152,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-330f, 82f, 0f),
                new Vector3(-330f, 37f, 0f),
                Vector3.one,
                new Vector3(-410f, 37f, 0f),
                new Vector3(-330f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(330f, 82f, 0f),
                new Vector3(330f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(230f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(210f, -130f, 0f),
                new Vector3(335f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-330f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-160f, 73f, 0f)),
            new LobbyResolutionLayout(
                "2048x1536",
                2048,
                1536,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-330f, 82f, 0f),
                new Vector3(-330f, 37f, 0f),
                Vector3.one,
                new Vector3(-410f, 37f, 0f),
                new Vector3(-330f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(330f, 82f, 0f),
                new Vector3(330f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(230f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(210f, -130f, 0f),
                new Vector3(335f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-330f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-160f, 73f, 0f)),
            new LobbyResolutionLayout(
                "2560x1080",
                2560,
                1080,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-330f, 82f, 0f),
                new Vector3(-330f, 37f, 0f),
                Vector3.one,
                new Vector3(-410f, 37f, 0f),
                new Vector3(-330f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(330f, 82f, 0f),
                new Vector3(330f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(230f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(210f, -130f, 0f),
                new Vector3(335f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-330f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-160f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1600x1200",
                1600,
                1200,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-260f, 82f, 0f),
                new Vector3(-260f, 37f, 0f),
                Vector3.one,
                new Vector3(-340f, 37f, 0f),
                new Vector3(-260f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(260f, 82f, 0f),
                new Vector3(260f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(160f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(140f, -130f, 0f),
                new Vector3(265f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-260f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-100f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1680x1050",
                1680,
                1050,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-275f, 82f, 0f),
                new Vector3(-275f, 37f, 0f),
                Vector3.one,
                new Vector3(-355f, 37f, 0f),
                new Vector3(-275f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(275f, 82f, 0f),
                new Vector3(275f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(175f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(155f, -130f, 0f),
                new Vector3(280f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-275f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-115f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1440x960",
                1440,
                960,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-230f, 82f, 0f),
                new Vector3(-230f, 37f, 0f),
                Vector3.one,
                new Vector3(-310f, 37f, 0f),
                new Vector3(-230f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(230f, 82f, 0f),
                new Vector3(230f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(130f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(110f, -130f, 0f),
                new Vector3(235f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-230f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-70f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1440x1080",
                1440,
                1080,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-230f, 82f, 0f),
                new Vector3(-230f, 37f, 0f),
                Vector3.one,
                new Vector3(-310f, 37f, 0f),
                new Vector3(-230f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(230f, 82f, 0f),
                new Vector3(230f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(130f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(110f, -130f, 0f),
                new Vector3(235f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-230f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-70f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1280x960",
                1280,
                960,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-205f, 82f, 0f),
                new Vector3(-205f, 37f, 0f),
                Vector3.one,
                new Vector3(-285f, 37f, 0f),
                new Vector3(-205f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(205f, 82f, 0f),
                new Vector3(205f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(105f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(85f, -130f, 0f),
                new Vector3(210f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-205f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-45f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1280x1024",
                1280,
                1024,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-205f, 82f, 0f),
                new Vector3(-205f, 37f, 0f),
                Vector3.one,
                new Vector3(-285f, 37f, 0f),
                new Vector3(-205f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(205f, 82f, 0f),
                new Vector3(205f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(105f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(85f, -130f, 0f),
                new Vector3(210f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-205f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-45f, 73f, 0f)),
            new LobbyResolutionLayout(
                "1400x1050",
                1400,
                1050,
                new Vector3(0f, 8f, 0f),
                new Vector3(0.94f, 0.94f, 1f),
                new Vector3(0f, 170f, 0f),
                new Vector3(-205f, 82f, 0f),
                new Vector3(-205f, 37f, 0f),
                Vector3.one,
                new Vector3(-285f, 37f, 0f),
                new Vector3(-205f, -76f, 0f),
                new Vector3(0.74f, 0.74f, 1f),
                new Vector3(205f, 82f, 0f),
                new Vector3(205f, 37f, 0f),
                Vector3.one,
                160,
                new Vector3(105f, 37f, 0f),
                435f,
                145f,
                -10f,
                -72.5f,
                -10f,
                -4f,
                8,
                45,
                new Vector3(85f, -130f, 0f),
                new Vector3(210f, -185f, 0f),
                220,
                35,
                210,
                30,
                new Vector3(-205f, -185f, 0f),
                new Vector3(0f, -185f, 0f),
                new Vector3(-45f, 73f, 0f))
        };

        private sealed class LobbyResolutionLayout
        {
            public readonly string Name;
            public readonly int ScreenWidth;
            public readonly int ScreenHeight;
            public readonly Vector3 RootPosition;
            public readonly Vector3 RootScale;
            public readonly Vector3 TitlePosition;
            public readonly Vector3 PlayersHeaderPosition;
            public readonly Vector3 PlayersBackgroundPosition;
            public readonly Vector3 PlayersBackgroundScale;
            public readonly Vector3 PlayerListPosition;
            public readonly Vector3 SaveSelectorPosition;
            public readonly Vector3 SaveSelectorScale;
            public readonly Vector3 ChatHeaderPosition;
            public readonly Vector3 ChatBackgroundPosition;
            public readonly Vector3 ChatBackgroundScale;
            public readonly int ChatBackgroundHeight;
            public readonly Vector3 ChatPanelPosition;
            public readonly float ChatClipWidth;
            public readonly float ChatClipHeight;
            public readonly float ChatClipCenterX;
            public readonly float ChatClipCenterY;
            public readonly float ChatContentX;
            public readonly float ChatContentTopY;
            public readonly int ChatMaxVisibleLines;
            public readonly int ChatWrapChars;
            public readonly Vector3 ChatInputPosition;
            public readonly Vector3 SendButtonPosition;
            public readonly int ChatInputWidth;
            public readonly int ChatInputHeight;
            public readonly int ChatInputClipWidth;
            public readonly int ChatInputClipHeight;
            public readonly Vector3 BackButtonPosition;
            public readonly Vector3 StartButtonPosition;
            public readonly Vector3 InviteButtonPosition;

            public LobbyResolutionLayout(
                string name,
                int screenWidth,
                int screenHeight,
                Vector3 rootPosition,
                Vector3 rootScale,
                Vector3 titlePosition,
                Vector3 playersHeaderPosition,
                Vector3 playersBackgroundPosition,
                Vector3 playersBackgroundScale,
                Vector3 playerListPosition,
                Vector3 saveSelectorPosition,
                Vector3 saveSelectorScale,
                Vector3 chatHeaderPosition,
                Vector3 chatBackgroundPosition,
                Vector3 chatBackgroundScale,
                int chatBackgroundHeight,
                Vector3 chatPanelPosition,
                float chatClipWidth,
                float chatClipHeight,
                float chatClipCenterX,
                float chatClipCenterY,
                float chatContentX,
                float chatContentTopY,
                int chatMaxVisibleLines,
                int chatWrapChars,
                Vector3 chatInputPosition,
                Vector3 sendButtonPosition,
                int chatInputWidth,
                int chatInputHeight,
                int chatInputClipWidth,
                int chatInputClipHeight,
                Vector3 backButtonPosition,
                Vector3 startButtonPosition,
                Vector3 inviteButtonPosition)
            {
                Name = name;
                ScreenWidth = screenWidth;
                ScreenHeight = screenHeight;
                RootPosition = rootPosition;
                RootScale = rootScale;
                TitlePosition = titlePosition;
                PlayersHeaderPosition = playersHeaderPosition;
                PlayersBackgroundPosition = playersBackgroundPosition;
                PlayersBackgroundScale = playersBackgroundScale;
                PlayerListPosition = playerListPosition;
                SaveSelectorPosition = saveSelectorPosition;
                SaveSelectorScale = saveSelectorScale;
                ChatHeaderPosition = chatHeaderPosition;
                ChatBackgroundPosition = chatBackgroundPosition;
                ChatBackgroundScale = chatBackgroundScale;
                ChatBackgroundHeight = chatBackgroundHeight;
                ChatPanelPosition = chatPanelPosition;
                ChatClipWidth = chatClipWidth;
                ChatClipHeight = chatClipHeight;
                ChatClipCenterX = chatClipCenterX;
                ChatClipCenterY = chatClipCenterY;
                ChatContentX = chatContentX;
                ChatContentTopY = chatContentTopY;
                ChatMaxVisibleLines = chatMaxVisibleLines;
                ChatWrapChars = chatWrapChars;
                ChatInputPosition = chatInputPosition;
                SendButtonPosition = sendButtonPosition;
                ChatInputWidth = chatInputWidth;
                ChatInputHeight = chatInputHeight;
                ChatInputClipWidth = chatInputClipWidth;
                ChatInputClipHeight = chatInputClipHeight;
                BackButtonPosition = backButtonPosition;
                StartButtonPosition = startButtonPosition;
                InviteButtonPosition = inviteButtonPosition;
            }

            public bool Matches(int screenWidth, int screenHeight)
            {
                if (Name == "1366x768")
                {
                    return Mathf.Abs(screenWidth - ScreenWidth) <= 12 &&
                           Mathf.Abs(screenHeight - ScreenHeight) <= 12;
                }

                return ScreenWidth == screenWidth && ScreenHeight == screenHeight;
            }
        }

        public static LobbyGUI Create()
        {
            CoopMod.Logger.LogInfo($"[LobbyGUI] Create() called. _instance={(_instance != null ? "exists" : "null")}, destroyed={(_instance != null ? (_instance.gameObject == null).ToString() : "N/A")}");
            
            if (_instance != null)
            {
                CoopMod.Logger.LogInfo("[LobbyGUI] Returning existing instance");
                return _instance;
            }

            CoopMod.Logger.LogInfo("Creating LobbyGUI with simple panel...");

            // Find UIRoot to parent to
            UIRoot uiRoot = Object.FindObjectOfType<UIRoot>();
            if (uiRoot == null)
            {
                CoopMod.Logger.LogError("Cannot find UIRoot!");
                return null;
            }

            // Create a simple GameObject for our lobby (no fancy frame)
            GameObject lobbyObj = new GameObject("LobbyGUI");
            lobbyObj.layer = 13; // NGUI layer
            lobbyObj.transform.SetParent(uiRoot.transform, false);
            lobbyObj.transform.localPosition = Vector3.zero;
            lobbyObj.transform.localScale = Vector3.one;
            
            _instance = lobbyObj.AddComponent<LobbyGUI>();
            _instance.add_to_opened_stack = true;
            var navigationController = lobbyObj.AddComponent<GamepadNavigationController>();
            navigationController.auto_select = true;
            navigationController.vertical_settings = new GamepadNavigationSettings();
            navigationController.horizontal_settings = new GamepadNavigationSettings();

            // Keep alive across scenes
            Object.DontDestroyOnLoad(lobbyObj);

            // Add UIPanel for rendering
            UIPanel panel = lobbyObj.AddComponent<UIPanel>();
            panel.depth = 100; // Above main menu
            panel.alpha = 1f;
            panel.clipping = UIDrawCall.Clipping.None;

            CoopMod.Logger.LogInfo($"Created simple lobby panel, parent: {uiRoot.name}");

            // Create lobby content directly
            _instance.CreateLobbyContent(lobbyObj);

            // Initialize BaseMenuGUI
            _instance.Init();
            _instance.ConfigureGamepadNavigation();

            // Hide initially
            lobbyObj.SetActive(false);

            CoopMod.Logger.LogInfo($"LobbyGUI created successfully! Instance null? {_instance == null}");
            return _instance;
        }

        private void OnDestroy()
        {
            CoopMod.Logger.LogWarning($"[LobbyGUI] OnDestroy called! Clearing _instance. Was: {(_instance != null ? "not null" : "null")}");
            UnsubscribeLobbyKick();
            if (saveSelectorPanel != null)
            {
                saveSelectorPanel.OnSlotSelected -= OnSaveSlotSelected;
                saveSelectorPanel.OnSlotsChanged -= RefreshGamepadNavigation;
            }
            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void CreateLobbyContent(GameObject root)
        {
            CoopMod.Logger.LogInfo("Creating lobby content...");
            CoopMod.Logger.LogInfo($"Root transform: {root.name}, localScale: {root.transform.localScale}, lossyScale: {root.transform.lossyScale}");

            // Get a reference font from existing UI
            UIFont referenceFont = null;
            var existingLabels = Object.FindObjectsOfType<UILabel>();
            if (existingLabels != null && existingLabels.Length > 0)
            {
                foreach (var label in existingLabels)
                {
                    if (label.bitmapFont != null)
                    {
                        referenceFont = label.bitmapFont;
                        CoopMod.Logger.LogInfo($"Found reference font: {referenceFont.name}");
                        break;
                    }
                }
            }

            if (referenceFont == null)
            {
                CoopMod.Logger.LogError("Could not find any font reference!");
                return;
            }

            // Find styled headers to clone from InventoryGUI
            GameObject buffHeaderTemplate = null;
            GameObject perkHeaderTemplate = null;
            var inventoryGUI = Object.FindObjectOfType<InventoryGUI>(true);
            if (inventoryGUI != null)
            {
                CoopMod.Logger.LogInfo("Found InventoryGUI, searching for styled headers...");
                
                // Try accessing public fields directly (as shown in decompiled code)
                try
                {
                    var goHdrBuffsField = typeof(InventoryGUI).GetField("go_hdr_buffs");
                    var goHdrPerksField = typeof(InventoryGUI).GetField("go_hdr_perks");
                    
                    if (goHdrBuffsField != null)
                    {
                        buffHeaderTemplate = goHdrBuffsField.GetValue(inventoryGUI) as GameObject;
                        if (buffHeaderTemplate != null)
                            CoopMod.Logger.LogInfo($"Found go_hdr_buffs via reflection: {buffHeaderTemplate.name}");
                    }
                    
                    if (goHdrPerksField != null)
                    {
                        perkHeaderTemplate = goHdrPerksField.GetValue(inventoryGUI) as GameObject;
                        if (perkHeaderTemplate != null)
                            CoopMod.Logger.LogInfo($"Found go_hdr_perks via reflection: {perkHeaderTemplate.name}");
                    }
                }
                catch (System.Exception ex)
                {
                    CoopMod.Logger.LogWarning($"Reflection failed: {ex.Message}");
                }
                
                // Fallback: Search through all children
                if (buffHeaderTemplate == null && perkHeaderTemplate == null)
                {
                    CoopMod.Logger.LogInfo("Reflection failed, searching children manually...");
                    var allTransforms = inventoryGUI.GetComponentsInChildren<Transform>(true);
                    CoopMod.Logger.LogInfo($"InventoryGUI has {allTransforms.Length} total children");
                    
                    foreach (var t in allTransforms)
                    {
                        CoopMod.Logger.LogInfo($"  Child: {t.name}");
                        if (t.name.ToLower().Contains("hdr") || t.name.ToLower().Contains("buff") || t.name.ToLower().Contains("perk"))
                        {
                            if (buffHeaderTemplate == null)
                            {
                                buffHeaderTemplate = t.gameObject;
                                CoopMod.Logger.LogInfo($"Using as header template: {t.name}");
                                break;
                            }
                        }
                    }
                }
            }

            // Use the best header we found
            GameObject headerTemplate = buffHeaderTemplate ?? perkHeaderTemplate;
            if (headerTemplate != null)
            {
                CoopMod.Logger.LogInfo($"Using header template: {headerTemplate.name}");
            }
            else
            {
                CoopMod.Logger.LogWarning("Could not find styled headers, will use simple labels");
            }

            // Content parent is just the root
            Transform contentParent = root.transform;

            // Create title using styled header (centered at top)
            titleObj = null;
            if (headerTemplate != null)
            {
                titleObj = Object.Instantiate(headerTemplate, contentParent);
                titleObj.name = "LobbyTitle";
                titleObj.transform.localPosition = new Vector3(0, 140, 0);
                titleObj.transform.localScale = Vector3.one;
                titleObj.SetActive(true);
                StripLocalizedLabels(titleObj);
                
                // Configure children
                foreach (Transform child in titleObj.transform)
                {
                    child.gameObject.SetActive(true);
                    
                    // Enable UI2DSprite for background - keep original size
                    var sprite2D = child.GetComponent<UI2DSprite>();
                    if (sprite2D != null)
                    {
                        CoopMod.Logger.LogInfo($"    Title sprite BEFORE: {sprite2D.width}x{sprite2D.height}");
                        sprite2D.enabled = true;
                        sprite2D.depth = 100;
                        // DON'T resize - keep template size (220x20)
                        sprite2D.MarkAsChanged();
                        CoopMod.Logger.LogInfo($"    Title sprite AFTER: {sprite2D.width}x{sprite2D.height}, enabled={sprite2D.enabled}");
                    }
                }
                
                // Update the label text - same approach as Players/Chat headers
                titleLabel = titleObj.GetComponentInChildren<UILabel>();
                if (titleLabel != null)
                {
                    CoopMod.Logger.LogInfo($"  TITLE label BEFORE: text='{titleLabel.text}', pos={titleLabel.transform.localPosition}, width={titleLabel.width}, height={titleLabel.height}");
                    CoopMod.Logger.LogInfo($"  TITLE label overflow: {titleLabel.overflowMethod}, pivot={titleLabel.pivot}");
                    
                    titleLabel.text = "MULTIPLAYER LOBBY";
                    // Don't change width - keep template default (200)
                    // Use ClampContent to prevent text from extending beyond the header
                    titleLabel.overflowMethod = UILabel.Overflow.ClampContent;
                    titleLabel.enabled = true;
                    titleLabel.depth = 105;
                    titleLabel.MarkAsChanged();
                    
                    CoopMod.Logger.LogInfo($"  TITLE label AFTER: text='{titleLabel.text}', pos={titleLabel.transform.localPosition}, width={titleLabel.width}, height={titleLabel.height}");
                    CoopMod.Logger.LogInfo($"  TITLE label overflow: {titleLabel.overflowMethod}, depth={titleLabel.depth}");
                }
            }
            else
            {
                // Fallback: simple label
                titleObj = new GameObject("LobbyTitle");
                titleObj.SetActive(false);
                titleObj.layer = 13;
                titleObj.transform.SetParent(contentParent, false);
                titleObj.transform.localPosition = new Vector3(0, 140, 0);
                titleObj.transform.localScale = Vector3.one;
                
                titleLabel = titleObj.AddComponent<UILabel>();
                if (titleLabel != null)
                {
                    titleLabel.bitmapFont = referenceFont;
                    titleLabel.text = "MULTIPLAYER LOBBY";
                    titleLabel.fontSize = 32;
                    titleLabel.color = new Color(1f, 0.84f, 0f);
                    titleLabel.alignment = NGUIText.Alignment.Center;
                    titleLabel.overflowMethod = UILabel.Overflow.ResizeFreely;
                    titleLabel.depth = 105;
                    titleLabel.enabled = true;
                    titleLabel.MarkAsChanged();
                    CoopMod.Logger.LogInfo($"Created fallback title label, text: '{titleLabel.text}'");
                }
                titleObj.SetActive(true);
            }
            
            CoopMod.Logger.LogInfo($"Created title at {titleObj.transform.localPosition}");

            // Create "Players" header with styled bar (clone template if available)
            playersHeaderObj = null;
            if (headerTemplate != null)
            {
                playersHeaderObj = Object.Instantiate(headerTemplate, contentParent);
                playersHeaderObj.name = "PlayersHeader";
                playersHeaderObj.transform.localPosition = new Vector3(-200, 60, 0);
                playersHeaderObj.transform.localScale = Vector3.one;
                playersHeaderObj.SetActive(true);
                StripLocalizedLabels(playersHeaderObj);
                
                // Activate all child objects and configure UI2DSprite backgrounds
                CoopMod.Logger.LogInfo($"  Configuring PlayersHeader children...");
                
                foreach (Transform child in playersHeaderObj.transform)
                {
                    child.gameObject.SetActive(true);
                    
                    // Check for UI2DSprite (NGUI 2D texture sprite)
                    var sprite2D = child.GetComponent<UI2DSprite>();
                    if (sprite2D != null)
                    {
                        sprite2D.enabled = true;
                        sprite2D.depth = 100;
                        
                        // Log texture and size info
                        CoopMod.Logger.LogInfo($"    Child '{child.name}': UI2DSprite found");
                        CoopMod.Logger.LogInfo($"      - Enabled: {sprite2D.enabled}, Depth: {sprite2D.depth}");
                        CoopMod.Logger.LogInfo($"      - Texture: {(sprite2D.mainTexture != null ? sprite2D.mainTexture.name : "NULL")}");
                        CoopMod.Logger.LogInfo($"      - Size: {sprite2D.width}x{sprite2D.height}");
                        
                        // Force NGUI to update
                        sprite2D.MarkAsChanged();
                    }
                    else
                    {
                        CoopMod.Logger.LogInfo($"    Child '{child.name}': No UI2DSprite");
                    }
                }
                
                // Update the label text and ensure it's visible
                var headerLabel = playersHeaderObj.GetComponentInChildren<UILabel>();
                if (headerLabel != null)
                {
                    CoopMod.Logger.LogInfo($"  PLAYERS label: text='{headerLabel.text}', pos={headerLabel.transform.localPosition}, pivot={headerLabel.pivot}, enabled={headerLabel.enabled}");
                    headerLabel.text = "Players";
                    headerLabel.enabled = true;
                    headerLabel.depth = 105;
                    headerLabel.MarkAsChanged();
                    CoopMod.Logger.LogInfo($"  Updated Players label, pos={headerLabel.transform.localPosition}, depth: {headerLabel.depth}");
                }
                
                CoopMod.Logger.LogInfo("Cloned styled header for Players section");
            }
            else
            {
                // Fallback: create simple header
                playersHeaderObj = new GameObject("PlayersHeader");
                playersHeaderObj.layer = 13;
                playersHeaderObj.transform.SetParent(contentParent, false);
                playersHeaderObj.transform.localPosition = new Vector3(-400, -250, 0);
                playersHeaderObj.transform.localScale = Vector3.one;
                
                var simpleHeader = playersHeaderObj.AddComponent<UILabel>();
                simpleHeader.bitmapFont = referenceFont;
                simpleHeader.text = "Players";
                simpleHeader.fontSize = 22;
                simpleHeader.color = new Color(0.9f, 0.7f, 0.4f);
                simpleHeader.alignment = NGUIText.Alignment.Center;
                simpleHeader.depth = 105;
                simpleHeader.MarkAsChanged();
                CoopMod.Logger.LogInfo("Created fallback header for Players section");
            }

            // DIAGNOSTIC: Try to find and inspect actual Perks/Buffs UI text properties
            // Search for all InventoryGUI instances (including inactive ones)
            var allInvGUIs = Resources.FindObjectsOfTypeAll<InventoryGUI>();
            CoopMod.Logger.LogInfo($"=== DIAGNOSTIC: Found {allInvGUIs.Length} InventoryGUI instances ===");
            
            if (allInvGUIs.Length > 0)
            {
                var invGUI = allInvGUIs[0];
                CoopMod.Logger.LogInfo("=== DIAGNOSTIC: INSPECTING PERKS/BUFFS UI ===");
                
                // Also search for PerkBuffItemGUI instances directly (these contain the actual perk displays)
                var allPerkItems = Resources.FindObjectsOfTypeAll<PerkBuffItemGUI>();
                CoopMod.Logger.LogInfo($"Found {allPerkItems.Length} PerkBuffItemGUI instances in memory");
                
                if (allPerkItems.Length > 0)
                {
                    int foundCount = 0;
                    foreach (var perkItem in allPerkItems)
                    {
                        // Check if this perk item has the header label
                        if (perkItem.txt_header != null)
                        {
                            foundCount++;
                            var label = perkItem.txt_header;
                            CoopMod.Logger.LogInfo($"=== FOUND PERK HEADER #{foundCount}: '{label.text}' ===");
                            CoopMod.Logger.LogInfo($"  GameObject: {perkItem.gameObject.name}");
                            CoopMod.Logger.LogInfo($"  Label Name: {label.name}");
                            CoopMod.Logger.LogInfo($"  Font: {(label.bitmapFont != null ? label.bitmapFont.name : "NULL")}");
                            CoopMod.Logger.LogInfo($"  FontSize: {label.fontSize}");
                            CoopMod.Logger.LogInfo($"  Color: {label.color}");
                            CoopMod.Logger.LogInfo($"  EffectStyle: {label.effectStyle}");
                            CoopMod.Logger.LogInfo($"  EffectColor: {label.effectColor}");
                            CoopMod.Logger.LogInfo($"  EffectDistance: {label.effectDistance}");
                            CoopMod.Logger.LogInfo($"  SpacingX: {label.spacingX}, SpacingY: {label.spacingY}");
                            CoopMod.Logger.LogInfo($"  Pivot: {label.pivot}");
                            CoopMod.Logger.LogInfo($"  Overflow: {label.overflowMethod}");
                            CoopMod.Logger.LogInfo($"  Alignment: {label.alignment}");
                            CoopMod.Logger.LogInfo($"  Depth: {label.depth}");
                            CoopMod.Logger.LogInfo($"  Width: {label.width}, Height: {label.height}");
                            
                            // Also log description label if it exists
                            if (perkItem.txt_descr != null)
                            {
                                var descLabel = perkItem.txt_descr;
                                CoopMod.Logger.LogInfo($"  === DESCRIPTION LABEL ===");
                                CoopMod.Logger.LogInfo($"  Descr Font: {(descLabel.bitmapFont != null ? descLabel.bitmapFont.name : "NULL")}");
                                CoopMod.Logger.LogInfo($"  Descr FontSize: {descLabel.fontSize}");
                                CoopMod.Logger.LogInfo($"  Descr Color: {descLabel.color}");
                                CoopMod.Logger.LogInfo($"  Descr Text (first 50 chars): {(descLabel.text.Length > 50 ? descLabel.text.Substring(0, 50) : descLabel.text)}");
                            }
                            
                            if (foundCount >= 3) break; // Limit to first 3
                        }
                    }
                    
                    if (foundCount == 0)
                    {
                        CoopMod.Logger.LogInfo("PerkBuffItemGUI instances exist but txt_header fields are null");
                        CoopMod.Logger.LogInfo("This means perks exist but haven't been displayed yet - open Inventory and view Perks tab");
                    }
                }
                else
                {
                    CoopMod.Logger.LogInfo("No PerkBuffItemGUI instances found in memory");
                }
                
                // Fallback: search all labels in InventoryGUI
                var allLabels = invGUI.GetComponentsInChildren<UILabel>(true);
                CoopMod.Logger.LogInfo($"Found {allLabels.Length} total labels in InventoryGUI hierarchy");
                
                // Look for any golden-colored labels (likely headers)
                int goldenCount = 0;
                foreach (var label in allLabels)
                {
                    // Check for golden color (R > 0.9, G > 0.6, B < 0.3)
                    if (label.color.r > 0.9f && label.color.g > 0.6f && label.color.b < 0.3f && !string.IsNullOrEmpty(label.text))
                    {
                        goldenCount++;
                        CoopMod.Logger.LogInfo($"Golden label #{goldenCount}: '{label.text}' (font: {label.bitmapFont?.name}, size: {label.fontSize})");
                        if (goldenCount >= 3) break;
                    }
                }
            }
            else
            {
                CoopMod.Logger.LogInfo("InventoryGUI not found - load a save game to initialize it");
            }
            
            // Instead of using Resources.Load which may not initialize fonts properly,
            // extract fonts directly from existing working labels in the header we just cloned
            UIFont headerFont = null;
            
            // Try to get fonts from the cloned header (these are already working/initialized)
            if (playersHeaderObj != null)
            {
                var headerLabel = playersHeaderObj.GetComponentInChildren<UILabel>();
                if (headerLabel != null && headerLabel.bitmapFont != null)
                {
                    headerFont = headerLabel.bitmapFont;
                    CoopMod.Logger.LogInfo($"Extracted font from cloned header: {headerFont.name}");
                }
            }
            
            // Fallback: try Resources.Load
            if (headerFont == null)
            {
                headerFont = UnityEngine.Resources.Load<UIFont>("ngui_fonts/header");
                if (headerFont != null)
                {
                    CoopMod.Logger.LogInfo($"Loaded header font from Resources: {headerFont.name}");
                }
            }
            
            // For body font, load tiny_font (used for perk descriptions)
            bodyFont = UnityEngine.Resources.Load<UIFont>("ngui_fonts/tiny_font");
            if (bodyFont == null)
            {
                CoopMod.Logger.LogWarning("Could not load tiny_font, using small_font_bold as fallback");
                bodyFont = headerFont;
            }
            
            // Final fallback
            if (headerFont == null)
            {
                CoopMod.Logger.LogWarning("Could not load header font, using reference font");
                headerFont = referenceFont;
                bodyFont = referenceFont;
            }
            
            CoopMod.Logger.LogInfo($"Final fonts - header: {(headerFont != null ? headerFont.name : "NULL")}, body: {(bodyFont != null ? bodyFont.name : "NULL")}");
            
            // EXACT colors extracted from Journalist perk diagnostic
            // Header uses tan color from perk title: RGBA(0.875, 0.667, 0.424, 1.000)
            Color headerColor = new Color(0.875f, 0.667f, 0.424f, 1f); // RGB(223, 170, 108) - #DFAA6C
            // Body uses light gray from perk description: RGBA(0.643, 0.635, 0.675, 1.000)
            Color bodyColor = new Color(0.643f, 0.635f, 0.675f, 1f); // RGB(164, 162, 172) - #A4A2AC
            
            CoopMod.Logger.LogInfo($"Loaded fonts - header: {(headerFont != null ? headerFont.name : "NULL")}, body: {(bodyFont != null ? bodyFont.name : "NULL")}");
            
            // Create panels FIRST (before backgrounds so they can be parented)
            GameObject playerListPanelObj = new GameObject("PlayerListPanel");
            playerListPanelObj.layer = 13;
            playerListPanel = playerListPanelObj.AddComponent<PlayerListPanel>();
            
            GameObject chatPanelObj = new GameObject("ChatPanel");
            chatPanelObj.layer = 13;
            chatPanel = chatPanelObj.AddComponent<ChatPanel>();
            
            CoopMod.Logger.LogInfo("Created PlayerListPanel and ChatPanel components");

            // Clone save slot background for Players section
            var saveSlotsGUI = Object.FindObjectOfType<SaveSlotsMenuGUI>(true);
            GameObject saveSlotTemplate = null; // Store for save selector
            if (saveSlotsGUI != null)
            {
                var saveSlots = saveSlotsGUI.GetComponentsInChildren<SaveSlotGUI>(true);
                if (saveSlots != null && saveSlots.Length > 0)
                {
                    // Clone the first save slot as a template for our background and save selector
                    saveSlotTemplate = saveSlots[0].gameObject;
                    
                    GameObject playersBgObj = Object.Instantiate(saveSlotTemplate, contentParent);
                    playersBackgroundObj = playersBgObj;
                    playersBgObj.name = "PlayersBackground";
                    playersBgObj.layer = 13;
                    playersBgObj.transform.localPosition = new Vector3(-200, 15, 0);
                    playersBgObj.transform.localScale = Vector3.one;
                    
                    // Remove the SaveSlotGUI component and all interactive elements
                    var slotComponent = playersBgObj.GetComponent<SaveSlotGUI>();
                    if (slotComponent != null)
                    {
                        Object.Destroy(slotComponent);
                    }
                    StripControllerComponents(playersBgObj);
                    
                    // Remove all text labels - we just want the background
                    var labels = playersBgObj.GetComponentsInChildren<UILabel>(true);
                    foreach (var label in labels)
                    {
                        Object.Destroy(label.gameObject);
                    }
                    
                    // Remove buttons
                    var buttons = playersBgObj.GetComponentsInChildren<UIButton>(true);
                    foreach (var button in buttons)
                    {
                        Object.Destroy(button.gameObject);
                    }
                    
                    playersBgObj.SetActive(true);
                    
                    // Log detailed transform hierarchy for background
                    CoopMod.Logger.LogInfo($"=== BACKGROUND TRANSFORM HIERARCHY ===");
                    CoopMod.Logger.LogInfo($"PlayersBackground: local={playersBgObj.transform.localPosition}, world={playersBgObj.transform.position}");
                    CoopMod.Logger.LogInfo($"  localScale={playersBgObj.transform.localScale}, lossyScale={playersBgObj.transform.lossyScale}");
                    CoopMod.Logger.LogInfo($"  parent={playersBgObj.transform.parent?.name}");
                    if (playersBgObj.transform.parent != null)
                    {
                        CoopMod.Logger.LogInfo($"  parent local={playersBgObj.transform.parent.localPosition}, world={playersBgObj.transform.parent.position}");
                        CoopMod.Logger.LogInfo($"  parent localScale={playersBgObj.transform.parent.localScale}, lossyScale={playersBgObj.transform.parent.lossyScale}");
                    }
                    
                    // Check for UIWidget/UIPanel on background
                    var bgWidget = playersBgObj.GetComponent<UIWidget>();
                    var bgPanel = playersBgObj.GetComponent<UIPanel>();
                    var bgSprite = playersBgObj.GetComponent<UI2DSprite>();
                    CoopMod.Logger.LogInfo($"  Background components: UIWidget={bgWidget!=null}, UIPanel={bgPanel!=null}, UI2DSprite={bgSprite!=null}");
                    
                    // Position the PlayerListPanel at the same location as background (direct positioning)
                    if (playerListPanel != null)
                    {
                        CoopMod.Logger.LogInfo($"BEFORE Initialize: PlayersBackground at {playersBgObj.transform.localPosition}, world pos {playersBgObj.transform.position}");
                        playerListPanel.Initialize(contentParent, new Vector3(-280, 15, 0), headerFont, headerColor, OnKickPlayerRequested); // Use header font for player names (bold golden text)
                        CoopMod.Logger.LogInfo($"AFTER Initialize: Panel at {playerListPanel.transform.localPosition}, world pos {playerListPanel.transform.position}");
                        CoopMod.Logger.LogInfo($"Positioned PlayerListPanel left-aligned inside frame");
                    }
                    
                    CoopMod.Logger.LogInfo($"Cloned save slot background for Players at {playersBgObj.transform.localPosition}");
                }
                else
                {
                    CoopMod.Logger.LogWarning("Could not find SaveSlotGUI to clone background from");
                }
            }

            // Create Save Selector Panel below the Players section
            if (saveSlotTemplate != null)
            {
                GameObject saveSelectorObj = new GameObject("SaveSelectorPanel");
                saveSelectorObj.layer = 13;
                saveSelectorPanel = saveSelectorObj.AddComponent<SaveSelectorPanel>();
                
                // Position below the players list
                // Players header at Y=60, background at Y=15
                // Reduce gap slightly - position at Y=-100 (moderate spacing below player list)
                saveSelectorPanel.Initialize(contentParent, new Vector3(-200, -100, 0), saveSlotTemplate);
                saveSelectorPanel.OnSlotSelected += OnSaveSlotSelected;
                saveSelectorPanel.OnSlotsChanged += RefreshGamepadNavigation;
                
                CoopMod.Logger.LogInfo("Created Save Selector Panel below players list");
            }
            else
            {
                CoopMod.Logger.LogWarning("Could not create save selector - missing save slot template");
            }



            // Create "Chat" header with styled bar
            chatHeaderObj = null;
            if (headerTemplate != null)
            {
                chatHeaderObj = Object.Instantiate(headerTemplate, contentParent);
                chatHeaderObj.name = "ChatHeader";
                chatHeaderObj.transform.localPosition = new Vector3(200, 60, 0);
                chatHeaderObj.transform.localScale = Vector3.one;
                chatHeaderObj.SetActive(true);
                StripLocalizedLabels(chatHeaderObj);
                
                // Activate all child objects and configure UI2DSprite backgrounds
                CoopMod.Logger.LogInfo($"  Configuring ChatHeader children...");
                
                foreach (Transform child in chatHeaderObj.transform)
                {
                    child.gameObject.SetActive(true);
                    
                    // Check for UI2DSprite (NGUI 2D texture sprite)
                    var sprite2D = child.GetComponent<UI2DSprite>();
                    if (sprite2D != null)
                    {
                        sprite2D.enabled = true;
                        sprite2D.depth = 100;
                        
                        // Log texture and size info
                        CoopMod.Logger.LogInfo($"    Child '{child.name}': UI2DSprite found");
                        CoopMod.Logger.LogInfo($"      - Enabled: {sprite2D.enabled}, Depth: {sprite2D.depth}");
                        CoopMod.Logger.LogInfo($"      - Texture: {(sprite2D.mainTexture != null ? sprite2D.mainTexture.name : "NULL")}");
                        CoopMod.Logger.LogInfo($"      - Size: {sprite2D.width}x{sprite2D.height}");
                        
                        // Force NGUI to update
                        sprite2D.MarkAsChanged();
                    }
                    else
                    {
                        CoopMod.Logger.LogInfo($"    Child '{child.name}': No UI2DSprite");
                    }
                }
                
                // Update the label text and ensure it's visible
                var headerLabel = chatHeaderObj.GetComponentInChildren<UILabel>();
                if (headerLabel != null)
                {
                    headerLabel.text = "Chat";
                    headerLabel.enabled = true;
                    headerLabel.depth = 105;
                    headerLabel.MarkAsChanged();
                    CoopMod.Logger.LogInfo($"  Updated header label text to 'Chat', depth: {headerLabel.depth}");
                }
                
                // Ensure UIPanel exists for rendering
                var headerPanel = chatHeaderObj.GetComponent<UIPanel>();
                if (headerPanel == null)
                {
                    headerPanel = chatHeaderObj.AddComponent<UIPanel>();
                    headerPanel.depth = 100;
                    CoopMod.Logger.LogInfo($"  Added UIPanel to ChatHeader, depth: {headerPanel.depth}");
                }
                else
                {
                    headerPanel.enabled = true;
                    CoopMod.Logger.LogInfo($"  UIPanel already exists, depth: {headerPanel.depth}");
                }
                
                CoopMod.Logger.LogInfo("Cloned styled header for Chat section");
            }
            else
            {
                // Fallback: create simple header
                chatHeaderObj = new GameObject("ChatHeader");
                chatHeaderObj.layer = 13;
                chatHeaderObj.transform.SetParent(contentParent, false);
                chatHeaderObj.transform.localPosition = new Vector3(200, 60, 0);
                chatHeaderObj.transform.localScale = Vector3.one;
                
                var simpleHeader = chatHeaderObj.AddComponent<UILabel>();
                simpleHeader.bitmapFont = referenceFont;
                simpleHeader.text = "Chat";
                simpleHeader.fontSize = 22;
                simpleHeader.color = new Color(0.9f, 0.7f, 0.4f);
                simpleHeader.alignment = NGUIText.Alignment.Center;
                simpleHeader.depth = 105;
                simpleHeader.MarkAsChanged();
                CoopMod.Logger.LogInfo("Created fallback header for Chat section");
            }

            // Clone save slot background for Chat section
            if (saveSlotsGUI != null && saveSlotTemplate != null)
            {
                // Use the saveSlotTemplate we already got from the players section
                saveSlotBackgroundTemplate = saveSlotTemplate; // Store for chat input
                
                GameObject chatBgObj = Object.Instantiate(saveSlotTemplate, contentParent);
                    chatBackgroundObj = chatBgObj;
                    chatBgObj.name = "ChatBackground";
                    chatBgObj.layer = 13;
                    chatBgObj.transform.localPosition = new Vector3(200, 15, 0); // Back to original position
                    chatBgObj.transform.localScale = Vector3.one;
                    
                    // Remove the SaveSlotGUI component and all interactive elements
                    var slotComponent = chatBgObj.GetComponent<SaveSlotGUI>();
                    if (slotComponent != null)
                    {
                        Object.Destroy(slotComponent);
                    }
                    StripControllerComponents(chatBgObj);
                    
                    // Remove all text labels - we just want the background
                    var labels = chatBgObj.GetComponentsInChildren<UILabel>(true);
                    foreach (var label in labels)
                    {
                        Object.Destroy(label.gameObject);
                    }
                    
                    // Remove buttons
                    var buttons = chatBgObj.GetComponentsInChildren<UIButton>(true);
                    foreach (var button in buttons)
                    {
                        Object.Destroy(button.gameObject);
                    }
                    
                    // Resize the background to match chat panel height
                    var chatBgWidget = chatBgObj.GetComponent<UIWidget>();
                    if (chatBgWidget != null)
                    {
                        chatBgWidget.pivot = UIWidget.Pivot.Top; // CRITICAL: Pivot at top so it extends downward only
                        chatBgWidget.height = 200; // Reduced to make room for input field below
                    }
                    
                    chatBgObj.SetActive(true);
                    
                    // Position the ChatPanel at the same location as background (direct positioning)
                    if (chatPanel != null)
                    {
                        CoopMod.Logger.LogInfo($"BEFORE Initialize: ChatBackground at {chatBgObj.transform.localPosition}, world pos {chatBgObj.transform.position}");
                        chatPanel.Initialize(contentParent, new Vector3(100, 15, 0), bodyFont, bodyColor); // Moved higher
                        CoopMod.Logger.LogInfo($"AFTER Initialize: Panel at {chatPanel.transform.localPosition}, world pos {chatPanel.transform.position}");
                        CoopMod.Logger.LogInfo($"Positioned ChatPanel left-aligned inside frame");
                    }
                    
                    CoopMod.Logger.LogInfo($"Cloned save slot background for Chat at {chatBgObj.transform.localPosition}");
            }

            // Create chat input field below the chat box
            CreateChatInput(contentParent);

            // Create buttons at the bottom
            CreateButtons(contentParent);

            ApplyResolutionLayout();

            CoopMod.Logger.LogInfo("Lobby content created: Title, Player List, Chat, Input, Buttons");
        }

        private static void StripControllerComponents(GameObject root)
        {
            if (root == null)
                return;
            foreach (var item in root.GetComponentsInChildren<GamepadNavigationItem>(true))
                Object.DestroyImmediate(item);
            foreach (var menuItem in root.GetComponentsInChildren<MenuItemGUI>(true))
                Object.DestroyImmediate(menuItem);
            foreach (var controller in root.GetComponentsInChildren<GamepadNavigationController>(true))
                Object.DestroyImmediate(controller);
        }

        private static void StripLocalizedLabels(GameObject root)
        {
            if (root == null)
                return;
            foreach (var localizedLabel in root.GetComponentsInChildren<LocalizedLabel>(true))
            {
                localizedLabel.enabled = false;
                Object.DestroyImmediate(localizedLabel);
            }
        }

        private void ApplyResolutionLayout()
        {
            LobbyResolutionLayout layout = GetActiveResolutionLayout();
            if (layout == DefaultResolutionLayout)
            {
                CoopMod.Logger.LogInfo($"[LobbyGUI] Using default unscaled layout for {Screen.width}x{Screen.height}");
                return;
            }

            transform.localPosition = layout.RootPosition;
            transform.localScale = layout.RootScale;

            ApplyTransform(titleObj, layout.TitlePosition, Vector3.one);
            ApplyTransform(playersHeaderObj, layout.PlayersHeaderPosition, Vector3.one);
            ApplyTransform(playersBackgroundObj, layout.PlayersBackgroundPosition, layout.PlayersBackgroundScale);
            ApplyTransform(chatHeaderObj, layout.ChatHeaderPosition, Vector3.one);
            ApplyTransform(chatBackgroundObj, layout.ChatBackgroundPosition, layout.ChatBackgroundScale);
            ApplyTransform(backButtonObj, layout.BackButtonPosition, Vector3.one);
            ApplyTransform(startButtonObj, layout.StartButtonPosition, Vector3.one);
            ApplyTransform(inviteButtonObj, layout.InviteButtonPosition, Vector3.one);

            ApplyWidgetHeight(chatBackgroundObj, layout.ChatBackgroundHeight);

            if (playerListPanel != null)
            {
                playerListPanel.transform.localPosition = layout.PlayerListPosition;
            }

            if (saveSelectorPanel != null)
            {
                saveSelectorPanel.ApplyLayout(layout.SaveSelectorPosition, layout.SaveSelectorScale);
            }

            if (chatPanel != null)
            {
                chatPanel.ApplyLayout(
                    layout.ChatPanelPosition,
                    layout.ChatClipWidth,
                    layout.ChatClipHeight,
                    layout.ChatClipCenterX,
                    layout.ChatClipCenterY,
                    layout.ChatContentX,
                    layout.ChatContentTopY,
                    layout.ChatMaxVisibleLines,
                    layout.ChatWrapChars,
                    IsCompactLowHeightLayout(layout) ? 10f : 0f);
            }

            if (chatInputPanel != null)
            {
                chatInputPanel.ApplyLayout(
                    layout.ChatInputPosition,
                    layout.SendButtonPosition,
                    layout.ChatInputWidth,
                    layout.ChatInputHeight,
                    layout.ChatInputClipWidth,
                    layout.ChatInputClipHeight);
            }

            CoopMod.Logger.LogInfo(
                $"[LobbyGUI] Applied layout {layout.Name}: " +
                $"root={transform.localPosition}, scale={transform.localScale}, " +
                $"saveSelector={layout.SaveSelectorPosition}/{layout.SaveSelectorScale}");
        }

        private static void ApplyTransform(GameObject target, Vector3 localPosition, Vector3 localScale)
        {
            if (target == null)
                return;

            target.transform.localPosition = localPosition;
            target.transform.localScale = localScale;
        }

        private static void ApplyWidgetHeight(GameObject target, int height)
        {
            if (target == null || height <= 0)
                return;

            UIWidget widget = target.GetComponent<UIWidget>() ?? target.GetComponentInChildren<UIWidget>(true);
            if (widget == null)
                return;

            widget.height = height;
            widget.MarkAsChanged();
        }

        private static bool IsCompactLowHeightLayout(LobbyResolutionLayout layout)
        {
            if (layout == null)
                return false;

            return (layout.ScreenWidth == 1280 &&
                    (layout.ScreenHeight == 800 || layout.ScreenHeight == 960 || layout.ScreenHeight == 1024)) ||
                   (layout.ScreenWidth == 1366 && layout.ScreenHeight == 768) ||
                   (layout.ScreenWidth == 1920 && layout.ScreenHeight == 800);
        }

        private static LobbyResolutionLayout GetActiveResolutionLayout()
        {
            int screenWidth = Screen.width;
            int screenHeight = Screen.height;
            for (int i = 0; i < ResolutionLayouts.Length; i++)
            {
                LobbyResolutionLayout layout = ResolutionLayouts[i];
                if (layout.Matches(screenWidth, screenHeight))
                    return layout;
            }

            return DefaultResolutionLayout;
        }

        private void CreateChatInput(Transform parent)
        {
            // Use the same background template as chat box
            if (saveSlotBackgroundTemplate != null)
            {
                // Create chat input field positioned below chat box
                GameObject inputObj = new GameObject("ChatInput");
                chatInputPanel = inputObj.AddComponent<ChatInputPanel>();
                chatInputPanel.Initialize(parent, new Vector3(200, -180, 0), saveSlotBackgroundTemplate, bodyFont); // Left position where it was visible
                
                // Create Send button next to input field
                chatInputPanel.CreateSendButton(parent, new Vector3(250, -210, 0)); // To the right of input (250 + 220 + 10 = 480)
                
                // Set up message send handler
                chatInputPanel.OnMessageSent += OnChatMessageSent;
                
                CoopMod.Logger.LogInfo("Chat input field and Send button created");
            }
            else
            {
                CoopMod.Logger.LogError("saveSlotBackgroundTemplate is null! Cannot create chat input.");
            }
        }

        private void OnChatMessageSent(string message)
        {
            if (chatPanel != null)
            {
                // Format message with player name
                string playerName = SteamHelper.GetLocalPlayerName();
                string formattedMessage = $"{playerName}: {message}";
                
                // Add to centralized chat manager (will sync to both lobby and in-game chat)
                ChatManager.AddMessage(formattedMessage);
                
                // Broadcast to other players in the lobby
                LobbyChatSync.BroadcastChatMessage(formattedMessage);
            }
        }

        private void CreateButtons(Transform parent)
        {
            // Use DialogButtonGUI to clone buttons (same as Send button)
            var dialogButtons = Resources.FindObjectsOfTypeAll<DialogButtonGUI>();
            if (dialogButtons == null || dialogButtons.Length == 0)
            {
                CoopMod.Logger.LogError("Cannot find DialogButtonGUI to clone buttons from!");
                return;
            }

            var template = dialogButtons[0].gameObject;

            // Create Back button (aligned with Send button at Y=-210)
            GameObject backObj = Object.Instantiate(template, parent);
            backObj.name = "btn_back";
            backObj.transform.localPosition = new Vector3(-200, -210, 0);
            backObj.transform.localScale = Vector3.one;
            backObj.layer = 13;
            
            var backLabel = backObj.GetComponentInChildren<UILabel>(true);
            if (backLabel != null)
            {
                backLabel.text = "Back";
            }

            // Remove DialogButtonGUI component (use manual click detection instead)
            var backButtonGUI = backObj.GetComponent<DialogButtonGUI>();
            if (backButtonGUI != null)
            {
                Object.Destroy(backButtonGUI);
            }

            // Remove UIButton components (they don't work with zero scale)
            var backUIButtons = backObj.GetComponentsInChildren<UIButton>(true);
            foreach (var btn in backUIButtons)
            {
                Object.Destroy(btn);
            }

            // Store widget for manual click detection
            backButtonWidget = backObj.GetComponent<UIWidget>();
            if (backButtonWidget == null)
            {
                backButtonWidget = backObj.GetComponentInChildren<UIWidget>(true);
            }

            backButtonObj = backObj;
            CoopMod.Logger.LogInfo($"Created Back button at {backObj.transform.localPosition}, widget: {backButtonWidget != null}");

            // Create Start Game / Ready button (aligned with Send button at Y=-210)
            // Host sees "Start Game", clients see "Ready"
            GameObject startObj = Object.Instantiate(template, parent);
            startObj.name = "btn_start";
            startObj.transform.localPosition = new Vector3(0, -210, 0);
            startObj.transform.localScale = Vector3.one;
            startObj.layer = 13;
            
            startButtonLabel = startObj.GetComponentInChildren<UILabel>(true);
            if (startButtonLabel != null)
            {
                // Default to "Start Game", will be updated based on host/client status
                startButtonLabel.text = "Start Game";
            }

            // Remove DialogButtonGUI component (use manual click detection instead)
            var startButtonGUI = startObj.GetComponent<DialogButtonGUI>();
            if (startButtonGUI != null)
            {
                Object.Destroy(startButtonGUI);
            }

            // Remove UIButton components (they don't work with zero scale)
            var startUIButtons = startObj.GetComponentsInChildren<UIButton>(true);
            foreach (var btn in startUIButtons)
            {
                Object.Destroy(btn);
            }

            // Store widget for manual click detection
            startButtonWidget = startObj.GetComponent<UIWidget>();
            if (startButtonWidget == null)
            {
                startButtonWidget = startObj.GetComponentInChildren<UIWidget>(true);
            }

            startButtonObj = startObj;
            CoopMod.Logger.LogInfo($"Created Start button at {startObj.transform.localPosition}, widget: {startButtonWidget != null}");

            // Create Invite button (positioned right next to Players header)
            GameObject inviteObj = Object.Instantiate(template, parent);
            inviteObj.name = "btn_invite";
            inviteObj.transform.localPosition = new Vector3(-40, 51, 0); // Right next to Players header
            inviteObj.transform.localScale = Vector3.one;
            inviteObj.layer = 13;
            
            var inviteLabel = inviteObj.GetComponentInChildren<UILabel>(true);
            if (inviteLabel != null)
            {
                inviteLabel.text = "Invite";
            }

            // Remove DialogButtonGUI component (use manual click detection instead)
            var inviteButtonGUI = inviteObj.GetComponent<DialogButtonGUI>();
            if (inviteButtonGUI != null)
            {
                Object.Destroy(inviteButtonGUI);
            }

            // Remove UIButton components (they don't work with zero scale)
            var inviteUIButtons = inviteObj.GetComponentsInChildren<UIButton>(true);
            foreach (var btn in inviteUIButtons)
            {
                Object.Destroy(btn);
            }

            // Store widget for manual click detection
            inviteButtonWidget = inviteObj.GetComponent<UIWidget>();
            if (inviteButtonWidget == null)
            {
                inviteButtonWidget = inviteObj.GetComponentInChildren<UIWidget>(true);
            }

            inviteButtonObj = inviteObj;
            backNavigationItem = ConfigureControllerButton(backButtonObj, OnBackPressed);
            startNavigationItem = ConfigureControllerButton(
                startButtonObj,
                OnStartPressed,
                horizontalFramePadding: 28);
            inviteNavigationItem = ConfigureControllerButton(inviteButtonObj, OnInvitePressed);
            CoopMod.Logger.LogInfo($"Created Invite button at {inviteObj.transform.localPosition}, widget: {inviteButtonWidget != null}");
        }

        private GamepadNavigationItem ConfigureControllerButton(
            GameObject buttonObj,
            System.Action onPressed,
            int horizontalFramePadding = 14)
        {
            if (buttonObj == null)
                return null;

            StripLocalizedLabels(buttonObj);
            var navigationItem = buttonObj.GetComponent<GamepadNavigationItem>() ??
                                 buttonObj.AddComponent<GamepadNavigationItem>();
            navigationItem.active = true;
            ControllerFocusFrame.Attach(
                buttonObj,
                navigationItem,
                horizontalPadding: horizontalFramePadding);

            UILabel label = buttonObj.GetComponentInChildren<UILabel>(true);
            Color normalColor = label != null ? label.color : Color.white;
            navigationItem.SetCallbacks(
                () => SetControllerButtonFocus(label, normalColor, true),
                () => SetControllerButtonFocus(label, normalColor, false),
                () => onPressed?.Invoke());
            return navigationItem;
        }

        private static void SetControllerButtonFocus(UILabel label, Color normalColor, bool focused)
        {
            if (label == null)
                return;
            label.color = focused ? new Color(1f, 0.84f, 0f, 1f) : normalColor;
            label.MarkAsChanged();
        }

        private void ConfigureGamepadNavigation()
        {
            if (backNavigationItem == null || startNavigationItem == null || inviteNavigationItem == null)
                return;

            GamepadNavigationItem[] slots = saveSelectorPanel?.GetControllerNavigationItems() ??
                                            new GamepadNavigationItem[0];
            GamepadNavigationItem firstSlot = slots.Length > 0 ? slots[0] : null;
            GamepadNavigationItem lastSlot = slots.Length > 0 ? slots[slots.Length - 1] : null;

            for (int i = 0; i < slots.Length; i++)
            {
                GamepadNavigationItem slot = slots[i];
                slot.active = true;
                slot.SetCustomDirectionItem(i > 0 ? slots[i - 1] : inviteNavigationItem, Direction.Up);
                slot.SetCustomDirectionItem(i + 1 < slots.Length ? slots[i + 1] : backNavigationItem, Direction.Down);
                slot.SetCustomDirectionItem(slot, Direction.Left);
                slot.SetCustomDirectionItem(startNavigationItem, Direction.Right);
            }

            inviteNavigationItem.active = true;
            inviteNavigationItem.SetCustomDirectionItem(firstSlot ?? startNavigationItem, Direction.Down);
            inviteNavigationItem.SetCustomDirectionItem(backNavigationItem, Direction.Left);
            inviteNavigationItem.SetCustomDirectionItem(startNavigationItem, Direction.Right);

            backNavigationItem.active = true;
            backNavigationItem.SetCustomDirectionItem(lastSlot ?? inviteNavigationItem, Direction.Up);
            backNavigationItem.SetCustomDirectionItem(startNavigationItem, Direction.Right);

            startNavigationItem.active = true;
            startNavigationItem.SetCustomDirectionItem(inviteNavigationItem, Direction.Up);
            startNavigationItem.SetCustomDirectionItem(backNavigationItem, Direction.Left);
        }

        private void RefreshGamepadNavigation()
        {
            ConfigureGamepadNavigation();
            if (!is_shown || !BaseGUI.for_gamepad || gamepad_controller == null)
                return;

            gamepad_controller.ReinitItems(false);
            if (gamepad_controller.focused_item == null)
                gamepad_controller.FocusOnFirstActive();
        }

        private new void Update()
        {
            // Call base Update first
            base.Update();
            
            // Check for gamepad button press to add Player 2 (local co-op)
            if (ModConfig.EnableLocalCoop.Value && LocalCoopManager.Instance != null)
            {
                // Check if Player 2 hasn't been added yet
                if (LocalCoopManager.Instance.Player2 == null)
                {
                    // Check for gamepad button press (A on Xbox, X on PlayStation - both map to Joystick Button 0)
                    if (Input.GetKeyDown(KeyCode.JoystickButton0))
                    {
                        AddLocalPlayer2();
                    }
                }
            }
            
            // Manual click detection for buttons (same pattern as ChatInputPanel Send button)
            if (Input.GetMouseButtonDown(0) && uiCamera != null)
            {
                Vector3 mousePos = Input.mousePosition;
                
                // Check Back button click
                if (backButtonWidget != null)
                {
                    Vector3[] corners = backButtonWidget.worldCorners;
                    Vector2 min = uiCamera.WorldToScreenPoint(corners[0]);
                    Vector2 max = uiCamera.WorldToScreenPoint(corners[2]);
                    
                    float minX = Mathf.Min(min.x, max.x);
                    float maxX = Mathf.Max(min.x, max.x);
                    float minY = Mathf.Min(min.y, max.y);
                    float maxY = Mathf.Max(min.y, max.y);
                    
                    if (mousePos.x >= minX && mousePos.x <= maxX && 
                        mousePos.y >= minY && mousePos.y <= maxY)
                    {
                        CoopMod.Logger.LogInfo("Back button clicked!");
                        OnBackPressed();
                        return;
                    }
                }
                
                // Check Start button click
                if (startButtonWidget != null)
                {
                    Vector3[] corners = startButtonWidget.worldCorners;
                    Vector2 min = uiCamera.WorldToScreenPoint(corners[0]);
                    Vector2 max = uiCamera.WorldToScreenPoint(corners[2]);
                    
                    float minX = Mathf.Min(min.x, max.x);
                    float maxX = Mathf.Max(min.x, max.x);
                    float minY = Mathf.Min(min.y, max.y);
                    float maxY = Mathf.Max(min.y, max.y);
                    
                    if (mousePos.x >= minX && mousePos.x <= maxX && 
                        mousePos.y >= minY && mousePos.y <= maxY)
                    {
                        CoopMod.Logger.LogInfo("Start button clicked!");
                        OnStartPressed();
                        return;
                    }
                }
                
                // Check Invite button click
                if (inviteButtonWidget != null)
                {
                    Vector3[] corners = inviteButtonWidget.worldCorners;
                    Vector2 min = uiCamera.WorldToScreenPoint(corners[0]);
                    Vector2 max = uiCamera.WorldToScreenPoint(corners[2]);
                    
                    float minX = Mathf.Min(min.x, max.x);
                    float maxX = Mathf.Max(min.x, max.x);
                    float minY = Mathf.Min(min.y, max.y);
                    float maxY = Mathf.Max(min.y, max.y);
                    
                    if (mousePos.x >= minX && mousePos.x <= maxX && 
                        mousePos.y >= minY && mousePos.y <= maxY)
                    {
                        CoopMod.Logger.LogInfo("Invite button clicked!");
                        OnInvitePressed();
                        return;
                    }
                }
            }
        }

        private void OnBackPressed()
        {
            CoopMod.Logger.LogInfo("[UI] Back button pressed - leaving lobby");
            
            // Leave the Steam lobby
            if (Network.SteamLobbyManager.Instance != null && Network.SteamLobbyManager.Instance.IsInLobby)
            {
                if (isHost)
                {
                    CoopMod.Logger.LogInfo("[UI] Host leaving - lobby will be destroyed");
                    ChatManager.AddMessage("[System] Host is closing the lobby...");
                }
                else
                {
                    CoopMod.Logger.LogInfo("[UI] Client leaving lobby");
                    ChatManager.AddMessage("[System] You left the lobby");
                }
                
                Network.SteamLobbyManager.Instance.LeaveLobby();
            }
            
            // Clear chat messages for fresh start next time
            ChatManager.ClearMessages();
            
            // Reset ready states
            LobbyReadySystem.ResetReadyStates();
            
            // Unsubscribe from game start event
            if (Network.SteamP2PManager.Instance != null)
            {
                Network.SteamP2PManager.Instance.OnGameStartReceived -= OnGameStartReceived;
            }
            
            // Mark multiplayer session as inactive
            isMultiplayerSessionActive = false;
            
            // Reset local state
            isHost = false;
            isLocalReady = false;
            UnsubscribeLobbyKick();
            if (playerListPanel != null)
            {
                playerListPanel.SetKickButtonsEnabled(false);
            }
            
            Hide();
            
            // Show the multiplayer submenu again
            if (MultiplayerSubMenuGUI.Instance != null)
            {
                MultiplayerSubMenuGUI.Instance.Open();
            }
        }

        private void OnStartPressed()
        {
            CoopMod.Logger.LogInfo("[UI] ========== OnStartPressed() ==========");
            CoopMod.Logger.LogInfo($"[UI] isHost = {isHost}");
            CoopMod.Logger.LogInfo($"[UI] SteamLobbyManager.IsInLobby = {Network.SteamLobbyManager.Instance?.IsInLobby}");
            CoopMod.Logger.LogInfo($"[UI] SteamLobbyManager.IsHost = {Network.SteamLobbyManager.Instance?.IsHost}");
            
            // If we're a client, this button toggles ready state
            if (!isHost)
            {
                OnReadyPressed();
                return;
            }
            
            // Host logic
            int totalCount = Network.SteamLobbyManager.Instance?.GetLobbyMemberCount() ?? 0;
            
            // If host is alone, just start the game
            if (totalCount <= 1)
            {
                OnHostStartGame();
                return;
            }
            
            // If host has other players, they need to ready up first
            // If host is not ready, toggle ready state
            if (!isLocalReady)
            {
                OnReadyPressed();
                return;
            }
            
            // Host is ready - check if all players are ready to start
            if (LobbyReadySystem.CheckAllPlayersReady())
            {
                OnHostStartGame();
            }
            else
            {
                // Not everyone ready yet - show message
                int readyCount = LobbyReadySystem.GetReadyPlayerCount();
                ChatManager.AddMessage($"[System] Waiting for players ({readyCount}/{totalCount} ready)");
            }
        }
        
        /// <summary>
        /// Called when a client clicks the Ready button
        /// </summary>
        private void OnReadyPressed()
        {
            CoopMod.Logger.LogInfo("[UI] Ready button pressed");
            
            // Toggle ready state
            LobbyReadySystem.ToggleLocalReady();
            isLocalReady = LobbyReadySystem.IsLocalPlayerReady();
            
            // Update button text
            UpdateStartButtonText();
        }
        
        /// <summary>
        /// Called when the host clicks Start Game
        /// </summary>
        private void OnHostStartGame()
        {
            CoopMod.Logger.LogInfo("[UI] Host starting game...");
            
            int totalCount = Network.SteamLobbyManager.Instance?.GetLobbyMemberCount() ?? 0;
            
            // Mark that we're loading a game in multiplayer context
            MainMenuPatches.IsMultiplayerSaveMode = true;
            
            // If there are other players, check if all are ready
            if (totalCount > 1 && !LobbyReadySystem.CheckAllPlayersReady())
            {
                int readyCount = LobbyReadySystem.GetReadyPlayerCount();
                ChatManager.AddMessage($"[System] Waiting for players to ready up ({readyCount}/{totalCount} ready)");
                return;
            }
            
            // Check if a save slot is selected
            if (saveSelectorPanel != null)
            {
                if (!saveSelectorPanel.HasSelection())
                {
                    ChatManager.AddMessage("[System] Please select a save slot first!");
                    return;
                }

                var selectedSlot = saveSelectorPanel.GetSelectedSlot();
                string saveSlotName = selectedSlot?.real_time ?? "NEW_GAME";
                CoopMod.Logger.LogInfo($"[UI] Selected save slot: {saveSlotName}");

                // Advertise that this lobby has transitioned into a joinable running game.
                Network.SteamLobbyManager.Instance?.MarkGameRunning();
                
                // Store the save slot for transfer to clients
                if (selectedSlot != null && totalCount > 1)
                {
                    Multiplayer.SaveTransferManager.SetHostSaveSlot(selectedSlot);
                }
                
                // Start game load sync if multiplayer
                if (totalCount > 1)
                {
                    CoopMod.Logger.LogInfo("[UI] Starting GameLoadSync for multiplayer");
                    Multiplayer.GameLoadSync.Instance?.StartWaitingForPlayers(totalCount);
                }
                
                // Broadcast game start to all clients before starting
                if (totalCount > 1)
                {
                    CoopMod.Logger.LogInfo("[UI] Broadcasting GAME_START to all clients");
                    Network.SteamP2PManager.Instance?.BroadcastGameStart(saveSlotName);
                }
                
                // Hide the lobby first
                Hide(false);
                
                // Open the SaveSlotsMenuGUI which will handle the game start
                if (GUIElements.me != null && GUIElements.me.saves != null)
                {
                    CoopMod.Logger.LogInfo("[UI] Triggering game start via SaveSlotsMenuGUI");
                    
                    // Open the saves menu first (this initializes it)
                    GUIElements.me.saves.Open();
                    
                    // Immediately trigger the save slot selection
                    if (selectedSlot != null)
                    {
                        CoopMod.Logger.LogInfo($"[UI] Starting game with existing save: {selectedSlot.real_time}");
                        GUIElements.me.saves.OnSelectSlotPressed(selectedSlot);
                    }
                    else
                    {
                        CoopMod.Logger.LogInfo("[UI] Starting new game");
                        // Pass null to start a new game
                        GUIElements.me.saves.OnSelectSlotPressed(null);
                    }
                }
                else
                {
                    CoopMod.Logger.LogError("[UI] GUIElements.me.saves is null!");
                }
            }
            else
            {
                CoopMod.Logger.LogWarning("[UI] Save selector panel not available");
                ChatManager.AddMessage("[System] Please select a save slot first!");
            }
        }
        
        /// <summary>
        /// Called when the client receives GAME_START from the host
        /// </summary>
        private void OnGameStartReceived(CSteamID hostID, string saveSlotName)
        {
            CoopMod.Logger.LogInfo($"[UI] Received GAME_START from host! Save slot: {saveSlotName}");
            
            // Get total player count for sync
            int totalCount = Network.SteamLobbyManager.Instance?.GetLobbyMemberCount() ?? 2;

            // Host hot-reload case: the client is already in a multiplayer game session.
            // Tear down coop state now so that when our player respawns after the reload,
            // OnlineCoopManager rebuilds against the fresh world. Without this the old
            // remote-player reference and hasActivated latch block re-activation.
            bool alreadyInGame = MainGame.game_started;
            if (alreadyInGame)
            {
                CoopMod.Logger.LogInfo("[UI] GAME_START received mid-game (host hot-reload) - tearing down coop state");
                try
                {
                    Network.OnlineCoopManager.Instance?.PrepareHotReload();
                }
                catch (System.Exception ex)
                {
                    CoopMod.Logger.LogWarning($"[UI] PrepareHotReload on client failed: {ex.Message}");
                }
            }
            
            // Check if host is starting a new game
            if (saveSlotName == "NEW_GAME")
            {
                CoopMod.Logger.LogInfo("[UI] Host starting NEW_GAME - client will also start new game");
                ChatManager.AddMessage($"[System] Host is starting a new game...");
                
                // Hide the lobby UI
                Hide(false);
                
                // Start game load sync AFTER hiding lobby but BEFORE game starts
                // This ensures the sync is tracking before the game begins loading
                CoopMod.Logger.LogInfo("[UI] Starting GameLoadSync for client");
                Multiplayer.GameLoadSync.Instance?.StartWaitingForPlayers(totalCount);
                
                // Start a new game for client too
                if (GUIElements.me?.saves != null)
                {
                    CoopMod.Logger.LogInfo("[UI] Opening SaveSlotsMenuGUI to start new game");
                    GUIElements.me.saves.Open();
                    CoopMod.Logger.LogInfo("[UI] Triggering OnSelectSlotPressed(null) for new game");
                    GUIElements.me.saves.OnSelectSlotPressed(null); // null = new game
                }
                else
                {
                    CoopMod.Logger.LogError("[UI] GUIElements.me.saves is null - cannot start new game!");
                }
                return;
            }
            
            // For existing saves, start sync tracking
            CoopMod.Logger.LogInfo("[UI] Starting GameLoadSync for client (existing save)");
            Multiplayer.GameLoadSync.Instance?.StartWaitingForPlayers(totalCount);
            
            // Show message to user
            ChatManager.AddMessage($"[System] Host is starting the game...");
            ChatManager.AddMessage($"[System] Requesting save data from host...");
            
            // Store host ID for later use
            pendingHostID = hostID;
            
            // Subscribe to save transfer events
            SubscribeToSaveTransferEvents();
            
            // Request save data from host
            Multiplayer.SaveTransferManager.RequestSaveFromHost(hostID);
        }

        /// <summary>
        /// Client path for joining a host that is already in-game. This skips the
        /// pre-game lobby ready sync because the host is already loaded and will not
        /// send another initial ready signal.
        /// </summary>
        public void JoinRunningGameFromHost(CSteamID hostID, string hostName)
        {
            CoopMod.Logger.LogInfo($"[UI] Joining running game hosted by {hostName} ({hostID})");

            MarkMultiplayerSessionActive();
            isHost = false;
            isLocalReady = false;
            pendingHostID = hostID;

            LobbyReadySystem.ResetReadyStates();
            SubscribeToGameStartReceived();
            SubscribeToSaveTransferEvents();

            if (JoinGameGUI.Instance != null && JoinGameGUI.Instance.is_shown)
            {
                JoinGameGUI.Instance.Hide();
            }

            if (GUIElements.me?.main_menu != null)
            {
                GUIElements.me.main_menu.Hide(true);
            }

            gameObject.SetActive(false);

            ChatManager.AddMessage("[System] Joining running game...");
            ChatManager.AddMessage("[System] Requesting save data from host...");
            Multiplayer.SaveTransferManager.RequestSaveFromHost(hostID);
        }

        private void SubscribeToGameStartReceived()
        {
            if (Network.SteamP2PManager.Instance != null)
            {
                Network.SteamP2PManager.Instance.OnGameStartReceived -= OnGameStartReceived;
                Network.SteamP2PManager.Instance.OnGameStartReceived += OnGameStartReceived;
                CoopMod.Logger.LogInfo("[UI] Subscribed to OnGameStartReceived event");
            }
        }

        private void SubscribeToSaveTransferEvents()
        {
            Multiplayer.SaveTransferManager.OnSaveReceived -= OnSaveDataReceived;
            Multiplayer.SaveTransferManager.OnSaveReceived += OnSaveDataReceived;
            Multiplayer.SaveTransferManager.OnTransferProgress -= OnSaveTransferProgress;
            Multiplayer.SaveTransferManager.OnTransferProgress += OnSaveTransferProgress;
            Multiplayer.SaveTransferManager.OnTransferError -= OnSaveTransferError;
            Multiplayer.SaveTransferManager.OnTransferError += OnSaveTransferError;
        }
        
        private CSteamID pendingHostID;
        
        private void OnSaveTransferProgress(float progress)
        {
            int percent = (int)(progress * 100);
            if (percent % 25 == 0) // Log every 25%
            {
                CoopMod.Logger.LogInfo($"[UI] Save transfer progress: {percent}%");
            }
        }
        
        private void OnSaveTransferError(string error)
        {
            CoopMod.Logger.LogError($"[UI] Save transfer error: {error}");
            ChatManager.AddMessage($"[System] Error: {error}");
            
            // Unsubscribe from events
            Multiplayer.SaveTransferManager.OnSaveReceived -= OnSaveDataReceived;
            Multiplayer.SaveTransferManager.OnTransferProgress -= OnSaveTransferProgress;
            Multiplayer.SaveTransferManager.OnTransferError -= OnSaveTransferError;
        }
        
        private void OnSaveDataReceived(SaveSlotData slotData)
        {
            CoopMod.Logger.LogInfo($"[UI] Save data received! Loading game: {slotData.filename_no_extension}");
            ChatManager.AddMessage($"[System] Save received! Loading game...");
            
            // Unsubscribe from events
            Multiplayer.SaveTransferManager.OnSaveReceived -= OnSaveDataReceived;
            Multiplayer.SaveTransferManager.OnTransferProgress -= OnSaveTransferProgress;
            Multiplayer.SaveTransferManager.OnTransferError -= OnSaveTransferError;
            
            // Hide the lobby UI
            Hide(false);
            
            // Load the received save
            Multiplayer.SaveTransferManager.LoadReceivedSave(slotData);
        }
        
        /// <summary>
        /// Update the start button text based on host/client and ready state
        /// </summary>
        private void UpdateStartButtonText()
        {
            if (startButtonLabel == null)
            {
                CoopMod.Logger.LogWarning("[UI] UpdateStartButtonText: startButtonLabel is null!");
                return;
            }
            
            if (isHost)
            {
                int totalCount = Network.SteamLobbyManager.Instance?.GetLobbyMemberCount() ?? 0;
                
                // If host is alone, just show "Start Game"
                if (totalCount <= 1)
                {
                    startButtonLabel.text = "Start Game";
                    CoopMod.Logger.LogInfo($"[UI] UpdateStartButtonText: Host alone, showing 'Start Game'");
                }
                else
                {
                    int readyCount = LobbyReadySystem.GetReadyPlayerCount();
                    CoopMod.Logger.LogInfo($"[UI] UpdateStartButtonText: isLocalReady={isLocalReady}, readyCount={readyCount}, totalCount={totalCount}");
                    
                    if (readyCount >= totalCount)
                    {
                        startButtonLabel.text = "Start Game";
                    }
                    else
                    {
                        startButtonLabel.text = $"Ready {readyCount}/{totalCount}";
                    }
                }
            }
            else
            {
                // Client button action toggles the local ready state.
                startButtonLabel.text = isLocalReady ? "Unready" : "Ready";
                CoopMod.Logger.LogInfo($"[UI] UpdateStartButtonText: Client, isLocalReady={isLocalReady}");
            }
        }
        
        /// <summary>
        /// Called when any player's ready state changes
        /// </summary>
        private void OnPlayerReadyChanged(CSteamID playerID, bool isReady)
        {
            CoopMod.Logger.LogInfo($"[UI] Player {playerID} ready state changed to: {isReady}");
            
            // Update the player avatar's golden outline
            if (playerListPanel != null)
            {
                playerListPanel.SetPlayerReady(playerID, isReady);
            }
            
            // Update button text (for host to show ready count)
            UpdateStartButtonText();
        }

        private void OnInvitePressed()
        {
            CoopMod.Logger.LogInfo("[UI] ========== OnInvitePressed() ==========");
            CoopMod.Logger.LogInfo($"[UI] SteamLobbyManager.IsInLobby = {Network.SteamLobbyManager.Instance?.IsInLobby}");
            
            // Hide the lobby
            Hide();

            // Open the friend invite screen
            var friendInviteGUI = FriendInviteGUI.Instance;
            if (friendInviteGUI == null)
            {
                CoopMod.Logger.LogInfo("[UI] FriendInviteGUI.Instance is null - creating...");
                friendInviteGUI = FriendInviteGUI.Create();
            }

            if (friendInviteGUI != null)
            {
                friendInviteGUI.Open();
            }
            else
            {
                CoopMod.Logger.LogError("Failed to create FriendInviteGUI!");
                // Re-open lobby if creation failed
                Open();
            }
        }

        private void SubscribeLobbyKick()
        {
            var p2p = Network.SteamP2PManager.Instance;
            if (p2p == null) return;

            p2p.OnLobbyKickReceived -= OnLobbyKickReceived;
            p2p.OnLobbyKickReceived += OnLobbyKickReceived;
        }

        private void UnsubscribeLobbyKick()
        {
            var p2p = Network.SteamP2PManager.Instance;
            if (p2p == null) return;

            p2p.OnLobbyKickReceived -= OnLobbyKickReceived;
        }

        private void OnKickPlayerRequested(CSteamID targetID)
        {
            if (!isHost || Network.SteamLobbyManager.Instance?.IsHost != true)
            {
                CoopMod.Logger.LogWarning("[UI] Ignoring kick request because this client is not the lobby host");
                return;
            }

            if (targetID == CSteamID.Nil)
                return;

            string playerName = SteamFriends.GetFriendPersonaName(targetID);
            if (string.IsNullOrEmpty(playerName))
            {
                playerName = targetID.ToString();
            }

            ShowKickConfirmation(targetID, playerName);
        }

        private void ShowKickConfirmation(CSteamID targetID, string playerName)
        {
            pendingKickTarget = targetID;
            pendingKickPlayerName = playerName ?? targetID.ToString();

            string message = $"Kick {pendingKickPlayerName} from Lobby?";
            CoopMod.Logger.LogInfo($"[UI] Showing kick confirmation for {pendingKickPlayerName} ({targetID})");

            var dialog = GUIElements.me?.dialog;
            if (dialog == null)
            {
                CoopMod.Logger.LogWarning("[UI] DialogGUI unavailable; kick confirmation cannot be shown");
                ClearPendingKick();
                return;
            }

            dialog.Open(
                message,
                "Yes",
                new GJCommons.VoidDelegate(OnKickConfirmed),
                "No",
                new GJCommons.VoidDelegate(OnKickCancelled),
                null,
                GameKey.Select,
                GameKey.Back,
                "",
                false,
                "");
        }

        private void OnKickConfirmed()
        {
            CSteamID targetID = pendingKickTarget;
            string playerName = pendingKickPlayerName;
            ClearPendingKick();

            if (targetID == CSteamID.Nil)
                return;

            if (targetID == SteamUser.GetSteamID())
            {
                CoopMod.Logger.LogInfo("[UI] Debug self-kick confirmation accepted; no kick sent for local host");
                return;
            }

            string reason = "You were removed from the lobby by the host.";
            bool sent = Network.SteamP2PManager.Instance.SendLobbyKick(targetID, reason);
            if (sent)
            {
                CoopMod.Logger.LogInfo($"[UI] Kick requested for {playerName} ({targetID})");
                ChatManager.AddMessage($"[System] Removing {playerName} from the lobby...");
                LobbyReadySystem.RemovePlayer(targetID);
            }
            else
            {
                CoopMod.Logger.LogWarning($"[UI] Failed to send lobby kick to {playerName} ({targetID})");
                ChatManager.AddMessage($"[System] Could not remove {playerName} from the lobby.");
            }
        }

        private void OnKickCancelled()
        {
            CoopMod.Logger.LogInfo($"[UI] Kick cancelled for {pendingKickPlayerName} ({pendingKickTarget})");
            ClearPendingKick();
        }

        private void ClearPendingKick()
        {
            pendingKickTarget = CSteamID.Nil;
            pendingKickPlayerName = "";
        }

        private void OnLobbyKickReceived(CSteamID senderID, string reason)
        {
            if (MainGame.game_started && Network.OnlineCoopManager.Instance?.IsOnlineCoopEnabled == true)
            {
                return;
            }

            var lobbyManager = Network.SteamLobbyManager.Instance;
            if (lobbyManager == null || !lobbyManager.IsInLobby)
                return;

            CSteamID lobbyOwner = lobbyManager.GetLobbyOwner();
            if (lobbyOwner != CSteamID.Nil && senderID != lobbyOwner)
            {
                CoopMod.Logger.LogWarning($"[UI] Ignoring LobbyKick from non-host {senderID}; owner is {lobbyOwner}");
                return;
            }

            string message = string.IsNullOrEmpty(reason)
                ? "You were removed from the lobby by the host."
                : reason;

            CoopMod.Logger.LogInfo($"[UI] LobbyKick accepted from host {senderID}: {message}");
            ChatManager.AddMessage($"[System] {message}");

            lobbyManager.LeaveLobby();
            LobbyReadySystem.ResetReadyStates();

            if (Network.SteamP2PManager.Instance != null)
            {
                Network.SteamP2PManager.Instance.OnGameStartReceived -= OnGameStartReceived;
            }

            isMultiplayerSessionActive = false;
            isHost = false;
            isLocalReady = false;
            UnsubscribeLobbyKick();

            if (playerListPanel != null)
            {
                playerListPanel.SetKickButtonsEnabled(false);
                playerListPanel.ClearPlayers();
            }

            Hide();

            if (MultiplayerSubMenuGUI.Instance != null)
            {
                MultiplayerSubMenuGUI.Instance.Open();
            }

            if (GUIElements.me != null && GUIElements.me.dialog != null)
            {
                GUIElements.me.dialog.OpenOK(message);
            }
        }

        private System.Collections.IEnumerator TriggerSaveSlotAfterFrame(SaveSlotData slot)
        {
            // Wait one frame for the GUI to fully open
            yield return null;
            
            // Now trigger the save slot
            if (GUIElements.me != null && GUIElements.me.saves != null)
            {
                CoopMod.Logger.LogInfo($"Auto-triggering save slot: {slot.real_time}");
                GUIElements.me.saves.OnSelectSlotPressed(slot);
            }
        }
        
        private void OnSaveSlotSelected(SaveSlotData slotData)
        {
            string slotName = slotData != null ? slotData.real_time : "New Game";
            CoopMod.Logger.LogInfo($"Save slot selected in lobby: {slotName}");
            
            ChatManager.AddMessage($"[System] Selected: {slotName}");

            // If we're hosting, broadcast the selection so joined clients see the same
            // highlighted slot in their mirrored save list.
            try
            {
                if (Network.SteamLobbyManager.Instance != null && Network.SteamLobbyManager.Instance.IsHost)
                {
                    string filename = slotData != null ? (slotData.filename_no_extension ?? "") : "";
                    Network.HostSaveListSync.HostRememberLocalSelection(filename);
                    Network.HostSaveListSync.HostBroadcastSelected(filename);
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[LobbyGUI] Failed to broadcast host save selection: {ex.Message}");
            }
        }

        public override void Open()
        {
            CoopMod.Logger.LogInfo("[UI] ========== LobbyGUI.Open() ==========");
            CoopMod.Logger.LogInfo($"[UI] SteamManager.Initialized = {SteamManager.Initialized}");
            
            // Mark that we're starting a multiplayer session
            isMultiplayerSessionActive = true;
            CoopMod.Logger.LogInfo("[UI] isMultiplayerSessionActive = true");
            
            // Find UI Camera for coordinate conversion (needed for button clicks)
            if (uiCamera == null)
            {
                uiCamera = NGUITools.FindCameraForLayer(gameObject.layer);
                if (uiCamera != null)
                {
                    CoopMod.Logger.LogInfo($"[UI] UI Camera found for button clicks: {uiCamera.name}");
                }
                else
                {
                    CoopMod.Logger.LogWarning("[UI] Could not find UI Camera for button click detection!");
                }
            }
            
            // Hide main menu first
            if (GUIElements.me?.main_menu != null)
            {
                GUIElements.me.main_menu.Hide(true);
            }

            // Make sure GameObject is active and visible
            gameObject.SetActive(true);
            ApplyResolutionLayout();
            base.Open();
            ConfigureGamepadNavigation();
            
            // Show chat input panel
            if (chatInputPanel != null)
            {
                chatInputPanel.SetActive(true);
            }
            
            InitializeAsHost();
            CoopMod.Logger.LogInfo("[UI] LobbyGUI opened successfully");
        }

        public override void Hide(bool play_sound = true)
        {
            CoopMod.Logger.LogInfo("Hiding LobbyGUI");
            
            // Hide chat input panel
            if (chatInputPanel != null)
            {
                chatInputPanel.SetActive(false);
            }
            
            base.Hide(play_sound);
        }

        /// <summary>
        /// Public method to show the chat input panel without reinitializing the entire GUI.
        /// Used when returning from FriendInviteGUI to preserve lobby state.
        /// </summary>
        public void ShowChatInput()
        {
            ApplyResolutionLayout();

            // Returning from FriendInviteGUI previously only reactivated this
            // GameObject, leaving BaseGUI's shown/controller state disabled.
            if (!is_shown)
            {
                gameObject.SetActive(true);
                base.Open();
                ConfigureGamepadNavigation();
            }

            if (chatInputPanel != null)
            {
                chatInputPanel.SetActive(true);
                CoopMod.Logger.LogInfo("ShowChatInput: Reactivated chat input panel");
            }
            else
            {
                CoopMod.Logger.LogWarning("ShowChatInput: chatInputPanel is null!");
            }
        }

        public void AddPlayer(string playerName)
        {
            if (playerListPanel != null)
            {
                playerListPanel.AddPlayer(playerName);
            }
            // Use ChatManager to sync with in-game chat
            ChatManager.AddMessage($"[System] {playerName} joined");
        }
        
        /// <summary>
        /// Add a player with their actual Steam ID (for online multiplayer)
        /// </summary>
        public void AddPlayerWithSteamID(string playerName, Steamworks.CSteamID steamID)
        {
            if (playerListPanel != null)
            {
                playerListPanel.AddPlayer(playerName, steamID);
            }
            // Use ChatManager to sync with in-game chat
            ChatManager.AddMessage($"[System] {playerName} joined");
            
            // Update button text since player count changed
            // Host button should change from "Start Game" to "Ready" when others join
            UpdateStartButtonText();
        }

        public void RemovePlayer(string playerName)
        {
            if (playerListPanel != null)
            {
                playerListPanel.RemovePlayer(playerName);
            }
            // Use ChatManager to sync with in-game chat
            ChatManager.AddMessage($"[System] {playerName} left");
            
            // Update button text since player count changed
            UpdateStartButtonText();
        }
        
        public void RemovePlayerBySteamID(CSteamID steamID, string playerName)
        {
            if (playerListPanel != null)
            {
                playerListPanel.RemovePlayerBySteamID(steamID);
            }
            // Use ChatManager to sync with in-game chat
            ChatManager.AddMessage($"[System] {playerName} left");
            
            // Update button text since player count changed
            UpdateStartButtonText();
        }
        
        /// <summary>
        /// Called when the host disconnects - kicks the client back to the menu
        /// </summary>
        public void OnHostDisconnected()
        {
            CoopMod.Logger.LogInfo("[UI] Host disconnected - returning to multiplayer menu");
            
            // Clear chat messages
            ChatManager.ClearMessages();
            
            // Reset ready states
            LobbyReadySystem.ResetReadyStates();
            
            // Mark multiplayer session as inactive
            isMultiplayerSessionActive = false;
            
            // Reset local state
            isHost = false;
            isLocalReady = false;
            UnsubscribeLobbyKick();
            if (playerListPanel != null)
            {
                playerListPanel.SetKickButtonsEnabled(false);
            }
            
            Hide();
            
            // Show the multiplayer submenu
            if (MultiplayerSubMenuGUI.Instance != null)
            {
                MultiplayerSubMenuGUI.Instance.Open();
            }

            // Show a "Lobby Closed" dialog so the client knows why they were
            // returned to the menu. Mirrors the in-game host-disconnect dialog
            // (OnlineCoopManager.ShowDisconnectDialogAndReturnToTitle).
            if (GUIElements.me != null && GUIElements.me.dialog != null)
            {
                GUIElements.me.dialog.OpenOK("Lobby Closed");
            }
        }

        /// <summary>
        /// Add the local Player 2 when gamepad button is pressed.
        /// Called when local co-op is enabled and player presses A/X on their gamepad.
        /// </summary>
        private void AddLocalPlayer2()
        {
            CoopMod.Logger.LogInfo("[LocalCoop] Adding Player 2 to lobby...");
            
            // Add Player 2 to the player list with same Steam name but (2) suffix
            if (playerListPanel != null)
            {
                string localPlayerName = SteamHelper.GetLocalPlayerName();
                playerListPanel.AddPlayer($"{localPlayerName} (2)");
            }
            
            // Notify via chat
            ChatManager.AddMessage("[System] Player 2 joined locally!");
            
            // Don't enable local co-op here - it will be enabled automatically when the game loads
            // via LocalCoopActivationPatch when MainGame.SetMainPlayer is called
            
            CoopMod.Logger.LogInfo("[LocalCoop] Player 2 added successfully!");
        }

        public void AddChatMessage(string message)
        {
            CoopMod.Logger.LogInfo($"[LobbyGUI] AddChatMessage called: {message}");
            CoopMod.Logger.LogInfo($"[LobbyGUI] chatPanel is {(chatPanel != null ? "not null" : "NULL")}");
            if (chatPanel != null)
            {
                chatPanel.AddMessage(message);
            }
            else
            {
                CoopMod.Logger.LogWarning("[LobbyGUI] chatPanel is null, cannot add message!");
            }
        }
        
        public void ClearChatMessages()
        {
            if (chatPanel != null)
            {
                chatPanel.ClearMessages();
            }
        }

        private void InitializeAsHost()
        {
            CoopMod.Logger.LogInfo("[UI] ========== InitializeAsHost() ==========");
            CoopMod.Logger.LogInfo($"[UI] ModConfig.EnableLocalCoop.Value = {ModConfig.EnableLocalCoop.Value}");
            
            // Set host flag
            isHost = true;
            isLocalReady = false;
            SubscribeLobbyKick();

            if (saveSelectorPanel != null)
            {
                saveSelectorPanel.SetReadOnlyMirrorMode(false);
            }
            
            // Reset ready states for new lobby
            LobbyReadySystem.ResetReadyStates();
            
            // Subscribe to ready state changes
            LobbyReadySystem.OnPlayerReadyChanged -= OnPlayerReadyChanged;
            LobbyReadySystem.OnPlayerReadyChanged += OnPlayerReadyChanged;
            
            // Create Steam lobby if we're not in local co-op mode
            if (!ModConfig.EnableLocalCoop.Value)
            {
                CoopMod.Logger.LogInfo($"[UI] Creating Steam lobby ({ModConfig.HostedSessionVisibility.Value}, max players: {ModConfig.MaxPlayers.Value})...");
                Network.SteamLobbyManager.Instance.CreateLobby(ModConfig.MaxPlayers.Value);
            }
            
            if (playerListPanel != null)
            {
                playerListPanel.ClearPlayers();
                playerListPanel.SetKickButtonsEnabled(!ModConfig.EnableLocalCoop.Value);
                // Add local player with their Steam username
                string localPlayerName = SteamHelper.GetLocalPlayerName();
                CoopMod.Logger.LogInfo($"[UI] Local player name: {localPlayerName}");
                playerListPanel.AddPlayer(localPlayerName);
            }
            
            // Update button text for host
            UpdateStartButtonText();
            
            // Add system messages via ChatManager (don't clear - messages persist across sessions)
            // Add appropriate messages based on whether we're using local co-op or Steam multiplayer
            if (ModConfig.EnableLocalCoop.Value)
            {
                ChatManager.AddMessage("[System] Local Co-op Session");
                ChatManager.AddMessage("[System] Press [A] (Xbox) or [X] (PlayStation) to add Player 2");
            }
            else
            {
                ChatManager.AddMessage("[System] Creating Steam lobby...");
                ChatManager.AddMessage("[System] Waiting for players...");
            }
            
            CoopMod.Logger.LogInfo("[UI] InitializeAsHost() complete");
        }
        
        /// <summary>
        /// Initialize the lobby as a client (joining an existing lobby)
        /// </summary>
        private void InitializeAsClient(string hostName)
        {
            CoopMod.Logger.LogInfo("[UI] ========== InitializeAsClient() ==========");
            CoopMod.Logger.LogInfo($"[UI] Host name: {hostName}");
            
            // Set client flag
            isHost = false;
            isLocalReady = false;
            SubscribeLobbyKick();

            if (saveSelectorPanel != null)
            {
                saveSelectorPanel.SetReadOnlyMirrorMode(true);
            }
            
            // Reset ready states for new lobby
            LobbyReadySystem.ResetReadyStates();
            
            // Subscribe to ready state changes
            LobbyReadySystem.OnPlayerReadyChanged -= OnPlayerReadyChanged;
            LobbyReadySystem.OnPlayerReadyChanged += OnPlayerReadyChanged;
            
            SubscribeToGameStartReceived();
            
            if (playerListPanel != null)
            {
                playerListPanel.ClearPlayers();
                playerListPanel.SetKickButtonsEnabled(false);
                
                // Add all lobby members from Steam
                var lobbyManager = Network.SteamLobbyManager.Instance;
                int memberCount = lobbyManager.GetLobbyMemberCount();
                CoopMod.Logger.LogInfo($"[UI] Lobby has {memberCount} members");
                
                for (int i = 0; i < memberCount; i++)
                {
                    var memberID = Steamworks.SteamMatchmaking.GetLobbyMemberByIndex(lobbyManager.CurrentLobbyID, i);
                    string memberName = Steamworks.SteamFriends.GetFriendPersonaName(memberID);
                    CoopMod.Logger.LogInfo($"[UI] Adding member {i}: {memberName} (Steam ID: {memberID})");
                    playerListPanel.AddPlayer(memberName, memberID);
                }
            }
            
            // Update button text for client (shows "Ready")
            UpdateStartButtonText();
            
            // Request chat history from host to see previous messages
            CoopMod.Logger.LogInfo("[UI] Requesting chat history from host...");
            LobbyChatSync.RequestChatHistory();
            
            ChatManager.AddMessage($"[System] Joined {hostName}'s lobby!");
            ChatManager.AddMessage("[System] Waiting for host to start the game...");
            
            CoopMod.Logger.LogInfo("[UI] InitializeAsClient() complete");
        }
        
        /// <summary>
        /// Opens the lobby GUI as a client (after successfully joining a lobby)
        /// </summary>
        public void OpenAsClient(string hostName)
        {
            CoopMod.Logger.LogInfo($"[UI] ========== LobbyGUI.OpenAsClient() ==========");
            CoopMod.Logger.LogInfo($"[UI] Host: {hostName}");
            
            // Mark that we're starting a multiplayer session
            isMultiplayerSessionActive = true;
            CoopMod.Logger.LogInfo("[UI] isMultiplayerSessionActive = true");
            
            // Find UI Camera for coordinate conversion (needed for button clicks)
            if (uiCamera == null)
            {
                uiCamera = NGUITools.FindCameraForLayer(gameObject.layer);
                if (uiCamera != null)
                {
                    CoopMod.Logger.LogInfo($"[UI] UI Camera found for button clicks: {uiCamera.name}");
                }
            }
            
            // Hide main menu and JoinGameGUI
            if (GUIElements.me?.main_menu != null)
            {
                GUIElements.me.main_menu.Hide(true);
            }
            
            // Hide JoinGameGUI if it's open
            if (JoinGameGUI.Instance != null && JoinGameGUI.Instance.is_shown)
            {
                JoinGameGUI.Instance.Hide();
            }

            // Make sure GameObject is active and visible
            gameObject.SetActive(true);
            ApplyResolutionLayout();
            base.Open();
            ConfigureGamepadNavigation();
            
            // Show chat input panel
            if (chatInputPanel != null)
            {
                chatInputPanel.SetActive(true);
            }
            
            InitializeAsClient(hostName);
            CoopMod.Logger.LogInfo("[UI] LobbyGUI opened as client successfully");
        }

        protected override bool OnPressedBack()
        {
            OnBackPressed();
            return true;
        }

        public override void OnClosePressed()
        {
            OnBackPressed();
        }
        
        /// <summary>
        /// Clear multiplayer session flag when returning to menu
        /// </summary>
        public static void ClearMultiplayerSession()
        {
            isMultiplayerSessionActive = false;
            CoopMod.Logger.LogInfo("[LobbyGUI] Multiplayer session cleared");
        }
    }
}
