# Architecture

Back From The Grave is a BepInEx plugin that adds online co-op to Graveyard Keeper. It uses Steam lobbies for discovery, Steam P2P for transport, and Harmony patches to observe or redirect game behavior.

The current online implementation supports one host and one client. Both run a complete local copy of the game world.

## Runtime overview

```text
Graveyard Keeper
       │
       │ game methods and events
       ▼
Harmony patches
       │
       │ local actions
       ▼
Synchronization modules
       │
       │ binary messages
       ▼
SteamP2PManager ───────────────► Remote peer
       ▲                              │
       └──────────────────────────────┘
              received state
```

Harmony patches are the integration layer. They detect actions such as crafting, dialogue, quest changes, object replacement, and combat. The matching synchronization module serializes the relevant state and sends it to the other peer.

On receipt, the remote module validates the message and applies it to its own game world.

## Host and client responsibilities

The host owns:

- The Steam lobby and session settings.
- The selected save used to start the session.
- Canonical story progression and shared world state.
- Resolution of client requests for shared interactions.

The client owns:

- Its local player input and presentation.
- Its player position, animation, cosmetics, and player-specific actions.
- Requests for shared interactions that it initiates.

A client can still start conversations, cutscenes, crafting, and world interactions. For state that must have one authoritative result, the client sends a request to the host. The host applies it and broadcasts the resulting canonical state.

## Session startup

1. `CoopMod` loads configuration, applies Harmony patches, and creates the persistent managers.
2. The host creates a Steam lobby and selects a save.
3. The client joins and receives the host's lobby settings and save data.
4. Both peers load the same base world.
5. `OnlineCoopManager` creates the remote player representation on each peer.
6. `GameLoadSync` waits until both peers are loaded and ready.
7. Gameplay synchronization is enabled.

The loading barrier prevents either player from entering gameplay while the other is still restoring the save or initializing its world objects.

## Main components

| Component | Responsibility |
| --- | --- |
| `CoopMod` | Plugin entry point and persistent component setup. |
| `SteamLobbyManager` | Lobby creation, joining, metadata, and membership. |
| `SteamP2PManager` | Message transport, dispatch, reliability, and connection health. |
| `BinaryProtocol` | Message opcodes and binary reader/writer helpers. |
| `SaveTransferManager` | Transfers the host save to a joining client. |
| `GameLoadSync` | Coordinates loading and the ready barrier. |
| `OnlineCoopManager` | Remote player lifecycle, movement, following, and session updates. |
| `SyncBehaviour` | Shared lifecycle, sequence checking, and remote-apply protection for sync modules. |
| `WGORegistry` | Tracks world objects by stable identity. |
| `Patches/` | Captures game events and redirects behavior where required. |
| `UI/` | Multiplayer menus, lobby, server browser, chat, and controller navigation. |

## Synchronization model

The mod uses several forms of synchronization because not all state has the same requirements.

### Events

One-time actions are sent when they occur. Examples include dialogue advancement, quest changes, object spawning, object replacement, and cutscene camera changes.

### Periodic snapshots

State that may change through several game paths is compared and sent at an interval. Examples include crafting, technology, player parameters, quests, and zone state.

### Live updates

Movement and visual state are updated frequently. These messages favor responsiveness, while important lifecycle messages use reliable delivery.

### Host-authoritative requests

Some client actions cannot be safely applied independently on both peers. The client sends the intent to the host, the host performs or validates it, and the host sends the canonical result back.

```text
Client action
    │
    ▼
Request to host
    │
    ▼
Host applies shared mutation
    │
    ▼
Canonical result sent to client
```

## Preventing synchronization loops

Applying remote state often calls the same game methods that Harmony patches monitor. Without protection, receiving a message would immediately send it back.

Synchronization modules prevent this with:

- Remote-application guards.
- Per-sender sequence numbers.
- Short echo-suppression windows.
- Checks that distinguish the local player from the remote player proxy.
- Ownership checks for host-authoritative state.

New synchronization code should always define who owns the state and how remote application is prevented from becoming a new local event.

## World object identity

Graveyard Keeper represents most interactive entities as `WorldGameObject` instances. Network messages identify them primarily by `unique_id`.

When that is insufficient, resolution can also use:

- `custom_tag` for named story objects.
- `obj_id` for object type.
- Position as a fallback for otherwise identical objects.

`WGORegistry`, spawn synchronization, replacement synchronization, and destruction synchronization keep these identities aligned between peers.

## Dialogue and cutscenes

Dialogue and cutscenes are shared live rather than replayed independently.

- Either participating player can advance dialogue.
- Speech bubbles are routed to the player who advanced the conversation.
- A nearby player can join a cutscene already in progress.
- The joining player is locked, walks toward the initiator, and follows scripted movement.
- Cutscene completion releases control on both peers.
- Camera targets are mirrored for participating remote players.

Players outside the participation range continue normal gameplay and cannot advance that cutscene's dialogue.

## Source layout

```text
src/
├── CoopMod.cs       Plugin initialization
├── ModConfig.cs     User-facing configuration
├── Network/         Steam lobby, transport, protocol, and discovery
├── Multiplayer/     State synchronization and online player management
├── Patches/         Harmony integration with game behavior
├── UI/              Multiplayer menus and overlays
├── LocalCoop/       Optional local co-op implementation
├── Features/        Save and standalone gameplay features
└── Utils/           Shared helpers
```

The network protocol belongs in `Network/`. Game-state ownership and serialization belong in `Multiplayer/`. Harmony patches should stay small and forward captured changes to those modules.
