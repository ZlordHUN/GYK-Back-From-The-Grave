# Graveyard Keeper: Back From The Grave

Back From The Grave is an unofficial cooperative multiplayer mod for Graveyard Keeper. It uses Steam lobbies and peer-to-peer networking to run a shared game for one host and one client.

The mod is in beta. Keep backups of important saves and please save regularly!

Current release: **1.0.0 Beta**

## Features

- Steam lobby hosting, invites, server browsing, favorites, and LAN discovery.
- Automatic transfer and loading of the host's selected save.
- Independent local and remote player movement, animation, cosmetics, inventories, and carried items.
- Shared time, weather, quests, technology, crafting, world objects, combat, drops, zones, workers, dungeons, and fishing progression.
- Shared dialogue and cutscenes, including joining an active cutscene when nearby.
- In-game chat, player name tags, chat bubbles, and map indicators.
- Synchronization support for Stranger Sins, Game of Crone, and Better Save Soul.
- Manual save and load interfaces.

## Requirements

- Graveyard Keeper on Steam.
- Steam running and both players online.
- BepInEx 5.4.x for 64-bit Unity Mono.
- The same version of Back From The Grave installed for both players.
- Any DLC enabled by the host must also be owned by the client.

## Installation

Install the mod on both computers.

1. Install BepInEx 5.4.x in the Graveyard Keeper installation directory.
2. Run the game once so BepInEx creates its folders.
3. Download the latest mod DLL from the Releases page.
4. Copy the DLL to the plugins directory of Bepinex.
5. Start the game through Steam.

The main menu should contain a `Multiplayer` entry.

### Linux and Proton

Install the Windows version of BepInEx into the Proton game directory. Add this launch option in Steam:

```text
WINEDLLOVERRIDES="winhttp=n,b" %command% -force-opengl
```

OpenGL helps with performance in my experience. On top of using OpenGL, the use of the following mods is recommended: 
- GYK Helper
- NullFixes
- RestInPatches
- ShowMeMoar - Optional for ultrawide monitor users.

## Starting a game

### Host

1. Open `Multiplayer`.
2. Select `Host Game`.
3. Configure the lobby and create it.
4. Select the save that will be used for the session.
5. Invite a friend or wait for them to join.
6. Start once the client is ready.

The host's selected save is transferred to the client before gameplay begins.

### Client

1. Open `Multiplayer`.
2. Select `Join Game`, accept a Steam invitation, or use the server browser.
3. Join the host's lobby.
4. Mark yourself ready.
5. Wait for the host to start the session.

Do not close the game while a save is being transferred or while the loading screen reports that synchronization is still in progress.

## Save behavior

The host's save is the shared source of world progression when a session starts. The client receives a local multiplayer copy.

Both players can save during a session. Only the host can load another save while online co-op is active, because loading a different client-side world would desynchronize the session.

Keep normal backups of your Graveyard Keeper save directory. This is beta software and synchronization bugs may affect save state.

## Configuration

Most settings are available through `Multiplayer > Settings`. BepInEx also writes them to:

```text
BepInEx/config/com.zlord.graveyardkeeper.coop.cfg
```

Important settings include:

| Setting | Purpose |
| --- | --- |
| `MaxPlayers` | Maximum number of players in the lobby. Development and testing was performed with only 2 players!|
| `HostedSessionVisibility` | Public, friends-only, or private lobby visibility. |
| `EnableLocalCoop` | Enable the experimental same-computer co-op mode. |
| `CameraMode` | Camera mode for local co-op. |
| `EnableDLCStories` | Synchronize Stranger Sins content. |
| `EnableDLCRefugees` | Synchronize Game of Crone content. |
| `EnableDLCSouls` | Synchronize Better Save Soul content. |
| `EnableCheats` | Enable Minecraft Style Cheats|

If one player does not own a DLC, disable that DLC in the host's settings before creating the lobby.

## Current limitations

- Online co-op currently supports one host and one client.
- The Steam version of the game is required.
- The host remains authoritative for shared story and world state.
- Local co-op is experimental and is separate from the supported online two-player flow.
- Compatibility with other gameplay-altering mods is not guaranteed.
- A poor or interrupted connection can delay world synchronization.

## Building from source

The project targets .NET Framework 4.6 and references assemblies from an installed copy of Graveyard Keeper and BepInEx.

### Windows

The default project path expects the standard Steam installation:

```text
C:\Program Files (x86)\Steam\steamapps\common\Graveyard Keeper
```

If the game is installed elsewhere, update `GamePath` in `GraveyardKeeperCoop.csproj`.

Build with:

```powershell
dotnet build
```

### Linux

The project checks common native Steam and Flatpak locations. For a custom installation, set:

```bash
export GRAVEYARD_KEEPER_PATH="/path/to/Graveyard Keeper"
dotnet build
```

Build output remains inside the repository:

```text
bin/Debug/net46/GraveyardKeeperCoop.dll
```


## Project structure

```text
src/
├── Network/       Steam lobby, discovery, transport, and protocol
├── Multiplayer/   Online player management and game-state synchronization
├── Patches/       Harmony integration with Graveyard Keeper
├── UI/            Multiplayer menus, lobby, chat, and overlays
├── LocalCoop/     Experimental local co-op
├── Features/      Save-related features
└── Utils/         Shared helpers
```

See [Documentation/ARCHITECTURE.md](Documentation/ARCHITECTURE.md) for the runtime design.

## Troubleshooting

### Multiplayer does not appear

- Confirm BepInEx is installed and starts with the game.
- Confirm the DLL is under `BepInEx/plugins/GraveyardKeeperCoop/`.
- Check `BepInEx/LogOutput.log` for plugin or Harmony errors.

### A player cannot join

- Confirm both players are running the same mod version.
- Confirm Steam is online for both players.
- Confirm the lobby is not full.
- Confirm the client owns every DLC enabled by the host.

### Linux installation does not load

- Confirm the BepInEx files are in the game directory used by Proton.
- Confirm the `WINEDLLOVERRIDES` launch option is set.

### Reporting bugs

- Please open an issue on Github, provide a description of the bug and attach any relevant logs or screenshots/recordings.

## License

This project is available under the [MIT License](LICENSE).
