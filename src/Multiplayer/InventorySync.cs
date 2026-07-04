using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Synchronizes live item economy state that is not covered by the initial host save:
    /// player/container inventories, item params, toolbar slots, and world drops.
    /// Clients send only changes relative to the last host baseline; the host applies them
    /// and rebroadcasts a canonical full snapshot.
    /// </summary>
    public class InventorySync : MonoBehaviour
    {
        private const byte PayloadVersion = 2;
        private const float SyncIntervalSeconds = 3f;
        private const float EchoSuppressSeconds = 0.45f;
        private const int MaxPayloadBytes = 1024 * 1024;
        private const int MaxWgoEntries = 4096;
        private const int MaxDropEntries = 4096;
        private const int MaxToolbarSlots = 16;
        private const double CaptureWarningMilliseconds = 20.0;

        private static InventorySync _instance;
        public static InventorySync Instance => _instance;

        private readonly Dictionary<ulong, uint> lastReceivedSequenceByOrigin = new Dictionary<ulong, uint>();
        private readonly Dictionary<string, string> hostBaselineWgoFingerprints = new Dictionary<string, string>();
        private readonly HashSet<string> locallyMutatedWgoKeys = new HashSet<string>();

        private bool isSyncEnabled;
        private bool hasHostBaseline;
        private bool isApplyingRemote;
        private float lastSendTime = -SyncIntervalSeconds;
        private float suppressSendUntil;
        private uint nextSequence;
        private string lastSentFingerprint = string.Empty;
        private string hostBaselineDropsFingerprint = string.Empty;
        private string hostBaselineToolbarFingerprint = string.Empty;
        private float lastImmediateSendTime;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            CoopMod.Logger.LogInfo("[InventorySync] Initialized");
        }

        private void OnEnable()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnInventorySyncReceived -= OnInventorySyncReceived;
                SteamP2PManager.Instance.OnInventorySyncReceived += OnInventorySyncReceived;
            }
        }

        private void OnDisable()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnInventorySyncReceived -= OnInventorySyncReceived;
            }
        }

        public void EnableSync()
        {
            isSyncEnabled = true;
            hasHostBaseline = false;
            isApplyingRemote = false;
            lastSendTime = -SyncIntervalSeconds;
            suppressSendUntil = 0f;
            nextSequence = 0;
            lastSentFingerprint = string.Empty;
            hostBaselineWgoFingerprints.Clear();
            locallyMutatedWgoKeys.Clear();
            hostBaselineDropsFingerprint = string.Empty;
            hostBaselineToolbarFingerprint = string.Empty;
            lastImmediateSendTime = 0f;
            lastReceivedSequenceByOrigin.Clear();

            CoopMod.Logger.LogInfo("[InventorySync] Sync enabled");

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop != null && onlineCoop.IsOnlineCoopEnabled && onlineCoop.IsHost)
            {
                SendLocalSnapshot(force: true);
            }
        }

        public void DisableSync()
        {
            isSyncEnabled = false;
            isApplyingRemote = false;
            lastReceivedSequenceByOrigin.Clear();
            hostBaselineWgoFingerprints.Clear();
            locallyMutatedWgoKeys.Clear();
            CoopMod.Logger.LogInfo("[InventorySync] Sync disabled");
        }

        internal static bool IsApplyingRemote => Instance != null && Instance.isApplyingRemote;

        internal void NotifyInventoryMutated(WorldGameObject wgo, bool force = false)
        {
            if (!isSyncEnabled || isApplyingRemote || !CanUseInventoryState())
                return;
            if (wgo != null && !ShouldSyncWgo(wgo))
                return;

            MarkLocallyMutated(wgo);
            SendImmediateSnapshot(force);
        }

        internal void NotifyInventoryDataMutated(Item data, bool force = false)
        {
            if (!isSyncEnabled || isApplyingRemote || data == null || !CanUseInventoryState())
                return;

            WorldGameObject owner = FindWgoByData(data);
            if (owner != null && !ShouldSyncWgo(owner))
                return;

            MarkLocallyMutated(owner);
            SendImmediateSnapshot(force);
        }

        private void MarkLocallyMutated(WorldGameObject wgo)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (wgo == null || onlineCoop == null || onlineCoop.IsHost)
                return;

            string key = BuildWgoKey(
                IsLocalPlayerWgo(wgo),
                wgo.unique_id,
                wgo.obj_id,
                wgo.custom_tag,
                wgo.transform.position);
            if (!string.IsNullOrEmpty(key))
                locallyMutatedWgoKeys.Add(key);
        }

        internal void FlushNow()
        {
            if (!isSyncEnabled || isApplyingRemote || !CanUseInventoryState())
                return;

            SendImmediateSnapshot(force: true);
        }

        private void SendImmediateSnapshot(bool force)
        {
            float now = Time.realtimeSinceStartup;
            if (!force && now - lastImmediateSendTime < 0.15f)
                return;

            lastImmediateSendTime = now;
            SendLocalSnapshot(force);
        }

        private void Update()
        {
            if (!isSyncEnabled || isApplyingRemote)
                return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || !CanUseInventoryState())
                return;

            float now = Time.realtimeSinceStartup;
            if (now - lastSendTime < SyncIntervalSeconds || now < suppressSendUntil)
                return;

            SendLocalSnapshot(force: false);
        }

        public void SendLocalSnapshot(bool force)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || !CanUseInventoryState())
                return;

            bool fullSnapshot = onlineCoop.IsHost && (force || !hasHostBaseline);
            if (!onlineCoop.IsHost && !hasHostBaseline)
                return;

            InventorySnapshot snapshot;
            Stopwatch captureStopwatch = Stopwatch.StartNew();
            try
            {
                snapshot = CaptureSnapshot(fullSnapshot);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[InventorySync] Failed to capture inventory snapshot: {ex.Message}");
                return;
            }
            finally
            {
                captureStopwatch.Stop();
            }

            if (captureStopwatch.Elapsed.TotalMilliseconds >= CaptureWarningMilliseconds)
            {
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] CaptureSnapshot took {captureStopwatch.Elapsed.TotalMilliseconds:F1}ms " +
                    $"full={fullSnapshot} changes={snapshot.HasChanges} wgo_entries={snapshot.WgoEntries.Count} " +
                    $"drops={snapshot.Drops.Count} toolbar={snapshot.ToolbarItems.Count}");
            }

            if (!snapshot.HasChanges)
            {
                lastSendTime = Time.realtimeSinceStartup;
                return;
            }

            if (!force && snapshot.Fingerprint == lastSentFingerprint)
            {
                lastSendTime = Time.realtimeSinceStartup;
                return;
            }

            snapshot.Sequence = ++nextSequence;

            byte[] payload;
            try
            {
                payload = SerializeSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[InventorySync] Failed to serialize inventory snapshot: {ex.Message}");
                return;
            }

            if (payload.Length > MaxPayloadBytes)
            {
                CoopMod.Logger.LogWarning($"[InventorySync] Snapshot too large ({payload.Length} bytes); not sending");
                return;
            }

            if (onlineCoop.IsHost)
            {
                SteamP2PManager.Instance?.BroadcastInventorySync(payload);
            }
            else
            {
                SteamP2PManager.Instance?.SendInventorySyncToHost(payload);
                for (int i = 0; i < snapshot.WgoEntries.Count; i++)
                {
                    locallyMutatedWgoKeys.Remove(snapshot.WgoEntries[i].Key);
                }
            }

            lastSentFingerprint = snapshot.Fingerprint;
            lastSendTime = Time.realtimeSinceStartup;

            if (onlineCoop.IsHost)
            {
                ApplySnapshotToBaseline(snapshot);
            }
        }

        private void OnInventorySyncReceived(CSteamID senderID, byte[] payload)
        {
            if (!isSyncEnabled)
                return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || !CanUseInventoryState())
                return;

            if (payload == null || payload.Length == 0 || payload.Length > MaxPayloadBytes)
                return;

            InventorySnapshot snapshot;
            try
            {
                snapshot = DeserializeSnapshot(payload);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[InventorySync] Failed to parse snapshot: {ex.Message}");
                return;
            }

            ulong localSteamId = GetLocalSteamId();
            if (snapshot.OriginSteamId == 0UL || snapshot.OriginSteamId == localSteamId)
                return;

            if (!onlineCoop.IsHost && !IsExpectedHost(senderID))
                return;

            if (IsStaleSnapshot(snapshot))
                return;

            try
            {
                if (!onlineCoop.IsHost && HasUnsentLocalChanges())
                {
                    SendLocalSnapshot(force: true);
                    return;
                }

                ApplySnapshot(snapshot);

                if (!onlineCoop.IsHost)
                {
                    // Every host delta becomes the client's new canonical
                    // baseline. Keeping only the initial full snapshot made
                    // clients repeatedly send stale container contents back.
                    ApplySnapshotToBaseline(snapshot);
                }

                suppressSendUntil = Time.realtimeSinceStartup + EchoSuppressSeconds;
                SeedLastSentFingerprintFromCurrentState(onlineCoop.IsHost);

                if (onlineCoop.IsHost)
                {
                    SendLocalSnapshot(force: false);
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[InventorySync] Failed to apply snapshot: {ex.Message}");
            }
        }

        private bool HasUnsentLocalChanges()
        {
            if (!hasHostBaseline)
                return false;

            try
            {
                return CaptureSnapshot(fullSnapshot: false).HasChanges;
            }
            catch
            {
                return false;
            }
        }

        private bool IsStaleSnapshot(InventorySnapshot snapshot)
        {
            uint lastSequence;
            if (lastReceivedSequenceByOrigin.TryGetValue(snapshot.OriginSteamId, out lastSequence) &&
                snapshot.Sequence <= lastSequence)
            {
                return true;
            }

            lastReceivedSequenceByOrigin[snapshot.OriginSteamId] = snapshot.Sequence;
            return false;
        }

        private void ApplySnapshot(InventorySnapshot snapshot)
        {
            isApplyingRemote = true;
            try
            {
                if (snapshot.HasToolbar &&
                    snapshot.ToolbarFingerprint != BuildToolbarFingerprint(CaptureToolbar()))
                {
                    ApplyToolbar(snapshot.ToolbarItems);
                }

                // Sender already filtered to entries it saw as changed. The
                // previous per-entry fingerprint check here fought a losing
                // battle between cheap (sender-side) and JSON (wire-recomputed)
                // fingerprints; the resulting mismatch meant the check almost
                // never short-circuited anyway. RestoreSavedInventory is just a
                // field assignment, so always applying is cheaper than running
                // another full fingerprint pass to "save" one.
                for (int i = 0; i < snapshot.WgoEntries.Count; i++)
                {
                    ApplyWgoEntry(snapshot.WgoEntries[i]);
                }

                if (snapshot.HasDrops &&
                    snapshot.DropsFingerprint != BuildDropsFingerprint(CaptureDrops()))
                {
                    ApplyDrops(snapshot.Drops);
                }

                RedrawInventoryUi();
            }
            finally
            {
                isApplyingRemote = false;
            }
        }

        private void ApplySnapshotToBaseline(InventorySnapshot snapshot)
        {
            if (snapshot.IsFull)
            {
                hostBaselineWgoFingerprints.Clear();
            }

            for (int i = 0; i < snapshot.WgoEntries.Count; i++)
            {
                InventoryWgoEntry entry = snapshot.WgoEntries[i];
                hostBaselineWgoFingerprints[entry.Key] = entry.Fingerprint;
            }

            if (snapshot.IsFull || snapshot.HasDrops)
            {
                hostBaselineDropsFingerprint = snapshot.DropsFingerprint;
            }

            if (snapshot.IsFull || snapshot.HasToolbar)
            {
                hostBaselineToolbarFingerprint = snapshot.ToolbarFingerprint;
            }

            hasHostBaseline = true;
        }

        private void SeedLastSentFingerprintFromCurrentState(bool fullSnapshot)
        {
            try
            {
                InventorySnapshot current = CaptureSnapshot(fullSnapshot);
                lastSentFingerprint = current.Fingerprint;
            }
            catch
            {
                lastSentFingerprint = string.Empty;
            }
        }

        private static bool CanUseInventoryState()
        {
            return MainGame.game_started
                && MainGame.me != null
                && MainGame.me.save != null
                && MainGame.me.player != null
                && MainGame.me.world_root != null;
        }

        private static bool IsExpectedHost(CSteamID senderID)
        {
            var lobbyID = SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (lobbyID == CSteamID.Nil)
                return true;

            return SteamMatchmaking.GetLobbyOwner(lobbyID) == senderID;
        }

        private static ulong GetLocalSteamId()
        {
            return SteamManager.Initialized ? SteamUser.GetSteamID().m_SteamID : 0UL;
        }

        private InventorySnapshot CaptureSnapshot(bool fullSnapshot)
        {
            var snapshot = new InventorySnapshot
            {
                Version = PayloadVersion,
                OriginSteamId = GetLocalSteamId(),
                IsFull = fullSnapshot,
                WgoEntries = new List<InventoryWgoEntry>(),
                Drops = new List<InventoryDropEntry>(),
                ToolbarItems = CaptureToolbar()
            };

            snapshot.ToolbarFingerprint = BuildToolbarFingerprint(snapshot.ToolbarItems);
            snapshot.HasToolbar = fullSnapshot || snapshot.ToolbarFingerprint != hostBaselineToolbarFingerprint;

            // Phase 1: compute cheap structural fingerprints for all eligible WGOs.
            // This avoids the expensive Item.ToJSON() call for unchanged entries (the
            // common case). JSON serialization only happens below for entries whose
            // cheap fingerprint actually differs from the baseline.
            List<WgoCheapFingerprint> cheapFingerprints = CaptureCheapWgoFingerprints();
            bool isHost = OnlineCoopManager.Instance?.IsHost == true;
            var allWgoFingerprintBuilder = new StringBuilder(cheapFingerprints.Count * 48);
            for (int i = 0; i < cheapFingerprints.Count; i++)
            {
                WgoCheapFingerprint cheap = cheapFingerprints[i];
                allWgoFingerprintBuilder.Append(cheap.Key).Append('=').Append(cheap.Fingerprint).Append('\n');
            }

            // Phase 2: serialize JSON only for WGOs whose cheap fingerprint changed
            // (or all of them, for a full snapshot).
            for (int i = 0; i < cheapFingerprints.Count; i++)
            {
                WgoCheapFingerprint cheap = cheapFingerprints[i];
                bool hasBaseline = hostBaselineWgoFingerprints.TryGetValue(
                    cheap.Key,
                    out string baselineFingerprint);
                bool changed = fullSnapshot ||
                               (hasBaseline
                                   ? baselineFingerprint != cheap.Fingerprint
                                   : isHost || locallyMutatedWgoKeys.Contains(cheap.Key));
                if (!changed)
                    continue;

                InventoryWgoEntry entry = BuildWgoEntry(cheap.Wgo);
                if (!string.IsNullOrEmpty(entry.Key))
                {
                    snapshot.WgoEntries.Add(entry);
                }
            }

            // CombatSync carries immediate create/collect events from either peer.
            // Inventory snapshots are the durable reconciliation layer, so only
            // the host may publish their canonical drop list. Allowing clients to
            // send drop snapshots lets an in-flight pre-pickup snapshot resurrect
            // a corpse after the host has already collected it.
            if (isHost)
            {
                snapshot.Drops = CaptureDrops();
                snapshot.DropsFingerprint = BuildDropsFingerprint(snapshot.Drops);
                snapshot.HasDrops =
                    fullSnapshot ||
                    snapshot.DropsFingerprint != hostBaselineDropsFingerprint;
            }
            else
            {
                snapshot.Drops = new List<InventoryDropEntry>();
                snapshot.DropsFingerprint = hostBaselineDropsFingerprint;
                snapshot.HasDrops = false;
            }

            snapshot.Fingerprint = snapshot.IsFull + "\nTB:" + snapshot.ToolbarFingerprint +
                                   "\nWGO:\n" + allWgoFingerprintBuilder +
                                   "\nDROPS:" + snapshot.DropsFingerprint;

            snapshot.HasChanges = fullSnapshot ||
                                  snapshot.HasToolbar ||
                                  snapshot.WgoEntries.Count > 0 ||
                                  snapshot.HasDrops;

            if (!snapshot.HasToolbar)
            {
                snapshot.ToolbarItems.Clear();
            }

            if (!snapshot.HasDrops)
            {
                snapshot.Drops.Clear();
            }

            return snapshot;
        }

        private static List<InventoryWgoEntry> CaptureWgoEntries()
        {
            var entries = new List<InventoryWgoEntry>();
            List<WorldGameObject> worldObjects = MainGame.me.GetListOfWorldObjects();
            if (worldObjects == null)
                return entries;

            bool capturedLocalPlayer = false;
            for (int i = 0; i < worldObjects.Count; i++)
            {
                WorldGameObject wgo = worldObjects[i];
                if (!ShouldSyncWgo(wgo))
                    continue;

                capturedLocalPlayer |= IsLocalPlayerWgo(wgo);
                InventoryWgoEntry entry = BuildWgoEntry(wgo);
                if (!string.IsNullOrEmpty(entry.Key))
                {
                    entries.Add(entry);
                }
            }

            if (!capturedLocalPlayer && ShouldSyncWgo(MainGame.me.player))
            {
                entries.Add(BuildWgoEntry(MainGame.me.player));
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return entries;
        }

        private const float EligibleWgoCacheTtlSeconds = 5f;
        private static readonly List<WorldGameObject> EligibleWgoCache = new List<WorldGameObject>();
        private static float _eligibleWgoCacheBuildTime = -1000f;

        /// <summary>
        /// Walks all sync-eligible WGOs and produces cheap structural fingerprints.
        /// This is the hot path used by CaptureSnapshot to decide which WGOs need
        /// full JSON serialization. Keep this O(N) and allocation-light: it must not
        /// call Item.ToJSON() or any other reflective serializer.
        /// </summary>
        private static List<WgoCheapFingerprint> CaptureCheapWgoFingerprints()
        {
            var entries = new List<WgoCheapFingerprint>();

            if (Time.realtimeSinceStartup - _eligibleWgoCacheBuildTime >= EligibleWgoCacheTtlSeconds)
            {
                EligibleWgoCache.Clear();
                // Use the WGORegistry's maintained snapshot (O(1)) instead of
                // MainGame.me.GetListOfWorldObjects(), which calls FindObjectsOfType
                // and scans the entire scene (~20k objects, ~100ms) on every call.
                List<WorldGameObject> worldObjects = WGORegistry.Instance?.SnapshotAll()
                                                      ?? MainGame.me?.GetListOfWorldObjects();
                if (worldObjects != null)
                {
                    for (int i = 0; i < worldObjects.Count; i++)
                    {
                        WorldGameObject wgo = worldObjects[i];
                        if (ShouldSyncWgo(wgo))
                            EligibleWgoCache.Add(wgo);
                    }
                }
                _eligibleWgoCacheBuildTime = Time.realtimeSinceStartup;
            }

            bool capturedLocalPlayer = false;
            for (int i = 0; i < EligibleWgoCache.Count; i++)
            {
                WorldGameObject wgo = EligibleWgoCache[i];
                capturedLocalPlayer |= IsLocalPlayerWgo(wgo);
                AppendCheapFingerprint(entries, wgo);
            }

            if (!capturedLocalPlayer && MainGame.me?.player != null && ShouldSyncWgo(MainGame.me.player))
            {
                AppendCheapFingerprint(entries, MainGame.me.player);
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return entries;
        }

        private static void AppendCheapFingerprint(List<WgoCheapFingerprint> entries, WorldGameObject wgo)
        {
            bool isLocalPlayerInventory = IsLocalPlayerWgo(wgo);
            string key = BuildWgoKey(isLocalPlayerInventory, wgo.unique_id, wgo.obj_id, wgo.custom_tag, wgo.transform.position);
            if (string.IsNullOrEmpty(key))
                return;

            string fingerprint = BuildCheapWgoFingerprint(wgo, isLocalPlayerInventory);
            entries.Add(new WgoCheapFingerprint { Key = key, Fingerprint = fingerprint, Wgo = wgo });
        }

        /// <summary>
        /// Cheap structural fingerprint that captures every field Item.ToJSON would
        /// serialize, but without invoking JSON. Detects any meaningful inventory
        /// mutation (item added/removed/reordered, value/durability/params changes).
        /// </summary>
        private static string BuildCheapWgoFingerprint(WorldGameObject wgo, bool isLocalPlayerInventory)
        {
            return BuildCheapItemFingerprint(wgo?.data);
        }

        private static string BuildCheapItemFingerprint(Item data)
        {
            if (data == null)
                return "empty";

            var sb = new StringBuilder(64);
            sb.Append("sz:").Append(data.inventory_size).Append('|');
            AppendItemListFingerprint(sb, data.inventory);
            sb.Append('|');
            AppendItemListFingerprint(sb, data.secondary_inventory);
            return sb.ToString();
        }

        private static void AppendItemListFingerprint(StringBuilder sb, List<Item> items)
        {
            if (items == null)
            {
                sb.Append("n:0");
                return;
            }

            sb.Append("c:").Append(items.Count).Append('{');
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item == null)
                {
                    sb.Append("n;");
                    continue;
                }

                // Use public Item properties (id/value/durability/hp/progress/money)
                // — _params itself is private. inventory_size reflects nested bag
                // capacity, included so bag-size changes are detected.
                sb.Append(item.id ?? string.Empty).Append(',')
                  .Append(item.value).Append(',')
                  .Append(item.durability).Append(',')
                  .Append(item.hp).Append(',')
                  .Append(item.progress).Append(',')
                  .Append(item.money).Append(',')
                  .Append(item.inventory_size).Append(';');
            }

            sb.Append('}');
        }

        private static Dictionary<string, string> BuildCurrentWgoFingerprintMap()
        {
            // Reuse the cheap fingerprint path so ApplySnapshot does NOT re-serialize
            // every WGO inventory to JSON just to decide which entries to apply.
            var result = new Dictionary<string, string>();
            List<WgoCheapFingerprint> entries = CaptureCheapWgoFingerprints();
            for (int i = 0; i < entries.Count; i++)
            {
                WgoCheapFingerprint entry = entries[i];
                result[entry.Key] = entry.Fingerprint;
            }

            return result;
        }

        private static bool ShouldSyncWgo(WorldGameObject wgo)
        {
            if (wgo == null || wgo.data == null || wgo.is_removed || wgo.IsDisabled())
                return false;

            if (IsLocalPlayerWgo(wgo))
                return true;

            if (wgo.is_player)
                return false;

            Item data = wgo.data;
            if ((data.inventory != null && data.inventory.Count > 0) ||
                (data.secondary_inventory != null && data.secondary_inventory.Count > 0))
            {
                return true;
            }

            if (data.inventory_size > 0)
                return true;

            ObjectDefinition objDef = wgo.obj_def;
            return objDef != null &&
                   (objDef.inventory_size > 0 ||
                    objDef.open_in_multiinventory ||
                    (objDef.can_insert_items != null && objDef.can_insert_items.Count > 0) ||
                    objDef.can_insert_items_limit > 0);
        }

        internal static WorldGameObject FindWgoByData(Item data)
        {
            if (data == null)
                return null;
            if (MainGame.me?.player != null && MainGame.me.player.data == data)
                return MainGame.me.player;

            List<WorldGameObject> worldObjects = WGORegistry.Instance?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
            if (worldObjects == null)
                return null;

            for (int i = 0; i < worldObjects.Count; i++)
            {
                WorldGameObject wgo = worldObjects[i];
                if (wgo != null && wgo.data == data)
                    return wgo;
            }

            return null;
        }

        private static InventoryWgoEntry BuildWgoEntry(WorldGameObject wgo)
        {
            bool isLocalPlayerInventory = IsLocalPlayerWgo(wgo);
            var entry = new InventoryWgoEntry
            {
                IsLocalPlayerInventory = isLocalPlayerInventory,
                UniqueId = wgo.unique_id,
                ObjId = wgo.obj_id ?? string.Empty,
                CustomTag = wgo.custom_tag ?? string.Empty,
                Position = wgo.transform.position,
                ItemJson = SerializeWgoInventoryData(wgo, isLocalPlayerInventory),
                SecondaryItemJsons = SerializeItemList(wgo.data?.secondary_inventory),
                Key = BuildWgoKey(isLocalPlayerInventory, wgo.unique_id, wgo.obj_id, wgo.custom_tag, wgo.transform.position)
            };

            // Use the same cheap structural fingerprint that
            // BuildCurrentWgoFingerprintMap produces on the receiver, so the
            // per-entry comparison in ApplySnapshot actually matches.
            entry.Fingerprint = BuildCheapWgoFingerprint(wgo, isLocalPlayerInventory);
            return entry;
        }

        private static List<string> CaptureToolbar()
        {
            var result = new List<string>();
            string[] equippedItems = MainGame.me?.save?.equipped_items;
            if (equippedItems == null)
                return result;

            for (int i = 0; i < equippedItems.Length; i++)
            {
                result.Add(equippedItems[i] ?? string.Empty);
            }

            return result;
        }

        private static List<InventoryDropEntry> CaptureDrops()
        {
            var result = new List<InventoryDropEntry>();
            List<DropResGameObject> drops = DropsList.me?.drops;
            if (drops == null)
                return result;

            for (int i = 0; i < drops.Count; i++)
            {
                DropResGameObject drop = drops[i];
                if (drop == null || drop.is_collected || drop.res == null || drop.res.IsEmpty())
                    continue;

                var entry = new InventoryDropEntry
                {
                    ItemJson = SerializeItem(drop.res),
                    Position = drop.transform.position,
                    ZoneId = drop.zone_id ?? string.Empty
                };
                entry.Fingerprint = BuildDropFingerprint(entry);
                result.Add(entry);
            }

            result.Sort((a, b) => string.CompareOrdinal(a.Fingerprint, b.Fingerprint));
            return result;
        }

        private static void ApplyWgoEntry(InventoryWgoEntry entry)
        {
            WorldGameObject wgo = ResolveWgo(entry);
            if (wgo == null)
            {
                CoopMod.Logger.LogDebug($"[InventorySync] Could not resolve WGO inventory target: {entry.ObjId} uid={entry.UniqueId}");
                return;
            }

            Item item = DeserializeItem(entry.ItemJson);
            if (item == null)
                return;

            item.secondary_inventory = DeserializeItemList(entry.SecondaryItemJsons);

            if (entry.IsLocalPlayerInventory)
            {
                ApplyLocalPlayerInventory(wgo, item);
                return;
            }

            wgo.RestoreSavedInventory(item);
            try
            {
                wgo.Redraw(true);
                SmartDrawer drawer =
                    wgo.GetComponentInChildren<SmartDrawer>(true);
                drawer?.Redraw(true);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[InventorySync] Failed to redraw synchronized WGO '{wgo.obj_id}': {ex.Message}");
            }
        }

        private static WorldGameObject ResolveWgo(InventoryWgoEntry entry)
        {
            if (entry.IsLocalPlayerInventory)
            {
                return MainGame.me?.player;
            }

            if (entry.UniqueId > 0L)
            {
                if (WGORegistry.Instance != null && WGORegistry.Instance.TryGet(entry.UniqueId, out var indexed))
                    return indexed;

                WorldGameObject byUniqueId = WorldMap.GetWorldGameObjectByUniqueId(entry.UniqueId, false);
                if (byUniqueId != null)
                    return byUniqueId;
            }

            if (!string.IsNullOrEmpty(entry.CustomTag))
            {
                WorldGameObject byTag = WorldMap.GetWorldGameObjectByCustomTag(entry.CustomTag, true);
                if (byTag != null)
                    return byTag;
            }

            if (string.IsNullOrEmpty(entry.ObjId))
                return null;

            WorldGameObject registryMatch = WGORegistry.Instance?.ResolveNearest(entry.ObjId, entry.Position, 128f);
            if (registryMatch != null)
                return registryMatch;

            List<WorldGameObject> worldObjects = MainGame.me.GetListOfWorldObjects();
            WorldGameObject nearest = null;
            float nearestDistance = 128f * 128f;

            for (int i = 0; i < worldObjects.Count; i++)
            {
                WorldGameObject candidate = worldObjects[i];
                if (candidate == null || candidate.obj_id != entry.ObjId)
                    continue;

                float distance = (candidate.transform.position - entry.Position).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = candidate;
                }
            }

            return nearest;
        }

        private static void ApplyLocalPlayerInventory(WorldGameObject wgo, Item source)
        {
            if (wgo?.data == null || source == null)
                return;

            wgo.data.inventory_size = source.inventory_size;
            wgo.data.inventory = source.inventory ?? new List<Item>();
            wgo.data.secondary_inventory = source.secondary_inventory ?? new List<Item>();
        }

        private static void ApplyToolbar(List<string> toolbarItems)
        {
            if (MainGame.me?.save == null || toolbarItems == null)
                return;

            int slotCount = Mathf.Min(toolbarItems.Count, MainGame.me.save.equipped_items.Length);
            for (int i = 0; i < slotCount; i++)
            {
                MainGame.me.save.equipped_items[i] = toolbarItems[i] ?? string.Empty;
            }
        }

        private static void ApplyDrops(List<InventoryDropEntry> drops)
        {
            DropsList list = DropsList.me;
            if (list == null)
                return;

            CombatSync.PushDropCreateSuppression();
            try
            {
                var existingDrops = new List<DropResGameObject>(list.drops);
                var matchedDrops = new HashSet<DropResGameObject>();

                if (drops != null)
                {
                    for (int i = 0; i < drops.Count; i++)
                    {
                        InventoryDropEntry entry = drops[i];
                        Item item = DeserializeItem(entry.ItemJson);
                        if (item == null || item.IsEmpty())
                            continue;

                        DropResGameObject existing = FindMatchingExistingDrop(
                            existingDrops,
                            matchedDrops,
                            item,
                            entry.Position);
                        if (existing != null)
                        {
                            matchedDrops.Add(existing);
                            existing.res = item;
                            existing.transform.position = entry.Position;
                            existing.zone_id = entry.ZoneId ?? string.Empty;
                            existing.res.drop_zone_id = existing.zone_id;
                            continue;
                        }

                        DropResGameObject created = DropResGameObject.Drop(
                            entry.Position,
                            item,
                            MainGame.me.world_root,
                            Direction.IgnoreDirection,
                            1f,
                            -1,
                            true,
                            true);

                        if (created != null)
                        {
                            created.zone_id = entry.ZoneId ?? string.Empty;
                            if (created.res != null)
                            {
                                created.res.drop_zone_id = created.zone_id;
                            }
                        }
                    }
                }

                for (int i = 0; i < existingDrops.Count; i++)
                {
                    DropResGameObject drop = existingDrops[i];
                    if (drop == null || matchedDrops.Contains(drop))
                        continue;

                    if (drop.res != null)
                    {
                        WorldMap.OnDropItemRemoved(drop.res);
                    }
                    drop.is_collected = true;
                    drop.DestroyLinkedHint();
                    list.drops.Remove(drop);
                    Destroy(drop.gameObject);
                }
            }
            finally
            {
                CombatSync.PopDropCreateSuppression();
            }
        }

        private static DropResGameObject FindMatchingExistingDrop(
            List<DropResGameObject> existingDrops,
            HashSet<DropResGameObject> matchedDrops,
            Item incoming,
            Vector3 incomingPosition)
        {
            DropResGameObject nearest = null;
            float nearestDistance = float.MaxValue;

            for (int i = 0; i < existingDrops.Count; i++)
            {
                DropResGameObject candidate = existingDrops[i];
                if (candidate == null ||
                    candidate.is_collected ||
                    matchedDrops.Contains(candidate) ||
                    !RepresentsSameDrop(candidate.res, incoming))
                {
                    continue;
                }

                float distance = (candidate.transform.position - incomingPosition).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearest = candidate;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        private static bool RepresentsSameDrop(Item current, Item incoming)
        {
            if (current == null || incoming == null)
                return false;

            if (!string.Equals(current.id, incoming.id, StringComparison.Ordinal) ||
                current.value != incoming.value ||
                current.worker_unique_id != incoming.worker_unique_id)
            {
                return false;
            }

            // CombatSync's immediate create packet carries only id/value, while
            // the later inventory snapshot includes the generated corpse subtype.
            // Treat an omitted subtype as the same drop so that snapshot enriches
            // the existing object instead of recreating it and replaying drop SFX.
            return string.IsNullOrEmpty(current.sub_name) ||
                   string.IsNullOrEmpty(incoming.sub_name) ||
                   string.Equals(current.sub_name, incoming.sub_name, StringComparison.Ordinal);
        }

        private static void RedrawInventoryUi()
        {
            try
            {
                GUIElements gui = GUIElements.me;
                if (gui?.hud != null)
                {
                    gui.hud.Redraw();
                    gui.hud.Update();
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[InventorySync] Failed to redraw inventory UI: {ex.Message}");
            }
        }

        private static string SerializeItem(Item item)
        {
            return item != null ? item.ToJSON() : string.Empty;
        }

        private static string SerializeWgoInventoryData(WorldGameObject wgo, bool isLocalPlayerInventory)
        {
            if (wgo?.data == null)
                return string.Empty;

            if (!isLocalPlayerInventory)
                return SerializeItem(wgo.data);

            var inventoryOnly = new Item
            {
                inventory_size = wgo.data.inventory_size,
                inventory = wgo.data.inventory ?? new List<Item>()
            };

            return SerializeItem(inventoryOnly);
        }

        private static Item DeserializeItem(string itemJson)
        {
            if (string.IsNullOrEmpty(itemJson))
                return new Item();

            Item item = JsonUtility.FromJson<Item>(itemJson);
            EnsureItemLists(item);
            return item;
        }

        private static List<string> SerializeItemList(List<Item> items)
        {
            var result = new List<string>();
            if (items == null)
                return result;

            for (int i = 0; i < items.Count; i++)
            {
                result.Add(SerializeItem(items[i]));
            }

            return result;
        }

        private static List<Item> DeserializeItemList(List<string> itemJsons)
        {
            var result = new List<Item>();
            if (itemJsons == null)
                return result;

            for (int i = 0; i < itemJsons.Count; i++)
            {
                Item item = DeserializeItem(itemJsons[i]);
                if (item != null)
                {
                    result.Add(item);
                }
            }

            return result;
        }

        private static void EnsureItemLists(Item item)
        {
            if (item == null)
                return;

            if (item.inventory == null)
            {
                item.inventory = new List<Item>();
            }

            if (item.secondary_inventory == null)
            {
                item.secondary_inventory = new List<Item>();
            }

            for (int i = 0; i < item.inventory.Count; i++)
            {
                EnsureItemLists(item.inventory[i]);
            }

            for (int i = 0; i < item.secondary_inventory.Count; i++)
            {
                EnsureItemLists(item.secondary_inventory[i]);
            }
        }

        private static bool IsLocalPlayerWgo(WorldGameObject wgo)
        {
            return wgo != null && MainGame.me?.player != null && wgo == MainGame.me.player;
        }

        private static string BuildWgoKey(bool isLocalPlayerInventory, long uniqueId, string objId, string customTag, Vector3 position)
        {
            if (isLocalPlayerInventory)
                return "player:local";

            if (uniqueId > 0L)
                return "u:" + uniqueId;

            if (!string.IsNullOrEmpty(customTag))
                return "t:" + customTag;

            return string.Format("p:{0}:{1:F1}:{2:F1}", objId ?? string.Empty, position.x, position.y);
        }

        private static string BuildToolbarFingerprint(List<string> toolbarItems)
        {
            if (toolbarItems == null)
                return string.Empty;

            var builder = new StringBuilder();
            for (int i = 0; i < toolbarItems.Count; i++)
            {
                builder.Append(toolbarItems[i] ?? string.Empty).Append('|');
            }

            return builder.ToString();
        }

        private static string BuildDropFingerprint(InventoryDropEntry entry)
        {
            return string.Format("{0}|{1:F1}|{2:F1}|{3}",
                entry.ItemJson ?? string.Empty,
                entry.Position.x,
                entry.Position.y,
                entry.ZoneId ?? string.Empty);
        }

        private static string BuildDropsFingerprint(List<InventoryDropEntry> drops)
        {
            if (drops == null)
                return string.Empty;

            var builder = new StringBuilder(drops.Count * 64);
            for (int i = 0; i < drops.Count; i++)
            {
                builder.Append(drops[i].Fingerprint).Append('\n');
            }

            return builder.ToString();
        }

        private static byte[] SerializeSnapshot(InventorySnapshot snapshot)
        {
            using (var stream = new MemoryStream(4096))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(snapshot.Version);
                writer.Write(snapshot.OriginSteamId);
                writer.Write(snapshot.Sequence);
                writer.Write(snapshot.IsFull);
                writer.Write(snapshot.HasToolbar);
                if (snapshot.HasToolbar)
                {
                    writer.Write(snapshot.ToolbarItems.Count);
                    for (int i = 0; i < snapshot.ToolbarItems.Count; i++)
                    {
                        writer.Write(snapshot.ToolbarItems[i] ?? string.Empty);
                    }
                }

                writer.Write(snapshot.WgoEntries.Count);
                for (int i = 0; i < snapshot.WgoEntries.Count; i++)
                {
                    WriteWgoEntry(writer, snapshot.WgoEntries[i]);
                }

                writer.Write(snapshot.HasDrops);
                if (snapshot.HasDrops)
                {
                    writer.Write(snapshot.Drops.Count);
                    for (int i = 0; i < snapshot.Drops.Count; i++)
                    {
                        WriteDropEntry(writer, snapshot.Drops[i]);
                    }
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        private static InventorySnapshot DeserializeSnapshot(byte[] payload)
        {
            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                var snapshot = new InventorySnapshot
                {
                    Version = reader.ReadByte()
                };

                if (snapshot.Version != PayloadVersion)
                {
                    throw new InvalidDataException($"Unsupported inventory sync payload version {snapshot.Version}");
                }

                snapshot.OriginSteamId = reader.ReadUInt64();
                snapshot.Sequence = reader.ReadUInt32();
                snapshot.IsFull = reader.ReadBoolean();
                snapshot.HasToolbar = reader.ReadBoolean();
                snapshot.ToolbarItems = new List<string>();
                if (snapshot.HasToolbar)
                {
                    int toolbarCount = ReadSafeCount(reader, "toolbar slot count", MaxToolbarSlots);
                    for (int i = 0; i < toolbarCount; i++)
                    {
                        snapshot.ToolbarItems.Add(reader.ReadString());
                    }
                }

                int wgoCount = ReadSafeCount(reader, "WGO inventory count", MaxWgoEntries);
                snapshot.WgoEntries = new List<InventoryWgoEntry>(wgoCount);
                for (int i = 0; i < wgoCount; i++)
                {
                    snapshot.WgoEntries.Add(ReadWgoEntry(reader));
                }

                snapshot.HasDrops = reader.ReadBoolean();
                snapshot.Drops = new List<InventoryDropEntry>();
                if (snapshot.HasDrops)
                {
                    int dropCount = ReadSafeCount(reader, "drop count", MaxDropEntries);
                    snapshot.Drops = new List<InventoryDropEntry>(dropCount);
                    for (int i = 0; i < dropCount; i++)
                    {
                        snapshot.Drops.Add(ReadDropEntry(reader));
                    }
                }

                snapshot.ToolbarFingerprint = BuildToolbarFingerprint(snapshot.ToolbarItems);
                snapshot.DropsFingerprint = BuildDropsFingerprint(snapshot.Drops);

                var fingerprintBuilder = new StringBuilder();
                for (int i = 0; i < snapshot.WgoEntries.Count; i++)
                {
                    InventoryWgoEntry entry = snapshot.WgoEntries[i];
                    fingerprintBuilder.Append(entry.Key).Append('=').Append(entry.Fingerprint).Append('\n');
                }

                snapshot.Fingerprint = snapshot.IsFull + "\nTB:" + snapshot.ToolbarFingerprint +
                                       "\nWGO:\n" + fingerprintBuilder +
                                       "\nDROPS:" + snapshot.DropsFingerprint;
                snapshot.HasChanges = snapshot.IsFull ||
                                      snapshot.HasToolbar ||
                                      snapshot.WgoEntries.Count > 0 ||
                                      snapshot.HasDrops;
                return snapshot;
            }
        }

        private static void WriteWgoEntry(BinaryWriter writer, InventoryWgoEntry entry)
        {
            writer.Write(entry.UniqueId);
            writer.Write(entry.ObjId ?? string.Empty);
            writer.Write(entry.CustomTag ?? string.Empty);
            writer.Write(entry.IsLocalPlayerInventory);
            WriteVector3(writer, entry.Position);
            writer.Write(entry.ItemJson ?? string.Empty);
            writer.Write(entry.SecondaryItemJsons.Count);
            for (int i = 0; i < entry.SecondaryItemJsons.Count; i++)
            {
                writer.Write(entry.SecondaryItemJsons[i] ?? string.Empty);
            }
        }

        private static InventoryWgoEntry ReadWgoEntry(BinaryReader reader)
        {
            var entry = new InventoryWgoEntry
            {
                UniqueId = reader.ReadInt64(),
                ObjId = reader.ReadString(),
                CustomTag = reader.ReadString(),
                IsLocalPlayerInventory = reader.ReadBoolean(),
                Position = ReadVector3(reader),
                ItemJson = reader.ReadString()
            };

            int secondaryCount = ReadSafeCount(reader, "secondary inventory count", 256);
            entry.SecondaryItemJsons = new List<string>(secondaryCount);
            for (int i = 0; i < secondaryCount; i++)
            {
                entry.SecondaryItemJsons.Add(reader.ReadString());
            }

            entry.Key = BuildWgoKey(entry.IsLocalPlayerInventory, entry.UniqueId, entry.ObjId, entry.CustomTag, entry.Position);
            Item fingerprintItem = DeserializeItem(entry.ItemJson);
            if (fingerprintItem != null)
            {
                fingerprintItem.secondary_inventory =
                    DeserializeItemList(entry.SecondaryItemJsons);
            }
            entry.Fingerprint = BuildCheapItemFingerprint(fingerprintItem);
            return entry;
        }

        private static void WriteDropEntry(BinaryWriter writer, InventoryDropEntry entry)
        {
            writer.Write(entry.ItemJson ?? string.Empty);
            WriteVector3(writer, entry.Position);
            writer.Write(entry.ZoneId ?? string.Empty);
        }

        private static InventoryDropEntry ReadDropEntry(BinaryReader reader)
        {
            var entry = new InventoryDropEntry
            {
                ItemJson = reader.ReadString(),
                Position = ReadVector3(reader),
                ZoneId = reader.ReadString()
            };
            entry.Fingerprint = BuildDropFingerprint(entry);
            return entry;
        }

        private static void WriteVector3(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        private static Vector3 ReadVector3(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static int ReadSafeCount(BinaryReader reader, string label, int max)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > max)
                throw new InvalidDataException($"Invalid {label}: {count}");

            return count;
        }

        private sealed class InventorySnapshot
        {
            public byte Version;
            public ulong OriginSteamId;
            public uint Sequence;
            public bool IsFull;
            public bool HasToolbar;
            public bool HasDrops;
            public bool HasChanges;
            public List<string> ToolbarItems;
            public List<InventoryWgoEntry> WgoEntries;
            public List<InventoryDropEntry> Drops;
            public string ToolbarFingerprint = string.Empty;
            public string DropsFingerprint = string.Empty;
            public string Fingerprint = string.Empty;
        }

        private sealed class InventoryWgoEntry
        {
            public string Key;
            public bool IsLocalPlayerInventory;
            public long UniqueId;
            public string ObjId;
            public string CustomTag;
            public Vector3 Position;
            public string ItemJson;
            public List<string> SecondaryItemJsons = new List<string>();
            public string Fingerprint;
        }

        private sealed class InventoryDropEntry
        {
            public string ItemJson;
            public Vector3 Position;
            public string ZoneId;
            public string Fingerprint;
        }

        /// <summary>
        /// Lightweight per-WGO fingerprint used for fast change detection.
        /// Stores only the key, the cheap structural fingerprint, and a reference
        /// to the source WGO (so changed entries can be JSON-serialized on demand).
        /// </summary>
        private struct WgoCheapFingerprint
        {
            public string Key;
            public string Fingerprint;
            public WorldGameObject Wgo;
        }
    }
}
