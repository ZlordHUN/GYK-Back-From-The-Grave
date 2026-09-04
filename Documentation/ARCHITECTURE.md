# Architecture

Back From The Grave is a BepInEx plugin that adds online co-op to Graveyard Keeper. It uses Steam lobbies for discovery, Steam P2P for transport, and Harmony patches to observe or redirect game behavior.

Each participant runs a complete local copy of the game world. Two-player is the
primary tested topology. Lobbies and several per-peer systems support up to four
players, but 3-4 player gameplay is experimental because some dialogue, cutscene,
work-indicator, and action-presentation paths still assume one primary remote
participant.

## Support boundaries

- Local-network discovery advertises Steam lobby metadata. Joining and gameplay
  still require Steam and use Steam P2P; direct-IP and non-Steam LAN transport are
  not implemented.
- The original host owns the campaign and session authority. Steam lobby ownership
  may move after that host leaves, but the mod does not migrate game authority and
  returns the remaining clients to the menu.
- Joining clients receive the host save for initial convergence. Runtime world
  synchronization uses subsystem events and bounded snapshots; it cannot serialize
  and restore the entire live map on demand.
- Active WorldGameObject state is reconciled through budgeted baselines and deltas.
  Spawns, replacements, transforms, activation, and destruction also have explicit
  messages. A missing snapshot entry is not considered a deletion because the
  object may only be unloaded or inactive.
- Destruction has no durable tombstone/epoch replay yet. If an explicit destruction
  event is missed, absence from a later WGO baseline will not repair it; rejoining
  from the host save is the current recovery path.

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
SteamP2PManager ───────────────► Remote peers
       ▲                              │
       └──────────────────────────────┘
              received state
```

Harmony patches are the integration layer. They detect actions such as crafting, dialogue, quest changes, object replacement, and combat. The matching synchronization module serializes the relevant state and sends it to the relevant peers.

On receipt, the remote module validates the message and applies it to its own game world.

## Host and client responsibilities

The host owns:

- The Steam lobby and session settings.
- The selected save used to start the session.
- Canonical story progression and shared world state.
- Resolution of client requests for shared interactions.

Each client owns:

- Its local player input and presentation.
- Its player position, animation, cosmetics, and player-specific actions.
- Requests for shared interactions that it initiates.

A client can still start conversations, cutscenes, crafting, and world interactions. For state that must have one authoritative result, the client sends a request to the host. The host applies it and broadcasts the resulting canonical state. The presence of a request path does not by itself mean every base-game or DLC script side effect has been campaign-certified.

## Session startup

1. `CoopMod` loads configuration, applies Harmony patches, and creates the persistent managers.
2. The host creates a Steam lobby and selects a save.
3. Clients join and receive the host's lobby settings and save data.
4. The required roster loads the same base world.
5. `OnlineCoopManager` creates remote player representations on each peer.
6. `GameLoadSync` waits until the required roster is loaded and ready.
7. Gameplay synchronization is enabled.

The loading barrier prevents a required participant from entering gameplay while another required participant is still restoring the save or initializing world objects.

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

`WGORegistry`, state baselines, spawn synchronization, replacement synchronization,
transform/activation messages, and destruction synchronization keep these
identities aligned between peers. Destruction is event-driven: state snapshots do
not infer removal from an absent object.

## Dialogue and cutscenes

Dialogue and cutscenes are shared live rather than replayed independently.

- Either participating player can advance dialogue.
- Speech bubbles are routed to the player who advanced the conversation.
- A nearby player can join a cutscene already in progress.
- The joining player is locked, walks toward the initiator, and follows scripted movement.
- Cutscene completion releases control on participating peers.
- Camera targets are mirrored for participating remote players.

Players outside the participation range can observe mirrored presentation while
continuing normal gameplay, but they cannot advance that cutscene's dialogue.

## Source layout

```text
src/
├── Bootstrap/       Plugin initialization and user-facing configuration
├── Features/        Save and standalone gameplay features
├── LocalCoop/       Experimental same-PC input, player, and camera runtime
├── Network/         Diagnostics, discovery, protocol, sessions, and Steam transport
├── Multiplayer/     Chat, gameplay, players, progression, sessions, and world state
├── Patches/         Core, local-co-op, save, UI, and online Harmony integration
├── UI/              Chat, customization, lobby, menus, players, and save screens
└── Utils/           Chat, dialogue, diagnostics, rendering, and Steam helpers
```

`Bootstrap/CoopMod.cs` is the plugin entry point and
`Bootstrap/ModConfig.cs` owns configuration. The network protocol belongs in
`Network/`; game-state ownership and serialization belong in `Multiplayer/`.
Harmony patches should stay small and forward captured changes to those modules.
