using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Steamworks;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Handles transferring save data from host to client.
    /// Save files consist of:
    /// - .info file (JSON metadata)
    /// - .dat file (binary game save)
    /// </summary>
    public static class SaveTransferManager
    {
        // Message types for save transfer
        private const string MSG_SAVE_INFO = "SAVE_INFO";
        private const string MSG_SAVE_DATA_CHUNK = "SAVE_DATA";
        private const string MSG_SAVE_COMPLETE = "SAVE_COMPLETE";
        private const string MSG_SAVE_COOP_POSITIONS = "SAVE_COOP_POSITIONS";
        // Host -> client: stable identity for the save revision that follows.
        // filename_no_extension is [NonSerialized] in SaveSlotData, so it cannot
        // travel inside the vanilla .info JSON.
        private const string MSG_SAVE_IDENTITY = "SAVE_IDENTITY";
        private const string MSG_REQUEST_SAVE = "REQUEST_SAVE";
        // Client -> host: REQUEST_SAVE_HASHED|<cached hash>
        // If the host's current save matches the cached hash, the host responds
        // with MSG_SAVE_CACHED and skips the data transfer entirely.
        private const string MSG_REQUEST_SAVE_HASHED = "REQUEST_SAVE_HASHED";
        // Host -> client: SAVE_CACHED|<infoJson>
        // Tells the client to reuse its locally cached slot for this host.
        private const string MSG_SAVE_CACHED = "SAVE_CACHED";
        private const float SAVE_REQUEST_RETRY_INTERVAL_SECONDS = 5f;
        private const int MAX_SAVE_REQUEST_RETRIES = 6;
        
        // Chunking for large save files (Steam P2P has message size limits)
        private const int CHUNK_SIZE = 32768; // 32KB chunks
        
        // Track transfer progress
        private static string pendingSaveFilename;
        private static string pendingHostSlotFilename;
        private static int pendingWorldSeed = -1;
        private static string pendingLoadSaveHash;
        private static byte[] receivedSaveData;
        private static int expectedChunks;
        private static int receivedChunks;
        private static SaveSlotData pendingSlotData;
        private static bool pendingCoopPositionsReceived;
        private static string pendingCoopPositionsJson;
        private static float lastTransferProgress;
        private static CSteamID pendingRequestHost = CSteamID.Nil;
        private static float nextSaveRequestRetryAt;
        private static int saveRequestRetryCount;
        private static bool transferCompletionReceived;
        private static CSteamID lastInvalidatedPeer = CSteamID.Nil;
        private static float lastInvalidationRestartAt;

        // Client-side: Steam ID of the host we're currently receiving from
        // (used to persist the hash cache once the transfer completes).
        private static CSteamID pendingSenderHost = CSteamID.Nil;
        
        // Host: Save slot to send to clients (set before game starts loading)
        private static SaveSlotData hostSaveSlotToSend;
        private static bool hostSavePreparationInFlight;
        private static string hostSavePreparationSlot = string.Empty;
        private static int hostSavePreparationGeneration;
        private static readonly List<Action<SaveSlotData>> hostSavePreparationWaiters =
            new List<Action<SaveSlotData>>();
        
        // Events
        public static event Action<float> OnTransferProgress;
        public static event Action<SaveSlotData> OnSaveReceived;
        public static event Action<string> OnTransferError;

        public static bool IsTransferActive => expectedChunks > 0 && receivedChunks < expectedChunks;
        public static float LastTransferProgress => lastTransferProgress;
        public static int ReceivedChunks => receivedChunks;
        public static int ExpectedChunks => expectedChunks;
        
        /// <summary>
        /// Host: Set the save slot to send to clients (call before game starts loading)
        /// </summary>
        public static void SetHostSaveSlot(SaveSlotData slot)
        {
            hostSaveSlotToSend = slot;
            CoopMod.Logger.LogInfo($"[SaveTransfer] Host save slot set: {slot?.filename_no_extension ?? "null"}");
        }
        
        /// <summary>
        /// Host: Send save data to a specific client
        /// </summary>
        public static void SendSaveToClient(CSteamID clientID, SaveSlotData slot)
        {
            if (slot == null)
            {
                CoopMod.Logger.LogError("[SaveTransfer] Cannot send null save slot");
                return;
            }
            
            try
            {
                string saveFolder = PlatformSpecific.GetSaveFolder();
                string infoPath = saveFolder + slot.filename_no_extension + ".info";
                string dataPath = saveFolder + slot.filename_no_extension + ".dat";
                
                if (!File.Exists(infoPath) || !File.Exists(dataPath))
                {
                    CoopMod.Logger.LogError($"[SaveTransfer] Save files not found: {slot.filename_no_extension}");
                    return;
                }
                
                // Read files
                string infoJson = File.ReadAllText(infoPath);
                byte[] saveData = File.ReadAllBytes(dataPath);
                int worldSeed = ResolveHostWorldSeed(slot, saveData);
                
                CoopMod.Logger.LogInfo($"[SaveTransfer] Sending save to client. Info: {infoJson.Length} bytes, Data: {saveData.Length} bytes");
                
                SendSaveIdentityToClient(
                    clientID,
                    slot.filename_no_extension,
                    worldSeed);

                // Send info file first
                string infoMessage = $"{MSG_SAVE_INFO}|{infoJson}";
                Network.SteamP2PManager.Instance?.SendP2PMessage(clientID, infoMessage);
                SendCoopPositionsToClient(clientID, slot);
                
                // Calculate chunks
                int totalChunks = (int)Math.Ceiling(saveData.Length / (double)CHUNK_SIZE);
                CoopMod.Logger.LogInfo($"[SaveTransfer] Sending {totalChunks} chunks");
                
                // Send data in chunks
                for (int i = 0; i < totalChunks; i++)
                {
                    int offset = i * CHUNK_SIZE;
                    int length = Math.Min(CHUNK_SIZE, saveData.Length - offset);
                    
                    byte[] chunk = new byte[length];
                    Array.Copy(saveData, offset, chunk, 0, length);
                    
                    // Encode as base64 for safe transmission
                    string chunkBase64 = Convert.ToBase64String(chunk);
                    string chunkMessage = $"{MSG_SAVE_DATA_CHUNK}|{i}|{totalChunks}|{chunkBase64}";
                    
                    Network.SteamP2PManager.Instance?.SendP2PMessage(clientID, chunkMessage);
                }
                
                // Send completion message
                string completeMessage = $"{MSG_SAVE_COMPLETE}|{saveData.Length}";
                Network.SteamP2PManager.Instance?.SendP2PMessage(clientID, completeMessage);
                
                CoopMod.Logger.LogInfo("[SaveTransfer] Save transfer initiated");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[SaveTransfer] Error sending save: {ex.Message}");
            }
        }

        private static void SendCoopPositionsToClient(CSteamID clientID, SaveSlotData slot)
        {
            try
            {
                string sidecarJson = MultiplayerSavePositions.ReadSidecarJson(slot?.filename_no_extension);
                string payload = string.IsNullOrEmpty(sidecarJson)
                    ? string.Empty
                    : Convert.ToBase64String(Encoding.UTF8.GetBytes(sidecarJson));

                Network.SteamP2PManager.Instance?.SendP2PMessage(clientID, $"{MSG_SAVE_COOP_POSITIONS}|{payload}");

                if (string.IsNullOrEmpty(sidecarJson))
                {
                    CoopMod.Logger.LogInfo($"[SaveTransfer] No multiplayer position sidecar for '{slot?.filename_no_extension}', sent clear marker");
                }
                else
                {
                    CoopMod.Logger.LogInfo($"[SaveTransfer] Sent multiplayer position sidecar for '{slot?.filename_no_extension}' ({sidecarJson.Length} bytes)");
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[SaveTransfer] Failed to send multiplayer position sidecar: {ex.Message}");
            }
        }

        private static void SendSaveIdentityToClient(
            CSteamID clientID,
            string hostSlotFilename,
            int worldSeed)
        {
            string normalizedSlot = (hostSlotFilename ?? string.Empty).Trim();
            if (clientID == CSteamID.Nil || string.IsNullOrEmpty(normalizedSlot) ||
                normalizedSlot.Length > 256 || worldSeed < 0)
            {
                CoopMod.Logger.LogWarning(
                    $"[SaveTransfer] Could not send stable save identity " +
                    $"(slot='{normalizedSlot}', world={worldSeed})");
                return;
            }

            string encodedSlot = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(normalizedSlot));
            Network.SteamP2PManager.Instance?.SendP2PMessage(
                clientID,
                $"{MSG_SAVE_IDENTITY}|{encodedSlot}|{worldSeed}");
            CoopMod.Logger.LogInfo(
                $"[SaveTransfer] Sent stable save identity: " +
                $"host_slot='{normalizedSlot}', world={worldSeed}");
        }

        private static int ResolveHostWorldSeed(
            SaveSlotData slot,
            byte[] saveData = null)
        {
            SaveSlotData activeSlot = MainGame.me?.save_slot;
            GameSave activeSave = MainGame.me?.save;
            if (slot != null && activeSlot != null && activeSave != null &&
                string.Equals(
                    slot.filename_no_extension,
                    activeSlot.filename_no_extension,
                    StringComparison.Ordinal))
            {
                return activeSave.dungeon_seed;
            }

            return saveData != null && saveData.Length > 0
                ? SaveHashCache.ComputeWorldSeed(saveData)
                : SaveHashCache.ComputeSlotWorldSeed(
                    slot?.filename_no_extension);
        }
        
        /// <summary>
        /// Client: Request save data from host.
        /// If a cached save exists for this host, the cached hash is sent with the
        /// request; the host will short-circuit with MSG_SAVE_CACHED when the hash
        /// matches its current save, avoiding a full re-download.
        /// </summary>
        public static void RequestSaveFromHost(CSteamID hostID)
        {
            pendingSenderHost = hostID;
            pendingSaveFilename = null;
            pendingHostSlotFilename = null;
            pendingWorldSeed = -1;
            pendingLoadSaveHash = null;
            transferCompletionReceived = false;
            lastInvalidatedPeer = CSteamID.Nil;
            lastInvalidationRestartAt = 0f;
            pendingCoopPositionsReceived = false;
            pendingCoopPositionsJson = null;
            lastTransferProgress = 0f;
            TrackPendingSaveRequest(hostID);
            SendSaveRequest(hostID, retry: false);
        }

        public static void UpdateRetry()
        {
            if (pendingRequestHost == CSteamID.Nil || IsTransferActive)
                return;

            float now = Time.realtimeSinceStartup;
            if (now < nextSaveRequestRetryAt)
                return;

            if (saveRequestRetryCount >= MAX_SAVE_REQUEST_RETRIES)
            {
                string error = $"No save transfer response from host after {MAX_SAVE_REQUEST_RETRIES} retries";
                CoopMod.Logger.LogWarning($"[SaveTransfer] {error}");
                Network.NetworkDiagnostics.RecordError("SaveTransfer", error);
                OnTransferError?.Invoke(error);
                ClearPendingSaveRequest();
                return;
            }

            saveRequestRetryCount++;
            nextSaveRequestRetryAt = now + SAVE_REQUEST_RETRY_INTERVAL_SECONDS;
            SendSaveRequest(pendingRequestHost, retry: true);
        }

        private static void TrackPendingSaveRequest(CSteamID hostID)
        {
            pendingRequestHost = hostID;
            nextSaveRequestRetryAt = Time.realtimeSinceStartup + SAVE_REQUEST_RETRY_INTERVAL_SECONDS;
            saveRequestRetryCount = 0;
        }

        private static void ClearPendingSaveRequest()
        {
            pendingRequestHost = CSteamID.Nil;
            nextSaveRequestRetryAt = 0f;
            saveRequestRetryCount = 0;
        }

        private static void MarkHostSaveResponse(CSteamID senderID)
        {
            if (pendingRequestHost == CSteamID.Nil || pendingRequestHost == senderID)
                ClearPendingSaveRequest();
        }

        private static void MarkTransferComplete()
        {
            transferCompletionReceived = true;
            pendingSenderHost = CSteamID.Nil;
            ClearPendingSaveRequest();
        }

        private static void SendSaveRequest(CSteamID hostID, bool retry)
        {
            var cached = SaveHashCache.Get(hostID.m_SteamID);
            if (cached != null && !string.IsNullOrEmpty(cached.hash))
            {
                if (CachedSaveDataMatchesRecordedHash(cached, out string actualHash))
                {
                    CoopMod.Logger.LogInfo(
                        $"[SaveTransfer] {(retry ? "Retrying" : "Requesting")} save from host (verified cached slot='{cached.slotFilename}', hash={ShortHash(cached.hash)})");
                    Network.SteamP2PManager.Instance?.SendP2PMessage(
                        hostID,
                        $"{MSG_REQUEST_SAVE_HASHED}|{cached.hash}");
                    return;
                }

                CoopMod.Logger.LogWarning(
                    $"[SaveTransfer] Cached save slot '{cached.slotFilename}' changed " +
                    $"after it was downloaded (recorded={ShortHash(cached.hash)}, " +
                    $"actual={ShortHash(actualHash)}); requesting a fresh host copy");
            }

            CoopMod.Logger.LogInfo($"[SaveTransfer] {(retry ? "Retrying" : "Requesting")} save from host (no verified cache)...");
            Network.SteamP2PManager.Instance?.SendP2PMessage(hostID, MSG_REQUEST_SAVE);
        }

        private static bool CachedSaveDataMatchesRecordedHash(
            SaveHashCache.Entry cached,
            out string actualHash)
        {
            actualHash = string.Empty;
            if (cached == null || string.IsNullOrEmpty(cached.slotFilename) ||
                string.IsNullOrEmpty(cached.hash))
            {
                return false;
            }

            actualHash = SaveHashCache.ComputeSlotHash(cached.slotFilename);
            return !string.IsNullOrEmpty(actualHash) &&
                   string.Equals(
                       actualHash,
                       cached.hash,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string ShortHash(string hash)
        {
            if (string.IsNullOrEmpty(hash))
                return "missing";

            return hash.Substring(0, Math.Min(12, hash.Length)) + "...";
        }
        
        /// <summary>
        /// Process incoming save transfer messages
        /// </summary>
        public static void HandleMessage(CSteamID senderID, string message)
        {
            // Order matters: REQUEST_SAVE_HASHED must be checked before REQUEST_SAVE
            // since the former's prefix contains the latter's prefix.
            if (message.StartsWith(MSG_REQUEST_SAVE_HASHED))
            {
                HandleSaveRequestHashed(senderID, message);
            }
            else if (message.StartsWith(MSG_REQUEST_SAVE))
            {
                HandleSaveRequest(senderID);
            }
            else if (message.StartsWith(MSG_SAVE_CACHED))
            {
                MarkHostSaveResponse(senderID);
                HandleSaveCached(senderID, message);
            }
            else if (message.StartsWith(MSG_SAVE_IDENTITY))
            {
                MarkHostSaveResponse(senderID);
                HandleSaveIdentity(senderID, message);
            }
            else if (message.StartsWith(MSG_SAVE_COOP_POSITIONS))
            {
                HandleSaveCoopPositions(message);
            }
            else if (message.StartsWith(MSG_SAVE_INFO))
            {
                MarkHostSaveResponse(senderID);
                HandleSaveInfo(message);
            }
            else if (message.StartsWith(MSG_SAVE_DATA_CHUNK))
            {
                MarkHostSaveResponse(senderID);
                HandleSaveDataChunk(message);
            }
            else if (message.StartsWith(MSG_SAVE_COMPLETE))
            {
                MarkHostSaveResponse(senderID);
                HandleSaveComplete(message);
            }
        }
        
        /// <summary>
        /// Process incoming binary save transfer messages
        /// </summary>
        public static void HandleBinaryMessage(CSteamID senderID, Network.Op op, byte[] data, int length)
        {
            var reader = new Network.MsgReader(data, length);
            
            switch (op)
            {
                case Network.Op.SaveRequest:
                    HandleSaveRequest(senderID);
                    break;
                    
                case Network.Op.SaveStart:
                    MarkHostSaveResponse(senderID);
                    HandleBinarySaveStart(ref reader);
                    break;
                    
                case Network.Op.SaveChunk:
                    MarkHostSaveResponse(senderID);
                    HandleBinarySaveChunk(ref reader);
                    break;
                    
                case Network.Op.SaveEnd:
                    MarkHostSaveResponse(senderID);
                    HandleBinarySaveEnd(ref reader);
                    break;
                    
                case Network.Op.SaveAck:
                    // Acknowledgment received
                    break;
            }
        }
        
        private static void HandleBinarySaveStart(ref Network.MsgReader reader)
        {
            try
            {
                string infoJson = reader.ReadString();
                int totalBytes = reader.ReadInt32();
                int totalChunks = reader.ReadInt32();
                
                pendingSlotData = SaveSlotData.FromJSON(infoJson);
                if (string.IsNullOrEmpty(pendingHostSlotFilename))
                    pendingHostSlotFilename = pendingSlotData.filename_no_extension;
                pendingSaveFilename = ResolvePendingLocalSlotFilename();
                pendingSlotData.filename_no_extension = pendingSaveFilename;
                
                receivedSaveData = new byte[totalBytes];
                expectedChunks = totalChunks;
                receivedChunks = 0;
                pendingCoopPositionsReceived = false;
                pendingCoopPositionsJson = null;
                lastTransferProgress = 0f;
                transferCompletionReceived = false;
                
                CoopMod.Logger.LogInfo($"[SaveTransfer] Binary transfer started: {totalBytes} bytes in {totalChunks} chunks");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[SaveTransfer] Error parsing binary save start: {ex.Message}");
                OnTransferError?.Invoke("Failed to start binary save transfer");
            }
        }
        
        private static void HandleBinarySaveChunk(ref Network.MsgReader reader)
        {
            try
            {
                int chunkIndex = reader.ReadInt32();
                int chunkSize = reader.ReadInt32();
                byte[] chunkData = reader.ReadRawBytes(chunkSize);
                
                int offset = chunkIndex * CHUNK_SIZE;
                Array.Copy(chunkData, 0, receivedSaveData, offset, chunkData.Length);
                
                receivedChunks++;
                
                float progress = (float)receivedChunks / expectedChunks;
                lastTransferProgress = progress;
                OnTransferProgress?.Invoke(progress);
                
                if (receivedChunks % 10 == 0 || receivedChunks == expectedChunks)
                {
                    CoopMod.Logger.LogInfo($"[SaveTransfer] Binary chunk {receivedChunks}/{expectedChunks}");
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[SaveTransfer] Error processing binary chunk: {ex.Message}");
            }
        }
        
        private static void HandleBinarySaveEnd(ref Network.MsgReader reader)
        {
            try
            {
                int totalBytes = reader.ReadInt32();
                
                CoopMod.Logger.LogInfo($"[SaveTransfer] Binary transfer complete: {totalBytes} bytes");
                lastTransferProgress = 1f;
                
                byte[] finalData = new byte[totalBytes];
                Array.Copy(receivedSaveData, finalData, totalBytes);
                
                if (!WriteSaveToDisk(pendingSlotData, finalData))
                    return;

                try
                {
                    if (pendingSenderHost != CSteamID.Nil && pendingSlotData != null)
                    {
                        string hash = SaveHashCache.ComputeHash(finalData);
                        pendingLoadSaveHash = hash;
                        SaveHashCache.Put(
                            pendingSenderHost.m_SteamID,
                            pendingHostSlotFilename,
                            pendingWorldSeed,
                            pendingSlotData.filename_no_extension,
                            hash,
                            pendingSlotData.real_time);
                    }
                }
                catch (Exception cacheEx)
                {
                    CoopMod.Logger.LogWarning($"[SaveTransfer] Could not update hash cache (binary): {cacheEx.Message}");
                }

                MarkTransferComplete();
                OnSaveReceived?.Invoke(pendingSlotData);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[SaveTransfer] Error completing binary transfer: {ex.Message}");
                OnTransferError?.Invoke("Failed to complete binary save transfer");
            }
        }
        
        /// <summary>
        /// Check if a message is a save transfer message
        /// </summary>
        public static bool IsSaveTransferMessage(string message)
        {
            return message.StartsWith(MSG_REQUEST_SAVE) ||    // also matches MSG_REQUEST_SAVE_HASHED
                   message.StartsWith(MSG_SAVE_INFO) ||
                   message.StartsWith(MSG_SAVE_DATA_CHUNK) ||
                   message.StartsWith(MSG_SAVE_COMPLETE) ||
                   message.StartsWith(MSG_SAVE_COOP_POSITIONS) ||
                   message.StartsWith(MSG_SAVE_IDENTITY) ||
                   message.StartsWith(MSG_SAVE_CACHED);
        }
        
        private static void HandleSaveRequest(CSteamID clientID)
        {
            CoopMod.Logger.LogInfo($"[SaveTransfer] Received save request from {clientID}");
            
            // First try the host's pre-set save slot, then fall back to active save
            SaveSlotData slotToSend = GetHostSlotToSend();
            
            if (slotToSend != null)
            {
                GameLoadSync.Instance?.NotifyHostSaveTransferStarted();
                PrepareHostSlotForTransfer(slotToSend, (readySlot) => SendSaveToClient(clientID, readySlot));
            }
            else
            {
                CoopMod.Logger.LogError("[SaveTransfer] No save slot available to send!");
            }
        }

        /// <summary>
        /// Host: client sent REQUEST_SAVE_HASHED|&lt;clientCachedHash&gt;.
        /// If our current save data hashes to the same value, reply with
        /// SAVE_CACHED|&lt;infoJson&gt; so the client can reuse its cached slot.
        /// Otherwise fall back to the normal full transfer.
        /// </summary>
        private static void HandleSaveRequestHashed(CSteamID clientID, string message)
        {
            string clientHash = string.Empty;
            int sep = message.IndexOf('|');
            if (sep >= 0 && sep < message.Length - 1)
            {
                clientHash = message.Substring(sep + 1);
            }

            SaveSlotData slotToSend = GetHostSlotToSend();
            if (slotToSend == null)
            {
                CoopMod.Logger.LogError("[SaveTransfer] (hashed req) No save slot available to send!");
                return;
            }

            GameLoadSync.Instance?.NotifyHostSaveTransferStarted();

            PrepareHostSlotForTransfer(slotToSend, (readySlot) =>
            {
                string hostHash = SaveHashCache.ComputeSlotHash(readySlot.filename_no_extension);

                if (!string.IsNullOrEmpty(clientHash) && !string.IsNullOrEmpty(hostHash) && hostHash == clientHash)
                {
                    try
                    {
                        string infoPath = PlatformSpecific.GetSaveFolder() + readySlot.filename_no_extension + ".info";
                        string infoJson = File.Exists(infoPath) ? File.ReadAllText(infoPath) : readySlot.ToJSON();
                        string response = $"{MSG_SAVE_CACHED}|{infoJson}";
                        SendSaveIdentityToClient(
                            clientID,
                            readySlot.filename_no_extension,
                            ResolveHostWorldSeed(readySlot));
                        CoopMod.Logger.LogInfo(
                            $"[SaveTransfer] Client's cached hash matches host save - sending SAVE_CACHED (skip transfer, hash={hostHash.Substring(0, Math.Min(12, hostHash.Length))}...)");
                        SendCoopPositionsToClient(clientID, readySlot);
                        Network.SteamP2PManager.Instance?.SendP2PMessage(clientID, response);
                        return;
                    }
                    catch (Exception ex)
                    {
                        CoopMod.Logger.LogError($"[SaveTransfer] Error building SAVE_CACHED response: {ex.Message}");
                        // fall through to full transfer
                    }
                }

                string clientHashShort = clientHash.Length > 12 ? clientHash.Substring(0, 12) + "..." : clientHash;
                string hostHashShort = hostHash.Length > 12 ? hostHash.Substring(0, 12) + "..." : hostHash;
                CoopMod.Logger.LogInfo(
                    $"[SaveTransfer] Hash mismatch or missing (clientHash='{clientHashShort}', hostHash='{hostHashShort}') - performing full transfer");
                SendSaveToClient(clientID, readySlot);
            });
        }

        private static SaveSlotData GetHostSlotToSend()
        {
            return hostSaveSlotToSend ?? MainGame.me?.save_slot;
        }

        private static void PrepareHostSlotForTransfer(SaveSlotData slot, Action<SaveSlotData> onReady)
        {
            if (slot == null)
            {
                CoopMod.Logger.LogError("[SaveTransfer] Cannot prepare null host save slot");
                return;
            }

            int preparationGeneration = -1;
            try
            {
                bool isActiveRunningSave = MainGame.game_started &&
                                           MainGame.me != null &&
                                           MainGame.me.save != null &&
                                           MainGame.me.save_slot != null &&
                                           MainGame.me.save_slot.filename_no_extension == slot.filename_no_extension;

                if (!isActiveRunningSave)
                {
                    onReady?.Invoke(slot);
                    return;
                }

                if (hostSavePreparationInFlight &&
                    string.Equals(
                        hostSavePreparationSlot,
                        slot.filename_no_extension,
                        StringComparison.Ordinal))
                {
                    if (onReady != null)
                        hostSavePreparationWaiters.Add(onReady);
                    CoopMod.Logger.LogInfo(
                        $"[SaveTransfer] Coalesced save request while preparing " +
                        $"'{slot.filename_no_extension}' " +
                        $"(waiting clients={hostSavePreparationWaiters.Count})");
                    return;
                }

                if (hostSavePreparationInFlight)
                {
                    CoopMod.Logger.LogWarning(
                        $"[SaveTransfer] Cannot prepare '{slot.filename_no_extension}' " +
                        $"while '{hostSavePreparationSlot}' is still in flight");
                    return;
                }

                hostSavePreparationInFlight = true;
                hostSavePreparationSlot = slot.filename_no_extension ?? string.Empty;
                preparationGeneration = ++hostSavePreparationGeneration;
                hostSavePreparationWaiters.Clear();
                if (onReady != null)
                    hostSavePreparationWaiters.Add(onReady);

                CoopMod.Logger.LogInfo($"[SaveTransfer] Saving current host game before transfer: {slot.filename_no_extension}");
                MainGame.me.save.PrepareForSave();
                CoopMod.Logger.LogInfo($"[SaveTransfer] Prepared active host save before transfer. Local player position: {MainGame.me.save.player_position}");
                PlatformSpecific.SaveGame(slot, MainGame.me.save, (savedSlot) =>
                {
                    if (preparationGeneration != hostSavePreparationGeneration)
                    {
                        CoopMod.Logger.LogInfo(
                            "[SaveTransfer] Ignoring stale host-save callback from a previous session");
                        return;
                    }

                    if (savedSlot == null)
                    {
                        CompleteHostSavePreparation(null);
                        CoopMod.Logger.LogError("[SaveTransfer] Failed to save current host game before transfer");
                        OnTransferError?.Invoke("Host failed to save current game before transfer");
                        return;
                    }

                    // Vanilla invokes this callback even after a caught disk exception.
                    // The SaveGameDataToSlot postfix verifies the final .dat and releases
                    // the waiting transfers through NotifyHostSaveWriteCompleted.
                });
            }
            catch (Exception ex)
            {
                if (preparationGeneration < 0 ||
                    preparationGeneration == hostSavePreparationGeneration)
                {
                    CompleteHostSavePreparation(null);
                }
                CoopMod.Logger.LogError($"[SaveTransfer] Error preparing host save for transfer: {ex.Message}");
                OnTransferError?.Invoke("Host failed to prepare save transfer");
            }
        }

        private static void CompleteHostSavePreparation(SaveSlotData savedSlot)
        {
            Action<SaveSlotData>[] waiters = hostSavePreparationWaiters.ToArray();
            hostSavePreparationWaiters.Clear();
            hostSavePreparationInFlight = false;
            hostSavePreparationSlot = string.Empty;

            if (savedSlot == null)
                return;

            for (int i = 0; i < waiters.Length; i++)
            {
                try
                {
                    waiters[i]?.Invoke(savedSlot);
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogError(
                        $"[SaveTransfer] Failed to serve a waiting client: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Called by the save writer postfix after it verifies whether the final host
        /// .dat was actually replaced. A verified profile commit is queued before this
        /// releases save-transfer waiters, preserving transaction order on the wire.
        /// </summary>
        internal static void NotifyHostSaveWriteCompleted(
            SaveSlotData savedSlot,
            bool succeeded)
        {
            if (!hostSavePreparationInFlight || savedSlot == null ||
                !string.Equals(
                    hostSavePreparationSlot,
                    savedSlot.filename_no_extension,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (!succeeded)
            {
                CompleteHostSavePreparation(null);
                CoopMod.Logger.LogError(
                    "[SaveTransfer] Verified host save write failed before transfer");
                OnTransferError?.Invoke("Host failed to save current game before transfer");
                return;
            }

            hostSaveSlotToSend = savedSlot;
            MultiplayerSavePositions.CaptureCurrentSession(savedSlot);
            CompleteHostSavePreparation(savedSlot);
        }

        /// <summary>
        /// Client: host said our cache is up-to-date. Reuse the previously cached
        /// local slot for this host and proceed to load the game as if the transfer
        /// had completed normally.
        /// </summary>
        private static void HandleSaveCached(CSteamID senderID, string message)
        {
            try
            {
                var cached = GetPendingCacheEntry(senderID);
                if (cached == null || !File.Exists(PlatformSpecific.GetSaveFolder() + cached.slotFilename + ".dat"))
                {
                    CoopMod.Logger.LogWarning("[SaveTransfer] Host said SAVE_CACHED but local cache entry is missing - requesting full transfer");
                    TrackPendingSaveRequest(senderID);
                    Network.SteamP2PManager.Instance?.SendP2PMessage(senderID, MSG_REQUEST_SAVE);
                    return;
                }

                // The mirror can be changed after download (for example, vanilla
                // SleepGUI autosaves when a sleeping client is disconnected). The
                // host only validated the hash value we advertised, so independently
                // verify the bytes again before trusting its SAVE_CACHED response.
                if (!CachedSaveDataMatchesRecordedHash(cached, out string actualHash))
                {
                    CoopMod.Logger.LogWarning(
                        $"[SaveTransfer] Refusing changed cached save slot " +
                        $"'{cached.slotFilename}' after SAVE_CACHED " +
                        $"(recorded={ShortHash(cached.hash)}, " +
                        $"actual={ShortHash(actualHash)}); requesting full transfer");
                    TrackPendingSaveRequest(senderID);
                    Network.SteamP2PManager.Instance?.SendP2PMessage(
                        senderID,
                        MSG_REQUEST_SAVE);
                    return;
                }

                // Parse optional info JSON (for updated real_time / metadata)
                SaveSlotData slotData = null;
                string hostSlotFilename = pendingHostSlotFilename ?? string.Empty;
                int sep = message.IndexOf('|');
                if (sep >= 0 && sep < message.Length - 1)
                {
                    try
                    {
                        slotData = SaveSlotData.FromJSON(message.Substring(sep + 1));
                        if (string.IsNullOrEmpty(hostSlotFilename))
                            hostSlotFilename = slotData.filename_no_extension;
                        // Persist freshened info so the slot list stays in sync
                        try
                        {
                            string infoPath = PlatformSpecific.GetSaveFolder() + cached.slotFilename + ".info";
                            slotData.filename_no_extension = cached.slotFilename;
                            File.WriteAllText(infoPath, slotData.ToJSON());
                        }
                        catch (Exception writeEx)
                        {
                            CoopMod.Logger.LogWarning($"[SaveTransfer] Could not refresh cached .info: {writeEx.Message}");
                        }
                    }
                    catch (Exception parseEx)
                    {
                        CoopMod.Logger.LogWarning($"[SaveTransfer] Could not parse SAVE_CACHED info JSON: {parseEx.Message}");
                    }
                }

                if (slotData == null)
                {
                    // Fall back to reading the cached .info from disk
                    try
                    {
                        string infoPath = PlatformSpecific.GetSaveFolder() + cached.slotFilename + ".info";
                        slotData = SaveSlotData.FromJSON(File.ReadAllText(infoPath));
                        slotData.filename_no_extension = cached.slotFilename;
                    }
                    catch (Exception ex)
                    {
                        CoopMod.Logger.LogError($"[SaveTransfer] Failed to load cached slot info: {ex.Message}");
                        Network.SteamP2PManager.Instance?.SendP2PMessage(senderID, MSG_REQUEST_SAVE);
                        return;
                    }
                }

                CoopMod.Logger.LogInfo($"[SaveTransfer] Using cached save slot '{cached.slotFilename}' (skipped download)");
                ApplyPendingCoopPositionsToSlot(cached.slotFilename);
                pendingSlotData = slotData;
                pendingHostSlotFilename = string.IsNullOrEmpty(hostSlotFilename)
                    ? cached.slotFilename
                    : hostSlotFilename;
                pendingLoadSaveHash = cached.hash;
                MarkTransferComplete();
                OnSaveReceived?.Invoke(slotData);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[SaveTransfer] Error handling SAVE_CACHED: {ex.Message}");
                OnTransferError?.Invoke("Failed to reuse cached save");
            }
        }

        private static void HandleSaveIdentity(
            CSteamID senderID,
            string message)
        {
            if (senderID == CSteamID.Nil ||
                (pendingSenderHost != CSteamID.Nil &&
                 senderID != pendingSenderHost))
            {
                CoopMod.Logger.LogWarning(
                    $"[SaveTransfer] Ignored save identity from unexpected peer {senderID}");
                return;
            }

            try
            {
                string[] parts = message.Split('|');
                if (parts.Length != 3 ||
                    !int.TryParse(parts[2], out int worldSeed) ||
                    worldSeed < 0)
                {
                    throw new FormatException("invalid identity fields");
                }

                string hostSlotFilename = Encoding.UTF8.GetString(
                    Convert.FromBase64String(parts[1])).Trim();
                if (string.IsNullOrEmpty(hostSlotFilename) ||
                    hostSlotFilename.Length > 256 ||
                    hostSlotFilename.IndexOfAny(
                        new[] { '\t', '\r', '\n' }) >= 0)
                {
                    throw new FormatException("invalid host slot");
                }

                pendingHostSlotFilename = hostSlotFilename;
                pendingWorldSeed = worldSeed;
                SaveHashCache.Entry cached = SaveHashCache.Get(
                    senderID.m_SteamID,
                    hostSlotFilename,
                    worldSeed);
                pendingSaveFilename = cached?.slotFilename;

                CoopMod.Logger.LogInfo(
                    $"[SaveTransfer] Received stable save identity: " +
                    $"host_slot='{hostSlotFilename}', world={worldSeed}, " +
                    $"local_slot='{pendingSaveFilename ?? "new"}'");
            }
            catch (Exception ex)
            {
                pendingHostSlotFilename = null;
                pendingWorldSeed = -1;
                pendingSaveFilename = null;
                CoopMod.Logger.LogWarning(
                    $"[SaveTransfer] Rejected malformed save identity: {ex.Message}");
            }
        }

        private static SaveHashCache.Entry GetPendingCacheEntry(CSteamID host)
        {
            if (host != CSteamID.Nil &&
                !string.IsNullOrEmpty(pendingHostSlotFilename) &&
                pendingWorldSeed >= 0)
            {
                return SaveHashCache.Get(
                    host.m_SteamID,
                    pendingHostSlotFilename,
                    pendingWorldSeed);
            }

            return host != CSteamID.Nil
                ? SaveHashCache.Get(host.m_SteamID)
                : null;
        }

        private static void HandleSaveCoopPositions(string message)
        {
            try
            {
                int sep = message.IndexOf('|');
                string payload = sep >= 0 && sep < message.Length - 1
                    ? message.Substring(sep + 1)
                    : string.Empty;

                pendingCoopPositionsReceived = true;
                if (string.IsNullOrEmpty(payload))
                {
                    pendingCoopPositionsJson = null;
                    CoopMod.Logger.LogInfo("[SaveTransfer] Received empty multiplayer position sidecar marker");
                    return;
                }

                pendingCoopPositionsJson = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                CoopMod.Logger.LogInfo($"[SaveTransfer] Received multiplayer position sidecar ({pendingCoopPositionsJson.Length} bytes)");
            }
            catch (Exception ex)
            {
                pendingCoopPositionsReceived = false;
                pendingCoopPositionsJson = null;
                CoopMod.Logger.LogWarning($"[SaveTransfer] Error handling multiplayer position sidecar: {ex.Message}");
            }
        }

        private static void ApplyPendingCoopPositionsToSlot(string slotFilename)
        {
            if (!pendingCoopPositionsReceived || string.IsNullOrEmpty(slotFilename))
            {
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(pendingCoopPositionsJson))
                {
                    MultiplayerSavePositions.DeleteSidecar(slotFilename);
                    return;
                }

                MultiplayerSavePositions.WriteSidecarJson(slotFilename, pendingCoopPositionsJson);
            }
            finally
            {
                pendingCoopPositionsReceived = false;
                pendingCoopPositionsJson = null;
            }
        }

        private static string ResolvePendingLocalSlotFilename()
        {
            if (!string.IsNullOrEmpty(pendingSaveFilename))
                return pendingSaveFilename;

            // Reuse is safe only after a current host explicitly identified the
            // campaign. An older host sends no marker; selecting its most-recent
            // host-only cache could overwrite a different world.
            if (pendingSenderHost != CSteamID.Nil &&
                !string.IsNullOrEmpty(pendingHostSlotFilename) &&
                pendingWorldSeed >= 0)
            {
                SaveHashCache.Entry cached = SaveHashCache.Get(
                    pendingSenderHost.m_SteamID,
                    pendingHostSlotFilename,
                    pendingWorldSeed);
                if (cached != null &&
                    !string.IsNullOrEmpty(cached.slotFilename))
                {
                    return cached.slotFilename;
                }
            }

            string saveFolder = PlatformSpecific.GetSaveFolder();
            string baseName = "coop_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string candidate = baseName;
            int suffix = 2;
            while (File.Exists(saveFolder + candidate + ".dat") ||
                   File.Exists(saveFolder + candidate + ".info"))
            {
                candidate = baseName + "_" + suffix.ToString();
                suffix++;
            }
            return candidate;
        }
        
        private static void HandleSaveInfo(string message)
        {
            try
            {
                // Parse: SAVE_INFO|{json}
                string json = message.Substring(MSG_SAVE_INFO.Length + 1);
                pendingSlotData = SaveSlotData.FromJSON(json);
                if (string.IsNullOrEmpty(pendingHostSlotFilename))
                    pendingHostSlotFilename = pendingSlotData.filename_no_extension;
                
                // Reuse the local mirror when this exact host campaign was seen before.
                pendingSaveFilename = ResolvePendingLocalSlotFilename();
                pendingSlotData.filename_no_extension = pendingSaveFilename;
                
                // Reset transfer state
                receivedSaveData = null;
                receivedChunks = 0;
                expectedChunks = 0;
                lastTransferProgress = 0f;
                transferCompletionReceived = false;
                
                CoopMod.Logger.LogInfo($"[SaveTransfer] Received save info: {pendingSlotData.real_time}");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[SaveTransfer] Error parsing save info: {ex.Message}");
                OnTransferError?.Invoke("Failed to parse save info");
            }
        }
        
        private static void HandleSaveDataChunk(string message)
        {
            try
            {
                // Parse: SAVE_DATA|chunkIndex|totalChunks|base64Data
                string[] parts = message.Split('|');
                if (parts.Length < 4)
                {
                    CoopMod.Logger.LogError("[SaveTransfer] Invalid chunk message format");
                    return;
                }
                
                int chunkIndex = int.Parse(parts[1]);
                int totalChunks = int.Parse(parts[2]);
                string base64Data = parts[3];
                
                // Initialize buffer on first chunk
                if (receivedSaveData == null)
                {
                    expectedChunks = totalChunks;
                    receivedSaveData = new byte[totalChunks * CHUNK_SIZE]; // Over-allocate, trim later
                }
                
                // Decode and store chunk
                byte[] chunkData = Convert.FromBase64String(base64Data);
                int offset = chunkIndex * CHUNK_SIZE;
                Array.Copy(chunkData, 0, receivedSaveData, offset, chunkData.Length);
                
                receivedChunks++;
                
                // Report progress
                float progress = (float)receivedChunks / expectedChunks;
                lastTransferProgress = progress;
                OnTransferProgress?.Invoke(progress);
                
                if (receivedChunks % 10 == 0 || receivedChunks == expectedChunks)
                {
                    CoopMod.Logger.LogInfo($"[SaveTransfer] Received chunk {receivedChunks}/{expectedChunks}");
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[SaveTransfer] Error processing chunk: {ex.Message}");
            }
        }
        
        private static void HandleSaveComplete(string message)
        {
            try
            {
                // Parse: SAVE_COMPLETE|totalBytes
                string[] parts = message.Split('|');
                int totalBytes = int.Parse(parts[1]);
                
                CoopMod.Logger.LogInfo($"[SaveTransfer] Transfer complete! Total bytes: {totalBytes}");
                lastTransferProgress = 1f;
                
                // Trim the buffer to actual size
                byte[] finalData = new byte[totalBytes];
                Array.Copy(receivedSaveData, finalData, totalBytes);
                
                // Write files to disk
                if (!WriteSaveToDisk(pendingSlotData, finalData))
                    return;

                // Persist hash so next session can skip the re-download
                try
                {
                    if (pendingSenderHost != CSteamID.Nil && pendingSlotData != null)
                    {
                        string hash = SaveHashCache.ComputeHash(finalData);
                        pendingLoadSaveHash = hash;
                        SaveHashCache.Put(
                            pendingSenderHost.m_SteamID,
                            pendingHostSlotFilename,
                            pendingWorldSeed,
                            pendingSlotData.filename_no_extension,
                            hash,
                            pendingSlotData.real_time);
                    }
                }
                catch (Exception cacheEx)
                {
                    CoopMod.Logger.LogWarning($"[SaveTransfer] Could not update hash cache: {cacheEx.Message}");
                }

                MarkTransferComplete();

                // Notify listeners
                OnSaveReceived?.Invoke(pendingSlotData);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[SaveTransfer] Error completing transfer: {ex.Message}");
                OnTransferError?.Invoke("Failed to complete save transfer");
            }
        }
        
        private static bool WriteSaveToDisk(SaveSlotData slotData, byte[] saveData)
        {
            try
            {
                string saveFolder = PlatformSpecific.GetSaveFolder();
                string infoPath = saveFolder + slotData.filename_no_extension + ".info";
                string dataPath = saveFolder + slotData.filename_no_extension + ".dat";
                
                // Write info file
                string infoJson = slotData.ToJSON();
                File.WriteAllText(infoPath, infoJson);
                
                // Write data file
                File.WriteAllBytes(dataPath, saveData);
                ApplyPendingCoopPositionsToSlot(slotData.filename_no_extension);
                
                CoopMod.Logger.LogInfo($"[SaveTransfer] Save written to disk: {slotData.filename_no_extension}");
                return true;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[SaveTransfer] Error writing save to disk: {ex.Message}");
                OnTransferError?.Invoke("Failed to write save file");
                return false;
            }
        }
        
        /// <summary>
        /// Load the received save and start the game
        /// </summary>
        public static void LoadReceivedSave(SaveSlotData slotData)
        {
            if (slotData == null)
            {
                CoopMod.Logger.LogError("[SaveTransfer] No save data to load!");
                return;
            }
            
            CoopMod.Logger.LogInfo($"[SaveTransfer] Loading received save: {slotData.filename_no_extension}");

            string saveHash = pendingLoadSaveHash;
            if (string.IsNullOrEmpty(saveHash))
                saveHash = SaveHashCache.ComputeSlotHash(slotData.filename_no_extension);
            
            // Use the game's normal load flow
            if (GUIElements.me?.saves != null)
            {
                // Select the saved personal profile matching this exact host slot and
                // .dat revision. Unsaved live changes are intentionally not captured.
                JoinerProfileManager.Instance?.OnHostSaveLoadStartedAsJoiner(
                    pendingHostSlotFilename,
                    saveHash);
                GameLoadSync.Instance?.PrepareForReceivedSaveLoad();
                GUIElements.me.saves.OnSelectSlotPressed(slotData);
            }
            else
            {
                CoopMod.Logger.LogError("[SaveTransfer] SaveSlotsMenuGUI not available!");
            }
        }

        /// <summary>
        /// Compatibility hook for a transport that has explicitly abandoned accepted reliable
        /// messages while realigning its channel. RNET v2 retains its window and therefore does
        /// not call this during ordinary degradation/recovery. A future transport that discards
        /// delivery state must invoke this once for the affected peer.
        /// </summary>
        public static void OnReliableChannelResynced(CSteamID peer)
        {
            if (peer == CSteamID.Nil || pendingSenderHost == CSteamID.Nil ||
                peer != pendingSenderHost || transferCompletionReceived)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (lastInvalidatedPeer == peer && now - lastInvalidationRestartAt < 1f)
                return;

            lastInvalidatedPeer = peer;
            lastInvalidationRestartAt = now;

            CoopMod.Logger.LogWarning(
                $"[SaveTransfer] Reliable delivery state was abandoned during transfer " +
                $"({receivedChunks}/{expectedChunks} chunks) - restarting from the host");

            pendingSaveFilename = null;
            pendingHostSlotFilename = null;
            pendingWorldSeed = -1;
            pendingLoadSaveHash = null;
            receivedSaveData = null;
            expectedChunks = 0;
            receivedChunks = 0;
            pendingSlotData = null;
            pendingCoopPositionsReceived = false;
            pendingCoopPositionsJson = null;
            lastTransferProgress = 0f;
            OnTransferProgress?.Invoke(0f);

            // A direct one-off send, as used by the public v1 implementation, leaves the
            // retry state disarmed after the first host response. Rearm it before sending.
            TrackPendingSaveRequest(peer);
            SendSaveRequest(peer, retry: true);
        }

        /// <summary>
        /// Abort client receive state when the host is no longer available. Unlike a transport
        /// delivery invalidation, a departed peer cannot service an immediate restart request.
        /// </summary>
        public static void OnPeerUnavailable(CSteamID peer, string reason)
        {
            if (peer == CSteamID.Nil || transferCompletionReceived ||
                (peer != pendingSenderHost && peer != pendingRequestHost))
            {
                return;
            }

            string error = string.IsNullOrEmpty(reason)
                ? "The host became unavailable during save transfer"
                : reason;
            CoopMod.Logger.LogWarning($"[SaveTransfer] {error}");
            Reset();
            OnTransferError?.Invoke(error);
        }
        
        /// <summary>
        /// Reset transfer state
        /// </summary>
        public static void Reset()
        {
            pendingSaveFilename = null;
            pendingHostSlotFilename = null;
            pendingWorldSeed = -1;
            pendingLoadSaveHash = null;
            receivedSaveData = null;
            expectedChunks = 0;
            receivedChunks = 0;
            pendingSlotData = null;
            pendingSenderHost = CSteamID.Nil;
            hostSaveSlotToSend = null;
            hostSavePreparationGeneration++;
            hostSavePreparationInFlight = false;
            hostSavePreparationSlot = string.Empty;
            hostSavePreparationWaiters.Clear();
            transferCompletionReceived = false;
            lastInvalidatedPeer = CSteamID.Nil;
            lastInvalidationRestartAt = 0f;
            pendingCoopPositionsReceived = false;
            pendingCoopPositionsJson = null;
            lastTransferProgress = 0f;
            ClearPendingSaveRequest();
        }
    }
}
