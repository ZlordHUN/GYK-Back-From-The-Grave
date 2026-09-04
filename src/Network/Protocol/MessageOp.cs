namespace GraveyardKeeperCoop.Network
{
    /// <summary>
    /// Binary message opcodes. Each message type has a 1-byte opcode followed by payload.
    /// More efficient than string-based protocol for frequent messages like position updates.
    /// </summary>
    public enum Op : byte
    {
        // Connection/Handshake (1-9)
        Hello = 1,        // Product version + protocol revision admission handshake
        Ping = 2,
        Pong = 9,
        Invite = 3,
        GameStart = 4,
        GameLoaded = 5,
        ReadyToPlay = 6,
        LobbyRequest = 7,  // Request lobby ID from host (for Steam Rich Presence join)
        LobbyInfo = 8,     // Host responds with lobby ID

        // Player State (10-19)
        PlayerPosition = 10,
        PlayerState = 11,
        PlayerAnimation = 12, // Animation state sync (tool use, attack, etc.)
        PlayerParity = 13,    // Full player stat/buff/action parity snapshot

        // Game Sync (20-29)
        TimeSync = 20,
        WGODestroy = 21,
        WeatherSync = 22,
        InventorySync = 23,
        WGOStateSync = 24,
        CraftSync = 25,
        TechSync = 26,
        QuestSync = 27,
        ZoneNavSync = 28,
        CombatSync = 29,

        // Dialogue (30-39)
        DialogueStart = 30,
        DialogueAdvance = 31,
        DialogueEnd = 32,
        DialogueChoice = 33,
        DialogueBubble = 34,
        DialogueOptions = 35,    // Read-only mirror of the initiating player's visible answer list
        DialogueHover = 36,      // Currently highlighted answer index (-1 = none)

        // Save Transfer (40-49)
        SaveRequest = 40,
        SaveStart = 41,
        SaveChunk = 42,
        SaveEnd = 43,
        SaveAck = 44,

        // Chat (50-59)
        ChatMessage = 50,
        ChatHistory = 51,

        // Player Cosmetics (60-69)
        PlayerCosmetics = 60,
        CosmeticsRequest = 61,

        // Cutscene/FlowScript Sync (70-79)
        CutsceneSync = 70,     // FlowScript name + optional origin/facing/zone scope for deferred activation
        CutsceneWalkTo = 71,  // Tell remote player to walk to a position next to the local player and face a direction
        CutsceneComplete = 76,    // FlowScript finished/remotely completed; deferred receivers should skip replay
        CutsceneCamera = 78,      // Mirror live cutscene CameraFlyTo/CameraFlyBack commands

        // Lobby host save slot mirror (72-74)
        HostSaveListRequest = 72, // Client -> host: please send me your save slot list
        HostSaveList = 73,        // Host -> client: here is the list of save slots (+ currently selected)
        HostSaveSelected = 74,    // Host -> all: this is the filename I just selected

        // NPC interaction sync (75)
        NpcInteraction = 75,      // Replay an NPC interaction (dialogue, intro, etc.) on the remote machine

        // Intro skip sync (77, 79)
        SkipIntro = 77,           // Tell remote player to skip the intro cutscene
        IntroSkipState = 79,      // Host-arbitrated intro skip owner and hold progress

        // Player param sync (80)
        PlayerParamSync = 80,
        PlayerVisualSync = 81,

        // Spawn sync (85)
        SpawnSync = 85,

        // Dungeon sync (90)
        DungeonSync = 90,

        // Worker sync (95)
        WorkerSync = 95,

        // Fishing sync (96)
        FishingSync = 96,

        // DLC sync (97)
        DLCSync = 97,

        // Live transform and host-authority lanes (98-100)
        WGOTransformSync = 98,
        InteractionRequest = 99,
        InteractionZeroHp = 100,
        NpcVisualSync = 101,

        // Lobby, reliable-transport, sleep, NPC-flow, trade, save UI, and debug control (102-113)
        LobbyKick = 102,          // Host asks a client to leave the lobby/session
        WorkIndicatorSync = 103,  // Remote player-owned HP/crafting progress indicator
        Heartbeat = 104,          // RNET v2 keepalive, delivered inside the reliable channel
        WorldEntryBarrier = 105,  // Prepare/ack/release barrier for synchronized world entry
        JoinerProfileCommit = 106, // Host save slot + SHA-256 revision; commits joiner-owned inventory
        NpcInteractionComplete = 107, // Temporary NPC visual authority returned after the player-bound flow ends
        SleepSync = 108,          // Host-arbitrated sleep roster, quorum, and synchronized wake
        PlayerTrade = 109,        // Host-coordinated invitations, escrow offers, readiness, and commit
        ManualSaveActivity = 110, // Show/hide the shared vanilla saving indicator
        DebugGiveRequest = 111,   // Client -> host: request an authenticated item grant
        DebugGiveGrant = 112,     // Host -> target client: grant item on its locally-owned player
        DebugGiveResult = 113,    // Host -> requesting client: request acceptance/rejection notice
        FishingPresentation = 114, // Player-owned fishing pose and passive minigame progress

        // 0xFE and 0xFF are permanently reserved for RNET v2 wire frames and must
        // never be assigned as application Op values.
    }
}

